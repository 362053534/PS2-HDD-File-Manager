#include <stdint.h>
#include <kernel.h>
#include <tcpip.h>
#include <ps2ip.h>
#include <netman.h>
#include "diagnostics.h"

static struct netif *g_outputInterface;
static netif_linkoutput_fn g_originalOutput;

/* Observe the actual function-pointer path, not optional linker wrapping.
 * Forward the exact packet and result; do not dequeue, allocate or do RPC. */
static err_t diagnosticOutput(struct netif *interface, struct pbuf *packet)
{
    err_t result;
    g_apaDiag.outputCalls++;
    g_apaDiag.outputThreadId = GetThreadId();
    result = g_originalOutput(interface, packet);
    g_apaDiag.outputReturns++;
    g_apaDiag.outputError = result;
    if (result != 0) g_apaDiag.outputErrors++;
    return result;
}

int apaDiagAttachOutput(void)
{
    struct netif *interface = netif_find("sm0");
    int old;
    if (interface == 0 || interface->linkoutput == 0) return -1;
    old = DIntr();
    if (interface->linkoutput != diagnosticOutput) {
        g_originalOutput = interface->linkoutput;
        g_outputInterface = interface;
        interface->linkoutput = diagnosticOutput;
    }
    g_apaDiag.outputHook = 1;
    if (old) EIntr();
    return 0;
}

/* Called with interrupts briefly disabled; Next only peeks at the head in
 * the canonical NETMAN API, never dequeues it or sends an IOP request. */
void apaDiagOutputSnapshot(ApaDiagnostics *snapshot)
{
    void *payload = 0;
    int length = NetManTxPacketNext(&payload);
    snapshot->txQueueLen = length > 0 ? (unsigned)length : 0;
    snapshot->outputHook = g_outputInterface != 0 &&
                          g_outputInterface->linkoutput == diagnosticOutput;
    snapshot->interfaceFlags = g_outputInterface != 0 ? g_outputInterface->flags : 0;
    snapshot->netmanLink = NetManGetGlobalNetIFLinkState();
}
