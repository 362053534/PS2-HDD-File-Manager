#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <malloc.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <kernel.h>
#include <debug.h>

#include <fileXio_rpc.h>
#include <hdd-ioctl.h>
#include <io_common.h>
#include <iox_stat.h>

#include "pfsnet.h"

#define PFSNET_XFER_NONE 0
#define PFSNET_XFER_PUT 1
#define PFSNET_XFER_GET 2
/* 512KiB：对齐 HDL 大批次，显著减少 fileXio SIF 往返；多槽环形缓冲吸收硬盘寻道/元数据刷新峰值 */
#define PFSNET_XFER_BUF (512 * 1024)
#define PFSNET_RING_MAX_BANKS 16 /* 16 * 512KiB = 8MiB EE 环形缓冲 */
#define PFSNET_IO_STACK 0x2000

#define PFSNET_IO_MODE_SINGLE 0
#define PFSNET_IO_MODE_PUT_RING 1

extern void *_gp;

static char g_mountedPartition[APA_IDMAX];
static int g_mounted;
/* 挂载前缓存：挂载后常无法再 open hdd0:分区，HIOCGETSIZE 会失败导致总容量为 0 */
static uint64_t g_mountedTotalBytes;

/* 进行中的 Put/Get（控制线程内同步收发载荷，END/ABORT 再提交） */
static struct {
    int kind;
    int fileFd;
    int dataSock;
    uint32_t flags;
    uint64_t totalBytes;
    char tempPath[768];
    char finalPath[768];
} g_xfer;

/* 网络闪断后保留的未完成 Put 临时文件状态，供自动重试时零损耗断点续传 */
static struct {
    int valid;
    uint32_t flags;
    uint64_t totalBytes;
    uint64_t gotBytes;
    char partition[APA_IDMAX];
    char tempPath[768];
    char finalPath[768];
} g_partialPut;

/* [0]/[1] 乒乓及多槽环形缓冲池：堆上分配，避免 BSS 拖慢 crt0 清零 / 撑大 ELF MemSiz */
static uint8_t *g_xferBuf[2];
static uint8_t *g_ringBase;
static int g_ringBanks;
static volatile int g_ringBankLen[PFSNET_RING_MAX_BANKS];
static int g_putFreeSem = -1;
static int g_putFillSem = -1;
static volatile int g_putStop;
static volatile int g_putUserAbort;
static uint64_t g_putTotalBytes;
static volatile int g_ioMode;
static uint32_t g_tempSeq;

/* 网络收发工作线程（主线程专跑 fileXio，避免并发 RPC） */
static uint8_t g_ioStack[PFSNET_IO_STACK] __attribute__((aligned(16)));
static int g_ioSemStart = -1;
static int g_ioSemDone = -1;
static int g_ioTid = -1;
static int g_ioAsync;
static int g_ioSock;
static void *g_ioBuf;
static int g_ioLen;
static int g_ioResult;
static int g_ioIsSend;
static int32_t g_lastPutErr;

static void clearXferState(void)
{
    g_xfer.kind = PFSNET_XFER_NONE;
    g_xfer.fileFd = -1;
    g_xfer.dataSock = -1;
    g_xfer.flags = 0;
    g_xfer.totalBytes = 0;
    g_xfer.tempPath[0] = '\0';
    g_xfer.finalPath[0] = '\0';
}

static void clearPartialPut(void)
{
    if (g_partialPut.valid) {
        if (g_partialPut.tempPath[0] != '\0') {
            fileXioRemove(g_partialPut.tempPath);
            if (g_mounted)
                fileXioSync(PFSNET_MOUNT_POINT, FILEXIO_MOUNTFLAG_NORMAL);
        }
    }
    g_partialPut.valid = 0;
    g_partialPut.flags = 0;
    g_partialPut.totalBytes = 0;
    g_partialPut.gotBytes = 0;
    g_partialPut.partition[0] = '\0';
    g_partialPut.tempPath[0] = '\0';
    g_partialPut.finalPath[0] = '\0';
}

static void savePartialPut(uint64_t totalBytes, uint64_t gotBytes, uint32_t flags)
{
    g_partialPut.valid = 1;
    g_partialPut.flags = flags;
    g_partialPut.totalBytes = totalBytes;
    g_partialPut.gotBytes = gotBytes;
    strncpy(g_partialPut.partition, g_mountedPartition, sizeof(g_partialPut.partition) - 1);
    g_partialPut.partition[sizeof(g_partialPut.partition) - 1] = '\0';
    strncpy(g_partialPut.tempPath, g_xfer.tempPath, sizeof(g_partialPut.tempPath) - 1);
    g_partialPut.tempPath[sizeof(g_partialPut.tempPath) - 1] = '\0';
    strncpy(g_partialPut.finalPath, g_xfer.finalPath, sizeof(g_partialPut.finalPath) - 1);
    g_partialPut.finalPath[sizeof(g_partialPut.finalPath) - 1] = '\0';
}

static void abortXferCleanup(void)
{
    g_putStop = 1;
    if (g_xfer.dataSock >= 0) {
        close(g_xfer.dataSock);
        g_xfer.dataSock = -1;
    }
    if (g_xfer.fileFd >= 0) {
        fileXioClose(g_xfer.fileFd);
        g_xfer.fileFd = -1;
    }
    if (g_xfer.kind == PFSNET_XFER_PUT && g_xfer.tempPath[0] != '\0') {
        fileXioRemove(g_xfer.tempPath);
        if (g_mounted)
            fileXioSync(PFSNET_MOUNT_POINT, FILEXIO_MOUNTFLAG_NORMAL);
    }
    clearXferState();
}

static void ioWorker(void *arg);
static int initIoWorker(void);
static uint64_t getPartitionTotalBytes(const char *partition);

int pfsnetFsInit(void)
{
    g_mounted = 0;
    g_mountedPartition[0] = '\0';
    g_mountedTotalBytes = 0;
    clearXferState();
    memset(&g_partialPut, 0, sizeof(g_partialPut));

    if (!g_ringBase) {
        static const int bankTry[] = { PFSNET_RING_MAX_BANKS, 8, 4, 2 };
        unsigned int i;
        for (i = 0; i < sizeof(bankTry) / sizeof(bankTry[0]); i++) {
            size_t total = (size_t)bankTry[i] * (size_t)PFSNET_XFER_BUF;
            g_ringBase = (uint8_t *)memalign(64, total);
            if (g_ringBase != NULL) {
                g_ringBanks = bankTry[i];
                break;
            }
        }
    }
    if (!g_ringBase || g_ringBanks < 2) {
        scr_printf("FATAL: xfer buf alloc failed\n");
        return -1;
    }
    g_xferBuf[0] = g_ringBase;
    g_xferBuf[1] = g_ringBase + PFSNET_XFER_BUF;

    initIoWorker();
    return 0;
}

void pfsnetFsAbortActiveTransfer(int userAbort)
{
    int wasStopped;
    int old = DIntr();
    if (userAbort)
        g_putUserAbort = 1;
    wasStopped = g_putStop;
    g_putStop = 1;
    if (old)
        EIntr();

    if (!wasStopped) {
        if (g_putFreeSem >= 0)
            SignalSema(g_putFreeSem);
        if (g_putFillSem >= 0)
            SignalSema(g_putFillSem);
        if (g_ioTid >= 0)
            pfsnetWakeMboxWaiter(g_ioTid);
    }
}

