# Object sync reference

How world-state (boxes, ice, chains, moving platforms, pillars, destructibles) is synced in Stick Fight. Written after the 2026-05-23 audit consolidated the scattered findings.

## TL;DR

Stock SF has **three** separate sync mechanisms for non-player world state. Most bugs in this project come from confusing them. Knowing which mechanism a given object uses is the prerequisite to debugging its sync.

| Mechanism | Used by | Wire | Authority |
|---|---|---|---|
| **NetworkSyncableObject** (NSO) | Boxes, crates, ice, chains, ragdolls, any free-moving prop | `ObjectUpdate` (26) channel 10 5Hz, plus our v26 `WorldStateSnapshot` 30Hz | The instance with `mHasControl=true` (static field — see Finding 6 in AUDIT_2026-05-23.md) |
| **MapInfoSyncableBase** | `GhostPlatform`, `MoveAlongPathUsingForce`, `PillarHandler`, `PlayMoveAnimations` | stock `MapInfoSync` (33) channel 0 5Hz, **plus** our v26 `WorldStateSnapshot` mapSync (positions, v26.5) + mapState (`GetData()` payloads, v26.6) sections at 30Hz | The host (`IsServer && IsNetworkMatch`) |
| **DestructiblePiece** events | Anything destructible (riding on top of either of the above) | `ObjectSimpleDestruction` (28) / `ObjectInvokeDestructionEvent` (29) / `ObjectDestructionCollision` (30) channel 11 event-driven | Whichever side detected the breaking collision |

## NetworkSyncableObject (NSO)

The big workhorse. Source: `refs/decompiled/Assembly-CSharp/NetworkSyncableObject.cs`.

### Identity
- `m_Index` (ushort) — unique scene-wide ID, assigned during scene load
- Registered in `mNetworkManager.AddSyncableObject(m_Index, this)` from `Init()` (line 162)
- Auth side is determined by `mHasControl` which is a **static field** (line 42) — it's process-global, not per-instance. Set in `Start()` to `MultiplayerManager.IsServer` (line 192). On our oracle this is `true`, on clients it's `false`.

### Stock SF authority flow
1. Host (`IsServer=true`) has `mHasControl=true`. Each NSO's `LateUpdate` calls `TickSyncPos()` at 5Hz when `mHasControl && mIsListening && !mIsSnake && !m_OnlyRecieveInitState` (line 253).
2. `TickSyncPos` calls `SendNewObjectStatePackage` which fires `ObjectUpdate` (msgType 26) on channel 10 via `mPacketHandler.SendP2PPacketToServer` (or in host case, via `mNetworkManager.OnObjectUpdate`).
3. Clients (`mHasControl=false`) receive via `ListenForPackages(channel=10)` (line 287) → `ReceivedPackage` → `LerpLocalDummy` between snapshots.
4. Clients have `m_ShouldDisableAllRigidBodiesOnInit=true` (line 84), so `Init()` calls `DisableAllRigidBodies()` (line 168). That makes their rigidbodies `isKinematic = true` (line 231) — purely visual, no local physics.
5. Special falloff path: if an authoritative NSO's `mObjectToSync.position.y < -50f`, it sends one final `ObjectUpdate` and then sets `mIsListening = false` (line 257-261). This is "the object fell off the map."

### Important exemptions in `DisableAllRigidBodies` (line 222-235)
- NSOs with a `MoveAlongPathUsingForce` component stay DYNAMIC even on clients (`flag=true` exempts them).
- NSOs marked `m_HardSync` stay DYNAMIC even on clients.

Both exist because those NSOs **need** local force application to look right; stock SF accepts the cross-client drift this causes (Finding 3a in AUDIT_2026-05-23.md).

