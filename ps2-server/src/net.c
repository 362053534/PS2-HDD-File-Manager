#include <stdio.h>
#include <stdlib.h>
#include <malloc.h>
#include <string.h>
#include <limits.h>
#include <stdint.h>
#include <errno.h>
#include <kernel.h>
#include <kernel_util.h>
#include <sifdma.h>
#include <sifcmd.h>
#include <sifrpc.h>
#include <timer.h>
#include <timer_alarm.h>
#include <delaythread.h>
#include <debug.h>
#include <netman.h>
#include <netman_rpc.h>
#include <ps2ip.h>

#include "pfsnet.h"

extern void *_gp;
extern void NetManToggleGlobalNetIFLinkState(unsigned char state);

/* GCC14 constructor 写入 .init_array，而 PS2 crt0 只遍历 .ctors，需手动填充 */
extern void __ps2ipeeOpsInitializeImpl(void);

/*
 * PS2SDK 默认 sys_now() 为 clock() / 1000，而 _times()/clock() 用 32 位微秒计数，
 * 会在开机约 71.58 分钟（0xFFFFFFFF us）处从 4294967 跳回 0，破坏 lwIP TCP 定时器比较。
 * 此处用 64 位硬件总线计时器直接换算毫秒，保证在 49.7 天内单调递增并按 2^32 自然回绕。
 * 同时顺带巡检 DMAC_SIF0_CHCR.STR (0x1000c000 & 0x100)，若意外停机立即恢复 DChain。
 */
uint32_t __wrap_sys_now(void)
{
    int old = DIntr();
    if ((_lw(0x1000c000) & 0x100) == 0)
        iSifSetDChain();
    if (old)
        EIntr();
    return (uint32_t)(GetTimerSystemTime() / ((u64)kBUSCLK / 1000ULL));
}

/*
 * 修复 PS2SDK ee/libcglue/src/lock.c 中 __retarget_lock_acquire_recursive /
 * __retarget_lock_release_recursive 在无 DI()/EI() 保护下读写 lock->count 与 lock->thread_id
 * 的竞态，防止多线程并发 malloc / memalign / free 时损坏堆锁。
 */
extern void __real___retarget_lock_acquire_recursive(void *lock);
extern void __real___retarget_lock_release_recursive(void *lock);

void __wrap___retarget_lock_acquire_recursive(void *lock)
{
    int old = DIntr();
    __real___retarget_lock_acquire_recursive(lock);
    if (old)
        EIntr();
}

void __wrap___retarget_lock_release_recursive(void *lock)
{
    int old = DIntr();
    __real___retarget_lock_release_recursive(lock);
    if (old)
        EIntr();
}

/*
 * 解决大文件顺序传输（如 2GB+ PFS 分段/子分区切换写盘停顿）时 PS2 端死锁断线：
 *
 * 1) PS2SDK libps2ip.a 默认 PBUF_POOL_SIZE = 128，其中 NETMAN 固定占走 64 个 RX 环缓冲，
 *    仅剩 64 个 PBUF_POOL 供整个 lwIP 使用。当大文件写到约 2GB 触发 PFS 元数据/日志刷盘或
 *    跨 APA 子分区找空闲块时，fileXioWrite 耗时变长，ioWorker 收完当前 512KiB 后暂停 recv，
 *    主机端发来的 TCP 窗口帧 + 尾包/重传帧一旦塞满剩余 64 个 PBUF_POOL，EE 侧 NETMAN_RxThread
 *    会在 while (AllocRxPacket() == NULL) {} 死循环，进而导致 IOP 侧 SMAP (优先级 0x28)
 *    在 while (bd->length != 0) {} 忙等，饿死低优先级的 fileXio/pfs/atad 写盘线程，形成
 *    EE <-> IOP 四方永久死锁。
 * 2) 通过 wrap memp_init 与 pbuf_alloc，在启动时利用 EE 充裕内存向 PBUF_POOL 等池追加
 *    512 个 64 字节对齐缓冲（可用 RX 缓冲从 64 扩至 576+），并在 pbuf_alloc 兜底动态扩容，
 *    确保 AllocRxPacket 永不返回 NULL。
 * 3) 同时替换 sys_arch 消息池（从 100 扩至 1024），并将 sys_arch_mbox_fetch / sys_arch_sem_wait
 *    内部的 32 位 clock() 微秒计时替换为 64 位单调毫秒计时，消除 35.79 / 71.58 分钟溢出。
 */
struct lwip_memp_desc_min {
    u16 size;
};

extern const struct lwip_memp_desc_min memp_PBUF_POOL;
extern const struct lwip_memp_desc_min memp_PBUF;
extern const struct lwip_memp_desc_min memp_TCP_SEG;
extern const struct lwip_memp_desc_min memp_TCPIP_MSG_API;
extern const struct lwip_memp_desc_min memp_NETBUF;
extern const struct lwip_memp_desc_min memp_SYS_TIMEOUT;

extern void __real_memp_init(void);
extern void memp_free_pool(const void *desc, void *mem);
extern struct pbuf *__real_pbuf_alloc(pbuf_layer l, u16 size, pbuf_type type);
extern err_t __real_netconn_recv_tcp_pbuf(void *conn, struct pbuf **new_buf);

#define PFSNET_PBUF_POOL_STRIDE 1664u
#define PFSNET_SMALL_POOL_STRIDE 64u

static int g_extraMempInitialized;
static volatile int g_dynamicPbufCount;

static void expandLwipPool(const struct lwip_memp_desc_min *desc, u32 count, u32 minStride)
{
    u32 stride;
    u8 *block;
    u32 i;

    if (!desc || count == 0)
        return;
    stride = desc->size;
    if (stride < minStride)
        stride = minStride;
    stride = (stride + 63u) & ~63u;

    block = (u8 *)memalign(64, stride * count);
    if (!block)
        return;
    memset(block, 0, stride * count);
    for (i = 0; i < count; i++)
        memp_free_pool(desc, block + i * stride);
}

