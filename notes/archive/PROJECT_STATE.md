# sf-multiplayer · project state

> ⚠️ **Superseded (2026-06-06).** Point-in-time snapshot — the living current-state doc is [`NEXT_STEPS.md`](NEXT_STEPS.md). Live server: `69.53.117.43`.

> **As of 2026-05-23 night** — for sharing with ALKA + SF comp Discord. Tone is "what comp players need to know" + "where the project actually is."
>
> Live status mirror: [`STATUS.md`](STATUS.md) (updated continuously while work happens).

## TL;DR

A centralized server for Stick Fight that **works end-to-end on a real server** (69.53.117.43 right now), with a **one-click Windows installer/launcher**, and a **one-line Linux installer**. Server has known crash issues (documented, not yet patched). Code is on GitHub: https://github.com/kitslayer/sf-multiplayer

## What works today

- ✅ **Server** runs as a real BepInEx plugin inside a headless StickFight.exe under Proton. Speaks SF's stock v25 UDP protocol + a v26 extension for server-authoritative state.
- ✅ **End-to-end connect** — patched Steam SF (with our 2 BepInEx plugins) connects directly to the server, no Steam-side networking involved.
- ✅ **Match flow** — handshake, spawn, weapon pickup/throw/drop, death + kill propagation, round advance, 123-map rotation, in-game chat.
- ✅ **Server-side NSO physics** — boxes / chains / ice positions broadcast from the oracle at 30Hz; clients smoothly converge.
- ✅ **Server-side platform sync (v26.5)** — moving platforms (`MoveAlongPathUsingForce`, `PillarHandler`, `GhostPlatform`) at the same position on every client.
- ✅ **Server-side projectile hit registration** — bullets simulated server-side, hits emit authoritative damage (Phase 6.17 v0.3).
- ✅ **Shift-correction reconciliation** — canonical CSGO/Valorant netcode model: server corrections applied as offset to current local position, not "snap back to stale server pos."
- ✅ **Multi-lobby** — one server can host multiple concurrent matches on adjacent UDP ports. Each lobby is fully isolated.
- ✅ **In-game admin chat** — `/help`, `/start`, `/restart`, `/next`, `/map <N>`, `/listmaps`, `/players`, `/lobbies`, `/tickrate <Hz>`, `/weapons <list>`, `/kick <slot>`, `/anticheat <on|off>`, `/code`, `/ping`, `/version`.
- ✅ **Lobby browser** — HTTP `/lobbies` JSON endpoint + standalone GUI app + dark-themed web page.
- ✅ **One-click client install** — Windows `SFLauncher.exe` auto-installs BepInEx + plugins on first run; Linux `sflauncher.sh` does the same in one command.
- ✅ **Production server (69.53.117.43)** — running 24/7 under systemd auto-restart. Connect with `-address 69.53.117.43 -port 1337` in Steam launch options.

## Known issues being tracked

| ID | Symptom | Status |
|---|---|---|
| **CRASH** | Oracle native access violation (`0xc0000005`) at EIP `0x0057ed26`, hits same instruction 5x in a row in `lock xadd dword ptr [eax], ecx`. Deterministic, not a race. Probably an NSO event-channel iteration race or Steam-runtime DLL-loading issue. | **Documented** in [`notes/CRASH_INVESTIGATION.md`](notes/CRASH_INVESTIGATION.md). No patch — needs StickFight.exe symbols to attribute. systemd auto-restarts when it happens (~10s downtime). |
| **OPEN-1..OPEN-6** | User-reported gameplay bugs from earlier testing (void/lava no damage; chains/ice/boxes random break). Most likely fixed by reverts in commit `4affabc`, but **untested on the deployed build**. | Need a real client session to verify. |
| **Phase 6.17 v0.4** | Server-side projectile hit detection uses a 1.2u sphere; misses some shots if the rig's torso position differs from its root transform. Tracked, not blocking. | Polish. |
| **Workshop maps** | Only the 123 pre-dumped Landfall scenes are supported. Workshop maps require runtime asset-bundle loading. | Future; comp scene doesn't use these per user. |
| **Full input-replay rollback** | Phase 6.12.2 v1.0 ships shift-correction reconciliation but not the full Movement state-restore replay loop. Edge cases under pathological lag could feel rubber-bandy. | Polish; canonical end-state but real edge cases are rare. |

