# Wire protocol reference

What every packet looks like on UDP. Stock SF v25 plus our v26 extensions. Numbers are bytes unless noted. All multi-byte ints / floats are **little-endian**.

## Envelope (universal)

Every packet, in either direction:

```
+----+----+----+----+    +----+    +- - body - -+    +----+----+----+----+----+----+----+----+    +----+
|  u32 timestamp    | -> | u8 |    |  N bytes   | -> |        u64 steamID (LE)            | -> | u8 |
|       (LE)        |    |type|    |            |    |                                    |    | ch |
+----+----+----+----+    +----+    +- - - - - --+    +----+----+----+----+----+----+----+----+    +----+
```

Total = 5 (prefix) + N (body) + 9 (suffix) = **N + 14**. Minimum packet = 14 bytes (zero-body).

- `u32 timestamp` — seconds since Unix epoch. Cosmetic in current impl, not used for ordering.
- `u8 msgType` — packet type ID. Stock SF defines 0..38 (`P2PPackageHandler.MsgType`). Kit's patched DLL extends with 56/57. Our v26 protocol adds 39/40.
- `body` — type-specific payload.
- `u64 steamID` — sender's SteamID (Goldberg fake or real Steam). Used by the server to identify who's talking. v26 server-emitted packets use `0`.
- `u8 channel` — Unity event channel. Stock SF uses 0/1 for general traffic, NSO updates on channel 10, weapon throw on `slot*2 + 3`, player talk on `slot*2 + 3`.

## Stock SF v25 message types (0..38)

