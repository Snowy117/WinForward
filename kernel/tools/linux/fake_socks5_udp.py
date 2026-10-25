#!/usr/bin/env python3
"""Minimal SOCKS5 UDP ASSOCIATE upstream for forwarded product E2E.

Default mode echoes every datagram back on the association data socket. With
--reject it answers UDP ASSOCIATE with rep=7 (command not supported), which is
the product-level reject fixture: the relay must report association failures and
must not fall back to a direct send.

Relay UDP ports are allocated from 40600-40999 so they stay inside the Linux test
firewall window (40000-41000).
"""
import argparse
import signal
import socket
import struct
import sys
import threading

lock = threading.Lock()
next_relay_port = 40600
associations = 0
association_failures = 0
datagrams = 0
reject_associate = False


def log(message):
    print(f"[socks5-udp] {message}", flush=True)


def read_exactly(conn, size):
    data = b""
    while len(data) < size:
        chunk = conn.recv(size - len(data))
        if not chunk:
            raise ConnectionError("closed")
        data += chunk
    return data


def serve(conn, addr):
    global associations, association_failures, datagrams, next_relay_port
    udp = None
    try:
        header = read_exactly(conn, 2)
        methods = read_exactly(conn, header[1])
        del methods
        conn.sendall(b"\x05\x00")
        request = read_exactly(conn, 4)
        if reject_associate or request[1] != 0x03:
            with lock:
                association_failures += 1
            log(f"associate-failed peer={addr} rep=7")
            conn.sendall(b"\x05\x07\x00\x01\x00\x00\x00\x00\x00\x00")
            return
        if request[3] == 0x01:
            read_exactly(conn, 4)
        elif request[3] == 0x04:
            read_exactly(conn, 16)
        elif request[3] == 0x03:
            length = read_exactly(conn, 1)[0]
            read_exactly(conn, length)
        read_exactly(conn, 2)

        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        with lock:
            while True:
                candidate = next_relay_port
                next_relay_port += 1
                if next_relay_port > 40999:
                    next_relay_port = 40600
                try:
                    udp.bind(("0.0.0.0", candidate))
                    break
                except OSError:
                    continue
        udp_port = udp.getsockname()[1]
        reply = b"\x05\x00\x00\x01" + socket.inet_aton("0.0.0.0") + struct.pack("!H", udp_port)
        conn.sendall(reply)
        with lock:
            associations += 1
        log(f"associate peer={addr} relay=0.0.0.0:{udp_port}")

        def watch_control():
            try:
                while conn.recv(1):
                    pass
            except Exception:
                pass
            udp.close()

        threading.Thread(target=watch_control, daemon=True).start()
        while True:
            data, peer = udp.recvfrom(65535)
            if len(data) < 10:
                continue
            with lock:
                datagrams += 1
                number = datagrams
            log(f"datagram #{number} len={len(data)} peer={peer}")
            udp.sendto(data, peer)
    except Exception as exc:
        log(f"session {addr} ended: {exc}")
    finally:
        if udp is not None:
            udp.close()
        conn.close()


def summary(*_):
    with lock:
        log(f"summary associations={associations} failures={association_failures} datagrams={datagrams}")
    sys.exit(0)


def main():
    global reject_associate
    parser = argparse.ArgumentParser()
    parser.add_argument("port", nargs="?", type=int, default=40500)
    parser.add_argument("--reject", action="store_true", help="answer UDP ASSOCIATE with rep=7")
    args = parser.parse_args()
    reject_associate = args.reject
    signal.signal(signal.SIGTERM, summary)
    signal.signal(signal.SIGINT, summary)

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", args.port))
    server.listen(16)
    log(f"listening tcp {args.port} reject={reject_associate}")
    while True:
        conn, addr = server.accept()
        threading.Thread(target=serve, args=(conn, addr), daemon=True).start()


if __name__ == "__main__":
    main()
