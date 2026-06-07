# Vanilla SF mechanisms the oracle never implemented (2026-05-24)

> Status: **investigative** — three vanilla SF components / message paths that our oracle code has zero references to. Each represents a sync model the oracle either skips or replaces with a different (and apparently lossy) mechanism.

Surfaced during the [Unity Explorer ground-truth session](2026-05-24_vanilla-ground-truth.md). When inspecting `Crate2(6)` and `Crate(3)` on vanilla SF, three components and one network message family appeared that don't appear anywhere in `sf-headless-host/` or `sf-client-recon/`.

## 1. `RigidBodyIndexHolder` + `NetworkPlayer.SendAddedForce`

### What it is

`RigidBodyIndexHolder` is a per-rigidbody MonoBehaviour that assigns a **byte** index (0-255) to a physics body. Stock SF uses it for force-application sync — distinct from `NetworkSyncableObject.Index` (ushort) which is used for position/destruction sync.

```csharp
// refs/decompiled/Assembly-CSharp/RigidBodyIndexHolder.cs (full file)
public class RigidBodyIndexHolder : MonoBehaviour
{
    private byte mIndex;
    private bool mInited;
    public byte Index => mIndex;
    public void InitIndex(byte index) { ... }
}
```

Confirmed at runtime on Crate2(6) in Desert5: `RigidBodyIndexHolder.Index = 0`, `mInited = True`.

### How vanilla SF uses it

```csharp
// refs/decompiled/Assembly-CSharp/CollisionDamage.cs:28
byte index = component2.GetComponent<RigidBodyIndexHolder>().Index;
component.GetComponent<NetworkPlayer>().SendAddedForce(index, vector, ForceMode.VelocityChange);
```

When player A's body collides with a crate, this code path triggers. Vanilla SF then sends a network message naming **(byte rbIndex, Vector3 velocity, ForceMode mode)** to all other players. Each remote client looks up the same rbIndex locally and applies `Rigidbody.AddForce(velocity, mode)`. Because all clients have `Rigidbody.isKinematic = false`, identical force impulses produce identical trajectories (modulo floating-point drift, which is small for short durations).

Stock SF also uses RBIH in `ProjectileCollision.cs:385, :422, :459` to attribute projectile hits to the right rigidbody for damage application.

### Our oracle's situation

Zero references to `RigidBodyIndexHolder` in `sf-headless-host/SFHeadlessHost.cs`, `sf-headless-host/SfMapTerrainHost.cs`, `sf-client-recon/SFClientRecon.cs`, `sf-client-recon/SfNsoClientPush.cs`, `sf-client-recon/SfMapTerrainClient.cs`, or `sf-client-recon/SfOracleLobbyConnect.cs`. Grep confirms — the component is completely off the oracle's radar.

The oracle's box-sync model is **position-based**: `SfNsoClientPush.RelayPushableCrateUpdates` reads `rb.position` every 200ms and sends it as msgType 26 (`ObjectUpdate`). Server applies the position via `comp.transform.position = pos`. Server then broadcasts 30Hz position snapshots back.

### Why this matters

The two sync models have fundamentally different runtime behavior:

| Property | Force-based (vanilla) | Position-based (oracle) |
|---|---|---|
| Sync data | byte index + Vector3 velocity + ForceMode | Vector3 position |
| Bandwidth | event-driven (only on collision) | 30Hz constant per active NSO |
| Local sim | each client runs full physics | client interpolates between snapshots |
| Push feel | tactile (force = instant local response) | laggy (must wait for snapshot round-trip) |
| Failure mode on packet loss | minor desync on one client | drift accumulates |
| Compatibility with `isKinematic=true` | breaks (force on kinematic does nothing) | works (position is just set) |

