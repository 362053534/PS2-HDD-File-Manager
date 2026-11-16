#include <stdio.h>
#include <string.h>
#include <stdarg.h>
#include <kernel.h>
#include <timer.h>
#include <delaythread.h>
#include <debug.h>
#include <fileXio_rpc.h>
#include "diagnostics.h"
#include "data_io.h"

volatile ApaDiagnostics g_apaDiag;
extern void *_gp;
static unsigned char g_diagStack[0x3000] __attribute__((aligned(16)));
static int g_diagStarted;

void apaDiagPutBegin(uint64_t total, uint64_t offset, uint32_t banks)
{
    int old = DIntr();
    g_apaDiag.totalBytes = total;
    g_apaDiag.recvBytes = g_apaDiag.diskBytes = offset;
    g_apaDiag.banks = banks;
    g_apaDiag.recvError = g_apaDiag.diskError = 0;
    g_apaDiag.recvStage = g_apaDiag.diskStage = APA_IO_IDLE;
    g_apaDiag.active = 1;
    if (old) EIntr();
}

void apaDiagRecvResult(int result, int error)
{
    if (result > 0)
        g_apaDiag.recvBytes += (unsigned)result;
    else {
        g_apaDiag.recvError = result < 0 ? error : 0;
        g_apaDiag.recvStage = APA_IO_ERROR;
    }
}

int apaDiagWrite(int fd, const void *buffer, int bytes)
{
    int result;
    g_apaDiag.diskStage = APA_IO_WRITE;
    result = fileXioWrite(fd, buffer, bytes);
    if (result > 0) {
        g_apaDiag.diskBytes += (unsigned)result;
        g_apaDiag.diskStage = APA_IO_READY;
    } else {
        g_apaDiag.diskError = result;
        g_apaDiag.diskStage = APA_IO_ERROR;
    }
    return result;
}

static const char *ioName(uint32_t stage)
{
    static const char *names[] = {"idle", "wait-free", "recv", "ready",
        "wait-filled", "write", "close", "done", "error"};
    return stage < sizeof(names)/sizeof(names[0]) ? names[stage] : "?";
}
static const char *dataName(uint32_t phase)
{
    static const char *names[] = {"idle", "getfl", "setfl", "check", "send",
        "recv", "result", "delay", "progress", "restore", "done", "error"};
    return phase < sizeof(names)/sizeof(names[0]) ? names[phase] : "?";
}
static const char *rxName(uint32_t stage)
{
    static const char *names[] = {"sleep", "alloc", "dma-clear", "enqueue"};
    return stage < sizeof(names)/sizeof(names[0]) ? names[stage] : "?";
}

static void diagLine(int row, const char *format, ...)
{
    char line[71];
    va_list args;
    va_start(args, format);
    vsnprintf(line, sizeof(line), format, args);
    va_end(args);
    scr_setXY(4, row);
    scr_printf("%-70s", line);
}

/* No thread manipulation: sample status using local EE kernel syscalls.
 * Priority identifies roles in the canonical NETMAN/ps2ip stack. */
static int findThread(int priority)
{
    int id;
    ee_thread_status_t status;
    for (id = 1; id < 256; id++) {
        if (ReferThreadStatus(id, &status) >= 0 &&
            status.initial_priority == priority)
            return id;
    }
    return -1;
}

static void threadLine(int row, const char *name, int *id, int priority,
                       ee_thread_status_t *sample)
{
    ee_thread_status_t status;
    memset(sample, 0, sizeof(*sample));
    if (*id < 0 && priority >= 0) *id = findThread(priority);
    if (*id >= 0 && ReferThreadStatus(*id, &status) >= 0) {
        *sample = status;
        diagLine(row, "%s tid:%d state:%x wait:%u id:%u wake:%u", name, *id,
                 (unsigned)status.status, (unsigned)status.waitType,
                 (unsigned)status.waitId, (unsigned)status.wakeupCount);
    } else {
        *id = -1;
        diagLine(row, "%s: not available", name);
    }
}

/* TV-safe area: columns 4..73, rows 6..25 (leave overscan margins).
 * Fixed rows: no scrolling, file writes, network requests or IOP RPC here.
 * Ages mean time since a sampled counter last advanced, not a timeout verdict.
 * If HB freezes too, thread starvation/timer/interrupt failure remain possible. */
