#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

#include <debug.h>
#include <fileXio_rpc.h>
#include <io_common.h>

#include "pfsnet.h"

static int parseIpv4(const char *text, struct ip4_addr *out)
{
    unsigned int a, b, c, d;

    if (!text || !out)
        return -1;
    if (sscanf(text, "%u.%u.%u.%u", &a, &b, &c, &d) != 4)
        return -1;
    if (a > 255 || b > 255 || c > 255 || d > 255)
        return -1;
    IP4_ADDR(out, a, b, c, d);
    return 0;
}

/* 直接 fileXioOpen 必须用 IOP 标志（FIO_O_*），不能用 newlib O_RDONLY(=0) */
static int readFileText(const char *path, char *buf, int bufMax, int *outLen)
{
    int fd;
    int n;

    if (!path || !buf || bufMax < 2)
        return -1;

    fd = fileXioOpen(path, FIO_O_RDONLY, 0);
    if (fd < 0) {
        scr_printf("  skip %s (open %d)\n", path, fd);
        return -1;
    }

    n = fileXioRead(fd, buf, bufMax - 1);
    fileXioClose(fd);
    if (n <= 0) {
        scr_printf("  skip %s (read %d)\n", path, n);
        return -1;
    }

    buf[n] = '\0';
    if (outLen)
        *outLen = n;
    scr_printf("  ok   %s (%d bytes)\n", path, n);
    return 0;
}

/* uLE / LaunchELF：IPCONFIG.DAT "A.B.C.D A.B.C.D A.B.C.D" = IP NM GW */
static int tryLoadIpconfigDat(PfsNetIpConfig *cfg)
{
    static const char *paths[] = {
        "mc0:/SYS-CONF/IPCONFIG.DAT",
        "mc1:/SYS-CONF/IPCONFIG.DAT",
        "mc0:SYS-CONF/IPCONFIG.DAT",
        "mc1:SYS-CONF/IPCONFIG.DAT",
    };
    char buf[256];
    unsigned int ip[4], nm[4], gw[4];
    unsigned int i;

    scr_printf("Trying IPCONFIG.DAT...\n");
    for (i = 0; i < sizeof(paths) / sizeof(paths[0]); i++) {
        if (readFileText(paths[i], buf, sizeof(buf), NULL) < 0)
            continue;
        if (sscanf(buf, "%u.%u.%u.%u %u.%u.%u.%u %u.%u.%u.%u",
                   &ip[0], &ip[1], &ip[2], &ip[3],
                   &nm[0], &nm[1], &nm[2], &nm[3],
                   &gw[0], &gw[1], &gw[2], &gw[3]) != 12) {
            scr_printf("  skip %s (bad format)\n", paths[i]);
            continue;
        }
        if (ip[0] > 255 || ip[1] > 255 || ip[2] > 255 || ip[3] > 255 ||
            nm[0] > 255 || nm[1] > 255 || nm[2] > 255 || nm[3] > 255 ||
            gw[0] > 255 || gw[1] > 255 || gw[2] > 255 || gw[3] > 255) {
            scr_printf("  skip %s (bad octets)\n", paths[i]);
            continue;
        }

        IP4_ADDR(&cfg->ip, ip[0], ip[1], ip[2], ip[3]);
        IP4_ADDR(&cfg->nm, nm[0], nm[1], nm[2], nm[3]);
        IP4_ADDR(&cfg->gw, gw[0], gw[1], gw[2], gw[3]);
        cfg->useDhcp = 0;
        cfg->source = PFSNET_IP_SRC_IPCONFIG;
        strncpy(cfg->sourcePath, paths[i], sizeof(cfg->sourcePath) - 1);
        cfg->sourcePath[sizeof(cfg->sourcePath) - 1] = '\0';
        return 0;
    }
    return -1;
}

static void trimInPlace(char *s)
{
    char *start;
    char *end;
    size_t len;

    if (!s)
        return;
    start = s;
    while (*start && isspace((unsigned char)*start))
        start++;
    if (start != s)
        memmove(s, start, strlen(start) + 1);
    len = strlen(s);
    while (len > 0 && isspace((unsigned char)s[len - 1])) {
        s[len - 1] = '\0';
        len--;
    }
    end = strchr(s, '\r');
    if (end)
        *end = '\0';
}

