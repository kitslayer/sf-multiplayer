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

### `NetworkSyncableObject` — solo, single instance (initial capture)

C# Console REPL on instance 1 (solo lobby, before any Quick Match):

```
mHasControl=True
```

Static field. Returns True even in solo mode — because solo SF treats the local player as the host. Note this is different from the 2-player case below.

### `NetworkSyncableObject` — 2-player joined (Goldberg LAN match)

Two parallel Goldberg-emulated instances joined via Quick Match. Inspector reads on a Crate's NetworkSyncableObject component:

**Host (instance 1, "yellow", account `sf_test_local2`):**

| Field | Value |
|---|---|
| `mHasControl` | **True** |
| `mIsListening` | **True** |
| `m_AllowForceFromClient` | True |
| `m_VelocitySync` | False |
| `m_UsesMoveAlongPathUsingForce` | True |
| `Index` | 19 (ushort) |
| `ListeningForPackages` | True |
| `firstPassFlag` | False |
| `m_DeadZone` | 0.1 |
| `m_DirectionFractor` | 0.005 |
| `m_EndPos` | (-0.0007, -6.6777, 1.5073) |

**Client (instance 2, "blue", account `sf_test_local2_p2`):**

| Field | Value |
|---|---|
| `mHasControl` | **False** |
| `mIsLerping` | True (was actively lerping toward incoming position at sample time) |
| `m_VelocitySync` | False |
| `m_UsesMoveAlongPathUsingForce` | True |
| `m_TimeBetweenPackages` | 0.2485 (~4Hz update from host) |
| `m_TimeOfLastPackage` | 3676.666 (game-time of most recent state from host) |
| `m_DirectionFractor` | 0.005 |
| `mCurrentSendTickCount` | 0 |
| `mDontSyncPos` | False |
| `mHasRecievedHelloPackage` | False (the hello-message handshake) |

(Different `Object.name` between sides — host showed `Crate (3)` instance 71162, client showed `Crate (6)` instance 72940. Worth re-confirming same network entity by matching `m_Index` — host = 19, client value not captured yet.)

### Authority model summary

| State | Host vanilla | Client vanilla | Our oracle (server) | Our oracle (client) |
|---|---|---|---|---|
| `mHasControl` | **True** | **False** | True (forced via P6.5 patch) | **True (forced — DIVERGES from vanilla)** |
| Rigidbody integration | full local physics | mostly kinematic + lerp | full local physics | competing: physics + lerp (SmoothTowardTargets fights) |
| Broadcasts state | yes (host owns) | no | yes | yes (also tries to broadcast — fights with server snapshots) |
| Lerps toward incoming pos | no | yes | no | yes (via `_nsoTargets`) |

## Divergence vs oracle setup (Bug F confirmation)

| Field | Vanilla host | Vanilla client | Oracle server (.115) | Oracle client | Diverges? |
|---|---|---|---|---|---|
| `NSO.mHasControl` (static per-process) | **True** | **False** | True | **True (forced)** | **YES — client side is wrong** |
| `Rigidbody.isKinematic` | False (active sim) | mostly False but mIsLerping switches behaviors | False (matches host) | **TRUE permanently** (SmoothTowardTargets forces it) | **YES** |
| `Rigidbody.interpolation` | None | None | None | None | no |
| `Rigidbody.mass` | 1500 | 1500 | 1500 | 1500 | no |
| `Rigidbody.useGravity` | True | True | True | irrelevant when kinematic | effectively yes |
| Force-sync mechanism | `NetworkPlayer.SendAddedForce` (broadcasts on collision) | applies received force | not implemented | not implemented | **YES — mechanism absent** |
| Lerp toward remote state | no (it IS the authority) | yes (`mIsLerping=True` continuously) | n/a | yes but with kinematic flip | partially |

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

## Architectural implication (added after 2-player capture)

Our oracle's "both sides are server" pattern (forcing `MultiplayerManager.IsServer=true` and therefore `NSO.mHasControl=true` on every connected client) is **not** how vanilla SF works. Vanilla picks ONE owner (the host) and everyone else is `mHasControl=false`. The non-owners lerp toward incoming state and never broadcast outgoing.

To match vanilla's quality without losing server authority:

- **Server** (the oracle on .115): `mHasControl=true`. This is the "host" in vanilla terms.
- **All connected clients**: `mHasControl=false`. They should lerp, not broadcast.

Our current `SFClientRecon.cs:1603-1610` postfix that forces `mHasControl=true` on every NSO start at the client should be **removed** (let it default to false). The smoother's existing lerp-toward-target logic at `SFClientRecon.cs:500-501` is fine — it matches what vanilla `mIsLerping=true` clients do. The conflict goes away because there's no local-side physics integration to fight with.

This single change is potentially the root fix for the "boxes feel wrong" family of symptoms — it preserves the smoother's behavior while eliminating the double-authority race. Worth testing in isolation before adding the more complex `SendAddedForce`-style protocol additions.

## Open questions

- Does Crate(3) (with `IgnorePlayerWhenOffScreen`) have any physics differences from Crate2(6) at runtime? Quick check.
- Does removing the `mHasControl=true` force on clients actually fix Bug F symptoms in oracle play, or does it expose other code paths that assumed the force was in place?
- Vanilla client shows `mIsLerping=True` continuously — does the oracle's snapshot apply path drive an equivalent flag, or do we bypass SF's own lerp infrastructure entirely?
- Vanilla host shows `mIsListening=True` (unexpected — would have predicted False since host publishes, doesn't listen). Means the host has its OWN local copy of the listener path active, possibly for the case of being kicked / re-joining. Doesn't change our fix model but worth knowing.
- `m_AllowForceFromClient=True` on this crate — vanilla supports per-NSO opt-in for client-side forces. Use this as the gate in a future `PktPlayerCollideForce` v26 message implementation: only forward client-emitted forces for NSOs where `m_AllowForceFromClient=true`.

## Methodology

Unity Explorer 4.9.0 BepInEx5.Mono variant. Vanilla SF launched via Proton with Goldberg emulator. Two parallel instances (account_steamid ...992 and ...993) for 2-player tests. Field values read directly via UE's Inspector. No code instrumentation.
