#!/usr/bin/env python3
"""Bounded PfsNet hardware probe. Writes only a new, uniquely named directory.

Keeps its test file for inspection; never overwrites or deletes existing data.
Socket acceptance, control frames, byte counts, and round-trip SHA256 are checked.
"""
import argparse
import datetime
import hashlib
import json
import os
import socket
import struct
import sys
import time
import uuid


def event(stage, **fields):
    print(json.dumps(dict(time=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                          stage=stage, **fields)), flush=True)


def string(value):
    data = value.encode('utf-8')
    return struct.pack('<H', len(data)) + data


def exact(sock, count):
    data = bytearray()
    while len(data) < count:
        part = sock.recv(count - len(data))
        if not part:
            raise EOFError('connection closed with %d bytes outstanding' % (count-len(data)))
        data.extend(part)
    return bytes(data)


def tcp_info(sock):
    # Darwin TCP_CONNECTION_INFO layout, verified against this Mac's SDK tcp.h.
    if sys.platform != 'darwin':
        return {}
    raw = sock.getsockopt(socket.IPPROTO_TCP, 0x106, 104)
    if len(raw) < 80:
        return {'tcp_info_length': len(raw)}
    words = struct.unpack_from('=12I', raw, 4)
    return dict(state=raw[0], rto_ms=words[2], mss=words[3], cwnd=words[5],
                peer_window=words[6], pending_bytes=words[7],
                srtt_ms=words[10], retransmit_bytes=struct.unpack_from('=Q', raw, 72)[0])


def keepalive(sock):
    # Match PfsNetClient.ConfigureControlSocket/ConfigureDataSocket.
    # Darwin's idle option was checked against the target Mac's netinet/tcp.h.
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
    idle = 0x10 if sys.platform == 'darwin' else socket.TCP_KEEPIDLE
    for option, value in ((idle, 60), (socket.TCP_KEEPINTVL, 15), (socket.TCP_KEEPCNT, 10)):
        sock.setsockopt(socket.IPPROTO_TCP, option, value)
    event('keepalive', idle_seconds=60, interval_seconds=15, retries=10)


