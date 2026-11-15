#ifndef PFSNET_DATA_IO_H
#define PFSNET_DATA_IO_H
#include <stdint.h>
#define PFSNET_DATA_IDLE_MS 90000u
/* 单个传输线程写入；只记录调用位置，不发日志、不等待、不改变收发行为。 */
enum { APA_DATA_IDLE, APA_DATA_GETFL, APA_DATA_SETFL, APA_DATA_CHECK,
       APA_DATA_SEND, APA_DATA_RECV, APA_DATA_RESULT, APA_DATA_DELAY,
       APA_DATA_PROGRESS, APA_DATA_RESTORE, APA_DATA_DONE, APA_DATA_ERROR };
typedef struct PfsnetDataIoTrace {
    uint32_t phase, calls, yields, requested, moved;
    int32_t lastResult, lastError, fdFlags;
} PfsnetDataIoTrace;
extern volatile PfsnetDataIoTrace g_pfsnetDataIoTrace;
/* Exactly bytes or -1 with errno. Progress reports only actual bytes moved.
 * Partial data is never reported as a completed ring bank. Temporarily sets nonblocking mode, restores fd flags, and never closes it. */
int pfsnetDataIoAll(int socket, void *buffer, int bytes, int sending,
                   volatile int *stop, unsigned idleMs, void (*progress)(int));
#endif
