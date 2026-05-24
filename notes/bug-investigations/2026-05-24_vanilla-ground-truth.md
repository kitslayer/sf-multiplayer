# Vanilla SF crate physics — runtime ground truth (2026-05-24)

> Status: **investigative findings captured** — confirms Bug F divergence + surfaces a new missing-mechanism finding

## Why this investigation

[`2026-05-24_v0.3.4-session-bugs.md`](2026-05-24_v0.3.4-session-bugs.md) identified Bug F: box physics divergence between vanilla and oracle. The root cause was inferred from code reading — needed runtime confirmation. This file captures Unity Explorer field reads from a **vanilla** (Goldberg + stock Assembly-CSharp + no SF plugins) SF instance for direct comparison.

## Setup used

- Install: `~/sf-mirror-local` (Goldberg-emulated Steam)
- Assembly-CSharp.dll = stock (md5 `b215e152afd2c4f3fa4271d780834e9a`)
- BepInEx plugins active: only `sinai-dev-UnityExplorer` (no SF mods)
- Map sampled: **Desert5**
- Target crate: **`Crate2 (6)`** (one of the simple-variant pushable crates — Rigidbody + NSO + RigidBodyIndexHolder, no AudioSource/ShakeOnImpact/IgnorePlayerWhenOffScreen)

A second crate variant **`Crate (3)`** in the same scene has the same physics setup plus 6 extra components: `AudioSource`, `RandomPitch`, `SoundPan`, `ShakeOnImpact`, `IgnorePlayerWhenOffScreen`, `LevelEditor.NetworkComponentTAG`. Worth knowing two variants exist; both behave the same physics-wise.

## Captured field values

### `UnityEngine.Rigidbody` on Crate2(6)

| Field | Value |
|---|---|
| `isKinematic` | **False** |
| `interpolation` | **None** |
| `mass` | **1500** |
| `drag` | 0 |
| `angularDrag` | 0 |
| `useGravity` | True |
| `collisionDetectionMode` | Discrete |
| `constraints` | None |
| `freezeRotation` | False |
| `detectCollisions` | True |
| `velocity` | (-0.0052, -0.0798, 0.0411) — small settling motion |
| `angularVelocity` | (0.0348, 0, 0.0099) |
| `centerOfMass` | (0, 0, 0) |
| `worldCenterOfMass` | (-0.015, 1.3827, 7.4513) |
| `inertiaTensor` | (2250, 1406, 1406) |
| `inertiaTensorRotation` | (0, 0, 0) |
| `solverIterationCount` | 6 |
| `solverIterations` | 6 |
| `solverVelocityIterationCount` | 1 |
| `solverVelocityIterations` | 1 |
| `sleepThreshold` | 0.005 |
| `sleepVelocity` | 0 |
| `sleepAngularVelocity` | 0 |
| `maxAngularVelocity` | 7 |
| `maxDepenetrationVelocity` | 1e23 (effectively infinite) |
| `useConeFriction` | False |
| `position` | (-0.0152, 1.388, 7.4495) |

### `RigidBodyIndexHolder` on Crate2(6)

| Field | Value |
|---|---|
| `Index` (property) | **0** (byte) |
| `mIndex` (private field) | 0 |
| `mInited` | True |

The init-once flag confirms RigidBodyIndexHolder.InitIndex was called during scene load. Stock SF uses this byte index in `NetworkPlayer.SendAddedForce(index, vector, ForceMode)` to identify which rigidbody to apply force to on remote clients (see `CollisionDamage.cs:28`).

### `NetworkSyncableObject` (still to capture)

Not yet sampled — non-blocking, would just confirm `m_Index` (ushort, separate from RBIH byte) and `mIsListening`. Add in a follow-up if needed.

## Divergence vs oracle setup (Bug F confirmation)

| Field | Vanilla | Oracle server (.115) | Oracle client | Diverges? |
|---|---|---|---|---|
| `Rigidbody.isKinematic` | False | False (matches vanilla) | **TRUE permanently** (SmoothTowardTargets forces it at `SFClientRecon.cs:500-501` every frame) | **YES** |
| `Rigidbody.interpolation` | None | None (untouched) | None (untouched) | no |
| `Rigidbody.mass` | 1500 | 1500 (prefab) | 1500 (prefab) | no |
| `Rigidbody.useGravity` | True | True (untouched) | irrelevant when kinematic | effectively yes (kinematic ignores gravity) |
| Force-sync mechanism | `NetworkPlayer.SendAddedForce(byte index, Vector3 vel, ForceMode)` per `CollisionDamage.cs:28` | not implemented | not implemented | **YES — entire mechanism absent** |