void pfsnetFsAbortActiveSocket(void)
{
    pfsnetFsAbortActiveTransfer(0);
}

void pfsnetFsClientCleanup(void)
{
    abortXferCleanup();
    if (g_mounted)
        fileXioSync(PFSNET_MOUNT_POINT, FILEXIO_MOUNTFLAG_NORMAL);
}

void pfsnetFsForceUmount(void)
{
    abortXferCleanup();
    clearPartialPut();
    if (g_mounted) {
        fileXioUmount(PFSNET_MOUNT_POINT);
        g_mounted = 0;
        g_mountedPartition[0] = '\0';
        g_mountedTotalBytes = 0;
    }
}

int pfsnetIsMounted(void)
{
    return g_mounted;
}

const char *pfsnetMountedPartition(void)
{
    return g_mounted ? g_mountedPartition : "";
}

static int isSystemPartitionName(const char *name)
{
    static const char *protectedNames[] = {
        "__mbr", "__net", "__system", "__sysconf", "__common", "__boot", NULL};
    int i;
    for (i = 0; protectedNames[i] != NULL; i++) {
        if (strcmp(name, protectedNames[i]) == 0)
            return 1;
    }
    return 0;
}

static int mapErrno(int err)
{
    if (err >= 0)
        return PFSNET_OK;
    /* fileXio 返回负 errno；不同运行时数值可能不一致，做宽松映射 */
    if (err == -2 || err == -ENOENT)
        return PFSNET_ERR_NOENT;
    if (err == -17 || err == -EEXIST)
        return PFSNET_ERR_EXIST;
    if (err == -28 || err == -ENOSPC)
        return PFSNET_ERR_NOSPC;
    if (err == -16 || err == -EBUSY)
        return PFSNET_ERR_BUSY;
    if (err == -22 || err == -EINVAL)
        return PFSNET_ERR_INVAL;
    /* APA 拒绝删除 __ 前缀分区时返回 -EACCES(13)；勿落到笼统 ERR_IO(-2) */
    if (err == -13 || err == -EACCES)
        return PFSNET_ERR_INVAL;
    return PFSNET_ERR_IO;
}

int pfsnetReadString(const uint8_t *payload, uint32_t payloadLen, uint32_t offset, char *out, int outMax, uint32_t *consumed)
{
    uint16_t len;
    if (offset + 2 > payloadLen || outMax <= 0)
        return PFSNET_ERR_INVAL;
    len = (uint16_t)(payload[offset] | (payload[offset + 1] << 8));
    if (offset + 2 + len > payloadLen)
        return PFSNET_ERR_INVAL;
    if (len >= (uint16_t)outMax)
        return PFSNET_ERR_INVAL;
    memcpy(out, payload + offset + 2, len);
    out[len] = '\0';
    if (consumed)
        *consumed = 2 + len;
    return PFSNET_OK;
}

int pfsnetWriteString(uint8_t *dest, int destMax, const char *text)
{
    size_t len;
    if (!text)
        text = "";
    len = strlen(text);
    if (len > 0xFFFF || destMax < (int)(2 + len))
        return -1;
    dest[0] = (uint8_t)(len & 0xFF);
    dest[1] = (uint8_t)((len >> 8) & 0xFF);
    memcpy(dest + 2, text, len);
    return (int)(2 + len);
}

static int buildDevicePath(char *out, int outMax, const char *partition)
{
    return snprintf(out, outMax, "hdd0:%s", partition);
}

static int joinPfsPath(char *out, int outMax, const char *dir, const char *name)
{
    if (!dir || dir[0] == '\0' || strcmp(dir, "/") == 0)
        return snprintf(out, outMax, PFSNET_MOUNT_POINT "/%s", name);
    if (dir[0] == '/')
        return snprintf(out, outMax, PFSNET_MOUNT_POINT "%s/%s", dir, name);
    return snprintf(out, outMax, PFSNET_MOUNT_POINT "/%s/%s", dir, name);
}

static int openPfsPath(char *out, int outMax, const char *path)
{
    if (!path || path[0] == '\0' || strcmp(path, "/") == 0)
        return snprintf(out, outMax, PFSNET_MOUNT_POINT "/");
    if (path[0] == '/')
        return snprintf(out, outMax, PFSNET_MOUNT_POINT "%s", path);
    return snprintf(out, outMax, PFSNET_MOUNT_POINT "/%s", path);
}

static int doMount(const char *partition)
{
    char device[64];
    int result;

    if (g_xfer.kind != PFSNET_XFER_NONE)
        return PFSNET_ERR_BUSY;

    if (g_mounted) {
        if (strcmp(g_mountedPartition, partition) == 0)
            return PFSNET_OK;
        clearPartialPut();
        fileXioUmount(PFSNET_MOUNT_POINT);
        g_mounted = 0;
        g_mountedPartition[0] = '\0';
        g_mountedTotalBytes = 0;
    }

    /* 须在 mount 前读取 APA 长度；挂载后 open 同一分区常失败 */
    g_mountedTotalBytes = getPartitionTotalBytes(partition);

    buildDevicePath(device, sizeof(device), partition);
    result = fileXioMount(PFSNET_MOUNT_POINT, device, FILEXIO_MOUNTFLAG_NORMAL);
    if (result < 0) {
        g_mountedTotalBytes = 0;
        return mapErrno(result);
    }

    strncpy(g_mountedPartition, partition, sizeof(g_mountedPartition) - 1);
    g_mountedPartition[sizeof(g_mountedPartition) - 1] = '\0';
    g_mounted = 1;
    return PFSNET_OK;
}

static int doUmount(void)
{
    if (g_xfer.kind != PFSNET_XFER_NONE)
        return PFSNET_ERR_BUSY;
    if (!g_mounted)
        return PFSNET_OK;
    clearPartialPut();
    fileXioUmount(PFSNET_MOUNT_POINT);
    g_mounted = 0;
    g_mountedPartition[0] = '\0';
    g_mountedTotalBytes = 0;
    return PFSNET_OK;
}

static int handleListPartitions(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    uint32_t flags = 0;
    int fd;
    iox_dirent_t dirent;
    uint8_t *out = client->tx;
    int used = 4;
    uint32_t count = 0;

    if (payloadLen >= 4)
        flags = payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24);

    fd = fileXioDopen("hdd0:");
    if (fd < 0)
        return pfsnetSendResponse(client, mapErrno(fd), NULL, 0);

    while (fileXioDread(fd, &dirent) > 0) {
        uint8_t partFlags;
        int wrote;
        uint64_t totalBytes;
        int isPfs;
        int isSystem;

        if (dirent.stat.attr & APA_FLAG_SUB)
            continue;
        if (dirent.name[0] == '\0' || strcmp(dirent.name, ".") == 0 || strcmp(dirent.name, "..") == 0)
            continue;

        isPfs = (dirent.stat.mode == APA_TYPE_PFS);
        isSystem = isSystemPartitionName(dirent.name);
        if (!isPfs)
            continue;
        if (isSystem && (flags & 1u) == 0)
            continue;

        /* name + flags + total_bytes */
        if (used + 2 + (int)strlen(dirent.name) + 1 + 8 > PFSNET_SEND_MAX)
            break;

        wrote = pfsnetWriteString(out + used, PFSNET_SEND_MAX - used, dirent.name);
        if (wrote < 0)
            break;
        used += wrote;
        partFlags = (uint8_t)((isSystem ? 1 : 0) | (isPfs ? 2 : 0));
        out[used++] = partFlags;
        /* APA stat.size 是主分区长度（512B 扇区）；含子分区的精确总量在 GET_PFS_FREE 再算 */
        totalBytes = ((uint64_t)dirent.stat.size) * 512ull;
        memcpy(out + used, &totalBytes, 8);
        used += 8;
        count++;
    }
    fileXioDclose(fd);

    memcpy(out, &count, 4);
    return pfsnetSendResponse(client, PFSNET_OK, out, (uint32_t)used);
}

