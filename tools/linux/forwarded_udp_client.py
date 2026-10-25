#!/usr/bin/env python3
"""External Linux client for wfprobe forwarded-udp / WinForward.Controller UDP E2E.

Sends deterministic payloads to one or more targets, verifies echoed bytes and the response
source address, then reports client_port/target_port/bytes/count/sha/source via a TCP verify line.
--target accepts host:port for IPv4, bare IPv6 (fe80::99%eth0:40250) or bracketed IPv6; --verify
stays on the VM IPv4 address. Sweep mode is IPv4-only and rejects IPv6 targets.
No third-party dependencies; ports are chosen from the caller (40000-41000 for listeners).
"""
import argparse
import hashlib
import ipaddress
import socket
import sys
import time


def payload_for(seq, size):
    return bytes(((seq * 31 + j * 7 + 3) & 0xFF) for j in range(size))


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


def datagram_target(host, port):
    family = socket.AF_INET6 if address_version(host) == 6 else socket.AF_INET
    infos = socket.getaddrinfo(host, port, family, socket.SOCK_DGRAM)
    return family, infos[0][4]


def normalize_peer(peer):
    return ipaddress.ip_address(peer[0].split("%")[0]), peer[1]


def run_sweep(target, count, base, payload_size, source_start, source_count, dest_start, dest_end, diagonal,
              sequential, interval):
    host, port = parse_endpoint(target)
    if address_version(host) == 6:
        print("--sweep-count is IPv4-only; IPv6 sweep is not supported", file=sys.stderr)
        return 2
    payload = payload_for(0, payload_size)
    sent = 0
    if sequential:
        for index in range(count):
            if sent >= count:
                break
            source_port = source_start + index
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                sock.bind(("0.0.0.0", source_port))
                sock.sendto(payload, (host, int(port)))
                sent += 1
            except OSError:
                pass
            finally:
                sock.close()
            if interval > 0:
                time.sleep(interval)
    elif diagonal:
        for index in range(source_count):
            if sent >= count:
                break
            source_port = source_start + index
            dest_port = dest_start + index
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                sock.bind(("0.0.0.0", source_port))
                sock.sendto(payload, (host, dest_port))
                sent += 1
            except OSError:
                pass
            finally:
                sock.close()
    elif dest_end >= dest_start:
        sources = [source_start + index for index in range(source_count)] if source_start else [base]
        for source_port in sources:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                sock.bind(("0.0.0.0", source_port))
            except OSError:
                sock.close()
                continue
            try:
                for dest_port in range(dest_start, dest_end + 1):
                    if sent >= count:
                        break
                    sock.sendto(payload, (host, dest_port))
                    sent += 1
            finally:
                sock.close()
            if sent >= count:
                break
    else:
        candidate = base
        while sent < count:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try:
                sock.bind(("0.0.0.0", candidate))
                sock.sendto(payload, (host, int(port)))
                sent += 1
                candidate += 1
            except OSError:
                candidate += 1
                if candidate > base + count * 4:
                    raise
            finally:
                sock.close()
    print(f"[client] sweep target={host} sent={sent} source_start={source_start} "
          f"dest={dest_start}-{dest_end} base={base} sequential={sequential}", flush=True)
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", action="append", default=[], help="host:port (repeatable)")
    parser.add_argument("--count", type=int, default=10)
    parser.add_argument("--payload-size", type=int, default=64)
    parser.add_argument("--verify", default="", help="host:port of TCP verify listener")
    parser.add_argument("--no-wait-verify", action="store_true", help="do not wait for the suite verify port")
    parser.add_argument("--source-port-base", type=int, default=40501)
    parser.add_argument("--timeout", type=float, default=1.0)
    parser.add_argument("--interval", type=float, default=0.03)
    parser.add_argument("--gap", type=float, default=0.0, help="seconds to sleep after the first datagram")
    parser.add_argument("--no-response", action="store_true")
    parser.add_argument("--sent-file", default="", help="after sending, create this file (test synchronization)")
    parser.add_argument("--no-verify", action="store_true")
    parser.add_argument("--sweep-count", type=int, default=0, help="send this many one-shot datagrams from unique ports")
    parser.add_argument("--sweep-base", type=int, default=41001)
    parser.add_argument("--sweep-source-start", type=int, default=0)
    parser.add_argument("--sweep-source-count", type=int, default=1)
    parser.add_argument("--sweep-dest-start", type=int, default=0)
    parser.add_argument("--sweep-dest-end", type=int, default=0)
    parser.add_argument("--sweep-diagonal", action="store_true")
    parser.add_argument("--sweep-sequential", action="store_true",
                        help="unique source ports, one datagram each to the fixed --target port")
    parser.add_argument("--sweep-interval", type=float, default=0.0,
                        help="seconds to sleep between one-shot datagrams (capacity pacing)")
    args = parser.parse_args()

    if args.sweep_count:
        if not args.target:
            print("--sweep-count requires --target", file=sys.stderr)
            return 2
        return run_sweep(args.target[0], args.sweep_count, args.sweep_base, args.payload_size,
                        args.sweep_source_start, args.sweep_source_count, args.sweep_dest_start,
                        args.sweep_dest_end, args.sweep_diagonal, args.sweep_sequential,
                        args.sweep_interval)

    if not args.target:
        print("at least one --target is required", file=sys.stderr)
        return 2

    targets = []
    for text in args.target:
        host, port = parse_endpoint(text)
        family, sockaddr = datagram_target(host, port)
        targets.append((host, port, family, sockaddr))

    if args.verify and not args.no_verify and not args.no_wait_verify:
        verify_host, verify_port = parse_endpoint(args.verify)
        deadline = time.time() + 10.0
        while time.time() < deadline:
            try:
                with socket.create_connection((verify_host, verify_port), timeout=0.5):
                    print(f"[client] verify port ready {args.verify}", flush=True)
                    break
            except OSError:
                time.sleep(0.2)
        else:
            print(f"[client] verify port {args.verify} not ready after 10s", file=sys.stderr, flush=True)

    lines = []
    ok_all = True
    for index, (host, port, family, sockaddr) in enumerate(targets):
        source_port = args.source_port_base + index
        sock = socket.socket(family, socket.SOCK_DGRAM)
        bind_address = "::" if family == socket.AF_INET6 else "0.0.0.0"
        sock.bind((bind_address, source_port))
        sock.settimeout(args.timeout)
        expected = {payload_for(seq, args.payload_size): seq for seq in range(args.count)}
        received = {}
        source_ok = True
        last_source = ""
        expected_address = ipaddress.ip_address(host.split("%")[0])
        for seq in range(args.count):
            sock.sendto(payload_for(seq, args.payload_size), sockaddr)
            if seq == 0 and args.gap > 0:
                time.sleep(args.gap)
            else:
                time.sleep(args.interval)
        if args.sent_file:
            open(args.sent_file, "w", encoding="utf-8").close()
        if args.no_response:
            sock.close()
            lines.append(f"client_port={source_port} target_port={port} bytes=0 count=0 sha= source= source_ok=0 ok=0")
            ok_all = False
            continue
        deadline = time.time() + max(2.0, args.count * 0.5)
        while len(received) < args.count and time.time() < deadline:
            try:
                data, peer = sock.recvfrom(65535)
            except socket.timeout:
                break
            seq = expected.get(data)
            if seq is None:
                continue
            received[seq] = data
            peer_address, peer_port = normalize_peer(peer)
            last_source = f"{peer_address}:{peer_port}"
            if peer_address != expected_address or peer_port != port:
                source_ok = False
        sock.close()
        ordered = b"".join(received[seq] for seq in range(args.count) if seq in received)
        sha = hashlib.sha256(ordered).hexdigest() if len(received) == args.count else ""
        ok = len(received) == args.count and source_ok
        ok_all = ok_all and ok
        lines.append(f"client_port={source_port} target_port={port} bytes={len(ordered)} count={len(received)} "
                     f"sha={sha} source={last_source} source_ok={1 if source_ok else 0} ok={1 if ok else 0}")
        print(f"[client] target={host}:{port} received={len(received)}/{args.count} source_ok={source_ok} "
              f"last_source={last_source}", flush=True)

    if not args.no_response and not args.no_verify and args.verify:
        verify_host, verify_port = parse_endpoint(args.verify)
        for line in lines:
            with socket.create_connection((verify_host, verify_port), timeout=5.0) as conn:
                conn.sendall((line + "\n").encode())
    print("\n".join(lines), flush=True)
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