void __wrap_memp_init(void)
{
    __real_memp_init();
    if (!g_extraMempInitialized) {
        g_extraMempInitialized = 1;
        expandLwipPool(&memp_PBUF_POOL, 512, PFSNET_PBUF_POOL_STRIDE);
        expandLwipPool(&memp_TCP_SEG, 256, PFSNET_SMALL_POOL_STRIDE);
        expandLwipPool(&memp_PBUF, 256, PFSNET_SMALL_POOL_STRIDE);
        expandLwipPool(&memp_TCPIP_MSG_API, 256, PFSNET_SMALL_POOL_STRIDE);
        expandLwipPool(&memp_NETBUF, 128, PFSNET_SMALL_POOL_STRIDE);
        expandLwipPool(&memp_SYS_TIMEOUT, 64, PFSNET_SMALL_POOL_STRIDE);
    }
}

struct pbuf *__wrap_pbuf_alloc(pbuf_layer l, u16 size, pbuf_type type)
{
    struct pbuf *p = __real_pbuf_alloc(l, size, type);
    if (p != NULL)
        return p;

    if (type == PBUF_POOL && g_dynamicPbufCount < 1024) {
        u32 stride = memp_PBUF_POOL.size;
        void *extra;
        if (stride < PFSNET_PBUF_POOL_STRIDE)
            stride = PFSNET_PBUF_POOL_STRIDE;
        stride = (stride + 63u) & ~63u;
        extra = memalign(64, stride);
        if (extra != NULL) {
            memset(extra, 0, stride);
            g_dynamicPbufCount++;
            memp_free_pool(&memp_PBUF_POOL, extra);
            p = __real_pbuf_alloc(l, size, type);
        }
    }
    return p;
}

/*
 * lwIP 在收到带 TCP PSH 标志的报文时会给 pbuf 打上 PBUF_FLAG_PUSH(0x01)，
 * 导致 lwip_recvfrom 每遇到 PSH 就设置 done=1 提前返回（即便 recvmbox 中仍有大量已到达的分片）。
 * macOS XNU 在每次填满发送块或 TSO 切分末尾都会密集打上 TH_PUSH，导致 PS2 侧 recv() 频繁只读几 KB 就返回。
 * 清除 PBUF_FLAG_PUSH 后：当 recvmbox 已空（rcvevent <= 0）时 lwip_recvfrom 仍会正常立即返回（不影响控制口短包），
 * 当 recvmbox 中还有排队分片（rcvevent > 0）时则一次性把队列中所有 pbuf 连续拷完。
 */
err_t __wrap_netconn_recv_tcp_pbuf(void *conn, struct pbuf **new_buf)
{
    err_t err = __real_netconn_recv_tcp_pbuf(conn, new_buf);
    if (err == 0 && new_buf != NULL && *new_buf != NULL)
        (*new_buf)->flags &= (u8)~0x01u;
    return err;
}

/*
 * 解决 EE BIOS 32 槽 SIF1 DMA 队列在突发流量下瞬时满载导致
 * fileXioWrite 的 _request_rdata (iSifSendCmd) 丢包永久死锁，以及
 * NETMAN HandleRxEvent 在 DMAC_SIF0 中断链中漏调 iSifSetDChain() 导致 SIF0 永久停机：
 *
 * 1) SifSetDma 维护 12 槽在途 DMA ID 环（仅等待 12 次前的 DMA 完成），既避免单槽等待阻塞
 *    NETMAN_RxThread 短突发，又将线程态 SIF1 占用严格限制在 <=18 槽以内，始终为 DMAC_SIF0
 *    中断态 _request_rdata (iSifSendCmd) 预留 >=14 个空闲硬件槽位。
 * 2) __wrap_iSifSendCmd 的定时器重试池将 packet 深拷贝到 64 字节对齐缓冲，防止异步重试时
 *    读取已失效的调用栈或被后续请求覆盖的包头。
 * 3) 接管 NETMAN EE 侧 rpc_server：将 HandleRxEvent 以 next=-1 挂在 DMAC_SIF0 链尾（在
 *    _SifCmdIntHandler 之后执行），凡检测到 DMAC_SIF0_CHCR.STR (0x1000c000 & 0x100) 为 0
 *    一律无条件调用 iSifSetDChain()，彻底杜绝因 RxBufferNextRdPtr 竞态或中途 SIF_DMA_INT_O
 *    造成的 SIF0 (IOP->EE) 永久停机。
 */
extern unsigned int __real_SifSetDma(SifDmaTransfer_t *sdd, s32 len);
extern unsigned int __real_SifSendCmd(int cmd, void *packet, int packet_size,
                                      void *src_extra, void *dest_extra, int size_extra);
extern unsigned int __real_iSifSendCmd(int cmd, void *packet, int packet_size,
                                       void *src_extra, void *dest_extra, int size_extra);

#define PFSNET_SIF_DMA_RING 12
static unsigned int g_sifDmaRing[PFSNET_SIF_DMA_RING];
static unsigned int g_sifDmaRingIdx;

unsigned int __wrap_SifSetDma(SifDmaTransfer_t *sdd, s32 len)
{
    unsigned int id;
    if (len == 1) {
        int old = DIntr();
        unsigned int slot = g_sifDmaRingIdx;
        unsigned int prev = g_sifDmaRing[slot];
        g_sifDmaRingIdx = (slot + 1u) % PFSNET_SIF_DMA_RING;
        if (old)
            EIntr();
        if (prev != 0) {
            while (SifDmaStat(prev) >= 0)
                nopdelay();
        }
        while ((id = __real_SifSetDma(sdd, 1)) == 0)
            nopdelay();
        old = DIntr();
        g_sifDmaRing[slot] = id;
        if (old)
            EIntr();
        return id;
    }
    while ((id = __real_SifSetDma(sdd, len)) == 0)
        nopdelay();
    return id;
}

