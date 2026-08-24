#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>

#include <fileXio_rpc.h>
#include <io_common.h>
#include <delaythread.h>

#include "pfsnet.h"

#define PFSNET_MAX_BDM_DEVICES 5
#define PFSNET_USB_RETRY 15
#define PFSNET_USB_RETRY_US 200000

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
    if (fd < 0)
        return -1;

    n = fileXioRead(fd, buf, bufMax - 1);
    fileXioClose(fd);
    if (n <= 0)
        return -1;

    buf[n] = '\0';
    if (outLen)
        *outLen = n;
    return 0;
}

static int fileExists(const char *path)
{
    int fd;

    if (!path || !path[0])
        return 0;
    fd = fileXioOpen(path, FIO_O_RDONLY, 0);
    if (fd < 0)
        return 0;
    fileXioClose(fd);
    return 1;
}

static void joinPrefix(char *out, int outMax, const char *prefix, const char *name)
{
    size_t n;

    if (!out || outMax <= 0)
        return;
    out[0] = '\0';
    if (!prefix || !name)
        return;
    n = strlen(prefix);
    if (n > 0 && (prefix[n - 1] == '/' || prefix[n - 1] == ':'))
        snprintf(out, outMax, "%s%s", prefix, name);
    else
        snprintf(out, outMax, "%s/%s", prefix, name);
}

static int prefixHasFile(const char *prefix, const char *name)
{
    char path[96];

    joinPrefix(path, sizeof(path), prefix, name);
    return fileExists(path);
}

static void trimInPlace(char *s)
{
    char *start;
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
    start = strchr(s, '\r');
    if (start)
        *start = '\0';
}

static int parseIpconfigText(const char *buf, PfsNetIpConfig *cfg, const char *path)
{
    unsigned int ip[4], nm[4], gw[4];

    if (!buf || !cfg)
        return -1;
    if (sscanf(buf, "%u.%u.%u.%u %u.%u.%u.%u %u.%u.%u.%u",
               &ip[0], &ip[1], &ip[2], &ip[3],
               &nm[0], &nm[1], &nm[2], &nm[3],
               &gw[0], &gw[1], &gw[2], &gw[3]) != 12)
        return -1;
    if (ip[0] > 255 || ip[1] > 255 || ip[2] > 255 || ip[3] > 255 ||
        nm[0] > 255 || nm[1] > 255 || nm[2] > 255 || nm[3] > 255 ||
        gw[0] > 255 || gw[1] > 255 || gw[2] > 255 || gw[3] > 255)
        return -1;

    IP4_ADDR(&cfg->ip, ip[0], ip[1], ip[2], ip[3]);
    IP4_ADDR(&cfg->nm, nm[0], nm[1], nm[2], nm[3]);
    IP4_ADDR(&cfg->gw, gw[0], gw[1], gw[2], gw[3]);
    cfg->useDhcp = 0;
    cfg->source = PFSNET_IP_SRC_IPCONFIG;
    strncpy(cfg->sourcePath, path ? path : "IPCONFIG.DAT", sizeof(cfg->sourcePath) - 1);
    cfg->sourcePath[sizeof(cfg->sourcePath) - 1] = '\0';
    return 0;
}

static int parseOplNetworkText(const char *buf, PfsNetIpConfig *cfg, const char *path)
{
    char line[256];
    char key[64];
    char val[128];
    char ipText[32] = "";
    char nmText[32] = "";
    char gwText[32] = "";
    int useDhcp = 1;
    int hasIp = 0, hasNm = 0, hasGw = 0, hasDhcpKey = 0;
    const char *p;
    const char *nl;
    int lineLen;

    if (!buf || !cfg)
        return -1;

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

    if (!hasDhcpKey && !(hasIp && hasNm && hasGw))
        return -1;

    /* DHCP 只置标志，真正申请地址走 main 里第一阶段 ethInit/ethWaitLinkAndDhcp。
     * 静态三项若写了，留下作 DHCP 失败兜底，不另开一套 DHCP。 */
    if (hasIp && hasNm && hasGw) {
        if (parseIpv4(ipText, &cfg->ip) < 0 ||
            parseIpv4(nmText, &cfg->nm) < 0 ||
            parseIpv4(gwText, &cfg->gw) < 0)
            return -1;
    } else if (!useDhcp) {
        return -1;
    } else {
        IP4_ADDR(&cfg->ip, 0, 0, 0, 0);
        IP4_ADDR(&cfg->nm, 0, 0, 0, 0);
        IP4_ADDR(&cfg->gw, 0, 0, 0, 0);
    }
    cfg->useDhcp = useDhcp;

    cfg->source = PFSNET_IP_SRC_OPL;
    strncpy(cfg->sourcePath, path ? path : "conf_network.cfg", sizeof(cfg->sourcePath) - 1);
    cfg->sourcePath[sizeof(cfg->sourcePath) - 1] = '\0';
    return 0;
}

