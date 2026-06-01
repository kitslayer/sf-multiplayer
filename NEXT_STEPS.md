# Next steps

Where the project is and what's coming next. Refreshed continuously — see [`WHATS_NEW.md`](WHATS_NEW.md) for the running session log, [`notes/PROTOCOL.md`](notes/PROTOCOL.md) for the wire-format spec.

> **Update (2026-06):** single-port multi-lobby (router + control plane + in-game browser) and the box-physics fixes are merged to `main` and deployed live — see [`WHATS_NEW.md`](WHATS_NEW.md) and [`notes/MULTI_LOBBY_LIVE.md`](notes/MULTI_LOBBY_LIVE.md) for current state + capacity. Sections below dated 2026-05-23 are partially superseded.

## Current state

Live server end-to-end. Steam Stick Fight (Windows or Linux/Proton) connects to a headless SF instance running [`sf-headless-host/SFHeadlessHost.dll`](sf-headless-host/) — a BepInEx + Harmony plugin that turns SF into its own v25-speaking dedicated server.

**Working:**
- Handshake, spawn, auto-load to a Landfall map
- Weapon pickup / throw / drop forwarded through SF's host-side handlers
- Death + kill propagation + round advance, full 123-map rotation
- Box pushing — via the Phase 6.7 *mirror rig* (a kinematic player rig per client that the oracle teleports to the client's reported position so it can collide with `NetworkSyncableObject`s)

**Known broken / partial / untested-live (as of end-of-session 2026-05-23, commit `39f4c56`):**

See [`notes/SESSION_2026-05-23.md`](notes/SESSION_2026-05-23.md) for the full session handoff. Open items to verify on next-session resume:

- **OPEN-1..OPEN-6** — user-reported bugs from live testing. Some likely fixed by the reverts in `4affabc` (void/lava damage, chains/ice/boxes random break) but UNTESTED on current build. See [`notes/BUGS_BACKLOG.md`](notes/BUGS_BACKLOG.md) "Open — needs verification" section for the test plan.
- Hard-snap on big divergence — Phase 6.12.2 v1.0 shipped (shift correction) but the >2.5u WARN-log path needs live exercise to confirm threshold is right
- Per-map weapon allow-lists from actual map data — `/weapons` chat command works for manual control, but no automatic per-map presets
- Workshop maps not supported at runtime (just the 123 pre-dumped Landfall scenes)
- Phase 6.17 v0.4 polish ideas — projectile occlusion against NSOs (currently only static scene), per-weapon damage values

**Shipped this session (active, not reverted):**
- ✅ P0-12 — `MapInfoSync` Vector2 key quantization (silent platform desync from ULP drift)
- ✅ P0-13 — full-keyframe snapshot to new v26 endpoints (late-joiners get at-rest box positions)
- ✅ P0-14 — v26.5 `WorldStateSnapshot` carries authoritative `MapInfoSyncableBase` positions
- ✅ P0-15 — `DestructiblePiece` guard suppresses post-large-lerp swept-collision destructions
- ✅ Phase 6.12.2 v1.0 — Shift-correction reconciliation (CSGO model: server corrections applied as offset to current local pos)
- ✅ Phase 6.17 v0.2 — Server-side projectile hit registration (swept sphere check)
- ✅ Phase 6.17 v0.3 — Particle direction bytes + wall occlusion via Linecast
- ✅ Phase 6.20 — Chat command admin suite (`/weapons`, `/kick`, `/anticheat`, `/map`, `/listmaps`)
- ✅ serve-lobbies.py polish — card layout, copy-to-clipboard, `/healthz`
- ✅ Deploy fix — `setup-all.sh` now deploys `SFHeadlessHost.dll` to BOTH oracle and Steam installs

**Reverted this session (tried, broke things, rolled back in `4affabc`):**
- ❌ P0-11 Y-aware destruction filter — forwarded chain stress-breaks. Back to drop-all.
- ❌ P1-8 attacker-slot anti-spoof — blocked all player-on-player damage. Removed.
- ❌ Dynamic-NSO client patch — caused spurious NSO-on-NSO destructions. Back to stock kinematic.

## End-state goal — CONFIRMED 2026-05-23

**Client-side prediction + server-side authoritative simulation + client reconciliation.** The canonical CS/Valorant/Overwatch netcode model. User confirmed: "we should have client prediction as the end goal."

```
PER-FRAME (client side)
  1. Read input (keyboard / pad)
  2. Run local Movement.cs on the input → predicted player position
  3. Run local NSO physics on local pushes → predicted box motion
  4. Send PktPlayerInput { stickXY, aimXY, buttons, sequenceNum } to server
  5. Render at predicted positions

EVERY 33ms (server → client)
  6. Receive PktWorldStateSnapshot { tick, players, NSOs, projectiles, lastInputSeq }
  7. Compare local-predicted position at sequenceNum=lastInputSeq vs server.position
  8. If divergence > threshold: snap-correct + REPLAY buffered inputs from
     (lastInputSeq+1) through current sequence — i.e. re-run Movement.cs
     starting from the server-authoritative state. End result: local
     view converges to "server's view + my latest local inputs applied"
     instead of "stale-by-RTT server view alone."

PER-FRAME (server side)
  9. Process inbound inputs into SlotInputs[slot]
  10. SF's own Movement.cs (server-side, via InjectInputPrefix) advances
      the authoritative rig from those inputs
  11. SF's own physics advances NSO/projectile state on the oracle's scene
  12. NSO.TickSyncPos (5Hz) broadcasts deltas; our v26 snapshot (30Hz)
      broadcasts everything
  13. Validates incoming damage events against tick-history rewind buffer
```

Where we are vs. that target:
- ✅ v26 wire protocol shipped (snapshot, input, fire-weapon)
- ✅ Client snapshot smoothing + divergence-snap (Phase 6.11.2 + 6.12.2 v0.2)
- ✅ Tick-history ring buffer on server (Phase 6.14.5 v0.1)
- ✅ Server-authoritative NSOs + dynamic-client + kinematic-platforms (Phase 6.19)
- ✅ **Shift-correction reconciliation (Phase 6.12.2 v1.0)** — server corrections applied as offset to current local position; no more "snap back to stale server pos" feel. The canonical CSGO/Valorant model.
- ✅ **Server-side projectile hit registration (Phase 6.17 v0.2)** — swept sphere check, authoritative damage emit. Polish pending: particle direction bytes, wall occlusion (v0.3).
- ⏳ **Local NSO push prediction (Phase 6.18)** — boxes feel responsive now thanks to dynamic client NSOs (commit `6875908`), but still no "predict local push, server confirms" loop. Optional polish.
- ⏳ **Full Movement.cs state-restore replay** — current shift-correction handles the position; subtle internal-state divergence (jump state, grounded flag, etc.) would need a full state-restore loop. Only matters in pathological lag cases.

## Roadmap

### Phase 6.9 — real authoritative NetworkPlayer per client ✅
- Ripped the Phase 6.7 mirror rig (`SpawnMirrorRigsForAllClients`, `UpdateMirrorRigPosition`, `MakeRigKinematicMirror`) and the settle-phase coroutine
- Renamed → `SpawnAuthoritativePlayersForAllClients` + `ConfigureAuthoritativeRig`
- Per-instance `Controller.mHasControl = true` on each spawned rig (server-side authority)
- `HandlePlayerUpdate` is now pure relay; no longer drives anything server-side

### Phase 6.10 — server snapshots ✅
- New v26 msgType `PktWorldStateSnapshot` (39)
- Server broadcasts at 30Hz to all spawned clients on v26 port (1339)
- Wire format: `u32 serverTick, u8 playerCount, [u8 slot, f32 x, f32 y, f32 z] × N`
- Stock clients ignore msgType 39 (their `MsgType` enum stops at 38) — safe to ship before client plugin lands

### Phase 6.11 — client reconciliation ✅ (snap, no smoothing yet)
- New BepInEx plugin: [`sf-client-recon/`](sf-client-recon/) shipped to each player's `<SF install>/BepInEx/plugins/`
- Binds UDP 1339, parses incoming `PktWorldStateSnapshot`, snap-corrects local `NetworkPlayer` position to server's authoritative view
- Phase 6.11.2 (next): replace instant snap with smooth interpolation over ~100ms
- Phase 6.11.3 (later): also correct OTHER players' positions (currently they're still driven by forwarded `PlayerUpdate`)

