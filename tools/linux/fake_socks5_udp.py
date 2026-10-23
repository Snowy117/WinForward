#!/usr/bin/env python3
"""Minimal SOCKS5 UDP ASSOCIATE echo upstream for B5 product E2E.

Relay UDP ports are allocated from 40600-40999 so they stay inside the Linux test firewall
window (40000-41000). Each datagram is echoed unchanged to the association data socket.
"""
import socket
import struct
import sys
import threading

port = int(sys.argv[1]) if len(sys.argv) > 1 else 40500
lock = threading.Lock()
next_relay_port = 40600
associations = 0
datagrams = 0


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
    global associations, datagrams, next_relay_port
    udp = None
    try:
        header = read_exactly(conn, 2)
        methods = read_exactly(conn, header[1])
        del methods
        conn.sendall(b"\x05\x00")
        request = read_exactly(conn, 4)
        if request[1] != 0x03:
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


def main():
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", port))
    server.listen(16)
    log(f"listening tcp {port}")
    while True:
        conn, addr = server.accept()
        threading.Thread(target=serve, args=(conn, addr), daemon=True).start()


if __name__ == "__main__":
    main()
