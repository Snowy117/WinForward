#!/usr/bin/env python3
"""One-shot TCP SYN probe for the forwarded fail phase (no verify handshake)."""
import argparse
import socket
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True, help="host:port")
    parser.add_argument("--timeout", type=float, default=1.0)
    args = parser.parse_args()
    host, port = args.target.rsplit(":", 1)
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.settimeout(args.timeout)
    try:
        code = sock.connect_ex((host, int(port)))
    finally:
        sock.close()
    print(f"[forwarded-tcp-probe] syn -> {args.target} connect_ex={code}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
