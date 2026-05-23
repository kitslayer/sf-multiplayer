# What's new — 2026-05-23 session

Roughly 16 commits landed in one day. This file's a running tally so visitors and contributors can scan the deltas without scrolling git log. Headline items below; commit messages have the detail.

## Architecture additions

- **Phase 6.10 — Server-authoritative state snapshots** (msgType 39)
  Wire protocol: `u32 serverTick, u8 playerCount, [u8 slot, f32 x, f32 y, f32 z] × N, u16 nsoCount, [u16 id, f32 x, f32 y, f32 z, f32 rotZ] × M`. 30Hz broadcast from oracle to every spawned client.

- **Phase 6.11 — Client reconciliation plugin** ([`sf-client-recon/`](sf-client-recon/))
  New BepInEx + Harmony plugin: listens on UDP 1339 (configurable via `SFCLIENTRECON_PORT`), parses `WorldStateSnapshot`, snaps local NetworkPlayer to server position. Uncaps FPS.

- **Phase 6.11.2 — Smooth interpolation**
  Snapshot apply targets a dict; per-frame exponential lerp at rate=15/s toward latest target. No more 30Hz teleport jitter.

- **Phase 6.12 — Inbound `PktPlayerInput`** (msgType 40)
  Client sends stickX/stickY/aimX/aimY/buttons + sequence number at 60Hz from `SFClientRecon`. Server consumes into `SlotInputs[slot]` which existing `InjectInputPrefix` on `Controller.Update` writes to `Movement.cs` — server-side authoritative player motion.

- **Phase 6.12 hardening**
  Per-axis validation (NaN/Inf/extreme reject + clamp to [-1, 1]). Multi-instance v26 port via source-addr discovery — same machine can run two clients with different v26 ports without collision. Stale-client sweep cleans up per-slot v26 endpoint + rate guard.

- **Phase 6.13 v1 — Multi-process sharding**
  [`launch-lobby.sh`](launch-lobby.sh) / [`stop-lobby.sh`](stop-lobby.sh) / [`stop-all-lobbies.sh`](stop-all-lobbies.sh) / [`list-lobbies.sh`](list-lobbies.sh). Each lobby is its own SF.exe + wineprefix + UDP port + log; registry at `/tmp/sf-lobbies/`. ~500 MB RAM per lobby; ~8 lobbies fit on a hobby VPS.

- **Phase 6.13 v1.5 — HTTP lobby-browser endpoint**
  [`serve-lobbies.py`](serve-lobbies.py) serves `GET /lobbies` JSON for in-game / web server browsers.

- **Phase 6.14 — Server-authoritative NSO positions**
  Snapshot extended with NSO entries (boxes / pushed crates / ice debris). Client smoothly converges to server positions. Removes the "boxes drift across clients" desync class.

- **Phase 6.8 — Map-preset weapons broadcast**
  `CheckForGroundWeapons` added to `InvokeMultiplayerManagerInitChain`. Map-placed `WeaponPickUp` prefabs now broadcast via `GroundWeaponsInit` (msgType 31) on each scene load — clients see weapons at the level's designed positions.

