# What's new — 2026-05-23 session

**60+ commits** landed in one day. Running tally so visitors can scan the deltas without scrolling git log. See [`notes/ARCHITECTURE.md`](notes/ARCHITECTURE.md) for the system overview, [`notes/PROTOCOL.md`](notes/PROTOCOL.md) for the wire-format spec, [`notes/BUGS_BACKLOG.md`](notes/BUGS_BACKLOG.md) for the bug incident log.

## End-state goal (confirmed 2026-05-23)

**Client-side prediction + server-authoritative simulation + client reconciliation** — canonical CS/Valorant/Overwatch netcode. Foundations all shipped this session; full input-replay rollback + local NSO prediction are the remaining big steps before the loop closes.

## Sharding state

**Multi-process sharding (v1) is shipped** — `launch-lobby.sh CODE` spawns a fully isolated SF.exe oracle per lobby, each with its own UDP port + wineprefix + log. `serve-lobbies.py` exposes the running set as JSON for browsers. `list-lobbies.sh` tabulates. ~500MB RAM + 1 vCPU per lobby; a hobby VPS handles 6-8 concurrent.

**In-process sharding (v2)** is design-only. One SF.exe running N additive scenes at Z-offset with per-shard state isolation. Documented in [`notes/phase6/12-PHASE6.13-sharding.md`](notes/phase6/12-PHASE6.13-sharding.md). The hard part is SF's singleton-heavy code: `MultiplayerManager.Instance`, `GameManager.Instance`, etc. all assume one global match. Would need either Harmony-dispatch on `Instance` getters by shard, or careful state save/restore around each call. ALKA's `WorldShardManager.cs` ships the scene-management piece but his `applyInput`/`damage`/etc. are also marked "next: scoped per shard" — not done. For comp scale, v1 is sufficient.

## P0 bugs found + fixed during live multi-client testing

(Full details in `notes/BUGS_BACKLOG.md`.)

- **PlayerUpdate forwarded on channel 0** instead of `slot*2 + 2` — `NetworkPlayer.InitNetworkSpawnID` sets a per-slot receive channel; forwards on channel 0 never reach any NetworkPlayer's listener. Fixed by preserving the incoming channel through `HandlePlayerUpdate`.
- **SteamID overwrite from envelope** — SF's `SendP2PPacketToUser` puts the *destination's* SteamID in envelope, not sender's. Our blind `cli.SteamID = envelope.steamID` corrupted records when `OnClientJoined.PingAllUsers()` fired. Fixed by setting SteamID exactly once from `ClientRequestingIndex` body.
- **PktClientSpawned before PktClientJoined** — `OnPlayerSpawned` reads `mConnectedClients[b]` (populated by `OnClientJoined`) at line 1623 of decompile. Sending Spawned first NullRef'd existing peers' rig wire-up. Fixed by reversing the order.
- **`Spawned` gate stale after /start** — `BroadcastStartMatch` resets `cli.Spawned=false` per round; `HandlePlayerUpdate` gated on Spawned → stopped forwarding the moment match started. Fixed by gating on `cli.Initialized` (set permanently after ClientInit).
- **Cross-client NSO authority fight** — every client had `mHasControl=true` via the client-shim → all of them broadcast their own physics → boxes desync'd, randomly destructed. Fixed by removing the client-shim entirely; oracle is sole authority now (server-authoritative model).
- **Server-originated destruction events broadcast to clients** — chains/ice on the oracle stress-broke under joint forces at scene load; SendBroadcastPrefix forwarded those destructions → clients removed intact local objects. Fixed by skipping server-originated msgType 28/29/30 outbound (kept inbound relay-to-all path intact).
- **`stop-lobby.sh` killed user's game windows** — `pkill -f StickFight.exe.*-port 1337` matched both clients too. Fixed by matching the lobby-specific `-logFile` instead.

## Architecture additions

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

