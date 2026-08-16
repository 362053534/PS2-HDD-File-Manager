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
#include <ps2sdkapi.h>
#include <libpad.h>
#include <delaythread.h>

#include "pfsnet.h"

/* 对齐 PS2HDDTester：跳过 libkernel 运行时补丁与完整 libcglue，避免真机黑屏。 */
DISABLE_PATCHED_FUNCTIONS();
PS2_DISABLE_AUTOSTART_PTHREAD();

void _libcglue_timezone_update(void)
{
}

void _libcglue_init(void)
{
}

void _libcglue_deinit(void)
{
}

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
extern unsigned char BDM_irx[];
extern unsigned int size_BDM_irx;
extern unsigned char BDMFS_FATFS_irx[];
extern unsigned int size_BDMFS_FATFS_irx;
extern unsigned char USBD_irx[];
extern unsigned int size_USBD_irx;
extern unsigned char USBMASS_BD_irx[];
extern unsigned int size_USBMASS_BD_irx;
extern unsigned char PADMAN_irx[];
extern unsigned int size_PADMAN_irx;

static char g_padBuf[256] __attribute__((aligned(64)));
static int g_padOpen;

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
    loadModuleBuffer(SIO2MAN_irx, size_SIO2MAN_irx, 0, NULL);
    loadModuleBuffer(PADMAN_irx, size_PADMAN_irx, 0, NULL);
    loadModuleBuffer(MCMAN_irx, size_MCMAN_irx, 0, NULL);
    loadModuleBuffer(MCSERV_irx, size_MCSERV_irx, 0, NULL);
}

static void padSetup(void)
{
    padInit(0);
    g_padOpen = padPortOpen(0, 0, g_padBuf);
}

static int padStartHeld(void)
{
    struct padButtonStatus buttons;
    int state;

    if (!g_padOpen)
        return 0;
    state = padGetState(0, 0);
    if (state != PAD_STATE_STABLE && state != PAD_STATE_FINDCTP1)
        return 0;
    if (padRead(0, 0, &buttons) == 0)
        return 0;
    return ((0xFFFF ^ buttons.btns) & PAD_START) != 0;
}

static void safeExitElf(void)
{
    scr_printf("Exiting...\n");
    pfsnetStopServer();
    pfsnetFsForceUmount();
    if (g_padOpen)
        padPortClose(0, 0);
    padEnd();
    Exit(0);
}

/* 忽略启动时仍按着的 START，松手后再按才退出 */
static void waitStartToExit(void)
{
    int armed = 0;

    scr_printf("Press START to exit\n");
    while (1) {
        int held = padStartHeld();

        if (!held)
            armed = 1;
        else if (armed)
            safeExitElf();
        DelayThread(50000);
    }
}

/* 与 362053534 OPL bdmLoadModules 相同顺序：BDM → FATFS → USBD → usbmass_bd */
static void loadUsbStack(void)
{
    if (loadModuleBuffer(BDM_irx, size_BDM_irx, 0, NULL) < 0)
        return;
    if (loadModuleBuffer(BDMFS_FATFS_irx, size_BDMFS_FATFS_irx, 0, NULL) < 0)
        return;
    if (loadModuleBuffer(USBD_irx, size_USBD_irx, 0, NULL) < 0)
        return;
    loadModuleBuffer(USBMASS_BD_irx, size_USBMASS_BD_irx, 0, NULL);
}

/* 单行配置摘要：来源 + 模式（静态时附带 IP/NM，用 lwIP 宏避免端序错显） */
static void printIpConfig(const PfsNetIpConfig *cfg)
{
    if (cfg->useDhcp) {
        scr_printf("cfg: %s DHCP\n", pfsnetIpSourceName(cfg->source));
    } else {
        scr_printf("cfg: %s static %u.%u.%u.%u/%u.%u.%u.%u\n",
                   pfsnetIpSourceName(cfg->source),
                   (unsigned)ip4_addr1(&cfg->ip), (unsigned)ip4_addr2(&cfg->ip),
                   (unsigned)ip4_addr3(&cfg->ip), (unsigned)ip4_addr4(&cfg->ip),
                   (unsigned)ip4_addr1(&cfg->nm), (unsigned)ip4_addr2(&cfg->nm),
                   (unsigned)ip4_addr3(&cfg->nm), (unsigned)ip4_addr4(&cfg->nm));
    }
}

/* 唯一的 DHCP 实现。配置里 ps2_ip_use_dhcp=1 也调用这里，不另开一套、不跳回「第一阶段」。 */
static int applyDhcp(PfsNetIpConfig *cfg)
{
    scr_printf("Waiting for link + DHCP...\n");
    if (ethInit(1, &cfg->ip, &cfg->nm, &cfg->gw) < 0 || ethWaitLinkAndDhcp(1) < 0)
        return -1;
    cfg->source = PFSNET_IP_SRC_DHCP;
    strncpy(cfg->sourcePath, "DHCP", sizeof(cfg->sourcePath) - 1);
    cfg->sourcePath[sizeof(cfg->sourcePath) - 1] = '\0';
    cfg->useDhcp = 1;
    ethReadCurrentConfig(&cfg->ip, &cfg->nm, &cfg->gw);
    return 0;
}

static void applyStatic(PfsNetIpConfig *cfg)
{
    cfg->useDhcp = 0;
    if (ethInit(0, &cfg->ip, &cfg->nm, &cfg->gw) < 0)
        scr_printf("WARN: ethInit(static) failed\n");
    ethWaitLinkAndDhcp(0);
    if (ethApplyIPConfig(0, &cfg->ip, &cfg->nm, &cfg->gw) < 0)
        scr_printf("WARN: setconfig(static) failed\n");
}

