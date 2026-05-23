# Architecture — sf-multiplayer (Path A, server-authoritative)

The whole system in one place. As of 2026-05-23 evening.

## One-paragraph summary

A real Stick Fight Unity binary runs headlessly on the server, with a BepInEx plugin (`SFHeadlessHost.dll`) that turns it into a v25 UDP dedicated server. Real Stick Fight clients (Windows native or Linux/Proton) speak SF's stock v25 protocol to the oracle directly. A small companion plugin (`SFClientRecon.dll`) on each player's machine handles v26 extensions (snapshots, prediction inputs, divergence detection). The oracle is the sole authority for player positions, NSO physics (boxes/chains/ice), and weapon spawning — clients render what the oracle tells them.

## Component overview

```
┌────────────────────────────────────┐         ┌────────────────────────────────────┐
│  Player machine (Windows native    │         │  Server machine (Linux + Proton    │
│  or Linux + Proton + Goldberg)     │         │  + Goldberg + BepInEx)             │
│                                    │         │                                    │
│  StickFight.exe (graphical)        │  v25    │  StickFight.exe -batchmode         │
│    + patched Assembly-CSharp.dll   │  UDP    │    + SFHeadlessHost.dll            │
│    + BepInEx                       │  ────►  │    + Harmony patches:              │
│    + SFClientRecon.dll  ───────────│         │      • IsServer=true (postfix)     │
│    + SFHeadlessHost.dll            │  v26    │      • IsNetworkMatch=true         │
│      (client shim, currently       │  ────►  │      • SpawnRandomWeapon impl      │
│       a no-op for NSOs)            │         │      • SendMessageToAllClients     │
│                                    │  ◄────  │        (prefix → forward to v25)   │
│                                    │  v26    │      • Controller.Update input    │
│                                    │  snap   │        injection prefix            │
│                                    │  30Hz   │                                    │
└────────────────────────────────────┘         └────────────────────────────────────┘
```

### Server side: `sf-headless-host/SFHeadlessHost.dll`

Runs inside a headless Stick Fight Unity instance launched with `-batchmode -nographics`. Bootstrap order:

1. `BepInEx` (preloader → chainloader → plugin load)
2. **Awake**: install ~10 Harmony patches, parse env vars
3. **Phase 6.5 patches** force the running SF instance to *think* it's a multiplayer host:
   - `MultiplayerManager.IsServer` postfix → always `true`
   - `MatchmakingHandler.IsNetworkMatch` postfix → always `true`
   - `SetNetworkMatch(false)` prefix → forces arg back to `true`
   - `WeaponSelectionHandler.GetRandomWeaponIndex` prefix → returns valid index
   - `GameManager.SpawnRandomWeapon` prefix → replaces impl
   - `MultiplayerManager.SendMessageToAllClients` prefix → captures every host-side broadcast and re-emits it on our v25 UDP socket
   - `Controller.Update` prefix → injects buffered inputs into PlayerActions (Phase 6.12 prediction)
   - Various `MultiplayerManager.Init*` postfix loggers
4. **Force scene-load** to MainScene, then call into bootstrap so SF wires up its host-side state without a menu
5. **Bind UDP server** on `SFHEADLESS_PORT` (default 1337). All inbound v25 + v26 traffic lands here.
6. **Heartbeat tick** logs `clients=… spawned=… rx=…/s snap=…/s input=…/s rigs=… matchStarted=…` every 30s.

Once a real client connects, the flow looks like:

```
client patched DLL                       oracle (Headless SF + plugin)
─────────────────────────                ──────────────────────────────
ClientRequestingAccepting ─────────►
                          ◄───────────── ClientAccepted
ClientRequestingIndex ─────────────►
                                          • Allocate slot 0..3
                                          • Record SteamID from body
                                          • Build ClientInit body with slot-0..3 layout
                          ◄───────────── ClientInit (50-byte body)
                                          • OnInitFromServer parses, spawns dummy
                                            NetworkPlayers for other slots, gets
                                            mLocalPlayerIndex
ClientRequestingToSpawn ───────────►
                                          • For each existing client: send PktClientJoined
                                            BEFORE PktClientSpawned (order matters —
                                            OnPlayerSpawned reads mConnectedClients[slot])
                          ◄───────────── PktClientJoined (slot, steamID) to each peer
                          ◄───────────── PktClientSpawned (slot, pos, euler, flag) to all
                                          • Send welcome chat via PlayerTalked
                          ◄───────────── PktPlayerTalked ("Welcome to lobby ...")

(some time passes, players walk around in lobby)

PlayerUpdate (channel slot*2+2) ───►
                                          • HandlePlayerUpdate parses Y,Z
                                          • Forward to all OTHER Initialized clients
                                            ON THE SAME CHANNEL (slot*2+2)
                                          • Also teleport server's ghost rig for this
                                            slot to (0, Y, Z) so it physically sweeps
                                            through boxes on the server's scene
```

