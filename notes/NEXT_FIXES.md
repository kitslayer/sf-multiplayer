# Next fixes — prioritized

> **HISTORICAL (2026-05-23) — all five fixes below have since shipped or been superseded.** This was the recommended fix order right after the 2026-05-23 audit found five open P0 bugs. Status as of now (cross-check [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md) + [`../NEXT_STEPS.md`](../NEXT_STEPS.md), which are the source of truth):
> - **P0-15** (destruction guard) — shipped; the dynamic-NSO revert in `4affabc` also made it largely moot (kinematic NSOs don't fire NSO-on-NSO collisions).
> - **P0-11** (Y-aware destruction filter) — the Y-aware version forwarded chain stress-breaks and was **reverted** (`4affabc`); back to drop-all of server-originated 28/29/30. See P0-11b.
> - **P0-13** (keyframe to new endpoints) — shipped.
> - **P0-12** (quantize MapInfoSync Vector2 keys) — shipped.
> - **P0-14** (MapInfoSyncable in the snapshot) — shipped as **v26.5 (positions) + v26.6 (state)**. NOTE: the on-the-wire entry below was the *proposal*; the shipped format differs (see the corrected box in section 5).
>
> Keep this file for the reasoning/fix-shapes; don't treat it as a live TODO.

After the 2026-05-23 audit found five open P0 bugs, this is the recommended fix order. Each entry is small enough to ship in one session and either fully fixes the issue or makes meaningful progress.

The findings are documented in [`AUDIT_2026-05-23.md`](AUDIT_2026-05-23.md); root causes are listed in [`BUGS_BACKLOG.md`](BUGS_BACKLOG.md). This doc is the "what to do" companion to those "what's wrong" docs.

## 1. P0-15 — Tighten the client-side destruction guard (cheap, immediate win)

**Symptom**: ice randomly breaks during normal gameplay even when no player is near.

**Cause**: `DestructiblePiece.OnCollisionEnter` fires on the client when our snapshot lerp moves an adjacent box into the ice. `Collide()` sees `force.magnitude > forceThreshold` and broadcasts the destruction.

**Fix shape**: Harmony patch on `DestructiblePiece.OnCollisionEnter` (or `Collide`) in `SFClientRecon.cs`:

```csharp
// Pseudocode — prefix on Collide:
// Return false (skip) if:
//   - not network match (already handled by stock)
//   - the colliding rigidbody's root has a Controller that is NOT the local player
// The stock SF check at DestructiblePiece.cs:62 already does this for the player
// case but we need a wider net — any non-player-driven local collision should
// not trigger destruction-broadcast on a non-host client. Only the server's
// authoritative collision should drive destruction.
```

**Risk**: low. The patch only narrows what causes a destruction broadcast; it doesn't change what destruction events themselves do. Worst case is "ice doesn't break when it should," which is recoverable by another hit.

**Verification**: a 5-minute match with one player walking past ice repeatedly should produce 0 destructions if nothing actually hits the ice.

**Estimated effort**: ~30 lines of Harmony in SFClientRecon, test in one session.

---

## 2. P0-11 — Replace coarse destruction filter with killbox-Y guard

**Symptom**: a box that the oracle destroys (e.g., gets pushed off a platform near the killbox) sometimes appears intact on clients forever — a "ghost box."

**Cause**: outbound destruction filter at `SFHeadlessHost.cs:618-624` drops ALL server-originated destructions to avoid forwarding killbox-fall destructions. But it also drops legitimate server-side breaks.

**Fix shape**: replace the unconditional `msgType==28||29||30 → skip` with a position-aware check. The destruction body for msgType 30 is `[u16 idx, i16 forceX, i16 forceY, f32 multiplier]` — we can look up the NSO position by index from the oracle's own scene:

```csharp
// Pseudocode in SendBroadcastPrefix:
if (msgType == 28 || msgType == 29 || msgType == 30) {
    ushort idx = (ushort)(data[0] | (data[1] << 8));
    var nso = FindNsoByIndex(idx);  // walk active NSOs once or cache the index map
    if (nso != null && nso.transform.position.y < -30f) {
        skip = true;  // killbox fall → drop
    }
    // else: this is a legitimate destruction; forward it
}
```

**Risk**: low. Worst case is a one-frame race where the NSO position is being read while it's mid-fall; the position would be Y > -30 still and we'd forward the destruction (which the client would correctly apply since the NSO is still in-scene there).

**Verification**: push a box off the killbox → confirm both clients still see other intact boxes (no spurious removal). Then hit a box with high force on a regular platform → confirm both clients see it break.

**Estimated effort**: ~50 lines including the NSO-by-index cache, test in one session.

---

## 3. P0-13 — Send keyframe snapshot to new v26 endpoints

**Symptom**: a client connecting after a box has been pushed and stopped sees the box at its prefab spawn position, not its actual resting position.

**Cause**: `CollectActiveNsoSnapshot` only includes NSOs whose position recently changed (+1s keepalive). At-rest NSOs are absent from the snapshot stream. New clients have no way to learn their positions.

**Fix shape**: in `HandlePlayerInput`, track `_v26Endpoints` per slot; the first time we see input from a new endpoint, send a full-keyframe snapshot to that endpoint (every NSO regardless of position-delta) before resuming the regular delta stream.

**Risk**: very low. Worst case is one extra packet per client join, ~1-2 KB.

**Verification**: connect client A, push a box, let it settle. Then connect client B. Client B should see the box at its pushed position immediately.

**Estimated effort**: ~30 lines, one session.

---

## 4. P0-12 — Patch `MapInfoSyncableBase` Vector2 key precision

**Symptom**: on maps with `GhostPlatform`, some clients see platforms stuck "on" while others toggle correctly.

**Cause**: bit-exact `Vector2` dictionary lookup at `MultiplayerManager.cs:1147-1161`. World-position floats can differ by a few ULPs between server and client at Awake time.

**Fix shape**: two Harmony prefix patches on `MultiplayerManager.AddMapDataObject` and `OnMapDataRecieved` that round the Vector2 to 3 decimal places before dict access. Symmetric on both server (oracle) and client (SFClientRecon).

```csharp
static Vector2 QuantizeKey(Vector2 v) =>
    new Vector2(Mathf.Round(v.x * 1000f) / 1000f, Mathf.Round(v.y * 1000f) / 1000f);
```

**Risk**: low if symmetric. The only failure mode is two platforms with positions closer than 1mm in either Y or Z — vanishingly unlikely in Landfall maps.

**Verification**: add a temp log on lookup-failure messages from `MultiplayerManager.GetObjectToSync`. Pre-patch should fail occasionally; post-patch should never fail.

**Estimated effort**: ~80 lines including the symmetric patches, one session.

---

## 5. P0-14 — v26.5 snapshot includes MapInfoSyncable section (the BIG fix)

**Symptom**: moving platforms (`MoveAlongPathUsingForce`) and pressure pillars (`PillarHandler`) drift across clients within seconds.

**Cause**: stock SF only syncs abstract state (waypoint index, value); every client integrates physics locally with non-deterministic results. Detail in `AUDIT_2026-05-23.md` Finding 3a.

**Fix shape**: server-authoritative position broadcast. Extend v26 `WorldStateSnapshot` with a new section after the projectile section.

> **As shipped (corrected — see PROTOCOL.md for the canonical layout):** the entry is keyed by the object's `m_StartPos` **Vector2**, NOT `transform.GetInstanceID()` — Unity assigns instance IDs per-process so they never match across server and client. Two sections were added, not one:
> ```
> u16 mapSyncCount (LE)                 -- v26.5: positions
> for each MapInfoSyncableBase (20 bytes):
>   f32 startX (LE)   -- m_StartPos.x (the key; quantized 0.01 by P0-12)
>   f32 startY (LE)   -- m_StartPos.y
>   f32 posX (LE)
>   f32 posY (LE)
>   f32 posZ (LE)
> u16 mapStateCount (LE)                -- v26.6: GetData() payloads
> for each (9 + dataLen bytes):
>   f32 startX, f32 startY, u8 dataLen, dataLen bytes
> ```

The original proposal was a single 16-byte `instanceID`-keyed position section:

```
u16 mapSyncCount (LE)
for each MapInfoSyncableBase (16 bytes):
  u32 instanceID (transform.GetInstanceID())   -- did NOT survive cross-process; replaced by the Vector2 key above
  f32 posX (LE)
  f32 posY (LE)
  f32 posZ (LE)
```

On the client, a lerp dict applies the position to the matching object (looked up by the `m_StartPos` Vector2). `MapInfoSyncableBase.Update` is patched to skip local force/spring integration on non-host so the snapshot stream isn't fought.

**Risk**: medium. The integration on `MoveAlongPathUsingForce` and `PillarHandler` is what makes them feel right physically. Going kinematic means motion is server-driven; smoothness depends on snapshot rate (30Hz). Could feel laggy.

**Mitigation**: snapshot rate is already 30Hz; client-side smoothing is already in `SFClientRecon`. Apply same exponential lerp as for NSOs.

**Verification**: load a map with moving platforms (TODO: identify which Landfall maps have them — see open todo in audit). Watch the platform position on both clients during a 30s observation. Pre-fix: ≥1 unit drift. Post-fix: drift < 0.1 units.

**Estimated effort**: 200+ lines spanning server + client + protocol bump. Plan it as a two-session arc; one session for protocol + server emit, one for client apply + kinematic patch.

---

## Numbering recap

| Order | ID | Title | Severity | Effort |
|---|---|---|---|---|
| 1 | P0-15 | Tighten client destruction guard | medium freq | ~30 lines |
| 2 | P0-11 | Y-aware destruction filter | rare but high impact | ~50 lines |
| 3 | P0-13 | Keyframe snapshot for late join | low | ~30 lines |
| 4 | P0-12 | Quantize MapInfoSync Vector2 keys | medium | ~80 lines |
| 5 | P0-14 | v26.5 MapInfoSyncable position broadcast | high (on affected maps) | 200+ lines, 2 sessions |

After these five, the major remaining work is the in-flight Phase 6.18 client-side prediction replay loop (`NEXT_STEPS.md`) and the long-tail items (workshop maps at runtime, per-map weapon allow-lists, server-side projectile hit registration).
