#!/usr/bin/env python3
"""Actual periodic callbacks plus guarded SIF recovery with fake DMA registers.
Proves the missing recovery path for a stopped receiver; not a PS2 hardware test.
"""
from pathlib import Path
import os,shutil,subprocess,tempfile
root=Path(__file__).resolve().parents[1]
s=(root/"src/net.c").read_text(encoding="utf-8")
def function(signature):
    a=s.index(signature+"\n{");b=s.index("{",a)+1;depth=1
    while depth:depth+=(s[b]=="{")-(s[b]=="}");b+=1
    return s[a:b]
watch=""
if "/* SIF_RX_WATCH_BEGIN */" in s:
    watch=s.split("/* SIF_RX_WATCH_BEGIN */",1)[1].split("/* SIF_RX_WATCH_END */",1)[0]
code=r"""
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
typedef uint64_t u64;typedef uint32_t u32;typedef int32_t s32;
typedef struct {unsigned psize;} SifCmdHeader_t;
static SifCmdHeader_t header;
static struct {void *pktbuf;} _sif_cmd_data;
static struct {u32 sifIrqs,sifKickAttempts;} g_apaDiag;
static int g_nmIsInitialized,bufferToken;
static void *g_nmFrameBufferStatus;
static u64 now;
static u32 chcr,dstat;
static int enabled=1,kicks,checks,registerReads,injectPendingAt,injectCommandAt;
#define MSec2TimerBusClock(ms) ((u64)(ms)*1000)
u64 iGetTimerSystemTime(void) {return now;}
int DIntr(void) {int old=enabled;enabled=0;return old;}
void EIntr(void) {enabled=1;}
u32 _lw(unsigned address) {
 assert(!enabled);
 registerReads++;
 if(injectPendingAt && registerReads==injectPendingAt)dstat|=0x20;
 if(injectCommandAt && registerReads==injectCommandAt)header.psize=32;
 if(address==0x1000c000)return chcr;
 assert(address==0x1000e010);return dstat;
}
void iSifSetDChain(void) {
 assert(!enabled && !(chcr&0x100) && !(dstat&0x20) && !header.psize);
 kicks++;
 chcr|=0x100; /* Model only the receiver control register, not real packet delivery. */
}
void pfsnetCheckWaitSlotsIsr(void) {checks++;}
void ExitHandler(void) {}
"""+watch+function("static u64 pfsnetTimerTickAlarmCb(s32 id, u64 scheduled_time, u64 actual_time, void *arg, void *pc_value)")+"\n"+function("static s32 pfsnetVblankBackupIsr(s32 cause)")+r"""
static void tick(unsigned ms) {now+=MSec2TimerBusClock(ms);pfsnetVblankBackupIsr(0);assert(enabled);}
static void reset(void) {
 g_nmIsInitialized=0;g_nmFrameBufferStatus=NULL;_sif_cmd_data.pktbuf=NULL;
 pfsnetVblankBackupIsr(0);
 now=1000000;chcr=0x01000084;dstat=0x00a00044;header.psize=0;
 g_apaDiag.sifIrqs=g_apaDiag.sifKickAttempts=0;kicks=checks=registerReads=injectPendingAt=injectCommandAt=0;
 g_nmIsInitialized=1;g_nmFrameBufferStatus=&bufferToken;_sif_cmd_data.pktbuf=&header;
}
int main(void) {
 setbuf(stdout,NULL);reset();tick(0);tick(19);assert(!kicks);tick(1);
 if(kicks!=1) {
  puts("FAIL stopped SIF0 with sleeping RX has no independent periodic recovery");return 1;
 }
 assert(g_apaDiag.sifKickAttempts==1);
 puts("PASS actual VBlank callback rearms a stable stopped/empty SIF receiver");
 tick(50);assert(kicks==1);
 puts("PASS running DMA is never restarted");
 reset();dstat|=0x20;tick(0);tick(1000);assert(!kicks);
 puts("PASS pending SIF0 interrupt is left to its normal command handler");
 reset();header.psize=32;tick(0);tick(1000);assert(!kicks);
 puts("PASS unprocessed command buffer is never overwritten by recovery");
 reset();tick(0);registerReads=0;injectPendingAt=3;tick(20);assert(!kicks);
 reset();tick(0);registerReads=0;injectCommandAt=4;tick(20);assert(!kicks);
 puts("PASS IRQ/command arriving between the first check and recheck prevents rearm");
 reset();enabled=0;pfsnetVblankBackupIsr(0);now+=MSec2TimerBusClock(20);pfsnetVblankBackupIsr(0);
 assert(kicks==1&&!enabled);enabled=1;
 puts("PASS interrupt entry state is preserved without enabling nested interrupts");
 reset();g_nmIsInitialized=0;tick(0);tick(1000);assert(!kicks);
 puts("PASS uninitialized NETMAN is not touched");
 reset();tick(0);tick(10);g_apaDiag.sifIrqs++;tick(0);tick(19);assert(!kicks);tick(1);assert(kicks==1);
 puts("PASS new SIF activity resets the stable-idle observation window");
 reset();tick(0);tick(20);assert(kicks==1);chcr&=~0x100;tick(1);tick(20);assert(kicks==1);tick(250);assert(kicks==2);
 puts("PASS repeated stopped states are rate limited");
 reset();
 pfsnetTimerTickAlarmCb(0,0,0,NULL,NULL);now+=MSec2TimerBusClock(20);
 pfsnetTimerTickAlarmCb(0,0,0,NULL,NULL);assert(kicks==1&&checks==2&&enabled);
 puts("PASS actual periodic timer callback also provides the recovery entry");
}
"""
cc=os.environ.get("CC") or shutil.which("cc")
if not cc:raise SystemExit("A native C compiler is required")
with tempfile.TemporaryDirectory(prefix="apa-sif-recovery-") as folder:
    work=Path(folder);(work/"check.c").write_text(code,encoding="utf-8")
    subprocess.run([cc,"-std=c99","-Wall","-Wextra","-Werror",str(work/"check.c"),"-o",str(work/"check")],check=True)
    subprocess.run([str(work/"check")],check=True,timeout=2)
