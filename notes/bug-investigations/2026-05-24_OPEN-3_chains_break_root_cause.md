# OPEN-3 chain random-break — root cause hypothesis (2026-05-24)

> Status: **strong root cause hypothesis from decompile + runtime data**, no code changes yet, requires oracle-session repro to confirm

Comp-reported bug: chains in Castle/Ice maps randomly break in oracle play even when nobody shot them. Vanilla SF doesn't have this problem.

## What we learned at runtime

Live Unity Explorer probe of Castle10's `Castle_Chain1` (id 97094):

```
[DestructiblePiece]
  forceThreshold = 0         ← any non-zero force breaks the chain
  simpleDestruction = True
  eventDestruction = False
  mInited = False            ← still uninited even after scene-load complete
  mAssigned = True
  m_AllowForceFromClient = True
  m_OnlyRecieveInitState = True
  mDontSyncPos = True
[RigidBodyIndexHolder]
  mIndex = 0
  mInited = True             ← RBIH IS inited
```

## The decompile evidence

Stock `DestructiblePiece.cs` break-check, line 130:

```csharp
private void OnCollisionEnter(Collision collision)
{
    float multiplier = 1f;
    Rigidbody rigidbody = collision.rigidbody;
    // ...
    Vector3 force = collision.relativeVelocity * (rigidbody != null ? rigidbody.mass : 1f);
    if ((simpleDestruction || eventDestruction) && force.magnitude * multiplier > forceThreshold)
    {
        // → DESTROY
    }
}
```

With `forceThreshold = 0`, **any** non-zero collision force breaks the chain.

User-confirmed gameplay context: **chains only break from bullets in vanilla; they have no player-collision** (chains and players live on different physics layers, so player rigs literally pass through chains without firing `OnCollisionEnter`).

## How vanilla SF avoids spurious chain breaks

Vanilla bullets are **raycasts**, not Rigidbody projectiles:

- `refs/decompiled/Assembly-CSharp/RayCastForward.cs` (the stock SF bullet handler) uses `Physics.Raycast` + manual damage application, NOT a flying Rigidbody.
- Raycast hits do NOT trigger `OnCollisionEnter` on the target.
- Chain destruction from bullets happens via a separate code path that calls `DestructiblePiece.Collide(direction, multiplier, fromBullet)` directly with the bullet's known direction + a defined multiplier.
- Because the call site is explicit + controlled, vanilla never triggers spurious chain breaks.

