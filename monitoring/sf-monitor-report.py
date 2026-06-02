#!/usr/bin/env python3
"""sf-monitor-report — human-readable summary of sf-monitor JSONL history.

The daemon stores one JSON object per sample (machine-friendly). This prints a
readable digest: overall averages/peaks, a time-bucketed table, and optional
per-lobby and raw-tail views. Read-only; stdlib only; runs anywhere the JSONL is.

Usage:
  sf-monitor-report.py                 # newest metrics-*.jsonl in the datadir
  sf-monitor-report.py FILE            # a specific file
  sf-monitor-report.py --bucket 15     # 15-minute buckets (0 = auto)
  sf-monitor-report.py --lobby MAIN    # focus one lobby over time
  sf-monitor-report.py --tail 20       # last N raw samples as a table

Datadir default: $SF_MON_DATADIR or ~/sf-monitor/data
"""
import argparse
import glob
import json
import os
import sys
from datetime import datetime

DATADIR = os.path.expanduser(os.environ.get("SF_MON_DATADIR", "~/sf-monitor/data"))


def load(path):
    rows = []
    with open(path) as f:
        for ln in f:
            ln = ln.strip()
            if ln:
                try:
                    rows.append(json.loads(ln))
                except ValueError:
                    pass
    return rows


def hms(ts):
    return datetime.fromtimestamp(ts).strftime("%H:%M:%S")


def avg(xs):
    xs = [x for x in xs if x is not None]
    return sum(xs) / len(xs) if xs else 0.0


def mempct(r):
    m = r.get("mem", {})
    t = m.get("totalMB", 0) or 1
    return m.get("usedMB", 0) / t * 100


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("file", nargs="?")
    ap.add_argument("--bucket", type=int, default=0, help="bucket size in minutes (0 = auto)")
    ap.add_argument("--lobby", help="focus a single lobby code")
    ap.add_argument("--tail", type=int, default=0, help="show the last N raw samples instead")
    a = ap.parse_args()

    path = a.file
    if not path:
        files = sorted(glob.glob(os.path.join(DATADIR, "metrics-*.jsonl")))
        path = files[-1] if files else None
    if not path or not os.path.exists(path):
        print(f"no metrics file (looked in {DATADIR}); pass one explicitly.", file=sys.stderr)
        return 1
    rows = load(path)
    if not rows:
        print("file has no samples yet.", file=sys.stderr)
        return 1

    span = rows[-1]["ts"] - rows[0]["ts"]
    print(f"\nsf-monitor report — {os.path.basename(path)}")
    print(f"{len(rows)} samples · {hms(rows[0]['ts'])} → {hms(rows[-1]['ts'])} "
          f"· {span/60:.1f} min · {rows[-1].get('ncpu','?')} cores\n")

    # ---- raw tail mode ----
    if a.tail:
        print(f"{'time':>8}  {'cpu%':>5}  {'mem%':>5}  {'load':>5}  {'lob':>3}  {'cli':>3}  {'net KB/s':>12}")
        for r in rows[-a.tail:]:
            print(f"{hms(r['ts']):>8}  {r['cpu']:>5.1f}  {mempct(r):>5.0f}  {r['load'][0]:>5.2f}  "
                  f"{r['server']['lobbyCount']:>3}  {r['server']['clients']:>3}  "
                  f"{'↓%g ↑%g' % (r['net']['rxKBs'], r['net']['txKBs']):>12}")
        return 0

    # ---- per-lobby mode ----
    if a.lobby:
        code = a.lobby.upper()
        seen = [(r["ts"], lb) for r in rows for lb in r.get("lobbies", []) if lb["code"].upper() == code]
        if not seen:
            print(f"lobby {code!r} not seen in this file.")
            return 1
        cpus = [lb["cpu"] for _, lb in seen]
        rss = [lb["rssMB"] for _, lb in seen]
        cli = [lb["clients"] for _, lb in seen]
        print(f"lobby {code}: seen in {len(seen)} samples")
        print(f"  cpu%   avg {avg(cpus):5.1f}  peak {max(cpus):5.1f}")
        print(f"  rss MB avg {avg(rss):5.0f}  peak {max(rss):5.0f}")
        print(f"  clients      peak {max(cli)}")
        return 0

    # ---- overall summary ----
    cpu = [r["cpu"] for r in rows]
    mp = [mempct(r) for r in rows]
    ld = [r["load"][0] for r in rows]
    scpu = [r["server"]["cpu"] for r in rows]
    cli = [r["server"]["clients"] for r in rows]
    lob = [r["server"]["lobbyCount"] for r in rows]
    rx = [r["net"]["rxKBs"] for r in rows]
    tx = [r["net"]["txKBs"] for r in rows]
    tot = rows[-1]["mem"]["totalMB"]
    print(f"{'':10}{'CPU%':>8}{'MEM%':>8}{'LOAD':>8}{'SRV-CPU%':>10}{'LOBBIES':>9}{'CLIENTS':>9}{'NET KB/s':>12}")
    print(f"{'avg':<10}{avg(cpu):>8.1f}{avg(mp):>8.0f}{avg(ld):>8.2f}{avg(scpu):>10.1f}{avg(lob):>9.1f}{avg(cli):>9.1f}{'↓%.0f ↑%.0f' % (avg(rx), avg(tx)):>12}")
    print(f"{'peak':<10}{max(cpu):>8.1f}{max(mp):>8.0f}{max(ld):>8.2f}{max(scpu):>10.1f}{max(lob):>9d}{max(cli):>9d}{'↓%.0f ↑%.0f' % (max(rx), max(tx)):>12}")
    print(f"\n  total RAM {tot/1024:.1f}G · peak used {max(mp)/100*tot/1024:.1f}G\n")

    # ---- time-bucketed table ----
    bsec = a.bucket * 60 if a.bucket else (300 if span < 3600 else 3600)
    buckets = {}
    for r in rows:
        k = int(r["ts"] // bsec) * bsec
        buckets.setdefault(k, []).append(r)
    label = f"{bsec//60}-min" if bsec < 3600 else f"{bsec//3600}-hr"
    print(f"per {label} bucket:")
    print(f"  {'time':>8}  {'cpu avg/pk':>11}  {'mem%pk':>6}  {'load pk':>7}  {'lob pk':>6}  {'cli pk':>6}  {'net pk ↓/↑':>12}")
    for k in sorted(buckets):
        b = buckets[k]
        cu = [r["cpu"] for r in b]
        print(f"  {hms(k):>8}  {avg(cu):>5.0f}/{max(cu):<5.0f}  {max(mempct(r) for r in b):>6.0f}  "
              f"{max(r['load'][0] for r in b):>7.2f}  {max(r['server']['lobbyCount'] for r in b):>6d}  "
              f"{max(r['server']['clients'] for r in b):>6d}  "
              f"{'%.0f/%.0f' % (max(r['net']['rxKBs'] for r in b), max(r['net']['txKBs'] for r in b)):>12}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
