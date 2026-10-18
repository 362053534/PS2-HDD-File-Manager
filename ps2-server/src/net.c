#include <stdio.h>
#include <string.h>
#include <kernel.h>
#include <timer.h>
#include <delaythread.h>
#include <debug.h>
#include <netman.h>
#include <ps2ip.h>

#include "pfsnet.h"

/* GCC14 constructor 写入 .init_array，而 PS2 crt0 只遍历 .ctors，需手动填充 */
extern void __ps2ipeeOpsInitializeImpl(void);

/*
 * PS2SDK 默认 sys_now() 为 clock() / 1000，而 _times()/clock() 用 32 位微秒计数，
 * 会在开机约 71.58 分钟（0xFFFFFFFF us）处从 4294967 跳回 0，破坏 lwIP TCP 定时器比较。
 * 此处用 64 位硬件总线计时器直接换算毫秒，保证在 49.7 天内单调递增并按 2^32 自然回绕。
 */
uint32_t __wrap_sys_now(void)
{
    return (uint32_t)(GetTimerSystemTime() / ((u64)kBUSCLK / 1000ULL));
}

#define ETH_TCPIP_PRIO 0x58 /* lwIP tcpip 线程，见 PS2SDK lwipopts.h */

/*
 * 等链路/DHCP 时必须真正让出 CPU，否则主线程（通常优先级 0）会饿死
 * 优先级 0x58 的 tcpip/DHCP 定时器（362053534 ps2sdk lwipopts.h）。
 *
 * 362053534 OPL ethsupport.c 用 SetAlarm+WaitSema，但 OPL 会打内核补丁。
 * 本 ELF 使用 KERNEL_NOPATCH / DISABLE_PATCHED_FUNCTIONS()，InitAlarm()
 * 是空函数，H-SYNC SetAlarm 回调永不触发，WaitSema 会永久卡死。
 *
 * 因此等待走 362053534 ps2sdk 的 DelayThread（硬件 SetTimerAlarm），
 * 阻塞主线程同时允许 tcpip 运行。若 DelayThread 失败，把本线程降到
 * tcpip 之下再忙等。
 */
static void sleepMs(unsigned int msec)
{
    int tid;
    int oldPrio;
    ee_thread_status_t status;
    u64 start;
    u64 ticks;

    if (msec == 0)
        return;
    if (DelayThread((s32)msec * 1000) == 0)
        return;

    tid = GetThreadId();
    oldPrio = 1;
    memset(&status, 0, sizeof(status));
    if (ReferThreadStatus(tid, &status) >= 0 && status.current_priority > 0)
        oldPrio = status.current_priority;

    /* 降到 tcpip 之下，否则主线程空转仍会饿死 DHCP */
    ChangeThreadPriority(tid, ETH_TCPIP_PRIO + 8);

    start = GetTimerSystemTime();
    ticks = (u64)msec * ((u64)kBUSCLK / 1000ULL);
    while ((GetTimerSystemTime() - start) < ticks)
        RotateThreadReadyQueue(ETH_TCPIP_PRIO);

    ChangeThreadPriority(tid, oldPrio);
}

/* 1s/次，最多 30s（与 OPL 相同上限）；超时返回 -1，绝不静默死等 */
static int WaitValidNetState(int (*checkingFunction)(void))
{
    int retry_cycles;

    for (retry_cycles = 0; checkingFunction() == 0; retry_cycles++) {
        if (retry_cycles >= 30)
            return -1;
        sleepMs(1000);
    }
    return 0;
}

static int g_lastLinkUp;

static int ethGetLinkStatus(void)
{
    int up = NetManIoctl(NETMAN_NETIF_IOCTL_GET_LINK_STATUS, NULL, 0, NULL, 0)
             == NETMAN_NETIF_ETH_LINK_STATE_UP;

    g_lastLinkUp = up;
    return up;
}

int ethLinkIsUp(void)
{
    /* DHCP 失败后停客户端再 ioctl 会卡死 IOP RPC；显示/判定用缓存 */
    return g_lastLinkUp;
}