- **Phase 6.15 v1 — Chat commands** (`/code` `/ping` `/start` `/players` `/lobbies` `/version` `/help`)
  Body format confirmed from decompile (raw UTF-8). Server parses `/`-prefixed `PktPlayerTalked`, emits responses back via `SendChatToPlayer` (PktPlayerTalked with `steamID=0`, recipient's owner channel). `/code` reads `SF_LOBBY_CODE`; `/lobbies` reads `/tmp/sf-lobbies/*.conf`. `/options`, `/join`, `/newlobby` deferred to Phase 6.13 v2 in-process sharding.

- **Phase 6.15.1 — Welcome chat on spawn**
  Server emits "Welcome to lobby {code}. Type /help for commands." once per SfClient when they first hit `ClientRequestingToSpawn`. Mirrors ALKA's `sendJoinHelpMessages` UX.

- **Phase 6.9.5 — Ghost-rig box pushing (`UpdateGhostRigPosition`)**
  After the mirror rig was ripped in Phase 6.9, boxes stopped getting physically pushed server-side because the auth `NetworkPlayer` had no inputs driving it. Restored the position-sync behavior on the auth rig itself: all body rigidbodies set kinematic, NSO components disabled to prevent index collisions, `HandlePlayerUpdate` calls `Rigidbody.MovePosition` to sweep through boxes. Same effect as the mirror rig but on a real authoritative `NetworkPlayer` so destructible / pickup gates accept it.

- **Phase 6.14.1 — Moving platforms snapshot**
  `CollectActiveNsoSnapshot` no longer skips kinematic NSOs. Instead it includes any NSO whose `transform.position` drifted > 1cm since last snapshot, or that had motion within the last 1s (keepalive). Catches Landfall's animator-driven moving platforms.

- **Phase 6.14.5 v0.1 + v0.2 — Tick history rewind buffer + damage range validation**
  Server records `{tick → per-slot position + alive}` ring buffer (60 entries ≈ 2s @ 30Hz). Damage validation now looks up positions at T-2 ticks (~66ms ago, lag-comp approximation) for the attacker↔victim distance check. Falls back to current positions when history unavailable.

- **Phase 6.16 v0.1 / v0.2 — Damage validation**
  Reject damage > 1000 / negative / NaN / Inf / attacker idx > 3 (except 255 = environment). Reject when attacker-victim distance > 50u (using rewind buffer when available).

- **Phase 6.17 v0.1 — Server-side projectile registry + simulation**
  New msgType 41 `PktClientFireWeapon` — client emits via Harmony postfix on `Weapon.ActuallyShoot` when local player fires. Server registers a virtual projectile (origin, dir, speed, lifetime), advances per frame, expires after 3s. Snapshot wire format bumped to v26.3 with new projectile section. v0.1 is observability/wire-protocol foundation — hit registration (v0.2) is next.

- **Phase 6.12.2 v0.1 + v0.2 — Divergence detection + hard snap**
  Client maintains a `seq → predicted position` ring buffer (240 entries ≈ 4s @ 60Hz input). On snapshot apply for own slot, looks up predicted position at server's `lastInputSeq` and compares. Drift > 1.0u logs warning; drift > 2.5u hard-snaps `rb.position` to server value + zeros velocity. Foundation for full replay rollback (Phase 6.12.2 v1.0).

- **Phase 6.16 v0.1 — Damage validation**
  Reject damage > 1000 / negative / NaN / Inf / attacker idx > 3 (except 255 = environment). First defensive floor; full rewind-buffer authority gated on Phase 6.14.5.

- **Phase 6.12.2 prep — `lastInputSeq` in snapshot**
  Wire format v26.2 bump: each player entry now 17 bytes (was 13). Server includes its last-acked `PktPlayerInput.sequenceNum` per slot so client can drive input replay rollback when it lands.

- **`v26.2` and forward-only protocol**: snapshot entry size changed; both sides need matched builds.

- **Anticheat enforce flag**: `SF_ANTICHEAT_ENFORCE=1` env var promotes the observer to drop packets when rate thresholds cross. Default off so legit traffic bursts aren't stomped before tuning.

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
