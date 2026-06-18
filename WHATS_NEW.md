# What's new — 2026-06-17 audit-leftover fixes (shipped)

Correctness + robustness pass closing the verified-real items from the June audit (issues #2/#5); the security P0s were already closed in the June-11 hardening. Versions: host `0.4.2`, installer/client `0.6.2` (box-fix `0.3.1` unchanged).

- **Half-joined clients no longer NRE on an early map change.** `BroadcastSfPacket` now skips clients that haven't completed `ClientInit`, so a `MapChange`/`StartMatch` firing mid-handshake can't reach a client that has no slot/roster yet. (#2d)
- **No mid-match lobby switching.** The in-game server browser's join path (`RequestJoinLobby`) now refuses to re-init the netstack while a match is in progress (`GameManager.inFight`) — leave to the menu first, which prevents a live-game desync. (#5c)
- **Lobby reaper grace for fresh lobbies.** The reaper's dead-pid branch now honors `LOBBY_MIN_AGE`, so a lobby still coming up under Proton/Wine isn't killed before anyone can join. (#5d)
- **`stop-lobby.sh` hardening.** The pid/bridge values read from the registry are validated numeric before the kill patterns use them. (#5e)

> Audit items already fixed earlier (projectile-speed clamp, 5th-client rejection, OnGUI guard, atomic create-cap) are not repeated here. Deferred (need a live 2-player session or external tooling): real weaponType/aim wiring, OPEN-1..6 re-verify, the patched-DLL IP scrub.

---

# What's new — 2026-06-14 keeper fixes + 60 Hz snapshots (shipped + deployed)

Server-side quality pass, merged to `main` and **deployed live to `.115`** (host 0.4.1, box-fix 0.3.1; installer/client 0.6.1):

- **Server-side destruction.** The oracle now breaks ice/boxes in *its own* world when a client does (previously it only relayed the event), so the server's collision world matches what players see — no more phantom ice/boxes server-side. Foundation for trustworthy server-side hit-reg + anti-cheat.
- **Explosion→crate parity.** Explosive rounds detonate server-side on fuse expiry and player impact (not just wall hits), with stock-shaped blast forces — crates react consistently on the authoritative sim.
- **60 Hz snapshots** (`SnapshotHz` 30→60, env-tunable via `SFHEADLESS_SNAPSHOT_HZ`) — boxes and remote players update every physics tick. The whole pipeline (physics, input, world broadcast) now ticks at 60.
- **Anti-cheat false-kick fixed.** The "impossible kill" heuristic auto-kicked legit melee/throw/quick-draw kills (they reach the server with little recorded damage). Now log-only by default; opt-in `SF_AC_KICK=1`.
- **Box polish:** bullet crate-kick classifies the hit body's nearest crate (not the map root), offline-gated local-slot discovery (fixes a pre-connect slot-0 window), settled-crate glide so a crate parked next to a player can't sit at a permanent offset, and a SFBoxFix dead-code strip.

> Server-authoritative *player movement* was prototyped and shelved: SF's non-deterministic physics + per-client destructible worlds make it rubber-band, and the right model is local prediction + server-side hit-reg/anti-cheat. Kept out of this release; notes in the repo.

---

# What's new — 2026-06-11 server-authoritative boxes (shipped + deployed)

The box-divergence fix (PR #8, merged + **deployed live to `.115`**, installer refreshed to `SFClientRecon 0.6.0`). The oracle's sim is now the single authority for crates; clients run dynamic local physics for instant push feel and continuously reconcile toward the server. Verified live over many rounds and map transitions: mean error 0.06–0.10 units, snap counters flat, zero crates falling through the world.

Root causes fixed along the way (full narrative with telemetry in [`notes/bug-investigations/2026-06-11_server-authoritative-boxes.md`](notes/bug-investigations/2026-06-11_server-authoritative-boxes.md)):

- **Slot discovery** — every client claimed slot 0, so one player per match silently received *zero* sync data (their snapshot stream went to a dead port; captured on the wire). Now derived from the rig the game actually marks locally-controlled.
- **Oracle scene tracking** — rounds taking the settle-skip load path left every object cache filtered to the *previous* map; the authority broadcast last round's crate layout all round. Scene loads are now tracked directly, mirrored client-side.
- **Headless cull** — stock `IgnorePlayerWhenOffScreen` removed collision from anything below y=-11; the oracle's crates fell through the world on big maps every round (masked for weeks by client relays).
- **Vanilla-first physics** — crate mass had been overridden to 45 vs the prefab's 500 (runtime ground truth via UnityExplorer); pushes felt weightless. Neither sim overrides prefab values anymore, and the constraint mask now matches what vanilla actually ships.
- **Client NRE storm** — ~150 exceptions/second on every client (uninitialized packet-channel slots), a chronic hidden FPS tax, fixed at the source.
- **"Fake hits"** — server-side bullet hit tests against lagged ghost rigs emitted phantom damage packets; server bullet damage is shadow-mode (observe + log) until hit-reg is lag-compensated.

Also: v26.7 wire appendix (NSO rotation as the stock up-vector pair; old clients unaffected), live-debugging tooling (per-instance timestamped console tees + a file-driven `boxes`/`rigs` query console on clients and oracle), README de-hyped + install-troubleshooting section, installer fully in English (zip rebuilt).

Known residual: explosion→crate force parity (an occasional box pop right after big blasts, converges within a second) — next on the list, with the server-browser test and the cleanup pass.

---

# What's new — 2026-06-11 security + crash-containment pass

Full-repo review (`notes/REVIEW_2026-06-10.md`) plus the fixes that came out of it. Shipped + deployed live to `.115`.

- **Security (host `SFHeadlessHost` 0.3.11):** `PktPlayerInput`/`PktClientFireWeapon` (msgType 40/41) now require the sending **source address to match the slot's handshaken owner** — a stranger can no longer drive or redirect another player's rig/snapshot stream (this also closes the ~20× keyframe amplification reflector). The per-source rate-guard table is now swept by last-touch and hard-capped (was an unbounded-growth OOM vector under a spoofed flood). Destructive chat commands (`/kick`, `/anticheat`, `/tickrate <hz>`) are gated behind `/admin <pass>` (`SF_ADMIN_PASS`) or `SF_ADMIN_STEAMIDS`. Chat text is control-char-sanitized before logging. A 5th client is now rejected instead of crammed into slot 0; client-asserted projectile speed is clamped.
- **Security (client `SFClientRecon` 0.5.3):** the v26 UDP socket now **drops datagrams from any source other than the server** (was: anyone could inject snapshots/banners/SELECT-ACKs to teleport objects, spoof "banned" banners, or stall joins), and every snapshot section count is clamped to the packet's real capacity before allocating (was a multi-MB-per-packet GC-storm vector on the 32-bit game). The RX thread no longer dies permanently on a transient Windows `SocketException`.
- **Crash containment** (the deterministic ~24h native crash — see [`notes/CRASH_INVESTIGATION.md`](notes/CRASH_INVESTIGATION.md), now updated with the periodicity + float-`+Inf` clue): `RuntimeMaxSec=82800`+`Restart=always` drop-in turns the daily hard-crash into a clean restart; a new `sf-oracle-watchdog` timer UDP-Pings the oracle every 2 min and restarts it if it's `active` but deaf (the 2026-06-10 wedge); `/healthz` now probes the port for real and reports `lobbiesResponsive`/`degraded`.
- **Ops/packaging:** `deploy/start-oracle-server.sh` made executable (fresh `systemctl start` was failing `203/EXEC`); `deploy/stop-all-lobbies.bat` kills registered/`-batchmode` PIDs instead of `/IM StickFight.exe` (was killing a player's own game); installer `curl` calls now use `--fail`; the root installer zip's plugin payload refreshed to match `dist/` (was a version behind), and its scripts extracted to `1-click-install/zip-src/` so they're reviewable.

> Removed the leftover sfdev-owned `SFOracleSocketHost.dll` (a benign rx-diagnostic, no source in repo) from the live oracle, and replaced the stale ALKA-built `SFBoxFix.dll` with the repo build (crate physics now matches what clients ship).

---

# What's new — single-port multi-lobby (merged to `main`)

Single-port multi-lobby front-door so one server hosts many lobbies and players
pick/create them **in-game**, while each lobby stays an isolated `SF.exe` (no
fragile in-process "true sharding"). **Merged to `main` and deployed live**
(systemd router + control plane); per-code routing + isolation verified across
two IPs and capacity measured. Docs:
[`notes/MULTI_LOBBY_LIVE.md`](notes/MULTI_LOBBY_LIVE.md) (deployed state +
capacity + ops), [`notes/ROUTER.md`](notes/ROUTER.md),
[`notes/ROUTER_LIVE_TEST.md`](notes/ROUTER_LIVE_TEST.md),
[`notes/PROTOCOL.md`](notes/PROTOCOL.md) (router control framing).

- **`sf-router/`** (new Go module) — stateless per-client UDP relay on one public
  port (1337). Clients send a magic-framed SELECT naming their lobby code; the
  router resolves it via the launch-lobby.sh registry and forwards both the v25
  game socket and the v26 recon socket to that lobby's backend, relaying replies
  back from the public port. Re-resolves on use (survives lobby restart/port
  reuse), teardown-stale on switch, bounded bindings, `/router/stats` for the
  reaper. `go test ./... -race` green (16 tests); reviewed + hardened.
- **`serve-lobbies.py`** — control plane: `POST /lobbies` create (token + per-IP
  rate-limit + `SF_MAX_LOBBIES` cap, spawns via launch-lobby.sh), `POST
  /lobbies/stop`, and a reaper that stops dead-pid + long-empty lobbies.
- **Client** (`SFClientRecon` + `SfOracleLobbyConnect`) — emits SELECT on the
  v26 socket (resend until snapshots flow; self-heals on switch);
  `RequestJoinLobby(code)` is the in-game join entry. Default lobby `MAIN` (env
  `SF_LOBBY`) keeps a no-browser Quick Match working.
- **`SFServerBrowser`** — real in-game JOIN, a join-by-code text field, and a
  CREATE LOBBY button (POST → auto-join). Replaces the old copy-to-clipboard.
- **Known limit (documented):** two players behind one public IP in *different*
  lobbies — the second's game socket may mis-route (the game socket can't SELECT
  without a patched-DLL change; deferred). Distinct IPs / same-lobby are fine.

---

# What's new — 2026-05-23 session

**60+ commits** landed in one day. Running tally so visitors can scan the deltas without scrolling git log.

**Session handoff: see [`notes/SESSION_2026-05-23.md`](notes/SESSION_2026-05-23.md)** for the complete pickup-point doc — what shipped, what got reverted, what's still open, and the test plan to verify the current build on next-session resume.

Other key references:
- [`notes/ARCHITECTURE.md`](notes/ARCHITECTURE.md) — system overview
- [`notes/PROTOCOL.md`](notes/PROTOCOL.md) — wire-format spec (currently v26.6)
- [`notes/OBJECT_SYNC.md`](notes/OBJECT_SYNC.md) — how SF's three world-object sync mechanisms interact
- [`notes/BUGS_BACKLOG.md`](notes/BUGS_BACKLOG.md) — bug incident log with the new "Open — needs verification" section
- [`notes/AUDIT_2026-05-23.md`](notes/AUDIT_2026-05-23.md) — deep audit with 8 findings

## End-of-session state (commit `39f4c56`)

Three things were attempted then reverted after live testing exposed regressions: **P1-8** (attacker-slot identity check — broke ALL player-on-player damage), **P0-11 Y-aware destruction filter** (forwarded chain stress-breaks), and the **dynamic-NSO client patch** (let local box→ice collisions fire spurious destructions). All reverts are in commit `4affabc`. The session ended after fixing a long-standing deploy-mismatch where `setup-all.sh` wasn't copying `SFHeadlessHost.dll` to the Steam install — which had been silently causing inconsistent client-mode behavior on player1 for ~5 hours.

Net shipped (still active): P0-12 platform-key quantization, P0-13 keyframe snapshot, P0-14 v26.5 platform sync, P0-15 destruction guard, Phase 6.12.2 v1.0 shift-correction reconciliation, Phase 6.17 v0.2/v0.3 server-side hit reg, full chat-command admin suite (Phase 6.20), lobby browser polish, comprehensive docs.

## Phase 6.19 — five P0s + one P1 + projectile hit reg shipped (2026-05-23 night)

Implemented and pushed in three commits (`c6e9797`, `7b5a037`, `8fa0f20`):

- **P0-11** — Y-aware destruction filter (replaces the coarse drop-all). Server now forwards legitimate destructions while still dropping killbox-fall events. `NsoIsKillboxFallen(idx)` helper looks up the NSO's current Y by index.
- **P0-12** — Vector2 key quantization for `MapInfoSync` dictionary. Both server and client install Harmony prefixes on `AddMapDataObject` + `OnMapDataRecieved` that round to 0.01 precision. Fixes silent platform-lookup failures from float ULP drift.
- **P0-13** — Full-keyframe snapshot to each new v26 endpoint on first PlayerInput. `CollectAllNsoSnapshot` mirrors the active variant but skips position-delta filtering (Y > -30 still applies). Late-joining clients see at-rest NSO positions immediately.
- **P0-14** — v26.5 wire format adds a `MapInfoSyncable` section to `WorldStateSnapshot`. Entries identified by `m_StartPos` Vector2 (stable cross-process; quantized by P0-12). Client kinematic-flips the platform/pillar on first sight so local AddForce/spring integrator stops fighting the snapshot.
- **P0-15** — Harmony prefix on `DestructiblePiece.OnCollisionEnter` skips when colliding body was lerped >0.3u in the last 150ms. Initial implementation marked every NSO every frame (over-suppression); tightened to only mark large-jump snapshot deltas.
- **P1-8** — `ValidateDamagePacket` rejects attacker-slot spoofing (`attackerIdx != sender.Slot`).
- **Phase 6.17 v0.2** — server-side projectile hit registration. Swept sphere-sphere check per projectile per tick (radius 1.2u). On hit, server emits authoritative `PktPlayerTookDamage` (25 damage, dmgType 0) on victim's mEventChannel. Projectile removed from registry. No occlusion / no particles yet (v0.3).

Plus a TOC added to the top of both `SFHeadlessHost.cs` and `SFClientRecon.cs` so newcomers can navigate by feature.

## End-of-session audit (2026-05-23 evening)

Three open issues identified during a research-only pass — see [`notes/AUDIT_2026-05-23.md`](notes/AUDIT_2026-05-23.md) for full evidence:

- **P0-11** (destruction race) — the hybrid dynamic-NSO client patch in commit `6875908` interacts badly with the server-originated destruction filter; legitimate server-side breaks get dropped, producing "ghost boxes" on clients.
- **P0-12** (`GhostPlatform` Vector2-key precision) — stock SF's `MapInfoSyncableBase` sync uses bit-exact float compare on world-space positions to look up platform objects. Float32 ULP mismatches between server and clients silently drop platform state updates.
- **P0-13** (first-snapshot gap) — our v26 snapshot only includes NSOs whose position recently changed; a late-joining client sees stale positions for at-rest boxes until something pushes them again.

Plus confirmed-correct: channel routing is right (false alarm flags now cross-verified against `P2PPackageHandler.GetChannelForMsgType` ground truth) and chains genuinely work fine.

## Round-end delay trimmed (commit `f084df6`)

User reported kills "take longer than usual to stop the match" — traced to two artificial waits in the death chain: 2.5s before MapChange + 3.0s before StartMatch = 5.5s total. Stock SF fires `ChangeMap` instantly from `GameManager.KillPlayer` when `playersAlive ≤ 1`. Trimmed to 0.5s + 2.0s (env-tunable via `SF_ROUND_END_DELAY` and `SF_NEXT_MATCH_DELAY`). New default: 2.5s total — less than half what it was.

## `/tickrate` chat command + 60Hz default (commit `6875908`)

Server's physics rate is now configurable live via `/tickrate N` chat command (range 20–240). Default raised from Unity's stock 50Hz to 60Hz on both server and client. SF's `Movement.cs` scales forces by `Time.deltaTime` (which equals `fixedDeltaTime` inside `FixedUpdate`), so per-second impulse is preserved across tickrate changes — safe to change live. Client FPS is fully independent of server tickrate (physics is fixed-step, snapshot broadcast is wall-clock-timed at 30Hz, input processing is packet-driven).

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

---

## Archived session changelogs (pre-2026-06)

> Moved here verbatim from `NEXT_STEPS.md` (which is now Current state + End-state goal + Roadmap + How to test only). These are dated per-session deploy notes — kept for history; the deploy scripts and version tags they reference (`deploy-physics-fix.ps1`, `jugar-oracle.ps1`, `SFClientRecon 0.2.x`) are historical.

### v0.2.8 mega-fix (2026-05-24) — cajas, Halloween, conexión

**Cliente (`SFClientRecon` 0.2.8):** `instalar-cliente-oracle.ps1`, auto-connect, relay `ObjectUpdate` cajas empujables, `OnMatchStart` log, solo `SFClientRecon` en plugins (sin `SFHeadlessHost` en PC).

**Oracle (`SFHeadlessHost` 0.2.8):** guard `IsPacketAvailable` (fin ~40k NullRef), skip `ReadyUp` sin clients, `StartCountDown()` real en servidor, `StartMatch` a clientes **5s después** de `MapChange`, keepalive cajas 25s en snapshot.

**Deploy:** `deploy-physics-fix.ps1 -InstallLocal -DeployVps` → `sudo systemctl restart sf-oracle.service`

**Logs esperados:** `[SF] Deferred StartMatch`, `[P6.5] Invoked GameManager.StartCountDown`, `nsos>0` al empujar, `[BOXES] Applied client ObjectUpdate`, cliente `[oracle-lobby] OnMatchStart`.

### v0.2.5 round-2+ fix (2026-05-24d)

- **Map load stuck** — `_oracleMapLoadInProgress` never cleared when Unity did not re-fire scene load → deaths queued forever. Now: queue advance + force-complete at 8s + `FinishOracleMapLoad` always in `finally`
- **Armas** — `RearmOracleCombatLoop` after `PostMapLoad`; periodic rearm if `inFight` drops
- **Cajas** — NSO snapshot only **pushable crates** (not all 90 NSOs/tick); fall guard max 2 resets/tick
- **Init order** — `InitMapDataObjects` before `EnsureMapSyncObjectsRegistered`

### v0.2.4 Factory/Desert fix (2026-05-24c)

- **Mono reflection** — `GetMultiplayerManagerInstance()` uses `(object)` null checks (fixes `mapSync=0` / `mapState=0` on VPS)
- **map dict** — `ClearMapDataObjects` before re-register each round; skip stale `PostMapLoad` if `buildIndex != _currentSceneIndex`
- **Sky weapons ronda 2+** — `RearmOracleCombatLoop` on `AdvanceRound` + delayed after `BroadcastStartMatch` (`inFight`, `randomWeaponCounter=2`)
- **Cajas** — dynamic NSO every snapshot tick; fall guard skips chains + real falls; client NSO smooth 40Hz + snap >0.5m
- **QA mapas** — `/map 6` … `/map 12` (Factory), Desert con cajas; log `mapSync>0 mapState>0`, `[P6.5 SRW]`, sin `op_Inequality`

### v26.6 terrain + weapons pass (2026-05-24b)

- **mapState** in `WorldStateSnapshot`: `GetData()`/`SetData()` for GhostPlatform on/off, pillars, move-path
- Oracle registers `MapInfoSyncableBase` in dict + `m_NetworkControl=true`
- `CheckForGroundWeapons` on match start, scene load, late-join resend (msg 31 cache)
- Client: crate NSO dynamic locally, quantized map keys, 90Hz input burst on Q/fire
- Deploy: `deploy-physics-fix.ps1 -InstallLocal -DeployVps` then `jugar-oracle.ps1`
- Verify log: `mapSync>0 mapState>0` (was `mapSync=0` always)

### Physics / sync pass (2026-05-24)

Shipped in `SFHeadlessHost` + `SFClientRecon` (deploy via `deploy-physics-fix.ps1`):

- **P0-16** — NSO fallthrough guard + spawn cache + 5s keyframe snapshots + client `ObjectUpdate` while pushing crates
- **P0-11b** — Y-aware server destruction forward (not drop-all)
- **P0-17** — explosive blast on oracle; faster local ice break; weapons skip NSO lerp
- Client: `SFCLIENTRECON_NSO_SMOOTH` (default 22), launch via desktop `Jugar Stick Fight Oracle.bat`

Verify on VPS log: `mapSync=N`, `nsos>0` after Desert3 load, `[BOXES] Reset fallthrough` rare/zero.