static int loadOplNetworkFile(const char *path, PfsNetIpConfig *cfg)
{
    char buf[2048];

    if (readFileText(path, buf, sizeof(buf), NULL) < 0)
        return -1;
    return parseOplNetworkText(buf, cfg, path);
}

static int loadIpconfigFile(const char *path, PfsNetIpConfig *cfg)
{
    char buf[256];

    if (readFileText(path, buf, sizeof(buf), NULL) < 0)
        return -1;
    return parseIpconfigText(buf, cfg, path);
}

static int loadOplFromPrefix(const char *prefix, PfsNetIpConfig *cfg)
{
    char path[96];

    joinPrefix(path, sizeof(path), prefix, "conf_network.cfg");
    return loadOplNetworkFile(path, cfg);
}

static int loadIpconfigFromPrefix(const char *prefix, PfsNetIpConfig *cfg)
{
    char path[96];

    joinPrefix(path, sizeof(path), prefix, "IPCONFIG.DAT");
    return loadIpconfigFile(path, cfg);
}

static int staticIpUsable(const PfsNetIpConfig *cfg)
{
    return cfg && !ip4_addr_isany_val(cfg->ip) && !ip4_addr_isany_val(cfg->nm);
}

/* 记忆卡 uLE 遗留路径：OPL configInit(NULL) 时 legacy 固定为 mc?:SYS-CONF/IPCONFIG.DAT */
static int loadMcLegacyIpconfig(PfsNetIpConfig *cfg)
{
    static const char *paths[] = {
        "mc0:/SYS-CONF/IPCONFIG.DAT",
        "mc1:/SYS-CONF/IPCONFIG.DAT",
        "mc0:SYS-CONF/IPCONFIG.DAT",
        "mc1:SYS-CONF/IPCONFIG.DAT",
    };
    unsigned int i;

    for (i = 0; i < sizeof(paths) / sizeof(paths[0]); i++) {
        if (loadIpconfigFile(paths[i], cfg) == 0)
            return 0;
    }
    return -1;
}

static int findMcPrefix(char *outPrefix, int outMax, const char *filename)
{
    static const char *prefixes[] = {
        "mc0:/OPL/",
        "mc1:/OPL/",
        "mc0:OPL/",
        "mc1:OPL/",
    };
    unsigned int i;

    for (i = 0; i < sizeof(prefixes) / sizeof(prefixes[0]); i++) {
        if (!prefixHasFile(prefixes[i], filename))
            continue;
        strncpy(outPrefix, prefixes[i], outMax - 1);
        outPrefix[outMax - 1] = '\0';
        return 0;
    }
    return -1;
}

static int findBdmPrefixOnce(char *outPrefix, int outMax, const char *filename)
{
    char prefix[40];
    int i;

    for (i = 0; i < PFSNET_MAX_BDM_DEVICES; i++) {
        snprintf(prefix, sizeof(prefix), "mass%d:", i);
        if (prefixHasFile(prefix, filename)) {
            strncpy(outPrefix, prefix, outMax - 1);
            outPrefix[outMax - 1] = '\0';
            return 0;
        }
        snprintf(prefix, sizeof(prefix), "mass%d:OPL/", i);
        if (prefixHasFile(prefix, filename)) {
            strncpy(outPrefix, prefix, outMax - 1);
            outPrefix[outMax - 1] = '\0';
            return 0;
        }
    }
    return -1;
}

