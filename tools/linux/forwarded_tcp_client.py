#!/usr/bin/env python3
"""External Linux TCP client/reporter for wfprobe forwarded-tcp core.

acked 必须是真实 TCP ACK 语义：读取 TCP_INFO.tcpi_bytes_acked（客户端发出的字节中被
对端 ACK 的数量），不再用 recv 实收字节冒充。
"""
import argparse
import hashlib
import socket
import struct


def payload_for(size):
    return bytes(((index * 31 + 7) & 0xFF) for index in range(size))


# linux/include/uapi/linux/tcp_info.h：8 个 u8 之后是 24 个 u32，再是
# u64 pacing_rate / max_pacing_rate，tcpi_bytes_acked 在偏移 120。
_TCP_INFO_BYTES_ACKED_OFFSET = 120


def tcp_bytes_acked(sock):
    try:
        info = sock.getsockopt(socket.IPPROTO_TCP, socket.TCP_INFO, 256)
    except OSError:
        return -1
    if len(info) < _TCP_INFO_BYTES_ACKED_OFFSET + 8:
        return -1
    return struct.unpack_from("Q", info, _TCP_INFO_BYTES_ACKED_OFFSET)[0]


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
            acked = tcp_bytes_acked(conn)
        sha = hashlib.sha256(received).hexdigest()
        # 本机内核的 tcpi_bytes_acked 把 SYN 也计入 1 字节；数据是否全部 ACK 用
        # [payload, payload+1] 判定，客户端与 suite 采用同一口径。
        ok = received == payload and sha == expected_sha and len(payload) <= acked <= len(payload) + 1
        line = (f"port={client_port} bytes={len(received)} acked={acked} "
                f"sha={sha} ok={1 if ok else 0}")
        print(f"[client] {line}", flush=True)
        with socket.create_connection((verify_host, int(verify_port)), timeout=args.timeout) as verify:
            verify.sendall((line + "\n").encode())
        if not ok:
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
