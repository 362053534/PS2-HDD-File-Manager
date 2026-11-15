#ifndef PFSNET_DATA_IO_H
#define PFSNET_DATA_IO_H
#define PFSNET_DATA_IDLE_MS 90000u
/* Exactly bytes or -1 with errno. Progress reports only actual bytes moved.
 * Partial data is never reported as a completed ring bank. Temporarily sets nonblocking mode, restores fd flags, and never closes it. */
int pfsnetDataIoAll(int socket, void *buffer, int bytes, int sending,
                   volatile int *stop, unsigned idleMs, void (*progress)(int));
#endif
