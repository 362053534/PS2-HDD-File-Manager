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
#include "diagnostics.h"

extern void *_gp;
extern void NetManToggleGlobalNetIFLinkState(unsigned char state);

/* GCC14 constructor 写入 .init_array，而 PS2 crt0 只遍历 .ctors，需手动填充 */
extern void __ps2ipeeOpsInitializeImpl(void);

static void pfsnetEnsureTimerInit(void);
static void pfsnetCheckWaitSlotsIsr(void);

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
extern int _SifCmdIntHandler(int channel);

/*
 * PS2SDK 默认 sys_now() 为 clock() / 1000，而 _times()/clock() 用 32 位微秒计数，
 * 会在开机约 71.58 分钟（0xFFFFFFFF us）处从 4294967 跳回 0，破坏 lwIP TCP 定时器比较。
 * 此处用 64 位硬件总线计时器直接换算毫秒，保证在 49.7 天内单调递增并按 2^32 自然回绕。
 * 切勿在此处盲目调用 iSifSetDChain()：若在 DIntr() 下 DMAC_SIF0 刚因 INT_O 停机而
 * _SifCmdIntHandler 尚未运行，线程态提前重启 DMAC_SIF0 会覆盖 _sif_cmd_data.pktbuf！
 */
uint32_t __wrap_sys_now(void)
{
    g_apaDiag.clockCalls++;
    return (uint32_t)(GetTimerSystemTime() / ((u64)kBUSCLK / 1000ULL));
}

/*
 * 修复 PS2SDK ee/libcglue/src/lock.c 中 __retarget_lock_acquire_recursive /
 * __retarget_lock_release_recursive 的中断态竞态：
 *
 * 切记：PS2 EE (R5900) 的 COP0 Status.EIE (bit 16) 是全局 CPU 寄存器位，跨 WaitSema / SignalSema
 * 线程上下文切换时 BIOS 不会按线程保存/恢复 Status.EIE！若在 DIntr() (EIE=0) 期间调用 WaitSema 或
 * SignalSema 触发线程切换，被切入的线程（如 ioWorker 0x42 / NETMAN_TxThread 0x56）会在 EIE=0 状态
 * 下运行，且其后续 DIntr() 返回 0 不再调用 EIntr()，导致全机硬件中断（DMAC_SIF0 / INTC_TIM2）永久关闭！
 * 因此 DIntr()/EIntr() 只能包裹非阻塞的字段读写，WaitSema 与 SignalSema 必须在开中断下调用。
 */
struct pfsnet_retarget_lock {
    int32_t sem_id;
    int32_t thread_id;
    int32_t count;
};

void __wrap___retarget_lock_acquire_recursive(void *lockPtr)
{
    struct pfsnet_retarget_lock *lock = (struct pfsnet_retarget_lock *)lockPtr;
    int32_t tid;
    int old;

    if (lock == NULL)
        return;
    tid = GetThreadId();
    old = DIntr();
    if (lock->count > 0 && lock->thread_id == tid) {
        lock->count++;
        if (old)
            EIntr();
        return;
    }
    if (old)
        EIntr();

    WaitSema(lock->sem_id);

    old = DIntr();
    lock->thread_id = tid;
    lock->count = 1;
    if (old)
        EIntr();
}

void __wrap___retarget_lock_release_recursive(void *lockPtr)
{
    struct pfsnet_retarget_lock *lock = (struct pfsnet_retarget_lock *)lockPtr;
    int release = 0;
    int old;

    if (lock == NULL)
        return;
    old = DIntr();
    if (lock->count > 1) {
        lock->count--;
    } else {
        lock->count = 0;
        lock->thread_id = -1;
        release = 1;
    }
    if (old)
        EIntr();

    if (release)
        SignalSema(lock->sem_id);
}