When someone types `/start` in chat:

```
PktPlayerTalked "/start" ───────────►
                                          • TryProcessChatCommand parses
                                          • Sends "Starting match..." back via PlayerTalked
                                          • FireMatchStart():
                                            – BroadcastMapChange(_currentSceneIndex)
                                            – BroadcastStartMatch
                                            – MatchmakingHandler.SetNetworkMatch(true)
                                            – Schedule oracle GameManager.StartMatch in 0.5s
                          ◄───────────── PktMapChange (winner=255, scene=N)
                          ◄───────────── PktStartMatch (empty body)

(oracle additively loads the Landfall scene)

                                          • NSO inventory + InvokeMultiplayerManagerInitChain
                                          • Auth rigs spawn 1s later (kinematic, HasControl=true)
                                          • Match in progress
```

### Client side: `sf-client-recon/SFClientRecon.dll`

Small companion plugin. Bound at `SFCLIENTRECON_PORT` (default 1339 on Steam SF, 1340 on the 2nd local instance) so the oracle can route v26 snapshots to a separate channel from the patched DLL's v25 socket.

Functions:

1. **Receive** `WorldStateSnapshot` (msgType 39) at 30Hz:
   - Parse player positions (slot, x, y, z, lastInputSeq)
   - Parse NSO positions (id, x, y, z, rotZ)
   - Parse projectile positions (id, slot, weaponType, x, y, z)
2. **Smooth toward targets** (Phase 6.11.2): each frame, exponentially lerp local NetworkPlayer/NSO transforms toward last-received server target. Rate ~15/s. Settles 95% of error in 200ms.
3. **Send** `PktPlayerInput` (msgType 40) at 60Hz from local Controller's `mPlayerActions`:
   - stickX, stickY, aimX, aimY, buttons + monotonic sequence number
4. **Hook** `Weapon.ActuallyShoot` postfix → emits `PktClientFireWeapon` (msgType 41) when local player fires
5. **Divergence detection** (Phase 6.12.2): keep a 240-entry ring buffer of `seq → predicted position`. On snapshot apply for own slot, compare. Drift > 1.0u logs; drift > 2.5u hard-snaps to server position.
6. **Uncap FPS** (`vSyncCount=0`, `targetFrameRate=-1`) so prediction runs at the user's max framerate.

### Wire protocol

Every packet:

```
[u32 ts][u8 msgType][N body][u64 steamID][u8 channel]
```

5 + N + 9 = N+14 bytes. Detailed byte layouts in [`PROTOCOL.md`](PROTOCOL.md).

**Channel routing summary** (this caught us several times):

| Channel | Purpose |
|---------|---------|
| 0 | Default — most lobby/setup msgTypes (handshake, MapChange, StartMatch, OptionsChanged); stock SF polls this channel for `CheckMessageType` dispatch |
| 1 | Same as 0 — stock SF polls both 0 and 1 for `CheckMessageType` (`P2PPackageHandler.Update` lines 110-117) |
| `slot*2 + 2` | Per-slot **PlayerUpdate** (msgType 10). `NetworkPlayer.InitNetworkSpawnID` sets `mUpdateChannel = slot*2+2`. The receiving client's NetworkPlayer listens on this specific channel; forwarding on channel 0 means the position update is never applied. |
| `slot*2 + 3` | Per-slot **event channel**. `mEventChannel = mUpdateChannel + 1`. Used by **PlayerTalked** (12), **WeaponThrown** (20), **RequestingWeaponThrow** (21). |
| 10 | NSO updates — **ObjectUpdate** (26), `SyncableObjectManager.ListenForPackages` |
| 11 | NSO destruction — **ObjectDestructionCollision** (30) |

**v26 extensions** (this repo, msgType 39+):