### Our oracle's NSO behavior
- Oracle is `IsServer=true` (via Phase 6.5 Harmony patch). So oracle's NSOs all have `mHasControl=true` and DO broadcast `ObjectUpdate` via the host code path.
- Our `SendBroadcastPrefix` (`SFHeadlessHost.cs:526`) intercepts the host-side `SendMessageToAllClients` call and forwards the packet over our v25 UDP socket to real clients.
- Our v26 `WorldStateSnapshot` (msgType 39, 30Hz) adds a global NSO snapshot section as a higher-rate redundant sync, with position-delta inclusion filter + 1s keepalive after motion stops.
- We have an outbound Y < -30 filter for `ObjectUpdate` (`SFHeadlessHost.cs:597-606`) to avoid forwarding the "object fell off killbox" update.
- Clients have a hybrid state since commit `6875908`: `DisableAllRigidBodies` is skipped (so NSOs stay dynamic for local push feedback) but `mHasControl` is NOT forced to true (so clients don't broadcast).

### Known issues with NSO sync
- **P0-11** (destruction race) — outbound destruction filter masks legitimate server-side breaks
- **P0-13** (first-snapshot gap) — late-joining clients see stale positions for at-rest NSOs until something pushes them
- **P0-15** (lerp-collision shatter) — swept lerp motion can fire `OnCollisionEnter` with high relativeVelocity, causing ice/destructibles to "randomly" break

See `BUGS_BACKLOG.md` for full details.

## MapInfoSyncableBase

A separate sync system that pre-dates the NSO architecture (or developed in parallel). Used for level objects that need their state preserved but don't fit the NSO model.

### Identity
- Each object's identity is its `Vector2(transform.position.y, transform.position.z)` at Awake-time, stored in `m_StartPos`.
- Registered in `MultiplayerManager.mMapDataObjectToSync : Dictionary<Vector2, MapInfoSyncableBase>` via `AddMapDataObject` (line 1105).
- **Lookup is bit-exact**: stock SF uses C# Dictionary's default `Equals` for the `Vector2` key, which compares `x` and `y` floats with full precision. One ULP off = forever-missing lookup.

### Stock SF authority flow
1. `MapInfoSyncableBase.Awake` (line 23-34) sets `m_NetworkControl = MatchmakingHandler.IsNetworkMatch && MultiplayerManager.IsServer`. Only the host has this true.
2. `Update` (line 36-42) calls `TickSyncPos()` if `m_NetworkControl=true`. That sends an outbound `MapInfoSync` (msgType 33) every `1/m_SendRatePerSecond` seconds (default 5Hz).
3. Wire format: `[f32 startPos.x][f32 startPos.y][bytes data]`. The body's first 8 bytes are the Vector2 key (for client-side dispatch); the rest is type-specific state from `GetData()`.
4. Clients receive via `P2PPackageHandler.CheckMessageType` on channel 0 → `OnMapDataRecieved` → reads Vector2 key, looks up in `mMapDataObjectToSync`, calls `SetData(data2)` on the found object.

### Subclass behaviors

#### `GhostPlatform.cs` — flickering platforms
- Server's `Update` advances an `onTime`/`offTime` timer; starts coroutines `FadeOut`/`FadeIn` when timer expires (line 81-92).
- Sync payload: 1 byte (`isOn` as 0 or 1).
- Position is snap (`transform.localPosition = startPosition` or `startPosition + Vector3.right * 2f` — line 139, 161). **Drift-free** — if `SetData` arrives, behavior is identical across clients.
- **Fragility**: stuck `isOn=true` (initial value) on any client whose Vector2 lookup fails.

#### `MoveAlongPathUsingForce.cs` — moving platforms
- Every client (server too) calls `rig.AddForce(...)` every frame to push the platform toward `positions[currentTargetId]` (line 84).
- Only server advances `currentTargetId` and sends `SetData` (line 102-105).
- Sync payload: 1 byte (`currentTargetId`).
- **Drifts**: clients integrate physics locally; without deterministic physics across machines, positions diverge within seconds of a waypoint switch.
- The NSO `DisableAllRigidBodies` exemption (Finding 7 in AUDIT_2026-05-23.md) intentionally keeps these dynamic on clients.

#### `PillarHandler.cs` — pressure-plate elevators
- Every client runs `MoveTowardsValue` (line 54-58): spring integrator `velocity += (... - position.y) * Time.deltaTime * 20f; position.y += velocity * Time.deltaTime`.
- `value` accumulates from `isBeingStoodOn` (local collision detection).
- Only server's `value` is sync'd; clients integrate position from `value` locally.
- **Drifts**: same problem as `MoveAlongPathUsingForce`. Plus, `isBeingStoodOn` is detected locally so server and client can disagree about whether a pillar is currently pressed.

#### `PlayMoveAnimations.cs`
- Not investigated in detail. Probably plays an Animator at a synced state.

### Known issues
- **P0-12** (FIXED) — Vector2 key precision could cause silent lookup failure on clients; both sides now quantize the key to 0.01.
- **P0-14** (FIXED) — `MoveAlongPathUsingForce` and `PillarHandler` drifted across clients; now server-authoritative.

See `BUGS_BACKLOG.md`. **The fix shipped** (v26.5 + v26.6): the oracle broadcasts each `MapInfoSyncableBase`'s position (mapSync section) and `GetData()` state (mapState section) inside the v26 `WorldStateSnapshot`, and the client applies them. The snapshot keys these objects by their `m_StartPos` **Vector2** (the same key stock SF's `MapInfoSync` uses, quantized by P0-12) — **not** `transform.GetInstanceID()`, because Unity assigns instance IDs per-process so they never match across server and client.