## What's NOT working / NOT shipped

- Full server-authoritative damage (clients still emit damage; server validates magnitude + range, but the damage source itself is client-emitted)
- Anticheat enforcement is observation-only by default (set `SF_ANTICHEAT_ENFORCE=1` to drop offending packets — needs threshold tuning)
- Replay system (snapshot stream design works for this — not built yet)
- ELO/MMR
- Real-time monitoring / alerting on server
- Multi-region hosting

## How a comp player joins right now

### Windows (1 click)
1. Download [`SFLauncher.exe`](https://github.com/kitslayer/sf-multiplayer/blob/main/dist/SFLauncher.exe) (~71MB, single self-contained file)
2. Double-click. First run: it auto-installs BepInEx + plugins into your Steam Stick Fight. The lobby browser window opens.
3. Pick a lobby, click Connect. The Steam launch options copy to clipboard. Steam opens.
4. Right-click Stick Fight → Properties → Launch Options → paste → close → click Play.

### Linux (1 command)
```bash
curl -O https://github.com/kitslayer/sf-multiplayer/raw/main/dist/sflauncher.sh
chmod +x sflauncher.sh
./sflauncher.sh
```
It downloads BepInEx + plugins, sets up the Steam SF install, opens a lobby-browser page in your default browser. Set the printed Steam launch options and play.

## How a server operator hosts

Full guide in [`notes/DEPLOY.md`](notes/DEPLOY.md). Tldr:
1. Linux box with 4GB+ RAM, 2+ CPU cores
2. `sudo apt install -y libc6:i386 libstdc++6:i386 libfreetype6:i386 libgl1:i386 libgl1-mesa-dri:i386 libvulkan1:i386 zlib1g:i386 libx11-6:i386 libxext6:i386 libxcomposite1:i386 libxrandr2:i386 libxxf86vm1:i386 xvfb steam-installer` (the `xvfb` part is non-obvious; Wine needs a virtual X display even in `-batchmode -nographics`)
3. rsync your local working sf-mirror-local SF install + a Proton install + Steam runtime libs to the server
4. Install the `sf-oracle.service` systemd unit (in `deploy/`)
5. `systemctl start sf-oracle.service` and you're up

Measured resource use: ~600MB RAM, ~50-70% of one vCPU per oracle.

## Repository structure

```
sf-multiplayer/
├── sf-headless-host/     ← server plugin source (~6,400 lines C#, + ~1,280 map-terrain helper)
├── sf-client-recon/      ← client plugin source (~4,200 lines C#)
├── deploy/
│   ├── SFLauncher/       ← Windows GUI source (.NET 8 WinForms)
│   ├── start-oracle-server.sh
│   ├── sf-oracle.service
│   ├── install-sf-client.bat / .sh
│   └── server-browser.html
├── dist/                 ← what comp players download
│   ├── SFLauncher.exe    ← one-click Windows
│   ├── sflauncher.sh     ← one-click Linux/macOS
│   ├── SFHeadlessHost.dll
│   └── SFClientRecon.dll
├── notes/
│   ├── ARCHITECTURE.md
│   ├── PROTOCOL.md
│   ├── OBJECT_SYNC.md
│   ├── DEPLOY.md
│   ├── BUGS_BACKLOG.md
│   ├── CRASH_INVESTIGATION.md
│   └── ... (40+ design + research docs)
├── README.md
├── NEXT_STEPS.md
├── STATUS.md             ← live status while work happens
├── PROJECT_STATE.md      ← (this file)
└── WHATS_NEW.md
```

## Project relationship with ALKA / other devs

- Early on we shared research with ALKA's [Stickfight-TestingMultiplayer](https://github.com/AlkaPrime12/Stickfight-TestingMultiplayer); ALKA has since stepped away from this project (see README credits)
- Independent codebase (this repo, BepInEx + Harmony on top of stock SF DLLs) but compatible wire-protocol family with stock SF v25
- Bug-fix ideas were shared both directions during that period

## Recent session work (2026-05-23)

> ~70 commits in one day across:
> - Phase 6.19 — six P0 bug fixes (P0-11..P0-15 + P1-8) for boxes/platforms/ice/late-join
> - Phase 6.12.2 v1.0 — shift-correction reconciliation
> - Phase 6.17 v0.2/v0.3 — server-side hit reg + wall occlusion + particles
> - Phase 6.20 — chat command admin suite (`/weapons`, `/kick`, `/anticheat`, `/map`, `/listmaps`)
> - Phase 6.21 — suppress destruction events from `WeaponPickUp` collisions (the gun-spawn-breaks-chain fix)
> - Phase 6.22 — per-lobby logs + reserved-port skip
> - Crash investigation + per-lobby log listener fix
> - .115 deployment with systemd, xvfb-run wrap, Steam runtime libs
> - Client installer + GUI lobby browser (Windows native .exe + Linux script)

Full timeline in [`notes/SESSION_2026-05-23.md`](notes/SESSION_2026-05-23.md).

## What's safe to share with comp Discord

- The `dist/` folder content (one-click installers + plugins) — totally safe to share
- The server IP if you want others to test: `-address 69.53.117.43 -port 1337`
- A pointer to https://github.com/kitslayer/sf-multiplayer for anyone who wants to read code

## What's NOT safe to share yet

- The crash issue should be tested more before claiming "stable for tournaments"
- Anticheat is observation-only — don't claim it's secure
- Server-side authoritative damage isn't fully wired — don't claim cheaters can't fake hits

## Code review findings (2026-05-23 night, recon-only)

Two parallel code-review agents audited the client + deploy code. Summary:

### SFLauncher.exe (Windows GUI) — clean
- 0 P0 (no crash/break-first-run issues)
- 1 P1: ZIP path-traversal defense missing in BepInEx extractor. Low risk in practice (BepInEx is an official GitHub release) but should harden.
- 3 P2: cosmetic / already-correct patterns

### Linux/macOS scripts — two real P0s to fix tomorrow

| ID | File:Line | Issue |
|---|---|---|
| **L-P0-1** | `dist/sflauncher.sh:115-116` + `dist/install-sf-client.sh:59-66` | `DL()` wrapper doesn't check curl/wget exit status. If BepInEx download fails (404, network down, GitHub blip), `unzip` silently fails and the user has a broken install with no warning. |
| **L-P0-2** | `deploy/start-oracle-server.sh` mode `0644` | Script is NOT executable in the repo. systemd ExecStart=… would fail "Permission denied" unless wrapped in `bash …`. |
| L-P1-1 | `dist/sflauncher.sh:51-57` | Steam install detection skips `$XDG_DATA_HOME/Steam` (some Linux users with custom XDG dirs won't auto-detect). |
| L-P1-2 | `deploy/sf-oracle.service:11,29` | Hardcodes `/home/miles/...` — only works for user `miles`. Should template with `%h` (systemd home dir) or `User=` substitution. |
| L-P1-3 | both .sh files | `trap "rm -rf $TMP" EXIT` should be single-quoted to defer expansion. Harmless in current code. |
| L-P2 | `sflauncher.sh:147` | Re-running appends a `<!-- prefilled URL: … -->` comment to the cached server-browser.html each time. Cosmetic accumulation. |

**None of these affect the running server on .115** (it's already up + executable bit is set on the deployed copies). They only affect FRESH installs by other users.

## TODO for next session (what would be good to do tomorrow)

1. **Fix L-P0-1 + L-P0-2** (Linux install bugs above) — small, high-confidence fixes
2. Real-client testing on .115 — verify OPEN-1..OPEN-6 are gone
3. Reproduce the native crash with more instrumentation + try to pin down the trigger
4. Tune the gunfight hit-reg radius if testing reveals shots-missing issues
5. Document the v26.5 wire protocol in PROTOCOL.md (currently in `notes/AUDIT_2026-05-23.md`)
6. Maybe build an Avalonia version of SFLauncher for Linux to give Linux comp players a real native GUI