unsigned int __wrap_SifSendCmd(int cmd, void *packet, int packet_size,
                               void *src_extra, void *dest_extra, int size_extra)
{
    unsigned int id;
    while ((id = __real_SifSendCmd(cmd, packet, packet_size, src_extra, dest_extra, size_extra)) == 0)
        nopdelay();
    return id;
}

typedef struct pfsnet_isif_retry {
    u8 pkt[64] __attribute__((aligned(64)));
    int cmd;
    int packet_size;
    void *src_extra;
    void *dest_extra;
    int size_extra;
    volatile int active;
} pfsnet_isif_retry_t;

static pfsnet_isif_retry_t g_isifRetryPool[8];

struct pfsnet_sif_cmd_data {
    void *pktbuf;
    void *unused;
    void *iopbuf;
    SifCmdHandlerData_t *sys_cmd_handlers;
    u32 nr_sys_handlers;
    SifCmdHandlerData_t *usr_cmd_handlers;
    u32 nr_usr_handlers;
    int *sregs;
};

extern struct pfsnet_sif_cmd_data _sif_cmd_data;

static int g_nmRpcSvrThreadID = -1;
static int g_nmRxThreadID = -1;
static unsigned char g_nmRpcSvrThreadStack[0x1000] __attribute__((aligned(16)));
static unsigned char g_nmRxThreadStack[0x1000] __attribute__((aligned(16)));
static volatile unsigned char g_nmIsInitialized;
static volatile unsigned char g_nmIsProcessingRx;

static struct NetManBD *g_nmFrameBufferStatus;
static struct NetManBD *g_nmRxIOPFrameBufferStatus;
static volatile unsigned short int g_nmRxBufferRdPtr;
static volatile unsigned short int g_nmRxBufferNextRdPtr;
static SifRpcDataQueue_t g_nmCbQueue;
static SifRpcServerData_t g_nmCbSrv;

/*
 * 统一在关中断 ISR 上下文中服务 DMAC_SIF0（同时处理 SIFCmd 与 NETMAN RX）：
 *
 * 根因修复：
 * 1) PS2SDK _SifCmdIntHandler (ee/kernel/src/sifcmd.c:132) 在进入 DMAC_SIF0 ISR 首行就执行 EI() 开中断，
 *    且双 handler 链中若 _SifCmdIntHandler 在前，当收到 NETMAN RX DMA (header->psize == 0) 时不会调用
 *    iSifSetDChain()；
 * 2) IOP sifman (iop/system/sifman/src/sifman.c:328-335) 会将同批的 NETMAN RX 帧与 64B SIFCmd
 *    (_request_rdata / _request_end) 串联进同一个 16-QW SIF0 硬件 FIFO；当在 ISR 末尾调用 iSifSetDChain()
 *    时，已在硬件 FIFO 中的 64B SIFCmd 会在 27ns 内瞬间传完并再次拉低 D5_CHCR.STR，随后被 EE ROM 内核
 *    在退出 DMAC 链时执行的 _sw(0x20, D_STAT) 抹掉 CIS5 中断位，导致 fileXioWrite 永久死锁。
 *
 * 此处：
 * - 全程保持关中断（直至最终 ExitHandler()），仅由单一 DMAC_SIF0 ISR 执行（绝不在定时器 ISR 中重入）；
 * - 循环排空 _sif_cmd_data.pktbuf 与 NETMAN BD，并在 iSifSetDChain() 后短轮询硬件 FIFO 瞬时落地的后续包。
 */
int __wrap__SifCmdIntHandler(int channel)
{
    int progress;
    int loops = 0;
    int r;

    (void)channel;

    do {
        progress = 0;

        if (_sif_cmd_data.pktbuf != NULL) {
            volatile SifCmdHeader_t *hdr = (volatile SifCmdHeader_t *)_sif_cmd_data.pktbuf;
            int size = hdr->psize;
            if (size != 0) {
                u128 packet[8];
                u128 *pktbuf = (u128 *)_sif_cmd_data.pktbuf;
                int pktquads = (size + 30) >> 4;
                int i = 0;
                u32 id;
                SifCmdHandlerData_t *cmd_handlers = NULL;
                SifCmdHeader_t *localHdr;

                hdr->psize = 0;
                if (pktquads > 8)
                    pktquads = 8;
                while (pktquads-- > 0) {
                    packet[i] = pktbuf[i];
                    i++;
                }

                iSifSetDChain();

                localHdr = (SifCmdHeader_t *)packet;
                id = localHdr->cid & ~SIF_CMD_ID_SYSTEM;
                if (localHdr->cid & SIF_CMD_ID_SYSTEM) {
                    if (id < _sif_cmd_data.nr_sys_handlers)
                        cmd_handlers = _sif_cmd_data.sys_cmd_handlers;
                } else {
                    if (id < _sif_cmd_data.nr_usr_handlers)
                        cmd_handlers = _sif_cmd_data.usr_cmd_handlers;
                }
                if (cmd_handlers != NULL && cmd_handlers[id].handler != NULL)
                    cmd_handlers[id].handler(packet, cmd_handlers[id].harg);

                progress = 1;
            }
        }

        if (g_nmFrameBufferStatus != NULL) {
            volatile struct NetManBD *bdRd =
                UNCACHED_SEG(&g_nmFrameBufferStatus[g_nmRxBufferRdPtr]);
            volatile struct NetManBD *bdNext =
                UNCACHED_SEG(&g_nmFrameBufferStatus[g_nmRxBufferNextRdPtr]);
            if (bdRd->length > 0 || bdNext->length > 0) {
                if (!g_nmIsProcessingRx && g_nmRxThreadID >= 0) {
                    g_nmIsProcessingRx = 1;
                    iWakeupThread(g_nmRxThreadID);
                    progress = 1;
                }
            }
        }

        if ((_lw(0x1000c000) & 0x100) == 0) {
            int spin;
            iSifSetDChain();
            for (spin = 0; spin < 24; spin++) {
                if ((_lw(0x1000c000) & 0x100) == 0) {
                    progress = 1;
                    break;
                }
                if (_sif_cmd_data.pktbuf != NULL &&
                    ((volatile SifCmdHeader_t *)_sif_cmd_data.pktbuf)->psize != 0) {
                    progress = 1;
                    break;
                }
            }
        }
    } while (progress && ++loops < 16);

    for (r = 0; r < 8; r++) {
        if (g_isifRetryPool[r].active) {
            if (__real_iSifSendCmd(g_isifRetryPool[r].cmd,
                                   g_isifRetryPool[r].pkt,
                                   g_isifRetryPool[r].packet_size,
                                   g_isifRetryPool[r].src_extra,
                                   g_isifRetryPool[r].dest_extra,
                                   g_isifRetryPool[r].size_extra) != 0) {
                g_isifRetryPool[r].active = 0;
            }
        }
    }

    ExitHandler();
    return 0;
}