static int ethGetDhcpStatus(void)
{
    t_ip_info ip_info;

    /* EE ps2ip_getconfig：找到返回 1，未找到返回 0 */
    if (ps2ip_getconfig("sm0", &ip_info) <= 0)
        return 0;
    /* dhcp 客户端尚未启动时 dhcp_enabled=0，必须继续等（不能误判为成功） */
    if (!ip_info.dhcp_enabled)
        return 0;
    if (ip_info.dhcp_status != DHCP_STATE_BOUND)
        return 0;
    /* BOUND 但地址仍为 0 时继续等 */
    if (ip_info.ipaddr.s_addr == 0)
        return 0;
    return 1;
}

/* 与 HDL/sample 一致：仅在模式变化时调用 SetLinkMode，失败不阻断后续 ps2ipInit */
static int ethApplyNetIFConfig(int mode)
{
    static int currentMode = NETMAN_NETIF_ETH_LINK_MODE_AUTO;
    int result;

    if (currentMode != mode) {
        if ((result = NetManSetLinkMode(mode)) == 0)
            currentMode = mode;
    } else {
        result = 0;
    }
    return result;
}

/* 362053534 libps2ip 导出；DHCP 超时后 dhcp_release() 会在主线程等发包，容易卡死 */
void dhcp_stop(struct netif *netif);

static void ethStopDhcpNoRelease(void)
{
    struct netif *nif = netif_find("sm0");

    if (nif)
        dhcp_stop(nif);
}

int ethApplyIPConfig(int use_dhcp, const struct ip4_addr *ip, const struct ip4_addr *nm, const struct ip4_addr *gw)
{
    t_ip_info ip_info;
    int result;

    /* EE ps2ip_getconfig：找到返回 1，未找到返回 0（不会返回负值） */
    if ((result = ps2ip_getconfig("sm0", &ip_info)) <= 0)
        return -1;

    /* 与 OPL ethApplyIPConfig 相同：仅在模式或静态地址变化时写入 */
    if ((use_dhcp != (int)ip_info.dhcp_enabled) ||
        (!use_dhcp &&
         (!ip4_addr_cmp(ip, (struct ip4_addr *)&ip_info.ipaddr) ||
          !ip4_addr_cmp(nm, (struct ip4_addr *)&ip_info.netmask) ||
          !ip4_addr_cmp(gw, (struct ip4_addr *)&ip_info.gw)))) {
        if (use_dhcp) {
            /* LWIP_DHCP_CHECK_LINK_UP=1：须在链路起来后调用；地址清零再 dhcp_start */
            ip4_addr_set_zero((struct ip4_addr *)&ip_info.ipaddr);
            ip4_addr_set_zero((struct ip4_addr *)&ip_info.netmask);
            ip4_addr_set_zero((struct ip4_addr *)&ip_info.gw);
            ip_info.dhcp_enabled = 1;
        } else {
            /* 先停 DHCP 客户端，避免 setconfig 走 dhcp_release() 卡死 */
            if (ip_info.dhcp_enabled)
                ethStopDhcpNoRelease();
            ip4_addr_set((struct ip4_addr *)&ip_info.ipaddr, ip);
            ip4_addr_set((struct ip4_addr *)&ip_info.netmask, nm);
            ip4_addr_set((struct ip4_addr *)&ip_info.gw, gw);
            ip_info.dhcp_enabled = 0;
        }
        return ps2ip_setconfig(&ip_info) ? 0 : -1;
    }
    return 0;
}