static int findBdmPrefix(char *outPrefix, int outMax, const char *filename)
{
    int retry;

    if (findBdmPrefixOnce(outPrefix, outMax, filename) == 0)
        return 0;
    for (retry = 0; retry < PFSNET_USB_RETRY; retry++) {
        DelayThread(PFSNET_USB_RETRY_US);
        if (findBdmPrefixOnce(outPrefix, outMax, filename) == 0)
            return 0;
    }
    return -1;
}

static int readHddPartitionName(char *outName, int outMax)
{
    char buf[512];
    char line[128];
    char key[64];
    char val[64];
    const char *p;
    const char *nl;
    int lineLen;

    if (readFileText("pfs0:OPL/conf_hdd.cfg", buf, sizeof(buf), NULL) < 0 &&
        readFileText("pfs0:/OPL/conf_hdd.cfg", buf, sizeof(buf), NULL) < 0)
        return -1;

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
        if (sscanf(line, "%63[^=]=%63[^\n]", key, val) != 2)
            continue;
        trimInPlace(key);
        trimInPlace(val);
        if (strcmp(key, "hdd_partition") != 0)
            continue;
        strncpy(outName, val, outMax - 1);
        outName[outMax - 1] = '\0';
        return 0;
    }
    return -1;
}

/*
 * OPL hddLoadSupportModules + checkLoadConfigHDD：
 * 读 __common/OPL/conf_hdd.cfg 得到分区（默认 +OPL），挂到 pfs0:。
 * 分区名以 + 开头时配置在根目录；否则在 pfs0:OPL/。
 */
static int findHddPrefix(char *outPrefix, int outMax, const char *filename)
{
    char partName[40];
    char device[48];
    const char *prefix;
    int mounted = 0;

    fileXioUmount(PFSNET_MOUNT_POINT);

    strncpy(partName, "+OPL", sizeof(partName) - 1);
    partName[sizeof(partName) - 1] = '\0';
    if (fileXioMount(PFSNET_MOUNT_POINT, "hdd0:__common", FILEXIO_MOUNTFLAG_READONLY) >= 0) {
        readHddPartitionName(partName, sizeof(partName));
        fileXioUmount(PFSNET_MOUNT_POINT);
    }

    if (strncmp(partName, "hdd0:", 5) == 0)
        strncpy(device, partName, sizeof(device) - 1);
    else
        snprintf(device, sizeof(device), "hdd0:%s", partName);
    device[sizeof(device) - 1] = '\0';

    /* 362053534 checkLoadConfigHDD：hddLoadSupportModules 失败则 100ms 重试，最多 2 秒 */
    {
        int retry;

        for (retry = 0; retry < 20 && !mounted; retry++) {
            if (fileXioMount(PFSNET_MOUNT_POINT, device, FILEXIO_MOUNTFLAG_READONLY) >= 0)
                mounted = 1;
            else if (strcmp(device, "hdd0:+OPL") != 0 &&
                     fileXioMount(PFSNET_MOUNT_POINT, "hdd0:+OPL", FILEXIO_MOUNTFLAG_READONLY) >= 0) {
                strncpy(device, "hdd0:+OPL", sizeof(device) - 1);
                mounted = 1;
            } else if (fileXioMount(PFSNET_MOUNT_POINT, "hdd0:__common", FILEXIO_MOUNTFLAG_READONLY) >= 0) {
                strncpy(device, "hdd0:__common", sizeof(device) - 1);
                mounted = 1;
            }
            if (!mounted)
                DelayThread(100000);
        }
    }
    if (!mounted)
        return -1;

    /* hdd0:+OPL → device[5]=='+'；其它分区用 pfs0:OPL/ */
    if (strlen(device) > 5 && device[5] == '+')
        prefix = "pfs0:";
    else
        prefix = "pfs0:OPL/";

    if (!prefixHasFile(prefix, filename) && strcmp(prefix, "pfs0:") == 0
        && prefixHasFile("pfs0:OPL/", filename))
        prefix = "pfs0:OPL/";

    if (!prefixHasFile(prefix, filename)) {
        fileXioUmount(PFSNET_MOUNT_POINT);
        return -1;
    }

    strncpy(outPrefix, prefix, outMax - 1);
    outPrefix[outMax - 1] = '\0';
    return 0;
}

