# Phase 6.14.5 — Server-side rewind buffer (lag-comp hit registration)

**Status:** design only. Research note based on ALKA's `damage_authority.go` rewind pattern.

## Why we need it

Right now: client A shoots client B. A's bullet ProjectileCollision fires on A's local machine, A's client says "I hit B at time T." Server forwards the damage. B applies damage.

Problem: by the time the damage packet reaches the server (40-100ms RTT), B has *already moved* in B's local prediction. From B's perspective, the hit landed on where B *used to be*, not where B *is now*. This is the canonical "ghost hit" / "I shot around the corner and died" complaint.

Two fixes used in production FPS netcode:

1. **Predict-and-rollback on the shooter side** (Overwatch model). Heavy on the client — has to predict where targets WILL be.
2. **Rewind on the server side** (CSGO model). Server keeps a sliding window of every player's position at each tick. When a damage event arrives stamped "this happened at server tick N," server rewinds all targets to tick N, validates the hit raycast, then applies damage. Then "fast-forwards" everyone back to current.

We want #2 — matches our existing architecture (server already simulates players via SF's own Movement.cs in our spawned authoritative rigs).

## Architecture

### Server side

```csharp
private class TickSample {
    public uint Tick;
    public Vector3[] Positions = new Vector3[4];   // slot → world pos
    public Vector3[] Velocities = new Vector3[4];
    public float[] Healths = new float[4];
    public bool[] Alive = new bool[4];
}
private readonly Queue<TickSample> _tickHistory = new Queue<TickSample>(64);
private const int MaxHistoryTicks = 60;   // 1s at 60Hz
```

After each FixedUpdate that advances player rigs:
```csharp
private void RecordTickSample() {
    var s = new TickSample { Tick = _serverTick };
    foreach (var kv in SlotToRig) {
        var rig = kv.Value;
        s.Positions[kv.Key]  = rig.transform.position;
        s.Velocities[kv.Key] = rig.GetComponent<Rigidbody>().velocity;
        // ... health, alive flag
    }
    _tickHistory.Enqueue(s);
    while (_tickHistory.Count > MaxHistoryTicks) _tickHistory.Dequeue();
}
```

When a PktPlayerTookDamage arrives with the client's last-acked server tick:
```csharp
private bool ValidateDamage(byte attackerSlot, byte victimSlot, uint clientLastAckedTick, float reportedDamage) {
    // Rewind window: clamp to history bounds
    var sample = _tickHistory.FirstOrDefault(s => s.Tick == clientLastAckedTick);
    if (sample == null) {
        // Out-of-window — accept on faith or reject. ALKA defaults to current
        // position (no rewind). Reasonable for low-skill bracket.
        return true;
    }
    var apos = sample.Positions[attackerSlot];
    var vpos = sample.Positions[victimSlot];
    var dist = Vector3.Distance(apos, vpos);
    var maxReach = MaxReachForWeapon(attacker.Weapon);
    if (dist > maxReach * 1.25f) {
        Log.LogWarning($"[lag-comp] Damage rejected — dist {dist} > {maxReach*1.25f}");
        return false;
    }
    return true;
}
```

### Client side

Client needs to send its "last-acked server tick" in damage packets. Currently our PktPlayerInput body carries `u32 sequenceNum` (client's own sequence). For lag-comp we need the client to also include the server tick it last received in a snapshot. So `PktPlayerTookDamage` body grows:

```
existing fields...
u32 clientLastAckedServerTick (NEW — taken from latest WorldStateSnapshot tick the client received)
```

OR (cleaner): server uses the source-client's `LastInputSeq` to look up which server tick was current when that input was sent. Less wire-format churn.

## Edge cases

1. **Late-joiner** — has no history sampled before they joined. Damage from them in the first ~1s should fall back to "no rewind."
2. **Player just respawned** — was alive=false at the historical tick. ALKA's `victim.Health <= 0 → reject` handles this.
3. **Attacker died after firing but before server sees the damage** — ALKA's `attacker.Health <= 0 → reject` blocks this. Aggressive (a fair shot from someone who then died to lava in 50ms gets dropped) but conservative.
4. **Weapon-range bounds** — need a per-weapon max-reach table. ALKA maintains this in `damage_authority.go::maxReachForWeapon`; ~30 lines of switch cases.

## What we'd ship

```
sf-headless-host/SFHeadlessHost.cs
├── new TickSample class
├── _tickHistory ring buffer
├── RecordTickSample() called from per-frame Update
├── ValidateDamage() called from PktPlayerTookDamage handler
└── MaxReachForWeapon() — table mirroring ALKA's

sf-client-recon/SFClientRecon.cs
└── track _lastAckedServerTick on each WorldStateSnapshot received, send in input
```

Estimated effort: ~1 day of code + 1 day of tuning (the maxReach numbers are SF-specific magic constants we'd need to dial in by playing).

## Why we haven't shipped it yet

Two reasons:

1. **Premature.** Our player snapshots barely work end-to-end (Phase 6.10-6.12 just shipped this morning). Lag-comp on top of an unproven prediction system would compound failures. Get prediction smooth + reconciling first.

2. **Anti-cheat lite is more urgent.** Right now any client can send a `PktPlayerTookDamage` for any slot with damage=666.666 (the killing-blow marker) and the server forwards it. The rate-limiter we added (ALKA P0-1 observer) catches packet floods but not single malicious damage packets. Real fix: validate the slot ↔ source-client mapping, validate damage magnitude vs weapon, validate attacker is actually alive and holding the claimed weapon. Rewind is the next layer on top of that.

When prediction settles and is stable, this becomes the next big netcode win.