### Phase 6.12 — input prediction + reconciliation replay (next up)
- Define v26 `PktPlayerInput` (msgType 40): `{ u32 sequenceNum, f32 stickX, f32 stickY, f32 aimX, f32 aimY, u32 buttons }`
- Client `SFClientRecon` plugin sends every fixed-update tick (or on input change) to the oracle on its v26 port
- Oracle parses inbound `PktPlayerInput`, populates the existing `SlotInputs` buffer — at which point the spawned authoritative rig's Movement.cs starts producing real authoritative positions instead of staying at spawn
- Oracle tags each outgoing snapshot with `lastInputSeq` (last sequence number consumed for that slot)
- Client maintains a sequence-tagged input ring buffer; on snapshot arrival, if local predicted position at sequence N differs from server position at sequence N beyond a tolerance, the client replays buffered inputs from N to current — the canonical CSGO rollback model

### Phase 6.13 — World shards (multi-lobby) ✅ v1 + ✅ v1.5
- v1 (shipped 2026-05-23): multi-process — each lobby is its own Proton+SF oracle on its own UDP port, fully isolated. Driven by [`launch-lobby.sh`](launch-lobby.sh), [`stop-lobby.sh`](stop-lobby.sh), [`list-lobbies.sh`](list-lobbies.sh), [`stop-all-lobbies.sh`](stop-all-lobbies.sh).
- v1.5 (shipped 2026-05-23): lobby browser endpoint — [`serve-lobbies.py`](serve-lobbies.py) exposes the registry as JSON at `GET /lobbies`. Any server browser (in-game mod, web UI) can poll. Filters out stale entries.
- v2 (design only): in-process — N additive scenes at Z-offset in one SF process, per-shard state isolation, Harmony dispatch on SF singletons. See [`notes/phase6/12-PHASE6.13-sharding.md`](notes/phase6/12-PHASE6.13-sharding.md) for the design + roadmap.