/*
 * 解决大文件顺序传输（如 2GB+ PFS 分段/子分区切换写盘停顿）时 PS2 端死锁断线：
 *
 * 1) PS2SDK libps2ip.a 默认 PBUF_POOL_SIZE = 128，其中 NETMAN 固定占走 64 个 RX 环缓冲，
 *    仅剩 64 个 PBUF_POOL 供整个 lwIP 使用；且 MEMP_NUM_TCPIP_MSG_INPKT 仅 50（小于 64）。
 * 2) 通过 wrap memp_init 与 pbuf_alloc，在启动时利用 EE 充裕内存向 PBUF_POOL、TCPIP_MSG_INPKT
 *    等池追加 64 字节对齐缓冲（可用 RX 缓冲从 64 扩至 576+），并在 pbuf_alloc 兜底动态扩容，
 *    确保 AllocRxPacket 永不返回 NULL。
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

    g_apaDiag.pbufMisses++;
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
 * SifSetDma 返回值在 dma_count >= 0x8000 (约 6~7GB 传输量) 时 bit 31 置 1：
 *
 * 切记：EE BIOS SifSetDma 返回 (dma_count << 16) | (index << 8) | count，其中 dma_count 为
 * 16 位无符号计数。当 dma_count 累进到 0x8000 (32768) 时，返回值 >= 0x80000000u，若按有符号
 * int 判断 (oldId > 0) 会在约 6~7GB 处变为负数从而跳过 SifDmaStat 等待！
 * 因此必须使用无符号 unsigned int 并以 (oldId != 0) 判定。
 */
extern unsigned int __real_SifSetDma(SifDmaTransfer_t *sdd, s32 len);
extern unsigned int __real_SifSendCmd(int cmd, void *packet, int packet_size,
                                      void *src_extra, void *dest_extra, int size_extra);
extern unsigned int __real_iSifSendCmd(int cmd, void *packet, int packet_size,
                                       void *src_extra, void *dest_extra, int size_extra);

#define PFSNET_SIF_DMA_RING_DEPTH 6
static volatile unsigned int g_sifDmaRing[PFSNET_SIF_DMA_RING_DEPTH];
static volatile int g_sifDmaRingIdx;

