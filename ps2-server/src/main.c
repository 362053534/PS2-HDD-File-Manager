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
    loadModuleBuffer(MCMAN_irx, size_MCMAN_irx, 0, NULL);
    loadModuleBuffer(MCSERV_irx, size_MCSERV_irx, 0, NULL);
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

int main(int argc, char *argv[])
{
    char ipText[32];
    PfsNetIpConfig cfg;

    (void)argc;
    (void)argv;

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
    loadModuleBuffer(POWEROFF_irx, size_POWEROFF_irx, 0, NULL);
    loadModuleBuffer(DEV9_irx, size_DEV9_irx, 0, NULL);
    loadHddStack();
    if (pfsnetFsInit() < 0) {
        scr_printf("FATAL: fs init failed\n");
        SleepThread();
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

    /* OPL 静态直接用；否则 DHCP，失败再 uLE/默认静态兜底 */
    pfsnetLoadIpConfig(&cfg);
    printIpConfig(&cfg);

    if (cfg.useDhcp) {
        if (ethInit(1, &cfg.ip, &cfg.nm, &cfg.gw) < 0 || ethWaitLinkAndDhcp(1) < 0) {
            scr_printf("DHCP failed, static fallback\n");
            cfg.useDhcp = 0;
            if (cfg.source != PFSNET_IP_SRC_OPL && cfg.source != PFSNET_IP_SRC_IPCONFIG) {
                cfg.source = PFSNET_IP_SRC_STATIC;
                strncpy(cfg.sourcePath, "192.168.0.10/24", sizeof(cfg.sourcePath) - 1);
                IP4_ADDR(&cfg.ip, 192, 168, 0, 10);
                IP4_ADDR(&cfg.nm, 255, 255, 255, 0);
                IP4_ADDR(&cfg.gw, 192, 168, 0, 1);
            }
            printIpConfig(&cfg);
            if (ethInit(0, &cfg.ip, &cfg.nm, &cfg.gw) < 0)
                scr_printf("WARN: ethInit(fallback) failed\n");
            /* 链路结果在下方统一检查；此处仍确保 setconfig 写入静态地址 */
            ethWaitLinkAndDhcp(0);
            if (ethApplyIPConfig(0, &cfg.ip, &cfg.nm, &cfg.gw) < 0)
                scr_printf("WARN: setconfig(fallback) failed\n");
        } else {
            cfg.source = PFSNET_IP_SRC_DHCP;
            strncpy(cfg.sourcePath, "DHCP", sizeof(cfg.sourcePath) - 1);
            cfg.sourcePath[sizeof(cfg.sourcePath) - 1] = '\0';
        }
    } else {
        /* OPL（或其它）已给出静态：不走 DHCP */
        if (ethInit(0, &cfg.ip, &cfg.nm, &cfg.gw) < 0)
            scr_printf("WARN: ethInit(static) failed\n");
        ethWaitLinkAndDhcp(0);
        if (ethApplyIPConfig(0, &cfg.ip, &cfg.nm, &cfg.gw) < 0)
            scr_printf("WARN: setconfig(static) failed\n");
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
        SleepThread();
    }
    scr_printf("Control :%u  Data(PC listen):%u\n", PFSNET_COMMAND_PORT, PFSNET_DATA_PORT);
    scr_printf("Waiting for PC client...\n");

    if (pfsnetStartServer() < 0) {
        scr_printf("FATAL: server start failed\n");
        SleepThread();
    }

    SleepThread();
    return 0;
}
