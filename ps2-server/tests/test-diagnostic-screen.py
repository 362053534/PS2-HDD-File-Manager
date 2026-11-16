#!/usr/bin/env python3
"""Render the real diagnostic monitor through a native TV-safe-area seam.
Catches left/bottom clipping; checks live and retained stall snapshots.
Does not emulate PS2 video hardware or prove the TV's exact overscan margins.
"""
from pathlib import Path
import os,shutil,subprocess,tempfile
root=Path(__file__).resolve().parents[1]
headers={
    'kernel.h': r"""#include <stdint.h>
typedef uint64_t u64;
typedef struct { void (*func)(void *); void *stack; int stack_size; void *gp_reg; int initial_priority; } ee_thread_t;
int DIntr(void); void EIntr(void); int CreateThread(ee_thread_t *); int StartThread(int,void *); int DeleteThread(int);

typedef struct {int status; void *func; void *stack; int stack_size; void *gp_reg; int initial_priority; int current_priority; unsigned attr,option,waitType,waitId,wakeupCount;} ee_thread_status_t;
int ReferThreadStatus(int,ee_thread_status_t *);
""",
    'timer.h': r"""#include <stdint.h>
#define kBUSCLK 147456000
uint64_t GetTimerSystemTime(void);
""",
    'delaythread.h': r"""void DelayThread(unsigned);
""",
    'debug.h': r"""void scr_setXY(int,int); int scr_getX(void); int scr_getY(void); void scr_printf(const char *,...) __attribute__((format(printf,1,2)));
""",
    'fileXio_rpc.h': r"""int fileXioWrite(int,const void *,int);
""",
}
cases={
    'check.c': r"""#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <stdarg.h>
#include <setjmp.h>
#include "kernel.h"
#include "diagnostics.h"
#include "data_io.h"
volatile PfsnetDataIoTrace g_pfsnetDataIoTrace;
void *_gp;
void apaDiagNetSnapshot(ApaDiagnostics *s){s->ioThreadId=8;s->sif0=0x100;s->outputThreadId=5;s->rxThreadId=7;s->outputHook=1;s->ipFound=1;s->address[0]=192;s->address[1]=168;s->address[2]=5;s->address[3]=3;}
static ee_thread_t thread;
static int enabled=1, result=512*1024, lines, createResult=4, startResult=0, deleted;
static jmp_buf done;
static char screen[2048];
static int cursorX,cursorY;
int DIntr(void){int old=enabled;enabled=0;return old;}
void EIntr(void){enabled=1;}
int CreateThread(ee_thread_t *th){thread=*th;return createResult;}
int ReferThreadStatus(int id,ee_thread_status_t *s){assert(enabled);if(id<5||id>8)return -1;memset(s,0,sizeof(*s));s->initial_priority=id==5?0x56:id==6?0x58:0x59;s->status=4;s->waitType=1;return 0;}
int StartThread(int id,void *arg){(void)id;(void)arg;return startResult;}
int DeleteThread(int id){(void)id;deleted++;return 0;}
uint64_t GetTimerSystemTime(void){return 147456000ULL;}
void DelayThread(unsigned us){assert(us==1000000);longjmp(done,1);}
void scr_setXY(int x,int y){cursorX=x;cursorY=y;}
int scr_getX(void){return 0;}int scr_getY(void){return 9;}
void scr_printf(const char *fmt,...){char b[256];va_list a;va_start(a,fmt);vsnprintf(b,sizeof(b),fmt,a);va_end(a);if(cursorX<4||cursorY<6||cursorY>25||cursorX+strlen(b)>74){fprintf(stderr,"FAIL diagnostic text outside TV safe area: x=%d y=%d length=%lu\n",cursorX,cursorY,(unsigned long)strlen(b));assert(0);}strcat(screen,b);lines++;}
int fileXioWrite(int fd,const void *buf,int n){(void)fd;(void)buf;(void)n;assert(g_apaDiag.diskStage==APA_IO_WRITE);return result;}
int main(void){
 uint64_t offset=5ULL<<30;
 g_apaDiag.sifKickAttempts=7;g_apaDiag.iopValid=1;g_apaDiag.iopPending=64;g_apaDiag.iopAtRead=1518;g_apaDiag.iopMismatch=64;
 g_pfsnetDataIoTrace.phase=APA_DATA_RECV;
 g_pfsnetDataIoTrace.calls=1;g_pfsnetDataIoTrace.moved=123456;
 apaDiagPutBegin(6ULL<<30,offset,16);
 apaDiagRecvResult(512*1024,0);assert(g_apaDiag.recvBytes==offset+512*1024);
 apaDiagRecvResult(-1,60);assert(g_apaDiag.recvError==60&&g_apaDiag.recvStage==APA_IO_ERROR);
 assert(apaDiagWrite(1,"",512*1024)==512*1024);assert(g_apaDiag.diskBytes==offset+512*1024);
 result=-5;assert(apaDiagWrite(1,"",1)==-5);assert(g_apaDiag.diskError==-5&&g_apaDiag.diskStage==APA_IO_ERROR);
 createResult=-1;assert(apaDiagStart()==-1);
 createResult=4;startResult=-2;assert(apaDiagStart()==-2&&deleted==1);
 startResult=0;assert(apaDiagStart()==0);assert(thread.initial_priority==0x78&&thread.stack_size>=0x2000);
 if(!setjmp(done))thread.func(NULL);
 assert(lines==20&&strstr(screen,"DIAG-11 HB:1")&&strstr(screen,"5120/6144 MiB"));
 assert(strstr(screen,"IOP valid:")&&strstr(screen,"IO tid:8")&&strstr(screen,"CORE tid:6")&&strstr(screen,"RX tid:7"));
 assert(strstr(screen,"IO:recv calls:1"));
 assert(strstr(screen,"RING pending:")&&strstr(screen,"BD:"));
 assert(enabled);puts("PASS: >4GiB progress, recv/write errors, monitor start failures, twenty bounded display rows; real IO ID and exact partial bytes");
}
""",
    'check-snapshot.c': r"""#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <stdarg.h>
#include <setjmp.h>
#include "kernel.h"
#include "diagnostics.h"
#include "data_io.h"
volatile PfsnetDataIoTrace g_pfsnetDataIoTrace;
void *_gp;
void apaDiagNetSnapshot(ApaDiagnostics *s){s->ioThreadId=8;s->sif0=0x100;s->outputThreadId=5;s->rxThreadId=7;s->outputHook=1;s->ipFound=1;s->address[0]=192;s->address[1]=168;s->address[2]=5;s->address[3]=3;}
static ee_thread_t thread;
static int enabled=1, result=512*1024, lines, createResult=4, startResult=0, deleted;
static jmp_buf done;
static char screen[2048];
static int cursorX,cursorY;
static int snapshotMode, ticks;
int DIntr(void){int old=enabled;enabled=0;return old;}
void EIntr(void){enabled=1;}
int CreateThread(ee_thread_t *th){thread=*th;return createResult;}
int ReferThreadStatus(int id,ee_thread_status_t *s){assert(enabled);if(id<5||id>8)return -1;memset(s,0,sizeof(*s));s->initial_priority=id==5?0x56:id==6?0x58:0x59;s->status=4;s->waitType=1;return 0;}
int StartThread(int id,void *arg){(void)id;(void)arg;return startResult;}
int DeleteThread(int id){(void)id;deleted++;return 0;}
uint64_t GetTimerSystemTime(void){
 if(snapshotMode){ticks++;lines=0;screen[0]=0;
  if(ticks==13){g_apaDiag.iopPending=0;g_apaDiag.iopAtRead=0;g_apaDiag.iopMismatch=0;g_apaDiag.sifKickAttempts=9;g_pfsnetDataIoTrace.phase=APA_DATA_DONE;g_apaDiag.active=0;g_apaDiag.recvStage=APA_IO_DONE;g_apaDiag.diskStage=APA_IO_DONE;}
  return 147456000ULL*ticks;
 }
 return 147456000ULL;
}
void DelayThread(unsigned us){assert(us==1000000);if(!snapshotMode||ticks>=15)longjmp(done,1);}
void scr_setXY(int x,int y){cursorX=x;cursorY=y;}
int scr_getX(void){return 0;}int scr_getY(void){return 9;}
void scr_printf(const char *fmt,...){char b[256];va_list a;va_start(a,fmt);vsnprintf(b,sizeof(b),fmt,a);va_end(a);if(cursorX<4||cursorY<6||cursorY>25||cursorX+strlen(b)>74){fprintf(stderr,"FAIL diagnostic text outside TV safe area: x=%d y=%d length=%lu\n",cursorX,cursorY,(unsigned long)strlen(b));assert(0);}strcat(screen,b);lines++;}
int fileXioWrite(int fd,const void *buf,int n){(void)fd;(void)buf;(void)n;assert(g_apaDiag.diskStage==APA_IO_WRITE);return result;}
int main(void){
 uint64_t offset=5ULL<<30;
 g_apaDiag.sifKickAttempts=7;g_apaDiag.iopValid=1;g_apaDiag.iopPending=64;g_apaDiag.iopAtRead=1518;g_apaDiag.iopMismatch=64;
 g_pfsnetDataIoTrace.phase=APA_DATA_RECV;
 g_pfsnetDataIoTrace.calls=1;g_pfsnetDataIoTrace.moved=123456;
 apaDiagPutBegin(6ULL<<30,offset,16);
 apaDiagRecvResult(512*1024,0);assert(g_apaDiag.recvBytes==offset+512*1024);
 apaDiagRecvResult(-1,60);assert(g_apaDiag.recvError==60&&g_apaDiag.recvStage==APA_IO_ERROR);
 assert(apaDiagWrite(1,"",512*1024)==512*1024);assert(g_apaDiag.diskBytes==offset+512*1024);
 result=-5;assert(apaDiagWrite(1,"",1)==-5);assert(g_apaDiag.diskError==-5&&g_apaDiag.diskStage==APA_IO_ERROR);
 createResult=-1;assert(apaDiagStart()==-1);
 createResult=4;startResult=-2;assert(apaDiagStart()==-2&&deleted==1);
 startResult=0;assert(apaDiagStart()==0);assert(thread.initial_priority==0x78&&thread.stack_size>=0x2000);
 if(!setjmp(done))thread.func(NULL);
 assert(lines==20&&strstr(screen,"DIAG-11 HB:1")&&strstr(screen,"5120/6144 MiB"));
 assert(strstr(screen,"IOP valid:")&&strstr(screen,"IO tid:8")&&strstr(screen,"CORE tid:6")&&strstr(screen,"RX tid:7"));
 snapshotMode=1;
 if(!setjmp(done))thread.func(NULL);
 assert(lines==20&&strstr(screen,"PUT:0")&&strstr(screen,"SNAP @11s NET:error HDD:error")&&strstr(screen,"SNAP IO tid:8 state:4 wait:1"));
 assert(strstr(screen,"SNAP IO:recv calls:1")&&strstr(screen,"bytes:123456"));
 assert(strstr(screen,"SIFkick:9")&&strstr(screen,"SNAP E:0 V:1 I:64 at:1518 M:64 S:00000100 K:7"));
 assert(strstr(screen,"RING pending:")&&strstr(screen,"BD:"));
 assert(enabled);puts("PASS: >4GiB progress, recv/write errors, monitor start failures, twenty bounded display rows; real IO ID and exact partial bytes");
}
""",
}
cc=os.environ.get("CC") or shutil.which("cc")
if not cc:raise SystemExit("A native C compiler is required")
with tempfile.TemporaryDirectory(prefix="apa-diag-screen-") as folder:
    work=Path(folder)
    for name,source in headers.items():(work/name).write_text(source,encoding="utf-8")
    for name,source in cases.items():
        path=work/name;path.write_text(source,encoding="utf-8")
        exe=work/path.stem
        subprocess.run([cc,"-std=c99","-Wall","-Wextra","-Werror","-I"+str(work),"-I"+str(root/"include"),str(root/"src/diagnostics.c"),str(path),"-o",str(exe)],check=True)
        subprocess.run([str(exe)],check=True,timeout=2)