- **Phase 6.15 v1 — Chat commands** (`/code` `/ping` `/start` `/help`)
  Body format confirmed from decompile (raw UTF-8). Server parses `/`-prefixed `PktPlayerTalked`, emits responses back via `SendChatToPlayer` (PktPlayerTalked with `steamID=0`, recipient's owner channel). `/code` reads from `SF_LOBBY_CODE` env var which `launch-lobby.sh` now sets. `/options`, `/join`, `/newlobby` deferred to Phase 6.13 v2 in-process sharding.

## Bug fixes (most absorbed from ALKA's [BUGS_BACKLOG](https://github.com/AlkaPrime12/Stickfight-TestingMultiplayer/blob/main/docs/BUGS_BACKLOG.md))

- **Ice / crate / chain destruction broadcast to all (ALKA P0-3).**
  `ObjectSimpleDestruction` (28) / `ObjectInvokeDestructionEvent` (29) / `ObjectDestructionCollision` (30) now relay to **all** clients including the sender. Previously the breaker's screen showed unbroken ice while others saw it shattered. Msg 29 was unhandled entirely.

- **`OptionsChanged` (37) + `PlayerTalked` (12) + `KickPlayer` (38)** relayed. Were falling through to unhandled-default.

- **Patched-DLL extension msgTypes** `LerpPlayer` (56) + `ColorChanged` (57) blind-relayed (ALKA P1-4). Stock SF's enum stops at 38; these are kit-patched DLL additions for remote-lerp + player-color sync.

- **Defensive try/catch around dispatch (ALKA P0-5).** A bad packet in one handler no longer poisons the rest of the 64-packet batch.

## Anticheat & ops

- **Observation-only rate guard (ALKA P0-1).** Per-client sliding-window queues for total / PlayerUpdate / damage / object packet rates. Logs warnings; doesn't drop yet — needs healthy-traffic telemetry before promoting to enforcer.

- **Heartbeat status line.** Every 30s the BepInEx log gets `clients=N spawned=M | rx=X/s snap=Y/s input=Z/s | rigs=K`. Gives a quick read on server health.

- **PlayerTalked telemetry.** First 20 chat packets get a hex+ASCII dump so we can decode the patched DLL's chat format for the future `/start`/`/code` admin commands (designed in [`notes/phase6/14-chat-commands.md`](notes/phase6/14-chat-commands.md)).

## Deploy & docs

- [`setup-all.sh`](setup-all.sh) — one-command build + drop into both plugins dirs.
- [`notes/VPS.md`](notes/VPS.md) — Path A VPS deployment guide (Proton, BepInEx, Goldberg, systemd template, firewall, monitoring, troubleshooting).
- [`notes/phase6/12-PHASE6.13-sharding.md`](notes/phase6/12-PHASE6.13-sharding.md) — in-process sharding design (v2, design-only; v1 multi-process is shipped).
- [`notes/phase6/13-rewind-buffer.md`](notes/phase6/13-rewind-buffer.md) — CSGO-style lag-comp design (future Phase 6.14.5).
- [`notes/phase6/14-chat-commands.md`](notes/phase6/14-chat-commands.md) — `/start`, `/code`, etc. admin interface design (future Phase 6.15).

## Repo housekeeping

- Branch reorg: `phase-6-headless` content force-promoted to `main`; legacy branch deleted. `legacy/` subdir holds parked Go server + earlier client plugins.

## Where we are vs ALKA (parallel project [AlkaPrime12/Stickfight-TestingMultiplayer](https://github.com/AlkaPrime12/Stickfight-TestingMultiplayer))

**Ahead of him:**
- v26 protocol live end-to-end (his is "Fase 3 future work")
- Server-authoritative NSO snapshots (he has only the scene-shard part of his world-shard design)
- HTTP `/lobbies` JSON endpoint
- Snapshot smoothing on client
- Chat commands actually implemented (he has them in design + DLL emit, server-side parser is now ours)

**Behind him:**
- Damage authority — we now have v0.1 (magnitude + attacker-idx sanity); his is fuller (range check, weapon match, alive check). Full version waits on Phase 6.14.5 rewind buffer.
- Server-side rewind buffer for lag-comp — designed, not shipped
- His one-click Windows `.bat` deploy scripts — we have Linux equivalents but no Windows yet
- Active launcher binary (he has a Go launcher that auto-patches the DLL)
- Workshop maps loading at runtime

**Same:**
- Spawn-flag fix (`flag=1` insta-die bug — both fixed)
- v25 raw-UDP protocol implementation
- Goldberg + Proton dev setup

## Test status

- Oracle's been running on `localhost:1337` via `launch-lobby.sh TEST` throughout the session.
- No live multiplayer test against the most recent build yet (mid-session deploys).
- Next live test should verify: ice break visible to breaker, boxes propagate identically across clients, no anticheat false-positives at normal play rates.
