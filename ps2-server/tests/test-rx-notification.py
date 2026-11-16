#!/usr/bin/env python3
"""Inject an IRQ at the actual RX worker's check-to-sleep boundary.
Models the owner SDK documented unpatched iWakeupThread running-thread defect.
Real ISR and worker functions; no PS2 hardware or DMA emulation.
"""
from pathlib import Path
import os,shutil,subprocess,tempfile
r=Path(__file__).resolve().parents[1]
s=(r/'src/net.c').read_text(encoding='utf-8')
def function(signature):
 a=s.index(signature+'\n{');b=s.index('{',a)+1;depth=1
 while depth: depth+=(s[b]=='{')-(s[b]=='}');b+=1
 return s[a:b]
code=r'''
#include <assert.h>
#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
#include <setjmp.h>
#include <string.h>
typedef int32_t s32;typedef uint32_t u32;typedef uint8_t u8;
typedef struct{unsigned psize;} SifCmdHeader_t;
struct NetManBD {unsigned length;void *payload,*packet;};
#define UNCACHED_SEG(p) (p)
#define NETMAN_RPC_BLOCK_SIZE 64
#define NETMAN_NETIF_FRAME_SIZE 1516
#define APA_RX_SLEEP 0
#define APA_RX_ALLOC 1
#define APA_RX_DMA 2
#define APA_RX_ENQUEUE 3
static struct NetManBD bd[64],*g_nmFrameBufferStatus=bd;
static unsigned g_nmRxBufferRdPtr,g_nmRxBufferNextRdPtr;
static int g_nmRxThreadID=30,g_nmRxEventSema=9,g_nmIsProcessingRx,g_timerInited;
static struct {void *pktbuf;} _sif_cmd_data;
static struct {unsigned sifIrqs,rxStage,rxAllocRetries,rxPackets;} g_apaDiag;
static int enabled=1,mode,delivered,token,waiting,processed,signals;
static jmp_buf done;
static s32 pfsnetNetManHandleRxEvent(s32 channel);
static void deliver(void){
 assert(enabled);enabled=0;delivered=1;bd[0].length=1516;
 pfsnetNetManHandleRxEvent(0);enabled=1;
}
static void DI(void){assert(enabled);enabled=0;}
static void EI(void){assert(!enabled);enabled=1;if(!delivered&&mode==0)deliver();}
static unsigned _lw(unsigned addr){return addr==0x1000c000?0x01000184:0x00a00044;}
static void _SifCmdIntHandler(int channel){(void)channel;abort();}
static void iSifSetDChain(void){abort();}
static void ExitHandler(void){}
static void pfsnetCheckWaitSlotsIsr(void){}
static int iWakeupThread(int id){assert(id==30);return waiting?0:-1;}
static int iSignalSema(int id){assert(id==9&&!enabled);signals++;if(token==1)return -1;token++;return 0;}
static int WaitSema(int id){
 assert(id==9&&enabled);if(!token){waiting=1;deliver();waiting=0;}
 if(!token){puts("FAIL RX notification vanished before wait");exit(1);}
 token--;return id;
}
static void SleepThread(void){
 if(!delivered){waiting=1;deliver();waiting=0;return;}
 puts("FAIL IRQ at check-to-sleep: RX asleep with pending frame and busy=1 (unpatched self-wake lost)");exit(1);
}
static void DelayThread(int usec){(void)usec;abort();}
static void SifWriteBackDCache(void *p,unsigned n){(void)p;(void)n;}
static void *NetManNetProtStackAllocRxPacket(unsigned n,void **p){(void)n;*p=(void *)16;return (void *)32;}
static void pfsnetNetManClearBufferLen(unsigned i,void *packet,void *payload){(void)packet;(void)payload;bd[i].length=0;}
static void NetManNetProtStackReallocRxPacket(void *p,unsigned n){(void)p;assert(n==1516);}
static void NetManNetProtStackEnQRxPacket(void *p){(void)p;processed++;longjmp(done,1);}
''' + function('static s32 pfsnetNetManHandleRxEvent(s32 channel)')+'\n'+function('static void pfsnetNetManRxThread(void *arg)')+r'''
int main(void){
 (void)g_nmRxEventSema;(void)iWakeupThread;(void)iSignalSema;(void)WaitSema;(void)SleepThread;
 for(mode=0;mode<2;mode++){
  memset(bd,0,sizeof(bd));g_nmRxBufferRdPtr=g_nmRxBufferNextRdPtr=0;
  delivered=token=waiting=processed=signals=g_nmIsProcessingRx=0;
  if(!setjmp(done))pfsnetNetManRxThread(NULL);
  assert(processed==1&&enabled&&g_nmRxBufferRdPtr==1&&signals==1);
 }
 puts("PASS actual RX worker + ISR: IRQ before wait and during wait both deliver the pending frame");
}
'''
lifecycle = r"""
#include <assert.h>
#include <string.h>
#include <stdio.h>
typedef struct {void (*func)(void *);void *stack,*gp_reg;int stack_size,initial_priority,attr,option;} ee_thread_t;
typedef struct {int init_count,max_count;} ee_sema_t;
static int g_nmIsInitialized,g_nmRxEventSema=-1,g_nmRpcSvrThreadID=-1,g_nmRxThreadID=-1,g_nmSifHandlerID=-1;
static int g_nmCbSrv,g_nmCbQueue,_gp;
static char g_nmRpcSvrThreadStack[4096];
static int semResult=9,threadResult=7,startResult,created,deleted,threadDeleted,terminated;
#define DMAC_SIF0 5
static void pfsnetNetManRpcThread(void *p){(void)p;}
static int CreateSema(ee_sema_t *s){assert(!s->init_count&&s->max_count==1);created++;return semResult;}
static int DeleteSema(int id){assert(id==9);deleted++;return 0;}
static int CreateThread(ee_thread_t *t){assert(g_nmRxEventSema==9&&t->func==pfsnetNetManRpcThread);return threadResult;}
static int StartThread(int id,void *arg){assert(id==7&&!arg);return startResult;}
static int DeleteThread(int id){assert(id==7||id==30);threadDeleted++;return 0;}
static int TerminateThread(int id){assert(id==7||id==30);terminated++;return 0;}
static void SifRemoveRpc(void *a,void *b){(void)a;(void)b;}
static void SifRemoveRpcQueue(void *p){(void)p;}
static int RemoveDmacHandler(int a,int b){(void)a;(void)b;return 0;}
""" + function('int __wrap_NetManInitRPCServer(void)')+'\n'+function('void __wrap_NetManDeinitRPCServer(void)')+r"""
int main(void){
 semResult=-22;assert(__wrap_NetManInitRPCServer()==-22&&!g_nmIsInitialized&&!deleted);
 semResult=9;threadResult=-5;assert(__wrap_NetManInitRPCServer()==-5&&g_nmRxEventSema==-1&&deleted==1);
 threadResult=7;startResult=-6;assert(__wrap_NetManInitRPCServer()==-6&&g_nmRpcSvrThreadID==-1&&deleted==2&&threadDeleted==1);
 startResult=0;assert(__wrap_NetManInitRPCServer()==0&&g_nmIsInitialized&&g_nmRxEventSema==9);
 int old=created;assert(__wrap_NetManInitRPCServer()==0&&created==old);
 g_nmRxThreadID=30;__wrap_NetManDeinitRPCServer();
 assert(!g_nmIsInitialized&&g_nmRxEventSema==-1&&g_nmRxThreadID==-1&&terminated==2&&deleted==3&&threadDeleted==3);
 __wrap_NetManDeinitRPCServer();assert(deleted==3);
 assert(__wrap_NetManInitRPCServer()==0&&g_nmRxEventSema==9);__wrap_NetManDeinitRPCServer();assert(deleted==4);
 puts("PASS actual RX event lifecycle: create/start failures, repeated init, teardown and reinit release semaphore exactly once");
}
"""
cc=os.environ.get('CC') or shutil.which('cc')
with tempfile.TemporaryDirectory(prefix='apa-rx-notify-') as d:
 p=Path(d);(p/'test.c').write_text(code,encoding='utf-8')
 subprocess.run([cc,'-std=c99','-Wall','-Wextra','-Werror',str(p/'test.c'),'-o',str(p/'test')],check=True)
 subprocess.run([str(p/'test')],check=True,timeout=3)

with tempfile.TemporaryDirectory(prefix='apa-rx-lifecycle-') as d:
 p=Path(d);(p/'test.c').write_text(lifecycle,encoding='utf-8')
 subprocess.run([cc,'-std=c99','-Wall','-Wextra','-Werror',str(p/'test.c'),'-o',str(p/'test')],check=True)
 subprocess.run([str(p/'test')],check=True,timeout=3)
