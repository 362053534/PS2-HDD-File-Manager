#include <errno.h>
#include <kernel.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <timer.h>
#include <ps2ip.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <debug.h>
#include <delaythread.h>

#include "pfsnet.h"

static volatile int g_trafficDepth;
static u64 g_trafficIdleSince;

void pfsnetTrafficBegin(void)
{
    int old = DIntr();

    g_trafficDepth++;
    if (old)
        EIntr();
}

void pfsnetTrafficEnd(void)
{
    int old = DIntr();

    if (g_trafficDepth > 0)
        g_trafficDepth--;
    if (g_trafficDepth == 0)
        g_trafficIdleSince = GetTimerSystemTime();
    if (old)
        EIntr();
}

int pfsnetPadPollAllowed(void)
{
    u64 idleNeed = 2ULL * (u64)kBUSCLK;

    if (g_trafficDepth > 0)
        return 0;
    if (g_trafficIdleSince == 0)
        return 1;
    return (GetTimerSystemTime() - g_trafficIdleSince) >= idleNeed;
}

extern void *_gp;

static uint8_t g_serverStack[PFSNET_SERVER_STACK] __attribute__((aligned(16)));
static int g_listenSocket = -1;
static int g_serverThreadId = -1;
static volatile int g_serverStop;
static volatile int g_clientStop;
static struct PfsNetClient g_client;

static int sendAll(int sock, const void *buf, int len)
{
    const char *ptr = (const char *)buf;
    int sent = 0;
    int ok = 1;

    pfsnetTrafficBegin();
    while (sent < len) {
        int n;
        if (g_clientStop || g_serverStop) {
            ok = 0;
            break;
        }
        n = send(sock, ptr + sent, len - sent, 0);
        if (n <= 0) {
            ok = 0;
            break;
        }
        sent += n;
    }
    pfsnetTrafficEnd();
    return ok ? sent : -1;
}

static int recvAll(int sock, void *buf, int len)
{
    char *ptr = (char *)buf;
    int got = 0;
    int ok = 1;

    pfsnetTrafficBegin();
    while (got < len) {
        int n;
        if (g_clientStop || g_serverStop) {
            ok = 0;
            break;
        }
        n = recv(sock, ptr + got, len - got, 0);
        if (n <= 0) {
            ok = 0;
            break;
        }
        got += n;
    }
    pfsnetTrafficEnd();
    return ok ? got : -1;
}

int pfsnetSendResponse(struct PfsNetClient *client, int result, const void *payload, uint32_t payloadLen)
{
    PfsNetPacketHdr hdr;

    if (payloadLen > PFSNET_SEND_MAX)
        return -1;

    hdr.command = PFSNET_CMD_HELLO; /* 响应不依赖 command 字段；填占位 */
    hdr.payload_length = payloadLen;
    hdr.result = result;

    if (sendAll(client->socket, &hdr, sizeof(hdr)) < 0)
        return -1;
    if (payloadLen > 0 && payload != NULL) {
        if (sendAll(client->socket, payload, (int)payloadLen) < 0)
            return -1;
    }
    return 0;
}

static int handleHello(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    uint32_t magic = 0, version = 0;
    uint32_t reply[3];
    uint32_t features;

    if (payloadLen < 8)
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);

    memcpy(&magic, payload, 4);
    memcpy(&version, payload + 4, 4);

    if (magic != PFSNET_MAGIC)
        return pfsnetSendResponse(client, PFSNET_ERR_MAGIC, NULL, 0);
    if (version != PFSNET_PROTOCOL_VERSION)
        return pfsnetSendResponse(client, PFSNET_ERR_VERSION, NULL, 0);

    features = PFSNET_FEAT_FILE_OPS | PFSNET_FEAT_DATA_CHANNEL
        | PFSNET_FEAT_APA_SECTOR | PFSNET_FEAT_APA_MUTATE
        | PFSNET_FEAT_STREAM_RING;
    reply[0] = PFSNET_MAGIC;
    reply[1] = PFSNET_PROTOCOL_VERSION;
    reply[2] = features;
    client->verified = 1;
    return pfsnetSendResponse(client, PFSNET_OK, reply, sizeof(reply));
}

static void closeClient(struct PfsNetClient *client)
{
    int fd = client->socket;
    client->socket = -1;
    if (fd >= 0)
        close(fd);
    client->verified = 0;
    if (g_serverStop)
        pfsnetFsForceUmount();
    else
        pfsnetFsClientCleanup();
}

static void clientThread(void *arg)
{
    struct PfsNetClient *client = (struct PfsNetClient *)arg;
    PfsNetPacketHdr hdr;

    client->running = 1;
    client->verified = 0;

    while (client->running && !g_clientStop && !g_serverStop) {
        if (recvAll(client->socket, &hdr, sizeof(hdr)) < 0)
            break;
        if (hdr.payload_length > PFSNET_RECV_MAX)
            break;
        if (hdr.payload_length > 0) {
            if (recvAll(client->socket, client->rx, (int)hdr.payload_length) < 0)
                break;
        }

        if (!client->verified) {
            if (hdr.command != PFSNET_CMD_HELLO) {
                pfsnetSendResponse(client, PFSNET_ERR_MAGIC, NULL, 0);
                break;
            }
            if (handleHello(client, client->rx, hdr.payload_length) < 0)
                break;
            continue;
        }

        {
            int cmdRc;
            pfsnetTrafficBegin();
            cmdRc = pfsnetHandleCommand(client, hdr.command, client->rx, hdr.payload_length);
            pfsnetTrafficEnd();
            if (cmdRc < 0)
                break;
        }
    }

    closeClient(client);
    client->threadId = -1;
    client->running = 0;
    ExitDeleteThread();
}