static void applyDefaultStatic(PfsNetIpConfig *cfg)
{
    cfg->source = PFSNET_IP_SRC_STATIC;
    strncpy(cfg->sourcePath, "192.168.0.10/24", sizeof(cfg->sourcePath) - 1);
    cfg->sourcePath[sizeof(cfg->sourcePath) - 1] = '\0';
    IP4_ADDR(&cfg->ip, 192, 168, 0, 10);
    IP4_ADDR(&cfg->nm, 255, 255, 255, 0);
    IP4_ADDR(&cfg->gw, 192, 168, 0, 1);
    applyStatic(cfg);
}

int main(int argc, char *argv[])
{
    char ipText[32];
    PfsNetIpConfig cfg;

    /* 先 init_scr 再 SifIopReset（与 HDDTester 一致），复位前挂死时仍有屏显。 */
    init_scr();
    scr_clear();
    scr_printf("PfsNetServer\n");
    FlushCache(0);

    while (!SifIopReset("", 0)) {
    }
    while (!SifIopSync()) {
    }
    SifInitRpc(0);
    SifInitIopHeap();
    SifLoadFileInit();
    sbv_patch_enable_lmb();
    sbv_patch_disable_prefix_check();

    loadModuleBuffer(IOMANX_irx, size_IOMANX_irx, 0, NULL);
    loadModuleBuffer(FILEXIO_irx, size_FILEXIO_irx, 0, NULL);
    fileXioInit();

    loadMcStack();
    padSetup();
    loadUsbStack();
    loadModuleBuffer(POWEROFF_irx, size_POWEROFF_irx, 0, NULL);
    loadModuleBuffer(DEV9_irx, size_DEV9_irx, 0, NULL);
    loadHddStack();
    if (pfsnetFsInit() < 0) {
        scr_printf("FATAL: fs init failed\n");
        waitStartToExit();
    }

    loadModuleBuffer(NETMAN_irx, size_NETMAN_irx, 0, NULL);
    loadModuleBuffer(SMAP_irx, size_SMAP_irx, 0, NULL);
    NetManInit();

    /* 大 RW 缓冲放到模块齐后再申请，避免 IOP 堆在加载期被占满 */
    {
        int rw = 512 * 1024;
        if (fileXioSetRWBufferSize(rw) < 0) {
            rw = 256 * 1024;
            if (fileXioSetRWBufferSize(rw) < 0) {
                rw = 128 * 1024;
                fileXioSetRWBufferSize(rw);
            }
        }
    }

    /*
     * 1) 先 DHCP（applyDhcp）。
     * 2) 失败再读网络配置：静态就 applyStatic；文件写 DHCP 则再调一次 applyDhcp（同一套函数），
     *    不是跳回「第一阶段」。仍失败才用文件里的静态或默认地址。
     */
    pfsnetIpConfigInitDefault(&cfg);
    if (applyDhcp(&cfg) < 0) {
        PfsNetIpConfig fileCfg;

        scr_printf("DHCP failed, read network cfg\n");
        pfsnetIpConfigInitDefault(&fileCfg);
        pfsnetLoadIpConfig(&fileCfg, (argc > 0) ? argv[0] : NULL);
        printIpConfig(&fileCfg);

        if (!fileCfg.useDhcp && !ip4_addr_isany_val(fileCfg.ip)) {
            cfg = fileCfg;
            applyStatic(&cfg);
        } else if (fileCfg.useDhcp && fileCfg.source == PFSNET_IP_SRC_OPL) {
            cfg = fileCfg;
            if (applyDhcp(&cfg) < 0) {
                scr_printf("DHCP (from cfg) failed, static fallback\n");
                if (ip4_addr_isany_val(fileCfg.ip))
                    applyDefaultStatic(&cfg);
                else {
                    cfg = fileCfg;
                    applyStatic(&cfg);
                }
            }
        } else if (fileCfg.source == PFSNET_IP_SRC_IPCONFIG) {
            cfg = fileCfg;
            applyStatic(&cfg);
        } else {
            applyDefaultStatic(&cfg);
        }
    }

    /* 清屏后只保留最终状态；无链路则拒绝启服 */
    ethGetIpDisplay(ipText, sizeof(ipText));
    scr_clear();
    scr_printf("PfsNetServer\n");
    scr_printf("IP: %s (%s)\n", ipText, pfsnetIpSourceName(cfg.source));
    scr_printf("NM: %u.%u.%u.%u  GW: %u.%u.%u.%u\n",
               (unsigned)ip4_addr1(&cfg.nm), (unsigned)ip4_addr2(&cfg.nm),
               (unsigned)ip4_addr3(&cfg.nm), (unsigned)ip4_addr4(&cfg.nm),
               (unsigned)ip4_addr1(&cfg.gw), (unsigned)ip4_addr2(&cfg.gw),
               (unsigned)ip4_addr3(&cfg.gw), (unsigned)ip4_addr4(&cfg.gw));
    scr_printf("Link: %s\n", ethLinkIsUp() ? "UP" : "DOWN");
    if (!ethLinkIsUp()) {
        scr_printf("FATAL: no ethernet link (SMAP)\n");
        scr_printf("Check cable/switch, reboot PS2\n");
        waitStartToExit();
    }
    scr_printf("Control :%u  Data(PC listen):%u\n", PFSNET_COMMAND_PORT, PFSNET_DATA_PORT);
    scr_printf("Waiting for PC client...\n");

    if (pfsnetStartServer() < 0) {
        scr_printf("FATAL: server start failed\n");
        waitStartToExit();
    }

    waitStartToExit();
    return 0;
}