### Phase 6.14 — Server-authoritative NSO positions ✅ (v0.1)
- Server includes NSO positions in `WorldStateSnapshot` (msgType 39)
- Client snaps local NSO transforms to server values
- Next: smoothing/interpolation between snapshots (6.14.1)

### Phase 6.14.5 — Server rewind buffer (lag-comp) — design only
- See [`notes/phase6/13-rewind-buffer.md`](notes/phase6/13-rewind-buffer.md)
- CSGO-style 100ms tick history; server rewinds positions to validate damage events
- Gated behind anticheat-lite (slot↔SteamID validation, weapon plausibility) shipping first

### Phase 6.15 — Chat-command admin interface ✅ v1
- v1 (shipped 2026-05-23): `/code`, `/room`, `/ping`, `/start`, `/help`. Server parses `/`-prefixed `PktPlayerTalked` (body = raw UTF-8 confirmed from decompile), responds via `SendChatToPlayer` on the requester's owner channel `(slot*2)+3`. Lobby code plumbed via `SF_LOBBY_CODE` env var set by `launch-lobby.sh`.
- v1 docs: [`notes/phase6/14-chat-commands.md`](notes/phase6/14-chat-commands.md)
- Next (v2): `/options`, `/join CODE`, `/newlobby`, `/public`, `/private`, `/invite USER` — these need multi-lobby coordination, gated on Phase 6.13 v2 in-process sharding.

