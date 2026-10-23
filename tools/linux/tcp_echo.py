#!/usr/bin/env python3
"""TCP echo backend for forwarded-tcp core phase."""
import socket
import sys
import threading

port = int(sys.argv[1]) if len(sys.argv) > 1 else 40250


def serve(conn):
    try:
        while True:
            data = conn.recv(65535)
            if not data:
                break
            conn.sendall(data)
    finally:
        conn.close()


server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("0.0.0.0", port))
server.listen(16)
print(f"[tcp-echo] listening {port}", flush=True)
while True:
    conn, _ = server.accept()
    threading.Thread(target=serve, args=(conn,), daemon=True).start()
