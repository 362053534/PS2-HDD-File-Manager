#include <stdint.h>
#include <errno.h>
#include <fcntl.h>
#include <sys/socket.h>
#include <timer.h>
#include <delaythread.h>
#include "data_io.h"

volatile PfsnetDataIoTrace g_pfsnetDataIoTrace;

static uint32_t dataNowMs(void)
{
    return (uint32_t)(GetTimerSystemTime() / ((u64)kBUSCLK / 1000ULL));
}

static int dataIoLoop(int socket, void *buffer, int bytes, int sending,
                      volatile int *stop, unsigned idleMs, void (*progress)(int))
{
    char *data = (char *)buffer;
    int moved = 0;
    uint32_t lastProgress;
    g_pfsnetDataIoTrace.phase = APA_DATA_CHECK;
    lastProgress = dataNowMs();
    while (moved < bytes) {
        int n;
        g_pfsnetDataIoTrace.phase = APA_DATA_CHECK;
        if (stop != 0 && *stop) { errno = ECANCELED; return -1; }
        if ((uint32_t)(dataNowMs() - lastProgress) >= idleMs) {
            errno = ETIMEDOUT;
            return -1;
        }
        /* SO_RCVTIMEO/SO_SNDTIMEO are optional lwIP build features. Never
         * depend on them for liveness. Try immediately on the fast path;
         * only yield when no bytes can move. The caller also enables
         * nonblocking mode for the duration of this call. */
        g_pfsnetDataIoTrace.calls++;
        g_pfsnetDataIoTrace.phase = sending ? APA_DATA_SEND : APA_DATA_RECV;
        n = sending ? send(socket, data + moved, bytes - moved, MSG_DONTWAIT)
                    : recv(socket, data + moved, bytes - moved, MSG_DONTWAIT);
        g_pfsnetDataIoTrace.lastResult = n;
        g_pfsnetDataIoTrace.lastError = n < 0 ? errno : 0;
        g_pfsnetDataIoTrace.phase = APA_DATA_RESULT;
        if (n > 0) {
            moved += n;
            g_pfsnetDataIoTrace.moved = (uint32_t)moved;
            g_pfsnetDataIoTrace.phase = APA_DATA_PROGRESS;
            lastProgress = dataNowMs();
            if (progress != 0) progress(n);
        } else if (n == 0) {
            errno = sending ? EPIPE : ECONNRESET;
            return -1;
        } else if (errno == EAGAIN || errno == EWOULDBLOCK) {
            g_pfsnetDataIoTrace.yields++;
            g_pfsnetDataIoTrace.phase = APA_DATA_DELAY;
            DelayThread(1000);
        } else if (errno != EINTR) {
            return -1;
        }
    }
    return moved;
}

int pfsnetDataIoAll(int socket, void *buffer, int bytes, int sending,
                   volatile int *stop, unsigned idleMs, void (*progress)(int))
{
    int flags, result, savedError, changed;
    g_pfsnetDataIoTrace.phase = APA_DATA_IDLE;
    g_pfsnetDataIoTrace.calls = g_pfsnetDataIoTrace.yields = 0;
    g_pfsnetDataIoTrace.requested = bytes > 0 ? (uint32_t)bytes : 0;
    g_pfsnetDataIoTrace.moved = 0;
    g_pfsnetDataIoTrace.lastResult = g_pfsnetDataIoTrace.lastError = 0;
    g_pfsnetDataIoTrace.fdFlags = -1;
    if (bytes < 0 || (bytes > 0 && buffer == 0) || idleMs == 0) {
        errno = EINVAL;
        return -1;
    }
    if (bytes == 0) return 0;
    g_pfsnetDataIoTrace.phase = APA_DATA_GETFL;
    flags = fcntl(socket, F_GETFL, 0);
    g_pfsnetDataIoTrace.fdFlags = flags;
    if (flags < 0) return -1;
    changed = !(flags & O_NONBLOCK);
    g_pfsnetDataIoTrace.phase = APA_DATA_SETFL;
    if (changed && fcntl(socket, F_SETFL, flags | O_NONBLOCK) < 0) return -1;
    result = dataIoLoop(socket, buffer, bytes, sending, stop, idleMs, progress);
    savedError = errno;
    /* Restore ownership state before close/cleanup in the caller. Preserve the
     * transport error if restoring the flags also fails after a disconnect. */
    g_pfsnetDataIoTrace.phase = APA_DATA_RESTORE;
    if (changed && fcntl(socket, F_SETFL, flags) < 0 && result >= 0) return -1;
    if (result < 0) errno = savedError;
    g_pfsnetDataIoTrace.phase = result < 0 ? APA_DATA_ERROR : APA_DATA_DONE;
    return result;
}
