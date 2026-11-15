#!/usr/bin/env python3
"""Run the actual data_io.c against native sockets and a deterministic clock.
No PS2 disk, root access or SDK is needed; this does not validate MIPS linkage.
"""
from pathlib import Path
import os, shutil, subprocess, tempfile
root = Path(__file__).resolve().parents[1]
cc = os.environ.get("CC") or shutil.which("cc")
if not cc:
    raise SystemExit("A native C compiler is required")
with tempfile.TemporaryDirectory(prefix="apa-data-io-") as folder:
    work = Path(folder)
    (work / "timer.h").write_text("#include <stdint.h>\ntypedef uint64_t u64;\n#define kBUSCLK 1000000\nu64 GetTimerSystemTime(void);\n", encoding="utf-8")
    (work / "delaythread.h").write_text("void DelayThread(unsigned);\n", encoding="utf-8")
    (work / "check.c").write_text(r"""
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
#include <signal.h>
#include <errno.h>
#include <sys/socket.h>
#include "data_io.h"
static uint64_t us;
static volatile int stop;
static int peer, mode, sum, sleeps;
uint64_t GetTimerSystemTime(void) { return us; }
void DelayThread(unsigned delay) {
    assert(g_pfsnetDataIoTrace.phase == APA_DATA_DELAY);
    us += delay; sleeps++;
    if (mode == 1 && sleeps == 3) stop = 1;
    if (mode == 2 && (sleeps == 8 || sleeps == 16)) {
        assert(write(peer, "abcdefgh", 8) == 8);
    }
}
static void progress(int n) {
    assert(n > 0 && g_pfsnetDataIoTrace.phase == APA_DATA_PROGRESS);
    assert(g_pfsnetDataIoTrace.lastResult == n);
    sum += n;
}
static void pair(int fd[2]) {
    assert(socketpair(AF_UNIX, SOCK_STREAM, 0, fd) == 0);
    us=0; stop=0; mode=0; sum=0; sleeps=0; peer=fd[1];
}
static void closePair(int fd[2]) { close(fd[0]); close(fd[1]); }
int main(void) {
    int fd[2]; char b[32]; int flags;
    setbuf(stdout, NULL);
    signal(SIGPIPE, SIG_IGN);
    pair(fd); flags=fcntl(fd[0], F_GETFL);
    assert(pfsnetDataIoAll(fd[0], b, 16, 0, &stop, 20, progress) == -1);
    assert(errno==ETIMEDOUT && us>=20000 && us<=21000 && sum==0);
    assert(g_pfsnetDataIoTrace.phase==APA_DATA_ERROR);
    assert(g_pfsnetDataIoTrace.calls==20 && g_pfsnetDataIoTrace.yields==20);
    assert(g_pfsnetDataIoTrace.moved==0 && g_pfsnetDataIoTrace.requested==16);
    assert(fcntl(fd[0], F_GETFL)==flags); closePair(fd);
    puts("PASS silent receive times out without SO_RCVTIMEO; fd flags restored");

    pair(fd); assert(write(fd[1],"abc",3)==3);
    assert(pfsnetDataIoAll(fd[0], b, 16, 0, &stop, 20, progress)==-1);
    assert(errno==ETIMEDOUT && sum==3 && !memcmp(b,"abc",3)); closePair(fd);
    puts("PASS partial receive reports progress but never completes an incomplete bank");

    pair(fd); mode=2;
    assert(pfsnetDataIoAll(fd[0], b, 16, 0, &stop, 10, progress)==16);
    assert(sum==16 && us==16000 && !memcmp(b,"abcdefghabcdefgh",16)); closePair(fd);
    puts("PASS real byte progress resets inactivity deadline; total duration may exceed it");

    pair(fd); mode=1;
    assert(pfsnetDataIoAll(fd[0], b, 16, 0, &stop, 20, progress)==-1);
    assert(errno==ECANCELED && us==3000); closePair(fd);
    puts("PASS cancellation exits a silent receive before the inactivity deadline");

    pair(fd); close(fd[1]);
    assert(pfsnetDataIoAll(fd[0], b, 16, 0, &stop, 20, progress)==-1);
    assert(errno==ECONNRESET && us==0); close(fd[0]);
    puts("PASS peer EOF returns a transport error without retrying forever");

    pair(fd);
    assert(pfsnetDataIoAll(fd[0], (void *)"hello", 5, 1, &stop, 20, progress)==5);
    assert(g_pfsnetDataIoTrace.phase==APA_DATA_DONE);
    assert(g_pfsnetDataIoTrace.calls==1 && g_pfsnetDataIoTrace.yields==0);
    assert(g_pfsnetDataIoTrace.moved==5 && g_pfsnetDataIoTrace.lastError==0);
    assert(read(fd[1],b,5)==5 && !memcmp(b,"hello",5) && sum==5 && sleeps==0); closePair(fd);
    puts("PASS successful send has an immediate fast path and exact payload");

    pair(fd); {
        int small=4096, size=4*1024*1024; char *large=calloc(1,(size_t)size);
        assert(large && setsockopt(fd[0],SOL_SOCKET,SO_SNDBUF,&small,sizeof(small))==0);
        assert(pfsnetDataIoAll(fd[0], large, size, 1, &stop, 20, progress)==-1);
        assert(errno==ETIMEDOUT && sum>0 && sum<size); free(large);
    } closePair(fd);
    puts("PASS a non-reading peer cannot block send indefinitely");

    pair(fd); us=((uint64_t)UINT32_MAX-5)*1000;
    assert(pfsnetDataIoAll(fd[0], b, 16, 0, &stop, 20, progress)==-1);
    assert(errno==ETIMEDOUT && sleeps==20); closePair(fd);
    puts("PASS inactivity deadline survives the 32-bit millisecond wrap");

    pair(fd);
    assert(pfsnetDataIoAll(fd[0],0,0,0,&stop,20,0)==0);
    assert(pfsnetDataIoAll(fd[0],0,1,0,&stop,20,0)==-1 && errno==EINVAL);
    closePair(fd); puts("PASS zero-length and invalid buffer contracts");
    return 0;
}
""", encoding="utf-8")
    subprocess.run([cc, "-std=c99", "-Wall", "-Wextra", "-Werror",
                    "-I" + str(work), "-I" + str(root / "include"),
                    str(root / "src/data_io.c"), str(work / "check.c"),
                    "-o", str(work / "check")], check=True)
    subprocess.run([str(work / "check")], check=True, timeout=10)
