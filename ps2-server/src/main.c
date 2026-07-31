#include <stdio.h>
#include <string.h>
#include <kernel.h>
#include <sifrpc.h>
#include <loadfile.h>
#include <iopheap.h>
#include <iopcontrol.h>
#include <sbv_patches.h>
#include <libpwroff.h>
#include <fileXio_rpc.h>
#include <debug.h>
#include <netman.h>
#include <ps2ip.h>

#include "pfsnet.h"

extern unsigned char IOMANX_irx[];
extern unsigned int size_IOMANX_irx;
extern unsigned char FILEXIO_irx[];
extern unsigned int size_FILEXIO_irx;
extern unsigned char POWEROFF_irx[];
extern unsigned int size_POWEROFF_irx;
extern unsigned char DEV9_irx[];
extern unsigned int size_DEV9_irx;
extern unsigned char ATAD_irx[];
extern unsigned int size_ATAD_irx;
extern unsigned char HDD_irx[];
extern unsigned int size_HDD_irx;
extern unsigned char PFS_irx[];
extern unsigned int size_PFS_irx;
extern unsigned char NETMAN_irx[];
extern unsigned int size_NETMAN_irx;
extern unsigned char SMAP_irx[];
extern unsigned int size_SMAP_irx;

static int loadModuleBuffer(void *buffer, unsigned int size, int argc, char *arg)
{
    int result;
    if ((result = SifExecModuleBuffer(buffer, size, argc, arg, NULL)) < 0)
        return result;
    return 0;
}

static void loadHddStack(void)
{
    static char hddArg[] = "-o" "\0" "4" "\0" "-n" "\0" "128";
    static char pfsArg[] = "-m" "\0" "2" "\0" "-o" "\0" "4" "\0" "-n" "\0" "12";

    loadModuleBuffer(ATAD_irx, size_ATAD_irx, 0, NULL);
    loadModuleBuffer(HDD_irx, size_HDD_irx, sizeof(hddArg), hddArg);
    loadModuleBuffer(PFS_irx, size_PFS_irx, sizeof(pfsArg), pfsArg);
}

int main(int argc, char *argv[])
{
    char ipText[32];
    struct ip4_addr ip, nm, gw;
    int useDhcp = 1;

    (void)argc;
    (void)argv;

    SifInitRpc(0);
    while (!SifIopReset("", 0)) {
    }
    while (!SifIopSync()) {
    }

    SifInitRpc(0);
    SifInitIopHeap();
    SifLoadFileInit();
    sbv_patch_enable_lmb();
    sbv_patch_disable_prefix_check();

    init_scr();
    scr_printf("PfsNetServer P1\n");
    scr_printf("Using local PS2SDK (UTF-8 path bytes)\n");

    loadModuleBuffer(IOMANX_irx, size_IOMANX_irx, 0, NULL);
    loadModuleBuffer(FILEXIO_irx, size_FILEXIO_irx, 0, NULL);
    fileXioInit();

    loadModuleBuffer(POWEROFF_irx, size_POWEROFF_irx, 0, NULL);
    loadModuleBuffer(DEV9_irx, size_DEV9_irx, 0, NULL);
    loadHddStack();
    pfsnetFsInit();

    loadModuleBuffer(NETMAN_irx, size_NETMAN_irx, 0, NULL);
    loadModuleBuffer(SMAP_irx, size_SMAP_irx, 0, NULL);
    NetManInit();

    /* 默认 DHCP；失败时回退 192.168.0.10/24 */
    IP4_ADDR(&ip, 192, 168, 0, 10);
    IP4_ADDR(&nm, 255, 255, 255, 0);
    IP4_ADDR(&gw, 192, 168, 0, 1);

    scr_printf("Starting network (DHCP)...\n");
    if (ethInit(useDhcp, &ip, &nm, &gw) < 0 || ethWaitLinkAndDhcp(useDhcp) < 0) {
        scr_printf("DHCP failed, trying static 192.168.0.10\n");
        useDhcp = 0;
        ethInit(useDhcp, &ip, &nm, &gw);
        ethWaitLinkAndDhcp(useDhcp);
    }

    ethGetIpDisplay(ipText, sizeof(ipText));
    scr_printf("IP: %s\n", ipText);
    scr_printf("Control :%u  Data(PC listen):%u\n", PFSNET_COMMAND_PORT, PFSNET_DATA_PORT);
    scr_printf("Waiting for PC client...\n");
    scr_printf("No pad interaction required.\n");

    if (pfsnetStartServer() < 0) {
        scr_printf("FATAL: server start failed\n");
        SleepThread();
    }

    SleepThread();
    return 0;
}