static void diagThread(void *arg)
{
    ApaDiagnostics s, held;
    PfsnetDataIoTrace data, heldData;
    unsigned beat = 0, rxAge = 0, diskAge = 0, pktAge = 0;
    uint64_t lastRx = 0, lastDisk = 0;
    uint32_t lastPkt = 0, previous = 0;
    int ioTid = -1, coreTid = -1, rxTid = -1;
    int heldValid = 0, stallArmed = 0;
    unsigned heldUptime = 0;
    ee_thread_status_t liveThreads[3], heldThreads[3];
    int heldIds[3];
    (void)arg;
    for (;;) {
        uint32_t now = (uint32_t)(GetTimerSystemTime() / ((u64)kBUSCLK / 1000));
        unsigned elapsed = previous ? now - previous : 0;
        int captureStall = 0;
        int old = DIntr();
        s = g_apaDiag;
        data = g_pfsnetDataIoTrace;
        apaDiagNetSnapshot(&s);
        if (old) EIntr();
        previous = now;
        rxAge = s.recvBytes != lastRx || !s.active ? 0 : rxAge + elapsed;
        diskAge = s.diskBytes != lastDisk || !s.active ? 0 : diskAge + elapsed;
        pktAge = s.rxPackets != lastPkt ? 0 : pktAge + elapsed;
        lastRx = s.recvBytes; lastDisk = s.diskBytes; lastPkt = s.rxPackets;
        if (rxAge == 0 || diskAge == 0) stallArmed = 0;
        if (s.active && rxAge >= 10000 && diskAge >= 10000 && !stallArmed) {
            held = s;
            heldData = data;
            heldUptime = now/1000;
            heldValid = stallArmed = captureStall = 1;
        }
        int savedX = scr_getX(), savedY = scr_getY();
        diagLine(6, "DIAG-9 HB:%u up:%us PUT:%u banks:%u",
                   ++beat, now/1000, s.active, s.banks);
        diagLine(7, "RX:%u age:%us SIF:%u stage:%s  ",
                   s.rxPackets, pktAge/1000, s.sifIrqs, rxName(s.rxStage));
        diagLine(8, "pbuf:%u retry:%u clock:%u SIFkick:%u",
                   s.pbufMisses, s.rxAllocRetries, s.clockCalls, s.sifKickAttempts);
        diagLine(9, "msg:%u/1024 peak:%u calls:%u waiting:%u",
                   s.msgUsed, s.msgPeak, s.msgWaits, s.msgWaiting);
        diagLine(10, "NET:%u/%u MiB age:%us %s err:%d   ",
                   (unsigned)(s.recvBytes>>20), (unsigned)(s.totalBytes>>20),
                   rxAge/1000, ioName(s.recvStage), (int)s.recvError);
        diagLine(11, "HDD:%u/%u MiB age:%us %s err:%d   ",
                   (unsigned)(s.diskBytes>>20), (unsigned)(s.totalBytes>>20),
                   diskAge/1000, ioName(s.diskStage), (int)s.diskError);
        diagLine(12, "IO:%s calls:%u yield:%u n:%d e:%d bytes:%u fl:%x",
                 dataName(data.phase), data.calls, data.yields, (int)data.lastResult,
                 (int)data.lastError, data.moved, (unsigned)data.fdFlags);
        diagLine(13, "LIVE IP:%u.%u.%u.%u found:%u dhcp:%u state:%u",
                 (unsigned)s.address[0], (unsigned)s.address[1],
                 (unsigned)s.address[2], (unsigned)s.address[3],
                 s.ipFound, s.dhcpEnabled, s.dhcpState);
        diagLine(14, "OUT hook:%u calls:%u done:%u queue:%u err:%d", s.outputHook,
                 s.outputCalls, s.outputReturns, s.txQueueLen, (int)s.outputError);
        ioTid = s.ioThreadId > 0 ? s.ioThreadId : -1;
        rxTid = s.rxThreadId;
        threadLine(15, "IO", &ioTid, -1, &liveThreads[0]);
        threadLine(16, "CORE", &coreTid, 0x58, &liveThreads[1]);
        threadLine(17, "RX", &rxTid, -1, &liveThreads[2]);
        if (captureStall) {
            memcpy(heldThreads, liveThreads, sizeof(heldThreads));
            heldIds[0] = ioTid; heldIds[1] = coreTid; heldIds[2] = rxTid;
        }
        if (heldValid) {
            diagLine(18, "SNAP @%us NET:%s HDD:%s RX:%s", heldUptime,
                     ioName(held.recvStage), ioName(held.diskStage), rxName(held.rxStage));
            diagLine(19, "SNAP IO:%s calls:%u yield:%u n:%d e:%d bytes:%u",
                     dataName(heldData.phase), heldData.calls, heldData.yields,
                     (int)heldData.lastResult, (int)heldData.lastError, heldData.moved);
            diagLine(20, "SNAP OUT:%u/%u R:%u S:%08x D:%08x K:%u",
                     held.outputCalls, held.outputReturns, held.rxPending,
                     (unsigned)held.sif0, (unsigned)held.dstat, held.sifKickAttempts);
        } else {
            diagLine(18, "SNAP: no 10-second NET+HDD stall observed yet");
            diagLine(19, "");
            diagLine(20, "");
        }
        {
            int i;
            static const char *names[] = {"IO", "CORE", "RX"};
            for (i = 0; i < 3; i++) {
                if (heldValid)
                    diagLine(21+i, "SNAP %s tid:%d state:%x wait:%u id:%u wake:%u",
                             names[i], heldIds[i], (unsigned)heldThreads[i].status,
                             (unsigned)heldThreads[i].waitType, (unsigned)heldThreads[i].waitId,
                             (unsigned)heldThreads[i].wakeupCount);
                else
                    diagLine(21+i, "");
            }
        }
        diagLine(24, "RING pending:%u first:%d NMlink:%u flags:%02x SOtimeo:%u",
                 s.rxPending, (int)s.rxFirstPending, s.netmanLink,
                 (unsigned)s.interfaceFlags, s.socketTimeoutMask);
        diagLine(25, "BD:%u len:%u/%u busy:%u cmd:%u SIF:%03x DS:%08x",
                 s.rxIndex, s.rxLen, s.rxNextLen, s.rxBusy, s.cmdSize,
                 (unsigned)s.sif0, (unsigned)s.dstat);
        scr_setXY(savedX, savedY);
        DelayThread(1000000);
    }
}

int apaDiagStart(void)
{
    ee_thread_t th;
    int tid;
    if (g_diagStarted) return 0;
    memset(&th, 0, sizeof(th));
    th.func = &diagThread;
    th.stack = g_diagStack;
    th.stack_size = sizeof(g_diagStack);
    th.gp_reg = &_gp;
    th.initial_priority = 0x78;
    tid = CreateThread(&th);
    if (tid < 0) return tid;
    {
        int rc = StartThread(tid, NULL);
        if (rc < 0) { DeleteThread(tid); return rc; }
    }
    g_diagStarted = 1;
    return 0;
}