The oracle picked position-based to enable server-authoritative validation (cheaters can't apply unbounded forces). But the cost is:

- Loss of tactile push feel
- Cannot use kinematic flag on client (the smoother does, breaking Bug F)
- Server must constantly relay state even when crate is at rest (the 25s keepalive ALKA added is a workaround for this)

### Possible fix

Add a new v26 message: `PktPlayerCollideForce { byte playerSlot, byte rbIndex, Vector3 velocity, byte mode }`. Server validates (range check on velocity magnitude, plausibility of rbIndex) then relays. Each client applies `Rigidbody.AddForce(vector, ForceMode(mode))` to their local NSO. Same as vanilla but with server validation. Requires:

- Reading `RigidBodyIndexHolder.Index` for the colliding crate
- A client-side Harmony patch on `CollisionDamage` to intercept the `SendAddedForce` call and route it through our UDP socket instead of Steam P2P
- Server-side handler for the new message
- Removing the smoother flip on `isKinematic` (or making it conditional)

Not a small change, but architecturally cleanest path to "boxes feel like vanilla."

## 2. `IgnorePlayerWhenOffScreen` (layer 24 trick)

### What it is

A MonoBehaviour that moves a GameObject to layer **24** whenever `transform.position.y < -11f`. Layer 24 in SF is the "no player collision" layer.

```csharp
// refs/decompiled/Assembly-CSharp/IgnorePlayerWhenOffScreen.cs (full file)
private void Update()
{
    if (base.transform.position.y < -11f)
        base.gameObject.layer = 24;
    else
        base.gameObject.layer = layer;
}
```

Found on Crate(3) (the "effect crate" variant with audio/shake components). NOT on Crate2(6).

### Why this matters

The oracle's `WakeNsosNearGhostSweep` (SFHeadlessHost.cs:3737-3756) does a `Physics.OverlapSphere(mid, radius)` to find crates near a player rig sweep:

```csharp
var hits = Physics.OverlapSphere(mid, radius);
```

`Physics.OverlapSphere` respects layer masks. The default overload includes all layers, but if the crate has moved to layer 24, it's still in the overlap result (since no layer mask is specified). So this isn't directly broken — but the layer change DOES affect player-side collision response. A crate at `y < -11` has its player-collision disabled visually but the oracle still tries to wake/sync it.

More subtly: SF uses this to "drop crates out of the world" gracefully. If the server's position-sync resets a crate that vanilla would have let fall into the killbox, the layer flip may not match between server and client (oracle teleports it to y > -11, layer flips back; client lerps slowly through y = -11 with the layer toggling), producing visible collider-flicker.

The oracle's `TickNsoFallGuard` (SFHeadlessHost.cs:3775+) catches crates at y < -32 and teleports them back to spawn. This conflicts with the layer-24 mechanism for crates that should naturally fall: they get reset before they can quietly disappear.

### Possible fix

Make `TickNsoFallGuard` skip NSOs with `IgnorePlayerWhenOffScreen` — those crates have an intentional out-of-bounds-disabled behavior we shouldn't override. Or set the threshold lower (e.g., y < -50 instead of y < -32) so the natural layer-24 path completes first.

## 3. `LevelEditor.NetworkComponentTAG` (marker)

### What it is

```csharp
// refs/decompiled/Assembly-CSharp/LevelEditor/NetworkComponentTAG.cs (full file)
namespace LevelEditor;
public class NetworkComponentTAG : MonoBehaviour { }
```

An empty MonoBehaviour. Pure marker — no fields, no methods. Present on Crate(3) but NOT on Crate2(6).

### Why this matters

The presence/absence of this tag distinguishes **level-editor-placed** networkable objects from **prefab** networkable objects. Stock SF likely uses `GetComponent<NetworkComponentTAG>()` somewhere to branch behavior — workshop-map crates need slightly different init treatment than Landfall stock crates.

Worth searching the decompile for where the tag is read. Grep results:

```
refs/decompiled/Assembly-CSharp/LevelEditor/LevelObject.cs
refs/decompiled/Assembly-CSharp/LevelEditor/NetworkComponentTAG.cs (definition only)
```

Only one consumer: `LevelObject.cs`. That's the level-editor's object representation. Likely the tag is added at scene-export time and used to wire up network sync for workshop maps.

For our oracle: irrelevant in the short term (we don't run workshop maps), but worth knowing the existence of this tag system if we ever extend to workshop maps. The fuzzy weapon match logic in v0.3.4 (`OnGroundWeaponsInit_FuzzyPostfix`) might need to factor this in to identify map-editor weapons differently from stock spawns.

## Cross-cutting takeaway

Our oracle replaces three vanilla SF mechanisms (force-sync, layer-flip kill, level-editor tagging) with either nothing or a position-based equivalent. Of the three:

1. **Force-sync is the most consequential** — it's the actual "boxes feel right" mechanism in vanilla. Implementing this in our v26 protocol is the right long-term move and could be a Phase 6.10 addition.
2. **Layer-flip kill** is minor — just exclude `IgnorePlayerWhenOffScreen`-having NSOs from `TickNsoFallGuard`.
3. **Level-editor tag** is a forward-looking concern for workshop map support.

Each of these merits a TODO entry in [`NEXT_STEPS.md`](../../NEXT_STEPS.md) or a follow-up commit — none are blocking the current scope but all are real gaps vs. vanilla-quality reproduction.

## Methodology / how to repro

1. Launch vanilla SF (stock Assembly-CSharp + Goldberg + UnityExplorer, no SF plugins) — see [`SF_VANILLA_INSPECTION.md`](../SF_VANILLA_INSPECTION.md) for full setup.
2. Enter any map with crates (Desert5 used here).
3. In UE Object Explorer, search "Crate" → click an instance → inspect Components panel.
4. Compare component list against grep results in our `sf-headless-host/` and `sf-client-recon/` source.
5. Any vanilla component absent from our source = a missing mechanism worth documenting.

Crate2(6) had only `Transform, MeshFilter, BoxCollider, MeshRenderer, Rigidbody, NetworkSyncableObject, RigidBodyIndexHolder`. Crate(3) added `AudioSource, RandomPitch, SoundPan, ShakeOnImpact, IgnorePlayerWhenOffScreen, LevelEditor.NetworkComponentTAG`.