static u64 pfsnetRetryISifSendCmdCb(s32 id, u64 scheduled_time, u64 actual_time, void *arg, void *pc_value)
{
    pfsnet_isif_retry_t *slot = (pfsnet_isif_retry_t *)arg;
    unsigned int did;

    (void)id;
    (void)pc_value;

    if (slot == NULL || !slot->active)
        return 0;
    did = __real_iSifSendCmd(slot->cmd, slot->pkt, slot->packet_size,
                             slot->src_extra, slot->dest_extra, slot->size_extra);
    if (did != 0) {
        slot->active = 0;
        return 0;
    }
    return ((actual_time > scheduled_time) ? (actual_time - scheduled_time) : 0ULL) + 0x8000ULL;
}

unsigned int __wrap_iSifSendCmd(int cmd, void *packet, int packet_size,
                                void *src_extra, void *dest_extra, int size_extra)
{
    unsigned int id;
    int spin;

    for (spin = 0; spin < 256; spin++) {
        id = __real_iSifSendCmd(cmd, packet, packet_size, src_extra, dest_extra, size_extra);
        if (id != 0)
            return id;
    }
    if (packet != NULL && packet_size > 0 && packet_size <= 64) {
        int i;
        for (i = 0; i < 8; i++) {
            if (!g_isifRetryPool[i].active) {
                memcpy(g_isifRetryPool[i].pkt, packet, (size_t)packet_size);
                g_isifRetryPool[i].cmd = cmd;
                g_isifRetryPool[i].packet_size = packet_size;
                g_isifRetryPool[i].src_extra = src_extra;
                g_isifRetryPool[i].dest_extra = dest_extra;
                g_isifRetryPool[i].size_extra = size_extra;
                g_isifRetryPool[i].active = 1;
                if (iSetTimerAlarm(0x8000ULL, &pfsnetRetryISifSendCmdCb, &g_isifRetryPool[i]) >= 0)
                    return 1;
                g_isifRetryPool[i].active = 0;
                break;
            }
        }
    }
    while ((id = __real_iSifSendCmd(cmd, packet, packet_size, src_extra, dest_extra, size_extra)) == 0) {
    }
    return id;
}

static void pfsnetNetManClearBufferLen(int index, void *packet, void *payload)
{
    struct NetManBD *bd;
    SifDmaTransfer_t dmat;

    bd = UNCACHED_SEG(&g_nmFrameBufferStatus[index]);
    bd->length = 0;
    bd->packet = packet;
    bd->payload = payload;

    dmat.src = (void *)&g_nmFrameBufferStatus[index];
    dmat.dest = &g_nmRxIOPFrameBufferStatus[index];
    dmat.size = sizeof(struct NetManBD);
    dmat.attr = 0;
    while (__wrap_SifSetDma(&dmat, 1) == 0)
        nopdelay();
}

int __wrap_NetManRPCAllocRxBuffers(void)
{
    int i;

    for (i = 0; i < NETMAN_RPC_BLOCK_SIZE; i++) {
        void *packet, *payload;

        if ((packet = NetManNetProtStackAllocRxPacket(NETMAN_NETIF_FRAME_SIZE, &payload)) != NULL) {
            pfsnetNetManClearBufferLen(i, packet, payload);
        } else {
            return -ENOMEM;
        }
    }
    return 0;
}

static void pfsnetNetManRxThread(void *arg)
{
    volatile struct NetManBD *bd;
    u32 PacketLength, PacketLengthAligned;
    u8 run;

    (void)arg;

    while (1) {
        void *payload, *payloadNext;
        void *packet, *packetNext;

        bd = UNCACHED_SEG(&g_nmFrameBufferStatus[g_nmRxBufferRdPtr]);

        do {
            if ((_lw(0x1000c000) & 0x100) == 0)
                SifSetDChain();
            DI();
            PacketLength = bd->length;
            if (PacketLength > 0) {
                run = 1;
            } else {
                run = 0;
                g_nmIsProcessingRx = 0;
            }
            EI();

            if (!run)
                SleepThread();
        } while (!run);

        payload = bd->payload;
        packet = bd->packet;

        while ((packetNext = NetManNetProtStackAllocRxPacket(NETMAN_NETIF_FRAME_SIZE, &payloadNext)) == NULL) {
            DelayThread(200);
        }
        g_nmRxBufferNextRdPtr = (g_nmRxBufferNextRdPtr + 1) % NETMAN_RPC_BLOCK_SIZE;
        pfsnetNetManClearBufferLen(g_nmRxBufferRdPtr, packetNext, payloadNext);
        g_nmRxBufferRdPtr = g_nmRxBufferNextRdPtr;

        PacketLengthAligned = (PacketLength + 63) & ~63;
        SifWriteBackDCache(payload, PacketLengthAligned);
        NetManNetProtStackReallocRxPacket(packet, PacketLength);
        NetManNetProtStackEnQRxPacket(packet);
    }
}