| ID | Direction | Body shape | Purpose |
|----|-----------|------------|---------|
| 39 | server → all clients, 30Hz | `u32 tick, u8 nPlayers, [u8 slot, f32 x, f32 y, f32 z, u32 lastInputSeq]×n, u16 nNSOs, [u16 id, f32 x, f32 y, f32 z, f32 rotZ]×m, u16 nProjs, [u32 id, u8 slot, u8 wType, f32 x, f32 y, f32 z]×k` | Authoritative state snapshot |
| 40 | client → server, 60Hz | `u32 seq, u8 slot, f32 stickX, f32 stickY, f32 aimX, f32 aimY, u32 buttons` | Player input for prediction+reconciliation |
| 41 | client → server, event | `u8 slot, u8 wType, f32 oX, f32 oY, f32 oZ, f32 dX, f32 dY, f32 dZ, f32 speed` | Local Weapon.ActuallyShoot — server registers projectile |

## Authority model (current)

```
PLAYER POSITIONS
  Source of truth: each client's local Movement.cs (patched DLL)
  Wire: PktPlayerUpdate (10) on channel slot*2+2 → server forwards to peers on same channel
  Server's view: maintained by HandlePlayerUpdate teleporting the ghost rig
  Server's snapshot: optional (v26) for client-side reconciliation

PLAYER MOVEMENT INPUT (for future server-sim)
  Wire: PktPlayerInput (40) on default channel from SFClientRecon → server
  Server uses: populates SlotInputs[slot] which InjectInputPrefix writes
    into Controller.mPlayerActions just before SF's Movement.cs reads them.
  Currently this drives the oracle's auth rig but the v26 path isn't yet
  the primary movement source — clients still send PktPlayerUpdate too.

NSO POSITIONS (boxes, chains, ice, crates — NOT moving platforms; see below)
  Source of truth: ORACLE. Oracle is host (mHasControl=true via Phase 6.5
    static patch). Clients (as of commit `6875908`) have dynamic NSOs but
    mHasControl=false — local push has instant feedback, but client does
    NOT broadcast. Oracle remains sole authority on canonical position;
    v26 snapshot reconciles clients toward server state.
  Wire: oracle's NSO.TickSyncPos fires at 5Hz, calls
    SendMessageToAllClients(ObjectUpdate, channel=10). Our prefix forwards
    to v25 clients with channel preserved. v26 WorldStateSnapshot adds
    a global 30Hz snapshot covering all moving NSOs with a 1s keepalive.
  Server also reads incoming PktObjectUpdate from clients (legacy stock-SF
    pre-multiplayer-fix path) and applies to its NSOs via
    ApplyClientObjectUpdate, but with mHasControl=false on clients those
    packets shouldn't arrive anymore.
  Open issue: see P0-11 (destruction race) and P0-13 (first-snapshot gap)
    in BUGS_BACKLOG.md.

LEVEL OBJECTS (GhostPlatform, MoveAlongPathUsingForce, PillarHandler,
    PlayMoveAnimations — anything that subclasses MapInfoSyncableBase)
  These are NOT NetworkSyncableObjects. They have an entirely separate
    sync system in stock SF:
    1. Awake registers each object in a Dictionary<Vector2, MapInfoSyncableBase>
       keyed by world-space (position.y, position.z). Bit-exact float Equals.
    2. Server's Update calls TickSyncPos every 1/5s, fires SyncMapData
       which packs [f32 startPos.x][f32 startPos.y][bytes data] and broadcasts
       via MapInfoSync (msgType 33, channel 0).
    3. Client's OnMapDataRecieved reads the Vector2 + does dictionary lookup;
       only if the key matches exactly does it call SetData on the object.
  Source of truth: ORACLE (the only one with m_NetworkControl=true).
  Wire: oracle's host-side SyncMapData → SendBroadcastPrefix → forward as
    v25 msgType 33 on channel 0 → client's OnMapDataRecieved.
  Open issue: see P0-12 in BUGS_BACKLOG.md — float32 ULP mismatch in the
    Vector2 key causes silent lookup failures, leaving clients with frozen
    initial state for affected platforms.

NSO DESTRUCTION (ice break, chain link snap, crate shatter)
  Source of truth: whichever side detected the collision.
  Wire IN: client sends PktObjectSimpleDestruction (28), InvokeDestructionEvent
    (29), or PktObjectDestructionCollision (30). Server relays to ALL clients
    via RelayBodyToAll (preserves channel, includes sender for the killing-
    blow signal pattern — ALKA P0-3).
  Wire OUT: server-originated destruction events are NOT forwarded
    (SendBroadcastPrefix drops them) because the oracle's local NSOs
    sometimes destruct spuriously (e.g. boxes that drift off the killbox
    on map load with no settle phase) and broadcasting those would
    randomly destroy intact local objects on clients.

WEAPONS SPAWN
  Source of truth: oracle.
  Map-preset weapons: WeaponPickUp prefabs in level geometry register via
    InitWeaponPickUpOnAwake → MultiplayerManager.AddPreSpawnedWeapon.
    CheckForGroundWeapons (invoked from InvokeMultiplayerManagerInitChain)
    broadcasts GroundWeaponsInit (msgType 31) when match starts.
  Runtime spawns: SF's WeaponSelectionHandler.GetRandomWeaponIndex + the
    GameManager.SpawnRandomWeapon timer, both patched to flow through the
    oracle's host-side code. Broadcast as WeaponSpawned (19).
  Pickup / drop / throw: pure relay through Handle{Pickup,Drop,Throw}Request.

PROJECTILES
  Phase 6.17 v0.1: oracle has a virtual-projectile registry (origin, dir,
    speed, lifetime). Client SFClientRecon emits PktClientFireWeapon (41)
    on Weapon.ActuallyShoot; oracle advances per frame; positions included
    in WorldStateSnapshot. Not yet doing hit registration server-side.

DAMAGE
  Source of truth: damage event (client emits PktPlayerTookDamage).
  Server validates: magnitude ≤ 1000, attacker idx ≤ 3 or 255, distance
    between attacker and victim rigs at server tick T-2 ≤ 50u (lag-comp
    approximation). Reject = drop, no relay.
  Accept: RelayBodyToAll (sender too, for killing-blow signal). Detects
    damage=666.666 and schedules round-advance.

CHAT + ADMIN COMMANDS
  PktPlayerTalked (12) body = raw UTF-8. Server forwards to OTHER clients
    and parses for / prefix:
      /code, /room      → "Lobby code: <SF_LOBBY_CODE env>"
      /ping             → "pong"
      /start            → FireMatchStart()
      /restart, /next   → schedule round advance
      /players          → "Players: N connected, M spawned, K rigs"
      /lobbies          → list other lobbies from /tmp/sf-lobbies/
      /version          → plugin version string
      /help             → list commands
  Server emits chat via SendChatToPlayer on the recipient's owner channel
    (slot*2+3) with envelope steamID=0.

ANTICHEAT (observation-only by default)
  Per-client packet rate windows:
    240/s total, 120/s PlayerUpdate, 30/s damage, 480/s NSO
  Logs warnings on threshold cross (throttled to 5s per client).
  Set SF_ANTICHEAT_ENFORCE=1 to actually drop offending packets.

DAMAGE TICK HISTORY (Phase 6.14.5)
  Server records per-tick {tick, positions[4], alive[4]} into a 60-entry
  ring buffer (~2s at 30Hz snapshot rate). LookupTickSample(t) retrieves
  the historical state. Currently used only for damage range validation
  at T-2. Future: full client-tick-stamped damage validation when
  patched DLL extension lands.
```

