# Doors (and other local-physics objects) desync — comp-blocking (2026-05-24)

> Status: **investigation complete with runtime evidence**, design sketch proposed, no code changes yet

Discovered during the 2026-05-24 vanilla SF Unity Explorer session, while probing Castle1's swinging doors via the SfBridge live-query plugin.

## Symptom

When player A swings a door open by walking into it, player B sees almost no motion on their screen. Door desyncs visually between host and client. Confirmed runtime data:

| Frame | Door 80476 HOST | Door 80476 CLIENT | Delta |
|---|---|---|---|
| T1 | y=-5.6 z=2.4 | y=-4.9 z=2.2 | **~0.7 units** |
| T2 | y=-5.3 z=2.3 | y=-5.0 z=2.2 | ~0.3 |
| T3 | y=-5.8 z=2.5 | y=-4.8 z=2.2 | **~1.0 unit** |

(The OTHER door in Castle1, id 80494, was NOT being pushed and stayed in sync — confirming the desync only manifests under collision-driven motion.)

## Why vanilla SF doesn't sync this

Doors in Stick Fight have:
- `Rigidbody`
- `ConfigurableJoint`
- `ConstantForce` (spring-back to closed position)
- `SetRigidbodySettings(maxAngular=50000)`
- `BoxCollider`
- Layer 22

But NOT:
- `NetworkSyncableObject` ← critical: no NSO means no automatic network sync
- `RigidBodyIndexHolder` ← no byte index for the existing `SendAddedForce` mechanism

There's no "Door" or "Hinge" MonoBehaviour class in SF's decompiled source (`refs/decompiled/Assembly-CSharp/`). Doors are pure Unity prefab constructs — placed by level designers in the Unity Editor, no custom code path. Stock SF never tries to network-sync them; each client simulates local door physics from local player collisions only. Casual play tolerates the resulting visual desync.

## Why this is a problem for comp

In a competitive match, door position affects:
- Sword reach: a "closed" door on side A may be "open" on side B → hits land differently
- Pathfinding: blocked vs unblocked traversal
- Hitbox occlusion: bullets pass through "open" door geometry on one side, hit it on the other

Vanilla SF's tolerance of this isn't acceptable when the player base is scrim-running tournaments.

## What makes this fixable

ConfigurableJoint + ConstantForce + identical static state = **deterministic spring physics**. Given the same input impulse, both clients' doors will swing through identical trajectories. We don't need continuous position sync; we need **collision-impulse sync** — a single packet per player→door collision event.

This is the same architectural pattern vanilla SF uses for player-pushable NSOs via `NetworkPlayer.SendAddedForce(byte rbIndex, Vector3 velocity, ForceMode mode)` (see [`2026-05-24_missing-vanilla-mechanisms.md`](2026-05-24_missing-vanilla-mechanisms.md)) — just applied to a category vanilla never bothered to sync.

## Design sketch — "DoorSync" plugin component

### Discovery + indexing

```csharp
// At scene-load + settle (additive scenes):
private static List<Rigidbody> _doors = new List<Rigidbody>();

void OnAdditiveSceneSettled(Scene scene) {
    _doors.Clear();
    foreach (var root in scene.GetRootGameObjects()) {
        foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true)) {
            if (rb.GetComponent<NetworkSyncableObject>() != null) continue; // skip real NSOs
            if (rb.GetComponent<ConfigurableJoint>() == null) continue;     // need a joint
            _doors.Add(rb);
        }
    }
    // Sort deterministically by initial world position. Both sides see identical
    // scene + identical sort key → identical index assignment.
    _doors.Sort((a, b) => {
        var pa = a.transform.position; var pb = b.transform.position;
        int c = pa.y.CompareTo(pb.y); if (c != 0) return c;
        c = pa.z.CompareTo(pb.z); if (c != 0) return c;
        return pa.x.CompareTo(pb.x);
    });
    // _doors[i] now has stable index i identical across hosts/clients
}
```

### Collision tracking

