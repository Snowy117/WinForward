#!/usr/bin/env python3
"""Send one explicitly fragmented UDP datagram from Linux to the Windows VM.

This is the B6 raw-fragment fixture: it builds every IPv4/IPv6 fragment itself
instead of relying on the outgoing interface MTU, so the driver sees exactly N
fragments (default 9). Requires CAP_NET_RAW; on the dev host run it through
`podman run --network=host --cap-add=NET_RAW`.

IPv4: IP header with MF/offset, UDP checksum 0 (legal in IPv4).
IPv6: IPv6 header -> Fragment header (next=UDP) -> UDP header + payload chunk.
"""
import argparse
import socket
import struct
import sys


def checksum(data):
    if len(data) % 2:
        data += b"\x00"
    total = 0
    for index in range(0, len(data), 2):
        total += (data[index] << 8) | data[index + 1]
        total = (total & 0xFFFF) + (total >> 16)
    return (~total) & 0xFFFF


def build_udp(source_port, target_port, payload):
    length = 8 + len(payload)
    return struct.pack("!HHHH", source_port, target_port, length, 0) + payload


def build_udp6(source, target, source_port, target_port, payload):
    """IPv6 的 UDP 校验和是强制的：不能用 0。"""
    header = struct.pack("!HHHH", source_port, target_port, 8 + len(payload), 0)
    pseudo = (socket.inet_pton(socket.AF_INET6, source) + socket.inet_pton(socket.AF_INET6, target) +
              struct.pack("!IxxxB", 8 + len(payload), socket.IPPROTO_UDP))
    total = checksum(pseudo + header + payload)
    return struct.pack("!HHHH", source_port, target_port, 8 + len(payload), total or 0xFFFF) + payload


def build_ipv4(source, target, identification, fragment_offset, more_fragments, udp_bytes):
    flags_offset = (0x2000 if more_fragments else 0) | (fragment_offset // 8)
    header = struct.pack(
        "!BBHHHBBH4s4s",
        0x45, 0, 20 + len(udp_bytes), identification, flags_offset, 64,
        socket.IPPROTO_UDP, 0, socket.inet_aton(source), socket.inet_aton(target))
    header = header[:10] + struct.pack("!H", checksum(header)) + header[12:]
    return header + udp_bytes


def build_ipv6(source, target, identification, fragment_offset, more_fragments, udp_bytes):
    # IPv6 Fragment Offset 以 8 字节为单位（与 IPv4 不同，这里传进来的 offset 是字节）。
    fragment = struct.pack("!BBHI", socket.IPPROTO_UDP, 0,
                           (fragment_offset // 8) | (1 if more_fragments else 0), identification)
    payload = fragment + udp_bytes
    header = struct.pack("!IHBB16s16s", 6 << 28, len(payload), socket.IPPROTO_FRAGMENT, 64,
                         socket.inet_pton(socket.AF_INET6, source),
                         socket.inet_pton(socket.AF_INET6, target))
    return header + payload


def split_datagram(data, fragment_payload):
    chunks = []
    offset = 0
    while offset < len(data):
        chunk = data[offset:offset + fragment_payload]
        chunks.append((offset, chunk))
        offset += len(chunk)
    return chunks


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--family", type=int, choices=(4, 6), default=4)
    parser.add_argument("--source", required=True)
    parser.add_argument("--target", required=True, help="address:port")
    parser.add_argument("--source-port", type=int, default=50000)
    parser.add_argument("--payload-size", type=int, default=544)
    parser.add_argument("--fragment-payload", type=int, default=64)
    parser.add_argument("--fragments", type=int, default=9)
    parser.add_argument("--identification", type=int, default=0xB6A1)
    parser.add_argument("--interface", default="eth0")
    parser.add_argument("--reverse", action="store_true", help="send non-first fragments before the first")
    args = parser.parse_args()

    if args.fragment_payload <= 0 or args.fragment_payload % 8:
        print("--fragment-payload must be a positive multiple of 8", file=sys.stderr)
        return 2
    if args.family == 6 and "%" in args.target:
        args.target = args.target.split("%", 1)[0]

    host, port = args.target.rsplit(":", 1)
    target_port = int(port)
    payload = bytes((index * 7 + 3) & 0xFF for index in range(args.payload_size))
    udp = (build_udp6(args.source, host, args.source_port, target_port, payload)
           if args.family == 6 else build_udp(args.source_port, target_port, payload))
    chunks = split_datagram(udp, args.fragment_payload)
    if len(chunks) != args.fragments:
        print(f"fragment count mismatch: built={len(chunks)} expected={args.fragments}", file=sys.stderr)
        return 2

    if args.family == 4:
        sender = socket.socket(socket.AF_INET, socket.SOCK_RAW, socket.IPPROTO_RAW)
        sender.setsockopt(socket.IPPROTO_IP, socket.IP_HDRINCL, 1)
        destination = (host, 0)
    else:
        sender = socket.socket(socket.AF_INET6, socket.SOCK_RAW, socket.IPPROTO_RAW)
        # Python 的 socket 模块在部分版本没有导出 IPV6_HDRINCL；Linux 的值是 36。
        sender.setsockopt(socket.IPPROTO_IPV6, getattr(socket, "IPV6_HDRINCL", 36), 1)
        scope_id = socket.if_nametoindex(args.interface)
        # 不 bind 源地址时，HDRINCL 发送会在邻居解析阶段卡住；bind 后走正常出接口。
        sender.bind((args.source, 0, 0, scope_id))
        destination = (host, 0, 0, scope_id)

    sent = 0
    order = list(enumerate(chunks))
    if args.reverse:
        order.reverse()
    for index, (offset, chunk) in order:
        more = index + 1 < len(chunks)
        if args.family == 4:
            packet = build_ipv4(args.source, host, args.identification, offset, more, chunk)
        else:
            packet = build_ipv6(args.source, host, args.identification, offset, more, chunk)
        sender.sendto(packet, destination)
        sent += 1

    sender.close()
    print(f"[raw-frag] family={args.family} target={args.target} fragments={sent} "
          f"datagram_bytes={len(udp)} fragment_payload={args.fragment_payload} "
          f"identification=0x{args.identification:04x} source_port={args.source_port}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