/* 打开 hdd0:分区，累加主分区 + 子分区扇区数得到字节总量（与 libhdd 一致） */
static uint64_t getPartitionTotalBytes(const char *partition)
{
    char device[8 + APA_IDMAX];
    int fd;
    iox_stat_t st;
    uint32_t i;
    uint32_t nparts;
    uint64_t sectors = 0;
    int rv;

    if (!partition || partition[0] == '\0')
        return 0;

    snprintf(device, sizeof(device), "hdd0:%s", partition);
    fd = fileXioOpen(device, FIO_O_RDONLY, 0);
    if (fd < 0) {
        /* 已挂载时 open 常失败；至少用 GetStat 主分区长度 */
        if (fileXioGetStat(device, &st) >= 0 && st.size > 0)
            return (uint64_t)st.size * 512ull;
        return 0;
    }

    if (fileXioGetStat(device, &st) < 0) {
        fileXioClose(fd);
        return 0;
    }

    nparts = (uint32_t)st.private_0 + 1u; /* private_0 = nsub */
    for (i = 0; i < nparts; i++) {
        rv = fileXioIoctl2(fd, HIOCGETSIZE, &i, sizeof(i), NULL, 0);
        if (rv > 0)
            sectors += (uint32_t)rv;
    }
    fileXioClose(fd);
    if (sectors == 0 && st.size > 0)
        return (uint64_t)st.size * 512ull;
    return sectors * 512ull;
}

static int handleGetApaFree(struct PfsNetClient *client)
{
    uint32_t freeSectors = 0;
    uint64_t totalBytes = 0;
    uint64_t freeBytes = 0;
    uint8_t out[16];
    int result;

    result = fileXioDevctl("hdd0:", HDIOC_TOTALSECTOR, NULL, 0, NULL, 0);
    if (result < 0)
        return pfsnetSendResponse(client, mapErrno(result), NULL, 0);
    totalBytes = (uint64_t)(uint32_t)result * 512ull;

    result = fileXioDevctl("hdd0:", HDIOC_FREESECTOR, NULL, 0, &freeSectors, sizeof(freeSectors));
    if (result < 0)
        return pfsnetSendResponse(client, mapErrno(result), NULL, 0);

    freeBytes = (uint64_t)freeSectors * 512ull;
    memcpy(out, &totalBytes, 8);
    memcpy(out + 8, &freeBytes, 8);
    return pfsnetSendResponse(client, PFSNET_OK, out, 16);
}

static int handleGetPfsFree(struct PfsNetClient *client)
{
    uint8_t out[20];
    uint64_t totalBytes = 0;
    uint64_t freeBytes = 0;
    uint32_t zone = 8192;
    int zoneSize;
    int zoneFree;

    if (!g_mounted)
        return pfsnetSendResponse(client, PFSNET_ERR_NOT_MOUNTED, NULL, 0);

    /* 与 libhdd / 本地 PfsSpaceReader 对齐：PDIOC_ZONEFREE * PDIOC_ZONESZ */
    zoneSize = fileXioDevctl(PFSNET_MOUNT_POINT, PDIOC_ZONESZ, NULL, 0, NULL, 0);
    zoneFree = fileXioDevctl(PFSNET_MOUNT_POINT, PDIOC_ZONEFREE, NULL, 0, NULL, 0);
    if (zoneSize > 0) {
        zone = (uint32_t)zoneSize;
        if (zoneFree >= 0)
            freeBytes = (uint64_t)(uint32_t)zoneFree * (uint64_t)(uint32_t)zoneSize;
    }

    totalBytes = g_mountedTotalBytes;
    if (totalBytes == 0)
        totalBytes = getPartitionTotalBytes(g_mountedPartition);
    if (totalBytes > 0 && freeBytes > totalBytes)
        freeBytes = totalBytes;

    memcpy(out, &totalBytes, 8);
    memcpy(out + 8, &freeBytes, 8);
    memcpy(out + 16, &zone, 4);
    return pfsnetSendResponse(client, PFSNET_OK, out, 20);
}

static int handleListDir(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    char path[512];
    char full[576];
    uint32_t consumed = 0;
    int fd;
    iox_dirent_t dirent;
    uint8_t *out = client->tx;
    int used = 4;
    uint32_t count = 0;
    int rc;

    if (!g_mounted)
        return pfsnetSendResponse(client, PFSNET_ERR_NOT_MOUNTED, NULL, 0);

    rc = pfsnetReadString(payload, payloadLen, 0, path, sizeof(path), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);

    openPfsPath(full, sizeof(full), path);
    fd = fileXioDopen(full);
    if (fd < 0)
        return pfsnetSendResponse(client, mapErrno(fd), NULL, 0);

    while (fileXioDread(fd, &dirent) > 0) {
        int isDir;
        uint64_t size;
        int wrote;
        char details[64];

        if (strcmp(dirent.name, ".") == 0 || strcmp(dirent.name, "..") == 0)
            continue;

        isDir = (dirent.stat.mode & FIO_S_IFDIR) != 0;
        size = ((uint64_t)dirent.stat.hisize << 32) | (uint64_t)dirent.stat.size;
        snprintf(details, sizeof(details), isDir ? "dir" : "file");

        if (used + 2 + (int)strlen(dirent.name) + 1 + 8 + 2 + (int)strlen(details) > PFSNET_SEND_MAX)
            break;

        wrote = pfsnetWriteString(out + used, PFSNET_SEND_MAX - used, dirent.name);
        if (wrote < 0)
            break;
        used += wrote;
        out[used++] = (uint8_t)(isDir ? 1 : 0);
        memcpy(out + used, &size, 8);
        used += 8;
        wrote = pfsnetWriteString(out + used, PFSNET_SEND_MAX - used, details);
        if (wrote < 0)
            break;
        used += wrote;
        count++;
    }
    fileXioDclose(fd);

    memcpy(out, &count, 4);
    return pfsnetSendResponse(client, PFSNET_OK, out, (uint32_t)used);
}

