#!/usr/bin/env python3
"""sf-monitor — lightweight host + lobby telemetry for the SF dedicated server.

Runs as a background daemon. Every few seconds it samples the host (CPU, RAM,
swap, disk, network, load) and per-lobby footprint (CPU%, RSS, clients, rx) by
reading /proc and polling the router/control-plane HTTP endpoints — ALL
read-only, it never signals or restarts anything. Samples are kept in an
in-memory ring (for live charts) and appended to daily JSONL files (for
history). A self-contained dashboard (no external JS/CDN) is served over HTTP.

Design goals: stdlib only, tiny footprint, ZERO impact on the running game.

Env:
  SF_MON_BIND        bind addr           (default 127.0.0.1:8090; view via SSH tunnel)
  SF_MON_INTERVAL    sample seconds      (default 5)
  SF_MON_DATADIR     JSONL store dir     (default ~/sf-monitor/data)
  SF_MON_RETAIN_DAYS delete JSONL older  (default 14)
  SF_LOBBIES_DIR     registry dir        (default /tmp/sf-lobbies)
  SF_ROUTER_STATS    router stats url    (default http://127.0.0.1:8081/router/stats)
  SF_LOBBIES_URL     control-plane list  (default http://127.0.0.1:8080/lobbies)
"""
from __future__ import annotations

import collections
import glob
import json
import os
import re
import threading
import time
import urllib.request
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# ---- config -----------------------------------------------------------------
BIND = os.environ.get("SF_MON_BIND", "127.0.0.1:8090")
INTERVAL = float(os.environ.get("SF_MON_INTERVAL", "5"))
DATADIR = os.path.expanduser(os.environ.get("SF_MON_DATADIR", "~/sf-monitor/data"))
RETAIN_DAYS = int(os.environ.get("SF_MON_RETAIN_DAYS", "14"))
LOBBIES_DIR = os.environ.get("SF_LOBBIES_DIR", "/tmp/sf-lobbies")
ROUTER_STATS = os.environ.get("SF_ROUTER_STATS", "http://127.0.0.1:8081/router/stats")
LOBBIES_URL = os.environ.get("SF_LOBBIES_URL", "http://127.0.0.1:8080/lobbies")

CLK_TCK = os.sysconf("SC_CLK_TCK")
PAGE = os.sysconf("SC_PAGE_SIZE")
RING = collections.deque(maxlen=2880)   # ~4h at 5s
RING_LOCK = threading.Lock()
START_TS = time.time()

# bridge → lobby code, refreshed each sample from the registry
_BRIDGE_RE = re.compile(r"sf-oracle-unity-(\d+)\.log")
_HEARTBEAT_RE = re.compile(
    r"tick=(\d+).*?clients=(\d+)\s+spawned=(\d+).*?rx=([\d.]+)/s.*?input=([\d.]+)/s")


# ---- /proc readers ----------------------------------------------------------
def read_cpu_stat():
    """Return (total_jiffies, idle_jiffies, {coreN: (total, idle)})."""
    total = idle = 0
    cores = {}
    with open("/proc/stat") as f:
        for line in f:
            if not line.startswith("cpu"):
                break
            parts = line.split()
            vals = [int(x) for x in parts[1:]]
            t = sum(vals)
            idl = vals[3] + (vals[4] if len(vals) > 4 else 0)  # idle + iowait
            if parts[0] == "cpu":
                total, idle = t, idl
            else:
                cores[parts[0]] = (t, idl)
    return total, idle, cores


def read_meminfo():
    m = {}
    with open("/proc/meminfo") as f:
        for line in f:
            k, _, rest = line.partition(":")
            m[k] = int(rest.split()[0])  # kB
    used = m["MemTotal"] - m.get("MemAvailable", m["MemFree"])
    return {
        "totalMB": m["MemTotal"] // 1024,
        "usedMB": used // 1024,
        "availMB": m.get("MemAvailable", m["MemFree"]) // 1024,
        "swapUsedMB": (m.get("SwapTotal", 0) - m.get("SwapFree", 0)) // 1024,
        "swapTotalMB": m.get("SwapTotal", 0) // 1024,
    }


