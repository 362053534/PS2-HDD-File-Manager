#!/usr/bin/env python3
"""Actual startup delay with a lost-alarm fault and deterministic kernel seams.
Native only: it cannot prove PS2 timer/BIOS behavior. --revision checks old code.
"""
from pathlib import Path
import argparse, os, re, shutil, subprocess, tempfile
root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument("--revision");args=parser.parse_args()
source=(subprocess.check_output(["git","show",args.revision+":ps2-server/src/net.c"],cwd=root,text=True,encoding="utf-8")
        if args.revision else (root/"src/net.c").read_text(encoding="utf-8"))
start=re.search(r"static (?:void|int) sleepMs\(",source).start()
opening=source.index("{",start);depth=1;end=opening+1
while depth:
    depth+=(source[end]=="{")-(source[end]=="}");end+=1
observer=source[source.index("static const char *g_bootFailure;"):source.index("#define ETH_TCPIP_PRIO")] if "static const char *g_bootFailure;" in source else ""
function=source[start:end];old="static void sleepMs(" in function
# Include the actual outer poll loop too, so the timeout test does not replace
# the very delay implementation that can prevent that loop from advancing.
if not old:
    a=source.index("static int WaitValidNetState(")
    o=source.index("{",a);level=1;b=o+1
    while level:
        level+=(source[b]=="{")-(source[b]=="}");b+=1
    poll=source[a:b]
else:
    poll=""
code=r"""
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
typedef uint64_t u64;
typedef int32_t s32;
typedef struct {int current_priority;} ee_thread_status_t;
#define ETH_TCPIP_PRIO 0x58
#define kBUSCLK 1000000
static u64 clock_ticks;
static int priority=64,rotations,changes,status_error,lower_error,rotate_error,restore_error;
static int original=64;
static int progress_lines,checking_calls,bound_at;
int scr_printf(const char *format,...) {(void)format;progress_lines++;return 0;}
int checking(void) {checking_calls++;return bound_at>0 && checking_calls>=bound_at;}
int GetThreadId(void) {return 7;}
int ReferThreadStatus(int tid,ee_thread_status_t *s) {assert(tid==7);s->current_priority=priority;return status_error;}
int ChangeThreadPriority(int tid,int p) {
 assert(tid==7);changes++;
 if(changes==1 && lower_error)return -1;
 if(changes==2 && restore_error)return -1;
 priority=p;return 0;
}
u64 GetTimerSystemTime(void) {clock_ticks+=1000;return clock_ticks;}
int RotateThreadReadyQueue(int p) {
 assert(priority>=ETH_TCPIP_PRIO+8 && p==priority);rotations++;return rotate_error;
}
/* Simulate the existing wrapped DelayThread waiting forever after an alarm
   fails to release its semaphore. The caller must not enter this path. */
s32 __wrap_DelayThread(s32 usec) {
 (void)usec;puts("FAULT INJECTION: alarm wake missing; wrapped DelayThread cannot return");
 for(;;)pause();
 return 0;
}
"""+observer+function+"\n"+poll
if old:
    code+=r"""
int main(void) {setbuf(stdout,NULL);(void)original;(void)progress_lines;(void)checking_calls;(void)bound_at;sleepMs(20);return 0;}
"""
else:
    code+=r"""
static void reset(void) {g_bootFailure=NULL;g_bootFailureCode=0;clock_ticks=0;priority=original=64;rotations=changes=status_error=lower_error=rotate_error=restore_error=0;progress_lines=checking_calls=bound_at=0;}
int main(void) {
 setbuf(stdout,NULL);reset();
 assert(sleepMs(20)==0 && priority==original && rotations>0 && changes==2);
 assert(clock_ticks>=21000);
 puts("PASS alarm wake unavailable: startup delay still returns and restores priority");
 reset();assert(sleepMs(0)==0 && changes==0 && rotations==0);
 puts("PASS zero delay leaves scheduling unchanged");
 reset();priority=original=120;assert(sleepMs(10)==0 && priority==120);
 puts("PASS an already lower-priority caller is never promoted above network workers");
 reset();status_error=-1;assert(sleepMs(10)==-1 && changes==0);
 reset();lower_error=-1;assert(sleepMs(10)==-1 && priority==64 && rotations==0);
 puts("PASS status/priority setup failures return without spinning at high priority");
 reset();rotate_error=-1;assert(sleepMs(10)==-1 && priority==original && changes==2);
 assert(!strcmp(ethBootFailure(NULL),"rotate ready queue") && g_bootFailureCode==-1);
 puts("PASS yield failure restores original priority and preserves exact failure");
 reset();restore_error=-1;assert(sleepMs(10)==-1);
 puts("PASS priority restoration failure is reported");
 reset();assert(WaitValidNetState(checking)==-1);
 assert(clock_ticks>=30000000 && priority==original && progress_lines==0);
 puts("PASS actual poll plus actual delay times out without alarm wakes or verbose startup output");
 reset();bound_at=3;assert(WaitValidNetState(checking)==0 && priority==original);
 assert(clock_ticks>=2000000 && checking_calls==3);
 puts("PASS actual poll sees a later ready state while alarm wakes remain unavailable");
}
"""
if not observer:
    code=code.replace("g_bootFailure=NULL;g_bootFailureCode=0;", "")
    code=code.replace(' assert(!strcmp(ethBootFailure(NULL),"rotate ready queue") && g_bootFailureCode==-1);', "")
cc=os.environ.get("CC") or shutil.which("cc")
if not cc:raise SystemExit("A native C compiler is required")
with tempfile.TemporaryDirectory(prefix="apa-startup-delay-") as folder:
    work=Path(folder);(work/"check.c").write_text(code,encoding="utf-8")
    subprocess.run([cc,"-std=c99","-Wall","-Wextra","-Werror",str(work/"check.c"),"-o",str(work/"check")],check=True)
    try:
        subprocess.run([str(work/"check")],check=True,timeout=1)
    except subprocess.TimeoutExpired:
        raise SystemExit("FAIL startup delay remains blocked when its alarm wake never arrives")
