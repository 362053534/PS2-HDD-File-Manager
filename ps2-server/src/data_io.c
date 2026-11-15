#include <stdint.h>
#include <errno.h>
#include <fcntl.h>
#include <sys/socket.h>
#include <timer.h>
#include <delaythread.h>
#include "data_io.h"

static uint32_t dataNowMs(void)
{
    return (uint32_t)(GetTimerSystemTime() / ((u64)kBUSCLK / 1000ULL));
}

static int dataIoLoop(int socket, void *buffer, int bytes, int sending,
                      volatile int *stop, unsigned idleMs, void (*progress)(int))
{
    char *data = (char *)buffer;
    int moved = 0;
    uint32_t lastProgress = dataNowMs();
    while (moved < bytes) {
        int n;
        if (stop != 0 && *stop) { errno = ECANCELED; return -1; }
        if ((uint32_t)(dataNowMs() - lastProgress) >= idleMs) {
            errno = ETIMEDOUT;
            return -1;
        }
        /* SO_RCVTIMEO/SO_SNDTIMEO are optional lwIP build features. Never
         * depend on them for liveness. Try immediately on the fast path;
         * only yield when no bytes can move. The caller also enables
         * nonblocking mode for the duration of this call. */
        n = sending ? send(socket, data + moved, bytes - moved, MSG_DONTWAIT)
                    : recv(socket, data + moved, bytes - moved, MSG_DONTWAIT);
        if (n > 0) {
            moved += n;
            lastProgress = dataNowMs();
            if (progress != 0) progress(n);
        } else if (n == 0) {
            errno = sending ? EPIPE : ECONNRESET;
            return -1;
        } else if (errno == EAGAIN || errno == EWOULDBLOCK) {
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
    if (bytes < 0 || (bytes > 0 && buffer == 0) || idleMs == 0) {
        errno = EINVAL;
        return -1;
    }
    if (bytes == 0) return 0;
    flags = fcntl(socket, F_GETFL, 0);
    if (flags < 0) return -1;
    changed = !(flags & O_NONBLOCK);
    if (changed && fcntl(socket, F_SETFL, flags | O_NONBLOCK) < 0) return -1;
    result = dataIoLoop(socket, buffer, bytes, sending, stop, idleMs, progress);
    savedError = errno;
    /* Restore ownership state before close/cleanup in the caller. Preserve the
     * transport error if restoring the flags also fails after a disconnect. */
    if (changed && fcntl(socket, F_SETFL, flags) < 0 && result >= 0) return -1;
    if (result < 0) errno = savedError;
    return result;
}
