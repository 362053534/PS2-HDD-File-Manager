#!/usr/bin/env python3
"""Replay a timeout-slot ownership race using the actual application functions.
Native scheduler seam only; does not execute PS2 BIOS or prove hardware timing.
"""
from pathlib import Path
import os, shutil, subprocess, tempfile
root=Path(__file__).resolve().parents[1]
source=(root/"src/net.c").read_text(encoding="utf-8")
def function(signature):
    start=source.index(signature+"\n{");opening=source.index("{",start);end=opening+1;depth=1
    while depth:
        depth+=(source[end]=="{")-(source[end]=="}");end+=1
    return source[start:end]
a=source.index("typedef struct pfsnet_wait_slot {")
b=source.index("static s32 g_vblankHandlerId",a)
code=r"""
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <setjmp.h>
typedef int32_t s32;
typedef uint64_t u64;
static int tid=1, released[3], waiting[3];
static u64 now=100;
static jmp_buf parkB;
static int scenario;
static int DIntr(void) {return 1;}
static void EIntr(void);
static u64 iGetTimerSystemTime(void) {return now;}
static u64 GetTimerSystemTime(void) {return now;}
static int GetThreadId(void) {return tid;}
static int PollSema(int sema) {(void)sema;return -1;}
static void pfsnetEnsureTimerInit(void) {}
static int iReleaseWaitThread(int id) {
    if(!waiting[id]) return -1;
    waiting[id]=0;released[id]++;return 0;
}
static int WaitSema(int sema);
"""+source[a:b]+function("static void pfsnetCheckWaitSlotsIsr(void)")+"\n"+function("static int waitSemaTimeoutClocks(int sema, u64 clocks)")+r"""
static void EIntr(void) {
    if(scenario==2 && !waiting[tid]) {
        for(int i=0;i<PFSNET_WAIT_SLOTS;i++) {
            if(g_waitSlots[i].active==1 && g_waitSlots[i].tid==tid) {
                now+=0x4000;
                pfsnetCheckWaitSlotsIsr();
                assert(g_waitSlots[i].active==1); /* Not waiting yet: retry later. */
            }
        }
    }
}
static int WaitSema(int sema) {
    assert(sema==tid*10);
    waiting[tid]=1;
    if(scenario==3) {waiting[tid]=0;return sema;} /* A real signal wins. */
    if(tid==2) longjmp(parkB,1); /* B is blocked: suspend this native test frame. */
    if(scenario==0) {
        /* Timer releases A. Before A resumes, another runnable thread B waits. */
        now+=0x4000;
        pfsnetCheckWaitSlotsIsr();
        assert(released[1]==1);
        pfsnetCheckWaitSlotsIsr();
        assert(released[1]==1); /* Released owners are not released twice. */
        if(setjmp(parkB)==0) {
            tid=2;
            (void)waitSemaTimeoutClocks(20,0x4000);
            assert(!"B must remain blocked until its own deadline");
        }
        tid=1;
        return -1; /* A finally resumes and executes its real cleanup. */
    }
    now+=0x4000;
    pfsnetCheckWaitSlotsIsr();
    return -1;
}
int main(void) {
    setbuf(stdout,NULL);
    assert(waitSemaTimeoutClocks(10,0x4000)==-1);
    assert(waiting[2] && released[2]==0);
    now+=0x4000;
    pfsnetCheckWaitSlotsIsr();
    if(released[2]!=1) {
        puts("FAIL A cleanup erased B timeout ownership: B remains blocked forever");
        return 1;
    }
    puts("PASS released A retains its slot until cleanup; B deadline still wakes B");
    /* A's slot is now free and can serve a later timeout. */
    scenario=1;
    assert(waitSemaTimeoutClocks(10,0x4000)==-1 && released[1]==2);
    puts("PASS completed waiter frees its own slot for reuse");
    scenario=2;
    assert(waitSemaTimeoutClocks(10,0x4000)==-1 && released[1]==3);
    puts("PASS expiry before WaitSema retains the armed slot and retries after waiting");
    scenario=3;
    assert(waitSemaTimeoutClocks(10,0x4000)==10 && released[1]==3);
    for(int i=0;i<PFSNET_WAIT_SLOTS;i++) assert(g_waitSlots[i].tid!=1);
    puts("PASS actual semaphore success preserves its token result and frees ownership");
    return 0;
}
"""
cc=os.environ.get("CC") or shutil.which("cc")
if not cc:raise SystemExit("A native C compiler is required")
with tempfile.TemporaryDirectory(prefix="apa-wait-slots-") as folder:
    work=Path(folder);(work/"check.c").write_text(code,encoding="utf-8")
    subprocess.run([cc,"-std=c99","-Wall","-Wextra","-Werror",str(work/"check.c"),"-o",str(work/"check")],check=True)
    subprocess.run([str(work/"check")],check=True,timeout=2)
