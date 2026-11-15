#!/usr/bin/env python3
"""Native test of actual startup polling functions with kernel/network seams.
No PS2 SDK/BIOS is executed. --revision allows checking the pre-fix behavior.
"""
from pathlib import Path
import argparse, os, shutil, subprocess, tempfile
root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument("--revision")
args=parser.parse_args()
source=(subprocess.check_output(["git","show",args.revision+":ps2-server/src/net.c"],cwd=root,text=True,encoding="utf-8")
        if args.revision else (root/"src/net.c").read_text(encoding="utf-8"))
def function(signature):
    start=source.index(signature);opening=source.index("{",start);depth=1;end=opening+1
    while depth:
        depth+=(source[end]=="{")-(source[end]=="}");end+=1
    return source[start:end]
code=r"""
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <stdint.h>
struct ip4_addr {uint32_t addr;};
typedef struct {struct {uint32_t s_addr;} ipaddr;unsigned dhcp_enabled,dhcp_status;} t_ip_info;
#define DHCP_STATE_BOUND 10
#define NETMAN_NETIF_ETH_LINK_STATE_UP 1
#define NETMAN_NETIF_IOCTL_GET_LINK_STATUS 1
#define NETMAN_NETIF_ETH_LINK_MODE_AUTO 0
static int link_up,sleeps,mode_error,config_error,lease_at,lease_calls,rpc_calls,starts,delay_error;
static int g_lastLinkUp;
int NetManIoctl(unsigned cmd,void *arg,unsigned len,void *out,unsigned size) {
 (void)cmd;(void)arg;(void)len;(void)out;(void)size;rpc_calls++;return link_up;
}
int NetManGetGlobalNetIFLinkState(void) {return link_up;}
static int sleepMs(unsigned ms) {assert(ms==1000);sleeps++;return delay_error;}
static int ethApplyNetIFConfig(int mode) {(void)mode;return mode_error;}
int ethApplyIPConfig(int dhcp,const struct ip4_addr *ip,const struct ip4_addr *nm,const struct ip4_addr *gw) {
 assert(dhcp==1 && ip->addr==0 && nm->addr==0 && gw->addr==0);starts++;return config_error;
}
int ps2ip_getconfig(char *name,t_ip_info *info) {
 assert(!strcmp(name,"sm0"));memset(info,0,sizeof(*info));lease_calls++;
 info->dhcp_enabled=1;
 if(lease_at && lease_calls>=lease_at) {info->dhcp_status=DHCP_STATE_BOUND;info->ipaddr.s_addr=1;}
 return 1;
}
#define ip4_addr_set_zero(p) ((p)->addr=0)
int scr_printf(const char *format,...) {(void)format;return 0;}
"""
if "static const char *g_bootFailure;" in source:
    code+=source[source.index("static const char *g_bootFailure;"):source.index("#define ETH_TCPIP_PRIO")]
for sig in ["static int WaitValidNetState(","static int ethGetLinkStatus(","static int ethGetDhcpStatus(","int ethWaitLinkAndDhcp("]:
    code+="\n"+function(sig)
code+=r"""
static void reset(void) {g_bootFailure=NULL;g_bootFailureCode=0;link_up=1;sleeps=mode_error=config_error=lease_at=lease_calls=rpc_calls=starts=delay_error=0;}
int main(void) {
 setbuf(stdout,NULL);
 reset();assert(ethWaitLinkAndDhcp(0)==0);
 if(rpc_calls) {puts("FAIL link polling called synchronous IOP RPC instead of reading EE events");return 1;}
 assert(sleeps==0 && starts==0 && g_lastLinkUp);
 puts("PASS ready link uses EE events with no IOP RPC or extra delay");
 reset();link_up=0;assert(ethWaitLinkAndDhcp(0)==-1);
 assert(sleeps==30 && rpc_calls==0 && starts==0 && !g_lastLinkUp);
 puts("PASS missing carrier returns after 30 polling seconds without starting DHCP");
 reset();mode_error=-1;assert(ethWaitLinkAndDhcp(0)==-1 && sleeps==0);
 puts("PASS mode error returns instead of repeating an unbounded loop");
 reset();assert(ethWaitLinkAndDhcp(1)==-1);
 assert(sleeps==30 && starts==1 && rpc_calls==0);
 puts("PASS no DHCP lease reaches its independent 30-second poll limit");
 reset();lease_at=4;assert(ethWaitLinkAndDhcp(1)==0);
 assert(sleeps==3 && starts==1 && rpc_calls==0);
 puts("PASS delayed DHCP lease succeeds after actual BOUND/nonzero address");
 reset();config_error=-1;assert(ethWaitLinkAndDhcp(1)==-1 && sleeps==0 && starts==1);
 assert(!strcmp(ethBootFailure(NULL),"DHCP start") && g_bootFailureCode==-1);
 puts("PASS DHCP start error returns immediately and retains the failure reason");
 reset();link_up=0;delay_error=-1;
 assert(ethWaitLinkAndDhcp(0)==-1 && sleeps==1 && starts==0);
 reset();ethBootFail("rotate ready queue",-42);link_up=0;delay_error=-1;
 assert(ethWaitLinkAndDhcp(0)==-1);
 assert(!strcmp(ethBootFailure(NULL),"rotate ready queue") && g_bootFailureCode==-42);
 puts("PASS startup scheduler failure exits instead of pretending the delay succeeded");
}
"""
if "static const char *g_bootFailure;" not in source:
    code=code.replace("g_bootFailure=NULL;g_bootFailureCode=0;", "")
    code=code.replace(' assert(!strcmp(ethBootFailure(NULL),"DHCP start") && g_bootFailureCode==-1);', "")
    first=code.index(' reset();ethBootFail(')
    last=code.index(' puts("PASS startup scheduler', first)
    code=code[:first]+code[last:]
cc=os.environ.get("CC") or shutil.which("cc")
if not cc:raise SystemExit("A native C compiler is required")
with tempfile.TemporaryDirectory(prefix="apa-boot-link-") as folder:
    work=Path(folder);(work/"check.c").write_text(code,encoding="utf-8")
    subprocess.run([cc,"-std=c99","-Wall","-Wextra","-Werror",str(work/"check.c"),"-o",str(work/"check")],check=True)
    subprocess.run([str(work/"check")],check=True,timeout=5)