unsigned int __wrap_SifSetDma(SifDmaTransfer_t *sdd, s32 len)
{
    unsigned int id;
    int slot;
    unsigned int oldId;
    int oldIntr = DIntr();

    slot = g_sifDmaRingIdx;
    g_sifDmaRingIdx = (slot + 1) % PFSNET_SIF_DMA_RING_DEPTH;
    oldId = g_sifDmaRing[slot];
    g_sifDmaRing[slot] = 0;
    if (oldIntr)
        EIntr();

    if (oldId != 0) {
        while (SifDmaStat((int)oldId) >= 0)
            nopdelay();
    }

    while ((id = __real_SifSetDma(sdd, len)) == 0)
        nopdelay();

    g_sifDmaRing[slot] = id;
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

unsigned int __wrap_iSifSendCmd(int cmd, void *packet, int packet_size,
                                void *src_extra, void *dest_extra, int size_extra)
{
    unsigned int id;

    /*
     * 切勿在此处调用 iSifSetDChain()（会与 DMAC_SIF0 INT_O 竞态覆盖 _sif_cmd_data.pktbuf），
     * 也绝不能在有限次自旋后放弃返回 0（_request_rdata 丢包会导致 IOP FILEXIO 永久死锁）。
     * DMAC_SIF1 硬件以 ~100MB/s 持续排空队列，自旋等待空槽即可在数微秒内成功。
     */
    while ((id = __real_iSifSendCmd(cmd, packet, packet_size, src_extra, dest_extra, size_extra)) == 0)
        nopdelay();
    return id;
}

static int g_nmRpcSvrThreadID = -1;
static int g_nmRxThreadID = -1;
static int g_nmSifHandlerID = -1;
static unsigned char g_nmRpcSvrThreadStack[0x1000] __attribute__((aligned(16)));
static unsigned char g_nmRxThreadStack[0x1000] __attribute__((aligned(16)));
static volatile unsigned char g_nmIsInitialized;
static volatile unsigned char g_nmIsProcessingRx;
static volatile int g_timerInited;

static struct NetManBD *g_nmFrameBufferStatus;
static struct NetManBD *g_nmRxIOPFrameBufferStatus;
static volatile unsigned short int g_nmRxBufferRdPtr;
static volatile unsigned short int g_nmRxBufferNextRdPtr;
static SifRpcDataQueue_t g_nmCbQueue;
static SifRpcServerData_t g_nmCbSrv;

#define PFSNET_NM_CLEAR_RING_DEPTH 8
static volatile unsigned int g_nmClearDmaRing[PFSNET_NM_CLEAR_RING_DEPTH];
static volatile int g_nmClearDmaIdx;

static void pfsnetNetManClearBufferLen(int index, void *packet, void *payload)
{
    struct NetManBD *bd;
    SifDmaTransfer_t dmat;
    int slot;
    unsigned int oldId;
    unsigned int id;

    bd = UNCACHED_SEG(&g_nmFrameBufferStatus[index]);
    bd->length = 0;
    bd->packet = packet;
    bd->payload = payload;

    slot = g_nmClearDmaIdx;
    g_nmClearDmaIdx = (slot + 1) % PFSNET_NM_CLEAR_RING_DEPTH;
    oldId = g_nmClearDmaRing[slot];
    if (oldId != 0) {
        while (SifDmaStat((int)oldId) >= 0)
            nopdelay();
    }

    dmat.src = (void *)&g_nmFrameBufferStatus[index];
    dmat.dest = &g_nmRxIOPFrameBufferStatus[index];
    dmat.size = sizeof(struct NetManBD);
    dmat.attr = 0;
    while ((id = __wrap_SifSetDma(&dmat, 1)) == 0)
        nopdelay();
    g_nmClearDmaRing[slot] = id;
}

/*
 * 挂在 _SifCmdIntHandler 之后（next = -1）的 DMAC_SIF0 中断处理：
 *
 * PS2SDK 原版 _SifCmdIntHandler (ee/kernel/src/sifcmd.c:121-175) 在调用 cmd_handlers[id].handler
 * (_request_rdata / _request_end，耗时 ~15us) 之前就提前执行了 iSifSetDChain()；
 * 若 IOP SIF0 队列中紧跟第二个带 INT_O 的 SIFCMD tag（如 SIF_CMD_RPC_END 紧跟 SIF_CMD_RPC_RDATA），
 * 该 tag 会在 ~0.64us 内 DMA 到 _sif_cmd_data.pktbuf，而 _SifCmdIntHandler 退出时不循环复查
 * header->psize。此处在其返回后立即复查 pktbuf->psize 并递归调用 _SifCmdIntHandler 排空，
 * 同时恢复 DChain 并唤醒 NetMan RX 线程。
 */
static s32 pfsnetNetManHandleRxEvent(s32 channel)
{
    SifCmdHeader_t *pktHdr = (SifCmdHeader_t *)_sif_cmd_data.pktbuf;
    struct NetManBD *bdRd;
    struct NetManBD *bdNext;
    int dchainStopped;

    g_apaDiag.sifIrqs++;

    while (pktHdr != NULL && pktHdr->psize != 0)
        _SifCmdIntHandler(channel);

    dchainStopped = ((_lw(0x1000c000) & 0x100) == 0);
    if (dchainStopped)
        iSifSetDChain();

    while (pktHdr != NULL && pktHdr->psize != 0)
        _SifCmdIntHandler(channel);

    if (g_nmFrameBufferStatus != NULL) {
        bdRd = UNCACHED_SEG(&g_nmFrameBufferStatus[g_nmRxBufferRdPtr]);
        bdNext = UNCACHED_SEG(&g_nmFrameBufferStatus[g_nmRxBufferNextRdPtr]);
        if (bdRd->length > 0 || bdNext->length > 0 || dchainStopped) {
            if (!g_nmIsProcessingRx && g_nmRxThreadID >= 0) {
                g_nmIsProcessingRx = 1;
                iWakeupThread(g_nmRxThreadID);
            }
        }
    }

    if (g_timerInited)
        pfsnetCheckWaitSlotsIsr();

    ExitHandler();
    return 0;
}

int __wrap_NetManRPCAllocRxBuffers(void)
{
    int i;

    for (i = 0; i < NETMAN_RPC_BLOCK_SIZE; i++) {
        void *packet, *payload;

        if ((packet = NetManNetProtStackAllocRxPacket(NETMAN_NETIF_FRAME_SIZE, &payload)) != NULL) {
            SifWriteBackDCache(payload, (NETMAN_NETIF_FRAME_SIZE + 63) & ~63);
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
            DI();
            PacketLength = bd->length;
            if (PacketLength > 0) {
                run = 1;
            } else {
                SifCmdHeader_t *pktHdr = (SifCmdHeader_t *)_sif_cmd_data.pktbuf;
                if ((_lw(0x1000c000) & 0x100) == 0 &&
                    (_lw(0x1000e010) & 0x20) == 0 &&
                    (pktHdr == NULL || pktHdr->psize == 0)) {
                    iSifSetDChain();
                }
                PacketLength = bd->length;
                if (PacketLength > 0) {
                    run = 1;
                } else {
                    run = 0;
                    g_nmIsProcessingRx = 0;
                }
            }
            EI();

            if (!run) {
                g_apaDiag.rxStage = APA_RX_SLEEP;
                SleepThread();
            }
        } while (!run);

        payload = bd->payload;
        packet = bd->packet;

        g_apaDiag.rxStage = APA_RX_ALLOC;
        while ((packetNext = NetManNetProtStackAllocRxPacket(NETMAN_NETIF_FRAME_SIZE, &payloadNext)) == NULL) {
            g_apaDiag.rxAllocRetries++;
            DelayThread(200);
        }
        SifWriteBackDCache(payloadNext, (NETMAN_NETIF_FRAME_SIZE + 63) & ~63);
        g_nmRxBufferNextRdPtr = (g_nmRxBufferNextRdPtr + 1) % NETMAN_RPC_BLOCK_SIZE;
        g_apaDiag.rxStage = APA_RX_DMA;
        pfsnetNetManClearBufferLen(g_nmRxBufferRdPtr, packetNext, payloadNext);
        g_nmRxBufferRdPtr = g_nmRxBufferNextRdPtr;

        PacketLengthAligned = (PacketLength + 63) & ~63;
        SifWriteBackDCache(payload, PacketLengthAligned);
        NetManNetProtStackReallocRxPacket(packet, PacketLength);
        g_apaDiag.rxStage = APA_RX_ENQUEUE;
        NetManNetProtStackEnQRxPacket(packet);
        g_apaDiag.rxPackets++;
    }
}

/* EE-local reads only; caller snapshots with interrupts briefly disabled.
 * Do not query NETMAN via IOP RPC here: that could hang the monitor too. */
void apaDiagNetSnapshot(ApaDiagnostics *s)
{
    SifCmdHeader_t *header = (SifCmdHeader_t *)_sif_cmd_data.pktbuf;
    s->sif0 = _lw(0x1000c000);
    s->dstat = _lw(0x1000e010);
    s->rxIndex = g_nmRxBufferRdPtr;
    s->rxThreadId = g_nmRxThreadID;
    s->rxFirstPending = (uint32_t)-1;
    s->rxPending = 0;
    apaDiagOutputSnapshot(s);
    s->rxBusy = g_nmIsProcessingRx;
    s->cmdSize = header != NULL ? header->psize : 0;
    {
        t_ip_info info;
        s->ipFound = ps2ip_getconfig("sm0", &info) > 0;
        if (s->ipFound) {
            struct ip4_addr *ip = (struct ip4_addr *)&info.ipaddr;
            s->address[0] = ip4_addr1(ip); s->address[1] = ip4_addr2(ip);
            s->address[2] = ip4_addr3(ip); s->address[3] = ip4_addr4(ip);
            s->dhcpEnabled = info.dhcp_enabled;
            s->dhcpState = info.dhcp_status;
        }
    }
    if (g_nmFrameBufferStatus != NULL) {
        volatile struct NetManBD *bd = UNCACHED_SEG(&g_nmFrameBufferStatus[g_nmRxBufferRdPtr]);
        volatile struct NetManBD *next = UNCACHED_SEG(&g_nmFrameBufferStatus[g_nmRxBufferNextRdPtr]);
        int i;
        s->rxLen = bd->length;
        s->rxNextLen = next->length;
        for (i = 0; i < NETMAN_RPC_BLOCK_SIZE; i++) {
            volatile struct NetManBD *item = UNCACHED_SEG(&g_nmFrameBufferStatus[i]);
            if (item->length > 0) {
                s->rxPending++;
                if (s->rxFirstPending == (uint32_t)-1) s->rxFirstPending = (unsigned)i;
            }
        }
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
                    /* next = -1：挂在 _SifCmdIntHandler 之后，补排空并发到达的 SIFCMD 并确保 SIF0 DChain 绝不漏开 */
                    g_nmSifHandlerID = AddDmacHandler(DMAC_SIF0, &pfsnetNetManHandleRxEvent, -1);
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
        if (g_nmSifHandlerID >= 0) {
            RemoveDmacHandler(DMAC_SIF0, g_nmSifHandlerID);
            g_nmSifHandlerID = -1;
        }
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
        int old = DIntr();
        message = g_freeHead;
        if (message != NULL) {
            g_freeHead = message->next;
            if (++g_apaDiag.msgUsed > g_apaDiag.msgPeak)
                g_apaDiag.msgPeak = g_apaDiag.msgUsed;
        }
        if (old)
            EIntr();
    } else {
        message = NULL;
    }
    return message;
}

static pfsnet_arch_message_t *allocMsg(void)
{
    pfsnet_arch_message_t *message;
    int old;

    old = DIntr();
    g_apaDiag.msgWaits++;
    g_apaDiag.msgWaiting++;
    if (old) EIntr();
    WaitSema(g_msgCountSema);
    old = DIntr();
    g_apaDiag.msgWaiting--;
    message = g_freeHead;
    if (message != NULL) {
        g_freeHead = message->next;
        if (++g_apaDiag.msgUsed > g_apaDiag.msgPeak)
            g_apaDiag.msgPeak = g_apaDiag.msgUsed;
    }
    if (old)
        EIntr();
    return message;
}

static void freeMsg(pfsnet_arch_message_t *msg)
{
    int old;

    if (msg == NULL)
        return;
    old = DIntr();
    msg->next = g_freeHead;
    g_freeHead = msg;
    if (g_apaDiag.msgUsed > 0) g_apaDiag.msgUsed--;
    if (old)
        EIntr();
    SignalSema(g_msgCountSema);
}

/*
 * 单一常驻周期硬件定时器 + VBlank 兜底（零运行时 SetTimerAlarm / ReleaseTimerAlarm）：
 *
 * PS2SDK ee/kernel/src/timer.c 的 SetNextComp 存在 `*T2_MODE & ~(1 << 11)` 未屏蔽 bit 10 (EQUF)
 * 的硬件竞态：当线程态高频调用 SetTimerAlarm / ReleaseTimerAlarm（数百万次/GB）时，若在 DIntr()
 * 临界区内恰好发生 T2_COUNT == T2_COMP，SetNextComp 会把 bit 10 写 1 清零，导致 TimerHandler_callback
 * 漏处理到期闹钟；且 iFreeTimerCounter 摘除链表头节点时未重算 T2_COMP。
 *
 * 解决方案：仅在启动时注册唯一一个返回 MSec2TimerBusClock(1) 自续期的常驻闹钟，运行期任何线程（含
 * waitSemaTimeoutMs 与 DelayThread）均不再调用 SetTimerAlarm / ReleaseTimerAlarm。
 * 同时 waitSemaTimeoutMs 严格以 WaitSema(sema) 的返回值 (ret == sema) 判定是否消费了信号量令牌，
 * 杜绝 SignalSema 与超时同窗发生时误判超时导致信号量令牌丢失。
 */
typedef struct pfsnet_wait_slot {
    volatile s32 tid;
    volatile u64 deadlineTicks;
    volatile int active;
} pfsnet_wait_slot_t;

#define PFSNET_WAIT_SLOTS 12
static pfsnet_wait_slot_t g_waitSlots[PFSNET_WAIT_SLOTS];
static s32 g_vblankHandlerId = -1;

static void pfsnetCheckWaitSlotsIsr(void)
{
    u64 nowTicks = iGetTimerSystemTime();
    int i;

    for (i = 0; i < PFSNET_WAIT_SLOTS; i++) {
        if (g_waitSlots[i].active && nowTicks >= g_waitSlots[i].deadlineTicks) {
            s32 tid = g_waitSlots[i].tid;
            if (tid >= 0 && iReleaseWaitThread(tid) >= 0)
                g_waitSlots[i].active = 0;
        }
    }
}

static u64 pfsnetTimerTickAlarmCb(s32 id, u64 scheduled_time, u64 actual_time, void *arg, void *pc_value)
{
    u64 step = MSec2TimerBusClock(2);

    (void)id;
    (void)arg;
    (void)pc_value;

    pfsnetCheckWaitSlotsIsr();

    /*
     * TimerHandler_callback 将返回值累加在 scheduled_time (timer_schedule) 而非 actual_time 上；
     * 若关中断耗时超过 1ms，仅返回固定步长会导致新 schedule <= actual_time + CLOCKS_GAP，
     * 使 SetNextComp 落入 (*T2_COUNT)+32 分支并存在清 EQUF 风险。补齐 actual_time - scheduled_time
     * 后可保证新 schedule 恒在 actual_time 之后约 2ms，SetNextComp 永远走安全分支。
     */
    if (actual_time > scheduled_time)
        step += (actual_time - scheduled_time);
    return step;
}

static s32 pfsnetVblankBackupIsr(s32 cause)
{
    (void)cause;
    pfsnetCheckWaitSlotsIsr();
    ExitHandler();
    return 0;
}

static void pfsnetEnsureTimerInit(void)
{
    int i;
    if (g_timerInited)
        return;

    StartTimerSystemTime();
    for (i = 0; i < PFSNET_WAIT_SLOTS; i++) {
        g_waitSlots[i].tid = -1;
        g_waitSlots[i].deadlineTicks = 0;
        g_waitSlots[i].active = 0;
    }
    g_timerInited = 1;

    SetTimerAlarm(MSec2TimerBusClock(1), &pfsnetTimerTickAlarmCb, NULL);
    if (g_vblankHandlerId < 0) {
        g_vblankHandlerId = AddIntcHandler(INTC_VBLANK_S, &pfsnetVblankBackupIsr, 0);
        if (g_vblankHandlerId >= 0)
            EnableIntc(INTC_VBLANK_S);
    }
}

#define PFSNET_MBOX_WAIT_SLOTS 8
static volatile int g_mboxWaitTids[PFSNET_MBOX_WAIT_SLOTS] = {
    -1, -1, -1, -1, -1, -1, -1, -1
};

int pfsnetShouldAbortMbox(int tid)
{
    if (tid < 0)
        return 0;
    if (pfsnetFsShouldAbortIoTid(tid))
        return 1;
    if (pfsnetServerShouldAbortClientTid(tid))
        return 1;
    return 0;
}

static int pfsnetEnterMboxWait(int tid)
{
    int i;
    int old = DIntr();
    for (i = 0; i < PFSNET_MBOX_WAIT_SLOTS; i++) {
        if (g_mboxWaitTids[i] < 0) {
            g_mboxWaitTids[i] = tid;
            if (old)
                EIntr();
            return i;
        }
    }
    if (old)
        EIntr();
    return -1;
}

static void pfsnetLeaveMboxWait(int slot)
{
    int old;
    if (slot < 0 || slot >= PFSNET_MBOX_WAIT_SLOTS)
        return;
    old = DIntr();
    g_mboxWaitTids[slot] = -1;
    if (old)
        EIntr();
}

void pfsnetWakeMboxWaiter(int tid)
{
    int i;
    int shouldWake = 0;
    int old;

    if (tid < 0)
        return;
    old = DIntr();
    for (i = 0; i < PFSNET_MBOX_WAIT_SLOTS; i++) {
        if (g_mboxWaitTids[i] == tid) {
            shouldWake = 1;
            break;
        }
    }
    if (old)
        EIntr();

    if (shouldWake)
        ReleaseWaitThread(tid);
}

static int waitSemaTimeoutClocks(int sema, u64 clocks)
{
    s32 tid;
    int slot = -1;
    int i;
    int ret;
    int old;
    u64 deadline;

    if (PollSema(sema) == sema)
        return sema;
    if (clocks == 0)
        return -1;

    pfsnetEnsureTimerInit();
    if (clocks < 0x4000ull)
        clocks = 0x4000ull;

    tid = GetThreadId();
    deadline = GetTimerSystemTime() + clocks;

    old = DIntr();
    for (i = 0; i < PFSNET_WAIT_SLOTS; i++) {
        if (!g_waitSlots[i].active) {
            slot = i;
            g_waitSlots[i].tid = tid;
            g_waitSlots[i].deadlineTicks = deadline;
            g_waitSlots[i].active = 1;
            break;
        }
    }
    if (old)
        EIntr();

    if (slot < 0)
        return (WaitSema(sema) == sema) ? sema : -1;

    ret = WaitSema(sema);

    old = DIntr();
    g_waitSlots[slot].active = 0;
    g_waitSlots[slot].tid = -1;
    if (old)
        EIntr();

    return (ret == sema) ? sema : -1;
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

/*
 * 接管 DelayThread：使用每线程专属信号量 + 常驻定时器槽位等待，
 * 避免 padWatchThread (DelayThread(250000)) 与 pfsnetNetManRxThread (DelayThread(200))
 * 在运行期调用内核 SetTimerAlarm 触发 T2_MODE EQUF 竞态。
 */
#define PFSNET_DELAY_SEM_SLOTS 16
static volatile s32 g_delaySemTids[PFSNET_DELAY_SEM_SLOTS];
static s32 g_delaySems[PFSNET_DELAY_SEM_SLOTS];
static int g_delaySemsInited;

static s32 getDelaySemaForThread(s32 tid)
{
    int i;
    int slot = -1;
    int old = DIntr();

    if (!g_delaySemsInited) {
        for (i = 0; i < PFSNET_DELAY_SEM_SLOTS; i++) {
            g_delaySemTids[i] = -1;
            g_delaySems[i] = -1;
        }
        g_delaySemsInited = 1;
    }
    for (i = 0; i < PFSNET_DELAY_SEM_SLOTS; i++) {
        if (g_delaySemTids[i] == tid && g_delaySems[i] >= 0) {
            if (old)
                EIntr();
            return g_delaySems[i];
        }
        if (slot < 0 && g_delaySemTids[i] < 0)
            slot = i;
    }
    if (slot >= 0)
        g_delaySemTids[slot] = tid;
    if (old)
        EIntr();

    if (slot < 0)
        return -1;
    if (g_delaySems[slot] < 0) {
        ee_sema_t sem;
        memset(&sem, 0, sizeof(sem));
        sem.init_count = 0;
        sem.max_count = 1;
        g_delaySems[slot] = CreateSema(&sem);
        if (g_delaySems[slot] < 0) {
            g_delaySemTids[slot] = -1;
            return -1;
        }
    }
    return g_delaySems[slot];
}

s32 __wrap_DelayThread(s32 usec)
{
    s32 tid;
    s32 sem;
    u64 clocks;

    if (usec <= 0)
        return 0;
    tid = GetThreadId();
    sem = getDelaySemaForThread(tid);
    if (sem < 0) {
        u64 start = GetTimerSystemTime();
        clocks = USec2TimerBusClock((u32)usec);
        while ((GetTimerSystemTime() - start) < clocks)
            nopdelay();
        return 0;
    }
    while (PollSema(sem) == sem) {
    }
    clocks = USec2TimerBusClock((u32)usec);
    waitSemaTimeoutClocks(sem, clocks);
    return 0;
}

static void retrieveMbxInternal(pfsnet_sys_mbox_t mBox, pfsnet_arch_message_t **message)
{
    pfsnet_arch_message_t *nextMessage;
    int old = DIntr();

    *message = mBox->FirstMessage;
    nextMessage = ((*message != NULL) && ((unsigned int)(*message)->next != 0xFFFFFFFFu))
        ? (*message)->next
        : NULL;
    if (mBox->FirstMessage == mBox->LastMessage)
        mBox->LastMessage = NULL;
    mBox->FirstMessage = nextMessage;
    if (old)
        EIntr();
}

static int receiveMbx(pfsnet_arch_message_t **message, pfsnet_sys_mbox_t *pMBox, u32 timeout)
{
    pfsnet_sys_mbox_t mBox;
    int sema;
    int result;
    int slot;
    int tid;

    if (pMBox == NULL || (mBox = *pMBox) == NULL)
        return -1;
    sema = mBox->MessageCountSema;

    if (PollSema(sema) == sema) {
        if (*pMBox == mBox) {
            retrieveMbxInternal(mBox, message);
            return 0;
        }
        return -1;
    }

    tid = GetThreadId();
    if (pfsnetShouldAbortMbox(tid))
        return -1;

    slot = pfsnetEnterMboxWait(tid);
    if (timeout > 0)
        result = waitSemaTimeoutMs(sema, timeout);
    else
        result = WaitSema(sema);
    pfsnetLeaveMboxWait(slot);

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
    int old;

    if (mbox == NULL || (m = *mbox) == NULL) {
        freeMsg(msg);
        return;
    }
    old = DIntr();
    msg->sys_msg = sys_msg;
    msg->next = NULL;
    if (m->FirstMessage == NULL)
        m->FirstMessage = msg;
    if (m->LastMessage != NULL)
        m->LastMessage->next = msg;
    m->LastMessage = msg;
    if (old)
        EIntr();
    SignalSema(m->MessageCountSema);
}

void __wrap_sys_init(void)
{
    unsigned int i;
    ee_sema_t sema;

    pfsnetEnsureTimerInit();

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
    int old;

    if (pMBox == NULL || (mBox = *pMBox) == NULL)
        return;

    old = DIntr();
    *pMBox = NULL;
    message = mBox->FirstMessage;
    mBox->FirstMessage = NULL;
    mBox->LastMessage = NULL;
    if (old)
        EIntr();

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
    if (timeout == 0) {
        int result;
        result = WaitSema(*sema);
        return (result == *sema) ? 0 : PFSNET_SYS_ARCH_TIMEOUT;
    }
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

/* 启动轮询不能等待依赖闹钟唤醒的信号量，否则外层超时也会一起失效。
 * 仅降低当前线程优先级并读取计数时钟；网络线程仍可抢占处理 DHCP。
 * 该路径只供启动使用，传输中的 DelayThread/定时器实现保持不变。 */
static int sleepMs(unsigned int msec)
{
    int tid, oldPrio, waitPrio, result = 0;
    ee_thread_status_t status;
    u64 start, ticks;

    if (msec == 0)
        return 0;
    tid = GetThreadId();
    memset(&status, 0, sizeof(status));
    if (ReferThreadStatus(tid, &status) < 0 || status.current_priority <= 0)
        return -1;
    oldPrio = status.current_priority;
    waitPrio = oldPrio > ETH_TCPIP_PRIO + 8 ? oldPrio : ETH_TCPIP_PRIO + 8;
    if (ChangeThreadPriority(tid, waitPrio) < 0)
        return -1;

    start = GetTimerSystemTime();
    ticks = (u64)msec * ((u64)kBUSCLK / 1000ULL);
    while ((GetTimerSystemTime() - start) < ticks) {
        if (RotateThreadReadyQueue(waitPrio) < 0) {
            result = -1;
            break;
        }
    }
    if (ChangeThreadPriority(tid, oldPrio) < 0)
        return -1;
    return result;
}

/* 启动每秒检查一次，最多 30 轮；休眠不依赖闹钟回调。计数时钟仍须正常运行。 */
static int WaitValidNetState(int (*checkingFunction)(void))
{
    int retry_cycles;

    for (retry_cycles = 0; checkingFunction() == 0; retry_cycles++) {
        if (retry_cycles >= 30)
            return -1;
        if (sleepMs(1000) < 0) {
            scr_printf("WARN: network poll scheduler failed\n");
            return -1;
        }
    }
    return 0;
}

static int g_lastLinkUp;

static int ethGetLinkStatus(void)
{
    /* NETMAN 注册网络栈后会补发链路状态；该读取仅访问 EE 缓存。
     * 同步 Ioctl 可能卡在 IOP RPC 内，使外层 30 次重试永远无法执行。 */
    int up = NetManGetGlobalNetIFLinkState() != 0;

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

    pfsnetEnsureTimerInit();

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

    if (WaitValidNetState(&ethGetLinkStatus) < 0) {
        scr_printf("WARN: link timeout (no carrier)\n");
        return -1;
    }
    /* 自动协商模式设置失败时返回，不能在无界 do/while 中反复重试。 */
    if (ethApplyNetIFConfig(NETMAN_NETIF_ETH_LINK_MODE_AUTO) != 0) {
        scr_printf("WARN: link mode setup failed\n");
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