```csharp
// Harmony patch on Rigidbody.OnCollisionEnter via a wrapper MonoBehaviour
// attached to each door at scene-load. Or attach a small DoorImpulseEmitter
// MonoBehaviour to each door in the discovery pass:

class DoorImpulseEmitter : MonoBehaviour {
    public byte doorIndex;
    private Rigidbody rb;
    void Awake() { rb = GetComponent<Rigidbody>(); }
    void OnCollisionEnter(Collision col) {
        // Only emit if the colliding rig belongs to the local-controlled player
        var ctrl = col.transform.root.GetComponent<Controller>();
        if (ctrl == null || !ctrl.HasControl) return;
        // Compute impulse: collision.impulse is the velocity change applied
        Vector3 impulse = col.impulse;
        if (impulse.sqrMagnitude < 0.01f) return; // ignore tiny taps
        // Broadcast — server-authoritative variant: send to server, let server validate + relay
        OracleNet.SendDoorImpulse(ctrl.PlayerSlot, doorIndex, impulse);
    }
}
```

### Wire protocol

New v26 message: `PktDoorImpulse`

| Offset | Size | Field |
|---|---|---|
| 0 | 1 | byte playerSlot |
| 1 | 1 | byte doorIndex |
| 2 | 12 | Vector3 impulse (3× f32 LE) |

Total: 14 bytes/event. Event-driven (only on collision), so even worst-case spam = a few packets/sec.

### Server validation (oracle authority preserved)

```
On PktDoorImpulse from client:
  validate playerSlot matches connection
  validate doorIndex < known door count for current scene
  validate magnitude(impulse) within plausibility range (e.g. < 50 units/s velocity)
  relay to ALL clients (including sender so they don't apply impulse twice — actually
    the sender already applied locally on collision; relay with sender filter)
```

### Reception

```csharp
void OnPktDoorImpulse(byte playerSlot, byte doorIndex, Vector3 impulse) {
    if (doorIndex >= _doors.Count) return;
    var rb = _doors[doorIndex];
    if (rb == null) return;
    rb.AddForce(impulse, ForceMode.VelocityChange);
    // ConfigurableJoint + ConstantForce will integrate this impulse identically
    // on every client, producing the same swing motion.
}
```

## Generalization

This same approach works for any local-physics object that lacks NSO but matters for gameplay:

- Doors (Castle maps)
- Hanging chains (Castle, Ice maps)
- Dangling ropes (some Workshop maps)
- Spring-back levers (Castle maps)
- Decorative cloth / banners (lower priority, but cheap to add)

Discovery rule: scan for `Rigidbody + Joint + !NetworkSyncableObject`. Apply same impulse-sync mechanism to all of them.

## Bandwidth + perf

- Event-driven, not periodic: zero bandwidth at rest
- Per-collision: 14 bytes outbound per local player who touches a door
- Worst case (heavy door-fighting): ~10 collisions/sec/player × 14 bytes = ~140 B/s/player. Negligible vs the 30Hz NSO snapshot.
- No keepalive needed (door's resting state is `ConstantForce`-driven, deterministic)

## Implementation cost

- ~150-200 lines of C# (discovery + emitter + receive + protocol)
- One new v26 message type
- One Harmony patch (or just MonoBehaviour-attach) per door
- Discovery hook attaches to the same `OnAnySceneLoadedRunSettle` pipeline ALKA already has

Not blocking the v0.3.4 box-fix or any other in-progress work. Could be a Phase 6.x addition once the higher-priority fixes land.

## Risks

- **Door identification drift**: if Unity loads GameObjects in non-deterministic order across processes, the byte-index assignment could differ. Mitigation: sort by world position, which is deterministic per scene.
- **Collision filter accuracy**: if `Controller.HasControl` doesn't cleanly identify the local player (e.g., during a grab interaction), we might emit duplicate impulses. Add a per-frame debounce.
- **Joint physics drift**: ConfigurableJoint is generally deterministic for short timescales but accumulates floating-point error over long sequences. A periodic resync (e.g., position broadcast every 10s while moving) keeps things bounded.

## Open questions

- How many doors exist per map? Need a full survey. Quick UE probe across all 123 stock maps would answer this.
- Are there OTHER non-NSO physics objects we should sync too? (Probably yes — chains, dangling ropes, etc.)
- Does vanilla SF's collision detection use `OnCollisionEnter` or a custom raycast pipeline? If custom, we'd need a different hook point.

## Comp impact

Door desync has been an unspoken issue in SF comp for years (per user). Fixing this would be a tangible quality-of-life improvement that vanilla SF itself doesn't provide. It's also a clean architectural precedent for fixing any future "vanilla local-physics-only object that matters in comp" — the design generalizes.

If the box-physics fix (`SmoothTowardTargets` exclusion) lands first and proves the value of the runtime authority model, this door-sync work is the natural next step in delivering "vanilla-quality+ feel."
