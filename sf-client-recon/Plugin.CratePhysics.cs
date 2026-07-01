using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFClientRecon
{
    public partial class Plugin
    {
        // Reference type so SmoothTowardTargets can mutate RenderPos/Rot in place
        // while iterating the dictionary (no struct copy-back needed).
        private class PoseTarget
        {
            public Vector3 Pos;          // latest server position
            public Quaternion Rot;       // latest server rotation
            public Vector3 Vel;          // estimated velocity (u/s) from last two snapshots
            public Vector3 RenderPos;    // dead-reckoned position actually shown
            public Quaternion RenderRot;
            public float LastRecvAt;     // realtime the latest snapshot was applied
            public bool HasRender;
            // True when Rot was built from the v26.7 up-vector (full in-plane
            // orientation). False = RotZ-only legacy rotation — the reconciler
            // must NOT rotation-correct against it (a crate tipped about X has
            // eulerAngles.z ≈ 0, so RotZ-only would "un-tip" every fallen crate).
            public bool HasFullRot;
        }
        private void ClampCrateVelocities()
        {
            if (_nsoCache.Count == 0) return;
            float hSqrBullet = CrateMaxHoriz      * CrateMaxHoriz;
            float hSqrBlast  = CrateMaxHorizBlast * CrateMaxHorizBlast;
            int crates = 0, kin = 0, dyn = 0, push = 0; float maxAng = 0f, maxLin = 0f;
            foreach (var kv in _nsoCache)
            {
                var entry = kv.Value;
                if (entry == null || entry.SkipSmooth) continue;
                crates++;
                if (entry.Pushable) push++;
                var rbs = entry.Rbs;
                if (rbs == null) continue;
                for (int i = 0; i < rbs.Length; i++)
                {
                    var rb = rbs[i];
                    if (rb == null) continue;
                    if (rb.isKinematic) { kin++; continue; }
                    dyn++;
                    float av = rb.angularVelocity.magnitude; if (av > maxAng) maxAng = av;
                    var v = rb.velocity;
                    float lm = v.magnitude; if (lm > maxLin) maxLin = lm;
                    bool changed = false;
                    // Pick the horizontal cap based on whether there's a vertical
                    // component (explosion → high cap; pure horizontal → bullet cap).
                    bool isBlast = Mathf.Abs(v.y) > CrateVertTrigger;
                    float hSqr   = isBlast ? hSqrBlast : hSqrBullet;
                    float hCap   = isBlast ? CrateMaxHorizBlast : CrateMaxHoriz;
                    float hMagSqr = v.x * v.x + v.z * v.z;
                    if (hMagSqr > hSqr)
                    {
                        float s = hCap / Mathf.Sqrt(hMagSqr);
                        v.x *= s; v.z *= s; changed = true;
                    }
                    if (v.y > CrateMaxUp) { v.y = CrateMaxUp; changed = true; }
                    else if (v.y < -CrateMaxFall) { v.y = -CrateMaxFall; changed = true; }
                    if (changed) rb.velocity = v;

                    // SETTLE — kill residual creep so resting crates don't slide.
                    if (lm < CrateSettleLin && av < CrateSettleAng)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        continue;   // settled — skip the air-tumble nudge
                    }

                    // AIR TUMBLE — falling fast with little spin ⇒ add a gentle,
                    // deterministic tumble so it rotates as it falls. LEGACY MODE
                    // ONLY: in reconcile mode this torque exists on no other sim —
                    // the server's real tumble now arrives via the v26.7 up-vector,
                    // and injecting our own would just be orientation divergence.
                    if (!CrateReconcileActive
                        && v.y < -CrateAirTumbleSpeed && rb.angularVelocity.sqrMagnitude < CrateAirTumbleMaxAngSqr)
                    {
                        float sign = ((rb.GetInstanceID() & 1) == 0) ? 1f : -1f;
                        rb.AddTorque(new Vector3(sign * rb.mass * CrateAirTumbleTorque, 0f, 0f), ForceMode.Force);
                    }
                }
            }
            // DIAG: shows if crates are dynamic (local physics) and whether they
            // ever get angular velocity (rotate). If kin>0 and dyn==0 they're
            // server-kinematic-followed → no local rotation.
            if ((maxLin > 1f || maxAng > 0.5f) && Time.realtimeSinceStartup - _crateDiagAt2 > 1f)
            {
                _crateDiagAt2 = Time.realtimeSinceStartup;
                if (VerboseDiag) Log.LogInfo($"[crate-rot] crates={crates} pushable={push} dynRB={dyn} kinRB={kin} maxLin={maxLin:0.0} maxAng={maxAng:0.00}");
            }
        }

        // Detect a pushable crate (non-kinematic rigidbody) directly above this
        // one. Used by stack cohesion + to disable overhang assist on load-bearing
        // crates so a stack stays cohesive.
        private static bool HasCrateAbove(Rigidbody self, Bounds b)
        {
            Vector3 origin = new Vector3(b.center.x, b.max.y + 0.02f, b.center.z);
            RaycastHit hit;
            if (!Physics.Raycast(origin, Vector3.up, out hit, 0.4f)) return false;
            var rb = hit.rigidbody;
            return rb != null && rb != self && !rb.isKinematic;
        }

        // Detect a player Controller / NetworkPlayer touching this crate.
        // Uses cached reflected types so it's cheap (no per-frame TypeByName).
        private static bool IsPlayerTouching(Rigidbody self, Bounds b)
        {
            if ((object)_ctrlTypeClient == null) _ctrlTypeClient = AccessTools.TypeByName("Controller");
            if ((object)_npTypeClient == null)   _npTypeClient   = AccessTools.TypeByName("NetworkPlayer");
            if ((object)_ctrlTypeClient == null && (object)_npTypeClient == null) return false;
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * 0.12f, _contactBuf, Quaternion.identity);
            for (int i = 0; i < n; i++)
            {
                var c = _contactBuf[i];
                if ((object)c == null) continue;
                if (c.attachedRigidbody == self) continue;
                var root = c.transform.root;
                if ((object)_ctrlTypeClient != null && root.GetComponentInChildren(_ctrlTypeClient, true) != null) return true;
                if ((object)_npTypeClient   != null && root.GetComponentInChildren(_npTypeClient,   true) != null) return true;
            }
            return false;
        }

        // 4-corner support sample: returns the count of corners with ground/box below.
        // Returns 0..4. Also outputs the X-axis bias of support (positive = right is supported).
        private static int SampleBottomSupport(Bounds b, out float xBias)
        {
            xBias = 0f;
            float probeY    = b.center.y - b.extents.y + 0.04f;
            float probeDist = 0.22f;
            float xL = b.center.x - b.extents.x * 0.82f;
            float xR = b.center.x + b.extents.x * 0.82f;
            float zN = b.center.z - b.extents.z * 0.55f;
            float zF = b.center.z + b.extents.z * 0.55f;
            int hits = 0;
            if (Physics.Raycast(new Vector3(xL, probeY, zN), Vector3.down, probeDist)) { hits++; xBias -= 1f; }
            if (Physics.Raycast(new Vector3(xL, probeY, zF), Vector3.down, probeDist)) { hits++; xBias -= 1f; }
            if (Physics.Raycast(new Vector3(xR, probeY, zN), Vector3.down, probeDist)) { hits++; xBias += 1f; }
            if (Physics.Raycast(new Vector3(xR, probeY, zF), Vector3.down, probeDist)) { hits++; xBias += 1f; }
            return hits;
        }

        private void ApplyStackAndContactBehavior()
        {
            if (_nsoCache.Count == 0) return;
            float now    = Time.realtimeSinceStartup;
            float dt     = Time.fixedDeltaTime;
            foreach (var kv in _nsoCache)
            {
                var entry = kv.Value;
                if (entry == null || entry.SkipSmooth || !entry.Pushable) continue;
                var rb = entry.Rb;
                if ((object)rb == null || rb.isKinematic) continue;

                var comp = entry.Comp;
                if ((object)comp == null) continue;
                var col = comp.GetComponent<Collider>() ?? comp.GetComponentInChildren<Collider>();
                if ((object)col == null || col.isTrigger) continue;
                Bounds b;
                try { b = col.bounds; } catch { continue; }
                if (b.extents.x < 0.05f || b.extents.y < 0.05f) continue;

                int rid    = rb.GetInstanceID();
                Vector3 pos = rb.position;
                Vector3 vel = rb.velocity;

                // ─── Void rescue: cache the last "safe" position above the killbox.
                // If the crate dips below CrateVoidRescueY but had a recent safe pos
                // well above the line, snap it back instead of letting it fall forever.
                if (pos.y > CrateVoidRescueMinSafeY)
                {
                    _crateSafePos[rid] = pos;
                    _crateSafeAt[rid]  = now;
                }
                else if (pos.y < CrateVoidRescueY
                    && _crateSafePos.TryGetValue(rid, out var safe)
                    && safe.y > CrateVoidRescueMinSafeY)
                {
                    rb.position        = safe;
                    rb.velocity        = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.WakeUp();
                    _crateFallStartedAt.Remove(rid);
                    continue;
                }

                // ─── Contact + stack probes
                int supports     = SampleBottomSupport(b, out float xBias);
                bool fullyAir    = supports == 0;
                bool fullyOnTop  = supports == 4;
                bool loadAbove   = HasCrateAbove(rb, b);
                bool playerHere  = IsPlayerTouching(rb, b);
                float speedSqr   = vel.sqrMagnitude;
                bool settled     = speedSqr < StackSettledSpeed * StackSettledSpeed;

                // ─── (A) STACK COHESION (settled stacks): damp twist so the column
                //     stays vertical at rest. Disabled while moving fast → vertical
                //     hits / shoves can disassemble the stack like vanilla.
                if (loadAbove && settled && rb.angularVelocity.sqrMagnitude > 0.0001f)
                    rb.angularVelocity *= StackAngularDamp;

                // ─── (B) PLAYER SHOVE DAMPING — two-stage so a player walk-into
                //     can't ever fling the crate: first a HARD cap on horizontal
                //     velocity (so a fresh impulse can't exceed PlayerImpartCapH),
                //     then exponential damping so existing horizontal motion
                //     bleeds off fast. Skips during blasts/falls (|v.y| > 2) so
                //     explosions / drops are unaffected.
                if (playerHere && Mathf.Abs(vel.y) < 2.0f)
                {
                    Vector3 v2 = vel;
                    float hSq = v2.x * v2.x + v2.z * v2.z;
                    if (hSq > PlayerImpartCapH * PlayerImpartCapH)
                    {
                        float s = PlayerImpartCapH / Mathf.Sqrt(hSq);
                        v2.x *= s; v2.z *= s;
                        rb.velocity = v2; vel = v2;
                        hSq = v2.x * v2.x + v2.z * v2.z;
                    }
                    if (hSq > 0.02f)
                    {
                        v2.x *= PlayerHorizDamp; v2.z *= PlayerHorizDamp;
                        rb.velocity = v2; vel = v2;
                    }
                    if (rb.angularVelocity.sqrMagnitude > 0.04f)
                        rb.angularVelocity *= PlayerAngularDamp;
                }

                // ─── (C) STACK POP — if THIS crate is rising (hit from below) and has
                //     load above, scatter the upper crate with an upward + lateral
                //     impulse so the column visibly breaks apart instead of welding.
                if (loadAbove && vel.y > 1.2f)
                {
                    var aboveRb = FindCrateRigidbodyAbove(rb);
                    if ((object)aboveRb != null && !aboveRb.isKinematic)
                    {
                        Vector3 imp = Vector3.up * rb.mass * StackPopUpwardImpulse;
                        // Lateral jitter based on a hash of the rigid id → deterministic but varied
                        float jx = ((rid * 73856093) % 1000) / 500f - 1f;       // -1..+1
                        imp += new Vector3(jx, 0, 0) * rb.mass * StackPopLateralImpulse;
                        aboveRb.AddForce(imp, ForceMode.Impulse);
                        // Also kick a small Z torque to start the tumble
                        aboveRb.AddTorque(new Vector3(0, 0, -jx * rb.mass * 0.25f), ForceMode.Impulse);
                    }
                }

                // ─── (D) FALL TUMBLE — once airborne and falling fast, ensure the
                //     crate isn't perfectly rigid: give it a tiny, mass-scaled tumble
                //     torque so it spins as it descends (vanilla feel).
                if (fullyAir && vel.y < -FallTumbleStartSpeed && rb.angularVelocity.sqrMagnitude < 4f)
                {
                    if (!_crateFallStartedAt.ContainsKey(rid)) _crateFallStartedAt[rid] = now;
                    float jh = ((rid * 19349663) % 1000) / 500f - 1f;
                    rb.AddTorque(new Vector3(0, 0, jh * rb.mass * FallTumbleTorque), ForceMode.Force);
                }
                else if (!fullyAir)
                {
                    _crateFallStartedAt.Remove(rid);
                }

                // ─── (E) EDGE TIP — strong gravity torque about the support edge
                //     when only partial bottom support exists. Skipped when stack-
                //     supported (loadAbove + settled) so towers don't self-collapse.
                if (loadAbove && settled) continue;
                if (fullyOnTop || fullyAir) continue;   // need partial support

                Vector3 tipDir = xBias > 0f ? Vector3.right : Vector3.left;
                float torqueScale = speedSqr > 0.5f ? OverhangFallTorqueMul : OverhangTorqueMul;
                Vector3 torque = Vector3.Cross(tipDir, Physics.gravity) * rb.mass * torqueScale;
                rb.AddTorque(torque, ForceMode.Force);
            }
        }

        private static Rigidbody FindCrateRigidbodyAbove(Rigidbody self)
        {
            if ((object)self == null) return null;
            var col = self.GetComponent<Collider>() ?? self.GetComponentInChildren<Collider>();
            if ((object)col == null) return null;
            Bounds b;
            try { b = col.bounds; } catch { return null; }
            Vector3 origin = new Vector3(b.center.x, b.max.y + 0.02f, b.center.z);
            RaycastHit hit;
            if (!Physics.Raycast(origin, Vector3.up, out hit, 0.45f)) return null;
            return hit.rigidbody;
        }
    }
}
