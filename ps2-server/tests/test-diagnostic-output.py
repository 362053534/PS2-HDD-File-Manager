#!/usr/bin/env python3
"""Exercise the real output observer through a netif function pointer.
SDK and driver boundaries are stubbed; actual PS2 linkage still needs Actions.
"""
from pathlib import Path
import os, shutil, subprocess, tempfile
root=Path(__file__).resolve().parents[1]
cc=os.environ.get("CC") or shutil.which("cc")
if not cc: raise SystemExit("A native C compiler is required")
with tempfile.TemporaryDirectory(prefix="apa-output-") as folder:
    work=Path(folder)
    (work/"kernel.h").write_text("int DIntr(void); void EIntr(void); int GetThreadId(void);\n",encoding="utf-8")
    (work/"tcpip.h").write_text("#ifndef TEST_TCPIP_H\n#define TEST_TCPIP_H\ntypedef signed char err_t;\nstruct pbuf {int token;};\nstruct netif;\ntypedef err_t (*netif_linkoutput_fn)(struct netif *,struct pbuf *);\nstruct netif {netif_linkoutput_fn linkoutput; unsigned char flags;};\n#endif\n",encoding="utf-8")
    (work/"ps2ip.h").write_text('#include "tcpip.h"\nstruct netif *netif_find(const char *name);\n',encoding="utf-8")
    (work/"netman.h").write_text("int NetManTxPacketNext(void **); int NetManGetGlobalNetIFLinkState(void);\n",encoding="utf-8")
    (work/"check.c").write_text(r"""
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "tcpip.h"
#include "diagnostics.h"
volatile ApaDiagnostics g_apaDiag;
static struct netif interface;
static struct pbuf packet={17};
static int found, enabled=1, called, peeks;
int DIntr(void){int old=enabled;enabled=0;return old;}
void EIntr(void){enabled=1;}
int GetThreadId(void){return 1234;}
struct netif *netif_find(const char *name){assert(!strcmp(name,"sm0"));return found?&interface:0;}
int NetManTxPacketNext(void **payload){peeks++;*payload=&packet;return 128;}
int NetManGetGlobalNetIFLinkState(void){return 1;}
static err_t original(struct netif *n,struct pbuf *p){assert(n==&interface&&p==&packet&&p->token==17);called++;return -7;}
int main(void){
 ApaDiagnostics s={0};
 assert(apaDiagAttachOutput()==-1);
 found=1;assert(apaDiagAttachOutput()==-1);
 interface.linkoutput=original;interface.flags=5;
 assert(apaDiagAttachOutput()==0&&enabled);
 assert(interface.linkoutput!=original);
 assert(apaDiagAttachOutput()==0); /* no recursive wrapping */
 assert(interface.linkoutput(&interface,&packet)==-7);
 assert(called==1&&g_apaDiag.outputCalls==1&&g_apaDiag.outputReturns==1);
 assert(g_apaDiag.outputError==-7&&g_apaDiag.outputErrors==1&&g_apaDiag.outputThreadId==1234);
 apaDiagOutputSnapshot(&s);
 assert(s.outputHook==1&&s.txQueueLen==128&&s.interfaceFlags==5&&s.netmanLink==1&&peeks==1);
 assert(packet.token==17&&called==1); /* a snapshot only peeks, never sends */
 interface.linkoutput=original;apaDiagOutputSnapshot(&s);assert(s.outputHook==0);
 puts("PASS output observer forwards exact packet/result, is idempotent, records real caller, and detects detachment; queue snapshot only peeks");
}
""",encoding="utf-8")
    subprocess.run([cc,"-std=c99","-Wall","-Wextra","-Werror","-I"+str(work),"-I"+str(root/"include"),str(root/"src/diagnostic_output.c"),str(work/"check.c"),"-o",str(work/"check")],check=True)
    subprocess.run([str(work/"check")],check=True,timeout=10)
