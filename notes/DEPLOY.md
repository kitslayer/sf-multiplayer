# Deploying the Phase 6 oracle to a server

The Phase 6 oracle is a headless Stick Fight instance running under Proton with our BepInEx plugin loaded. Below is the canonical setup for a fresh Linux server (Ubuntu 22.04 / 24.04 tested; should work on most modern distros with a working Wine ABI).

> `SERVER` below is a placeholder for your VPS host. The live deployment is `69.53.117.43` (player connect: game UDP **1337** via the [`sf-router`](ROUTER.md), lobby browser TCP **8080**). With the router in front, players reach every lobby through the single public UDP port 1337 — the per-lobby `1337+N` ports in the multi-lobby section below are the router's loopback backends, not separate public ports.

## Target: a Linux VPS with…

- 4+ GB RAM (per oracle ~600 MB; comfortable host = 4 GB + 600 MB × N lobbies)
- 2+ CPU cores (per oracle ~0.5 idle vCPU)
- 32-bit Wine library support (`i386` multi-arch on Debian/Ubuntu)
- **`xvfb` package** (Wine needs a virtual X11 display even in `-batchmode -nographics` mode — without it SF.exe loads but hangs at 0% CPU forever at `nodrv_CreateWindow`)
- `steam-installer` package (apt) for the Steam-runtime i386/amd64 libs Proton expects, OR manually rsync `~/.local/share/Steam/{ubuntu12_32,ubuntu12_64,linux32,linux64}` from a desktop install
- UDP port forwarding from public IP → 1337+ for connect, optional TCP 8080 for the lobby browser HTTP endpoint
- `rsync`, `unzip`, `python3`, `systemd`, basic GNU coreutils

## Files this deploy expects (under `~/sf-oracle/` on the server)

```
~/sf-oracle/
├── install/                 ← the StickFight install (StickFight.exe + StickFight_Data/ + BepInEx/)
│   └── BepInEx/plugins/
│       ├── SFHeadlessHost.dll      (our server plugin)
│       └── SFClientRecon.dll       (client plugin — also gets deployed here on the oracle; it no-ops in batchmode)
├── proton/                  ← bundled Proton install (the `proton` python script + `dist/` subdir)
├── runtime/                 ← used as STEAM_COMPAT_CLIENT_INSTALL_PATH; can be empty
└── prefix-<bridgeport>/     ← per-oracle wineprefix, created on first boot
```

## Server prep (one-time, needs sudo)

```bash
sudo apt-get update
sudo dpkg --add-architecture i386
sudo apt-get install -y \
  libc6:i386 libstdc++6:i386 libfreetype6:i386 \
  libgl1:i386 libgl1-mesa-dri:i386 libvulkan1:i386 zlib1g:i386 \
  libx11-6:i386 libxext6:i386 libxcomposite1:i386 libxrandr2:i386 libxxf86vm1:i386 \
  xvfb steam-installer
```

`steam-installer` pulls in the right Steam-runtime metapackages (`steam-libs:amd64`, `steam-libs:i386`, `steam-libs-i386:i386`) without launching the Steam GUI. `xvfb` is required because Wine inside Proton can't run without a display driver even in batchmode — `xvfb-run` provides a tiny virtual X11 server.

## Deploy from a local working machine

```bash
# 1. Sync SF install (your sf-mirror-local with the patched Assembly-CSharp.dll)
rsync -az --info=progress2 \
  --exclude='2026-*/' --exclude='*.dmp' \
  --exclude='__pycache__' --exclude='BepInEx/LogOutput.log' \
  ~/sf-mirror-local/ \
  miles@SERVER:~/sf-oracle/install/

# 2. Sync Proton (find your local copy — Steam puts it under
#    ~/.local/share/Steam/steamapps/common/Proton - *)
rsync -az ~/.local/share/Steam/steamapps/common/Proton\ -\ Experimental/ \
  miles@SERVER:~/sf-oracle/proton/

# 3. Sync the sf-multiplayer project (scripts + deploy files)
rsync -az --exclude='.git' --exclude='__pycache__' --exclude='refs' \
  --exclude='**/bin/' --exclude='**/obj/' --exclude='legacy' \
  ~/sf-multiplayer/ \
  miles@SERVER:~/sf-multiplayer/

# 4. Mark scripts executable
ssh miles@SERVER 'chmod +x ~/sf-multiplayer/deploy/start-oracle-server.sh'
```

## Systemd service install

```bash
# 1. Copy the unit file to the server
scp ~/sf-multiplayer/deploy/sf-oracle.service miles@SERVER:/tmp/

# 2. Install + enable (needs sudo on the server)
ssh miles@SERVER 'sudo mv /tmp/sf-oracle.service /etc/systemd/system/sf-oracle.service \
  && sudo systemctl daemon-reload \
  && sudo systemctl enable sf-oracle.service \
  && sudo systemctl start sf-oracle.service'

# 3. Watch it boot
ssh miles@SERVER 'journalctl -u sf-oracle.service -n 50 -f'
```