The system works because:
- Players can't physically collide with chains (layer matrix excludes Player layer from chain's layer)
- Bullets are raycasts (don't trigger OnCollisionEnter)
- Therefore the ONLY way OnCollisionEnter fires on a chain is via another Rigidbody (e.g., a thrown crate hitting it, or a chain piece falling into another) — and those are intentional gameplay events

## Why our oracle breaks this

Two architecturally-distinct paths our oracle introduces:

### Hypothesis A: oracle's server-side projectile sim creates Rigidbody bullets

The v26 protocol's server-authoritative hit registration (Phase 6.17) maintains a list of in-flight projectiles. The server simulates them as positional integrations. If any client-side code (in `SFClientRecon` or in oracle-side patches that affect the local SF instance) converts these into actual Rigidbody projectiles, those WILL physically collide with chains and trigger `OnCollisionEnter` with non-zero force. With `forceThreshold=0`, the chain breaks.

Vanilla bullets are raycasts and never trigger this path. Our oracle's bullets, if they're Rigidbody-based on the client side, will.

### Hypothesis B: `ApplyExplosiveBlastAt` force-breaks chains in radius

ALKA's v0.3.4 explosion handling at `SFHeadlessHost.cs:4774-4795`:

```csharp
private void ApplyExplosiveBlastAt(Vector3 center, float radius, float blastForce)
{
    var cols = Physics.OverlapSphere(center, radius);  // radius=5f
    var dpType = AccessTools.TypeByName("DestructiblePiece");
    var collideM = (object)dpType != null ? AccessTools.Method(dpType, "Collide") : null;
    foreach (var col in cols)
    {
        // ...
        if ((object)collideM != null && (object)dpType != null)
        {
            var dp = col.GetComponent(dpType) ?? col.GetComponentInParent(dpType);
            if ((object)dp != null)
            {
                collideM.Invoke(dp, new object[] { Vector3.up * 15f, 10f, true });
                //                                ↑ force          ↑ multiplier
            }
        }
    }
}
```

This calls `DestructiblePiece.Collide(Vector3.up * 15f, 10f, true)` on EVERY DestructiblePiece within 5 units of every explosion. With `forceThreshold=0`, every nearby chain dies.

Vanilla explosions damage destructibles via specific gameplay rules (e.g., grenade detonation checks LoS, applies damage to a curated list). Our oracle does a blanket OverlapSphere that hits everything within 5u, including chains that vanilla would have skipped.

This is the more likely culprit — it's a broader-scope mechanism that's easier to verify.

### Hypothesis C: snapshot apply jostles chain rigidbodies

If our 30Hz snapshot apply does `rb.position = snap.pos` on a chain segment (because we sync everything with an NSO component, and DestructiblePiece extends NSO), the teleport could cause adjacent chain links' joints to register a force pulse. With `forceThreshold=0`, BREAK.

But: chains have `mDontSyncPos = True`. Vanilla's NSO sync skips position broadcast for these. If our oracle correctly respects `mDontSyncPos`, this hypothesis is mooted. If we ignore the flag and broadcast positions anyway, it fires.

Check via `CollectActiveNsoSnapshot` (SFHeadlessHost.cs:4704) — does it filter on `mDontSyncPos`? Without filtering, we're position-syncing chains that vanilla never did.

## Validation runtime data

Live break test on Castle10 (this session):
- User shot chains intentionally → broke as expected
- Both sides agreed about which segments broke (NSO destruction event propagated correctly)
- Falling pieces had ~1.7-1.8 unit Y-axis desync (acceptable — they're falling off-screen anyway)
- Intact chains stayed in lockstep (deterministic local physics works)

So the **destruction-event propagation works fine**. The bug isn't in destruction transmission — it's in **destruction triggering**. We need to stop firing spurious destruction events.

## Fix priorities

### Fix 1 (highest leverage, lowest effort): Filter `ApplyExplosiveBlastAt`'s Collide call

Add LoS check + collider-tag filter before invoking `DestructiblePiece.Collide`:

```csharp
foreach (var col in cols)
{
    var dp = col.GetComponent(dpType) ?? col.GetComponentInParent(dpType);
    if (dp == null) continue;

    // ONLY break destructibles vanilla would have: crates and weapon-targeted destructibles
    // SKIP: chains (any with forceThreshold==0 AND simpleDestruction AND on chain layer)
    var fThresh = (float)AccessTools.Field(dpType, "forceThreshold").GetValue(dp);
    if (fThresh < 0.01f) continue;  // vanilla-fragile, never blast-destroyable

    // ...existing Collide call...
}
```

This skips chains while still letting crates/breakable-pillars take explosion damage.

### Fix 2: Verify projectile model doesn't physically collide

Audit `SFClientRecon` for any Rigidbody-projectile spawning. If found, convert to raycast-based hit detection or set the projectile's layer to one that excludes destructible layers (use vanilla's bullet layer if available).

### Fix 3: Filter snapshot apply against `mDontSyncPos`

In `SFHeadlessHost.CollectActiveNsoSnapshot` and `SFHeadlessHost.ApplyClientObjectUpdate`, check the NSO's `mDontSyncPos` flag and skip position sync for those.

```csharp
foreach (var nso in all) {
    // skip if vanilla doesn't sync this NSO's position
    var dontSyncF = nsoType.GetField("mDontSyncPos", ALL_INST);
    if ((object)dontSyncF != null && (bool)dontSyncF.GetValue(nso)) continue;
    // ...rest of collection logic
}
```

### Verify after fixing

Reproduce in oracle play: leave a Castle10 match running idle for ~60 seconds. With fixes in place, chains should remain intact unless explicitly shot. Without fixes (current state), some chains likely break during the idle window.

## Related to OPEN-2 (ice randomly breaks)

Ice destructibles use the same `DestructiblePiece` mechanism. Likely the same three hypotheses apply. Fix 1 + Fix 3 should improve both OPEN-2 and OPEN-3 simultaneously.

The difference between chains (Castle10 visible here) and ice (Ice maps not yet probed): chains have `eventDestruction=False, simpleDestruction=True`. Ice has `eventDestruction=True` for chain-style group destruction. ALKA's `IsChainStyleDestructibleRoot` filter in `ShouldSkipServerOriginatedDestruction` already accounts for this distinction. The hypotheses above apply to both.

## Open questions

- Does the oracle's projectile sim actually instantiate Rigidbody bullets, or just simulate positions? Need to read `SFHeadlessHost.cs` projectile-related sections.
- Does ApplyExplosiveBlastAt fire on the SERVER only, or also re-fire on each client receiving the explosion broadcast? If both, that's a 2x amplification of the chain-break problem.
- Why is `mInited = False` on this chain segment despite scene-load completing? Doesn't seem to gate destruction (chains still break), but suggests an init pipeline that didn't finish for these NSOs.
- Crates also have DestructiblePiece — do they also have `forceThreshold=0`? If yes, crates would also break on any spurious force. Worth probing.

## Where to verify next

When connected to the oracle (not vanilla solo):
1. Land in Castle10
2. Stand back 10+ units from any chain
3. Throw a grenade — does the closest chain break even though you didn't aim at it?
4. If yes → Hypothesis B confirmed (explosion blast)
5. If no → check Hypothesis A or C

Single 5-minute repro test would identify which fix matters most.