static int startClientThread(struct PfsNetClient *client)
{
    ee_thread_t thread;
    int tid;

    memset(&thread, 0, sizeof(thread));
    thread.func = &clientThread;
    thread.stack = client->stack;
    thread.stack_size = sizeof(client->stack);
    thread.gp_reg = &_gp;
    /* 高于 ioWorker(0x42) 与 NETMAN_RxThread(0x59)：
     * clientThread 在写盘期间 99.5% 时间睡眠在 SifCallRpc(WaitSema) 上，
     * 略高于 ioWorker 可保证每写完一槽 512KiB 立即在 <0.3ms 内向 IOP 下发下一槽 RPC，消除硬盘空转间隙。 */
    thread.initial_priority = 0x40;
    thread.attr = 0;
    thread.option = 0;

    tid = CreateThread(&thread);
    if (tid < 0)
        return tid;
    client->threadId = tid;
    StartThread(tid, client);
    return 0;
}

static void serverThread(void *arg)
{
    struct sockaddr_in addr;
    socklen_t addrLen;
    int clientSock;

    (void)arg;

    while (!g_serverStop) {
        addrLen = sizeof(addr);
        clientSock = accept(g_listenSocket, (struct sockaddr *)&addr, &addrLen);
        if (g_serverStop) {
            if (clientSock >= 0)
                close(clientSock);
            break;
        }
        if (clientSock < 0) {
            /* close listen 后 accept 会立刻失败；必须让出 CPU，否则 0x3f 忙等饿死退出路径 */
            DelayThread(20000);
            continue;
        }

        /* 单客户端：若旧连接仍在，通过协作标志 + pfsnetWakeMboxWaiter 唤醒旧线程自行 close/umount 退出；
         * 切勿跨线程对正阻塞在 recv 的 socket 调用 shutdown/close（LWIP_NETCONN_FULLDUPLEX=0），
         * 也切勿在旧线程完成 pfsnetFsForceUmount 前 memset 栈区！ */
        if (g_client.running) {
            int waitTicks;
            g_clientStop = 1;
            pfsnetFsAbortActiveSocket();
            for (waitTicks = 0; waitTicks < 600 && g_client.running; waitTicks++) {
                if (g_client.threadId >= 0)
                    pfsnetWakeMboxWaiter(g_client.threadId);
                DelayThread(50000);
            }
            if (g_client.running) {
                close(clientSock);
                continue;
            }
        }

        g_clientStop = 0;
        g_client.socket = clientSock;
        g_client.peer = addr;
        g_client.verified = 0;
        g_client.threadId = -1;
        g_client.running = 1;
        scr_printf("Client connected.\n");
        if (startClientThread(&g_client) < 0) {
            close(clientSock);
            g_client.socket = -1;
            g_client.running = 0;
            scr_printf("Client thread failed.\n");
        }
    }
    ExitDeleteThread();
}

int pfsnetStartServer(void)
{
    struct sockaddr_in addr;
    ee_thread_t thread;
    int yes = 1;

    g_serverThreadId = -1;
    g_serverStop = 0;

    g_listenSocket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (g_listenSocket < 0) {
        scr_printf("server: socket failed (%d)\n", g_listenSocket);
        return -1;
    }

    setsockopt(g_listenSocket, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));

    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_ANY);
    addr.sin_port = htons(PFSNET_COMMAND_PORT);

    if (bind(g_listenSocket, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        scr_printf("server: bind :%u failed\n", PFSNET_COMMAND_PORT);
        close(g_listenSocket);
        g_listenSocket = -1;
        return -1;
    }
    if (listen(g_listenSocket, 1) < 0) {
        scr_printf("server: listen failed\n");
        close(g_listenSocket);
        g_listenSocket = -1;
        return -1;
    }

    memset(&thread, 0, sizeof(thread));
    thread.func = &serverThread;
    thread.stack = g_serverStack;
    thread.stack_size = sizeof(g_serverStack);
    thread.gp_reg = &_gp;
    /* 高于 clientThread(0x40) 与 ioWorker(0x42)，保证控制连接接入与重连抢占响应 */
    thread.initial_priority = 0x3f;
    thread.attr = 0;
    thread.option = 0;

    g_serverThreadId = CreateThread(&thread);
    if (g_serverThreadId < 0) {
        scr_printf("server: CreateThread failed (%d)\n", g_serverThreadId);
        return -1;
    }
    StartThread(g_serverThreadId, NULL);
    return 0;
}

void pfsnetStopServer(void)
{
    int listenFd;

    /* 协作式停服：通过标志位 + pfsnetWakeMboxWaiter 唤醒 clientThread / ioWorker 自行退出 */
    g_serverStop = 1;
    g_clientStop = 1;
    pfsnetFsAbortActiveSocket();
    if (g_client.running && g_client.threadId >= 0)
        pfsnetWakeMboxWaiter(g_client.threadId);

    listenFd = g_listenSocket;
    g_listenSocket = -1;
    if (listenFd >= 0) {
        shutdown(listenFd, SHUT_RDWR);
        close(listenFd);
    }
}
