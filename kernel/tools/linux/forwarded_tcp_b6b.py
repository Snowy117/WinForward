#!/usr/bin/env python3
"""B6b external Linux client: same-source-port multi-destination and hot-swap TCP checks.

multi:  two targets are connected concurrently from one bound source port; each payload is echoed
        back and reported. This exercises per-ingress / per-L return disambiguation.
swap:   connection 1 is established and echoed, then (after --gap seconds) connection 2 is opened
        and echoed. The suite changes the forwarded blob between the two connections; the old
        entry must keep its upstream and the new one must use the new rule.

Targets may be IPv4 (192.168.100.99:40250) or scoped link-local IPv6 (fe80::99%eth0:40250).
--bind-address is repeatable: one value applies to every target, or one value per --target
(needed for cross-ingress connections on different Linux interfaces).
"""
import argparse
import hashlib
import ipaddress
import socket
import sys
import time


def payload_for(tag, size):
    return bytes(((tag * 53 + index * 17 + 11) & 0xFF) for index in range(size))


def parse_endpoint(text):
    host, port = text.rsplit(":", 1)
    if host.startswith("[") and host.endswith("]"):
        host = host[1:-1]
    return host, int(port)


def address_version(host):
    try:
        return ipaddress.ip_address(host.split("%")[0]).version
    except ValueError:
        return 4


def connect(target, bind_address, source_port, timeout):
    host, port = parse_endpoint(target)
    family = socket.AF_INET6 if address_version(host) == 6 else socket.AF_INET
    sock = socket.socket(family, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
    except OSError:
        pass
    if not bind_address:
        bind_address = "::" if family == socket.AF_INET6 else "0.0.0.0"
    bind_sockaddr = socket.getaddrinfo(bind_address, source_port, family, socket.SOCK_STREAM)[0][4]
    sock.bind(bind_sockaddr)
    sock.settimeout(timeout)
    sock.connect(socket.getaddrinfo(host, port, family, socket.SOCK_STREAM)[0][4])
    return sock


def exchange(sock, payload):
    sock.sendall(payload)
    received = b""
    while len(received) < len(payload):
        chunk = sock.recv(len(payload) - len(received))
        if not chunk:
            break
        received += chunk
    return received


def report_lines(results, verify, timeout):
    for line in results:
        print(f"[client] {line}", flush=True)
        if verify:
            host, port = parse_endpoint(verify)
            with socket.create_connection((host, port), timeout=timeout) as sink:
                sink.sendall((line + "\n").encode())
    return 0 if all("ok=1" in line for line in results) else 1


def result_line(target, sock, payload, received):
    expected = hashlib.sha256(payload).hexdigest()
    actual = hashlib.sha256(received).hexdigest()
    local = sock.getsockname()
    ok = received == payload and actual == expected
    return (f"target={target} local={local[0]}:{local[1]} bytes={len(received)} "
            f"sha={actual} ok={1 if ok else 0}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("multi", "swap"), required=True)
    parser.add_argument("--target", action="append", required=True)
    parser.add_argument("--bind-address", action="append", default=[],
                        help="local bind address (repeatable: one for all targets or one per target)")
    parser.add_argument("--source-port", type=int, required=True)
    parser.add_argument("--second-source-port", type=int, default=0)
    parser.add_argument("--payload-size", type=int, default=2048)
    parser.add_argument("--gap", type=float, default=2.0)
    parser.add_argument("--timeout", type=float, default=10.0)
    parser.add_argument("--verify", default="")
    args = parser.parse_args()

    bind_addresses = args.bind_address or [""]
    if len(bind_addresses) != 1 and len(bind_addresses) != len(args.target):
        print("--bind-address must be given once or once per --target", file=sys.stderr)
        return 2

    def bind_for(index):
        return bind_addresses[0] if len(bind_addresses) == 1 else bind_addresses[index]

    results = []
    if args.mode == "multi":
        if len(args.target) != 2:
            print("multi requires exactly two --target", file=sys.stderr)
            return 2
        sockets = []
        try:
            # 先建立两条连接（同一个源端口），再各发各收；并发窗口由套件的 accept 循环保持。
            for index, target in enumerate(args.target):
                sockets.append(connect(target, bind_for(index), args.source_port, args.timeout))
            payloads = [payload_for(index + 1, args.payload_size) for index in range(2)]
            for index, sock in enumerate(sockets):
                sock.sendall(payloads[index])
            for index, sock in enumerate(sockets):
                received = b""
                while len(received) < len(payloads[index]):
                    chunk = sock.recv(len(payloads[index]) - len(received))
                    if not chunk:
                        break
                    received += chunk
                results.append(result_line(args.target[index], sock, payloads[index], received))
        finally:
            for sock in sockets:
                sock.close()
    else:
        target = args.target[0]
        payload = payload_for(1, args.payload_size)
        first = connect(target, bind_for(0), args.source_port, args.timeout)
        try:
            received = exchange(first, payload)
            results.append(result_line(target, first, payload, received))
            time.sleep(args.gap)
            # 换表之后继续在旧连接上收发：驱动必须仍按旧表项/旧 upstream 回程。
            post = payload_for(3, args.payload_size)
            received_post = exchange(first, post)
            actual = hashlib.sha256(received_post).hexdigest()
            local = first.getsockname()
            ok = 1 if received_post == post else 0
            print(f"[client] post-swap target={target} local={local[0]}:{local[1]} "
                  f"bytes={len(received_post)} sha={actual} ok={ok}", flush=True)
        finally:
            first.close()

        payload = payload_for(2, args.payload_size)
        second_port = args.second_source_port or 0
        with connect(target, bind_for(0), second_port, args.timeout) as second:
            received = exchange(second, payload)
            results.append(result_line(target, second, payload, received))


    return report_lines(results, args.verify, args.timeout)


if __name__ == "__main__":
    raise SystemExit(main())