### Phase 6.16+ — broader authority
- Server-authoritative damage / hit registration (server validates damage events vs position+weapon)
- Server-authoritative destructibles full-physics (server simulates shards too, not just NSO parents)
- Server-authoritative weapon spawns with map-preset weapons + per-map allow-lists (Phase 6.8 task #34)
- Anticheat — promote observer to actively rate-limit + slot ↔ SteamID validation
- Workshop maps loaded at runtime (not just pre-dumped Landfall maps)
- Client-side smooth interpolation on snapshot apply (Phase 6.11.2)
- Input prediction replay rollback (Phase 6.12.2)

## v0.2.8 mega-fix (2026-05-24) — cajas, Halloween, conexión

**Cliente (`SFClientRecon` 0.2.8):** `instalar-cliente-oracle.ps1`, auto-connect, relay `ObjectUpdate` cajas empujables, `OnMatchStart` log, solo `SFClientRecon` en plugins (sin `SFHeadlessHost` en PC).

**Oracle (`SFHeadlessHost` 0.2.8):** guard `IsPacketAvailable` (fin ~40k NullRef), skip `ReadyUp` sin clients, `StartCountDown()` real en servidor, `StartMatch` a clientes **5s después** de `MapChange`, keepalive cajas 25s en snapshot.

**Deploy:** `deploy-physics-fix.ps1 -InstallLocal -DeployVps` → `sudo systemctl restart sf-oracle.service`

**Logs esperados:** `[SF] Deferred StartMatch`, `[P6.5] Invoked GameManager.StartCountDown`, `nsos>0` al empujar, `[BOXES] Applied client ObjectUpdate`, cliente `[oracle-lobby] OnMatchStart`.

## v0.2.5 round-2+ fix (2026-05-24d)

- **Map load stuck** — `_oracleMapLoadInProgress` never cleared when Unity did not re-fire scene load → deaths queued forever. Now: queue advance + force-complete at 8s + `FinishOracleMapLoad` always in `finally`
- **Armas** — `RearmOracleCombatLoop` after `PostMapLoad`; periodic rearm if `inFight` drops
- **Cajas** — NSO snapshot only **pushable crates** (not all 90 NSOs/tick); fall guard max 2 resets/tick
- **Init order** — `InitMapDataObjects` before `EnsureMapSyncObjectsRegistered`

## v0.2.4 Factory/Desert fix (2026-05-24c)

- **Mono reflection** — `GetMultiplayerManagerInstance()` uses `(object)` null checks (fixes `mapSync=0` / `mapState=0` on VPS)
- **map dict** — `ClearMapDataObjects` before re-register each round; skip stale `PostMapLoad` if `buildIndex != _currentSceneIndex`
- **Sky weapons ronda 2+** — `RearmOracleCombatLoop` on `AdvanceRound` + delayed after `BroadcastStartMatch` (`inFight`, `randomWeaponCounter=2`)
- **Cajas** — dynamic NSO every snapshot tick; fall guard skips chains + real falls; client NSO smooth 40Hz + snap >0.5m
- **QA mapas** — `/map 6` … `/map 12` (Factory), Desert con cajas; log `mapSync>0 mapState>0`, `[P6.5 SRW]`, sin `op_Inequality`

## v26.6 terrain + weapons pass (2026-05-24b)

- **mapState** in `WorldStateSnapshot`: `GetData()`/`SetData()` for GhostPlatform on/off, pillars, move-path
- Oracle registers `MapInfoSyncableBase` in dict + `m_NetworkControl=true`
- `CheckForGroundWeapons` on match start, scene load, late-join resend (msg 31 cache)
- Client: crate NSO dynamic locally, quantized map keys, 90Hz input burst on Q/fire
- Deploy: `deploy-physics-fix.ps1 -InstallLocal -DeployVps` then `jugar-oracle.ps1`
- Verify log: `mapSync>0 mapState>0` (was `mapSync=0` always)

## Physics / sync pass (2026-05-24)

Shipped in `SFHeadlessHost` + `SFClientRecon` (deploy via `deploy-physics-fix.ps1`):

- **P0-16** — NSO fallthrough guard + spawn cache + 5s keyframe snapshots + client `ObjectUpdate` while pushing crates
- **P0-11b** — Y-aware server destruction forward (not drop-all)
- **P0-17** — explosive blast on oracle; faster local ice break; weapons skip NSO lerp
- Client: `SFCLIENTRECON_NSO_SMOOTH` (default 22), launch via desktop `Jugar Stick Fight Oracle.bat`

Verify on VPS log: `mapSync=N`, `nsos>0` after Desert3 load, `[BOXES] Reset fallthrough` rare/zero.

## Client patched Assembly-CSharp (imported reference)

Prebuilt client artifacts — the patched `Assembly-CSharp.srv.v25.dll`, `SFClientRecon.dll`, and `SFServerBrowser.dll` — ship in [`dist/`](dist) and are bundled by the [`1-click-install/`](1-click-install) installer. (Earlier notes referenced a `client-mod/` directory that was never committed; these prebuilt DLLs supersede it.)

## Where to start reading

- [`sf-headless-host/SFHeadlessHost.cs`](sf-headless-host/SFHeadlessHost.cs) — the live plugin (~3300 lines). Key entry points: `Awake()` (Harmony patches), `HandleClientRequestingToSpawn`, `HandlePlayerUpdate`, `SpawnMirrorRigsForAllClients`, `TrySpawnPlayer`
- [`notes/phase6/10-PHASE6.5-host-side-gameplay.md`](notes/phase6/10-PHASE6.5-host-side-gameplay.md) — current host-side patch set + rationale
- [`notes/phase6/11-PHASE6.6-pickup-and-physics.md`](notes/phase6/11-PHASE6.6-pickup-and-physics.md) — pickup forwarding + diagnosis of why boxes initially didn't move
- [`refs/decompiled/Assembly-CSharp/MultiplayerManager.cs`](refs/) — host-side dispatcher
- [`refs/decompiled/Assembly-CSharp/NetworkSyncableObject.cs`](refs/) — the sync pattern (good reference for snapshot design)

## How to test locally

Server side:

```bash
SFHEADLESS_BRIDGEPORT=1341 SFHEADLESS_PORT=1337 SFHEADLESS_DEBUG=1 \
  bash launch-sf-headless.sh
```

Client side — Steam Stick Fight launch options:

```
WINEDLLOVERRIDES="winhttp=n,b" %command% -address 127.0.0.1 -port 1337
```

Logs:
- `$SF_MIRROR/BepInEx/LogOutput.log` — plugin output
- `/tmp/sf-oracle-unity-1341.log` — Unity log (use this for SF's own diagnostics)

The `-address` / `-port` flags are read by a patched `Assembly-CSharp.dll` shipped with the client (not in this repo for copyright reasons). The patched DLL has no embedded IP; everything comes from the CLI flags.
