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
- `u8 msgType` — packet type ID. Stock SF defines 0..38 (`P2PPackageHandler.MsgType`). Kit's patched DLL extends with 56/57. Our v26 protocol adds 39/40/41/42.
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
| v26.5 | + MapInfoSyncable position section (server-authoritative platform / pillar positions) | Phase 6.19 (P0-14 fix) |
| v26.6 | + MapInfoSyncable state section (`GetData()` payloads — GhostPlatform on/off, etc.) | terrain + weapons pass |

**Current snapshot wire version is v26.6.** Ground truth is `BuildWorldStateBody` in `../sf-headless-host/SFHeadlessHost.cs` (the single place the layout lives — both the periodic broadcast and the per-endpoint keyframe build through it). The two map sections (v26.5 positions + v26.6 state) are serialized in `WriteMapStateSection` / `MapStateSectionByteLen` in `../sf-headless-host/SfMapTerrainHost.cs`.

## v26 extensions (this repo)

### msgType 39 — `WorldStateSnapshot` (server → all clients, 30Hz)

Current format is **v26.6**. Backward-incompatible with earlier client builds — older `SFClientRecon.dll` will misparse the trailing sections.

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
u16 mapSyncCount (LE)                                                 (v26.5 + — MapInfoSyncableBase positions)
for each mapSync entry (20 bytes):
  f32 startX (LE)           -- m_StartPos.x; the cross-process key (quantized 0.01 by P0-12)
  f32 startY (LE)           -- m_StartPos.y
  f32 posX (LE)             -- current transform.position
  f32 posY (LE)
  f32 posZ (LE)
u16 mapStateCount (LE)                                                (v26.6 + — MapInfoSyncableBase GetData payloads)
for each mapState entry (9 + dataLen bytes):
  f32 startX (LE)           -- same Vector2 key as mapSync
  f32 startY (LE)
  u8  dataLen               -- length of the GetData() payload (capped at MapStateMaxPayload)
  dataLen bytes             -- type-specific state (e.g. GhostPlatform isOn = 1 byte)
```

- Sent to each spawned client's recorded v26 endpoint (discovered from their PlayerInput source addr).
- NSO entries are included for: dynamic bodies with non-zero velocity, kinematic bodies whose position changed since last snapshot (Phase 6.14.1 moving platforms), and 1s keepalive after motion stops. Static crates skip. Weapon NSO roots are excluded. The Y > -30 filter drops killbox-fallen NSOs.
- `mapSync` entries are keyed by the object's `m_StartPos` Vector2 (NOT `transform.GetInstanceID()` — Unity assigns instance IDs per-process, so they never match across server/client). P0-12 quantizes that Vector2 to 0.01 on both sides so the keys are stable cross-process.
- Snapshot fires when there is something to send (any player rig, NSO, or map entry) and at least one v26 endpoint is known.
- A full keyframe (all NSOs + map entries, no delta filter) is sent once to each newly-seen v26 endpoint, and a periodic full NSO keyframe is sent every ~5s.
- Total typical size: 4 players × 17 + 50 NSOs × 18 + projectile + map sections + headers ≈ 1 KB.

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

### msgType 42 — `V26Announce` (server → all clients, event-driven)

Server-emitted banner text the `SFClientRecon` plugin draws on-screen for ~3 seconds (e.g. lobby welcome / status notices).

```
N bytes  UTF-8 banner text (raw, no length prefix — body length comes from the packet)
```

- v25 envelope `steamID` is `0`.

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
| 43 | ClientHello (with v26 capabilities) | Reserved — pre-handshake to advertise plugin version |
| 44 | ServerStatus (heartbeat + lobby info) | Reserved — for in-band lobby browser without HTTP |

## Router control framing (SELECT) — NOT a msgType

The `sf-router` single-port front-door (see [`ROUTER.md`](ROUTER.md)) introduces
a **router-only** control datagram that is deliberately *outside* the game
envelope so the router can tell it apart from forwardable game traffic. It is
**not** a v26 msgType and never reaches a backend.

Framing (little-endian), distinct from `[u32 ts][u8 msgType][...]`:

```
[8] magic   = 53 46 52 54 52 00 00 01   ("SFRTR\0\0\1")
[1] op      = 0x01 SELECT | 0x02 LEAVE   (client→router)
              0x81 ACK                    (router→client)
SELECT/LEAVE: [1] codeLen, [codeLen] code (ASCII A-Z0-9), [4] nonce LE
ACK:          [1] status (0 ok / 1 no-such-lobby), [4] nonce LE
```

Disambiguation is total: a real SF datagram's byte[4] is a msgType ≤ ~46, but
`magic[4]='R'` (0x52 = 82), and the router additionally compares the full
8-byte magic. A SELECT that ever reached a backend by mistake would parse as
msgType 82 (out of range) and be ignored. The client resends SELECT at ~5 Hz
until snapshots flow, so loss of the unreliable control datagram self-heals.

## Compatibility

- v25 stock clients: handle 0..38. Receive but ignore 39/40/41/42/56/57.
- v25 patched clients (kit's DLL): handle 0..38 + 56/57. Receive but ignore 39/40/41/42.
- v26 clients (with `SFClientRecon.dll`): handle all of the above.
- v26 server (our oracle): emits 39 + 42, accepts 40 + 41, relays 56/57.

The snapshot wire format (msgType 39) must match between oracle and `SFClientRecon.dll` — each version bump (v26.2 added `lastInputSeq`; v26.5/v26.6 added the two map sections) appends to the body, so an older client misparses the new trailing fields. Keep both DLLs on the same build.