static void *pfsnetNetManEERpcHandler(int fnum, void *buffer, int NumBytes)
{
    ee_thread_t thread;
    void *result;

    (void)NumBytes;

    switch (fnum) {
        case NETMAN_EE_RPC_FUNC_INIT:
            g_nmRxIOPFrameBufferStatus = *(void **)buffer;
            if (g_nmFrameBufferStatus == NULL)
                g_nmFrameBufferStatus = memalign(64, sizeof(struct NetManBD) * NETMAN_RPC_BLOCK_SIZE);

            if (g_nmFrameBufferStatus != NULL) {
                memset(UNCACHED_SEG(g_nmFrameBufferStatus), 0, sizeof(struct NetManBD) * NETMAN_RPC_BLOCK_SIZE);
                g_nmRxBufferRdPtr = 0;
                g_nmRxBufferNextRdPtr = 0;
                g_nmIsProcessingRx = 0;

                thread.func = &pfsnetNetManRxThread;
                thread.stack = g_nmRxThreadStack;
                thread.stack_size = sizeof(g_nmRxThreadStack);
                thread.gp_reg = &_gp;
                thread.initial_priority = 0x59;
                thread.attr = thread.option = 0;

                if ((g_nmRxThreadID = CreateThread(&thread)) >= 0) {
                    StartThread(g_nmRxThreadID, NULL);
                    EnableDmac(DMAC_SIF0);
                    ((struct NetManEEInitResult *)buffer)->result = 0;
                    ((struct NetManEEInitResult *)buffer)->FrameBufferStatus = g_nmFrameBufferStatus;
                } else {
                    ((struct NetManEEInitResult *)buffer)->result = g_nmRxThreadID;
                }
            } else {
                ((struct NetManEEInitResult *)buffer)->result = -ENOMEM;
            }
            result = buffer;
            break;
        case NETMAN_EE_RPC_FUNC_DEINIT:
            if (g_nmFrameBufferStatus != NULL) {
                free(g_nmFrameBufferStatus);
                g_nmFrameBufferStatus = NULL;
            }
            result = NULL;
            break;
        case NETMAN_EE_RPC_FUNC_HANDLE_LINK_STATUS_CHANGE:
            NetManToggleGlobalNetIFLinkState(*(u32 *)buffer);
            result = NULL;
            break;
        default:
            *(int *)buffer = EINVAL;
            result = buffer;
    }

    return result;
}

static void pfsnetNetManRpcThread(void *arg)
{
    static unsigned char cb_rpc_buffer[64] __attribute__((__aligned__(64)));

    (void)arg;

    SifSetRpcQueue(&g_nmCbQueue, g_nmRpcSvrThreadID);
    SifRegisterRpc(&g_nmCbSrv, NETMAN_RPC_NUMBER, &pfsnetNetManEERpcHandler, cb_rpc_buffer, NULL, NULL, &g_nmCbQueue);
    SifRpcLoop(&g_nmCbQueue);
}

int __wrap_NetManInitRPCServer(void)
{
    ee_thread_t ThreadData;

    if (!g_nmIsInitialized) {
        ThreadData.func = &pfsnetNetManRpcThread;
        ThreadData.stack = g_nmRpcSvrThreadStack;
        ThreadData.stack_size = sizeof(g_nmRpcSvrThreadStack);
        ThreadData.gp_reg = &_gp;
        ThreadData.initial_priority = 0x57;
        ThreadData.attr = ThreadData.option = 0;

        if ((g_nmRpcSvrThreadID = CreateThread(&ThreadData)) >= 0) {
            StartThread(g_nmRpcSvrThreadID, NULL);
            g_nmIsInitialized = 1;
            return 0;
        }
        return g_nmRpcSvrThreadID;
    }
    return 0;
}

void __wrap_NetManDeinitRPCServer(void)
{
    if (g_nmIsInitialized) {
        SifRemoveRpc(&g_nmCbSrv, &g_nmCbQueue);
        SifRemoveRpcQueue(&g_nmCbQueue);
        if (g_nmRpcSvrThreadID >= 0) {
            TerminateThread(g_nmRpcSvrThreadID);
            DeleteThread(g_nmRpcSvrThreadID);
            g_nmRpcSvrThreadID = -1;
        }
        if (g_nmRxThreadID >= 0) {
            TerminateThread(g_nmRxThreadID);
            DeleteThread(g_nmRxThreadID);
            g_nmRxThreadID = -1;
        }
        g_nmIsInitialized = 0;
    }
}

#define PFSNET_SYS_MAX_MESSAGES 1024
#define PFSNET_SYS_ARCH_TIMEOUT 0xffffffffUL

typedef struct pfsnet_arch_message {
    struct pfsnet_arch_message *next;
    void *sys_msg;
} pfsnet_arch_message_t;

typedef struct pfsnet_mbox_data {
    int MessageCountSema;
    pfsnet_arch_message_t *FirstMessage;
    pfsnet_arch_message_t *LastMessage;
} pfsnet_mbox_data_t;

typedef pfsnet_mbox_data_t *pfsnet_sys_mbox_t;
typedef int pfsnet_sys_sem_t;

static pfsnet_arch_message_t g_msgPool[PFSNET_SYS_MAX_MESSAGES];
static pfsnet_arch_message_t *g_freeHead;
static int g_msgCountSema = -1;

static pfsnet_arch_message_t *tryAllocMsg(void)
{
    pfsnet_arch_message_t *message;

    if (PollSema(g_msgCountSema) == g_msgCountSema) {
        DI();
        message = g_freeHead;
        if (message != NULL)
            g_freeHead = message->next;
        EI();
    } else {
        message = NULL;
    }
    return message;
}

static pfsnet_arch_message_t *allocMsg(void)
{
    pfsnet_arch_message_t *message;

    WaitSema(g_msgCountSema);
    DI();
    message = g_freeHead;
    if (message != NULL)
        g_freeHead = message->next;
    EI();
    return message;
}

static void freeMsg(pfsnet_arch_message_t *msg)
{
    if (msg == NULL)
        return;
    DI();
    msg->next = g_freeHead;
    g_freeHead = msg;
    EI();
    SignalSema(g_msgCountSema);
}