/* OPL conf_network.cfg: key=value */
static int tryLoadOplConf(PfsNetIpConfig *cfg)
{
    static const char *paths[] = {
        "mc0:/OPL/conf_network.cfg",
        "mc1:/OPL/conf_network.cfg",
        "mc0:OPL/conf_network.cfg",
        "mc1:OPL/conf_network.cfg",
    };
    char buf[2048];
    char line[256];
    char key[64];
    char val[128];
    char ipText[32] = "";
    char nmText[32] = "";
    char gwText[32] = "";
    int useDhcp = 1;
    int hasIp = 0, hasNm = 0, hasGw = 0, hasDhcpKey = 0;
    unsigned int i;
    const char *p;
    const char *nl;
    int lineLen;

    scr_printf("Trying OPL conf_network.cfg...\n");
    for (i = 0; i < sizeof(paths) / sizeof(paths[0]); i++) {
        if (readFileText(paths[i], buf, sizeof(buf), NULL) < 0)
            continue;

        useDhcp = 1;
        hasIp = hasNm = hasGw = hasDhcpKey = 0;
        ipText[0] = nmText[0] = gwText[0] = '\0';

        p = buf;
        while (*p) {
            nl = strchr(p, '\n');
            if (nl) {
                lineLen = (int)(nl - p);
                if (lineLen >= (int)sizeof(line))
                    lineLen = (int)sizeof(line) - 1;
                memcpy(line, p, lineLen);
                line[lineLen] = '\0';
                p = nl + 1;
            } else {
                strncpy(line, p, sizeof(line) - 1);
                line[sizeof(line) - 1] = '\0';
                p += strlen(p);
            }

            trimInPlace(line);
            if (line[0] == '\0' || line[0] == '#' || line[0] == ';')
                continue;
            if (sscanf(line, "%63[^=]=%127[^\n]", key, val) != 2)
                continue;
            trimInPlace(key);
            trimInPlace(val);

            if (strcmp(key, "ps2_ip_use_dhcp") == 0) {
                useDhcp = (atoi(val) != 0);
                hasDhcpKey = 1;
            } else if (strcmp(key, "ps2_ip_addr") == 0) {
                strncpy(ipText, val, sizeof(ipText) - 1);
                ipText[sizeof(ipText) - 1] = '\0';
                hasIp = 1;
            } else if (strcmp(key, "ps2_netmask") == 0) {
                strncpy(nmText, val, sizeof(nmText) - 1);
                nmText[sizeof(nmText) - 1] = '\0';
                hasNm = 1;
            } else if (strcmp(key, "ps2_gateway") == 0) {
                strncpy(gwText, val, sizeof(gwText) - 1);
                gwText[sizeof(gwText) - 1] = '\0';
                hasGw = 1;
            }
        }

        /* 至少识别到 DHCP 键或完整静态三项，才视为有效 OPL 配置 */
        if (!hasDhcpKey && !(hasIp && hasNm && hasGw)) {
            scr_printf("  skip %s (no usable keys)\n", paths[i]);
            continue;
        }

        if (useDhcp) {
            cfg->useDhcp = 1;
            IP4_ADDR(&cfg->ip, 0, 0, 0, 0);
            IP4_ADDR(&cfg->nm, 0, 0, 0, 0);
            IP4_ADDR(&cfg->gw, 0, 0, 0, 0);
        } else {
            if (!hasIp || !hasNm || !hasGw) {
                scr_printf("  skip %s (static incomplete)\n", paths[i]);
                continue;
            }
            if (parseIpv4(ipText, &cfg->ip) < 0 ||
                parseIpv4(nmText, &cfg->nm) < 0 ||
                parseIpv4(gwText, &cfg->gw) < 0) {
                scr_printf("  skip %s (bad IP fields)\n", paths[i]);
                continue;
            }
            cfg->useDhcp = 0;
        }

        cfg->source = PFSNET_IP_SRC_OPL;
        strncpy(cfg->sourcePath, paths[i], sizeof(cfg->sourcePath) - 1);
        cfg->sourcePath[sizeof(cfg->sourcePath) - 1] = '\0';
        return 0;
    }
    return -1;
}

void pfsnetIpConfigInitDefault(PfsNetIpConfig *cfg)
{
    if (!cfg)
        return;
    memset(cfg, 0, sizeof(*cfg));
    cfg->useDhcp = 1;
    cfg->source = PFSNET_IP_SRC_DHCP;
    IP4_ADDR(&cfg->ip, 192, 168, 0, 10);
    IP4_ADDR(&cfg->nm, 255, 255, 255, 0);
    IP4_ADDR(&cfg->gw, 192, 168, 0, 1);
    strncpy(cfg->sourcePath, "DHCP", sizeof(cfg->sourcePath) - 1);
}

int pfsnetLoadIpConfig(PfsNetIpConfig *cfg)
{
    PfsNetIpConfig fileCfg;

    if (!cfg)
        return -1;

    /* 优先 DHCP；OPL / uLE 静态仅预载为失败兜底，不在此处关闭 DHCP */
    pfsnetIpConfigInitDefault(cfg);

    memset(&fileCfg, 0, sizeof(fileCfg));
    if (tryLoadOplConf(&fileCfg) == 0) {
        if (!fileCfg.useDhcp) {
            cfg->ip = fileCfg.ip;
            cfg->nm = fileCfg.nm;
            cfg->gw = fileCfg.gw;
            cfg->source = PFSNET_IP_SRC_OPL;
            cfg->useDhcp = 1;
            strncpy(cfg->sourcePath, fileCfg.sourcePath, sizeof(cfg->sourcePath) - 1);
            cfg->sourcePath[sizeof(cfg->sourcePath) - 1] = '\0';
            return 0;
        }
        /* OPL 声明 DHCP：与最高优先一致，再看 uLE 是否有静态兜底 */
    }
    if (tryLoadIpconfigDat(&fileCfg) == 0) {
        cfg->ip = fileCfg.ip;
        cfg->nm = fileCfg.nm;
        cfg->gw = fileCfg.gw;
        cfg->source = PFSNET_IP_SRC_IPCONFIG;
        cfg->useDhcp = 1;
        strncpy(cfg->sourcePath, fileCfg.sourcePath, sizeof(cfg->sourcePath) - 1);
        cfg->sourcePath[sizeof(cfg->sourcePath) - 1] = '\0';
        return 0;
    }

    /* 无记忆卡静态配置：DHCP + InitDefault 中的静态种子作最终兜底 */
    scr_printf("No MC static IP config, default DHCP\n");
    return 0;
}

const char *pfsnetIpSourceName(int source)
{
    switch (source) {
    case PFSNET_IP_SRC_IPCONFIG:
        return "IPCONFIG.DAT";
    case PFSNET_IP_SRC_OPL:
        return "OPL conf_network.cfg";
    case PFSNET_IP_SRC_DHCP:
        return "DHCP";
    case PFSNET_IP_SRC_STATIC:
        return "static fallback";
    default:
        return "unknown";
    }
}