def rpc(sock, command, payload=b''):
    start = time.monotonic()
    sock.sendall(struct.pack('<IIi', command, len(payload), 0) + payload)
    _, length, result = struct.unpack('<IIi', exact(sock, 12))
    if length > 131072:
        raise ValueError('oversized control response')
    response = exact(sock, length)
    event('rpc', command=hex(command), result=result, seconds=time.monotonic()-start)
    if result:
        raise RuntimeError('command %04x returned %d' % (command, result))
    return response


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('host')
    parser.add_argument('--partition', default='+OPL')
    parser.add_argument('--put-mib', type=int, default=0,
                        help='0 only probes HELLO/PING; positive writes a new test file')
    parser.add_argument('--ping-after-mib', type=int, default=0,
                        help='send a real control PING during PUT after this many MiB')
    args = parser.parse_args()
    if not 0 <= args.put_mib <= 8192:
        parser.error('put-mib must be between 0 and 8192')
    if args.ping_after_mib < 0 or (args.ping_after_mib and args.ping_after_mib >= args.put_mib):
        parser.error('ping-after-mib must be positive and less than put-mib, or 0')
    with socket.create_connection((args.host, 45061), timeout=10) as control:
        control.settimeout(60)
        control.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        keepalive(control)
        hello = rpc(control, 1, struct.pack('<II', 0x4e534650, 1))
        if len(hello) != 12:
            raise ValueError('invalid HELLO length')
        magic, version, features = struct.unpack('<III', hello)
        if (magic, version) != (0x4e534650, 1):
            raise ValueError('invalid HELLO identity')
        event('hello', features=hex(features))
        rpc(control, 2)
        if not args.put_mib:
            return
        rpc(control, 3, string(args.partition))
        directory = '/pfsdiag_' + uuid.uuid4().hex[:12]
        rpc(control, 0x21, string('/') + string(directory[1:]))
        name = 'probe.bin'
        size = args.put_mib * 1024 * 1024
        block = os.urandom(512 * 1024)
        expected = hashlib.sha256()
        event('test_file', partition=args.partition, directory=directory, name=name, bytes=size)
        with socket.socket() as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 512*1024)
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 512*1024)
            listener.bind((control.getsockname()[0], 45062))
            listener.listen(1)
            listener.settimeout(20)
            start = time.monotonic()
            rpc(control, 0x30, string(directory)+string(name)+struct.pack('<QI', size, 0))
            data, peer = listener.accept()
            with data:
                if peer[0] != control.getpeername()[0]:
                    raise RuntimeError('unexpected data peer')
                data.settimeout(30)
                data.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                keepalive(data)
                event('put_socket', send_buffer=data.getsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF))
                done = 0
                ping_sent = False
                last = time.monotonic()
                while done < size:
                    part = block[:min(len(block), size-done)]
                    # Distinct block prefixes make duplicated/reordered blocks detectable.
                    part = struct.pack('<Q', done) + part[8:]
                    try:
                        data.sendall(part)
                    except OSError as original:
                        event('put_failed', completed_send_bytes=done, **tcp_info(data))
                        # Ask for the server's transfer result before discarding the control channel.
                        # This test owns the new destination; incomplete PUT cannot be committed.
                        control.settimeout(5)
                        try:
                            rpc(control, 0x31)
                        except Exception as status_error:
                            event('put_failure_status', message=str(status_error))
                        raise original
                    expected.update(part)
                    done += len(part)
                    if args.ping_after_mib and not ping_sent and done >= args.ping_after_mib * 1048576:
                        # V1 dispatches control commands after PUT receives its data.
                        # Queue PING without stopping the sender to wait for its reply.
                        control.sendall(struct.pack('<IIi', 2, 0, 0))
                        ping_sent = True
                        event('ping_queued_during_put', bytes=done)
                    if time.monotonic()-last >= 5:
                        event('put_progress', bytes=done, mib_s=done/1048576/(time.monotonic()-start),
                              control_state=tcp_info(control).get('state'), **tcp_info(data))
                        last = time.monotonic()
                data.shutdown(socket.SHUT_WR)
            if ping_sent:
                _, length, result = struct.unpack('<IIi', exact(control, 12))
                if length != 0 or result != 0:
                    raise ValueError('invalid queued PING response')
                event('queued_ping_reply', result=result)
            rpc(control, 0x31)
            event('put_complete', bytes=size, seconds=time.monotonic()-start,
                  mib_s=size/1048576/(time.monotonic()-start))
            reply = rpc(control, 0x33, string(directory)+string(name)+struct.pack('<QQ', 0, 0))
            if len(reply) != 8 or struct.unpack('<Q', reply)[0] != size:
                raise ValueError('read-back length mismatch')
            data, peer = listener.accept()
            actual = hashlib.sha256()
            start = time.monotonic()
            with data:
                if peer[0] != control.getpeername()[0]:
                    raise RuntimeError('unexpected data peer')
                data.settimeout(30)
                keepalive(data)
                done = 0
                last = time.monotonic()
                while done < size:
                    part = data.recv(min(512*1024, size-done))
                    if not part:
                        raise EOFError('read-back ended at %d/%d' % (done, size))
                    actual.update(part)
                    done += len(part)
                    if time.monotonic()-last >= 5:
                        event('get_progress', bytes=done, mib_s=done/1048576/(time.monotonic()-start))
                        last = time.monotonic()
                rpc(control, 0x34)
            if actual.digest() != expected.digest():
                raise ValueError('read-back SHA256 mismatch')
            event('verified', sha256=actual.hexdigest(), bytes=size,
                  get_mib_s=size/1048576/(time.monotonic()-start))
        rpc(control, 4)


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        event('failure', error=type(exc).__name__, message=str(exc))
        raise
