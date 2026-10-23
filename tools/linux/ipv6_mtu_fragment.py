#!/usr/bin/env python3
"""IPv6 fragment fixture via a temporary small MTU on the Linux test NIC.

IPv6 raw HDRINCL sends are unreliable on this host (alternating EPERM), so the
IPv6 9-fragment case uses the kernel's own fragmentation: set eth0 MTU to 1280
(IPv6 minimum), send one UDP datagram with PMTUDISC_DONT, restore the MTU.
The fragment count is asserted on the driver side (udp_fragmented == 9).
"""
import argparse
import socket
import subprocess
import sys
import time


def read_mtu(interface):
    with open(f"/sys/class/net/{interface}/mtu", encoding="ascii") as handle:
        return int(handle.read().strip())


def set_mtu(interface, mtu):
    subprocess.run(["ip", "link", "set", "dev", interface, "mtu", str(mtu)], check=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--interface", default="eth0")
    parser.add_argument("--source", required=True)
    parser.add_argument("--target", required=True, help="[addr%iface]:port")
    parser.add_argument("--source-port", type=int, default=50000)
    parser.add_argument("--payload-size", type=int, default=10792)
    parser.add_argument("--mtu", type=int, default=1280)
    args = parser.parse_args()

    target = args.target
    if "%" in target:
        target = target.split("%", 1)[0]
    host, port = target.rsplit(":", 1)
    scope = socket.if_nametoindex(args.interface)

    original = read_mtu(args.interface)
    sender = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM)
    try:
        set_mtu(args.interface, args.mtu)
        sender.bind((args.source, args.source_port, 0, scope))
        discover = getattr(socket, "IPV6_MTU_DISCOVER", 23)
        dont = getattr(socket, "IPV6_PMTUDISC_DONT", 0)
        sender.setsockopt(socket.IPPROTO_IPV6, discover, dont)
        payload = bytes((index * 7 + 3) & 0xFF for index in range(args.payload_size))
        sender.sendto(payload, (host, int(port), 0, scope))
        time.sleep(0.2)
        fragments = (8 + len(payload) + (args.mtu - 40 - 8) - 1) // (args.mtu - 40 - 8)
        print(f"[ipv6-mtu-frag] target={args.target} mtu={args.mtu} payload={len(payload)} "
              f"expected_fragments={fragments} interface={args.interface}", flush=True)
    finally:
        sender.close()
        set_mtu(args.interface, original)


if __name__ == "__main__":
    raise SystemExit(main())
