#!/usr/bin/env python3
"""Run actual passive IOP observer with a RAM-window seam, not PS2 emulation.
Checks bounds, bounded reads, and EE/IOP disagreement without any IOP writes.
"""
from pathlib import Path
import os, shutil, subprocess, tempfile
root = Path(__file__).resolve().parents[1]
s = (root/'src/net.c').read_text(encoding='utf-8')
body = s.split('/* IOP_RX_OBSERVE_BEGIN */', 1)[1].split('/* IOP_RX_OBSERVE_END */', 1)[0]
assert '0xbc000000u + base' in body
body = body.replace('0xbc000000u + base', '(uintptr_t)iopMemory + base')
code = r'''
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "diagnostics.h"
#define NETMAN_RPC_BLOCK_SIZE 64
#define UNCACHED_SEG(p) (p)
struct NetManBD {uint16_t length,offset; uint32_t packet,payload,unused;};
static unsigned char iopMemory[0x200000] __attribute__((aligned(16)));
static struct NetManBD ee[64];
static struct NetManBD *g_nmFrameBufferStatus=ee,*g_nmRxIOPFrameBufferStatus;
static unsigned g_nmRxBufferRdPtr;
static int g_nmIsInitialized=1,entries,privileged;
static void ee_kmode_enter(void){assert(!privileged);privileged=1;entries++;}
static void ee_kmode_exit(void){assert(privileged);privileged=0;}
''' + body + r'''
int main(void) {
 ApaDiagnostics s;
 struct NetManBD *iop=(void *)(iopMemory+0x1000);
 g_nmRxIOPFrameBufferStatus=(void *)0x1000;
 memset(&s,0xff,sizeof(s));pfsnetObserveIopRx(&s);
 assert(s.iopValid&&s.iopPending==0&&s.iopFirstPending==UINT32_MAX&&s.iopAtRead==0&&s.iopMismatch==0);
 iop[7].length=1518;iop[63].length=65535;ee[63].length=32;g_nmRxBufferRdPtr=7;
 pfsnetObserveIopRx(&s);
 assert(s.iopValid&&s.iopPending==2&&s.iopFirstPending==7&&s.iopAtRead==1518&&s.iopMismatch==1);
 assert(iop[7].length==1518&&iop[63].length==65535); /* no mutation */
 for(unsigned i=0;i<64;i++)iop[i].length=1;
 pfsnetObserveIopRx(&s);assert(s.iopPending==64&&s.iopMismatch==63);
 assert(!privileged);
 uintptr_t invalid[]={0,1,0x1ffc04,0x200000,UINTPTR_MAX};
 for(unsigned i=0;i<sizeof(invalid)/sizeof(invalid[0]);i++){
  int old=entries;g_nmRxIOPFrameBufferStatus=(void *)invalid[i];pfsnetObserveIopRx(&s);
  assert(!s.iopValid&&!s.iopPending&&s.iopFirstPending==UINT32_MAX&&entries==old);
 }
 g_nmRxIOPFrameBufferStatus=(void *)(0x200000-64*16);
 pfsnetObserveIopRx(&s);assert(s.iopValid&&!s.iopPending);
 int old=entries;g_nmIsInitialized=0;pfsnetObserveIopRx(&s);assert(!s.iopValid&&entries==old);
 g_nmIsInitialized=1;g_nmFrameBufferStatus=NULL;pfsnetObserveIopRx(&s);assert(!s.iopValid&&entries==old);
 puts("PASS actual IOP observer: empty/full/disagreeing rings, invalid/boundary addresses, no IOP writes; not a hardware test");
}
'''
cc=os.environ.get('CC') or shutil.which('cc')
if not cc: raise SystemExit('A native C compiler is required')
with tempfile.TemporaryDirectory(prefix='apa-iop-observer-') as folder:
    path=Path(folder); (path/'check.c').write_text(code,encoding='utf-8')
    subprocess.run([cc,'-std=c99','-Wall','-Wextra','-Werror','-I'+str(root/'include'),str(path/'check.c'),'-o',str(path/'check')],check=True)
    subprocess.run([str(path/'check')],check=True,timeout=2)
