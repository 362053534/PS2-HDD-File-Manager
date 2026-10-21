#!/usr/bin/env python3
"""Exercise the client's RST-then-ABORT protocol on new test files only."""
import hashlib
import importlib.util
from pathlib import Path
import socket
import struct
import sys
import time
import uuid

spec = importlib.util.spec_from_file_location('probe', Path(__file__).with_name('pfsnet-transfer-probe.py'))
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)
rpc, string, exact, event = probe.rpc, probe.string, probe.exact, probe.event


def connect(host):
    control = socket.create_connection((host, 45061), timeout=10)
    control.settimeout(8)
    control.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    reply = rpc(control, 1, struct.pack('<II', 0x4e534650, 1))
    if len(reply) != 12 or struct.unpack('<II', reply[:8]) != (0x4e534650, 1):
        control.close()
        raise ValueError('invalid HELLO')
    return control


def accepted(listener, control):
    data, peer = listener.accept()
    if peer[0] != control.getpeername()[0]:
        data.close()
        raise RuntimeError('unexpected data peer')
    data.settimeout(15)
    return data


def reset(data):
    data.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack('ii', 1, 0))
    data.close()


def main():
    host = sys.argv[1]
    directory = '/pfscancel_' + uuid.uuid4().hex[:12]
    name = 'probe.bin'
    block = bytes(range(256)) * 2048
    total = 8 * 1024 * 1024
    with connect(host) as control, socket.socket() as listener:
        rpc(control, 3, string('+OPL'))
        rpc(control, 0x21, string('/') + string(directory[1:]))
        event('test_directory', directory=directory)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 512*1024)
        listener.bind((control.getsockname()[0], 45062))
        listener.listen(1)
        listener.settimeout(15)
        rpc(control, 0x30, string(directory)+string(name)+struct.pack('<QI', total, 0))
        data = accepted(listener, control)
        try:
            data.sendall(block)
            data.sendall(block)
            start = time.monotonic()
            reset(data)
            rpc(control, 0x32)
            event('put_cancel_pass', seconds=time.monotonic()-start)
        finally:
            data.close()
        rpc(control, 2)
        listing = rpc(control, 0x20, string(directory))
        if len(listing) < 4 or struct.unpack('<I', listing[:4])[0] != 0:
            raise ValueError('PUT cancellation left files in the new test directory')
        event('put_cleanup_pass')

        rpc(control, 0x30, string(directory)+string(name)+struct.pack('<QI', total, 0))
        expected = hashlib.sha256()
        with accepted(listener, control) as data:
            for _ in range(total // len(block)):
                data.sendall(block)
                expected.update(block)
            data.shutdown(socket.SHUT_WR)
        rpc(control, 0x31)
        reply = rpc(control, 0x33, string(directory)+string(name)+struct.pack('<QQ', 0, 0))
        if len(reply) != 8 or struct.unpack('<Q', reply)[0] != total:
            raise ValueError('GET length mismatch')
        data = accepted(listener, control)
        try:
            part = data.recv(65536)
            if not part:
                raise EOFError('GET ended before cancellation')
            start = time.monotonic()
            reset(data)
            rpc(control, 0x35)
            event('get_cancel_pass', seconds=time.monotonic()-start)
        finally:
            data.close()
        rpc(control, 2)
        rpc(control, 0x33, string(directory)+string(name)+struct.pack('<QQ', 0, 0))
        actual = hashlib.sha256()
        with accepted(listener, control) as data:
            done = 0
            while done < total:
                part = data.recv(min(512*1024, total-done))
                if not part:
                    raise EOFError('GET ended before full readback')
                actual.update(part)
                done += len(part)
            rpc(control, 0x34)
        if actual.digest() != expected.digest():
            raise ValueError('file changed after GET cancellation')
        event('get_after_cancel_verified', sha256=actual.hexdigest())
        rpc(control, 4)
    with connect(host) as control:
        rpc(control, 2)
        event('reconnect_pass')


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        event('failure', error=type(exc).__name__, message=str(exc))
        raise
