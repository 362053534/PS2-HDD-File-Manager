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
extern unsigned char SIO2MAN_irx[];
extern unsigned int size_SIO2MAN_irx;
extern unsigned char MCMAN_irx[];
extern unsigned int size_MCMAN_irx;
extern unsigned char MCSERV_irx[];
extern unsigned int size_MCSERV_irx;
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

static void loadMcStack(void)
{
    int r;

    r = loadModuleBuffer(SIO2MAN_irx, size_SIO2MAN_irx, 0, NULL);
    scr_printf("sio2man: %d\n", r);
    r = loadModuleBuffer(MCMAN_irx, size_MCMAN_irx, 0, NULL);
    scr_printf("mcman: %d\n", r);
    r = loadModuleBuffer(MCSERV_irx, size_MCSERV_irx, 0, NULL);
    scr_printf("mcserv: %d\n", r);
}

static void printIpConfig(const PfsNetIpConfig *cfg)
{
    unsigned char *b;

    scr_printf("IP source: %s\n", pfsnetIpSourceName(cfg->source));
    if (cfg->sourcePath[0])
        scr_printf("  path: %s\n", cfg->sourcePath);
    if (cfg->useDhcp) {
        scr_printf("  mode: DHCP\n");
    } else {
        b = (unsigned char *)&cfg->ip;
        scr_printf("  mode: static %u.%u.%u.%u\n", b[0], b[1], b[2], b[3]);
    }
}

int main(int argc, char *argv[])
{
    char ipText[32];
    PfsNetIpConfig cfg;

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

    loadMcStack();
    loadModuleBuffer(POWEROFF_irx, size_POWEROFF_irx, 0, NULL);
    loadModuleBuffer(DEV9_irx, size_DEV9_irx, 0, NULL);
    loadHddStack();
    pfsnetFsInit();

    {
        int rNet, rSmap, rNm;

        rNet = loadModuleBuffer(NETMAN_irx, size_NETMAN_irx, 0, NULL);
        rSmap = loadModuleBuffer(SMAP_irx, size_SMAP_irx, 0, NULL);
        rNm = NetManInit();
        scr_printf("NETMAN:%d SMAP:%d NetManInit:%d\n", rNet, rSmap, rNm);
    }

    /* A: IPCONFIG.DAT → OPL conf_network.cfg → DHCP → 静态兜底 */
    pfsnetLoadIpConfig(&cfg);
    printIpConfig(&cfg);

    if (!cfg.useDhcp) {
        int rEth, rLink, rCfg;

        scr_printf("Starting network (static)...\n");
        rEth = ethInit(0, &cfg.ip, &cfg.nm, &cfg.gw);
        scr_printf("ethInit(static): %d\n", rEth);
        if (rEth < 0)
            scr_printf("WARN: ethInit(static) failed\n");
        rLink = ethWaitLinkAndDhcp(0);
        scr_printf("link wait: %d\n", rLink);
        if (rLink < 0)
            scr_printf("WARN: link wait timeout\n");
        /* 链路起来后再 setconfig 一次，避免 init 时接口尚未稳定 */
        rCfg = ethApplyIPConfig(0, &cfg.ip, &cfg.nm, &cfg.gw);
        scr_printf("setconfig: %d\n", rCfg);
    } else {
        scr_printf("Starting network (DHCP)...\n");
        if (ethInit(1, &cfg.ip, &cfg.nm, &cfg.gw) < 0 || ethWaitLinkAndDhcp(1) < 0) {
            scr_printf("DHCP failed, fallback 192.168.0.10\n");
            cfg.useDhcp = 0;
            cfg.source = PFSNET_IP_SRC_STATIC;
            strncpy(cfg.sourcePath, "192.168.0.10/24", sizeof(cfg.sourcePath) - 1);
            IP4_ADDR(&cfg.ip, 192, 168, 0, 10);
            IP4_ADDR(&cfg.nm, 255, 255, 255, 0);
            IP4_ADDR(&cfg.gw, 192, 168, 0, 1);
            if (ethInit(0, &cfg.ip, &cfg.nm, &cfg.gw) < 0)
                scr_printf("WARN: ethInit(fallback) failed\n");
            ethWaitLinkAndDhcp(0);
            ethApplyIPConfig(0, &cfg.ip, &cfg.nm, &cfg.gw);
        }
    }

    ethGetIpDisplay(ipText, sizeof(ipText));
    scr_printf("IP: %s (%s)\n", ipText, pfsnetIpSourceName(cfg.source));
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