def read_loadavg():
    with open("/proc/loadavg") as f:
        p = f.read().split()
    return [float(p[0]), float(p[1]), float(p[2])]


def default_iface():
    try:
        with open("/proc/net/route") as f:
            next(f)
            for line in f:
                p = line.split()
                if p[1] == "00000000":
                    return p[0]
    except Exception:
        pass
    return None


def read_netdev(iface):
    """Return (rx_bytes, tx_bytes) for iface, or summed over non-lo if None."""
    rx = tx = 0
    with open("/proc/net/dev") as f:
        for line in f:
            if ":" not in line:
                continue
            name, _, data = line.partition(":")
            name = name.strip()
            if name == "lo":
                continue
            if iface and name != iface:
                continue
            cols = data.split()
            rx += int(cols[0])
            tx += int(cols[8])
    return rx, tx


def disk_usage(path):
    try:
        s = os.statvfs(path)
        total = s.f_blocks * s.f_frsize
        free = s.f_bavail * s.f_frsize
        return {"totalMB": total // (1 << 20), "usedMB": (total - free) // (1 << 20)}
    except Exception:
        return None


def _proc_cmdline(pid):
    try:
        with open(f"/proc/{pid}/cmdline", "rb") as f:
            return f.read().replace(b"\0", b" ").decode("utf-8", "replace")
    except Exception:
        return ""


def _proc_jiffies_rss(pid):
    try:
        with open(f"/proc/{pid}/stat") as f:
            data = f.read()
        # comm may contain spaces/parens — split after the last ')'
        rp = data.rfind(")")
        fields = data[rp + 2:].split()
        utime = int(fields[11])   # field 14 overall (0-based after comm: 11)
        stime = int(fields[12])
        return utime + stime, None
    except Exception:
        return None, None


def _proc_rss(pid):
    try:
        with open(f"/proc/{pid}/statm") as f:
            return int(f.read().split()[1]) * PAGE
    except Exception:
        return 0


def iter_relevant_procs():
    """Yield (pid, category, bridge, jiffies, rss) for SF-related processes.

    category: 'lobby' (bridge set), 'router', 'control', 'monitor'(skip).
    """
    for entry in os.listdir("/proc"):
        if not entry.isdigit():
            continue
        pid = int(entry)
        cmd = _proc_cmdline(pid)
        if not cmd:
            continue
        cat = bridge = None
        m = _BRIDGE_RE.search(cmd)
        if m:
            cat, bridge = "lobby", m.group(1)
        elif "/sf-router" in cmd or "sf-router -listen" in cmd:
            cat = "router"
        elif "serve-lobbies.py" in cmd:
            cat = "control"
        else:
            continue
        jiff, _ = _proc_jiffies_rss(pid)
        if jiff is None:
            continue
        yield pid, cat, bridge, jiff, _proc_rss(pid)


def read_registry():
    """bridge -> {code, port, pid} from the lobby registry."""
    out = {}
    for path in glob.glob(os.path.join(LOBBIES_DIR, "*.conf")):
        d = {}
        try:
            with open(path) as f:
                for line in f:
                    k, _, v = line.strip().partition("=")
                    d[k] = v
        except Exception:
            continue
        if "bridge" in d:
            out[d["bridge"]] = {"code": d.get("code", "?"),
                                "port": d.get("port", "?"),
                                "pid": d.get("pid", "")}
    return out


def parse_heartbeat(bridge):
    """Tail the per-lobby plugin log for the last heartbeat line."""
    path = f"/tmp/sf-oracle-plugin-{bridge}.log"
    try:
        with open(path, "rb") as f:
            f.seek(0, 2)
            size = f.tell()
            f.seek(max(0, size - 8192))
            tail = f.read().decode("utf-8", "replace")
    except Exception:
        return None
    last = None
    for mm in _HEARTBEAT_RE.finditer(tail):
        last = mm
    if not last:
        return None
    return {"tick": int(last.group(1)), "clients": int(last.group(2)),
            "spawned": int(last.group(3)), "rxps": float(last.group(4)),
            "inputps": float(last.group(5))}


def fetch_json(url):
    try:
        with urllib.request.urlopen(url, timeout=2) as r:
            return json.loads(r.read().decode())
    except Exception:
        return None


# ---- sampler ----------------------------------------------------------------
class Sampler(threading.Thread):
    daemon = True

    def __init__(self):
        super().__init__()
        self.iface = default_iface()
        self.prev_cpu = read_cpu_stat()
        self.prev_net = read_netdev(self.iface)
        self.prev_t = time.time()
        self.prev_jiff = {}  # pid -> jiffies
        os.makedirs(DATADIR, exist_ok=True)
        self._load_today()

    def _load_today(self):
        path = self._today_path()
        if not os.path.exists(path):
            return
        try:
            with open(path) as f:
                lines = f.readlines()[-RING.maxlen:]
            with RING_LOCK:
                for ln in lines:
                    try:
                        RING.append(json.loads(ln))
                    except Exception:
                        pass
        except Exception:
            pass

    def _today_path(self):
        return os.path.join(DATADIR, "metrics-%s.jsonl" % datetime.now(timezone.utc).strftime("%Y-%m-%d"))

    def _retain(self):
        cutoff = time.time() - RETAIN_DAYS * 86400
        for p in glob.glob(os.path.join(DATADIR, "metrics-*.jsonl")):
            try:
                if os.path.getmtime(p) < cutoff:
                    os.remove(p)
            except Exception:
                pass

    def run(self):
        last_retain = 0
        while True:
            try:
                self._sample()
            except Exception as e:  # never die
                print("[sf-monitor] sample error:", e, flush=True)
            if time.time() - last_retain > 3600:
                self._retain()
                last_retain = time.time()
            time.sleep(INTERVAL)

    def _sample(self):
        now = time.time()
        dt = max(0.1, now - self.prev_t)

        # host CPU
        tot, idl, cores = read_cpu_stat()
        dtot = tot - self.prev_cpu[0]
        didl = idl - self.prev_cpu[1]
        cpu_pct = round(100.0 * (1 - didl / dtot), 1) if dtot > 0 else 0.0
        percore = []
        for name in sorted(cores, key=lambda c: int(c[3:])):
            pt, pi = self.prev_cpu[2].get(name, (0, 0))
            d_t = cores[name][0] - pt
            d_i = cores[name][1] - pi
            percore.append(round(100.0 * (1 - d_i / d_t), 0) if d_t > 0 else 0.0)
        self.prev_cpu = (tot, idl, cores)

        # network rates
        rx, tx = read_netdev(self.iface)
        rx_rate = max(0, (rx - self.prev_net[0]) / dt)
        tx_rate = max(0, (tx - self.prev_net[1]) / dt)
        self.prev_net = (rx, tx)

        # per-process CPU/RSS, bucketed by lobby bridge + router/control
        reg = read_registry()
        new_jiff = {}
        lobby_acc = {}   # bridge -> {cpu, rss}
        comp = {"router": {"cpu": 0.0, "rss": 0}, "control": {"cpu": 0.0, "rss": 0}}
        sf_cpu_total = 0.0
        sf_rss_total = 0
        for pid, cat, bridge, jiff, rss in iter_relevant_procs():
            new_jiff[pid] = jiff
            prev = self.prev_jiff.get(pid)
            pcpu = 0.0
            if prev is not None and jiff >= prev:
                pcpu = (jiff - prev) / CLK_TCK / dt * 100.0
            sf_cpu_total += pcpu
            sf_rss_total += rss
            if cat == "lobby":
                a = lobby_acc.setdefault(bridge, {"cpu": 0.0, "rss": 0})
                a["cpu"] += pcpu
                a["rss"] += rss
            else:
                comp[cat]["cpu"] += pcpu
                comp[cat]["rss"] += rss
        self.prev_jiff = new_jiff

        # lobby occupancy from the router + per-lobby heartbeats
        rstats = fetch_json(ROUTER_STATS) or {}
        bycode = rstats.get("byCode", {}) or {}
        lobbies = []
        total_clients = 0
        # union of bridges seen in registry + running procs
        for bridge in sorted(set(list(reg.keys()) + list(lobby_acc.keys())), key=lambda b: int(b)):
            info = reg.get(bridge, {})
            code = info.get("code", "?")
            hb = parse_heartbeat(bridge)
            acc = lobby_acc.get(bridge, {"cpu": 0.0, "rss": 0})
            flows = int(bycode.get(code, 0))
            clients = hb["clients"] if hb else 0
            total_clients += clients
            lobbies.append({
                "code": code, "port": info.get("port", "?"), "bridge": bridge,
                "cpu": round(acc["cpu"], 1), "rssMB": acc["rss"] // (1 << 20),
                "flows": flows, "clients": clients,
                "spawned": hb["spawned"] if hb else 0,
                "rxps": hb["rxps"] if hb else 0.0,
                "tick": hb["tick"] if hb else 0,
                "running": bridge in lobby_acc,
            })

        sample = {
            "ts": round(now, 1),
            "cpu": cpu_pct, "ncpu": len(percore), "percore": percore,
            "load": read_loadavg(),
            "mem": read_meminfo(),
            "net": {"rxKBs": round(rx_rate / 1024, 1), "txKBs": round(tx_rate / 1024, 1)},
            "disk": {"root": disk_usage("/"), "tmp": disk_usage("/tmp")},
            "server": {
                "cpu": round(sf_cpu_total, 1), "rssMB": sf_rss_total // (1 << 20),
                "router": {"cpu": round(comp["router"]["cpu"], 1), "rssMB": comp["router"]["rss"] // (1 << 20)},
                "control": {"cpu": round(comp["control"]["cpu"], 1), "rssMB": comp["control"]["rss"] // (1 << 20)},
                "flows": int(rstats.get("flows", 0)),
                "lobbyCount": len(lobbies), "clients": total_clients,
            },
            "lobbies": lobbies,
        }
        with RING_LOCK:
            RING.append(sample)
        try:
            with open(self._today_path(), "a") as f:
                f.write(json.dumps(sample) + "\n")
        except Exception as e:
            print("[sf-monitor] write error:", e, flush=True)
        self.prev_t = now


# ---- HTTP -------------------------------------------------------------------
_LOG_BRIDGE_RE = re.compile(r"^\d{4,6}$")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, code, ctype, body):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = self.path.split("?")[0]
        q = dict(p.split("=", 1) for p in self.path.partition("?")[2].split("&") if "=" in p)
        if path == "/":
            self._send(200, "text/html; charset=utf-8", DASH.encode())
        elif path == "/api/now":
            with RING_LOCK:
                cur = RING[-1] if RING else {}
            self._send(200, "application/json",
                       json.dumps({"now": cur, "uptimeSec": round(time.time() - START_TS),
                                   "host": os.uname().nodename, "interval": INTERVAL}).encode())
        elif path == "/api/metrics":
            n = min(int(q.get("n", "240") or 240), RING.maxlen)
            with RING_LOCK:
                data = list(RING)[-n:]
            self._send(200, "application/json", json.dumps(data).encode())
        elif path == "/api/logs":
            self._serve_log(q)
        else:
            self._send(404, "text/plain", b"not found")

    def _serve_log(self, q):
        bridge = q.get("bridge", "")
        kind = q.get("kind", "plugin")
        n = min(int(q.get("n", "200") or 200), 2000)
        if not _LOG_BRIDGE_RE.match(bridge) or kind not in ("plugin", "unity"):
            self._send(400, "text/plain", b"bad params")
            return
        fname = f"/tmp/sf-oracle-{'plugin' if kind == 'plugin' else 'unity'}-{bridge}.log"
        try:
            with open(fname, "rb") as f:
                f.seek(0, 2)
                f.seek(max(0, f.tell() - 256 * 1024))
                lines = f.read().decode("utf-8", "replace").splitlines()[-n:]
            self._send(200, "text/plain; charset=utf-8", ("\n".join(lines)).encode())
        except Exception as e:
            self._send(404, "text/plain", str(e).encode())


# ---- dashboard (self-contained: no external JS/CDN) -------------------------
DASH = r"""<!doctype html><html><head><meta charset=utf-8>
<title>sf-monitor</title><meta name=viewport content="width=device-width,initial-scale=1">
<style>
*{box-sizing:border-box} body{margin:0;background:#0c0d10;color:#d8dade;font:13px/1.4 ui-monospace,Menlo,Consolas,monospace}
header{padding:10px 16px;background:#14151a;border-bottom:1px solid #23252c;display:flex;gap:18px;align-items:baseline;flex-wrap:wrap}
header h1{font-size:15px;margin:0;color:#8fb6ff;letter-spacing:.5px} header .sub{color:#6b6f78}
.wrap{padding:14px 16px;max-width:1400px;margin:0 auto}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:10px;margin-bottom:16px}
.card{background:#14151a;border:1px solid #23252c;border-radius:8px;padding:10px 12px}
.card .k{color:#6b6f78;font-size:11px;text-transform:uppercase;letter-spacing:.5px}
.card .v{font-size:22px;margin-top:3px} .card .v small{font-size:12px;color:#6b6f78}
.warn{color:#ffd479} .bad{color:#ff7b72} .ok{color:#7fdca4}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:14px} @media(max-width:900px){.grid2{grid-template-columns:1fr}}
.panel{background:#14151a;border:1px solid #23252c;border-radius:8px;padding:12px;margin-bottom:14px}
.panel h2{font-size:12px;margin:0 0 8px;color:#8a8f99;text-transform:uppercase;letter-spacing:.5px}
canvas{width:100%;height:120px;display:block}
table{width:100%;border-collapse:collapse;font-size:12px} th,td{text-align:left;padding:5px 8px;border-bottom:1px solid #1f2129}
th{color:#6b6f78;font-weight:normal} td.n{text-align:right;font-variant-numeric:tabular-nums}
select,#logbox{background:#0c0d10;color:#d8dade;border:1px solid #23252c;border-radius:6px;font:inherit}
#logbox{width:100%;height:260px;overflow:auto;white-space:pre;padding:8px;margin-top:8px}
.dot{display:inline-block;width:8px;height:8px;border-radius:50%;margin-right:6px}
.cores{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:5px 16px;margin:-4px 0 16px}
.core{display:flex;align-items:center;gap:7px;font-size:11px;color:#6b6f78}
.core .bar{flex:1;height:9px;background:#1a1a1f;border-radius:4px;overflow:hidden}
.core .bar i{display:block;height:100%}
.core .pct{width:32px;text-align:right;color:#8a8f99;font-variant-numeric:tabular-nums}
</style></head><body>
<header><h1>sf-monitor</h1><span class=sub id=host></span><span class=sub id=upt></span><span class=sub id=stamp></span></header>
<div class=wrap>
 <div class=cards id=cards></div>
 <div class=cores id=cores title="per-core CPU %"></div>
 <div class=grid2>
  <div class=panel><h2>CPU %</h2><canvas id=cpuC></canvas></div>
  <div class=panel><h2>RAM used (MB)</h2><canvas id=memC></canvas></div>
  <div class=panel><h2>Network (KB/s) — rx green / tx blue</h2><canvas id=netC></canvas></div>
  <div class=panel><h2>Server CPU (cores)</h2><canvas id=srvC></canvas></div>
 </div>
 <div class=panel><h2>Lobbies</h2><table id=lob><thead><tr>
   <th>code</th><th>port</th><th class=n>clients</th><th class=n>flows</th><th class=n>cpu(core)</th><th class=n>rss MB</th><th class=n>rx/s</th><th class=n>tick</th><th>state</th>
 </tr></thead><tbody></tbody></table></div>
 <div class=panel><h2>Lobby log</h2>
   <select id=logsel></select> <select id=logkind><option value=plugin>plugin</option><option value=unity>unity</option></select>
   <span class=sub>(auto-tails; logs live in tmpfs)</span>
   <div id=logbox>select a lobby…</div>
 </div>
</div>
<script>
const series={cpu:[],mem:[],netRx:[],netTx:[],ts:[]};
const srv=[];
const HIST=240;
function push(a,v){a.push(v); if(a.length>HIST)a.shift();}
function color(p,warn,bad){return p>=bad?'#ff7b72':p>=warn?'#ffd479':'#7fdca4';}
function chart(id,sets,opts){const c=document.getElementById(id),x=c.getContext('2d');
 const W=c.width=c.clientWidth*devicePixelRatio,H=c.height=120*devicePixelRatio;x.clearRect(0,0,W,H);
 let max=opts.max||0; sets.forEach(s=>s.data.forEach(v=>{if(v>max)max=v;})); max=max||1; max*=1.15;
 const fmt=opts.fmt||(v=>Math.round(v));
 x.strokeStyle='#23252c';x.lineWidth=1;for(let g=0;g<=2;g++){let y=H*g/2;x.beginPath();x.moveTo(0,y);x.lineTo(W,y);x.stroke();}
 x.fillStyle='#6b6f78';x.font=(11*devicePixelRatio)+'px monospace';x.textAlign='left';x.fillText(fmt(max)+(opts.unit||''),4,12*devicePixelRatio);
 sets.forEach((s,si)=>{const n=s.data.length;if(!n)return;const px=i=>W*i/(HIST-1),py=i=>H-(s.data[i]/max)*H;
  x.beginPath();s.data.forEach((v,i)=>{i?x.lineTo(px(i),py(i)):x.moveTo(px(i),py(i));});
  x.lineTo(px(n-1),H);x.lineTo(px(0),H);x.closePath();x.fillStyle=s.color+'1f';x.fill();
  x.strokeStyle=s.color;x.lineWidth=1.5*devicePixelRatio;x.beginPath();s.data.forEach((v,i)=>{i?x.lineTo(px(i),py(i)):x.moveTo(px(i),py(i));});x.stroke();
  x.fillStyle=s.color;x.textAlign='right';x.fillText(fmt(s.data[n-1])+(opts.unit||''),W-6,(14+si*15)*devicePixelRatio);});
 x.textAlign='left';}
function card(k,v,cls){return `<div class=card><div class=k>${k}</div><div class="v ${cls||''}">${v}</div></div>`;}
async function tick(){
 let r; try{r=await (await fetch('/api/now')).json();}catch(e){return;}
 const n=r.now; if(!n||!n.mem)return;
 document.getElementById('host').textContent=r.host;
 document.getElementById('upt').textContent='up '+Math.floor(r.uptimeSec/3600)+'h'+Math.floor(r.uptimeSec%3600/60)+'m';
 document.getElementById('stamp').textContent=new Date(n.ts*1000).toLocaleTimeString();
 push(series.cpu,n.cpu);push(series.mem,n.mem.usedMB);push(series.netRx,n.net.rxKBs);push(series.netTx,n.net.txKBs);
 const memPct=Math.round(n.mem.usedMB/n.mem.totalMB*100), l1=n.load[0], loadPct=l1/n.ncpu*100;
 document.getElementById('cards').innerHTML=
  card('CPU',n.cpu+'%',color(n.cpu,70,90))+
  card('RAM',memPct+'% <small>'+(n.mem.usedMB/1024).toFixed(1)+'/'+(n.mem.totalMB/1024).toFixed(1)+'G</small>',color(memPct,80,92))+
  card('Load',l1.toFixed(2)+' <small>/'+n.ncpu+'</small>',color(loadPct,75,100))+
  card('Lobbies',n.server.lobbyCount)+
  card('Clients',n.server.clients)+
  card('Server CPU',(n.server.cpu/100).toFixed(2)+' <small>cores /'+n.ncpu+'</small>')+
  card('Server RAM',(n.server.rssMB/1024).toFixed(1)+'G')+
  card('Net','↓'+n.net.rxKBs+' ↑'+n.net.txKBs+' <small>KB/s</small>')+
  card('tmpfs',n.disk.tmp?Math.round(n.disk.tmp.usedMB/n.disk.tmp.totalMB*100)+'%':'?')+
  card('swap',n.mem.swapUsedMB+' <small>MB</small>',n.mem.swapUsedMB>200?'warn':'');
 document.getElementById('cores').innerHTML=(n.percore||[]).map((p,i)=>'<div class=core><span>c'+i+'</span><span class=bar><i style="width:'+Math.max(2,p)+'%;background:'+color(p,70,90)+'"></i></span><span class=pct>'+Math.round(p)+'%</span></div>').join('');
 chart('cpuC',[{data:series.cpu,color:'#8fb6ff'}],{max:100,unit:'%'});
 chart('memC',[{data:series.mem,color:'#c39bff'}],{max:n.mem.totalMB,fmt:v=>(v/1024).toFixed(1)+'G'});
 chart('netC',[{data:series.netRx,color:'#7fdca4'},{data:series.netTx,color:'#8fb6ff'}],{unit:'KB/s'});
 push(srv,n.server.cpu/100); chart('srvC',[{data:srv,color:'#ffb86c'}],{unit:' cores',fmt:v=>v.toFixed(2)});
 // lobby table
 const tb=document.querySelector('#lob tbody');tb.innerHTML=n.lobbies.map(l=>{
  const st=l.running?'<span class=dot style=background:#7fdca4></span>up':'<span class=dot style=background:#ff7b72></span>down';
  return `<tr><td>${l.code}</td><td>${l.port}</td><td class=n>${l.clients}</td><td class=n>${l.flows}</td><td class=n>${(l.cpu/100).toFixed(2)}</td><td class=n>${l.rssMB}</td><td class=n>${l.rxps}</td><td class=n>${l.tick}</td><td>${st}</td></tr>`;}).join('');
 // log selector options
 const sel=document.getElementById('logsel'),cur=sel.value;
 const opts=n.lobbies.map(l=>`<option value=${l.bridge}>${l.code} (${l.bridge})</option>`).join('');
 if(sel.innerHTML!==opts){sel.innerHTML=opts; if(cur)sel.value=cur;}
}
async function tailLog(){
 const b=document.getElementById('logsel').value,k=document.getElementById('logkind').value;
 if(!b)return; const box=document.getElementById('logbox');
 try{const t=await (await fetch('/api/logs?bridge='+b+'&kind='+k+'&n=300')).text();
  const atBottom=box.scrollTop+box.clientHeight>=box.scrollHeight-30; box.textContent=t; if(atBottom)box.scrollTop=box.scrollHeight;}catch(e){}
}
setInterval(tick,3000); tick();
setInterval(tailLog,3000);
document.getElementById('logsel').addEventListener('change',tailLog);
document.getElementById('logkind').addEventListener('change',tailLog);
</script></body></html>"""


def main():
    host, _, port = BIND.rpartition(":")
    Sampler().start()
    httpd = ThreadingHTTPServer((host or "127.0.0.1", int(port)), Handler)
    print(f"[sf-monitor] sampling every {INTERVAL}s → {DATADIR}; dashboard on http://{BIND}", flush=True)
    httpd.serve_forever()


if __name__ == "__main__":
    main()