## Verify it works

From any machine that can reach the server's UDP port:

```bash
# 14-byte v25 Ping packet — server should reply within ~50ms with another 14 bytes
python3 -c "
import socket, struct, time
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.settimeout(2)
s.sendto(struct.pack('<IBQB', int(time.time()), 0, 0, 0), ('SERVER', 1337))
data, _ = s.recvfrom(64); print(f'ok — {len(data)} bytes back')
"
```

## Multi-lobby on one server

`launch-lobby.sh` works on the server too. It picks the next free port (skipping the V26_CLIENT_PORT range 1339/1340) and spawns a new oracle:

```bash
ssh miles@SERVER '~/sf-multiplayer/launch-lobby.sh ALPHA'
ssh miles@SERVER '~/sf-multiplayer/launch-lobby.sh BRAVO'
ssh miles@SERVER '~/sf-multiplayer/launch-lobby.sh CHARLIE'
```

Each has its own:
- UDP port (1337, 1338, 1341 — skipping 1339/1340)
- Wineprefix (`~/sf-oracle/prefix-<bridgeport>/`)
- Unity log (`/tmp/sf-oracle-unity-<bridgeport>.log`)
- Plugin log (`/tmp/sf-oracle-plugin-<bridgeport>.log`)

## Optional: HTTP lobby browser

For a webpage that shows the running lobbies + lets users copy connect strings:

```bash
ssh miles@SERVER 'nohup python3 ~/sf-multiplayer/serve-lobbies.py --port 8080 > /tmp/serve-lobbies.log 2>&1 &'
```

Then players open `http://SERVER:8080/` or the standalone `deploy/server-browser.html` pointed at `http://SERVER:8080/lobbies`.

## Resource accounting (measured 2026-05-23 on i7-7700 / 15GB RAM)

| Per-oracle | Idle (no players) | With players |
|---|---|---|
| RAM RSS | ~600 MB | ~700 MB |
| CPU (1 core) | ~42% | ~50-70% |
| Disk (logs) | minimal | ~few MB/min plugin log |

So a 4-vCPU box realistically hosts **3-4 concurrent oracles** at idle, fewer once busy. RAM is the cheaper limit (4 GB → 6 oracles). CPU is the bottleneck on most VPS instances.

## Known issues

- **Native crashes** in `StickFight.exe` at EIP `0x0057ed26` happen during gameplay (see [`CRASH_INVESTIGATION.md`](CRASH_INVESTIGATION.md)). systemd auto-restart catches these but matches lose state.
- **Port 1339 + 1340 reserved** for the v26 client snapshot listener — if you bind these as a game port, local clients won't be able to receive snapshots.
- **Proton's STEAM_COMPAT_CLIENT_INSTALL_PATH** is set to a stub directory (`~/sf-oracle/runtime`) because we don't install full Steam on the server. If Proton complains about missing Steam Runtime libs, you may need to either install `steam-launcher` (apt) or use a Steam-runtime-free Proton fork like Proton-GE.
- **Unity batchmode is CPU-hungry** even when idle (Unity's main loop runs at ~60Hz regardless of activity). Adjusting `Application.targetFrameRate` could help but requires plugin work.

## Updating an existing deploy

After local code changes + `setup-all.sh`:

```bash
# Sync new plugin DLLs
rsync -az ~/sf-mirror-local/BepInEx/plugins/SFHeadlessHost.dll \
  ~/sf-mirror-local/BepInEx/plugins/SFClientRecon.dll \
  miles@SERVER:~/sf-oracle/install/BepInEx/plugins/

# Sync any new scripts/configs
rsync -az --exclude='.git' ~/sf-multiplayer/deploy/ \
  miles@SERVER:~/sf-multiplayer/deploy/

# Restart
ssh miles@SERVER 'sudo systemctl restart sf-oracle.service'
```

The systemd unit's auto-restart will pick the new plugin up on next boot. md5 the plugins before and after if you want certainty.

## Where to look when things break

| Symptom | First place to check |
|---|---|
| Port not bound | `journalctl -u sf-oracle.service -n 100` — Proton boot errors usually print here |
| Oracle keeps restarting | `journalctl -u sf-oracle.service` — check exit code + crash dumps in `~/sf-oracle/install/2026-*` |
| Plugin logs nothing | `/tmp/sf-oracle-plugin-11337.log` — should have the boot banner + per-frame heartbeat |
| Unity errors | `/tmp/sf-oracle-unity-11337.log` — the Unity-level log (huge — flood means a per-frame exception) |
| Crash dumps | `~/sf-oracle/install/<TIMESTAMP>/{crash.dmp,error.log,output_log.txt}` |
| Client can't see server | UDP firewall (`sudo ufw status`); check from outside-the-LAN if VPS |
