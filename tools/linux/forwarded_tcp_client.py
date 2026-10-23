#!/usr/bin/env python3
"""External Linux TCP client/reporter for wfprobe forwarded-tcp core."""
import argparse
import hashlib
import socket


def payload_for(size):
    return bytes(((index * 31 + 7) & 0xFF) for index in range(size))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True)
    parser.add_argument("--count", type=int, default=1)
    parser.add_argument("--bytes", type=int, default=2048)
    parser.add_argument("--verify", required=True)
    parser.add_argument("--timeout", type=float, default=5.0)
    args = parser.parse_args()
    host, port = args.target.rsplit(":", 1)
    verify_host, verify_port = args.verify.rsplit(":", 1)
    payload = payload_for(args.bytes)
    expected_sha = hashlib.sha256(payload).hexdigest()
    for _ in range(args.count):
        with socket.create_connection((host, int(port)), timeout=args.timeout) as conn:
            client_port = conn.getsockname()[1]
            conn.sendall(payload)
            received = b""
            while len(received) < len(payload):
                chunk = conn.recv(len(payload) - len(received))
                if not chunk:
                    break
                received += chunk
        sha = hashlib.sha256(received).hexdigest()
        ok = received == payload and sha == expected_sha
        line = (f"port={client_port} bytes={len(received)} acked={len(received)} "
                f"sha={sha} ok={1 if ok else 0}")
        print(f"[client] {line}", flush=True)
        with socket.create_connection((verify_host, int(verify_port)), timeout=args.timeout) as verify:
            verify.sendall((line + "\n").encode())
        if not ok:
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