/*
 * 修复 PS2SDK ee/kernel/src/timer.c 中 SetNextComp 与 TimerHandler_callback 的硬件竞态：
 *
 * 1) timer.c:300-304 在 current_schedule < time_now + CLOCKS_GAP (仅 2 个 T2_COUNT 计数 = 3.47us)
 *    时，先执行 SetT2_COMP((*T2_COUNT) + 2) 再执行 SetT2_MODE((*T2_MODE) & ~(1 << 11)) 清 EQUF，
 *    若写完 T2_COMP 到清 EQUF 之间已跨过 2 个计数（3.47us），会把刚刚触发的 EQUF 抹掉，导致闹钟被推迟整整
 *    一轮 16 位溢出（113.77ms）甚至与 OVFF 叠加永久卡死；
 * 2) timer.c:402 的 TimerHandler_callback 仅在 ((*T2_MODE) & (1 << 10)) != 0 (EQUF=1) 时才遍历闹钟链表，
 *    一旦线程态 iSetTimerAlarm / iReleaseTimerAlarm 关中断执行 SetNextComp 清掉 EQUF，或闹钟恰好落在
 *    0xFFFF -> 0x0000 溢出中断 (OVFF) 边界，TimerHandler_callback 会直接跳过已到期的闹钟；
 * 3) DelayThread (delaythread.c:30) 的回调 DelayThreadWakeup_callback 在 TimerHandler_callback 遍历中途
 *    提前调用 ExitHandler() (sync; ei)。
 *
 * 此处通过 wrap SetNextComp、TimerHandler_callback 与 DelayThread：
 * - __wrap_SetNextComp 始终先清 EQUF 再写 T2_COMP，将最小安全提前量提升到 32 ticks (55.5us)，并在写完
 *   T2_COMP 后二次校验 T2_COUNT 是否已逼近/越过比较值；
 * - __wrap_TimerHandler_callback 先更新溢出计数，再无条件按时间戳 (abs_cmp >= target_cmp) 触发所有已到期闹钟；
 * - 按需单次注册闹钟（零常驻周期中断开销），保障 3.65MB/s 满速吞吐与数小时连续传输不挂死。
 */
typedef struct pfsnet_counter_struct {
    struct pfsnet_counter_struct *timer_next;
    struct pfsnet_counter_struct *timer_previous;
    vu32 timer_key;
    u32 timer_mode;
    u64 timer_base_time;
    u64 timer_base_count;
    u64 timer_schedule;
    timer_alarm_handler_t callback_handler;
    void *gp_value;
    void *callback_handler_arg;
    u32 padding[3];
} pfsnet_counter_struct_t __attribute__((aligned(64)));

typedef struct pfsnet_timer_ee_global {
    vu64 timer_handled_count;
    s32 intc_handler;
    vu32 timer_counter_total;
    vu32 timer_counter_used;
    pfsnet_counter_struct_t *timer_counter_buf_free;
    pfsnet_counter_struct_t *timer_counter_buf_alarm;
    vs32 current_handling_timer_id;
} pfsnet_timer_ee_global_t;

#define PFSNET_TIMER_MODE_HANDLER 0x00000002u
#define PFSNET_PTR_TO_TIMER_ID(ptr_) ((s32)((((uiptr)(ptr_)) << 4) | ((ptr_)->timer_key)))

extern pfsnet_timer_ee_global_t g_Timer;
extern void SetT2_COMP(u32 val);
extern void SetT2_MODE(u32 val);
extern void InsertAlarm_ForTimer(pfsnet_counter_struct_t *timer_link);
extern pfsnet_counter_struct_t *UnlinkAlarm_ForTimer(pfsnet_counter_struct_t *timer_unlink);

static inline u64 pfsnetApplyOverflow(void)
{
    u64 timer_system_time_now;
    u32 mode;
    u32 low;

    low = *T2_COUNT;
    mode = *T2_MODE;
    if ((mode & (1 << 11)) != 0) {
        g_Timer.timer_handled_count += 1;
        SetT2_MODE(mode & (~(1 << 10)));
        low = *T2_COUNT;
    }
    timer_system_time_now = (g_Timer.timer_handled_count << 16) | low;
    timer_system_time_now = timer_system_time_now << ((mode & 3) << 2);
    return timer_system_time_now;
}

void __wrap_SetNextComp(u64 time_now)
{
    u64 current_schedule, next_schedule;
    pfsnet_counter_struct_t *timer_current;
    u32 shift, minGapTicks;
    u64 minGapClocks;

    (void)time_now;

    if (g_Timer.current_handling_timer_id >= 0)
        return;

    timer_current = g_Timer.timer_counter_buf_alarm;
    if (timer_current == NULL) {
        SetT2_COMP(0);
        SetT2_MODE((*T2_MODE) & (~(1 << 11)));
        return;
    }

    current_schedule = timer_current->timer_schedule + timer_current->timer_base_time - timer_current->timer_base_count;
    timer_current = timer_current->timer_next;
    while (timer_current != NULL) {
        next_schedule = timer_current->timer_schedule + timer_current->timer_base_time - timer_current->timer_base_count;
        if (next_schedule < (current_schedule + 0x200ULL))
            current_schedule = next_schedule;
        else
            break;
        timer_current = timer_current->timer_next;
    }

    shift = ((*T2_MODE) & 3) << 2;
    minGapTicks = 32u;
    minGapClocks = (u64)minGapTicks << shift;

    /* 始终先清 EQUF，再写 T2_COMP，防止写完 T2_COMP 后被随后的 SetT2_MODE 抹掉刚触发的 EQUF */
    SetT2_MODE((*T2_MODE) & (~(1 << 11)));
    time_now = pfsnetApplyOverflow();

    if (current_schedule <= (time_now + minGapClocks)) {
        SetT2_COMP(((*T2_COUNT) + minGapTicks) & 0xFFFFu);
    } else {
        u32 comp = (u32)((current_schedule >> shift) & 0xFFFFu);
        SetT2_COMP(comp);
        if ((current_schedule - time_now) < (0x8000ULL << shift)) {
            u32 cntNow = (*T2_COUNT) & 0xFFFFu;
            s16 diff = (s16)((u16)comp - (u16)cntNow);
            if (diff < (s16)minGapTicks)
                SetT2_COMP((cntNow + minGapTicks) & 0xFFFFu);
        }
    }
}