## File layout

```
sf-multiplayer/
├── sf-headless-host/         BepInEx plugin loaded on the oracle headless SF
│   ├── SFHeadlessHost.cs     ~3500 lines: protocol, dispatch, snapshots, chat, anticheat
│   └── SFHeadlessHost.csproj
├── sf-client-recon/          BepInEx plugin loaded on each player's SF install
│   ├── SFClientRecon.cs      ~500 lines: snapshot RX, input TX, smoothing, divergence detect
│   └── SFClientRecon.csproj
├── launch-sf-headless.sh     Single-oracle launcher (Proton + batchmode)
├── launch-lobby.sh           Multi-lobby manager: spawn oracle, alloc port, write registry
├── stop-lobby.sh             Tear down one lobby by code
├── stop-all-lobbies.sh       Nuke all
├── list-lobbies.sh           Tabulate /tmp/sf-lobbies/*.conf
├── launch-sf-player.sh       Graphical 2nd-player instance for local testing
├── setup-all.sh              One-command build + deploy of both plugins
├── serve-lobbies.py          HTTP /lobbies JSON + HTML viewer
├── healthcheck.py            UDP Ping for liveness probes
├── stress-test-anticheat.py  Fires fake PlayerInput packets to verify rate-guard
├── deploy/                   Windows .bat wrappers (launch-lobby.bat, stop-all, list)
├── maps/                     123 Landfall scene JSON dumps (geometry, spawns, killboxes)
├── refs/                     Decompiled Assembly-CSharp.cs (~358 .cs files, not committed)
├── notes/
│   ├── ARCHITECTURE.md       This file
│   ├── PROTOCOL.md           Wire format spec
│   ├── VPS.md                Deployment guide
│   ├── BUGS_BACKLOG.md       Incident log (every bug we hit + fix shipped)
│   ├── recon/                Reverse-engineering notes from earlier phases
│   └── phase6/               Phase-by-phase design + status docs
└── legacy/                   Parked Go server + earlier client plugins
```