static void umountHddConfig(const char *prefix)
{
    if (prefix && strncmp(prefix, "pfs0:", 5) == 0)
        fileXioUmount(PFSNET_MOUNT_POINT);
}

static int bootIsMass(const char *bootPath)
{
    return bootPath && !strncmp(bootPath, "mass", 4) &&
           (bootPath[4] == ':' || bootPath[5] == ':');
}

static int bootIsHdd(const char *bootPath)
{
    if (!bootPath)
        return 0;
    if (!strncmp(bootPath, "hdd", 3) && (bootPath[3] == ':' || bootPath[4] == ':'))
        return 1;
    if (!strncmp(bootPath, "pfs", 3) && (bootPath[3] == ':' || bootPath[4] == ':'))
        return 1;
    return 0;
}

static int findPrefixForFile(char *outPrefix, int outMax, const char *bootPath, const char *filename)
{
    int found = 0;
    int usbTried = 0;
    int hddTried = 0;

    if (findMcPrefix(outPrefix, outMax, filename) == 0)
        return 0;

    if (bootIsMass(bootPath)) {
        usbTried = 1;
        if (findBdmPrefix(outPrefix, outMax, filename) == 0)
            found = 1;
    } else if (bootIsHdd(bootPath)) {
        hddTried = 1;
        if (findHddPrefix(outPrefix, outMax, filename) == 0)
            found = 1;
    }

    if (!found && !usbTried && findBdmPrefix(outPrefix, outMax, filename) == 0)
        found = 1;
    if (!found && !hddTried && findHddPrefix(outPrefix, outMax, filename) == 0)
        found = 1;
    return found ? 0 : -1;
}

void pfsnetIpConfigInitDefault(PfsNetIpConfig *cfg)
{
    if (!cfg)
        return;
    memset(cfg, 0, sizeof(*cfg));
    cfg->useDhcp = 1;
    cfg->source = PFSNET_IP_SRC_DHCP;
    strncpy(cfg->sourcePath, "DHCP", sizeof(cfg->sourcePath) - 1);
}

int pfsnetLoadOplStatic(PfsNetIpConfig *cfg, const char *bootPath)
{
    PfsNetIpConfig fileCfg;
    char prefix[40];

    if (!cfg)
        return -1;
    memset(&fileCfg, 0, sizeof(fileCfg));
    if (findPrefixForFile(prefix, sizeof(prefix), bootPath, "conf_network.cfg") < 0)
        return -1;
    if (loadOplFromPrefix(prefix, &fileCfg) < 0) {
        umountHddConfig(prefix);
        return -1;
    }
    umountHddConfig(prefix);
    if (!staticIpUsable(&fileCfg))
        return -1;
    fileCfg.useDhcp = 0;
    *cfg = fileCfg;
    return 0;
}

int pfsnetLoadUleStatic(PfsNetIpConfig *cfg, const char *bootPath)
{
    PfsNetIpConfig fileCfg;
    char prefix[40];

    if (!cfg)
        return -1;
    memset(&fileCfg, 0, sizeof(fileCfg));
    if (loadMcLegacyIpconfig(&fileCfg) == 0 && staticIpUsable(&fileCfg)) {
        *cfg = fileCfg;
        return 0;
    }
    if (findPrefixForFile(prefix, sizeof(prefix), bootPath, "IPCONFIG.DAT") < 0)
        return -1;
    if (loadIpconfigFromPrefix(prefix, &fileCfg) < 0) {
        umountHddConfig(prefix);
        return -1;
    }
    umountHddConfig(prefix);
    if (!staticIpUsable(&fileCfg))
        return -1;
    *cfg = fileCfg;
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