s32 __wrap_TimerHandler_callback(s32 cause, void *arg, void *addr)
{
    pfsnet_counter_struct_t *timer_current;
    pfsnet_counter_struct_t *timer_next;
    u64 target_cmp;
    u64 abs_cmp;
    u32 timer_id;
    u64 timer_schedule_next;

    (void)cause;
    (void)arg;

    abs_cmp = pfsnetApplyOverflow();
    if (((*T2_MODE) & (1 << 10)) != 0)
        SetT2_MODE((*T2_MODE) & (~(1 << 11)));

    timer_current = g_Timer.timer_counter_buf_alarm;
    while (timer_current != NULL) {
        target_cmp = timer_current->timer_schedule + timer_current->timer_base_time - timer_current->timer_base_count;
        abs_cmp = pfsnetApplyOverflow();
        if (abs_cmp < target_cmp)
            break;

        timer_next = UnlinkAlarm_ForTimer(timer_current);
        timer_id = (u32)PFSNET_PTR_TO_TIMER_ID(timer_current);
        g_Timer.current_handling_timer_id = (s32)timer_id;
        SetGP(timer_current->gp_value);
        timer_schedule_next = timer_current->callback_handler(
            (s32)timer_id,
            timer_current->timer_schedule,
            (abs_cmp + timer_current->timer_base_count) - timer_current->timer_base_time,
            timer_current->callback_handler_arg,
            addr);
        if (timer_schedule_next == 0) {
            timer_current->timer_mode &= ~PFSNET_TIMER_MODE_HANDLER;
        } else if (timer_schedule_next == (u64)-1) {
            timer_current->timer_next = g_Timer.timer_counter_buf_free;
            g_Timer.timer_counter_buf_free = timer_current;
            timer_current->timer_key = 0;
            timer_current->timer_mode = 0;
            g_Timer.timer_counter_used -= 1;
        } else {
            if (timer_schedule_next < 0x3999ULL)
                timer_schedule_next = 0x3999ULL;
            timer_current->timer_schedule += timer_schedule_next;
            InsertAlarm_ForTimer(timer_current);
        }
        timer_current = timer_next;
    }

    g_Timer.current_handling_timer_id = -1;
    __wrap_SetNextComp(pfsnetApplyOverflow());
    pfsnetApplyOverflow();
    ExitHandler();
    return 0;
}

typedef struct pfsnet_sema_wait {
    s32 tid;
    volatile int timedOut;
} pfsnet_sema_wait_t;

static u64 pfsnetWaitSemaAlarmCb(s32 id, u64 scheduled_time, u64 actual_time, void *arg, void *pc_value)
{
    pfsnet_sema_wait_t *w = (pfsnet_sema_wait_t *)arg;

    (void)id;
    (void)pc_value;

    if (w == NULL)
        return 0;
    if (iReleaseWaitThread(w->tid) >= 0) {
        w->timedOut = 1;
        return 0;
    }
    return ((actual_time > scheduled_time) ? (actual_time - scheduled_time) : 0ULL) + 0x8000ULL;
}

static int waitSemaTimeoutClocks(int sema, u64 clocks)
{
    pfsnet_sema_wait_t w;
    s32 alarmId;
    int ret;

    if (clocks < 0x4000ULL)
        clocks = 0x4000ULL;

    w.tid = GetThreadId();
    w.timedOut = 0;

    alarmId = SetTimerAlarm(clocks, &pfsnetWaitSemaAlarmCb, &w);
    if (alarmId < 0)
        return (WaitSema(sema) == sema) ? sema : -1;

    ret = WaitSema(sema);
    ReleaseTimerAlarm(alarmId);

    return (ret == sema && !w.timedOut) ? sema : -1;
}

static int waitSemaTimeoutMs(int sema, unsigned int msec)
{
    if (PollSema(sema) == sema)
        return sema;
    if (msec == 0)
        return -1;
    if (msec == UINT32_MAX)
        return (WaitSema(sema) == sema) ? sema : -1;

    return waitSemaTimeoutClocks(sema, MSec2TimerBusClock(msec));
}

s32 __wrap_DelayThread(s32 microseconds)
{
    u32 eie;
    s32 sema_id;
    ee_sema_t sema;
    u64 clocks;

    if (microseconds <= 0)
        return 0;

    __asm__ __volatile__("mfc0\t%0, $12" : "=r"(eie));
    if ((eie & 0x10000) == 0)
        return 0x80008008; /* ECPUDI */

    sema.attr = 0;
    sema.max_count = 1;
    sema.option = (u32)"PfsDelay";
    sema.init_count = 0;
    sema_id = CreateSema(&sema);
    if (sema_id < 0)
        return 0x80008003; /* ESEMAPHORE */

    clocks = ((u64)(u32)microseconds * (u64)kBUSCLK) / (1000ULL * 1000ULL);
    waitSemaTimeoutClocks(sema_id, clocks);
    DeleteSema(sema_id);
    return 0;
}

static void retrieveMbxInternal(pfsnet_sys_mbox_t mBox, pfsnet_arch_message_t **message)
{
    pfsnet_arch_message_t *nextMessage;

    DI();
    *message = mBox->FirstMessage;
    nextMessage = ((*message != NULL) && ((unsigned int)(*message)->next != 0xFFFFFFFFu))
        ? (*message)->next
        : NULL;
    if (mBox->FirstMessage == mBox->LastMessage)
        mBox->LastMessage = NULL;
    mBox->FirstMessage = nextMessage;
    EI();
}