static int handlePathNameOp(struct PfsNetClient *client, uint32_t command, const uint8_t *payload, uint32_t payloadLen)
{
    char parent[512];
    char name[256];
    char full[768];
    uint32_t off = 0, consumed = 0;
    int rc, result = 0;

    if (!g_mounted)
        return pfsnetSendResponse(client, PFSNET_ERR_NOT_MOUNTED, NULL, 0);

    rc = pfsnetReadString(payload, payloadLen, off, parent, sizeof(parent), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    rc = pfsnetReadString(payload, payloadLen, off, name, sizeof(name), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);

    joinPfsPath(full, sizeof(full), parent, name);

    switch (command) {
        case PFSNET_CMD_MKDIR:
            result = fileXioMkdir(full, 0777);
            break;
        case PFSNET_CMD_RMDIR:
            result = fileXioRmdir(full);
            break;
        case PFSNET_CMD_REMOVE:
            result = fileXioRemove(full);
            break;
        default:
            return pfsnetSendResponse(client, PFSNET_ERR_UNSUPPORTED, NULL, 0);
    }

    return pfsnetSendResponse(client, mapErrno(result), NULL, 0);
}

static int handleRename(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    char parent[512];
    char oldName[256];
    char newName[256];
    char oldPath[768];
    char newPath[768];
    uint32_t off = 0, consumed = 0;
    int rc, result;

    if (!g_mounted)
        return pfsnetSendResponse(client, PFSNET_ERR_NOT_MOUNTED, NULL, 0);

    rc = pfsnetReadString(payload, payloadLen, off, parent, sizeof(parent), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    rc = pfsnetReadString(payload, payloadLen, off, oldName, sizeof(oldName), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    rc = pfsnetReadString(payload, payloadLen, off, newName, sizeof(newName), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);

    joinPfsPath(oldPath, sizeof(oldPath), parent, oldName);
    joinPfsPath(newPath, sizeof(newPath), parent, newName);
    result = fileXioRename(oldPath, newPath);
    return pfsnetSendResponse(client, mapErrno(result), NULL, 0);
}

static int xferSendAll(int sock, const void *buf, int len)
{
    const char *ptr = (const char *)buf;
    int sent = 0;
    int ok = 1;

    pfsnetTrafficBegin();
    while (sent < len) {
        int n;
        if (g_putStop) {
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

static int xferRecvAll(int sock, void *buf, int len)
{
    char *ptr = (char *)buf;
    int got = 0;
    int ok = 1;

    pfsnetTrafficBegin();
    while (got < len) {
        int n;
        if (g_putStop) {
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

static void ioWorker(void *arg)
{
    (void)arg;
    for (;;) {
        WaitSema(g_ioSemStart);
        if (g_ioMode == PFSNET_IO_MODE_PUT_RING) {
            uint64_t netRem = g_putTotalBytes;
            int wrIdx = 0;

            while (netRem > 0 && !g_putStop) {
                int chunk = netRem > (uint64_t)PFSNET_XFER_BUF ? PFSNET_XFER_BUF : (int)netRem;
                uint8_t *dst = g_ringBase + ((size_t)wrIdx * (size_t)PFSNET_XFER_BUF);
                int r;

                WaitSema(g_putFreeSem);
                if (g_putStop) {
                    g_ringBankLen[wrIdx] = -1;
                    SignalSema(g_putFillSem);
                    break;
                }
                r = xferRecvAll(g_ioSock, dst, chunk);
                if (r != chunk) {
                    /* 数据口提前断开或被取消：立即置位 g_putStop，通知写盘线程跳过环形缓冲中尚未写盘的旧块 */
                    g_putStop = 1;
                    g_ringBankLen[wrIdx] = -1;
                    SignalSema(g_putFillSem);
                    break;
                }
                g_ringBankLen[wrIdx] = r;
                SignalSema(g_putFillSem);
                netRem -= (uint64_t)r;
                wrIdx++;
                if (wrIdx >= g_ringBanks)
                    wrIdx = 0;
            }
            SignalSema(g_ioSemDone);
            continue;
        }
        if (g_ioIsSend)
            g_ioResult = xferSendAll(g_ioSock, g_ioBuf, g_ioLen);
        else
            g_ioResult = xferRecvAll(g_ioSock, g_ioBuf, g_ioLen);
        SignalSema(g_ioSemDone);
    }
}

/* 启动网络 I/O 线程；失败则 g_ioAsync=0，走同步收发 */
static int initIoWorker(void)
{
    ee_sema_t sem;
    ee_thread_t th;

    if (g_ioTid >= 0)
        return 0;

    memset(&sem, 0, sizeof(sem));
    sem.init_count = 0;
    sem.max_count = 1;
    g_ioSemStart = CreateSema(&sem);
    g_ioSemDone = CreateSema(&sem);
    if (g_ioSemStart < 0 || g_ioSemDone < 0) {
        g_ioAsync = 0;
        return -1;
    }

    memset(&sem, 0, sizeof(sem));
    sem.init_count = 0;
    sem.max_count = PFSNET_RING_MAX_BANKS;
    g_putFreeSem = CreateSema(&sem);
    g_putFillSem = CreateSema(&sem);
    if (g_putFreeSem < 0 || g_putFillSem < 0) {
        g_ioAsync = 0;
        return -1;
    }

    memset(&th, 0, sizeof(th));
    th.func = &ioWorker;
    th.stack = g_ioStack;
    th.stack_size = sizeof(g_ioStack);
    th.gp_reg = &_gp;
    /* 必须高于 NETMAN_RxThread(0x59) 且略低于 clientThread(0x40)：
     * lwIP 的 pcb->rcv_wnd 在 tcp_input(NETMAN_RxThread) 中扣减，仅在应用线程调用
     * netconn_recv_tcp_pbuf -> tcp_recved 时恢复。ioWorker(0x42) 高于 NETMAN_RxThread(0x59)
     * 可在每收到一个分片释放 lock_tcpip_core 后立即调用 tcp_recved 推进窗口右沿并拷入 8MiB 环形缓冲，
     * 避免 NETMAN_RxThread 连续收包把 rcv_wnd 扣到 0 触发 ZeroWindow 锯齿停顿。 */
    th.initial_priority = 0x42;
    th.attr = 0;
    th.option = 0;

    g_ioTid = CreateThread(&th);
    if (g_ioTid < 0) {
        g_ioAsync = 0;
        return -1;
    }
    pfsnetSetIoWorkerTid(g_ioTid);
    StartThread(g_ioTid, NULL);
    g_ioAsync = 1;
    return 0;
}

static void ioBegin(int isSend, int sock, void *buf, int len)
{
    g_ioMode = PFSNET_IO_MODE_SINGLE;
    g_ioIsSend = isSend;
    g_ioSock = sock;
    g_ioBuf = buf;
    g_ioLen = len;
    if (g_ioAsync)
        SignalSema(g_ioSemStart);
    else
        g_ioResult = isSend ? xferSendAll(sock, buf, len) : xferRecvAll(sock, buf, len);
}

static int ioEnd(void)
{
    if (g_ioAsync)
        WaitSema(g_ioSemDone);
    return g_ioResult;
}

/* 数据通道：关 Nagle；缓冲对齐 XFER 块，避免 512KiB 收发被 256KiB 窗反复截断 */
static void tuneDataSocket(int sock)
{
    int yes = 1;
    int buf = PFSNET_XFER_BUF;
    struct timeval tv;

    setsockopt(sock, IPPROTO_TCP, TCP_NODELAY, &yes, sizeof(yes));
    setsockopt(sock, SOL_SOCKET, SO_RCVBUF, &buf, sizeof(buf));
    setsockopt(sock, SOL_SOCKET, SO_SNDBUF, &buf, sizeof(buf));

    /* 数据口空闲超时设为 90 秒，兼容无线 WiFi 中继重关联/重协商及 PFS 大文件元数据刷盘停顿；
     * 取消传输或异常终止时由 pfsnetCheckRxAbortFrame / pfsnetFsAbortActiveSocket 毫秒级唤醒。 */
    tv.tv_sec = 90;
    tv.tv_usec = 0;
    setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));
}

/* 向控制连接对端（PC）的 45062 回连数据通道 */
static int connectDataChannel(struct PfsNetClient *client)
{
    int sock;
    struct sockaddr_in addr;

    sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (sock < 0)
        return -1;

    tuneDataSocket(sock);

    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(PFSNET_DATA_PORT);
    addr.sin_addr = client->peer.sin_addr;

    if (connect(sock, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        close(sock);
        return -1;
    }
    return sock;
}

static int pathExists(const char *fullPath)
{
    iox_stat_t st;
    return fileXioGetStat(fullPath, &st) >= 0;
}

static int handlePutBegin(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    char parent[512];
    char finalName[256];
    char tempName[32];
    uint64_t totalBytes = 0;
    uint32_t flags = 0;
    uint32_t off = 0, consumed = 0;
    int rc, fd = -1, dataSock;
    int resumed = 0;
    uint64_t got = 0;

    if (!g_mounted)
        return pfsnetSendResponse(client, PFSNET_ERR_NOT_MOUNTED, NULL, 0);
    if (g_xfer.kind != PFSNET_XFER_NONE)
        return pfsnetSendResponse(client, PFSNET_ERR_BUSY, NULL, 0);

    rc = pfsnetReadString(payload, payloadLen, off, parent, sizeof(parent), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    rc = pfsnetReadString(payload, payloadLen, off, finalName, sizeof(finalName), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    if (off + 12 > payloadLen || finalName[0] == '\0')
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);

    memcpy(&totalBytes, payload + off, 8);
    off += 8;
    memcpy(&flags, payload + off, 4);

    joinPfsPath(g_xfer.finalPath, sizeof(g_xfer.finalPath), parent, finalName);
    if (pathExists(g_xfer.finalPath) && (flags & PFSNET_PUT_OVERWRITE) == 0)
        return pfsnetSendResponse(client, PFSNET_ERR_EXIST, NULL, 0);

    /*
     * 断点续传：当无线中继/数据口闪断触发自动重试时，客户端携带 PFSNET_PUT_RESUME。
     * 若 g_partialPut 记录的分区、目标路径、总字节数一致，且临时文件磁盘实际大小与
     * gotBytes 严格吻合，则以 FIO_O_WRONLY | FIO_O_APPEND 直接打开继续追加，
     * 既省去 15~30 秒删除数 GB 临时文件的开销，又避免从 0 重新传起。
     */
    if ((flags & PFSNET_PUT_RESUME) != 0 &&
        g_partialPut.valid &&
        strcmp(g_partialPut.partition, g_mountedPartition) == 0 &&
        strcmp(g_partialPut.finalPath, g_xfer.finalPath) == 0 &&
        g_partialPut.totalBytes == totalBytes &&
        g_partialPut.gotBytes > 0 &&
        g_partialPut.gotBytes < totalBytes) {
        iox_stat_t st;
        if (fileXioGetStat(g_partialPut.tempPath, &st) >= 0) {
            uint64_t diskSize = (uint64_t)st.size | ((uint64_t)st.hisize << 32);
            if (diskSize == g_partialPut.gotBytes) {
                fd = fileXioOpen(g_partialPut.tempPath, FIO_O_WRONLY | FIO_O_APPEND, 0666);
                if (fd >= 0) {
                    strncpy(g_xfer.tempPath, g_partialPut.tempPath, sizeof(g_xfer.tempPath) - 1);
                    g_xfer.tempPath[sizeof(g_xfer.tempPath) - 1] = '\0';
                    got = diskSize;
                    resumed = 1;
                    g_partialPut.valid = 0;
                    g_partialPut.tempPath[0] = '\0';
                }
            }
        }
    }

    if (!resumed) {
        clearPartialPut();
        g_tempSeq++;
        snprintf(tempName, sizeof(tempName), ".pfsnet_%08x", (unsigned)g_tempSeq);
        joinPfsPath(g_xfer.tempPath, sizeof(g_xfer.tempPath), parent, tempName);

        /* 必须用 FIO_O_*；newlib O_WRONLY(=1) 会被 IOP 当成 FIO_O_RDONLY */
        fd = fileXioOpen(g_xfer.tempPath, FIO_O_WRONLY | FIO_O_CREAT | FIO_O_TRUNC, 0666);
        if (fd < 0)
            return pfsnetSendResponse(client, mapErrno(fd), NULL, 0);
    }

    /* 先回成功（若请求带 PFSNET_PUT_RESUME 则返回 8 字节 uint64 续传起点偏移），再回连数据口（PROTOCOL §5.4） */
    if ((flags & PFSNET_PUT_RESUME) != 0)
        rc = pfsnetSendResponse(client, PFSNET_OK, &got, sizeof(got));
    else
        rc = pfsnetSendResponse(client, PFSNET_OK, NULL, 0);
    if (rc < 0) {
        fileXioClose(fd);
        if (resumed && got > 0) {
            savePartialPut(totalBytes, got, flags & ~PFSNET_PUT_RESUME);
        } else {
            fileXioRemove(g_xfer.tempPath);
        }
        clearXferState();
        return -1;
    }

    dataSock = connectDataChannel(client);
    if (dataSock < 0) {
        fileXioClose(fd);
        if (resumed && got > 0) {
            savePartialPut(totalBytes, got, flags & ~PFSNET_PUT_RESUME);
        } else {
            fileXioRemove(g_xfer.tempPath);
        }
        clearXferState();
        return 0;
    }
    g_xfer.dataSock = dataSock;
    g_putStop = 0;
    g_putUserAbort = 0;

    /* 多槽环形缓冲（16x512KiB = 8MiB）：ioWorker 连续排空 TCP 窗口，clientThread 连续写盘 */
    g_lastPutErr = 0;
    if (g_ioAsync && g_putFreeSem >= 0 && g_putFillSem >= 0) {
        int i;
        int rdIdx = 0;
        int failed = 0;

        while (PollSema(g_putFreeSem) == g_putFreeSem) {
        }
        while (PollSema(g_putFillSem) == g_putFillSem) {
        }
        for (i = 0; i < g_ringBanks; i++) {
            g_ringBankLen[i] = 0;
            SignalSema(g_putFreeSem);
        }

        g_putTotalBytes = totalBytes - got;
        g_ioSock = dataSock;
        g_ioMode = PFSNET_IO_MODE_PUT_RING;
        SignalSema(g_ioSemStart);

        while (got < totalBytes && !failed) {
            int bankLen;
            uint8_t *src;
            int written = 0;

            WaitSema(g_putFillSem);
            bankLen = g_ringBankLen[rdIdx];
            if (bankLen <= 0 || g_putStop) {
                if (g_lastPutErr == 0)
                    g_lastPutErr = PFSNET_ERR_DATA_CONN;
                failed = 1;
                break;
            }

            src = g_ringBase + ((size_t)rdIdx * (size_t)PFSNET_XFER_BUF);

            while (written < bankLen) {
                int wr;
                if (g_putStop) {
                    if (g_lastPutErr == 0)
                        g_lastPutErr = PFSNET_ERR_DATA_CONN;
                    failed = 1;
                    break;
                }
                wr = fileXioWrite(fd, src + written, bankLen - written);
                if (wr <= 0) {
                    g_lastPutErr = wr < 0 ? mapErrno(wr) : PFSNET_ERR_IO;
                    failed = 1;
                    g_putStop = 1;
                    break;
                }
                written += wr;
            }

            SignalSema(g_putFreeSem);

            if (!failed)
                got += (uint64_t)bankLen;

            rdIdx++;
            if (rdIdx >= g_ringBanks)
                rdIdx = 0;
        }

        if (failed) {
            g_putStop = 1;
            SignalSema(g_putFreeSem);
            if (g_ioTid >= 0)
                pfsnetWakeMboxWaiter(g_ioTid);
        }
        WaitSema(g_ioSemDone);
    } else {
        int idx = 0;
        int havePending = 0;
        int pendingIdx = 0;
        int pendingLen = 0;
        int failed = 0;

        while (got < totalBytes) {
            uint64_t remain = totalBytes - got - (uint64_t)(havePending ? pendingLen : 0);
            int chunk = remain > (uint64_t)PFSNET_XFER_BUF ? PFSNET_XFER_BUF : (int)remain;

            if (chunk <= 0)
                break;

            ioBegin(0, dataSock, g_xferBuf[idx], chunk);

            if (havePending) {
                int written = 0;
                while (written < pendingLen) {
                    int wr;
                    if (g_putStop) {
                        failed = 1;
                        break;
                    }
                    wr = fileXioWrite(fd, g_xferBuf[pendingIdx] + written, pendingLen - written);
                    if (wr <= 0) {
                        g_lastPutErr = wr < 0 ? mapErrno(wr) : PFSNET_ERR_IO;
                        failed = 1;
                        g_putStop = 1;
                        break;
                    }
                    written += wr;
                }
                if (!failed)
                    got += (uint64_t)pendingLen;
                havePending = 0;
            }

            if (ioEnd() < 0) {
                if (g_lastPutErr == 0)
                    g_lastPutErr = PFSNET_ERR_DATA_CONN;
                failed = 1;
            }
            if (failed)
                break;

            pendingIdx = idx;
            pendingLen = chunk;
            havePending = 1;
            idx ^= 1;
        }

        if (!failed && havePending) {
            int written = 0;
            while (written < pendingLen) {
                int wr = fileXioWrite(fd, g_xferBuf[pendingIdx] + written, pendingLen - written);
                if (wr <= 0) {
                    g_lastPutErr = wr < 0 ? mapErrno(wr) : PFSNET_ERR_IO;
                    failed = 1;
                    break;
                }
                written += wr;
            }
            if (!failed)
                got += (uint64_t)pendingLen;
        }
    }

    g_xfer.dataSock = -1;
    close(dataSock);
    fileXioClose(fd);

    if (got != totalBytes) {
        if (g_lastPutErr == 0)
            g_lastPutErr = PFSNET_ERR_DATA_CONN;
        if (!g_putUserAbort && got > 0 &&
            (g_lastPutErr == 0 || g_lastPutErr == PFSNET_ERR_DATA_CONN)) {
            if (g_mounted)
                fileXioSync(PFSNET_MOUNT_POINT, FILEXIO_MOUNTFLAG_NORMAL);
            savePartialPut(totalBytes, got, flags & ~PFSNET_PUT_RESUME);
        } else {
            fileXioRemove(g_xfer.tempPath);
            if (g_mounted)
                fileXioSync(PFSNET_MOUNT_POINT, FILEXIO_MOUNTFLAG_NORMAL);
            clearPartialPut();
        }
        clearXferState();
        return 0;
    }

    clearPartialPut();
    g_xfer.kind = PFSNET_XFER_PUT;
    g_xfer.fileFd = -1;
    g_xfer.dataSock = -1;
    g_xfer.flags = flags & ~PFSNET_PUT_RESUME;
    g_xfer.totalBytes = totalBytes;
    return 0;
}

static int handlePutEnd(struct PfsNetClient *client)
{
    int result;

    if (g_xfer.kind != PFSNET_XFER_PUT) {
        int32_t err = g_lastPutErr != 0 ? g_lastPutErr : PFSNET_ERR_INVAL;
        g_lastPutErr = 0;
        return pfsnetSendResponse(client, err, NULL, 0);
    }
    g_lastPutErr = 0;

    if ((g_xfer.flags & PFSNET_PUT_OVERWRITE) && pathExists(g_xfer.finalPath)) {
        result = fileXioRemove(g_xfer.finalPath);
        if (result < 0) {
            abortXferCleanup();
            return pfsnetSendResponse(client, mapErrno(result), NULL, 0);
        }
    }

    result = fileXioRename(g_xfer.tempPath, g_xfer.finalPath);
    if (result < 0) {
        abortXferCleanup();
        return pfsnetSendResponse(client, mapErrno(result), NULL, 0);
    }

    if (g_mounted)
        fileXioSync(PFSNET_MOUNT_POINT, FILEXIO_MOUNTFLAG_NORMAL);

    clearXferState();
    return pfsnetSendResponse(client, PFSNET_OK, NULL, 0);
}

static int handlePutAbort(struct PfsNetClient *client)
{
    abortXferCleanup();
    clearPartialPut();
    return pfsnetSendResponse(client, PFSNET_OK, NULL, 0);
}

static int handleGetBegin(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    char parent[512];
    char name[256];
    char full[768];
    uint64_t offset = 0, length = 0, fileSize = 0, toSend = 0, sent = 0;
    uint32_t off = 0, consumed = 0;
    int rc, fd, dataSock;
    iox_stat_t st;
    uint8_t reply[8];

    if (!g_mounted)
        return pfsnetSendResponse(client, PFSNET_ERR_NOT_MOUNTED, NULL, 0);
    if (g_xfer.kind != PFSNET_XFER_NONE)
        return pfsnetSendResponse(client, PFSNET_ERR_BUSY, NULL, 0);

    rc = pfsnetReadString(payload, payloadLen, off, parent, sizeof(parent), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    rc = pfsnetReadString(payload, payloadLen, off, name, sizeof(name), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    if (off + 16 > payloadLen || name[0] == '\0')
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);

    memcpy(&offset, payload + off, 8);
    off += 8;
    memcpy(&length, payload + off, 8);

    joinPfsPath(full, sizeof(full), parent, name);
    if (fileXioGetStat(full, &st) < 0)
        return pfsnetSendResponse(client, PFSNET_ERR_NOENT, NULL, 0);
    if (st.mode & FIO_S_IFDIR)
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);

    fileSize = ((uint64_t)st.hisize << 32) | (uint64_t)st.size;
    if (offset > fileSize)
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);
    toSend = length == 0 ? (fileSize - offset) : length;
    if (offset + toSend > fileSize)
        toSend = fileSize - offset;

    /* 必须用 FIO_O_RDONLY(=1)；newlib O_RDONLY(=0) 会使 open 成功但读到 0 字节 */
    fd = fileXioOpen(full, FIO_O_RDONLY, 0);
    if (fd < 0)
        return pfsnetSendResponse(client, mapErrno(fd), NULL, 0);

    if (offset > 0) {
        if (fileXioLseek64(fd, (s64)offset, FIO_SEEK_SET) < 0) {
            fileXioClose(fd);
            return pfsnetSendResponse(client, PFSNET_ERR_IO, NULL, 0);
        }
    }

    /* 与 Put 对称：先回 GET_BEGIN OK（含 total_bytes），再回连数据口后发字节 */
    memcpy(reply, &toSend, 8);
    if (pfsnetSendResponse(client, PFSNET_OK, reply, 8) < 0) {
        fileXioClose(fd);
        return -1;
    }

    dataSock = connectDataChannel(client);
    if (dataSock < 0) {
        fileXioClose(fd);
        return 0;
    }
    g_xfer.dataSock = dataSock;
    g_putStop = 0;

    /* 双缓冲：与 Put 对称——先启动上一块 send，再读下一块（重叠）。
     * 旧顺序「先读满再等 send」在读快于 TCP 时会让磁盘空等。 */
    {
        int idx = 0;
        int havePending = 0;
        int pendingIdx = 0;
        int pendingLen = 0;
        int failed = 0;

        while (!failed && (sent < toSend || havePending)) {
            int rd = 0;
            int ioStarted = 0;

            if (havePending) {
                ioBegin(1, dataSock, g_xferBuf[pendingIdx], pendingLen);
                ioStarted = 1;
            }

            if (sent + (uint64_t)(havePending ? pendingLen : 0) < toSend) {
                uint64_t remain = toSend - sent - (uint64_t)(havePending ? pendingLen : 0);
                int chunk = remain > (uint64_t)PFSNET_XFER_BUF ? PFSNET_XFER_BUF : (int)remain;

                while (rd < chunk) {
                    int n;
                    if (g_putStop) {
                        failed = 1;
                        break;
                    }
                    n = fileXioRead(fd, g_xferBuf[idx] + rd, chunk - rd);
                    if (n < 0) {
                        failed = 1;
                        g_putStop = 1;
                        break;
                    }
                    if (n == 0)
                        break; /* 真 EOF */
                    rd += n;
                }
            }

            if (ioStarted) {
                if (ioEnd() < 0)
                    failed = 1;
                else
                    sent += (uint64_t)pendingLen;
                havePending = 0;
            }

            if (failed)
                break;

            if (rd > 0) {
                pendingIdx = idx;
                pendingLen = rd;
                havePending = 1;
                idx ^= 1;
            } else if (sent >= toSend) {
                break;
            } else {
                failed = 1; /* 未读满却提前 EOF */
                break;
            }
        }

        /* 循环退出时若仍有未启动的 pending，同步发出 */
        if (!failed && havePending) {
            if (xferSendAll(dataSock, g_xferBuf[pendingIdx], pendingLen) < 0)
                failed = 1;
            else
                sent += (uint64_t)pendingLen;
        }
        (void)failed;
    }

    fileXioClose(fd);

    if (sent != toSend) {
        g_xfer.dataSock = -1;
        close(dataSock);
        return 0;
    }

    /* 半关闭写端发出 FIN；socket 留到 GET_END 再 close，避免 PC 未读完时 abortive close 丢尾部数据 */
    shutdown(dataSock, SHUT_WR);

    g_xfer.kind = PFSNET_XFER_GET;
    g_xfer.fileFd = -1;
    g_xfer.dataSock = dataSock;
    g_xfer.totalBytes = toSend;
    return 0;
}
static int handleGetEnd(struct PfsNetClient *client)
{
    if (g_xfer.kind != PFSNET_XFER_GET)
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);
    if (g_xfer.dataSock >= 0) {
        close(g_xfer.dataSock);
        g_xfer.dataSock = -1;
    }
    clearXferState();
    return pfsnetSendResponse(client, PFSNET_OK, NULL, 0);
}

static int handleGetAbort(struct PfsNetClient *client)
{
    abortXferCleanup();
    return pfsnetSendResponse(client, PFSNET_OK, NULL, 0);
}

/* APA 创建用的主/子分区尺寸阶梯（对齐 libhdd / pfsshell） */
static const char *const g_apaSizeStr[] = {
    "128M", "256M", "512M", "1G", "2G", "4G", "8G", "16G", "32G"
};
static const int g_apaSizeMb[] = {
    128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768
};

static int parseSizeToMb(const char *sizeStr, int *outMb)
{
    char buf[32];
    size_t n;
    unsigned long v;
    char unit;

    if (!sizeStr || !outMb)
        return PFSNET_ERR_INVAL;
    n = strlen(sizeStr);
    if (n < 2 || n >= sizeof(buf))
        return PFSNET_ERR_INVAL;
    memcpy(buf, sizeStr, n + 1);
    unit = buf[n - 1];
    buf[n - 1] = '\0';
    v = strtoul(buf, NULL, 10);
    if (v == 0)
        return PFSNET_ERR_INVAL;
    if (unit == 'M' || unit == 'm')
        *outMb = (int)v;
    else if (unit == 'G' || unit == 'g')
        *outMb = (int)(v * 1024ul);
    else
        return PFSNET_ERR_INVAL;
    if ((*outMb % 128) != 0)
        return PFSNET_ERR_INVAL;
    return PFSNET_OK;
}

static int formatPfsPartition(const char *name)
{
    /* 对齐 pfsshell mkpfs：zone 8192 + -f 片段参数 */
    int formatArg[3] = {8192, 0x00002d66, 0};
    char device[8 + APA_IDMAX];

    snprintf(device, sizeof(device), "hdd0:%s", name);
    return fileXioFormat("pfs:", device, (const char *)formatArg, sizeof(formatArg));
}

static int handleReadSectors(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    uint64_t lba64;
    uint32_t count;
    hddAtaTransfer_t arg;
    int rv;
    uint32_t bytes;

    if (payloadLen < 12)
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);
    memcpy(&lba64, payload, 8);
    memcpy(&count, payload + 8, 4);
    if (count == 0 || count > 256 || lba64 > 0xFFFFFFFFu)
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);

    bytes = count * 512u;
    if (!g_xferBuf[0])
        return pfsnetSendResponse(client, PFSNET_ERR_IO, NULL, 0);

    arg.lba = (u32)lba64;
    arg.size = count;
    rv = fileXioDevctl("hdd0:", HDIOC_READSECTOR, &arg, sizeof(arg), g_xferBuf[0], (int)bytes);
    if (rv != 0)
        return pfsnetSendResponse(client, mapErrno(rv < 0 ? rv : -EIO), NULL, 0);
    return pfsnetSendResponse(client, PFSNET_OK, g_xferBuf[0], bytes);
}

static int createApaPfsPartition(const char *name, int sizeMb)
{
    char openStr[8 + APA_IDMAX + 32];
    char checkStr[8 + APA_IDMAX];
    int maxIndex, useIndex, partSize, left, fd, rv, maxMb;

    if (isSystemPartitionName(name))
        return PFSNET_ERR_INVAL;

    snprintf(checkStr, sizeof(checkStr), "hdd0:%s", name);
    fd = fileXioOpen(checkStr, FIO_O_RDONLY, 0);
    if (fd >= 0 || fd == -EACCES) {
        if (fd >= 0)
            fileXioClose(fd);
        return PFSNET_ERR_EXIST;
    }

    maxMb = (int)((u32)fileXioDevctl("hdd0:", HDIOC_MAXSECTOR, NULL, 0, NULL, 0) / 2048u);
    for (maxIndex = 0; maxIndex < 9; maxIndex++) {
        if (g_apaSizeMb[maxIndex] == maxMb)
            break;
    }
    if (maxIndex >= 9)
        maxIndex = 8;

    for (useIndex = maxIndex; useIndex > 0 && g_apaSizeMb[useIndex] > sizeMb; useIndex--)
        ;

    fd = -1;
    for (; useIndex >= 0; useIndex--) {
        partSize = g_apaSizeMb[useIndex];
        snprintf(openStr, sizeof(openStr), "hdd0:%s,,,%s,PFS", name, g_apaSizeStr[useIndex]);
        fd = fileXioOpen(openStr, FIO_O_RDWR | FIO_O_CREAT, 0);
        if (fd >= 0)
            break;
        if (fd != -ENOSPC)
            return mapErrno(fd);
    }
    if (fd < 0)
        return PFSNET_ERR_NOSPC;

    left = sizeMb - g_apaSizeMb[useIndex];
    while (left > 0 && useIndex >= 0) {
        if (g_apaSizeMb[useIndex] > left) {
            while (useIndex > 0 && g_apaSizeMb[useIndex] > left)
                useIndex--;
            if (g_apaSizeMb[useIndex] > left)
                break;
        }
        rv = fileXioIoctl2(fd, HIOCADDSUB, (void *)g_apaSizeStr[useIndex],
                           (int)strlen(g_apaSizeStr[useIndex]) + 1, NULL, 0);
        if (rv == -ENOSPC) {
            useIndex--;
            continue;
        }
        if (rv == -EFBIG)
            break;
        if (rv < 0) {
            fileXioClose(fd);
            fileXioRemove(checkStr);
            return mapErrno(rv);
        }
        left -= g_apaSizeMb[useIndex];
    }

    fileXioClose(fd);
    rv = formatPfsPartition(name);
    if (rv < 0) {
        fileXioRemove(checkStr);
        return mapErrno(rv);
    }
    /* left>0 表示未能凑满请求容量；与本地核验一致，由 PC 写后校验决定是否回滚 */
    (void)left;
    return PFSNET_OK;
}

static int handleApaCreatePfs(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    char name[APA_IDMAX];
    char sizeStr[32];
    uint32_t off = 0, consumed = 0;
    int rc, sizeMb;

    if (g_mounted || g_xfer.kind != PFSNET_XFER_NONE)
        return pfsnetSendResponse(client, PFSNET_ERR_BUSY, NULL, 0);

    rc = pfsnetReadString(payload, payloadLen, off, name, sizeof(name), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    rc = pfsnetReadString(payload, payloadLen, off, sizeStr, sizeof(sizeStr), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    if (name[0] == '\0')
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);

    rc = parseSizeToMb(sizeStr, &sizeMb);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);

    return pfsnetSendResponse(client, createApaPfsPartition(name, sizeMb), NULL, 0);
}

static int handleApaRemove(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    char name[APA_IDMAX];
    char path[8 + APA_IDMAX];
    uint32_t consumed = 0;
    int rc, rv;

    if (g_xfer.kind != PFSNET_XFER_NONE)
        return pfsnetSendResponse(client, PFSNET_ERR_BUSY, NULL, 0);
    if (g_mounted) {
        rc = doUmount();
        if (rc != PFSNET_OK)
            return pfsnetSendResponse(client, rc, NULL, 0);
    }

    rc = pfsnetReadString(payload, payloadLen, 0, name, sizeof(name), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    if (name[0] == '\0' || isSystemPartitionName(name))
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);

    /* 标准 IOP ps2hdd 会拒绝删除任意 __ 前缀分区（-EACCES）。
     * __.POPS 等不是 __mbr/__common，PC 允许删，但未定义
     * APA_ALLOW_REMOVE_PARTITION_WITH_LEADING_UNDERSCORE 的 IRX 仍会失败。 */
    snprintf(path, sizeof(path), "hdd0:%s", name);
    rv = fileXioRemove(path);
    return pfsnetSendResponse(client, mapErrno(rv), NULL, 0);
}

static int handleApaRename(struct PfsNetClient *client, const uint8_t *payload, uint32_t payloadLen)
{
    char oldName[APA_IDMAX];
    char newName[APA_IDMAX];
    char oldPath[8 + APA_IDMAX];
    char newPath[8 + APA_IDMAX];
    uint32_t off = 0, consumed = 0;
    int rc, rv;

    if (g_mounted || g_xfer.kind != PFSNET_XFER_NONE)
        return pfsnetSendResponse(client, PFSNET_ERR_BUSY, NULL, 0);

    rc = pfsnetReadString(payload, payloadLen, off, oldName, sizeof(oldName), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    off += consumed;
    rc = pfsnetReadString(payload, payloadLen, off, newName, sizeof(newName), &consumed);
    if (rc != PFSNET_OK)
        return pfsnetSendResponse(client, rc, NULL, 0);
    if (oldName[0] == '\0' || newName[0] == '\0')
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);
    if (isSystemPartitionName(oldName) || isSystemPartitionName(newName))
        return pfsnetSendResponse(client, PFSNET_ERR_INVAL, NULL, 0);

    snprintf(oldPath, sizeof(oldPath), "hdd0:%s", oldName);
    snprintf(newPath, sizeof(newPath), "hdd0:%s", newName);
    rv = fileXioRename(oldPath, newPath);
    return pfsnetSendResponse(client, mapErrno(rv), NULL, 0);
}

int pfsnetHandleCommand(struct PfsNetClient *client, uint32_t command, const uint8_t *payload, uint32_t payloadLen)
{
    char partition[APA_IDMAX];
    uint32_t consumed = 0;
    int rc;

    switch (command) {
        case PFSNET_CMD_PING:
            return pfsnetSendResponse(client, PFSNET_OK, NULL, 0);

        case PFSNET_CMD_MOUNT:
            rc = pfsnetReadString(payload, payloadLen, 0, partition, sizeof(partition), &consumed);
            if (rc != PFSNET_OK)
                return pfsnetSendResponse(client, rc, NULL, 0);
            return pfsnetSendResponse(client, doMount(partition), NULL, 0);

        case PFSNET_CMD_UMOUNT:
            return pfsnetSendResponse(client, doUmount(), NULL, 0);

        case PFSNET_CMD_LIST_PARTITIONS:
            return handleListPartitions(client, payload, payloadLen);

        case PFSNET_CMD_GET_APA_FREE:
            return handleGetApaFree(client);

        case PFSNET_CMD_GET_PFS_FREE:
            return handleGetPfsFree(client);

        case PFSNET_CMD_LIST_DIR:
            return handleListDir(client, payload, payloadLen);

        case PFSNET_CMD_MKDIR:
        case PFSNET_CMD_RMDIR:
        case PFSNET_CMD_REMOVE:
            return handlePathNameOp(client, command, payload, payloadLen);

        case PFSNET_CMD_RENAME:
            return handleRename(client, payload, payloadLen);

        case PFSNET_CMD_PUT_BEGIN:
            return handlePutBegin(client, payload, payloadLen);

        case PFSNET_CMD_PUT_END:
            return handlePutEnd(client);

        case PFSNET_CMD_PUT_ABORT:
            return handlePutAbort(client);

        case PFSNET_CMD_GET_BEGIN:
            return handleGetBegin(client, payload, payloadLen);

        case PFSNET_CMD_GET_END:
            return handleGetEnd(client);

        case PFSNET_CMD_GET_ABORT:
            return handleGetAbort(client);

        case PFSNET_CMD_READ_SECTORS:
            return handleReadSectors(client, payload, payloadLen);

        case PFSNET_CMD_APA_CREATE_PFS:
            return handleApaCreatePfs(client, payload, payloadLen);

        case PFSNET_CMD_APA_REMOVE:
            return handleApaRemove(client, payload, payloadLen);

        case PFSNET_CMD_APA_RENAME:
            return handleApaRename(client, payload, payloadLen);

        default:
            return pfsnetSendResponse(client, PFSNET_ERR_UNSUPPORTED, NULL, 0);
    }
}
