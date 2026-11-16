#ifndef APANET_DIAGNOSTICS_H
#define APANET_DIAGNOSTICS_H
#include <stdint.h>

/* DIAG-5: observational only. IRQ hooks only update aligned 32-bit fields.
 * Each stage has one writer; byte totals are snapshotted with interrupts off.
 * Do not log, allocate, perform RPC or wait on a lock from hot-path hooks. */
enum { APA_RX_SLEEP, APA_RX_ALLOC, APA_RX_DMA, APA_RX_ENQUEUE };
enum { APA_IO_IDLE, APA_IO_FREE, APA_IO_RECV, APA_IO_READY,
       APA_IO_FILL, APA_IO_WRITE, APA_IO_CLOSE, APA_IO_DONE, APA_IO_ERROR };
typedef struct ApaDiagnostics {
    uint32_t sifIrqs, rxPackets, pbufMisses, rxAllocRetries;
    uint32_t clockCalls, msgUsed, msgPeak, msgWaits, msgWaiting;
    uint32_t sifKickAttempts; /* guarded restart attempts, not proof of recovery */
    uint32_t rxStage, recvStage, diskStage, active, banks;
    uint32_t sif0, dstat, rxIndex, rxLen, rxNextLen, rxBusy, cmdSize;
    uint32_t outputCalls, outputReturns, outputErrors, outputHook, txQueueLen;
    uint32_t rxPending, rxFirstPending, socketTimeoutMask, interfaceFlags, netmanLink;
    int32_t outputError, outputThreadId, rxThreadId, ioThreadId;
    uint32_t ipFound, dhcpEnabled, dhcpState;
    uint8_t address[4];
    uint64_t recvBytes, diskBytes, totalBytes;
    int32_t recvError, diskError;
} ApaDiagnostics;
extern volatile ApaDiagnostics g_apaDiag;
void apaDiagPutBegin(uint64_t total, uint64_t offset, uint32_t banks);
void apaDiagRecvResult(int result, int error);
int apaDiagWrite(int fd, const void *buffer, int bytes);
int apaDiagStart(void);
int apaDiagAttachOutput(void);
void apaDiagOutputSnapshot(ApaDiagnostics *snapshot);
void apaDiagNetSnapshot(ApaDiagnostics *snapshot);
#endif