static int receiveMbx(pfsnet_arch_message_t **message, pfsnet_sys_mbox_t *pMBox, u32 timeout)
{
    pfsnet_sys_mbox_t mBox;
    int sema;
    int result;

    if (pMBox == NULL || (mBox = *pMBox) == NULL)
        return -1;
    sema = mBox->MessageCountSema;

    if (timeout > 0)
        result = waitSemaTimeoutMs(sema, timeout);
    else
        result = WaitSema(sema);

    if (result == sema && *pMBox == mBox) {
        retrieveMbxInternal(mBox, message);
        return 0;
    }
    return -1;
}

static int pollMbx(pfsnet_arch_message_t **message, pfsnet_sys_mbox_t mBox)
{
    if (mBox != NULL && PollSema(mBox->MessageCountSema) == mBox->MessageCountSema) {
        retrieveMbxInternal(mBox, message);
        return 0;
    }
    return -1;
}

static void sendMbx(pfsnet_sys_mbox_t *mbox, pfsnet_arch_message_t *msg, void *sys_msg)
{
    pfsnet_sys_mbox_t m;

    if (mbox == NULL || (m = *mbox) == NULL) {
        freeMsg(msg);
        return;
    }
    DI();
    msg->sys_msg = sys_msg;
    msg->next = NULL;
    if (m->FirstMessage == NULL)
        m->FirstMessage = msg;
    if (m->LastMessage != NULL)
        m->LastMessage->next = msg;
    m->LastMessage = msg;
    EI();
    SignalSema(m->MessageCountSema);
}

void __wrap_sys_init(void)
{
    unsigned int i;
    ee_sema_t sema;

    sema.attr = 0;
    sema.option = (u32)"PS2IP";
    sema.init_count = PFSNET_SYS_MAX_MESSAGES;
    sema.max_count = PFSNET_SYS_MAX_MESSAGES;
    g_msgCountSema = CreateSema(&sema);

    g_freeHead = &g_msgPool[0];
    for (i = 0; i + 1 < PFSNET_SYS_MAX_MESSAGES; i++)
        g_msgPool[i].next = &g_msgPool[i + 1];
    g_msgPool[PFSNET_SYS_MAX_MESSAGES - 1].next = NULL;
}

err_t __wrap_sys_mbox_new(pfsnet_sys_mbox_t *mbox, int size)
{
    pfsnet_mbox_data_t *mBox;
    ee_sema_t sema;

    (void)size;
    *mbox = NULL;

    mBox = (pfsnet_mbox_data_t *)malloc(sizeof(pfsnet_mbox_data_t));
    if (mBox == NULL)
        return -1;

    mBox->LastMessage = NULL;
    mBox->FirstMessage = NULL;

    sema.attr = 0;
    sema.option = (u32)"PS2IP-msgcount";
    sema.init_count = 0;
    sema.max_count = PFSNET_SYS_MAX_MESSAGES;
    mBox->MessageCountSema = CreateSema(&sema);
    if (mBox->MessageCountSema < 0) {
        free(mBox);
        return -1;
    }

    *mbox = mBox;
    return 0;
}

void __wrap_sys_mbox_free(pfsnet_sys_mbox_t *pMBox)
{
    pfsnet_sys_mbox_t mBox;
    pfsnet_arch_message_t *message, *nextMessage;

    if (pMBox == NULL || (mBox = *pMBox) == NULL)
        return;

    DI();
    *pMBox = NULL;
    message = mBox->FirstMessage;
    mBox->FirstMessage = NULL;
    mBox->LastMessage = NULL;
    EI();

    while (message != NULL) {
        nextMessage = message->next;
        freeMsg(message);
        message = nextMessage;
    }

    DeleteSema(mBox->MessageCountSema);
    free(mBox);
}

err_t __wrap_sys_mbox_trypost(pfsnet_sys_mbox_t *mbox, void *sys_msg)
{
    pfsnet_arch_message_t *msg = tryAllocMsg();
    if (msg != NULL) {
        sendMbx(mbox, msg, sys_msg);
        return 0;
    }
    return -1;
}

void __wrap_sys_mbox_post(pfsnet_sys_mbox_t *mbox, void *sys_msg)
{
    pfsnet_arch_message_t *msg = allocMsg();
    if (msg != NULL)
        sendMbx(mbox, msg, sys_msg);
}

u32 __wrap_sys_arch_mbox_fetch(pfsnet_sys_mbox_t *pMBox, void **ppvMSG, u32 u32Timeout)
{
    pfsnet_arch_message_t *pmsg = NULL;
    u32 start = __wrap_sys_now();

    if (receiveMbx(&pmsg, pMBox, u32Timeout) == 0) {
        u32 elapsed = __wrap_sys_now() - start;
        if (pmsg != NULL) {
            if (ppvMSG != NULL)
                *ppvMSG = pmsg->sys_msg;
            freeMsg(pmsg);
        }
        return elapsed;
    }
    return PFSNET_SYS_ARCH_TIMEOUT;
}

u32 __wrap_sys_arch_mbox_tryfetch(pfsnet_sys_mbox_t *pMBox, void **ppvMSG)
{
    pfsnet_arch_message_t *pmsg = NULL;

    if (pollMbx(&pmsg, *pMBox) == 0) {
        if (pmsg != NULL) {
            if (ppvMSG != NULL)
                *ppvMSG = pmsg->sys_msg;
            freeMsg(pmsg);
        }
        return 0;
    }
    return PFSNET_SYS_ARCH_TIMEOUT;
}

u32 __wrap_sys_arch_sem_wait(pfsnet_sys_sem_t *sema, u32 timeout)
{
    if (timeout == 0)
        return (WaitSema(*sema) == *sema) ? 0 : PFSNET_SYS_ARCH_TIMEOUT;
    if (timeout == 1)
        return (PollSema(*sema) == *sema) ? 0 : PFSNET_SYS_ARCH_TIMEOUT;
    {
        u32 start = __wrap_sys_now();
        if (waitSemaTimeoutMs(*sema, timeout) == *sema) {
            u32 waitTime = __wrap_sys_now() - start;
            return (waitTime <= timeout) ? waitTime : timeout;
        }
        return PFSNET_SYS_ARCH_TIMEOUT;
    }
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