## Multi-lobby

Each lobby is a separate `StickFight.exe -batchmode -nographics` process spawned by `launch-lobby.sh`. Per-lobby state isolation:

| Resource | Per-lobby value |
|----------|-----------------|
| UDP port | base + N (default 1337+) |
| Bridge port | UDP port + 10000 (e.g. 11337) |
| Wineprefix | `/tmp/sf-oracle-prefix-<bridge-port>/` |
| Unity log | `/tmp/sf-oracle-unity-<bridge-port>.log` |
| Registry file | `/tmp/sf-lobbies/<CODE>.conf` |

~500 MB RAM per oracle, ~1 vCPU. Hobby VPS (8 GB / 4 vCPU) hosts ~6-8 concurrent.

In-process sharding (one SF.exe, multiple match scenes) is designed in [`phase6/12-PHASE6.13-sharding.md`](phase6/12-PHASE6.13-sharding.md) but not implemented — the singleton problem (`MultiplayerManager.Instance` etc.) makes it a much bigger lift than spinning up another process.

## Known tradeoffs (current)

- **Box-push has instant local feedback** (commit `6875908`) thanks to the hybrid NSO patch: clients have dynamic NSOs (local push works immediately) but `mHasControl=false` so they don't broadcast — oracle stays sole authority for canonical position. Trade-off: introduces P0-11 (destruction race) which is open.
- **Map-preset weapons spawn but per-map weapon allow-lists don't** — every map uses the global random weapon set. Phase 6.8 partial; full per-map allow-list awaits more research into SF's per-map config.
- **Workshop maps** not loaded at runtime (only the 123 pre-dumped Landfall scenes). Phase 6.16+.
- **Projectile hit registration** not server-side yet — clients still raycast their own bullets and emit PktPlayerTookDamage, which server validates by range. Phase 6.17 v0.2.
- **`GhostPlatform` / `MapInfoSyncableBase` objects sync via stock SF's fragile Vector2-keyed dictionary path** — see P0-12. Not yet folded into our v26 snapshot.
- **Anticheat is observation-only by default** — flip via `SF_ANTICHEAT_ENFORCE=1` once healthy traffic rates are dialled in.

## Deep-dive references

- [`AUDIT_2026-05-23.md`](AUDIT_2026-05-23.md) — full end-of-session audit covering destruction race, MapInfoSyncableBase precision, channel-routing false alarms, and first-snapshot-gap.
- [`OBJECT_SYNC.md`](OBJECT_SYNC.md) — definitive reference for the three world-object sync mechanisms (NSO, MapInfoSyncableBase, DestructiblePiece events). Reads as a debugging cheat sheet when you see boxes/platforms/ice misbehave.
- [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md) — every bug we've hit with root cause + fix or open status.

## Recent critical bug history (with fixes)

See [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md) for the full log with commit hashes.

The high-impact ones from this session:
- **PktClientJoined order** — sent AFTER PktClientSpawned, but stock SF's `OnPlayerSpawned` reads `mConnectedClients[b]` which is populated by `OnClientJoined`. Fixed by sending Join first, then Spawned.
- **SteamID overwrite from envelope** — `cli.SteamID = envelope.steamID` was wrong because SF's `SendP2PPacketToUser` puts the **destination's** SteamID in envelope, not sender's. When `OnClientJoined.PingAllUsers()` fired on P1 → packet at server had `from=P1.Addr` but `envelope=P2.SteamID` → P1's SfClient record got corrupted. Fixed by only setting SteamID once during `ClientRequestingIndex` (which carries identity in body).
- **PlayerUpdate forwarded on channel 0** — `NetworkPlayer.InitNetworkSpawnID` sets `mUpdateChannel = slot*2 + 2`. Receivers' NetworkPlayer listens on that channel; channel 0 forwards never reach a NetworkPlayer. Fixed by preserving incoming channel.
- **Spawned gate on PlayerUpdate forward** — `BroadcastStartMatch` resets `cli.Spawned=false`. `HandlePlayerUpdate` had `if (!Spawned) continue;` so PlayerUpdates stopped forwarding the moment /start fired. Fixed by gating on `Initialized` instead (set permanently after ClientInit).
- **Cross-client NSO authority fight** — every client had `mHasControl=true` (via client-shim) → every client broadcast position updates → boxes desync'd and randomly destructed from spurious local collisions. Fixed by removing the client-shim entirely; clients are now pure receivers, oracle is sole authority.