| ID | Name | Direction | Body shape | Notes |
|----|------|-----------|------------|-------|
| 0  | Ping | both | empty | Server echoes as PingResponse |
| 1  | PingResponse | server → client | empty | |
| 2  | ClientJoined | server → all | `byte slot, u64 steamID` | Broadcast on new join |
| 3  | ClientRequestingAccepting | client → server | empty | First handshake step |
| 4  | ClientAccepted | server → client | empty | Server says "you may proceed" |
| 5  | ClientInit | server → client | 50 bytes | Slot assignment + lobby state |
| 6  | ClientRequestingIndex | client → server | `byte protoVer, u64 steamID, …` | Asks for slot |
| 7  | ClientRequestingToSpawn | client → server | `byte slot, 6×f32 pos+euler` | Asks for spawn |
| 8  | ClientSpawned | server → all | `byte slot, 6×f32 pos+euler, bool flag, i32 colorCount` | flag=0 revive, flag=1 forced die |
| 9  | ClientReadyUp | client → server | `byte count, byte[count] slots` | Ready handshake |
| 10 | PlayerUpdate | both | `i16 posY/100, i16 posZ/100, …` | 60Hz client→server, relayed to others |
| 11 | PlayerTookDamage | client → server | `byte attackerIdx, f32 dmg, bool playFx, …, byte dmgType` | Broadcast to all (sender too, for killing-blow signal) |
| 12 | PlayerTalked | client → server | UTF-8 string (raw, no length prefix) | Channel = `slot*2+3`. Relayed; also parsed for `/`-prefixed admin commands. |
| 13 | PlayerForceAdded | client → server | force impulse data | Relay to others |
| 14 | PlayerForceAddedAndBlock | client → server | impulse + block flag | Relay to others |
| 15 | PlayerLavaForceAdded | client → server | impulse | Relay to others |
| 16 | PlayerFallOut | client → server | fall data | Relay to others |
| 17 | PlayerWonWithRicochet | client → server | win data | Relay to all |
| 18 | MapChange | server → all | `byte winnerIdx, byte mapType, …` | Triggers scene load |
| 19 | WeaponSpawned | server → all | weapon spawn data | Spawned by GameManager weapon-spawn timer |
| 20 | WeaponThrown | server → all | weapon throw data | Channel = `slot*2+3` |
| 21 | RequestingWeaponThrow | client → server | throw request | Server appends IDs + broadcasts as WeaponThrown |
| 22 | ClientRequestWeaponDrop | client → server | drop request | Server appends IDs + broadcasts as WeaponDropped |
| 23 | WeaponDropped | server → all | drop data | |
| 24 | WeaponWasPickedUp | server → all | pickup data | |
| 25 | ClientRequestingWeaponPickUp | client → server | pickup request | Server broadcasts WeaponWasPickedUp |
| 26 | ObjectUpdate | client → server | `u16 idx, position bytes` | NSO position sync, 5Hz from owner, channel 10. Relay to others. |
| 27 | ObjectSpawned | server → all | object spawn data | |
| 28 | ObjectSimpleDestruction | client → server | `u16 idx` | Relay to **all** (include sender — see [ALKA P0-3 fix](https://github.com/AlkaPrime12/Stickfight-TestingMultiplayer/blob/main/docs/BUGS_BACKLOG.md)) |
| 29 | ObjectInvokeDestructionEvent | client → server | `u16 idx` | Relay to all |
| 30 | ObjectDestructionCollision | client → server | `u16 idx, collision data` | Channel 11. Relay to all. |
| 31 | GroundWeaponsInit | server → all | `u16 count, [f32 x, f32 y, u16 weaponID, u16 syncID] × N` | Map-preset weapons. Emitted by `CheckForGroundWeapons`. |
| 32 | MapInfo | server → client | map metadata | |
| 33 | MapInfoSync | server → all | `f32 startPosX, f32 startPosY, [N bytes data]` | Stock SF's separate sync path for `MapInfoSyncableBase`-derived map objects (`GhostPlatform`, `MoveAlongPathUsingForce`, `PillarHandler`, `PlayMoveAnimations`). 5Hz when active. Client dispatches by `Vector2(startPosX, startPosY)` dictionary lookup — bit-exact float compare, see P0-12 in BUGS_BACKLOG.md. Channel 0. |
| 34 | WorkshopMapsLoaded | server → all | workshop map cycle | |
| 35 | StartMatch | server → all | empty | Kick off round countdown |
| 36 | ObjectHello | client → server | `u16 idx` | Client requesting initial object state |
| 37 | OptionsChanged | client → server | options blob | Relay to others |
| 38 | KickPlayer | server → all | `byte slot` | Host kick. Relay to all including target. |

## Patched-DLL extensions (kit's patched Assembly-CSharp.dll)

| ID | Name | Direction | Body | Notes |
|----|------|-----------|------|-------|
| 56 | LerpPlayer | client → server | empty | Triggers remote-lerp on NetworkPlayer. Blind-relay to others. |
| 57 | ColorChanged | client → server | HTML color string (4-64 bytes) | Player color change. Blind-relay to others. |

## v26 wire-format version history

| Version | Snapshot shape change | Shipped |
|---------|----------------------|---------|
| v26.0 | initial — players-only snapshot | Phase 6.10 |
| v26.1 | + NSO section | Phase 6.14 |
| v26.2 | + `lastInputSeq` per player | Phase 6.12.2 prep |
| v26.3 | + projectile section + kinematic NSO position-delta detection | Phase 6.14.1 + 6.17 |

## v26 extensions (this repo, added 2026-05-23)

### msgType 39 — `WorldStateSnapshot` (server → all clients, 30Hz)

Current format is v26.3 (after Phase 6.17 added projectile section). Backward-incompatible with earlier client builds.

```
u32 serverTick (LE)
u8  playerCount
for each player (17 bytes):
  u8  slot                  (0-3)
  f32 posX (LE)
  f32 posY (LE)
  f32 posZ (LE)
  u32 lastInputSeq (LE)     -- server's last-acked PktPlayerInput.sequenceNum for this slot
u16 nsoCount (LE)
for each NSO (18 bytes):
  u16 networkID (LE)        -- NetworkSyncableObject.Index
  f32 posX (LE)
  f32 posY (LE)
  f32 posZ (LE)
  f32 rotZ (LE)             -- transform.eulerAngles.z; pitch+yaw are zero in SF
u16 projCount (LE)                                                    (Phase 6.17 +)
for each projectile (18 bytes):
  u32 id (LE)
  u8  ownerSlot
  u8  weaponType
  f32 posX (LE)
  f32 posY (LE)
  f32 posZ (LE)
```

- Sent to each spawned client's recorded v26 endpoint (discovered from their PlayerInput source addr).
- NSO entries are included for: dynamic bodies with non-zero velocity, kinematic bodies whose position changed since last snapshot (Phase 6.14.1 moving platforms), and 1s keepalive after motion stops. Static crates skip.
- Snapshot only fires when `_matchStarted == true` and at least one client is connected.
- Total typical size: 4 players × 17 + 50 NSOs × 18 + headers + projectile section ≈ 1 KB.

### msgType 40 — `PlayerInput` (client → server, up to 60Hz)

```
u32 sequenceNum (LE)        -- monotonic per-client
u8  slot                    (0-3, validated server-side)
f32 stickX                  (clamped to [-1, 1])
f32 stickY                  (clamped to [-1, 1])
f32 aimX                    (clamped to [-1, 1])
f32 aimY                    (clamped to [-1, 1])
u32 buttons                 -- bit 0: jump / bit 1: fire / bit 2: block / bit 3: throw
```

- Total body = 25 bytes.
- Server uses source IPEndPoint as the snapshot send target (no separate "register port" step).
- Server clamps stick/aim to [-1, 1] before feeding into `SlotInputs`; rejects NaN/Inf or |v| > 1.5 entirely.
- v25 envelope `steamID` is `0` (server identifies by slot byte in body).

### msgType 41 — `ClientFireWeapon` (client → server, event-driven)

Sent by `SFClientRecon`'s Harmony postfix on `Weapon.ActuallyShoot` when the local player fires. Server registers a virtual projectile + simulates it.

```
u8  ownerSlot               (0-3)
u8  weaponType              (passthrough byte; not yet interpreted)
f32 originX                 (muzzle world position)
f32 originY
f32 originZ
f32 dirX                    (normalized forward)
f32 dirY
f32 dirZ
f32 speed                   (units/sec; 0 → server uses default 60 u/s)
```

- Total body = 30 bytes.
- Only emitted for local player (HasControl=true on the parent Controller).

## Channel encoding

The `u8 channel` byte at the end of the envelope has gameplay meaning for some msgTypes. **Ground truth is `P2PPackageHandler.GetChannelForMsgType` (`refs/decompiled/Assembly-CSharp/P2PPackageHandler.cs:310-344`)** — copy of stock SF's per-msgType routing, reproduced here verbatim:

| Channel | msgTypes routed here by stock SF |
|---------|---------|
| 0 | `Ping`, `PingResponse`, `ClientInit`, `ClientJoined`, `ClientRequestingIndex`, `ClientRequestingToSpawn`, `ClientSpawned`, `MapInfoSync`, `PlayerForceAddedAndBlock` |
| 1 | `MapChange`, `WeaponSpawned`, `WeaponWasPickedUp`, `ClientRequestingWeaponPickUp`, `ClientRequestWeaponDrop`, `WeaponDropped`, `ObjectSpawned`, `ObjectSimpleDestruction`, `ObjectInvokeDestructionEvent`, `GroundWeaponsInit`, `MapInfo`, `PlayerFallOut`, `OptionsChanged`, `KickPlayer`, `ClientAccepted`, `ClientRequestingAccepting`, `ClientReadyUp`, `StartMatch`, `WorkshopMapsLoaded` |
| 10 | `ObjectUpdate` (NSO 5Hz delta, per `mUpdateChannel`) |
| 11 | `ObjectDestructionCollision` (NSO destruction events) |
| `slot*2 + 2` | `PlayerUpdate` (per-slot owner update; `NetworkPlayer.InitNetworkSpawnID` sets `mUpdateChannel = slot*2 + 2`) |
| `slot*2 + 3` | Per-slot event channel: `PlayerTalked`, `WeaponThrown`, `RequestingWeaponThrow` (and chat commands ride on this). `mEventChannel = mUpdateChannel + 1` |

**Important polling caveat**: stock SF's `P2PPackageHandler.CheckForPackagesOnChannel` polls BOTH channels 0 and 1 for the lobby/setup msgType set (`P2PPackageHandler.cs:110-116`):

```csharp
CheckForPackagesOnChannelInMainMenu();   // channel 0
CheckForPackagesOnChannelInMainMenu(1);  // channel 1
CheckForPackagesOnChannel(1);            // channel 1
CheckForPackagesOnChannel();             // channel 0
```

So any of the channel-0 or channel-1 msgTypes will be dispatched regardless of which of the two they arrive on. Only the slot-routed channels (`slot*2+2`, `slot*2+3`, `10`, `11`) have tight routing — wrong channel = silent drop. This is why P0-1 in BUGS_BACKLOG.md was so painful (PlayerUpdate forwarded on channel 0 instead of slot*2+2).

## Future v26 IDs (reserved)

| ID | Tentative name | Status |
|----|----------------|--------|
| 42 | ServerEvent (reliable event channel) | Reserved — for damage events, kill confirms, etc. when we move off PlayerTookDamage |
| 43 | ClientHello (with v26 capabilities) | Reserved — pre-handshake to advertise plugin version |
| 44 | ServerStatus (heartbeat + lobby info) | Reserved — for in-band lobby browser without HTTP |

## Compatibility

- v25 stock clients: handle 0..38. Receive but ignore 39/40/56/57.
- v25 patched clients (kit's DLL): handle 0..38 + 56/57. Receive but ignore 39/40.
- v26 clients (with `SFClientRecon.dll`): handle all of the above.
- v26 server (our oracle): emits 39, accepts 40, relays 56/57.

The wire-format bump at v26.2 (lastInputSeq added to per-player snapshot entry) requires matched plugin builds on both sides — older `SFClientRecon.dll` will misparse the snapshot.
