#include <errno.h>
#include <kernel.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <ps2ip.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <debug.h>

#include "pfsnet.h"

extern void *_gp;

static uint8_t g_serverStack[PFSNET_SERVER_STACK] __attribute__((aligned(16)));
static int g_listenSocket = -1;
static int g_serverThreadId = -1;
static struct PfsNetClient g_client;

static int sendAll(int sock, const void *buf, int len)
{
    const char *ptr = (const char *)buf;
    int sent = 0;
    while (sent < len) {
        int n = send(sock, ptr + sent, len - sent, 0);
        if (n <= 0)
            return -1;
        sent += n;
    }
    return sent;
}

static int recvAll(int sock, void *buf, int len)
{
    char *ptr = (char *)buf;
    int got = 0;
    while (got < len) {
        int n = recv(sock, ptr + got, len - got, 0);
        if (n <= 0)
            return -1;
        got += n;
    }
    return got;
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

    features = PFSNET_FEAT_FILE_OPS | PFSNET_FEAT_DATA_CHANNEL;
    reply[0] = PFSNET_MAGIC;
    reply[1] = PFSNET_PROTOCOL_VERSION;
    reply[2] = features;
    client->verified = 1;
    return pfsnetSendResponse(client, PFSNET_OK, reply, sizeof(reply));
}

static void closeClient(struct PfsNetClient *client)
{
    if (client->socket >= 0) {
        close(client->socket);
        client->socket = -1;
    }
    client->verified = 0;
    client->running = 0;
    pfsnetFsForceUmount();
}

static void clientThread(void *arg)
{
    struct PfsNetClient *client = (struct PfsNetClient *)arg;
    PfsNetPacketHdr hdr;

    client->running = 1;
    client->verified = 0;

    while (client->running) {
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

        if (pfsnetHandleCommand(client, hdr.command, client->rx, hdr.payload_length) < 0)
            break;
    }

    closeClient(client);
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

    while (1) {
        addrLen = sizeof(addr);
        clientSock = accept(g_listenSocket, (struct sockaddr *)&addr, &addrLen);
        if (clientSock < 0)
            continue;

        /* 单客户端：若旧连接仍在，先踢掉 */
        if (g_client.running) {
            close(clientSock);
            continue;
        }

        memset(&g_client, 0, sizeof(g_client));
        g_client.socket = clientSock;
        g_client.peer = addr;
        scr_printf("Client connected.\n");
        if (startClientThread(&g_client) < 0) {
            close(clientSock);
            g_client.socket = -1;
            scr_printf("Client thread failed.\n");
        }
    }
}

int pfsnetStartServer(void)
{
    struct sockaddr_in addr;
    ee_thread_t thread;
    int yes = 1;

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
    thread.initial_priority = 0x3f;
    thread.attr = 0;
    thread.option = 0;

    g_serverThreadId = CreateThread(&thread);
    if (g_serverThreadId < 0) {
        scr_printf("server: CreateThread failed (%d)\n", g_serverThreadId);
        return -1;
    }
    StartThread(g_serverThreadId, NULL);
    scr_printf("server: listening on :%u\n", PFSNET_COMMAND_PORT);
    return 0;
}
