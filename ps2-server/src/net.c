#include <stdio.h>
#include <string.h>
#include <kernel.h>
#include <timer.h>
#include <debug.h>
#include <netman.h>
#include <ps2ip.h>

#include "pfsnet.h"

/* GCC14 constructor 写入 .init_array，而 PS2 crt0 只遍历 .ctors，需手动填充 */
extern void __ps2ipeeOpsInitializeImpl(void);

/* 约 msec 毫秒忙等：不用 SetAlarm/SleepThread（NOPATCH 下可能永不唤醒） */
static void busyWaitMs(unsigned int msec)
{
    u64 start;
    u64 ticks;

    StartTimerSystemTime();
    start = GetTimerSystemTime();
    ticks = (u64)msec * ((u64)kBUSCLK / 1000ULL);
    while ((GetTimerSystemTime() - start) < ticks) {
    }
}

/* 有界等待：约 100ms/次，最多 ~30s；超时返回 -1，绝不静默死等 */
static int WaitValidNetState(int (*checkingFunction)(void))
{
    int retry;

    for (retry = 0; checkingFunction() == 0; retry++) {
        busyWaitMs(100);
        if (retry >= 300)
            return -1;
    }
    return 0;
}

static int ethGetLinkStatus(void)
{
    return NetManIoctl(NETMAN_NETIF_IOCTL_GET_LINK_STATUS, NULL, 0, NULL, 0) == NETMAN_NETIF_ETH_LINK_STATE_UP;
}

int ethLinkIsUp(void)
{
    return ethGetLinkStatus();
}

static int ethGetDhcpStatus(void)
{
    t_ip_info ip_info;

    /* EE ps2ip_getconfig：找到返回 1，未找到返回 0 */
    if (ps2ip_getconfig("sm0", &ip_info) <= 0)
        return 0;
    if (!ip_info.dhcp_enabled)
        return 1;
    return (ip_info.dhcp_status == DHCP_STATE_BOUND || ip_info.dhcp_status == DHCP_STATE_OFF);
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

int ethApplyIPConfig(int use_dhcp, const struct ip4_addr *ip, const struct ip4_addr *nm, const struct ip4_addr *gw)
{
    t_ip_info ip_info;
    int result;

    /* EE ps2ip_getconfig：找到返回 1，未找到返回 0（不会返回负值） */
    if ((result = ps2ip_getconfig("sm0", &ip_info)) <= 0)
        return -1;

    if (use_dhcp) {
        ip_info.dhcp_enabled = 1;
    } else {
        ip4_addr_set((struct ip4_addr *)&ip_info.ipaddr, ip);
        ip4_addr_set((struct ip4_addr *)&ip_info.netmask, nm);
        ip4_addr_set((struct ip4_addr *)&ip_info.gw, gw);
        ip_info.dhcp_enabled = 0;
    }
    return ps2ip_setconfig(&ip_info) ? 0 : -1;
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

    /* 与 HDL 一致：链路模式失败不在此中止（否则永远到不了 ps2ipInit） */
    ethApplyNetIFConfig(NETMAN_NETIF_ETH_LINK_MODE_AUTO);

    /* 已初始化过：只改 IP 配置（DHCP 失败转静态时不能再次 ps2ipInit） */
    if (stackReady)
        return ethApplyIPConfig(use_dhcp, ip, nm, gw);

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

    if (use_dhcp)
        return ethApplyIPConfig(1, ip, nm, gw);

    /* 静态：地址已由 ps2ipInit 写入；再 setconfig 确保 dhcp 关闭（失败不回滚已写入地址） */
    ethApplyIPConfig(0, ip, nm, gw);
    return 0;
}

int ethWaitLinkAndDhcp(int use_dhcp)
{
    /* 无载波时 IP 仍可能已写入，但无法收发——必须视为失败 */
    if (WaitValidNetState(&ethGetLinkStatus) < 0) {
        scr_printf("WARN: link timeout (no carrier)\n");
        return -1;
    }
    if (use_dhcp) {
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
