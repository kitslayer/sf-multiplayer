# sf-monitor — access (team)

Read-only host + lobby telemetry on the server (`.115`). The dashboard is
**loopback-only** (`127.0.0.1:8090`) — reached over SSH, so there's no LAN/public
exposure. Everything here is read-only; it never touches the running game.

## 1. Live dashboard (graphical, real-time)
From your own machine, SSH to the server **adding a local port-forward**, then
open the URL in a browser (keep the SSH session open while viewing):

```
ssh -L 8090:127.0.0.1:8090 sfdev@<server>      # your usual SSH connection + this -L flag
                                               # (add -p PORT if you use a non-22 port)
#   then browse to:   http://localhost:8090
```

## 2. Text reports (SSH in, then run on the server)
```
/home/miles/sf-monitor/report.sh               # avg/peak summary + time buckets
/home/miles/sf-monitor/report.sh --tail 30     # last 30 samples as a table
/home/miles/sf-monitor/report.sh --lobby MAIN  # one lobby's cpu/rss/clients over time
/home/miles/sf-monitor/report.sh --bucket 60   # hourly buckets
/home/miles/sf-monitor/report.sh /home/miles/sf-monitor/data/metrics-2026-06-02.jsonl   # a specific day
```

## 3. Current snapshot / health
```
curl -s localhost:8090/api/now | python3 -m json.tool     # raw live metrics
systemctl status sf-monitor                                # is the monitor up?
```

Notes:
- CPU for lobbies/server is shown in **cores** (e.g. `0.51 cores` = half of one
  core); host CPU is `%` of all cores.
- Data: `/home/miles/sf-monitor/data/metrics-YYYY-MM-DD.jsonl` (daily, 14-day retention, ~11 MB/day).
- The monitor runs lowest-priority (~25 MB RAM) and only reads `/proc` + the
  existing router/control-plane endpoints + tails log files.