## What this confirms

**Bug F (the smoother-vs-push-relay logic conflict) is real.** The client's `SmoothTowardTargets` flips `isKinematic=true` every frame for any NSO with a target. Vanilla has `isKinematic=false`. They are inarguably different at runtime.

**Force-sync absence.** Vanilla SF uses `RigidBodyIndexHolder.Index` (byte) as the key for `NetworkPlayer.SendAddedForce` broadcasts. When player A collides with a crate:

```csharp
// CollisionDamage.cs:28 (stock SF)
byte index = component2.GetComponent<RigidBodyIndexHolder>().Index;
component.GetComponent<NetworkPlayer>().SendAddedForce(index, vector, ForceMode.VelocityChange);
```

Other clients receive this `SendAddedForce` message and apply the same `Rigidbody.AddForce(vector, ForceMode.VelocityChange)` to their local crate. Because all clients have `isKinematic=false`, the same impulse produces the same trajectory on each screen (modulo floating-point drift, which is small for short-duration physics).

The oracle setup never implements `SendAddedForce`. Instead it does 30Hz position snapshots (`SfNsoClientPush.RelayPushableCrateUpdates` → server → snapshot broadcast). That's a fundamentally different sync model:

| Property | Force-based (vanilla) | Position-based (oracle) |
|---|---|---|
| Sync data | byte index + Vector3 velocity + ForceMode | Vector3 position |
| Bandwidth | event-driven (only on collision) | 30Hz constant |
| Local sim | each client runs full physics | client interpolates between snapshots |
| Push feel | tactile (force = instant local response) | laggy (must wait for snapshot round-trip) |
| Failure mode if packet lost | minor desync on one client | drift accumulates |
| Compatibility with `isKinematic=true` | breaks (force on kinematic does nothing) | works (position is just set) |

The oracle picked position-based because it can be server-authoritative without trusting client-emitted forces. But the cost is the "boxes feel wrong" symptom: without the force-impulse mechanism, local push feel is lost; with kinematic-on-client forced by the smoother, even the brief tactile window evaporates.

## Two fix paths for the box problem

### Path 1: Fix Bug F without changing sync model
Stop SmoothTowardTargets from flipping `isKinematic=true`. Exclude pushable crates from the smoother per [`2026-05-24_v0.3.4-session-bugs.md`](2026-05-24_v0.3.4-session-bugs.md#bug-f). Client stays dynamic locally; local push feels right; server snapshots correct on big divergence. Loses some server authority (clients can cheat-push) but reasonable for non-comp use.

### Path 2: Add `SendAddedForce` to the wire protocol
New v26 message type: `PktPlayerCollideForce { byte playerSlot, byte rbIndex, Vector3 velocity, byte mode }`. Server validates (range check, magnitude plausibility) then relays. Each client applies `Rigidbody.AddForce(vector, ForceMode(mode))` to their local NSO. Same as vanilla but with server validation. More work but matches the comp-server authoritative model.

Worth doing Path 1 first (1-line plugin fix) to unblock the immediate symptom, then evaluate whether Path 2 is needed.

## Open questions

- Does Crate(3) (with `IgnorePlayerWhenOffScreen`) have any physics differences from Crate2(6)? Worth sampling.
- What is `NetworkSyncableObject.mHasControl` (static) on vanilla solo? Stock code says `MultiplayerManager.IsServer` controls it — in solo no-network match, IsServer is probably false. In our oracle setup we force IsServer=true on both sides, which flips mHasControl=true everywhere.
- Does SF's `NetworkPlayer.SendAddedForce` work in solo mode? If MatchmakingHandler.IsNetworkMatch is false (which it is in solo), SF might short-circuit the network broadcast and apply force locally only — confirming why solo "feels right" (no network = no divergence possible).

## Methodology

Unity Explorer 4.9.0 BepInEx5.Mono variant. Vanilla SF launched via Proton with Goldberg emulator. Two parallel instances (account_steamid ...992 and ...993) for 2-player tests. Field values read directly via UE's Inspector. No code instrumentation.
