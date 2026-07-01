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

        private struct SnapshotEntry
        {
            public int Slot;
            public float X, Y, Z;
            public uint LastInputSeq;  // v26.2 — server's last-acked input seq for this slot
        }

        // P0-14 v26.5 — MapInfoSyncableBase position entry.
        // Identified by its m_StartPos Vector2 (stock SF's stable cross-
        // machine key for MapInfoSync). 20 bytes wire size.
        private struct MapSyncSnapEntry
        {
            public float StartX, StartY;
            public float X, Y, Z;
        }

        private struct NsoSnapEntry
        {
            public ushort Id;
            public float X, Y, Z, RotZ;
            // v26.7 — NSO up-vector (stock SF's rotation representation; tipping
            // is about world X). NaN when the host didn't send the section.
            public float UpY, UpZ;
        }
        private static bool IsMatchActive()
        {
            if (!_gmFxLooked)
            {
                _gmFxLooked = true;
                _gmTypeFx = AccessTools.TypeByName("GameManager");
                if ((object)_gmTypeFx != null)
                    _gmInFightFx = AccessTools.Field(_gmTypeFx, "inFight");
            }
            if ((object)_gmInFightFx == null) return false;   // can't tell → safest is OFF
            try { return (bool)_gmInFightFx.GetValue(null); } catch { return false; }
        }

        private void SmoothTowardTargets()
        {
            if (_playerTargets.Count == 0 && _nsoTargets.Count == 0 && _mapSyncTargets.Count == 0) return;
            float playerT = 1f - Mathf.Exp(-SmoothRate * Time.deltaTime);
            float nsoT = 1f - Mathf.Exp(-_nsoSmoothRate * Time.deltaTime);
            try
            {
                // Players: smooth every slot that has a target recorded.
                // _playerTargets is filtered at ApplySnapshot time per the
                // SFCLIENTRECON_SMOOTH_REMOTE env var — so iterating all here
                // is correct whether the user enabled remote-player smoothing
                // or not.
                if (_playerTargets.Count > 0)
                {
                    var npType = AccessTools.TypeByName("NetworkPlayer");
                    if ((object)npType != null)
                    {
                        var nps = UnityEngine.Object.FindObjectsOfType(npType);
                        if (nps != null)
                        {
                            foreach (var np in nps)
                            {
                                if (!TryGetPlayerSlotFromNetworkPlayer(np, out var pi)) continue;
                                if (!_playerTargets.TryGetValue(pi, out var target)) continue;
                                var npComp = np as Component;
                                var rb = npComp.GetComponent<Rigidbody>() ?? npComp.GetComponentInChildren<Rigidbody>();
                                if ((object)rb != null) rb.position = Vector3.Lerp(rb.position, target, playerT);
                                else npComp.transform.position = Vector3.Lerp(npComp.transform.position, target, playerT);
                            }
                        }
                    }
                }

                // NSOs: smooth all entries in the target dict against cached refs.
                if (_nsoTargets.Count > 0 && _nsoCache.Count > 0)
                {
                    foreach (var kv in _nsoTargets)
                    {
                        // Unity-aware null checks (NO (object) cast): a crate/NSO
                        // destroyed on a round change leaves a stale ref in the
                        // cache. `(object)comp == null` is FALSE for a destroyed
                        // Unity object, so the old guard let it through and every
                        // subsequent member access threw — flooding [smooth] and
                        // aborting ALL smoothing each frame ("anda raro"). The
                        // Unity == overload detects destroyed objects.
                        if (!_nsoCache.TryGetValue(kv.Key, out var entry) || entry == null) continue;
                        if (entry.Comp == null) continue;
                        if (entry.SkipSmooth) continue;   // weapon / ice / chain
                        var comp = entry.Comp;
                        var rb = entry.Rb;
                        if (rb == null && comp == null) continue;
                        var pose = kv.Value;
                        if (pose == null || !pose.HasRender) continue;

                        // ===== OMEGA FIX — pushable crates: LOCAL PHYSICS + soft sync =====
                        // Keep crates DYNAMIC so local Unity physics drives them at
                        // full render framerate: instant, smooth, collides with the
                        // player + other crates, stacks/balances naturally (this is
                        // the "no lag" feel). The server stays authoritative via a
                        // GENTLE continuous correction toward its position — small
                        // enough not to fight the local sim, strong enough that the
                        // box can never drift far. Only a LARGE desync triggers a
                        // clean hard-snap, and we zero velocity on snap so the body
                        // can't explode/fly. No kinematic flips, no relay → nothing
                        // to tug-of-war with.
                        if (entry.Pushable && (object)rb != null)
                        {
                            // Pushable crates are NOT render-lerped here. They run
                            // dynamic local physics (instant push feel) and, in the
                            // default v0.6.0 mode, converge to the oracle's
                            // authoritative pose via ReconcilePushableCrates in
                            // FixedUpdate — velocity-steered with a deadband, not
                            // position-written, because every position-write scheme
                            // tried in May 2026 injected penetration/phantom motion
                            // (crates exploded, slid, drifted). In legacy mode
                            // (SF_CRATES_LOCAL_PHYSICS=1) they take no server input
                            // at all and the 5Hz relay reports them upward instead.
                            continue;
                        }

                        // ===== Non-pushable NSOs: kinematic + velocity extrapolation =====
                        // (ice debris, moving platforms, etc.) Driven purely by
                        // server snapshots; extrapolate between sparse updates and
                        // chase with an exponential render-lerp.
                        float age = Time.realtimeSinceStartup - pose.LastRecvAt;
                        if (age < 0f) age = 0f;
                        if (age > NsoMaxExtrapSec) age = NsoMaxExtrapSec;
                        Vector3 extrap = pose.Pos + pose.Vel * age;

                        float dist = Vector3.Distance(pose.RenderPos, extrap);
                        if (dist > NsoSnapDistance * 6f)
                        {
                            pose.RenderPos = extrap;
                            pose.RenderRot = pose.Rot;
                        }
                        else
                        {
                            pose.RenderPos = Vector3.Lerp(pose.RenderPos, extrap, nsoT);
                            pose.RenderRot = Quaternion.Slerp(pose.RenderRot, pose.Rot, nsoT);
                        }

                        if ((object)rb != null)
                        {
                            if (!rb.isKinematic) rb.isKinematic = true;   // P0-5 / ice regression guard
                            rb.position = pose.RenderPos;
                            rb.rotation = pose.RenderRot;
                        }
                        else
                        {
                            comp.transform.position = pose.RenderPos;
                            comp.transform.rotation = pose.RenderRot;
                        }
                        // P0-15 — moved to ApplyNsoSnapshot below; only mark
                        // recentLerpAt when a snapshot delivers a LARGE
                        // position delta (the actual "teleport-into-ice"
                        // case). Marking every frame here suppressed
                        // legitimate kick-into-ice destructions too.
                    }
                }

                // P0-14 — smooth MapInfoSyncableBase positions toward server
                // targets. Same exponential lerp as NSOs. Rigidbodies for
                // these were already made kinematic at first sight, so
                // setting transform.position here doesn't fight physics.
                if (_mapSyncTargets.Count > 0 && _mapSyncCache.Count > 0)
                {
                    foreach (var kv in _mapSyncTargets)
                    {
                        // Unity-aware null check (destroyed map objects between rounds).
                        if (!_mapSyncCache.TryGetValue(kv.Key, out var comp) || comp == null) continue;
                        var rb = comp.GetComponent<Rigidbody>();
                        if (rb != null) rb.position = Vector3.Lerp(rb.position, kv.Value, nsoT);
                        else comp.transform.position = Vector3.Lerp(comp.transform.position, kv.Value, nsoT);
                    }
                }
            }
            catch (Exception ex)
            {
                // Throttle: a per-frame throw here used to flood the log (which
                // itself tanks FPS) and gave no type. Log type+message once/sec.
                if (Time.realtimeSinceStartup - _lastSmoothErrAt > 1f)
                {
                    _lastSmoothErrAt = Time.realtimeSinceStartup;
                    Log.LogWarning($"[P6.11.2 smooth] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Phase 6.14 — apply server-authoritative NSO positions (boxes,
        // chains, ice debris). For now: snap (no smoothing). 6.14.1 will
        // lerp between snapshots since broadcast rate is 30Hz but client
        // Update is 60-144Hz.
        //
        // Maintain a cached id → NetworkSyncableObject map. Rebuild on miss
        // since scene changes invalidate entries.
        // Cache entry precomputes the per-NSO classification + rigidbody ONCE at
        // rebuild time. Previously SmoothTowardTargets/ApplyNsoSnapshot called
        // IsChainStyle/IsIceOnly/IsWeapon (each a GetComponentsInChildren) plus
        // GetComponent<Rigidbody> for every box every frame — on box-heavy maps
        // that was thousands of hierarchy walks/sec → frame drops scaling with
        // crate count. Now it's a single dictionary lookup per frame.
        private class NsoCacheEntry { public Component Comp; public Rigidbody Rb; public Rigidbody[] Rbs; public bool SkipSmooth; public bool Pushable; public bool Floating; }

        private void ApplyNsoSnapshot(List<NsoSnapEntry> snap, uint tick)
        {
            if (snap.Count == 0) return;
            // Map transition — both scenes briefly coexist with colliding NSO
            // ids; snapshots in flight may still describe the OLD map. Don't
            // record targets (or rebuild the cache) until the window passes.
            if (Time.realtimeSinceStartup < _reconSuppressUntil) return;
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                // Rebuild cache every 60 ticks (~2s at 30Hz) or on first run.
                if (_nsoCache.Count == 0 || _nsoCacheRebuildAt <= 0)
                {
                    _nsoCache.Clear();
                    var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                    if (all != null)
                    {
                        foreach (var nso in all)
                        {
                            var c = nso as Component;
                            if ((object)c == null) continue;
                            // PER-MAP ids: never cache an NSO from a stale
                            // coexisting scene (see Awake comment).
                            if (!SceneMatchesCurrentMapClient(c)) continue;
                            ushort id = 0;
                            if ((object)_nsoIndexProp != null)
                                id = (ushort)_nsoIndexProp.GetValue(nso, null);
                            else if ((object)_nsoIndexField != null)
                                id = (ushort)_nsoIndexField.GetValue(nso);
                            var go = c.gameObject;
                            bool skip = IsWeaponNsoRootClient(go)
                                     || IsIceOnlyDestructibleRoot(go)
                                     || IsChainStyleDestructibleRoot(go);
                            bool pushable = !skip && IsPushableCrateNsoClient(go);
                            var rbc = c.GetComponent<Rigidbody>();
                            // Floating crates (DontEnableRig) are intentionally
                            // suspended in the map. They must NOT run local physics
                            // (they'd fall "porque sí"); they only move if the game
                            // activates them, in which case the server tells us via
                            // snapshot. Treat them as kinematic-follow.
                            if ((object)_dontEnableRigType == null)
                                _dontEnableRigType = AccessTools.TypeByName("DontEnableRig");
                            bool floating = (object)_dontEnableRigType != null
                                && (object)c.GetComponentInChildren(_dontEnableRigType, true) != null;
                            // NOTE: crate physics (grip, drag, spin cap, continuous
                            // collision) is configured EARLY at map-load in
                            // DisableAllRigidBodies_PushPrefix → ConfigureCratePhysics,
                            // NOT here. Doing it at cache-build time (first fires
                            // ~3s in) re-touched already-settled crates and jolted
                            // them ("a los 3 segundos explotan/desaparecen"). The
                            // cache build now only records flags/refs.
                            _nsoCache[id] = new NsoCacheEntry
                            {
                                Comp = c,
                                Rb = rbc,
                                // All rigidbodies for the velocity clamp — for EVERY
                                // crate (not just pushable-classified), since a
                                // bullet flings whichever crate it hits regardless
                                // of classification. Skip weapons/ice/chains.
                                Rbs = !skip ? c.GetComponentsInChildren<Rigidbody>() : null,
                                SkipSmooth = skip,
                                Pushable = pushable && !floating,
                                Floating = floating
                            };
                        }
                    }
                    _nsoCacheRebuildAt = 60;
                }
                _nsoCacheRebuildAt--;

                int applied = 0;
                float nowTs = Time.realtimeSinceStartup;
                foreach (var e in snap)
                {
                    if (!_nsoCache.TryGetValue(e.Id, out var nsoEntry) || nsoEntry == null || (object)nsoEntry.Comp == null) continue;
                    var nsoComp = nsoEntry.Comp;
                    if (nsoEntry.SkipSmooth) continue;   // weapon / ice / chain — handled elsewhere
                    Vector3 newTarget = new Vector3(e.X, e.Y, e.Z);
                    // P0-15 — only flag for destruction-suppression when the
                    // snapshot delivered a LARGE position jump. The
                    // exponential lerp covers the jump over the next few
                    // frames; during those frames the body sweeps through
                    // adjacent geometry. Threshold (0.3u) is large enough to
                    // not catch normal active-push deltas (which arrive in
                    // ~0.05u increments between 30Hz snapshots) but small
                    // enough to catch the "teleport across map after a
                    // missed snapshot or scene reload" case that produces
                    // spurious destructions.
                    Vector3 currentPos = ((object)nsoEntry.Rb != null)
                        ? nsoEntry.Rb.position : nsoComp.transform.position;
                    if (Vector3.Distance(currentPos, newTarget) > NsoLargeLerpThreshold)
                    {
                        var rootT = nsoComp.transform.root;
                        if ((object)rootT != null) _recentLerpAt[rootT.GetInstanceID()] = nowTs;
                    }
                    bool hasFullRot;
                    Quaternion newRot = BuildNsoRotation(e, out hasFullRot);
                    float nowRt = Time.realtimeSinceStartup;
                    PoseTarget pt;
                    if (_nsoTargets.TryGetValue(e.Id, out pt) && pt != null && pt.HasRender)
                    {
                        float dtv = nowRt - pt.LastRecvAt;
                        // Estimate velocity from consecutive snapshots so we can
                        // extrapolate between them (server delivers ~30Hz or less;
                        // this keeps boxes moving smoothly at render framerate).
                        if (dtv > 0.0001f && dtv < 0.5f)
                            pt.Vel = (newTarget - pt.Pos) / dtv;
                        else
                            pt.Vel = Vector3.zero;
                        // Big jump = teleport / respawn: drop extrapolation history.
                        if (Vector3.Distance(pt.RenderPos, newTarget) > 3f)
                        {
                            pt.RenderPos = newTarget;
                            pt.RenderRot = newRot;
                            pt.Vel = Vector3.zero;
                        }
                        pt.Pos = newTarget;
                        pt.Rot = newRot;
                        pt.HasFullRot = hasFullRot;
                        pt.LastRecvAt = nowRt;
                    }
                    else
                    {
                        pt = new PoseTarget
                        {
                            Pos = newTarget, Rot = newRot, Vel = Vector3.zero,
                            RenderPos = newTarget, RenderRot = newRot,
                            LastRecvAt = nowRt, HasRender = true,
                            HasFullRot = hasFullRot
                        };
                        _nsoTargets[e.Id] = pt;
                    }
                    applied++;
                }
                if (VerboseDiag && (_snapsApplied == 1 || _snapsApplied % 90 == 0))
                    Log.LogInfo($"[P6.14] NSO snap tick={tick} targeted {applied}/{snap.Count}");
            }
            catch (Exception ex) { Log.LogWarning($"[P6.14 NSO apply] {ex.GetType().Name}: {ex.Message}"); }
        }
        private static void OnSceneLoadedTrackMapScene(Scene sc, LoadSceneMode mode)
        {
            try
            {
                if (sc.name != "MainScene") _clientMapSceneName = sc.name;
            }
            catch { }
        }
        private static bool SceneMatchesCurrentMapClient(Component c)
        {
            if (string.IsNullOrEmpty(_clientMapSceneName)) return true;
            try { return c.gameObject.scene.name == _clientMapSceneName; }
            catch { return true; }
        }

        // Reconstruct the server rotation exactly like stock LerpLocalDummy
        // (NetworkSyncableObject.cs:273-274): the up-vector tilts within the
        // Y-Z play plane and the rotation is LookRotation(Cross(right, up), up).
        // Falls back to the legacy eulerZ when the v26.7 section was absent.
        private static Quaternion BuildNsoRotation(NsoSnapEntry e, out bool hasFullRot)
        {
            if (!float.IsNaN(e.UpY) && !float.IsNaN(e.UpZ))
            {
                var up = new Vector3(0f, e.UpY, e.UpZ);
                if (up.sqrMagnitude > 0.0001f)
                {
                    hasFullRot = true;
                    return Quaternion.LookRotation(Vector3.Cross(Vector3.right, up), up);
                }
            }
            hasFullRot = false;
            return Quaternion.Euler(0f, 0f, e.RotZ);
        }

        private static bool IsWeaponNsoRootClient(GameObject root)
        {
            if ((object)root == null) return false;
            if ((object)_weaponPickUpTypeClient == null)
            {
                try { _weaponPickUpTypeClient = AccessTools.TypeByName("WeaponPickUp"); } catch { }
            }
            return (object)_weaponPickUpTypeClient != null
                && root.GetComponentInChildren(_weaponPickUpTypeClient, true) != null;
        }

        private static bool IsIceOnlyDestructibleRoot(GameObject root)
        {
            if ((object)_clientDpType == null)
            {
                _clientDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_clientDpType != null)
                {
                    _clientDpSimpleField = AccessTools.Field(_clientDpType, "simpleDestruction");
                    _clientDpEventField = AccessTools.Field(_clientDpType, "eventDestruction");
                }
            }
            if ((object)_clientDpType == null) return false;
            var dps = root.GetComponentsInChildren(_clientDpType);
            if (dps == null || dps.Length == 0) return false;
            bool any = false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                any = true;
                bool simple = (object)_clientDpSimpleField != null && (bool)_clientDpSimpleField.GetValue(dp);
                bool ev = (object)_clientDpEventField != null && (bool)_clientDpEventField.GetValue(dp);
                if (!simple || ev) return false;
            }
            return any;
        }

        private static bool IsChainStyleDestructibleRoot(GameObject root)
        {
            if ((object)_clientDpType == null)
            {
                _clientDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_clientDpType != null)
                {
                    _clientDpSimpleField = AccessTools.Field(_clientDpType, "simpleDestruction");
                    _clientDpEventField = AccessTools.Field(_clientDpType, "eventDestruction");
                }
            }
            if ((object)_clientDpType == null) return false;
            var dps = root.GetComponentsInChildren(_clientDpType);
            if (dps == null || dps.Length == 0) return false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_clientDpSimpleField != null && (bool)_clientDpSimpleField.GetValue(dp);
                bool ev = (object)_clientDpEventField != null && (bool)_clientDpEventField.GetValue(dp);
                if (!simple && !ev) return true;
            }
            return false;
        }

        // Bug F fix: mirror server's IsPushableCrateNso. Pushable crates need
        // to stay dynamic so SfNsoClientPush.RelayPushableCrateUpdates can
        // relay local push positions back to the server. Forcing kinematic
        // here (the old behavior) killed the relay (it skips when isKinematic)
        // and made boxes feel laggy/ghost-through-players.
        private static bool IsPushableCrateNsoClient(GameObject root)
        {
            if ((object)root == null) return false;
            // Exclude the special destructibles handled elsewhere.
            if (IsChainStyleDestructibleRoot(root) || IsWeaponNsoRootClient(root) || IsIceOnlyDestructibleRoot(root)) return false;
            // MUST match IsPushableCrateRoot (used by DisableAllRigidBodies prefix)
            // or the two disagree: the crate gets made dynamic (local physics) but
            // cached as non-pushable → it falls into the kinematic-follow path that
            // FIGHTS the local sim, and the velocity clamp skips it → bullet fling.
            // So: an NSO with NO DestructiblePiece (plain rigidbody crate) IS
            // pushable, and one WITH destructibles is pushable when any piece is
            // simpleDestruction && !eventDestruction.
            if ((object)_clientDpType == null) return true;
            var dps = root.GetComponentsInChildren(_clientDpType);
            if (dps == null || dps.Length == 0) return true;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_clientDpSimpleField != null && (bool)_clientDpSimpleField.GetValue(dp);
                bool ev = (object)_clientDpEventField != null && (bool)_clientDpEventField.GetValue(dp);
                if (simple && !ev) return true;
            }
            return false;
        }
        private void ApplyMapSyncSnapshot(List<MapSyncSnapEntry> snap, uint tick)
        {
            // DISABLED (P0-14 client position sync). Moving platforms / pillars /
            // animated map pieces run their OWN deterministic local scripts
            // (MoveAlongPathUsingForce, PillarHandler spring, Animator). We used to
            // force them kinematic and lerp their position from the 30Hz server
            // stream, which KILLED the local script and replaced smooth motion with
            // a laggy/jittery network follow → "los mapas con algo que se mueve se
            // bugean / se mueven raro al empezar". Letting the local script run
            // (vanilla behaviour) is correct and smooth. mapState/event sync
            // (visibility, on/off) still flows via ApplyMapStateSnapshot.
            return;
        }

        private void ApplySnapshot(List<SnapshotEntry> snap, uint tick)
        {
            try
            {
                // The local-player branch is disabled (client prediction owns the
                // local rig; the server's authority arrives via InjectInput/movement).
                // So unless opt-in remote smoothing is on there is nothing to apply
                // here — and this runs ~30x/sec. Read the env flag once (it can't
                // change at runtime) and skip the whole scan when it's off. (Also
                // dropped a dead FindObjectsOfType(NetworkPlayer) whose result was
                // never read — the loop iterates the snapshot, not the scene.)
                if (!_smoothRemoteRead)
                {
                    _smoothRemoteRead = true;
                    _smoothRemoteCached = Environment.GetEnvironmentVariable("SFCLIENTRECON_SMOOTH_REMOTE") == "1";
                }
                if (_smoothRemoteCached)
                {
                    int localSlot = FindLocalSlot();
                    if (localSlot >= 0)
                    {
                        foreach (var entry in snap)
                        {
                            // REMOTE players only; stock forwarded PlayerUpdate
                            // (msgType 10) drives them, we smooth toward the snapshot.
                            if (entry.Slot != localSlot)
                                _playerTargets[entry.Slot] = new Vector3(entry.X, entry.Y, entry.Z);
                        }
                    }
                }
                _snapsApplied++;
                if (VerboseDiag && (_snapsApplied == 1 || _snapsApplied % 90 == 0))
                    Log.LogInfo($"[P6.11] Applied snapshot tick={tick} (received={_snapsReceived}, applied={_snapsApplied}, staleDropped={_snapsDroppedStale}, smoothRemote={_smoothRemoteCached}).");
            }
            catch (Exception e) { Log.LogWarning($"[P6.11 apply] {e.Message}"); }
        }
    }
}
