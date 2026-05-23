#!/usr/bin/env python3
"""Healthcheck for an sf-multiplayer oracle.

Sends a v25 Ping (msgType 0, empty body) to the oracle's UDP port and
waits for the PingResponse. Returns exit code 0 if alive, 1 if no
response within timeout. Designed for use by uptime monitors, k8s
liveness probes, or systemd watchdog scripts.

Usage:
    ./healthcheck.py                       # 127.0.0.1:1337
    ./healthcheck.py --host 1.2.3.4
    ./healthcheck.py --host 1.2.3.4 --port 1338 --timeout 2

Wire format reference: notes/PROTOCOL.md.
"""
from __future__ import annotations

import argparse
import socket
import struct
import sys
import time


def build_ping(steam_id: int = 0) -> bytes:
    """Build a v25 Ping envelope: ts(u32 LE) + msgType(0x00) + body(empty) + steamID(u64 LE) + channel(u8)."""
    ts = int(time.time())
    return struct.pack("<I", ts) + bytes([0]) + struct.pack("<Q", steam_id) + bytes([0])


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=1337)
    ap.add_argument("--timeout", type=float, default=2.0, help="seconds to wait for response")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(args.timeout)
    try:
        pkt = build_ping()
        t0 = time.time()
        sock.sendto(pkt, (args.host, args.port))
        try:
            data, _ = sock.recvfrom(1500)
        except socket.timeout:
            print(f"DOWN — no response from {args.host}:{args.port} within {args.timeout}s")
            return 1
        rtt_ms = (time.time() - t0) * 1000
        if len(data) < 14:
            print(f"DOWN — bad reply ({len(data)} bytes)")
            return 1
        msg_type = data[4]
        # MsgType 1 = PingResponse. Some impls just echo Ping back as 0.
        if msg_type not in (0, 1):
            print(f"WARN — got msgType={msg_type} (expected 0/1) but server is alive")
            return 0
        print(f"OK — {args.host}:{args.port} replied in {rtt_ms:.1f}ms (msgType={msg_type})")
        return 0
    finally:
        sock.close()


if __name__ == "__main__":
    sys.exit(main())
