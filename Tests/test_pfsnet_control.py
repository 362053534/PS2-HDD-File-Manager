"""Host socket regression for the actual server control-poll implementation.

Runs with Python + a native C compiler; no PS2 SDK emulation or hardware claim.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class TransferControlTest(unittest.TestCase):
    def test_current_tcp_stream_only(self):
        source = (ROOT / 'ps2-server/src/server.c').read_text()
        start = source.index('static uint8_t g_controlHeader[')
        end = source.index('\nstatic int handleHello(', start)
        function = source[start:end]
        harness = r'''
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>
#include "protocol.h"
#define PFSNET_RECV_MAX PFSNET_MAX_CONTROL_PAYLOAD
struct Client { int verified, socket, threadId; };
static struct Client g_client;
static int g_clientStop, g_serverStop, aborts, userAbort, tid = 7;
static int GetThreadId(void) { return tid; }
static void pfsnetFsAbortActiveTransfer(int user) { aborts++; userAbort = user; }
static int recvAll(int s, void *buf, int len) {
    int done=0;
    while(done<len) { int n=recv(s,(char*)buf+done,len-done,0); if(n<=0) return -1; done+=n; }
    return done;
}
static int emulate_empty_recv, pending_socket_error;
static ssize_t probe_recv(int s, void *buf, size_t n, int flags) {
    ssize_t result=recv(s,buf,n,flags);
    if(emulate_empty_recv && result<0 && (errno==EAGAIN || errno==EWOULDBLOCK)) {
        pending_socket_error=EWOULDBLOCK;
        errno=0;
        return 0;
    }
    return result;
}
static int __attribute__((unused)) probe_getsockopt(int s,int level,int option,void *value,socklen_t *len) {
    if(pending_socket_error && level==SOL_SOCKET && option==SO_ERROR) {
        *(int*)value=pending_socket_error; pending_socket_error=0; *len=sizeof(int); return 0;
    }
    return getsockopt(s,level,option,value,len);
}
#define recv probe_recv
#define getsockopt probe_getsockopt
'''
        cases = r'''
#undef recv
#undef getsockopt
static int checks;
#define CHECK(x) do { checks++; if (!(x)) { fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x); return 1; } } while(0)
int main(void) {
    int s[2], other[2]; PfsNetPacketHdr h = {PFSNET_CMD_PING,0,0}, out;
    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, s) == 0);
    g_client = (struct Client){1,s[0],7};
    emulate_empty_recv=1;
    pfsnetPollTransferControl();
    CHECK(!aborts && !g_clientStop);
    emulate_empty_recv=0;
    CHECK(send(s[1], &h, 5, 0) == 5);
    pfsnetPollTransferControl();
    CHECK(!aborts && g_controlHeaderUsed == 5);
    CHECK(send(s[1], ((char*)&h)+5, sizeof h-5, 0) == sizeof h-5);
    tid=8; pfsnetPollTransferControl();
    CHECK(g_controlHeaderUsed == 5);
    tid=7; pfsnetPollTransferControl();
    CHECK(!aborts && recv(s[1], &out, sizeof out, MSG_DONTWAIT) == sizeof out && !out.result);
    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, other) == 0);
    CHECK(send(other[1], &h, sizeof h, 0) == sizeof h);
    close(other[1]);
    pfsnetPollTransferControl();
    CHECK(!aborts && !g_clientStop);
    close(other[0]);
    h.command=PFSNET_CMD_PUT_END;
    CHECK(send(s[1], &h, sizeof h, 0) == sizeof h);
    pfsnetPollTransferControl();
    CHECK(!aborts && recvControlHeader(s[0], &out) == 0 && out.command==h.command);
    h.command=PFSNET_CMD_PUT_ABORT;
    CHECK(send(s[1], &h, 3, 0) == 3);
    pfsnetPollTransferControl();
    CHECK(!aborts && g_controlHeaderUsed == 3);
    CHECK(send(s[1], ((char*)&h)+3, sizeof h-3, 0) == sizeof h-3);
    pfsnetPollTransferControl();
    CHECK(aborts==1 && userAbort==1);
    CHECK(recvControlHeader(s[0], &out)==0 && out.command==h.command);
    aborts=0; close(s[1]); pfsnetPollTransferControl();
    CHECK(aborts==1 && userAbort==0 && g_clientStop);
    close(s[0]);
    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, s) == 0);
    g_client.socket=s[0]; g_clientStop=0; aborts=0;
    h.payload_length=PFSNET_RECV_MAX+1;
    CHECK(send(s[1], &h, sizeof h, 0) == sizeof h);
    pfsnetPollTransferControl();
    CHECK(aborts==1 && g_clientStop);
    close(s[0]); close(s[1]);
    CHECK(socketpair(AF_UNIX, SOCK_STREAM, 0, s) == 0);
    g_client.socket=s[0]; g_clientStop=0; aborts=0; g_controlHeaderUsed=0;
    h=(PfsNetPacketHdr){PFSNET_CMD_PING,0,0};
    while(send(s[0], &h, sizeof h, MSG_DONTWAIT) > 0) {}
    CHECK(errno==EAGAIN || errno==EWOULDBLOCK);
    CHECK(send(s[1], &h, sizeof h, 0) == sizeof h);
    pfsnetPollTransferControl();
    CHECK(aborts==1 && g_clientStop);
    close(s[0]); close(s[1]);
    printf("PASS: %d checks (fragmented PING, owner thread, unrelated FIN, END, ABORT, EOF, invalid length)\n",checks);
    return 0;
}
'''
        with tempfile.TemporaryDirectory() as directory:
            c = Path(directory) / 'control.c'
            executable = Path(directory) / 'control'
            c.write_text(harness + function + cases)
            subprocess.run(['cc', '-std=c99', '-Wall', '-Wextra', '-Werror',
                            '-I', str(ROOT / 'ps2-server/include'), str(c),
                            '-o', str(executable)], check=True)
            subprocess.run([str(executable)], check=True, timeout=5)


if __name__ == '__main__':
    unittest.main()
