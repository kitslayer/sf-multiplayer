using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFHeadlessHost
{
    public partial class Plugin
    {

        // === Phase 6.17 v0.1 — server-side projectile registry ===
        // When a client fires (Weapon.ActuallyShoot on their side), the
        // SFClientRecon plugin emits PktClientFireWeapon (41). We register
        // a virtual projectile, advance it each frame, expire after a
        // configured lifetime, and broadcast positions in the snapshot.
        // Hit registration is v0.2 — for now this is observability +
        // visual consistency (all clients see the same bullet trajectory).
        private class Projectile
        {
            public uint     Id;
            public byte     OwnerSlot;
            public byte     WeaponType;     // 0=generic, 1=pistol, … (TBD)
            public Vector3  Position;
            public Vector3  Velocity;
            public float    BornAt;
            public float    LifetimeSec;    // max time of flight before expire
            public bool     IsThrown;       // thrown weapon (not a bullet) — never wall-explodes
            public bool     ShadowOnly;     // detect + LOG a hit, but emit NO damage (safe rollout)
        }

        // PktClientFireWeapon body (v26.3 — client → server):
        //   u8  ownerSlot
        //   u8  weaponType  (passthrough byte; meaning is whatever the client sends)
        //   f32 originX     (world position of muzzle)
        //   f32 originY
        //   f32 originZ
        //   f32 dirX        (normalized direction)
        //   f32 dirY
        //   f32 dirZ
        //   f32 speed       (units/sec — 0 → use DefaultProjectileSpeed)
        // Total: 2 + 24 + 4 = 30 bytes.
        private void HandleClientFireWeapon(byte[] data, int off, int len, IPEndPoint from)
        {
            if (len < 30) return;
            byte ownerSlot  = data[off];
            byte weaponType = data[off + 1];
            float ox = BitConverter.ToSingle(data, off + 2);
            float oy = BitConverter.ToSingle(data, off + 6);
            float oz = BitConverter.ToSingle(data, off + 10);
            float dx = BitConverter.ToSingle(data, off + 14);
            float dy = BitConverter.ToSingle(data, off + 18);
            float dz = BitConverter.ToSingle(data, off + 22);
            float speed = BitConverter.ToSingle(data, off + 26);
            if (ownerSlot > 3) { Log.LogWarning($"[P6.17] Fire reject — bad slot {ownerSlot}"); return; }
            // H-P0-1 (type 41 leg) — same slot ↔ source-address binding as
            // PlayerInput: only the slot owner's address may fire for it.
            SfClient fireOwner = null;
            foreach (var kv in _sfClients)
            {
                if (kv.Value.Slot == ownerSlot) { fireOwner = kv.Value; break; }
            }
            if (fireOwner == null || (object)fireOwner.Addr == null || (object)from == null
                || !fireOwner.Addr.Address.Equals(from.Address))
            {
                Log.LogWarning($"[P6.17] Fire reject — slot {ownerSlot} from non-owner {from}");
                return;
            }
            if (speed <= 0f || float.IsNaN(speed) || float.IsInfinity(speed)) speed = DefaultProjectileSpeed;
            if (speed > MaxProjectileSpeed) speed = MaxProjectileSpeed;
            var dir = new Vector3(dx, dy, dz);
            if (dir.sqrMagnitude < 0.01f) { Log.LogWarning($"[P6.17] Fire reject — zero/NaN direction"); return; }
            dir.Normalize();
            var p = new Projectile
            {
                Id          = _nextProjId++,
                OwnerSlot   = ownerSlot,
                WeaponType  = weaponType,
                Position    = new Vector3(ox, oy, oz),
                Velocity    = dir * speed,
                BornAt      = Time.realtimeSinceStartup,
                LifetimeSec = DefaultProjectileLifetime,
                // v0.4.0 — bullet damage SHADOW by default, like throws. The
                // sphere hit test runs against RTT-lagged ghost rigs, so
                // client-side misses registered as server hits and the emitted
                // PlayerTookDamage rendered hit feedback WITHOUT the victim's
                // HP authority changing — "fake hits" (live report 2026-06-11,
                // 4 spurious emissions in one session). Stock client-side
                // damage remains authoritative; flip SFHEADLESS_BULLET_DAMAGE=1
                // to re-enable for hit-reg tuning sessions.
                ShadowOnly  = !BulletDamageEnabled,
            };
            _projectiles.Add(p);
            Log.LogInfo($"[P6.17] Fire registered: id={p.Id} slot={ownerSlot} w={weaponType} pos={p.Position} vel={p.Velocity.magnitude:0.0}u/s");
        }

        // Advance every live projectile each frame. Expires by age.
        // Hit registration is v0.2: when projectile passes within ~1u of a
        // player rig (excluding owner), emit a PktPlayerTookDamage.
        private void TickProjectiles()
        {
            if (_projectiles.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            float dt = Time.fixedDeltaTime;  // FixedUpdate: deterministic fixed-step (FPS-independent)
            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                var p = _projectiles[i];
                if (now - p.BornAt > p.LifetimeSec)
                {
                    // EXPLOSION PARITY (v0.4.1) — an explosive round expiring
                    // is its FUSE going off (lobbed grenade-launcher shots
                    // land, roll, then detonate). Previously only wall hits
                    // blasted server-side, so fuse detonations moved crates on
                    // every client's local sim but never on the authority.
                    if (!p.IsThrown && IsExplosiveWeaponType(p.WeaponType, p.Velocity.magnitude))
                        ApplyExplosiveBlastAt(p.Position, BlastRadius, BlastForce);
                    _projectiles.RemoveAt(i);
                    continue;
                }
                // Phase 6.17 v0.2 — server-side hit registration. Advance,
                // then test the new position against every active rig
                // (excluding the owner). Sphere-sphere ~1.2u radius — coarse
                // but matches the lazy hit-feel of stock SF reasonably well
                // and is much cheaper than per-bone raycast. Emit server-
                // authoritative PktPlayerTookDamage on hit; the relay
                // path validates + fans out to all clients.
                //
                // v0.3 — wall occlusion via Physics.Linecast from prev to
                // new position. If the line is intersected by a collider
                // whose root isn't a player rig (Controller component),
                // expire the projectile silently (bullet hit a wall). If
                // first hit IS a player, fall through to the sphere check
                // so the existing hit emit applies.
                Vector3 prev = p.Position;
                p.Position += p.Velocity * dt;
                if (TryProjectileWallHit(prev, p.Position, out var wallHit, out var wallCol))
                {
                    // Thrown weapons hit walls and stick/drop — they never explode.
                    if (!p.IsThrown && IsExplosiveWeaponType(p.WeaponType, p.Velocity.magnitude))
                        ApplyExplosiveBlastAt(wallHit, BlastRadius, BlastForce);
                    else
                        // Server-auth boxes (v0.4.0): non-explosive shots
                        // shove the crate they hit in the oracle sim — the
                        // only sim that counts now.
                        ApplyBulletCrateKick(wallCol, p.Velocity);
                    _projectiles.RemoveAt(i);
                    continue;
                }
                int hitSlot = TestProjectileHit(p, prev);
                if (hitSlot >= 0)
                {
                    if (p.ShadowOnly)
                        Log.LogInfo($"[{(p.IsThrown ? "throw-auth" : "bullet-auth")}] SHADOW HIT: id={p.Id} owner-slot={p.OwnerSlot} → would damage slot {hitSlot} (server swept-sphere; no damage emitted in shadow mode)");
                    else
                        EmitServerDamage(hitSlot, p.OwnerSlot, p.WeaponType, p.Velocity);
                    // EXPLOSION PARITY (v0.4.1) — explosive rounds detonate on
                    // player impact too (the blast moves nearby crates; damage
                    // handling above is independent and stays shadow-gated).
                    if (!p.IsThrown && IsExplosiveWeaponType(p.WeaponType, p.Velocity.magnitude))
                        ApplyExplosiveBlastAt(p.Position, BlastRadius, BlastForce);
                    _projectiles.RemoveAt(i);
                }
            }
        }

        // v0.3 — wall occlusion. Linecast from prev to new along the segment
        // the projectile traveled this tick. If the first hit collider
        // belongs to scene geometry (no Controller component in its root),
        // it's a wall — the bullet expires without damage. Player rigs are
        // intentionally excluded because TestProjectileHit handles them
        // (and SF's player rigs span many bones; a raycast might hit a
        // hand collider while the sphere check finds the torso).
        private static bool IsExplosiveWeaponType(byte weaponType, float speed)
        {
            return speed < 50f || weaponType == 5 || weaponType == 6 || weaponType == 7 || weaponType == 8;
        }
        private static float BlastEnvFloat(string name, float dflt)
        {
            var v = Environment.GetEnvironmentVariable(name);
            float f;
            if (!string.IsNullOrEmpty(v) && float.TryParse(v, out f) && f > 0f) return f;
            return dflt;
        }

        // P6.17 — server-side explosion physics. Applies AddExplosionForce to
        // nearby dynamic rigidbodies + calls DestructiblePiece.Collide on
        // destructibles in radius.
        //
        // Two bugs fixed 2026-05-24 (see notes/bug-investigations/2026-05-24_OPEN-3_chains_break_root_cause.md):
        //
        // 1. Chains/ice were being randomly destroyed by any nearby explosion.
        //    Vanilla SF chains have forceThreshold=0 (any non-zero force breaks
        //    them) and ice has forceThreshold=15 (this method's effective force
        //    of 15*10=150 trivially exceeds it). The blanket OverlapSphere +
        //    blanket Collide() invocation triggered destruction on every
        //    chain/ice in range. Vanilla bullets that DON'T break chains/ice
        //    are raycasts that hit specific targets — they don't blanket-blast.
        //    Filter added below to skip vanilla-fragile destructibles.
        //
        // 2. networkForce=true was being passed, which makes Collide bypass
        //    its network branch (SendDestructMessage). Destruction was applied
        //    locally on the server only, and the destruction event was never
        //    sent to clients — clients only saw the break via the subsequent
        //    NSO position-sync (ice falls below world, position lerps down).
        //    networkForce=false now lets vanilla broadcast the destruction
        //    event properly, so all clients see the same break at the same time.
        //
        // 3. Added LoS check via Physics.Linecast — explosions should not
        //    blast through walls.
        private void ApplyExplosiveBlastAt(Vector3 center, float radius, float blastForce)
        {
            try
            {
                var cols = Physics.OverlapSphere(center, radius);
                if (cols == null) return;
                var dpType = AccessTools.TypeByName("DestructiblePiece");
                var collideM = (object)dpType != null ? AccessTools.Method(dpType, "Collide") : null;
                var fThreshF = (object)dpType != null ? AccessTools.Field(dpType, "forceThreshold") : null;
                var simpleF  = (object)dpType != null ? AccessTools.Field(dpType, "simpleDestruction") : null;
                var eventF   = (object)dpType != null ? AccessTools.Field(dpType, "eventDestruction") : null;
                int affected = 0, skippedChain = 0, skippedIce = 0, skippedLoS = 0;
                foreach (var col in cols)
                {
                    if ((object)col == null) continue;

                    // EXPLOSION PARITY (v0.4.1) — mirror stock Explosion.Explode's
                    // non-player treatment: a MASS-SCALED impulse (clamp(mass/500,
                    // 0.01, 1) — a 500-mass vanilla crate takes the full force)
                    // plus a mass-independent VelocityChange kick, both with
                    // upwardsModifier=1 like stock. The old single un-scaled
                    // impulse with upwards=0.5 moved crates differently than
                    // every client's local sim.
                    var rb = col.attachedRigidbody;
                    if ((object)rb != null && !rb.isKinematic)
                    {
                        float massScale = Mathf.Clamp(rb.mass / 500f, 0.01f, 1f);
                        rb.AddExplosionForce(blastForce * massScale, center, radius, 1f, ForceMode.Impulse);
                        rb.AddExplosionForce(BlastVelocityChange, center, radius, 1f, ForceMode.VelocityChange);
                    }

                    // For destructibles: filter before calling Collide
                    if ((object)collideM == null || (object)dpType == null) continue;
                    var dp = col.GetComponent(dpType) ?? col.GetComponentInParent(dpType);
                    if ((object)dp == null) continue;

                    // Skip vanilla-fragile destructibles: chains (simpleDestruction with threshold≈0)
                    // and ice (both flags false). Vanilla bullets break these via direct raycast
                    // hit calls; blanket explosion damage is OUR bug, not vanilla behavior.
                    bool simple = (object)simpleF  != null && (bool)simpleF.GetValue(dp);
                    bool eventD = (object)eventF   != null && (bool)eventF.GetValue(dp);
                    float thresh = (object)fThreshF != null ? (float)fThreshF.GetValue(dp) : 0f;

                    if (simple && thresh < 0.01f) { skippedChain++; continue; }   // chains
                    if (!simple && !eventD)       { skippedIce++;   continue; }   // ice

                    // LoS check: don't blast through walls
                    Vector3 dpPos = ((Component)dp).transform.position;
                    if (Physics.Linecast(center, dpPos, out var hit) && hit.collider != col &&
                        (hit.transform.root != ((Component)dp).transform.root))
                    {
                        skippedLoS++; continue;
                    }

                    // Pass networkForce=false so SendDestructMessage broadcasts to all clients.
                    // Previously this was `true` which suppressed the network destruction event.
                    collideM.Invoke(dp, new object[] { Vector3.up * 15f, 10f, false });
                    affected++;
                }
                if (affected > 0 || skippedChain > 0 || skippedIce > 0 || skippedLoS > 0)
                    Log.LogInfo($"[P6.17] Explosion at {center} r={radius}: affected={affected} skipChain={skippedChain} skipIce={skippedIce} skipLoS={skippedLoS}");
            }
            catch (Exception e) { Log.LogWarning($"[P6.17 explosion] {e.Message}"); }
        }

        private bool TryProjectileWallHit(Vector3 from, Vector3 to, out Vector3 hitPoint, out Collider hitCollider)
        {
            hitPoint = to;
            hitCollider = null;
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.001f) return false;
            if (Physics.Linecast(from, to, out var hit))
            {
                if ((object)hit.collider == null) return false;
                hitPoint = hit.point;
                hitCollider = hit.collider;
                var root = hit.collider.transform.root;
                if ((object)root == null) return false;
                if (root.GetComponent("Controller") != null) return false;
                return true;
            }
            return false;
        }
        private void ApplyBulletCrateKick(Collider hitCol, Vector3 projVelocity)
        {
            try
            {
                if ((object)hitCol == null) return;
                var rb = hitCol.attachedRigidbody;
                if ((object)rb == null || rb.isKinematic) return;
                // Classify the NEAREST NSO ancestor of the hit body, not
                // transform.root — crates are children of the map root, so the
                // old root check answered "does this MAP contain any crate"
                // and kicked unrelated dynamic bodies (ice debris) too.
                EnsureNsoTypeCache();
                if ((object)_nsoType == null) return;
                var nsoComp = rb.GetComponentInParent(_nsoType) as Component;
                if ((object)nsoComp == null || !IsPushableCrateNso(nsoComp.gameObject)) return;
                var root = nsoComp.transform;
                Vector3 dir = projVelocity;
                dir.x = 0f;
                if (dir.sqrMagnitude < 0.0001f) return;
                dir.Normalize();
                rb.WakeUp();
                rb.velocity += dir * BulletCrateKick;
                _bulletCrateKicks++;
                if (_bulletCrateKicks == 1 || _bulletCrateKicks % 25 == 0)
                    Log.LogInfo($"[P6.17] Bullet crate-kick #{_bulletCrateKicks} on '{root.name}'");
            }
            catch (Exception e) { Log.LogWarning($"[P6.17 crate-kick] {e.Message}"); }
        }
        private int TestProjectileHit(Projectile p, Vector3 prevPos)
        {
            foreach (var kv in SlotToRig)
            {
                if (kv.Key == p.OwnerSlot) continue;
                var rig = kv.Value;
                if ((object)rig == null) continue;
                Vector3 rigPos = rig.transform.position;
                // Cheap end-point sphere check first.
                if ((rigPos - p.Position).sqrMagnitude <= ProjectileHitRadiusSq) return kv.Key;
                // Swept: closest point on segment prev→new to rigPos.
                Vector3 seg = p.Position - prevPos;
                float segLenSq = seg.sqrMagnitude;
                if (segLenSq < 0.0001f) continue;
                float t = Mathf.Clamp01(Vector3.Dot(rigPos - prevPos, seg) / segLenSq);
                Vector3 closest = prevPos + seg * t;
                if ((rigPos - closest).sqrMagnitude <= ProjectileHitRadiusSq) return kv.Key;
            }
            return -1;
        }

        // Build a PktPlayerTookDamage body and broadcast it as if it had come
        // from the victim's own client. Standard 25 damage + dmgType=0
        // tracks vanilla pistol behavior. weaponType byte is reserved for
        // when we differentiate pistol/sniper/etc.; logged but not used yet.
        //
        // v0.3 — particle direction included. Body format (from
        // NetworkPlayer.SyncClienthealth parser at line 649):
        //   byte attackerIdx          (1)
        //   f32  damage               (4)
        //   bool playParticles        (1)
        //   f32  particleDir.y        (4)  if playParticles
        //   f32  particleDir.z        (4)  if playParticles
        //   byte dmgType              (1)
        // Total = 15 bytes with particles. Client renders the spray
        // direction from particleDir; we use the projectile's velocity
        // direction so it sprays backward from the hit point.
        private void EmitServerDamage(int victimSlot, byte attackerSlot, byte weaponType, Vector3 projVelocity)
        {
            byte[] body = new byte[15];
            int off = 0;
            body[off++] = attackerSlot;
            byte[] dmgBytes = BitConverter.GetBytes(25.0f);
            Buffer.BlockCopy(dmgBytes, 0, body, off, 4); off += 4;
            body[off++] = 1;  // playParticles=true
            // particleDir.y / .z — the receiver uses Quaternion.LookRotation
            // on Vector3(0, y, z). Pointing along the projectile velocity
            // means the particle system orients along the hit direction.
            // Normalize so magnitude doesn't affect particle behavior.
            Vector3 dir = projVelocity.sqrMagnitude > 0.0001f ? projVelocity.normalized : Vector3.forward;
            byte[] yBytes = BitConverter.GetBytes(dir.y);
            byte[] zBytes = BitConverter.GetBytes(dir.z);
            Buffer.BlockCopy(yBytes, 0, body, off, 4); off += 4;
            Buffer.BlockCopy(zBytes, 0, body, off, 4); off += 4;
            body[off++] = 0;  // dmgType=0 generic
            byte channel = (byte)(victimSlot * 2 + 3);
            BroadcastSfPacket(PktPlayerTookDamage, body, 0uL, channel);
            Log.LogInfo($"[P6.17v3] Server hit: attacker={attackerSlot} victim={victimSlot} w={weaponType} dir=({dir.y:0.00},{dir.z:0.00}) → 25 dmg on chan={channel}");
        }

        // Phase 6.14.5 — tick-history ring buffer for lag-comp.
        // Records per-slot positions at each server tick so a future damage-
        // event handler can rewind to validate. We just RECORD here; the
        // VALIDATE step needs the damage packet to carry a tick reference,
        // which requires a patched-DLL extension (not yet shipped). Until
        // then the buffer feeds telemetry only — but having it built means
        // when we add `clientLastAckedServerTick` to the damage protocol,
        // validation is a 30-line addition. See notes/phase6/13-rewind-buffer.md.
        private class TickSample
        {
            public uint Tick;
            public Vector3[] Positions = new Vector3[4];
            public bool[]    Alive     = new bool[4];
        }

        private void RecordTickSample()
        {
            var s = new TickSample { Tick = _serverTick };
            foreach (var kv in SlotToRig)
            {
                if (kv.Key < 0 || kv.Key > 3) continue;
                var rig = kv.Value;
                if ((object)rig == null) continue;
                s.Positions[kv.Key] = rig.transform.position;
                s.Alive[kv.Key]     = true;
            }
            _tickHistory.Enqueue(s);
            while (_tickHistory.Count > MaxHistoryTicks) _tickHistory.Dequeue();
        }

        // Lookup positions at a given server tick. Returns null if tick is
        // outside the buffer window.
        private TickSample LookupTickSample(uint tick)
        {
            foreach (var s in _tickHistory) if (s.Tick == tick) return s;
            return null;
        }
    }
}
