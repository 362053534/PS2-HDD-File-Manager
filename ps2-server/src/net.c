#include <stdio.h>
#include <string.h>
#include <kernel.h>
#include <netman.h>
#include <ps2ip.h>

#include "pfsnet.h"

static void EthStatusCheckCb(s32 alarm_id, u16 time, void *common)
{
    (void)alarm_id;
    (void)time;
    iWakeupThread(*(int *)common);
}

static int WaitValidNetState(int (*checkingFunction)(void))
{
    int threadId = GetThreadId();
    int retry;

    for (retry = 0; checkingFunction() == 0; retry++) {
        SetAlarm(1000 * 16, &EthStatusCheckCb, &threadId);
        SleepThread();
        if (retry >= 10)
            return -1;
    }
    return 0;
}

static int ethGetLinkStatus(void)
{
    return NetManIoctl(NETMAN_NETIF_IOCTL_GET_LINK_STATUS, NULL, 0, NULL, 0) == NETMAN_NETIF_ETH_LINK_STATE_UP;
}

static int ethGetDhcpStatus(void)
{
    t_ip_info ip_info;
    if (ps2ip_getconfig("sm0", &ip_info) < 0)
        return 0;
    if (!ip_info.dhcp_enabled)
        return 1;
    return (ip_info.dhcp_status == DHCP_STATE_BOUND || ip_info.dhcp_status == DHCP_STATE_OFF);
}

static int ethApplyIPConfig(int use_dhcp, const struct ip4_addr *ip, const struct ip4_addr *nm, const struct ip4_addr *gw)
{
    t_ip_info ip_info;
    int result;

    if ((result = ps2ip_getconfig("sm0", &ip_info)) < 0)
        return result;

    if (use_dhcp) {
        ip_info.dhcp_enabled = 1;
    } else {
        ip_addr_set((struct ip4_addr *)&ip_info.ipaddr, ip);
        ip_addr_set((struct ip4_addr *)&ip_info.netmask, nm);
        ip_addr_set((struct ip4_addr *)&ip_info.gw, gw);
        ip_info.dhcp_enabled = 0;
    }
    return ps2ip_setconfig(&ip_info);
}

int ethInit(int use_dhcp, const struct ip4_addr *ip, const struct ip4_addr *nm, const struct ip4_addr *gw)
{
    struct ip4_addr zeroIp, zeroNm, zeroGw;
    const struct ip4_addr *initIp = ip;
    const struct ip4_addr *initNm = nm;
    const struct ip4_addr *initGw = gw;

    if (NetManSetLinkMode(NETMAN_NETIF_ETH_LINK_MODE_AUTO) < 0)
        return -1;

    if (use_dhcp) {
        ip4_addr_set_zero(&zeroIp);
        ip4_addr_set_zero(&zeroNm);
        ip4_addr_set_zero(&zeroGw);
        initIp = &zeroIp;
        initNm = &zeroNm;
        initGw = &zeroGw;
    }

    if (ps2ipInit((struct ip4_addr *)initIp, (struct ip4_addr *)initNm, (struct ip4_addr *)initGw) != 0)
        return -1;

    if (use_dhcp)
        return ethApplyIPConfig(1, ip, nm, gw);
    return 0;
}

int ethWaitLinkAndDhcp(int use_dhcp)
{
    if (WaitValidNetState(&ethGetLinkStatus) < 0)
        return -1;
    if (use_dhcp)
        return WaitValidNetState(&ethGetDhcpStatus);
    return 0;
}

void ethGetIpDisplay(char *out, int outLen)
{
    t_ip_info ip_info;
    unsigned char *bytes;

    if (outLen <= 0)
        return;
    out[0] = '\0';
    if (ps2ip_getconfig("sm0", &ip_info) < 0) {
        strncpy(out, "?.?.?.?", outLen - 1);
        out[outLen - 1] = '\0';
        return;
    }

    bytes = (unsigned char *)&ip_info.ipaddr;
    snprintf(out, outLen, "%u.%u.%u.%u", bytes[0], bytes[1], bytes[2], bytes[3]);
}
