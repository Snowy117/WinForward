
#!/usr/bin/env python3
"""Direct TCP echo client for the A5 host-path control (no WFP forwarded path)."""
import argparse
import socket
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True)
    parser.add_argument("--bytes", type=int, default=16 * 1024 * 1024)
    args = parser.parse_args()
    host, port = args.target.rsplit(":", 1)
    payload = b"x" * min(args.bytes, 65536)
    remaining = args.bytes
    began = time.perf_counter()
    with socket.create_connection((host, int(port)), timeout=10) as sock:
        sock.settimeout(30)
        while remaining:
            chunk = payload[:remaining]
            sock.sendall(chunk)
            received = 0
            while received < len(chunk):
                data = sock.recv(len(chunk) - received)
                if not data:
                    raise SystemExit("connection closed")
                received += len(data)
            remaining -= len(chunk)
    elapsed = time.perf_counter() - began
    print(f"[host-tcp] bytes={args.bytes} seconds={elapsed:.3f} mbps={args.bytes / elapsed / 1e6:.1f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
