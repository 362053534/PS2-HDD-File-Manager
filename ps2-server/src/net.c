#include <stdio.h>
#include <stdlib.h>
#include <malloc.h>
#include <string.h>
#include <limits.h>
#include <stdint.h>
#include <kernel.h>
#include <kernel_util.h>
#include <sifdma.h>
#include <sifcmd.h>
#include <timer.h>
#include <timer_alarm.h>
#include <delaythread.h>
#include <debug.h>
#include <netman.h>
#include <ps2ip.h>

#include "pfsnet.h"

/* GCC14 constructor 写入 .init_array，而 PS2 crt0 只遍历 .ctors，需手动填充 */
extern void __ps2ipeeOpsInitializeImpl(void);

/*
 * PS2SDK 默认 sys_now() 为 clock() / 1000，而 _times()/clock() 用 32 位微秒计数，
 * 会在开机约 71.58 分钟（0xFFFFFFFF us）处从 4294967 跳回 0，破坏 lwIP TCP 定时器比较。
 * 此处用 64 位硬件总线计时器直接换算毫秒，保证在 49.7 天内单调递增并按 2^32 自然回绕。
 */
uint32_t __wrap_sys_now(void)
{
    return (uint32_t)(GetTimerSystemTime() / ((u64)kBUSCLK / 1000ULL));
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
 * fileXioWrite 的 _request_rdata (iSifSendCmd) 丢包永久死锁：
 *
 * IOP 侧 fileXio_Write_RPC 通过 SifRpcGetOtherData 拉取写盘数据时，向 EE 发送 SIF_CMD_RPC_RDATA
 * 并睡眠在 WaitEventFlag 上等待 EE 回复 SIF_CMD_RPC_END。但 PS2SDK ee/kernel/src/sifrpc.c 的
 * _request_rdata 在中断里调用 iSifSendCmd(SIF_CMD_RPC_END) 时未检查返回值 0。一旦 SIF1 队列满载，
 * SIF_CMD_RPC_END 被静默丢弃会导致永久死锁。通过 wrap SifSendCmd / iSifSendCmd 在返回 0 时自动
 * 自旋或通过 iSetTimerAlarm 硬件定时器重试，确保零额外阻塞开销且 SIF 命令永不丢失。
 */
extern unsigned int __real_SifSendCmd(int cmd, void *packet, int packet_size,
                                      void *src_extra, void *dest_extra, int size_extra);
extern unsigned int __real_iSifSendCmd(int cmd, void *packet, int packet_size,
                                       void *src_extra, void *dest_extra, int size_extra);

unsigned int __wrap_SifSendCmd(int cmd, void *packet, int packet_size,
                               void *src_extra, void *dest_extra, int size_extra)
{
    unsigned int id;
    while ((id = __real_SifSendCmd(cmd, packet, packet_size, src_extra, dest_extra, size_extra)) == 0)
        nopdelay();
    return id;
}

typedef struct pfsnet_isif_retry {
    int cmd;
    void *packet;
    int packet_size;
    void *src_extra;
    void *dest_extra;
    int size_extra;
    volatile int active;
} pfsnet_isif_retry_t;

static pfsnet_isif_retry_t g_isifRetryPool[8];

static u64 pfsnetISifRetryAlarmCb(s32 id, u64 scheduled_time, u64 actual_time, void *arg, void *pc_value)
{
    pfsnet_isif_retry_t *r = (pfsnet_isif_retry_t *)arg;
    (void)id;
    (void)scheduled_time;
    (void)actual_time;
    (void)pc_value;

    if (__real_iSifSendCmd(r->cmd, r->packet, r->packet_size, r->src_extra, r->dest_extra, r->size_extra) != 0) {
        r->active = 0;
        return 0;
    }
    return 0x4000;
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
    {
        int i;
        for (i = 0; i < 8; i++) {
            if (!g_isifRetryPool[i].active) {
                g_isifRetryPool[i].active = 1;
                g_isifRetryPool[i].cmd = cmd;
                g_isifRetryPool[i].packet = packet;
                g_isifRetryPool[i].packet_size = packet_size;
                g_isifRetryPool[i].src_extra = src_extra;
                g_isifRetryPool[i].dest_extra = dest_extra;
                g_isifRetryPool[i].size_extra = size_extra;
                if (iSetTimerAlarm(0x4000, &pfsnetISifRetryAlarmCb, &g_isifRetryPool[i]) >= 0)
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
 * 修复 PS2SDK WaitSemaEx (ee/kernel/src/kernel_util.c) 的超时丢失竞态：
 *
 * WaitSemaEx 先调 SetTimerAlarm（内部 EIntr 开中断）再调 WaitSema。
 * 当 tcpip_thread 在 sys_timeouts_mbox_fetch 传入短超时（如 1ms）或在 SetTimerAlarm 与 WaitSema 之间
 * 被抢占时，定时器中断在线程进入 WAIT 状态前触发，iReleaseWaitThread 对 READY 线程返回 <0 且闹钟被销毁，
 * 随后 tcpip_thread 进入 WaitSema(mbox) 永久睡眠（LWIP_TCPIP_CORE_LOCKING=1 下无人向该 mbox 投递消息），
 * 导致全栈 TCP/ARP 定时器彻底停摆、PS2 失联。
 *
 * 此处：
 * 1) 先走 PollSema 快路径，队列有包时零硬件定时器开销（避免每 GB 约 70 万次 Timer2 闹钟创建/销毁）；
 * 2) 若闹钟触发时目标线程尚未进入 WaitSema (iReleaseWaitThread < 0)，返回 0x4000 (~111us) 继续重触发，
 *    直到成功将线程从 WaitSema 唤醒或被 ReleaseTimerAlarm 取消。
 */
static u64 pfsnetWaitSemaAlarmCb(s32 id, u64 scheduled_time, u64 actual_time, void *arg, void *pc_value)
{
    s32 tid = (s32)(intptr_t)arg;
    (void)id;
    (void)scheduled_time;
    (void)actual_time;
    (void)pc_value;

    if (iReleaseWaitThread(tid) < 0)
        return 0x4000;
    return 0;
}

static int waitSemaTimeoutMs(int sema, unsigned int msec)
{
    s32 timerid;
    int ret;
    u64 clocks;

    if (PollSema(sema) == sema)
        return sema;
    if (msec == 0)
        return -1;
    if (msec == UINT32_MAX)
        return (WaitSema(sema) == sema) ? sema : -1;

    clocks = MSec2TimerBusClock(msec);
    if (clocks < 0x4000)
        clocks = 0x4000;

    timerid = SetTimerAlarm(clocks, &pfsnetWaitSemaAlarmCb, (void *)(intptr_t)GetThreadId());
    if (timerid < 0)
        return (WaitSema(sema) == sema) ? sema : -1;

    ret = WaitSema(sema);
    ReleaseTimerAlarm(timerid);
    return (ret == sema) ? sema : -1;
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
