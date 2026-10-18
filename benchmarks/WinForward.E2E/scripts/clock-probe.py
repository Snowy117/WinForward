#!/usr/bin/env python3
"""The Linux host's half of the E2E preflight clock probe.

Each probe is one TCP connection from the Windows VM. The VM sends one line,
``vmT0=<UTC ISO>``, and this script replies with the host instant at which the
line arrived, as ``<UTC ISO>\\n``. The orchestrator pairs that reply with the VM's
own send and receive times; the per-probe offset is

    offset(vm - host) = vmT0 + rtt / 2 - hostRecv

where ``rtt`` is the VM's send-to-receive interval. The orchestrator applies the campaign's clock
policy to the five-probe median: by default a stable offset within 5 s is recorded and corrected per
pass; ``-RequireClockSync`` enforces the 200 ms bound.

Run it on the host before the orchestrator preflight:

    python3 scripts/clock-probe.py --bind 0.0.0.0 --port 40099 --count 0

The default port is 40099, the orchestrator's ``-ClockProbePort`` default. The default ``--count 0``
serves until interrupted: a campaign takes five probes for the preflight and five on each side of
every pass. A preflight-only run can use ``--count 5``. One line
per probe is written to stdout for the evidence log; it records the receive and send instants and the
handshake the VM sent.

The probe waits indefinitely for the next connection by default and gives each probe 30 s to send its
line; ``--timeout`` and ``--read-timeout`` bound those waits.
"""
from __future__ import annotations

import argparse
import socket
import sys
import time
from datetime import datetime, timezone


def utc_iso(t: float) -> str:
    return datetime.fromtimestamp(t, timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--bind", default="0.0.0.0", help="address to listen on (default: 0.0.0.0)")
    parser.add_argument("--port", type=int, default=40099, help="TCP port (default: 40099)")
    parser.add_argument(
        "--count",
        type=int,
        default=0,
        help="number of probes to serve; 0 serves until interrupted (default: 0)",
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=0.0,
        help="accept timeout in seconds; 0 waits indefinitely (default: 0)",
    )
    parser.add_argument(
        "--read-timeout",
        type=float,
        default=30.0,
        help="per-connection read timeout in seconds; 0 waits indefinitely (default: 30)",
    )
    args = parser.parse_args()
    if args.count < 0:
        parser.error("--count must be 0 or more")
    accept_timeout = None if args.timeout <= 0 else args.timeout
    read_timeout = None if args.read_timeout <= 0 else args.read_timeout

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.settimeout(accept_timeout)
    server.bind((args.bind, args.port))
    server.listen(4)
    if args.count == 0:
        print(f"listening on {args.bind}:{args.port} until interrupted", flush=True)
    else:
        print(f"listening on {args.bind}:{args.port} for {args.count} probe(s)", flush=True)

    seq = 0
    try:
        while args.count == 0 or seq < args.count:
            seq += 1
            try:
                conn, peer = server.accept()
            except socket.timeout:
                print(f"timed out waiting for probe {seq}", file=sys.stderr, flush=True)
                return 1

            with conn:
                conn.settimeout(read_timeout)
                try:
                    # The arrival instant is what the offset measures, not the end of parsing.
                    t_before = time.time()
                    data = conn.recv(4096)
                    t_recv = time.time()
                    while b"\n" not in data and len(data) < 4096:
                        chunk = conn.recv(4096)
                        if not chunk:
                            break
                        data += chunk
                    payload = data.decode("utf-8", "replace").strip()
                    if not payload.startswith("vmT0="):
                        print(f"seq={seq} unexpected handshake: {payload!r}", file=sys.stderr, flush=True)
                        return 1
                    conn.sendall((utc_iso(t_recv) + "\n").encode("ascii"))
                    t_send = time.time()
                except socket.timeout:
                    print(f"seq={seq} timed out waiting for the VM line", file=sys.stderr, flush=True)
                    return 1

            print(
                "seq=%d peer=%s:%d hostRecvUtc=%s hostSendUtc=%s recvSpan=%.6f sendSpan=%.6f %s"
                % (
                    seq,
                    peer[0],
                    peer[1],
                    utc_iso(t_recv),
                    utc_iso(t_send),
                    t_recv - t_before,
                    t_send - t_recv,
                    payload,
                ),
                flush=True,
            )
    finally:
        server.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
