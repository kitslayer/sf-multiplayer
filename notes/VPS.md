# VPS deployment guide

How to host an sf-multiplayer Path A server on a Linux VPS. Mirrors what ALKA's `docs/VPS.md` does for his hybrid architecture, but for our single-Unity-process model.

## What's needed

- Linux VPS — Debian/Ubuntu 22+ or any distro with glibc ≥ 2.31. Tested on Gentoo. 1 vCPU + 1 GB RAM per concurrent lobby. So 2 vCPU + 2 GB is a comfortable single-lobby spec; 8 vCPU + 8 GB hosts ~8 lobbies.
- A purchased Stick Fight install (we can't ship the .exe).
- Proton — community build, latest stable. Doesn't require a graphical session.
- BepInEx 5.4.x — community-supplied, drops into the SF install root.
- Goldberg `steam_api.dll` shim — see [docs](https://github.com/Detanup01/gbe_fork/releases) (use the 32-bit `x32` build; Stick Fight is 32-bit).
- A patched `Assembly-CSharp.dll` (the v25 raw-UDP variant — same one the players use).

This repo's plugins (`SFHeadlessHost.dll`, optional `SFClientRecon.dll`) get built on the VPS or copied over.

## One-time setup

1. **Install Proton and dotnet.**
   ```bash
   # Proton: easiest via Steam (steamcmd), or grab a portable Proton-GE release.
   # Dotnet SDK 8+:
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
   echo 'export PATH=$HOME/.dotnet:$PATH' >> ~/.bashrc
   ```

2. **Place Stick Fight in `~/sf-mirror-local/`.** Either copy from a local install or fetch via `steamcmd +login anonymous +force_install_dir ~/sf-mirror-local +app_update 674940 +quit` (needs ownership credentials for SF since it's not free-to-play).

3. **Drop in BepInEx + Goldberg + the patched DLL.** Tree should look like:
   ```
   ~/sf-mirror-local/
   ├── StickFight.exe
   ├── winhttp.dll              (BepInEx loader)
   ├── BepInEx/                 (BepInEx core + config)
   ├── StickFight_Data/
   │   ├── Managed/
   │   │   └── Assembly-CSharp.dll       (the patched v25 DLL)
   │   └── Plugins/
   │       ├── steam_api.dll             (Goldberg)
   │       ├── steam_api.real.dll        (original, renamed backup)
   │       └── steam_settings/
   │           ├── steam_appid.txt       (just "674940")
   │           ├── configs.main.ini      (offline=1, etc.)
   │           └── configs.user.ini      (your fake SteamID)
   ```

4. **Clone this repo and build.**
   ```bash
   git clone https://github.com/kitslayer/sf-multiplayer.git
   cd sf-multiplayer
   # Copy reference DLLs into sf-headless-host/refs/ from your SF install:
   #   Assembly-CSharp.dll, UnityEngine.dll, BepInEx.dll, 0Harmony.dll
   ./setup-all.sh
   ```

   `setup-all.sh` builds + drops `SFHeadlessHost.dll` into the oracle install's `BepInEx/plugins/`. It'll also try to install `SFClientRecon.dll` to a Steam SF install — on a VPS you don't have one, so that step will SKIP. That's fine; the client plugin only matters on player machines.

## Running

**Single lobby on port 1337:**
```bash
./launch-lobby.sh TEST 1337
```

**Multiple lobbies, auto-port:**
```bash
./launch-lobby.sh AAAA
./launch-lobby.sh BBBB
./launch-lobby.sh CCCC
./list-lobbies.sh
```

Each lobby gets a free port in the `1337-1346` default range (configurable via `SF_BASE_PORT` / `SF_MAX_LOBBIES`). Stop with `./stop-lobby.sh CODE` or nuke all with `./stop-all-lobbies.sh`.

**As a systemd service** — drop into `/etc/systemd/system/sf-lobby@.service`:
```ini
[Unit]
Description=Stick Fight oracle for lobby %i
After=network.target

[Service]
Type=simple
User=miles
WorkingDirectory=/home/miles/sf-multiplayer
ExecStart=/home/miles/sf-multiplayer/launch-lobby.sh %i
ExecStop=/home/miles/sf-multiplayer/stop-lobby.sh %i
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Then `systemctl enable --now sf-lobby@AAAA`. Each lobby is its own templated unit.

## Firewall

```bash
# UDP for all the lobby ports
sudo ufw allow 1337:1346/udp comment 'sf-multiplayer lobbies'
```

The v26 client-side port (1339) is **only inbound on player machines**, not on the VPS. The VPS sends snapshots TO `clientIP:1339`, so that flow leaves the VPS on the v25 port (1337-1346) and arrives at the player's `IPAddress.Any:1339` listener.

## Player connection

Players install `SFClientRecon.dll` to their own `<SF install>/BepInEx/plugins/` (separate from the VPS install — they need Steam SF + the patched DLL + the plugin). Then Steam launch options:
```
WINEDLLOVERRIDES="winhttp=n,b" %command% -address <vps-public-ip> -port 1337
```

Or on Windows: same launch options without the `WINEDLLOVERRIDES` (since SF is already Windows-native there).

## Monitoring

- BepInEx logs: `~/sf-mirror-local/BepInEx/LogOutput.log` (shared across lobbies; recent activity from all of them is interleaved here)
- Unity log per lobby: `/tmp/sf-oracle-unity-${bridge-port}.log` (separate per lobby since bridge port differs)
- Lobby registry: `/tmp/sf-lobbies/CODE.conf` per running lobby

A simple `journalctl -u 'sf-lobby@*' -f` works if you used systemd.

## Resource budgeting

- One headless SF instance ≈ 400-600 MB RAM, ~50-80% of one CPU core when idle, more during matches.
- Disk: SF install is ~1.5 GB. Each wineprefix is ~150 MB.
- Network: roughly 5-10 KB/s per active connected player (combined inbound + outbound). At 4 players × 8 lobbies = 32 players, that's <500 KB/s — trivial for any VPS.

## Known limits (Path A v1)

- One oracle per lobby (no in-process sharding yet — see [`notes/phase6/12-PHASE6.13-sharding.md`](phase6/12-PHASE6.13-sharding.md))
- No web-based lobby browser yet. Players need to know the lobby's port out-of-band (Discord, server browser plugin coming).
- No workshop-map support (only the 123 pre-dumped Landfall scenes).

## When something breaks

- Oracle won't start: check `/tmp/sf-oracle-unity-*.log` for Unity crash + `~/sf-mirror-local/BepInEx/LogOutput.log` for plugin errors.
- Players can't connect: verify UDP port open in firewall; `ss -lunp | grep :1337` on the VPS confirms the oracle is listening.
- Ice/crates desync: confirm both sides on the latest build (md5 of `SFHeadlessHost.dll` on VPS matches latest CI; players' `SFClientRecon.dll` matches).
