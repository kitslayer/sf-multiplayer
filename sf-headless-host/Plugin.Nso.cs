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
        private static void RunNetworkSyncableObjectInventory()
        {
            try
            {
                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType == null) { Log.LogWarning("[P6.5 NSO] type not found"); return; }
                var nsos = UnityEngine.Object.FindObjectsOfType(nsoType);
                if (nsos == null) { Log.LogInfo("[P6.5 NSO] FindObjectsOfType returned null"); return; }
                int total = nsos.Length;
                int listening = 0;
                var mHasControlF = AccessTools.Field(nsoType, "mHasControl");
                var mIsListeningF = AccessTools.Field(nsoType, "mIsListening");
                var mIndexF = AccessTools.Field(nsoType, "m_Index");
                // mHasControl is static — single value across all NSOs.
                bool staticHasControl = false;
                if ((object)mHasControlF != null) staticHasControl = (bool)mHasControlF.GetValue(null);
                System.Text.StringBuilder sample = new System.Text.StringBuilder();
                int sampled = 0;
                foreach (var o in nsos)
                {
                    bool listen = (object)mIsListeningF != null && (bool)mIsListeningF.GetValue(o);
                    if (listen) listening++;
                    if (sampled < 10)
                    {
                        var comp = o as Component;
                        string name = (object)comp != null ? comp.gameObject.name : "?";
                        ushort idx = (object)mIndexF != null ? (ushort)mIndexF.GetValue(o) : (ushort)0;
                        sample.Append($"\n   [{sampled}] name={name} idx={idx} listening={listen}");
                        sampled++;
                    }
                }
                Log.LogInfo($"[P6.5 NSO] Inventory: {total} NetworkSyncableObjects found in active scene. Static mHasControl={staticHasControl}, {listening}/{total} are listening (mIsListening=true).{sample}");

                // === Phase 6.7 brute-force fixes ===

                // Fix 1: force-set static mHasControl=true. NSO.Start reads
                // MultiplayerManager.IsServer (which Mono inlined past our
                // postfix) and writes the result here. Single static field
                // across all 91 NSOs — one write fixes everything.
                if ((object)mHasControlF != null && !staticHasControl)
                {
                    mHasControlF.SetValue(null, true);
                    Log.LogInfo("[P6.5 NSO] Forced static NetworkSyncableObject.mHasControl = true.");
                }

                // Fix 2: directly populate per-NSO state instead of calling
                // SF's InitSyncedObjects (which throws because each NSO's
                // mNetworkManager is null — NSO.Awake bailed out early when
                // IsNetworkMatch was momentarily false during scene load).
                // We retroactively:
                //   - set NSO.mNetworkManager from GameManager.Instance.mMultiplayerManager
                //   - set NSO.mPacketHandler from GameManager.Instance.P2PPackageHandler
                //   - flip NSO.mIsListening = true
                if (total > 0 && listening == 0)
                {
                    var gmType = AccessTools.TypeByName("GameManager");
                    object gmInst = null;
                    if ((object)gmType != null)
                    {
                        var instGetter = AccessTools.PropertyGetter(gmType, "Instance");
                        if ((object)instGetter != null) gmInst = instGetter.Invoke(null, null);
                    }
                    object mmFromGm = null;
                    object ppFromGm = null;
                    if ((object)gmInst != null)
                    {
                        var mmField = AccessTools.Field(gmType, "mMultiplayerManager");
                        if ((object)mmField != null) mmFromGm = mmField.GetValue(gmInst);
                        var ppProp = AccessTools.PropertyGetter(gmType, "P2PPackageHandler");
                        if ((object)ppProp != null) ppFromGm = ppProp.Invoke(gmInst, null);
                    }
                    var nmField = AccessTools.Field(nsoType, "mNetworkManager");
                    var phField = AccessTools.Field(nsoType, "mPacketHandler");
                    var otsField = AccessTools.Field(nsoType, "mObjectToSync");
                    var updIdxField = AccessTools.Field(nsoType, "mUpdateIndex");
                    var sendRateField = AccessTools.Field(nsoType, "mSendRate");
                    var sendRatePerSecField = AccessTools.Field(nsoType, "mSendRatePerSecond");
                    int patched = 0, listenSet = 0, otsSet = 0, updIdxSet = 0;
                    int nsoIter = 0;
                    foreach (var o in nsos)
                    {
                        nsoIter++;
                        try
                        {
                            var oComp = o as Component;
                            // Distribute NSOs across UpdateIndexHandler buckets
                            // (0..MAX_UPDATE_INDEX-1, currently 5). Without this,
                            // all NSOs cluster on bucket 0 and only fire on every
                            // 5th frame, halving broadcast density.
                            if ((object)updIdxField != null)
                            {
                                updIdxField.SetValue(o, nsoIter % 5);
                                updIdxSet++;
                            }
                            if ((object)nmField != null && (object)mmFromGm != null)
                            {
                                var cur = nmField.GetValue(o);
                                if ((object)cur == null) { nmField.SetValue(o, mmFromGm); patched++; }
                            }
                            if ((object)phField != null && (object)ppFromGm != null)
                            {
                                var cur = phField.GetValue(o);
                                if ((object)cur == null) phField.SetValue(o, ppFromGm);
                            }
                            // mObjectToSync = base.transform if null (the source of the LateUpdate NullRef).
                            if ((object)otsField != null && (object)oComp != null)
                            {
                                var cur = otsField.GetValue(o) as Transform;
                                if ((object)cur == null) { otsField.SetValue(o, oComp.transform); otsSet++; }
                            }
                            // mSendRate = 1/mSendRatePerSecond if uninitialized (default would be 1/0 = inf).
                            if ((object)sendRateField != null && (object)sendRatePerSecField != null)
                            {
                                float sr = (float)sendRateField.GetValue(o);
                                if (sr <= 0f || float.IsInfinity(sr))
                                {
                                    float srPerSec = (float)sendRatePerSecField.GetValue(o);
                                    if (srPerSec <= 0f) srPerSec = 5f;
                                    sendRateField.SetValue(o, 1f / srPerSec);
                                }
                            }
                            if ((object)mIsListeningF != null)
                            {
                                mIsListeningF.SetValue(o, true);
                                listenSet++;
                            }
                        }
                        catch (Exception e) { Log.LogWarning($"[P6.5 NSO] patch one NSO threw: {e.Message}"); }
                    }
                    Log.LogInfo($"[P6.5 NSO] Patched {patched} NSOs (mNetworkManager was null), set mObjectToSync on {otsSet}, distributed mUpdateIndex on {updIdxSet}, mIsListening=true on {listenSet}/{total}.");

                    // Probe: snapshot 10 NSOs' initial position + kinematic state
                    // so we can see in the log whether the oracle's boxes
                    // actually move when the mirror rig walks through them.
                    _probeNsos.Clear();
                    int probeCount = 0;
                    foreach (var o in nsos)
                    {
                        if (probeCount >= 10) break;
                        var comp = o as Component;
                        if ((object)comp == null) continue;
                        var rb = comp.GetComponentInChildren<Rigidbody>();
                        bool kin = (object)rb != null && rb.isKinematic;
                        Vector3 pos = comp.transform.position;
                        ushort idx = 0;
                        var idxF = AccessTools.Field(nsoType, "m_Index");
                        if ((object)idxF != null) idx = (ushort)idxF.GetValue(o);
                        _probeNsos.Add(new ProbeNsoEntry { Component = comp, Name = comp.gameObject.name, Index = idx, InitialPos = pos, HasRigidbody = (object)rb != null, IsKinematic = kin });
                        Log.LogInfo($"[NSO probe] [{probeCount}] name='{comp.gameObject.name}' index={idx} pos={pos} rb={(object)rb != null} kinematic={kin}");
                        probeCount++;
                    }
                    _probeNextLogAt = Time.realtimeSinceStartup + 5f;
                }
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.5 NSO] inventory threw: {e}");
            }
            finally
            {
                _nsoInventoryDone = true;
            }
        }

        // === NSO movement probe ===
        // Captures a few NSOs' initial position at scene-ready and reports
        // displacement every 5s. Answers: "do oracle boxes actually move
        // when the mirror rig walks through them?"
        private struct ProbeNsoEntry
        {
            public Component Component;
            public string Name;
            public ushort Index;
            public Vector3 InitialPos;
            public bool HasRigidbody;
            public bool IsKinematic;
        }
        private static void TickNsoProbe()
        {
            if (_probeNsos.Count == 0) return;
            if (Time.realtimeSinceStartup < _probeNextLogAt) return;
            _probeNextLogAt = Time.realtimeSinceStartup + 5f;
            int moved = 0;
            for (int i = 0; i < _probeNsos.Count; i++)
            {
                var e = _probeNsos[i];
                if ((object)e.Component == null) continue;
                Vector3 cur = e.Component.transform.position;
                float disp = (cur - e.InitialPos).magnitude;
                if (disp > 0.05f) moved++;
                Log.LogInfo($"[NSO probe] [{i}] name='{e.Name}' index={e.Index} pos={cur} disp={disp:0.00} (init={e.InitialPos})");
            }
            Log.LogInfo($"[NSO probe] summary: {moved}/{_probeNsos.Count} moved >5cm from initial.");
        }
        private static void StateProbe()
        {
            try
            {
                if (Time.realtimeSinceStartup - _stateProbeLastAt < 2.0f) return;
                _stateProbeLastAt = Time.realtimeSinceStartup;
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) return;
                var instanceGetter = AccessTools.PropertyGetter(gmType, "Instance");
                object gmInst = null;
                if ((object)instanceGetter != null) gmInst = instanceGetter.Invoke(null, null);
                if ((object)gmInst == null) return;
                var inFightF = AccessTools.Field(gmType, "inFight");
                var rwcF = AccessTools.Field(gmType, "randomWeaponCounter");
                var matchTimeF = AccessTools.Field(gmType, "matchTime");
                var stillInMenuF = AccessTools.Field(gmType, "stillInMenu");
                bool inFight = (object)inFightF != null && (bool)inFightF.GetValue(gmInst);
                float rwc = (object)rwcF != null ? (float)rwcF.GetValue(gmInst) : float.NaN;
                float mt = (object)matchTimeF != null ? (float)matchTimeF.GetValue(gmInst) : float.NaN;
                bool stillInMenu = (object)stillInMenuF != null && (bool)stillInMenuF.GetValue(gmInst);

                var mhType = AccessTools.TypeByName("MatchmakingHandler");
                bool isNetMatch = false;
                if ((object)mhType != null)
                {
                    var inmField = AccessTools.Field(mhType, "mIsNetworkMatch");
                    if ((object)inmField != null) isNetMatch = (bool)inmField.GetValue(null);
                }
                Log.LogInfo($"[P6.5 probe] inFight={inFight} rwc={rwc:0.00} matchTime={mt:0.00} stillInMenu={stillInMenu} IsNetMatch={isNetMatch}");
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5 probe] {e.Message}");
            }
        }

        // After ghost-rig sweep, wake pushable map NSOs so CollectActiveNsoSnapshot
        // sees dynamic motion and v26 clients get box positions (not ghost-through).
        private void WakeNsosNearGhostSweep(Vector3 sweepFrom, Vector3 sweepTo)
        {
            try
            {
                float dist = Vector3.Distance(sweepFrom, sweepTo);
                if (dist < 0.05f) return;
                Vector3 mid = (sweepFrom + sweepTo) * 0.5f;
                // v0.4.0 — tight corridor. The old +1.25u pad woke every crate
                // near a player's PATH ~50×/s; nothing ever slept near players
                // and the whole field jittered ("imposible que se asienten").
                float radius = dist * 0.5f + 0.45f;
                var hits = Physics.OverlapSphere(mid, radius);
                if (hits == null || hits.Length == 0) return;

                EnsureNsoTypeCache();
                if ((object)_nsoType == null) return;

                int woken = 0;
                var seen = new HashSet<Component>();
                foreach (var col in hits)
                {
                    if ((object)col == null) continue;
                    var nsoComp = col.GetComponentInParent(_nsoType) as Component;
                    if ((object)nsoComp == null || !seen.Add(nsoComp)) continue;
                    if (IsChainStyleDestructibleRoot(nsoComp.gameObject)) continue;

                    ushort id = GetNsoIndex(nsoComp);
                    var nsoRbs = nsoComp.GetComponentsInChildren<Rigidbody>();
                    foreach (var rb in nsoRbs)
                    {
                        if ((object)rb == null) continue;
                        if (rb.isKinematic)
                        {
                            rb.isKinematic = false;
                            rb.WakeUp();
                            woken++;
                        }
                    }
                    var p = nsoComp.transform.position;
                    _nsoLastBroadcastPos[id] = p;
                    _nsoLastMovedAt[id] = Time.realtimeSinceStartup;
                }
                if (woken > 0 && (_ghostWakeLogCount < 8 || _ghostWakeLogCount % 120 == 0))
                    Log.LogInfo($"[BOXES] Ghost sweep woke {woken} crate RB(s) near ({mid.x:0.0},{mid.y:0.0},{mid.z:0.0}) r={radius:0.00}");
                if (woken > 0) _ghostWakeLogCount++;
            }
            catch (Exception ex) { Log.LogWarning($"[BOXES] WakeNsosNearGhostSweep: {ex.Message}"); }
        }

        private void EnsureNsoTypeCache()
        {
            if ((object)_nsoType != null) return;
            _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
            if ((object)_nsoType != null)
            {
                _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
            }
        }

        private ushort GetNsoIndex(Component nsoComp)
        {
            ushort id = 0;
            if ((object)_nsoIndexProp != null)
                id = (ushort)_nsoIndexProp.GetValue(nsoComp, null);
            else if ((object)_nsoIndexField != null)
                id = (ushort)_nsoIndexField.GetValue(nsoComp);
            return id;
        }

        private bool IsChainStyleDestructibleRoot(GameObject root)
        {
            if ((object)_wakeDpType == null)
            {
                _wakeDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_wakeDpType != null)
                {
                    _wakeDpSimpleField = AccessTools.Field(_wakeDpType, "simpleDestruction");
                    _wakeDpEventField = AccessTools.Field(_wakeDpType, "eventDestruction");
                }
            }
            if ((object)_wakeDpType == null) return false;
            var dps = root.GetComponentsInChildren(_wakeDpType);
            if (dps == null || dps.Length == 0) return false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_wakeDpSimpleField != null && (bool)_wakeDpSimpleField.GetValue(dp);
                bool ev = (object)_wakeDpEventField != null && (bool)_wakeDpEventField.GetValue(dp);
                if (!simple && !ev) return true;
            }
            return false;
        }

        /// <summary>Simple-destruction crate (not chain/ice pillar).</summary>
        private bool IsPushableCrateNso(GameObject root)
        {
            if (IsChainStyleDestructibleRoot(root) || IsWeaponNsoRoot(root)) return false;
            if ((object)_wakeDpType == null)
            {
                _wakeDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_wakeDpType != null)
                {
                    _wakeDpSimpleField = AccessTools.Field(_wakeDpType, "simpleDestruction");
                    _wakeDpEventField = AccessTools.Field(_wakeDpType, "eventDestruction");
                }
            }
            if ((object)_wakeDpType == null) return false;
            var dps = root.GetComponentsInChildren(_wakeDpType);
            if (dps == null || dps.Length == 0) return false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_wakeDpSimpleField != null && (bool)_wakeDpSimpleField.GetValue(dp);
                bool ev = (object)_wakeDpEventField != null && (bool)_wakeDpEventField.GetValue(dp);
                if (simple && !ev) return true;
            }
            return false;
        }
        private bool IsWeaponNsoRoot(GameObject root)
        {
            if ((object)root == null) return false;
            if ((object)_weaponPickUpType == null)
            {
                try { _weaponPickUpType = AccessTools.TypeByName("WeaponPickUp"); } catch { }
            }
            return (object)_weaponPickUpType != null
                && root.GetComponentInChildren(_weaponPickUpType, true) != null;
        }

        private void TickNsoFallGuard()
        {
            if (_nsoFallGuardNextAt < 0f) _nsoFallGuardNextAt = Time.realtimeSinceStartup + 2f;
            if (Time.realtimeSinceStartup < _nsoFallGuardNextAt) return;
            _nsoFallGuardNextAt = Time.realtimeSinceStartup + 1.0f;
            if (_nsoSpawnPos.Count == 0) return;
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                float now = Time.realtimeSinceStartup;
                int resetsThisTick = 0;
                foreach (var kv in _nsoSpawnPos)
                {
                    ushort id = kv.Key;
                    if (!_nsoByIndexCache.TryGetValue(id, out var comp) || (object)comp == null)
                    {
                        if (_nsoCacheLastRebuildAt < 0f || now - _nsoCacheLastRebuildAt > 2f)
                        {
                            RebuildNsoIndexCache();
                            _nsoCacheLastRebuildAt = now;
                        }
                        if (!_nsoByIndexCache.TryGetValue(id, out comp) || (object)comp == null)
                            continue;
                    }
                    var p = comp.transform.position;
                    // Only act on NSOs that have left the playable area. A crate
                    // below the void threshold is never in legitimate play (no
                    // player push or throw arc lives down there) — it tunneled the
                    // floor due to server physics. The previous version SKIPPED
                    // these: a falling crate is "recently moved" every frame and
                    // has fast downward velocity, so the old recent-push +
                    // downward-velocity guards fired on exactly the crates we
                    // needed to rescue, and the guard never did anything.
                    if (p.y >= NsoFallResetY) continue;
                    if (!IsPushableCrateNso(comp.gameObject))
                    {
                        // v0.4.0 — tracked NON-crate NSOs (ice debris etc.)
                        // knocked into the void fell forever: the rescue below
                        // is crates-only and the stale-NSO freezer deliberately
                        // skips fall-guard-tracked ids, so nothing owned this
                        // case (observed live: 7 bodies at y=-1366 and counting).
                        // Stock SF's host killboxes such debris; parking it
                        // kinematic is our equivalent.
                        var rbsNp = comp.GetComponentsInChildren<Rigidbody>();
                        if (rbsNp != null)
                        {
                            int frozeNp = 0;
                            foreach (var rbn in rbsNp)
                            {
                                if ((object)rbn == null || rbn.isKinematic) continue;
                                rbn.velocity = Vector3.zero;
                                rbn.angularVelocity = Vector3.zero;
                                rbn.isKinematic = true;
                                frozeNp++;
                            }
                            if (frozeNp > 0)
                                Log.LogInfo($"[BOXES] Froze void debris idx={id} Y={p.y:0.0} ({frozeNp} rb)");
                        }
                        continue;
                    }
                    if (resetsThisTick >= NsoFallMaxResetPerTick) break;

                    Vector3 spawn = kv.Value;
                    int voidCount = _nsoVoidResetCount.TryGetValue(id, out var vc) ? vc + 1 : 1;
                    _nsoVoidResetCount[id] = voidCount;

                    // Restore to the on-map spawn the crate had after settle and
                    // keep it DYNAMIC so it behaves like vanilla (pushable, stacks,
                    // can be knocked around). A real map crate has a floor under
                    // its spawn, so it lands and stays — no churn.
                    comp.transform.position = spawn;
                    comp.transform.rotation = Quaternion.identity;
                    bool freeze = voidCount > NsoVoidFreezeAfter;
                    var rbs = comp.GetComponentsInChildren<Rigidbody>();
                    foreach (var rb in rbs)
                    {
                        if ((object)rb == null) continue;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        // Only objects with NO floor under their spawn keep
                        // re-falling. After several rescues, stop the per-second
                        // teleport churn by parking them kinematic — these are
                        // never gameplay crates (e.g. lobby storage at y~70), so
                        // this never freezes a real map box.
                        rb.isKinematic = freeze;
                    }
                    _nsoLastBroadcastPos[id] = spawn;
                    _nsoLastMovedAt[id] = now;
                    _nsoFallthroughResetCount++;
                    resetsThisTick++;
                    if (_nsoFallthroughResetCount <= 5 || _nsoFallthroughResetCount % 20 == 0)
                        Log.LogInfo($"[BOXES] Reset fallthrough idx={id} Y={p.y:0.1} -> spawn ({spawn.y:0.1}) freeze={freeze} (#{_nsoFallthroughResetCount})");
                }
            }
            catch (Exception e) { Log.LogWarning($"[P0-16 fall guard] {e.Message}"); }
        }

        private bool TryGetNsoWorldPosition(ushort idx, out Vector3 pos)
        {
            pos = default;
            if (_nsoByIndexCache.Count == 0 || Time.realtimeSinceStartup - _nsoCacheLastRebuildAt > 5f)
            {
                RebuildNsoIndexCache();
                _nsoCacheLastRebuildAt = Time.realtimeSinceStartup;
            }
            if (!_nsoByIndexCache.TryGetValue(idx, out var comp) || (object)comp == null)
                return false;
            pos = comp.transform.position;
            return true;
        }

        private bool ShouldSkipServerOriginatedDestruction(byte[] data, int len)
        {
            if (data == null || len < 2) return true;
            ushort idx = (ushort)(data[0] | (data[1] << 8));
            if (!TryGetNsoWorldPosition(idx, out var pos))
                return true;
            if (pos.y < -30f) return true;
            if (_sceneLoadRealtime > 0f
                && (Time.realtimeSinceStartup - _sceneLoadRealtime) < 5f
                && TryGetNsoRoot(idx, out var root)
                && IsChainStyleDestructibleRoot(root))
                return true;
            return false;
        }

        private bool TryGetNsoRoot(ushort idx, out GameObject root)
        {
            root = null;
            if (!_nsoByIndexCache.TryGetValue(idx, out var comp) || (object)comp == null)
                return false;
            root = comp.gameObject;
            return true;
        }
        private void ApplyDestructionLocally(byte msgType, byte[] data, int off, int len)
        {
            try
            {
                if (len < 2) return;
                ushort idx = (ushort)(data[off] | (data[off + 1] << 8));

                // Index→Component cache (shared with ApplyClientObjectUpdate).
                float now = Time.realtimeSinceStartup;
                if (_nsoCacheLastRebuildAt < 0f || now - _nsoCacheLastRebuildAt > 5f || _nsoByIndexCache.Count == 0)
                {
                    RebuildNsoIndexCache();
                    _nsoCacheLastRebuildAt = now;
                }
                if (!_nsoByIndexCache.TryGetValue(idx, out var comp) || (object)comp == null)
                {
                    _destructMissCount++;
                    if (_destructMissCount == 1 || _destructMissCount % 60 == 0)
                        Log.LogInfo($"[DESTRUCT] No server NSO for idx={idx} (type={msgType}) #{_destructMissCount} — already gone or not registered.");
                    return;
                }

                if (!_dpDestLookupTried)
                {
                    _dpDestLookupTried = true;
                    _dpDestType = AccessTools.TypeByName("DestructiblePiece");
                    if ((object)_dpDestType != null)
                    {
                        _dpSimpleDestM = AccessTools.Method(_dpDestType, "NetworkForceSimpleDestruction");
                        _dpEventDestM  = AccessTools.Method(_dpDestType, "NetworkForceEvent");
                        _dpForceDestM  = AccessTools.Method(_dpDestType, "NetworkForceDestruction");
                    }
                }
                if ((object)_dpDestType == null) return;
                var dp = comp.GetComponent(_dpDestType) ?? comp.GetComponentInChildren(_dpDestType);
                if ((object)dp == null) { _destructMissCount++; return; }

                if (msgType == PktObjectSimpleDestruction && (object)_dpSimpleDestM != null)
                    _dpSimpleDestM.Invoke(dp, null);
                else if (msgType == PktObjectInvokeDestructionEvent && (object)_dpEventDestM != null)
                    _dpEventDestM.Invoke(dp, null);
                else if (msgType == PktObjectDestructionCollision && (object)_dpForceDestM != null)
                {
                    Vector3 force = Vector3.zero; float mult = 10f;
                    if (len >= 10)
                    {
                        force.y = (short)(data[off + 2] | (data[off + 3] << 8)) / 100f;
                        force.z = (short)(data[off + 4] | (data[off + 5] << 8)) / 100f;
                        mult = BitConverter.ToSingle(data, off + 6);
                    }
                    _dpForceDestM.Invoke(dp, new object[] { force, mult });
                }
                else return;

                _destructAppliedCount++;
                if (_destructAppliedCount == 1 || _destructAppliedCount % 30 == 0)
                    Log.LogInfo($"[DESTRUCT] Applied server-side #{_destructAppliedCount} idx={idx} type={msgType} on '{comp.name}'");
            }
            catch (Exception e) { Log.LogWarning($"[DESTRUCT] apply idx-type={msgType} threw: {e.Message}"); }
        }

        private void ApplyClientObjectUpdate(byte[] data, int off, int len)
        {
            if (len < 10) return;
            ushort idx = (ushort)(data[off] | (data[off + 1] << 8));
            short rawY = (short)(data[off + 2] | (data[off + 3] << 8));
            short rawZ = (short)(data[off + 4] | (data[off + 5] << 8));
            short rawRotZ = (short)(data[off + 8] | (data[off + 9] << 8));
            float py    = rawY / 100f;
            float pz    = rawZ / 100f;
            float rotZ  = rawRotZ / 100f;

            // Rebuild the index→Component cache periodically. NSO Indexes
            // get re-assigned on every scene load, so a stale cache after
            // a map change would point at destroyed objects.
            float now = Time.realtimeSinceStartup;
            if (_nsoCacheLastRebuildAt < 0f || now - _nsoCacheLastRebuildAt > 5f || _nsoByIndexCache.Count == 0)
            {
                RebuildNsoIndexCache();
                _nsoCacheLastRebuildAt = now;
            }
            if (!_nsoByIndexCache.TryGetValue(idx, out var comp) || (object)comp == null)
                return;
            Vector3 pos = new Vector3(0f, py, pz);
            Quaternion rot = Quaternion.Euler(0f, 0f, rotZ);
            var rootRb = comp.GetComponent<Rigidbody>();
            if (RefOk(rootRb) && !rootRb.isKinematic)
            {
                rootRb.position = pos;
                rootRb.rotation = rot;
                rootRb.WakeUp();
            }
            else
            {
                comp.transform.position = pos;
                comp.transform.rotation = rot;
            }
            // Mark as recently-moved so CollectActiveNsoSnapshot will
            // include this NSO in subsequent broadcasts even after the
            // client stops sending updates.
            _nsoLastBroadcastPos[idx] = comp.transform.position;
            _nsoLastMovedAt[idx]      = now;
            _objectUpdateAppliedCount++;
            if (_objectUpdateAppliedCount == 1 || _objectUpdateAppliedCount % 60 == 0)
                Log.LogInfo($"[BOXES] Applied client ObjectUpdate #{_objectUpdateAppliedCount} idx={idx} → ({py:0.0},{pz:0.0})");
        }

        // Server-side per-NSO cache entry. Classification (pushable/weapon) and
        // the Rigidbody ref are computed ONCE here instead of per-tick in
        // CollectActiveNsoSnapshot — which previously did FindObjectsOfType +
        // 2× GetComponentsInChildren per NSO every snapshot. On box-heavy maps
        // (~90 crates) that tanked server FPS → snapshots slowed → boxes lagged
        // on clients, worse the more crates a map had.
        private class NsoSrvEntry { public ushort Id; public Component Comp; public Rigidbody Rb; public bool Pushable; public bool Weapon; }

        private void RebuildNsoIndexCache()
        {
            _nsoByIndexCache.Clear();
            _nsoSrvEntries.Clear();
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                if (all == null) return;
                foreach (var nso in all)
                {
                    var c = nso as Component;
                    if ((object)c == null) continue;
                    if (!SceneMatchesCurrentMap(c)) continue;
                    ushort id = 0;
                    if ((object)_nsoIndexProp != null)
                        id = (ushort)_nsoIndexProp.GetValue(nso, null);
                    else if ((object)_nsoIndexField != null)
                        id = (ushort)_nsoIndexField.GetValue(nso);
                    _nsoByIndexCache[id] = c;
                    var go = c.gameObject;
                    _nsoSrvEntries.Add(new NsoSrvEntry
                    {
                        Id = id,
                        Comp = c,
                        Rb = c.GetComponent<Rigidbody>(),
                        Weapon = IsWeaponNsoRoot(go),
                        Pushable = IsPushableCrateNso(go)
                    });
                }
            }
            catch (Exception ex) { Log.LogWarning($"[BOXES NSO cache] {ex.Message}"); }
        }

        // Ensure the NSO cache is fresh enough for per-tick iteration.
        private void EnsureNsoSrvCache()
        {
            if (_nsoSrvEntries.Count == 0 || _nsoCacheLastRebuildAt < 0f
                || Time.realtimeSinceStartup - _nsoCacheLastRebuildAt > 2f)
            {
                RebuildNsoIndexCache();
                _nsoCacheLastRebuildAt = Time.realtimeSinceStartup;
            }
        }

        private List<NsoSnap> CollectActiveNsoSnapshot()
        {
            var result = new List<NsoSnap>();
            try
            {
                EnsureNsoSrvCache();
                float now = Time.realtimeSinceStartup;
                bool needRebuild = false;
                foreach (var ent in _nsoSrvEntries)
                {
                    var comp = ent.Comp;
                    if (!comp) { needRebuild = true; continue; }
                    if (ent.Weapon) continue;

                    ushort id = ent.Id;
                    Vector3 p;
                    try { p = comp.transform.position; }
                    catch { needRebuild = true; continue; }

                    var rb = ent.Rb;
                    if (!IsFiniteVec3(p) || p.y < -30f) continue;

                    bool dynamicBody = rb && !rb.isKinematic;
                    if (dynamicBody && ent.Pushable)
                    {
                        _nsoLastMovedAt[id] = now;
                        _nsoLastBroadcastPos[id] = p;
                        var eDyn = comp.transform.eulerAngles;
                        var upDyn = comp.transform.up;
                        result.Add(new NsoSnap { Id = id, X = p.x, Y = p.y, Z = p.z, RotZ = eDyn.z, UpY = upDyn.y, UpZ = upDyn.z });
                        continue;
                    }

                    bool dynamicMoving = false;
                    if (dynamicBody)
                    {
                        try
                        {
                            dynamicMoving = rb.velocity.sqrMagnitude > 0.0001f
                                || rb.angularVelocity.sqrMagnitude > 0.0001f;
                        }
                        catch { needRebuild = true; continue; }
                    }

                    bool positionDrifted = !_nsoLastBroadcastPos.TryGetValue(id, out var lastPos)
                        || Vector3.Distance(p, lastPos) > NsoPosDeltaThreshold;

                    float keepAlive = ent.Pushable ? NsoCrateKeepaliveSec : NsoKeepaliveSec;
                    bool recentlyActive = _nsoLastMovedAt.TryGetValue(id, out var lastMovedAt)
                        && (now - lastMovedAt) < keepAlive;

                    if (!dynamicMoving && !positionDrifted && !recentlyActive) continue;

                    if (dynamicMoving || positionDrifted) _nsoLastMovedAt[id] = now;
                    _nsoLastBroadcastPos[id] = p;

                    var e = comp.transform.eulerAngles;
                    var up = comp.transform.up;
                    result.Add(new NsoSnap { Id = id, X = p.x, Y = p.y, Z = p.z, RotZ = e.z, UpY = up.y, UpZ = up.z });
                }
                if (needRebuild)
                {
                    RebuildNsoIndexCache();
                    _nsoCacheLastRebuildAt = Time.realtimeSinceStartup;
                }
            }
            catch (Exception ex) { Log.LogWarning($"[P6.14 NSO collect] {ex.GetType().Name}: {ex.Message}"); }
            return result;
        }
        private void TickStaleNsoFreezer()
        {
            if (_nsoFreezerNextAt < 0f) _nsoFreezerNextAt = Time.realtimeSinceStartup + 5f;
            if (Time.realtimeSinceStartup < _nsoFreezerNextAt) return;
            _nsoFreezerNextAt = Time.realtimeSinceStartup + 3f;
            try
            {
                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType == null) return;
                var nsos = UnityEngine.Object.FindObjectsOfType(nsoType);
                if (nsos == null) return;
                int frozen = 0;
                foreach (var o in nsos)
                {
                    var comp = o as Component;
                    if ((object)comp == null) continue;
                    Vector3 pos = comp.transform.position;
                    if (pos.y > -25f) continue;
                    // Crates the fall-guard tracks (real map boxes with a known
                    // spawn) are rescued back to spawn and kept dynamic — the
                    // freezer must not steal them and park them kinematic in the
                    // void, or they'd never come back. Only freeze NSOs the
                    // fall-guard doesn't own (untracked runaway/non-gameplay).
                    if (_nsoSpawnPos.Count > 0)
                    {
                        ushort fid = GetNsoIndex(comp);
                        if (_nsoSpawnPos.ContainsKey(fid)) continue;
                    }
                    // Below playable area — freeze all its rigidbodies.
                    var rbs = comp.GetComponentsInChildren<Rigidbody>();
                    foreach (var rb in rbs)
                    {
                        if ((object)rb == null) continue;
                        if (!rb.isKinematic)
                        {
                            rb.velocity = Vector3.zero;
                            rb.angularVelocity = Vector3.zero;
                            rb.isKinematic = true;
                            frozen++;
                        }
                    }
                }
                if (frozen > 0)
                    Log.LogInfo($"[P6.7] Froze {frozen} runaway-fall rigidbodies (Y < -25).");
            }
            catch (Exception e) { Log.LogWarning($"[P6.7 freezer] {e.Message}"); }
        }
    }
}
