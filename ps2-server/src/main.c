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
#include <hdd-ioctl.h>

#include "pfsnet.h"
#include "diagnostics.h"

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
static uint8_t g_padStack[0x800] __attribute__((aligned(16)));
static int g_padOpen;
static int g_padWatchStarted;
static int g_mainTid;

extern void *_gp;

/* 低于 tcpip(0x58) 和 server(0x3f)，避免抢网络 */
#define PAD_WATCH_PRIO 0x70
#define PAD_POLL_US 250000

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
    static char pfsArg[] = "-m" "\0" "2" "\0" "-o" "\0" "4" "\0" "-n" "\0" "32";

    loadModuleBuffer(ATAD_irx, size_ATAD_irx, 0, NULL);
    loadModuleBuffer(HDD_irx, size_HDD_irx, sizeof(hddArg), hddArg);
    loadModuleBuffer(PFS_irx, size_PFS_irx, sizeof(pfsArg), pfsArg);
}

/* 与 362053534 OPL 一致：一字节 0 关闭 ATA 自动待机计时器。
 * ATAD 初始化会恢复默认计时，因此每次启动都设置；退出时仍正常停盘。
 * 仅设置服务实际使用的 hdd0，不通过周期读写来保持硬盘唤醒。 */
static int disableHddStandby(void)
{
    unsigned char standbyTimer = 0;
    return fileXioDevctl("hdd0:", HDIOC_IDLE, &standbyTimer,
                         sizeof(standbyTimer), NULL, 0);
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

/* 停栈对齐 uLE（umount + HDIOC_DEV9OFF + IOP UDNL 复位），否则脏 DEV9/SMAP/HDD 会黑屏。
 * 回菜单不能带 SkipMc/SkipHdd/BootBrowser：那是 uLE 从 MBR 出来时故意绕过 FMCB/FHDB、
 * 进原版浏览器的参数。不带参数让 rom0:OSDSYS 自己找 MC/HDD，才能回到 FHDB 选单。 */
static void safeExitElf(void)
{
    scr_printf("Exiting...\n");
    pfsnetFsForceUmount();
    fileXioDevctl("hdd0:", HDIOC_DEV9OFF, NULL, 0, NULL, 0);

    SifInitRpc(0);
    while (!SifIopReset("rom0:UDNL ", 0)) {
    }
    while (!SifIopSync()) {
    }
    SifInitRpc(0);
    SifLoadFileInit();

    _LoadExecPS2("rom0:OSDSYS", 0, NULL);
}

/* 低优先级后台看 START；只唤醒主线程，真正退出走 main */
static void padWatchThread(void *arg)
{
    int armed = 0;

    (void)arg;
    while (1) {
        if (!pfsnetPadPollAllowed()) {
            /* 收发中或停传未满 2 秒：不碰手柄，松开 START 后才重新武装 */
            armed = 0;
            DelayThread(PAD_POLL_US);
            continue;
        }

        {
            int held = padStartHeld();

            if (!held)
                armed = 1;
            else if (armed && g_mainTid > 0)
                WakeupThread(g_mainTid);
        }
        DelayThread(PAD_POLL_US);
    }
}

static void startPadWatch(void)
{
    ee_thread_t th;
    int tid;

    if (g_padWatchStarted)
        return;
    memset(&th, 0, sizeof(th));
    th.func = &padWatchThread;
    th.stack = g_padStack;
    th.stack_size = sizeof(g_padStack);
    th.gp_reg = &_gp;
    th.initial_priority = PAD_WATCH_PRIO;
    tid = CreateThread(&th);
    if (tid <= 0)
        return;
    StartThread(tid, NULL);
    g_padWatchStarted = 1;
}

/* 出错停住：提示后睡主线程，按键交给后台线程，醒来后安全退出 */
static void waitStartToExit(void)
{
    scr_printf("Press START to exit\n");
    startPadWatch();
    SleepThread();
    safeExitElf();
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
    scr_printf("Applying static IP...\n");
    /* 栈已在 DHCP 阶段初始化；ethInit(0) 只切静态，不再等 DHCP。
     * 链路已在第一轮确认过，不再走 ethWaitLinkAndDhcp，避免无谓等待。 */
    if (ethInit(0, &cfg->ip, &cfg->nm, &cfg->gw) < 0)
        scr_printf("WARN: ethInit(static) failed\n");
    if (ethApplyIPConfig(0, &cfg->ip, &cfg->nm, &cfg->gw) < 0)
        scr_printf("WARN: setconfig(static) failed\n");
}

static void failNoIp(void)
{
    scr_printf("FATAL: failed to obtain an IP address\n");
    waitStartToExit();
}

int main(int argc, char *argv[])
{
    char ipText[32];
    PfsNetIpConfig cfg;
    int hddStandbyResult;

    /* 先 init_scr 再 SifIopReset（与 HDDTester 一致），复位前挂死时仍有屏显。 */
    g_mainTid = GetThreadId();
    init_scr();
    scr_clear();
    scr_printf("ApaNetServer\n");
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
    loadModuleBuffer(POWEROFF_irx, size_POWEROFF_irx, 0, NULL);
    loadModuleBuffer(DEV9_irx, size_DEV9_irx, 0, NULL);
    loadHddStack();
    hddStandbyResult = disableHddStandby();
    if (pfsnetFsInit() < 0) {
        scr_printf("FATAL: fs init failed\n");
        waitStartToExit();
    }

    {
        /* FILEXIO.IRX 的 RPC 线程优先级为 40(0x28)，而 SMAP.IRX 默认线程优先级也是 40(0x28)。
         * 将 SMAP 线程优先级设为 36(0x24)，使其在写盘期间收到以太网中断时可立即抢占排空 16KB 硬件 RX FIFO。 */
        static char smapArg[] = "thpri=36";
        loadModuleBuffer(NETMAN_irx, size_NETMAN_irx, 0, NULL);
        loadModuleBuffer(SMAP_irx, size_SMAP_irx, sizeof(smapArg), smapArg);
        NetManInit();
    }

    /*
     * 1) DHCP
     * 2) 失败：OPL conf_network.cfg 里的静态地址（不再跑 OPL DHCP）
     * 3) 再失败：uLE IPCONFIG.DAT
     * 4) 仍没有可用地址：报错，按 START 退出（不写兜底 IP）
     */
    pfsnetIpConfigInitDefault(&cfg);
    if (applyDhcp(&cfg) < 0) {
        const char *bootPath = (argc > 0) ? argv[0] : NULL;
        PfsNetIpConfig fileCfg;

        scr_printf("DHCP failed, read network cfg\n");
        loadUsbStack();

        if (pfsnetLoadOplStatic(&fileCfg, bootPath) == 0) {
            printIpConfig(&fileCfg);
            cfg = fileCfg;
            applyStatic(&cfg);
        } else if (pfsnetLoadUleStatic(&fileCfg, bootPath) == 0) {
            printIpConfig(&fileCfg);
            cfg = fileCfg;
            applyStatic(&cfg);
        } else {
            failNoIp();
        }
    }

    /* 在网络栈与 USB 分配完 IOP 堆之后再设置 64KiB RWBufferSize：
     * 既大幅减少 SIF 往返次数，又避免单次 128KiB+ SIF1 DMA 长时间阻塞 NETMAN_TxThread 发送 TCP ACK。 */
    fileXioSetRWBufferSize(64 * 1024);

    /* 清屏后只保留最终状态；链路用 DHCP 阶段缓存，不再 NetManIoctl */
    ethGetIpDisplay(ipText, sizeof(ipText));
    scr_clear();
    scr_printf("ApaNetServer\n");
    scr_printf("IP: %s (%s)\n", ipText, pfsnetIpSourceName(cfg.source));
    if (hddStandbyResult == 0)
        scr_printf("HDD automatic standby: disabled\n");
    else
        scr_printf("WARN: disable HDD standby failed (%d)\n", hddStandbyResult);
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

    scr_printf("Press START to exit\n");
    if (apaDiagStart() < 0)
        scr_printf("WARN: DIAG-1 monitor failed to start\n");
    startPadWatch();
    SleepThread();
    safeExitElf();
    return 0;
}
