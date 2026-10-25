#!/usr/bin/env python3
"""UDP echo backend for wfprobe forwarded-udp core phase."""
import socket
import sys

port = int(sys.argv[1]) if len(sys.argv) > 1 else 40250
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind(("0.0.0.0", port))
print(f"[udp-echo] listening {port}", flush=True)
while True:
    data, peer = sock.recvfrom(65535)
    sock.sendto(data, peer)