int ethInit(int use_dhcp, const struct ip4_addr *ip, const struct ip4_addr *nm, const struct ip4_addr *gw)
{
    static int stackReady = 0;
    static int opsReady = 0;
    struct ip4_addr zeroIp, zeroNm, zeroGw;
    const struct ip4_addr *initIp = ip;
    const struct ip4_addr *initNm = nm;
    const struct ip4_addr *initGw = gw;
    int r;

    StartTimerSystemTime();

    /* 与 HDL/OPL 一致：链路模式失败不在此中止（否则永远到不了 ps2ipInit） */
    ethApplyNetIFConfig(NETMAN_NETIF_ETH_LINK_MODE_AUTO);

    /* 已初始化过：只改 IP 配置（DHCP 失败转静态时不能再次 ps2ipInit） */
    if (stackReady)
        return use_dhcp ? 0 : ethApplyIPConfig(0, ip, nm, gw);

    if (!opsReady) {
        __ps2ipeeOpsInitializeImpl();
        opsReady = 1;
    }

    if (use_dhcp) {
        ip4_addr_set_zero(&zeroIp);
        ip4_addr_set_zero(&zeroNm);
        ip4_addr_set_zero(&zeroGw);
        initIp = &zeroIp;
        initNm = &zeroNm;
        initGw = &zeroGw;
    }

    r = ps2ipInit((struct ip4_addr *)initIp, (struct ip4_addr *)initNm, (struct ip4_addr *)initGw);
    if (r != 0) {
        scr_printf("WARN: ps2ipInit failed (%d)\n", r);
        return -1;
    }

    stackReady = 1;

    /* DHCP 按 OPL：等链路起来后再 ethApplyIPConfig，见 ethWaitLinkAndDhcp */
    if (!use_dhcp)
        ethApplyIPConfig(0, ip, nm, gw);
    return 0;
}

int ethWaitLinkAndDhcp(int use_dhcp)
{
    struct ip4_addr zeroIp, zeroNm, zeroGw;

    /* OPL ethInitApplyConfig：先等到链路，必要时重试 SetLinkMode，再启动 DHCP */
    do {
        if (WaitValidNetState(&ethGetLinkStatus) < 0) {
            scr_printf("WARN: link timeout (no carrier)\n");
            return -1;
        }
    } while (ethApplyNetIFConfig(NETMAN_NETIF_ETH_LINK_MODE_AUTO) != 0);

    if (WaitValidNetState(&ethGetLinkStatus) < 0) {
        scr_printf("WARN: link timeout (no carrier)\n");
        return -1;
    }

    if (use_dhcp) {
        ip4_addr_set_zero(&zeroIp);
        ip4_addr_set_zero(&zeroNm);
        ip4_addr_set_zero(&zeroGw);
        if (ethApplyIPConfig(1, &zeroIp, &zeroNm, &zeroGw) < 0) {
            scr_printf("WARN: dhcp start failed\n");
            return -1;
        }
        if (WaitValidNetState(&ethGetDhcpStatus) < 0) {
            scr_printf("WARN: dhcp timeout\n");
            return -1;
        }
    }
    return 0;
}

void ethGetIpDisplay(char *out, int outLen)
{
    t_ip_info ip_info;

    if (outLen <= 0)
        return;
    out[0] = '\0';
    /* 未找到接口时 getconfig 返回 0 并清零；不能用 <0 判断 */
    if (ps2ip_getconfig("sm0", &ip_info) <= 0) {
        strncpy(out, "?.?.?.?", outLen - 1);
        out[outLen - 1] = '\0';
        return;
    }

    snprintf(out, outLen, "%u.%u.%u.%u",
             (unsigned)ip4_addr1((struct ip4_addr *)&ip_info.ipaddr),
             (unsigned)ip4_addr2((struct ip4_addr *)&ip_info.ipaddr),
             (unsigned)ip4_addr3((struct ip4_addr *)&ip_info.ipaddr),
             (unsigned)ip4_addr4((struct ip4_addr *)&ip_info.ipaddr));
}

int ethReadCurrentConfig(struct ip4_addr *ip, struct ip4_addr *nm, struct ip4_addr *gw)
{
    t_ip_info ip_info;

    if (ps2ip_getconfig("sm0", &ip_info) <= 0)
        return -1;
    if (ip)
        ip4_addr_set(ip, (struct ip4_addr *)&ip_info.ipaddr);
    if (nm)
        ip4_addr_set(nm, (struct ip4_addr *)&ip_info.netmask);
    if (gw)
        ip4_addr_set(gw, (struct ip4_addr *)&ip_info.gw);
    return 0;
}
