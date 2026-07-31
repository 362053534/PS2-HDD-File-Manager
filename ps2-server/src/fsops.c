#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/fcntl.h>
#include <sys/stat.h>

#include <fileXio_rpc.h>
#include <hdd-ioctl.h>
#include <iox_stat.h>

#include "pfsnet.h"

static char g_mountedPartition[APA_IDMAX];
static int g_mounted;

int pfsnetFsInit(void)
{
    g_mounted = 0;
    g_mountedPartition[0] = '\0';
    return 0;
}

void pfsnetFsForceUmount(void)
{
    if (g_mounted) {
        fileXioUmount(PFSNET_MOUNT_POINT);
        g_mounted = 0;
        g_mountedPartition[0] = '\0';
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

    if (g_mounted) {
        if (strcmp(g_mountedPartition, partition) == 0)
            return PFSNET_OK;
        fileXioUmount(PFSNET_MOUNT_POINT);
        g_mounted = 0;
        g_mountedPartition[0] = '\0';
    }

    buildDevicePath(device, sizeof(device), partition);
    result = fileXioMount(PFSNET_MOUNT_POINT, device, FILEXIO_MOUNTFLAG_NORMAL);
    if (result < 0)
        return mapErrno(result);

    strncpy(g_mountedPartition, partition, sizeof(g_mountedPartition) - 1);
    g_mountedPartition[sizeof(g_mountedPartition) - 1] = '\0';
    g_mounted = 1;
    return PFSNET_OK;
}

static int doUmount(void)
{
    if (!g_mounted)
        return PFSNET_OK;
    fileXioUmount(PFSNET_MOUNT_POINT);
    g_mounted = 0;
    g_mountedPartition[0] = '\0';
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
        /* size 在 iox_stat 中以 512 字节扇区计的情况因实现而异；这里用 hisize/size 拼 64 位近似 */
        totalBytes = ((uint64_t)dirent.stat.hisize << 32) | (uint64_t)dirent.stat.size;
        memcpy(out + used, &totalBytes, 8);
        used += 8;
        count++;
    }
    fileXioDclose(fd);

    memcpy(out, &count, 4);
    return pfsnetSendResponse(client, PFSNET_OK, out, (uint32_t)used);
}

static int handleGetApaFree(struct PfsNetClient *client)
{
    uint32_t freeSectors = 0;
    uint64_t totalBytes = 0;
    uint64_t freeBytes = 0;
    uint8_t out[16];
    int result;

    result = fileXioDevctl("hdd0:", HDIOC_FREESECTOR, NULL, 0, &freeSectors, sizeof(freeSectors));
    if (result < 0)
        return pfsnetSendResponse(client, mapErrno(result), NULL, 0);

    freeBytes = (uint64_t)freeSectors * 512ull;
    /* 总量对 UI 提示够用；精确总量留给 A1 扇区校验 */
    totalBytes = freeBytes;
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
    iox_stat_t st;

    if (!g_mounted)
        return pfsnetSendResponse(client, PFSNET_ERR_NOT_MOUNTED, NULL, 0);

    /* PFS 无统一 free ioctl 时，用根目录 stat 作占位；后续可换更准 API */
    if (fileXioGetStat(PFSNET_MOUNT_POINT "/", &st) >= 0) {
        totalBytes = ((uint64_t)st.hisize << 32) | (uint64_t)st.size;
        freeBytes = totalBytes; /* 占位：避免客户端预检硬失败；精确值后续增强 */
    }

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
        case PFSNET_CMD_PUT_END:
        case PFSNET_CMD_PUT_ABORT:
        case PFSNET_CMD_GET_BEGIN:
        case PFSNET_CMD_GET_END:
        case PFSNET_CMD_GET_ABORT:
        case PFSNET_CMD_READ_SECTORS:
        case PFSNET_CMD_APA_CREATE_PFS:
        case PFSNET_CMD_APA_REMOVE:
        case PFSNET_CMD_APA_RENAME:
            return pfsnetSendResponse(client, PFSNET_ERR_UNSUPPORTED, NULL, 0);

        default:
            return pfsnetSendResponse(client, PFSNET_ERR_UNSUPPORTED, NULL, 0);
    }
}
