#ifndef PFSNET_H
#define PFSNET_H

#include <stdint.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <ps2ip.h>

#include "protocol.h"

#define PFSNET_MOUNT_POINT "pfs0:"
#define PFSNET_RECV_MAX 131072
#define PFSNET_SEND_MAX 131072
#define PFSNET_CLIENT_STACK 0x4000
#define PFSNET_SERVER_STACK 0x2000

struct PfsNetClient;

/* 网络配置：OPL 静态则直接采用；否则 DHCP，失败再 uLE/默认静态兜底 */
enum PfsNetIpSource {
    PFSNET_IP_SRC_IPCONFIG = 1, /* uLE: mc?:/SYS-CONF/IPCONFIG.DAT */
    PFSNET_IP_SRC_OPL = 2,      /* OPL: mc?:/OPL/conf_network.cfg */
    PFSNET_IP_SRC_DHCP = 3,
    PFSNET_IP_SRC_STATIC = 4
};

typedef struct PfsNetIpConfig {
    int useDhcp;
    int source;
    struct ip4_addr ip;
    struct ip4_addr nm;
    struct ip4_addr gw;
    char sourcePath[64];
} PfsNetIpConfig;

void pfsnetIpConfigInitDefault(PfsNetIpConfig *cfg);
int pfsnetLoadIpConfig(PfsNetIpConfig *cfg);
const char *pfsnetIpSourceName(int source);

int ethInit(int use_dhcp, const struct ip4_addr *ip, const struct ip4_addr *nm, const struct ip4_addr *gw);
int ethApplyIPConfig(int use_dhcp, const struct ip4_addr *ip, const struct ip4_addr *nm, const struct ip4_addr *gw);
int ethWaitLinkAndDhcp(int use_dhcp);
int ethLinkIsUp(void);
void ethGetIpDisplay(char *out, int outLen);
int ethReadCurrentConfig(struct ip4_addr *ip, struct ip4_addr *nm, struct ip4_addr *gw);

int pfsnetFsInit(void);
void pfsnetFsForceUmount(void);
int pfsnetIsMounted(void);
const char *pfsnetMountedPartition(void);

int pfsnetHandleCommand(struct PfsNetClient *client, uint32_t command, const uint8_t *payload, uint32_t payloadLen);

int pfsnetStartServer(void);

/* 从载荷读取 length-prefixed UTF-8 字符串；成功返回消耗字节数，失败 <0 */
int pfsnetReadString(const uint8_t *payload, uint32_t payloadLen, uint32_t offset, char *out, int outMax, uint32_t *consumed);
int pfsnetWriteString(uint8_t *dest, int destMax, const char *text);

int pfsnetSendResponse(struct PfsNetClient *client, int result, const void *payload, uint32_t payloadLen);

struct PfsNetClient {
    uint8_t rx[PFSNET_RECV_MAX] __attribute__((aligned(64)));
    uint8_t tx[PFSNET_SEND_MAX] __attribute__((aligned(64)));
    uint8_t stack[PFSNET_CLIENT_STACK] __attribute__((aligned(16)));
    struct sockaddr_in peer;
    int socket;
    int verified;
    int threadId;
    int running;
};

#endif /* PFSNET_H */
