#!/usr/bin/env python3
"""Synthetic Stick Fight client for testing the sf-router multi-lobby routing.

Speaks the router's SELECT control protocol (see sf-router/select.go), then
bursts game-shaped UDP datagrams so you can watch the *backend* lobby's rx
counter rise — proving the router forwarded this client's traffic to the
backend for the lobby code it SELECTed, and (run from two machines) that two
different source IPs land in two different lobbies with no crossover.

This is NOT a real game client: it doesn't do the v25 handshake or spawn. It
exercises the router's per-endpoint (SELECT) and per-IP (game-socket) binding.

Usage:
  ./sf-synthetic-client.py --router 192.168.1.115:1338 --code MAIN --count 200
  ./sf-synthetic-client.py --router 192.168.1.115:1338 --code DUO  --count 200
"""
import argparse
import socket
import struct
import sys
import time

MAGIC = bytes([0x53, 0x46, 0x52, 0x54, 0x52, 0x00, 0x00, 0x01])  # "SFRTR\0\0\x01"
OP_SELECT = 0x01
OP_ACK = 0x81
ACK_OK = 0x00
ACK_NO_SUCH_CODE = 0x01
ACK_NAMES = {ACK_OK: "OK", ACK_NO_SUCH_CODE: "NO_SUCH_CODE"}


def build_select(code: str, nonce: int) -> bytes:
    code_b = code.upper().encode("ascii")
    return MAGIC + bytes([OP_SELECT, len(code_b)]) + code_b + struct.pack("<I", nonce)


def build_probe(seq: int) -> bytes:
    # Game-shaped: [u32 ts LE][u8 msgType=40 (PlayerInput)][payload]. The router
    # forwards anything that isn't the SELECT magic, so this lands at the backend.
    return struct.pack("<IB", int(time.time()) & 0xFFFFFFFF, 40) + b"SYNV26" + struct.pack("<I", seq)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--router", required=True, help="HOST:PORT of the sf-router")
    ap.add_argument("--code", required=True, help="lobby code to SELECT")
    ap.add_argument("--count", type=int, default=200, help="number of probe datagrams")
    ap.add_argument("--rate", type=float, default=60.0, help="probes per second")
    ap.add_argument("--game-socket", action="store_true",
                    help="also send probes from a 2nd socket WITHOUT SELECT (tests per-IP game-socket binding)")
    args = ap.parse_args()

    host, port = args.router.rsplit(":", 1)
    dst = (host, int(port))
    nonce = int(time.time()) & 0xFFFFFFFF

    # Recon socket: sends SELECT, then probes.
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.settimeout(2.0)
    s.sendto(build_select(args.code, nonce), dst)
    print(f"[recon] SELECT {args.code!r} sent to {dst} (nonce={nonce})")

    # Await ACK.
    try:
        data, _ = s.recvfrom(64)
        if len(data) >= 14 and data[:8] == MAGIC and data[8] == OP_ACK:
            st, ack_nonce = data[9], struct.unpack("<I", data[10:14])[0]
            ok = "✓" if (st == ACK_OK and ack_nonce == nonce) else "✗"
            print(f"[recon] ACK status={ACK_NAMES.get(st, st)} nonce={ack_nonce} {ok}")
            if st != ACK_OK:
                print("  -> router rejected the code (lobby not in registry?)")
        else:
            print(f"[recon] unexpected reply ({len(data)} bytes)")
    except socket.timeout:
        print("[recon] no ACK within 2s (router down, wrong port, or firewall?)")

    # Optional game socket: NO select — relies on the router's per-IP binding
    # (the real patched-DLL game socket can't SELECT, so this mirrors it).
    g = None
    if args.game_socket:
        g = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    interval = 1.0 / args.rate if args.rate > 0 else 0
    sent = 0
    for i in range(args.count):
        s.sendto(build_probe(i), dst)
        sent += 1
        if g is not None:
            g.sendto(build_probe(100000 + i), dst)
            sent += 1
        if interval:
            time.sleep(interval)
    print(f"[done] sent {sent} probe datagrams to lobby {args.code!r} "
          f"({'recon+game sockets' if g else 'recon socket only'})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
