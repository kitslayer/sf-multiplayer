#!/usr/bin/env python3
"""Stress-test the anticheat observer.

Fires PktPlayerInput packets at the oracle's UDP port at a configurable
rate to verify [anticheat] warnings appear in the BepInEx log when
the threshold is exceeded.

Use during dev to confirm the rate guard's threshold knobs aren't
miscalibrated. NOT for use against production servers — that'd be
a DoS.

Usage:
    ./stress-test-anticheat.py                       # 200 pps for 5s
    ./stress-test-anticheat.py --pps 500 --duration 10
    ./stress-test-anticheat.py --host 127.0.0.1 --port 1337

Expected log on the oracle (with default 240 pps total threshold):
    [Warning:SFHeadlessHost] [anticheat] 127.0.0.1:NNNN exceeded
    playerUpdate rate (121/s) — violation #1. Observation only; not
    dropping.
"""
from __future__ import annotations

import argparse
import socket
import struct
import sys
import time


def build_player_input_packet(seq: int = 0) -> bytes:
    """v25 envelope wrapping a PktPlayerInput (msgType 40, 25-byte body)."""
    # Body: u32 seq, u8 slot, 4×f32, u32 buttons
    body = (
        struct.pack("<I", seq)
        + bytes([0])  # slot 0
        + struct.pack("<f", 0.0)
        + struct.pack("<f", 0.0)
        + struct.pack("<f", 0.0)
        + struct.pack("<f", 0.0)
        + struct.pack("<I", 0)
    )
    assert len(body) == 25, f"body wrong size: {len(body)}"
    ts = int(time.time())
    return (
        struct.pack("<I", ts)
        + bytes([40])  # msgType = PktPlayerInput
        + body
        + struct.pack("<Q", 0)  # steamID = 0
        + bytes([0])  # channel
    )


def _is_loopback(host: str) -> bool:
    """True only if host is the loopback interface. This tool emits a sustained
    UDP packet storm, so by default it refuses any other target (see docstring)."""
    if host in ("localhost", "127.0.0.1", "::1"):
        return True
    try:
        import ipaddress
        return ipaddress.ip_address(socket.gethostbyname(host)).is_loopback
    except Exception:
        return False


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=1337)
    ap.add_argument("--pps", type=int, default=200, help="packets per second")
    ap.add_argument("--duration", type=float, default=5.0, help="seconds to run")
    ap.add_argument("--allow-remote", action="store_true",
                    help="permit a non-loopback --host; only for a test target you "
                         "are authorized to hit (this floods UDP, see the docstring)")
    args = ap.parse_args()

    if not args.allow_remote and not _is_loopback(args.host):
        print(f"Refusing to flood non-loopback host {args.host!r}: this is a UDP "
              f"stress tool, not for production. Re-run with --allow-remote only "
              f"against a test target you are authorized to hit.", file=sys.stderr)
        return 2

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    addr = (args.host, args.port)
    interval = 1.0 / args.pps
    sent = 0
    t_end = time.time() + args.duration
    next_send = time.time()
    while time.time() < t_end:
        now = time.time()
        if now < next_send:
            # Tight loop is wasteful; sleep the gap.
            time.sleep(max(0, next_send - now))
            continue
        sock.sendto(build_player_input_packet(sent), addr)
        sent += 1
        next_send += interval
        # Don't let next_send drift if we fall behind
        if next_send < now:
            next_send = now + interval
    sock.close()
    print(f"Sent {sent} PlayerInput packets at {args.pps} pps over {args.duration}s to {addr}.")
    print("Check BepInEx/LogOutput.log for [anticheat] warnings.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