## DestructiblePiece events

Riding on top of either NSO (boxes, ice) or non-NSO objects, `DestructiblePiece` adds the "shatter on collision" mechanic. Source: `refs/decompiled/Assembly-CSharp/DestructiblePiece.cs`.

### Variants
- `simpleDestruction=true` (ice blocks, glass): destroys all `ConfigurableJoint`s and `Collider`s in children (line 154-163)
- `eventDestruction=true` (chains): invokes a UnityEvent `destructionEvent` (line 167-173)
- Default (most boxes): full physics shatter — destroys body, sprays Props with impulse (line 175-204)

### Collision flow
- `OnCollisionEnter` (line 47-68): checks rigidbody mass for multiplier, gates on `Controller.HasControl` for player collisions (only LOCAL player collisions trigger; remote players' collisions skip — important).
- `Collide(force, multiplier, networkForce)` (line 121-148): in network match, if force exceeds threshold, calls `SendDestructMessage`.
- `SendDestructMessage` (line 98-119): on host, applies + broadcasts; on client, sends `ObjectDestructionCollision` (msgType 30) on channel 11 to server.
- Server's `RelayBodyToAll` forwards to all clients (sender included — see C-3/P0-3 fix shape).
- Client receives via `ListenForEventPackages(11)` (line 316) → `ReceivedDestruction` (line 82-96) → `NetworkForceDestruction`.

### Force calculation
- `force = collision.relativeVelocity * 0.1f`
- `multiplier` ranges from 1 (regular collision) to 50 (heavy rigidbody, mass > 1000)
- Per-piece `forceThreshold` field (set in prefab)

### Known issues
- **P0-15** — Lerp reconciliation of NSOs can produce high `relativeVelocity` during the smoothing interpolation, firing spurious destructions.

## Cheat sheet for debugging an object sync bug

| Symptom | Likely mechanism | Where to look first |
|---|---|---|
| Box at different position on each client | NSO | v26 snapshot + Y < -30 filter + first-snapshot gap (P0-13) |
| Ice randomly breaks for no reason | DestructiblePiece | P0-15 (lerp-collision) or P0-11 (filter masking real break) |
| Box "vanishes" on one client only | DestructiblePiece | P0-11 (server-originated destruction filter eating real event) |
| Chain swings differently per client | NSO (positional) or DestructiblePiece (visual) | Usually fine — chains tend to settle |
| Moving platform at different position per client | MapInfoSyncableBase (`MoveAlongPathUsingForce`) | P0-14 (FIXED — now in v26.5/v26.6 mapSync/mapState snapshot); if still drifting, check the object is being collected + the Vector2 key matches |
| Pressure pillar at different height per client | MapInfoSyncableBase (`PillarHandler`) | P0-14 (FIXED — server-authoritative via snapshot) |
| Flickering platform on/off out of sync | MapInfoSyncableBase (`GhostPlatform`) | P0-12 (Vector2 key precision failure) |
| Workshop map object behaves weirdly | depends on what the prefab is | Identify base class via decompile first |

## Open architectural questions

1. **Is `m_HardSync` worth promoting?** Setting NSOs as `m_HardSync` skips the kinematic-on-client step. Combined with our outbound snapshot, this might be a cleaner pathway than the current `SkipPrefix` blanket-skip.
2. ~~**Should MapInfoSyncableBase fold into the v26 snapshot?**~~ **DONE** (v26.5 positions + v26.6 state). Keyed by `m_StartPos` Vector2, not `transform.GetInstanceID()` (per-process IDs don't match cross-machine). See the MapInfoSyncableBase "Known issues" above.
3. **Are there other `MapInfoSyncableBase`-derived classes I haven't catalogued?** Search returned 4 (`GhostPlatform`, `MoveAlongPathUsingForce`, `PillarHandler`, `PlayMoveAnimations`). Need to verify by checking every map JSON for unrecognized GameObject types.
