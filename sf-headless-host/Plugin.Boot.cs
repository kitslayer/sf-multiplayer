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

        /// <summary>PostMapLoad runs StartCountDown; cancel the duplicate scheduled tick.</summary>
        internal static void SuppressScheduledOracleCountDown(string reason)
        {
            _oracleCountDownAt = -1f;
            _oracleCountDownFired = true;
            Log.LogInfo($"[P6.5] Suppressed scheduled StartCountDown ({reason}).");
        }

        private static void InvokeOracleStartCountDown()
        {
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) { Log.LogWarning("[P6.5] GameManager type not found (countdown)"); return; }
                object gmInst = null;
                var instanceGetter = AccessTools.PropertyGetter(gmType, "Instance");
                if ((object)instanceGetter != null) gmInst = instanceGetter.Invoke(null, null);
                if ((object)gmInst == null) gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                if ((object)gmInst == null) { Log.LogWarning("[P6.5] GameManager instance not found (countdown)"); return; }

                var startCountDown = AccessTools.Method(gmType, "StartCountDown");
                bool countDownOk = false;
                if ((object)startCountDown != null)
                {
                    try
                    {
                        startCountDown.Invoke(gmInst, null);
                        countDownOk = true;
                        Log.LogInfo("[P6.5] Invoked GameManager.StartCountDown() on oracle (boss/minigame coroutines).");
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning($"[P6.5] StartCountDown threw: {e.InnerException?.Message ?? e.Message}");
                    }
                }
                var inFightField = AccessTools.Field(gmType, "inFight");
                if (!countDownOk && (object)inFightField != null)
                {
                    inFightField.SetValue(gmInst, true);
                    Log.LogInfo("[P6.5] Fallback: GameManager.inFight = true (no countdown UI in batchmode).");
                }
                // Also reset randomWeaponCounter so a weapon will spawn soon.
                var rwcField = AccessTools.Field(gmType, "randomWeaponCounter");
                if ((object)rwcField != null)
                    rwcField.SetValue(gmInst, 2.0f);
                if ((object)Instance != null)
                    Instance.ScheduleNextSkyWeapon(OracleFirstSkyWeaponDelay);

                // Phase 6.9: manually invoke the network branch of
                // PrepareMapForTravel that SF's host normally runs (and which
                // never reaches us on the oracle — confirmed empirically by
                // zero hits on InitSyncedObjectsPostfix). This is the critical
                // sequence for destructibles + chains + ice.
                InvokeMultiplayerManagerInitChain();
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.5] InvokeOracleStartCountDown threw: {e}");
            }
        }

        // Phase 6.9 — settle phase at Landfall map load. Freezes all RBs briefly,
        // then re-enables dynamics only on pushable crates (not chain-style ice).
        private void OnAnySceneLoadedRunSettle(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "MainScene" || string.IsNullOrEmpty(scene.name)) return;
            if ((object)Instance != null && scene.buildIndex != Instance._currentSceneIndex)
            {
                Log.LogInfo($"[P6.9 settle] Skip stale scene '{scene.name}' buildIndex={scene.buildIndex} (match={Instance._currentSceneIndex}).");
                if (Instance.IsOracleMapLoadInProgress())
                    Instance.ForceCompleteOracleMapLoadIfNeeded("stale-settle-skip");
                return;
            }
            _sceneLoadRealtime = Time.realtimeSinceStartup;
            _nsoSpawnPos.Clear();
            _nsoVoidResetCount.Clear();   // per-NSO-index void-rescue budget — reset with the map (ids get reassigned)
            _nsoPeriodicKeyframeNextAt = Time.realtimeSinceStartup + 1f;
            Log.LogInfo($"[P6.9 settle] Scene loaded: '{scene.name}' (buildIndex={scene.buildIndex}); starting settle coroutine.");
            StartCoroutine(SettlePhaseCoroutine(scene));
            StartCoroutine(DelayedMapTerrainInitCoroutine());
        }

        private System.Collections.IEnumerator DelayedMapTerrainInitCoroutine()
        {
            yield return new WaitForSeconds(OraclePreCombatGraceSec);
            Scene scene;
            if (TryFindLoadedSceneForCurrentMapIndex(out scene))
                EnsureMapSyncObjectsRegistered(scene, true);
            else
                EnsureMapSyncObjectsRegistered();
            InvokeCheckForGroundWeapons("scene-loaded-delay");
            _groundWeaponsRetryAt = Time.realtimeSinceStartup + 4f;
        }

        private System.Collections.IEnumerator SettlePhaseCoroutine(Scene scene)
        {
            yield return null;
            var rootGOs = scene.GetRootGameObjects();
            var allRBs = new List<Rigidbody>();
            foreach (var go in rootGOs)
            {
                if ((object)go == null) continue;
                allRBs.AddRange(go.GetComponentsInChildren<Rigidbody>(true));
            }
            int n = allRBs.Count;
            Log.LogInfo($"[P6.9 settle] Scene '{scene.name}': freezing {n} rigidbodies for settle phase.");
            bool[] wasKinematic = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var rb = allRBs[i];
                if ((object)rb == null) continue;
                wasKinematic[i] = rb.isKinematic;
                rb.isKinematic = true;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            float settleSec = n > 50 ? 2.5f : 1.5f;
            yield return new WaitForSecondsRealtime(settleSec);
            var dpType = AccessTools.TypeByName("DestructiblePiece");
            var dontEnableType = AccessTools.TypeByName("DontEnableRig");
            FieldInfo simpleField = (object)dpType != null ? AccessTools.Field(dpType, "simpleDestruction") : null;
            FieldInfo eventField = (object)dpType != null ? AccessTools.Field(dpType, "eventDestruction") : null;
            int reEnabled = 0;
            for (int i = 0; i < n; i++)
            {
                var rb = allRBs[i];
                if ((object)rb == null) continue;
                if (wasKinematic[i]) continue;
                bool stayKinematic = false;
                if ((object)dpType != null)
                {
                    var dp = rb.GetComponent(dpType);
                    if ((object)dp != null)
                    {
                        bool simple = (object)simpleField != null && (bool)simpleField.GetValue(dp);
                        bool ev = (object)eventField != null && (bool)eventField.GetValue(dp);
                        if (!simple && !ev) stayKinematic = true;
                    }
                }
                if ((object)dontEnableType != null && rb.GetComponent(dontEnableType) != null) stayKinematic = true;
                if (!stayKinematic)
                {
                    rb.isKinematic = false;
                    reEnabled++;
                }
            }
            Log.LogInfo($"[P6.9 settle] Settle complete for '{scene.name}': {reEnabled}/{n} rigidbodies re-enabled dynamic.");
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            // v0.4.0 — id-keyed NSO state is PER-MAP: indexes are reassigned on
            // every scene load, and during the additive transition both maps'
            // NSOs are alive with colliding ids. Record the authoritative map
            // scene (every NSO cache/collect filters on it) and wipe the
            // id-keyed dicts so nothing from the previous round leaks into
            // snapshots, the keepalive, or the fall-guard.
            _currentMapSceneName = scene.name;
            _nsoLastBroadcastPos.Clear();
            _nsoLastMovedAt.Clear();
            _nsoSpawnPos.Clear();
            _nsoVoidResetCount.Clear();   // per-NSO-index void-rescue budget — reset with the map (ids get reassigned)
            RebuildNsoIndexCache();
            _nsoCacheLastRebuildAt = Time.realtimeSinceStartup;
            MarkSceneNsosMovedAfterSettle();
            if ((object)Instance != null)
                Instance.RunPostMapLoadServerInit(scene);
        }
        private static bool SceneMatchesCurrentMap(Component comp)
        {
            if (string.IsNullOrEmpty(_currentMapSceneName)) return true;
            try { return comp.gameObject.scene.name == _currentMapSceneName; }
            catch { return true; }
        }

        // v0.4.0 — track EVERY map-scene load directly. The settle coroutine
        // also updates _currentMapSceneName, but rounds that take the
        // stale-settle-skip / ForceCompleteMapLoad path bypass it entirely —
        // the oracle then filtered all NSO caches to the PREVIOUS map for the
        // whole round: broadcast old-scene crates (clients saw uniform 22-37u
        // errors), empty fall-guard, `boxes` dump showing 0 pushables mid-
        // match (caught live via the debug console, 2026-06-11).
        private static void OnOracleSceneLoadedTrackMap(Scene sc, LoadSceneMode mode)
        {
            try
            {
                if (!_batchModeHost) return;
                if (sc.name == "MainScene") return;
                _currentMapSceneName = sc.name;
                var inst = Instance;
                if ((object)inst != null)
                {
                    inst._nsoLastBroadcastPos.Clear();
                    inst._nsoLastMovedAt.Clear();
                    inst._nsoSpawnPos.Clear();
                    inst._nsoVoidResetCount.Clear();
                    inst.RebuildNsoIndexCache();
                    inst._nsoCacheLastRebuildAt = Time.realtimeSinceStartup;
                }
                Log.LogInfo($"[v26.7] Map scene tracked → '{sc.name}' (NSO caches reset).");
            }
            catch (Exception e) { Log.LogWarning($"[v26.7] scene-track: {e.Message}"); }
        }

        // After settle, seed snapshot tracking so quiescent crates still broadcast once.
        private void MarkSceneNsosMovedAfterSettle()
        {
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
                float now = Time.realtimeSinceStartup;
                foreach (var nso in all)
                {
                    var comp = nso as Component;
                    if ((object)comp == null) continue;
                    if (!SceneMatchesCurrentMap(comp)) continue;
                    ushort id = 0;
                    if ((object)_nsoIndexProp != null)
                        id = (ushort)_nsoIndexProp.GetValue(nso, null);
                    else if ((object)_nsoIndexField != null)
                        id = (ushort)_nsoIndexField.GetValue(nso);
                    var p = comp.transform.position;
                    _nsoLastBroadcastPos[id] = p;
                    _nsoLastMovedAt[id] = now;
                    if (p.y > -30f && !IsChainStyleDestructibleRoot(comp.gameObject) && !IsWeaponNsoRoot(comp.gameObject))
                        _nsoSpawnPos[id] = p;
                }
            }
            catch (Exception ex) { Log.LogWarning($"[P6.9 settle] MarkSceneNsosMovedAfterSettle: {ex.Message}"); }
        }

        // Phase 6.9 — manual invoke of MultiplayerManager.InitMapDataObjects +
        // ReadyUp + InitSyncedObjects. Mirrors GameManager.PrepareMapForTravel
        // lines 1023-1029. The full PrepareMapForTravel coroutine ALSO does
        // a kinematic-settle phase before this (set all rigidbodies kinematic,
        // detach joints, wait 1s, reattach) which is what stops crates from
        // tipping off their stack at scene-load. That bigger fix is the
        // "true" Phase 6.9 work — these three calls are the minimum to make
        // NSOs networked properly.
        private static void InvokeMultiplayerManagerInitChain()
        {
            try
            {
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType == null) { Log.LogWarning("[P6.9] MultiplayerManager type not found"); return; }
                var mmInst = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)mmInst == null) { Log.LogWarning("[P6.9] MultiplayerManager instance not found"); return; }

                var initMapData = AccessTools.Method(mmType, "InitMapDataObjects");
                if ((object)initMapData != null)
                {
                    try { initMapData.Invoke(mmInst, null); Log.LogInfo("[P6.9] InitMapDataObjects invoked manually."); }
                    catch (Exception e) { Log.LogError($"[P6.9] InitMapDataObjects threw: {e.InnerException?.Message ?? e.Message}"); }
                }

                var clientsField = AccessTools.Field(mmType, "mConnectedClients");
                var clientsArr = (object)clientsField != null ? clientsField.GetValue(mmInst) as Array : null;
                var readyUp = AccessTools.Method(mmType, "ReadyUp");
                if ((object)readyUp != null && clientsArr != null && clientsArr.Length > 0)
                {
                    try { readyUp.Invoke(mmInst, null); Log.LogInfo("[P6.9] ReadyUp invoked manually."); }
                    catch (Exception e) { Log.LogError($"[P6.9] ReadyUp threw: {e.InnerException?.Message ?? e.Message}"); }
                }
                else
                {
                    Log.LogInfo("[P6.9] Skipping ReadyUp — mConnectedClients empty on oracle (expected).");
                }

                // InitSyncedObjects is the critical one — runs NSO.Init on every
                // syncable object in scene, which calls AddSyncableObject + sets
                // mIsListening=true + InitRigidBodies. Without it, NSOs are in
                // a half-initialized state where physics works but networking
                // doesn't (boxes broadcast position but their NetworkSpawnID
                // never gets registered properly).
                var initSynced = AccessTools.Method(mmType, "InitSyncedObjects");
                if ((object)initSynced != null)
                {
                    try { initSynced.Invoke(mmInst, null); Log.LogInfo("[P6.9] InitSyncedObjects invoked manually — NSOs should now be fully networked."); }
                    catch (Exception e) { Log.LogError($"[P6.9] InitSyncedObjects threw: {e.InnerException?.Message ?? e.Message}"); }
                }

                // Phase 6.8 — CheckForGroundWeapons broadcasts the map's
                // pre-placed weapons (the ones in level geometry, registered
                // via InitWeaponPickUpOnAwake → AddPreSpawnedWeapon). Stock SF
                // calls this from GameManager.StartMapSequence after the map
                // loads + IsNetworkMatch is true. On our oracle that coroutine
                // chain doesn't fire; manually invoking ensures clients get
                // GroundWeaponsInit (msgType 31) so map-preset weapons appear
                // at their fixed spots. Addresses user-reported "I cant grab
                // guns that spawn on some maps."
                var checkGround = AccessTools.Method(mmType, "CheckForGroundWeapons");
                if ((object)checkGround != null)
                {
                    try { checkGround.Invoke(mmInst, null); Log.LogInfo("[P6.8] CheckForGroundWeapons invoked manually — map-preset weapons broadcast."); }
                    catch (Exception e) { Log.LogError($"[P6.8] CheckForGroundWeapons threw: {e.InnerException?.Message ?? e.Message}"); }
                }
                if ((object)Instance != null)
                {
                    Instance.EnsureMapSyncObjectsRegistered();
                    Instance.FlushGroundWeaponsAfterCheck("InitChain");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.9] InvokeMultiplayerManagerInitChain threw: {e}");
            }
        }

        /// <summary>Boss/Halloween maps: wake CustomMap handlers after scene + countdown init.</summary>
        private static void InvokeOracleBossMapSetup()
        {
            if (!_batchModeHost) return;
            try
            {
                // Gate by LOADED SCENE NAME, not a hardcoded index range. Boss/
                // event maps (HalloweenBoss2 = buildIndex 95, Space/Factory boss
                // variants, Pumpkin, etc.) live outside the old 100-109 range, so
                // that check silently skipped them → boss/event never spawned.
                bool isEventMap = false;
                for (int si = 0; si < SceneManager.sceneCount; si++)
                {
                    var sc = SceneManager.GetSceneAt(si);
                    if (!sc.isLoaded || sc.name == "MainScene") continue;
                    string n = sc.name.ToLowerInvariant();
                    if (n.Contains("boss") || n.Contains("halloween") || n.Contains("pumpkin")
                        || n.Contains("christmas") || n.Contains("xmas") || n.Contains("event"))
                    { isEventMap = true; break; }
                }
                if (!isEventMap) return;
                var behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                if (behaviours == null) return;
                int invoked = 0;
                foreach (var mb in behaviours)
                {
                    if ((object)mb == null) continue;
                    string tn = mb.GetType().Name;
                    if (tn.IndexOf("CustomMap", StringComparison.OrdinalIgnoreCase) < 0
                        && tn.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) < 0
                        && tn.IndexOf("Halloween", StringComparison.OrdinalIgnoreCase) < 0
                        && tn.IndexOf("Pumpkin", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    foreach (var m in mb.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (ReferenceEquals(m, null) || m.GetParameters().Length != 0) continue;
                        string mn = m.Name;
                        if (mn == "Awake" || mn == "Start" || mn.IndexOf("Init", StringComparison.OrdinalIgnoreCase) >= 0
                            || mn.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { m.Invoke(mb, null); invoked++; } catch { }
                            break;
                        }
                    }
                }
                if (invoked > 0)
                    Log.LogInfo($"[P6.5] Boss map setup: invoked {invoked} handler(s) on event scene.");
            }
            catch (Exception e) { Log.LogWarning($"[P6.5] InvokeOracleBossMapSetup: {e.Message}"); }
        }
        private static void InvokeOracleStartMatch()
        {
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) { Log.LogWarning("[P6.5] GameManager type not found"); return; }
                // Try the singleton accessor first — GameManager._instance is
                // set in Awake on the MainScene boot; persists if marked
                // DontDestroyOnLoad.
                object gmInst = null;
                var instanceGetter = AccessTools.PropertyGetter(gmType, "Instance");
                if ((object)instanceGetter != null)
                {
                    gmInst = instanceGetter.Invoke(null, null);
                }
                if ((object)gmInst == null)
                {
                    gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                }
                if ((object)gmInst == null)
                {
                    // Last resort: scan FindObjectsOfTypeAll (catches inactive + scene-less).
                    var includeInactive = Resources.FindObjectsOfTypeAll(gmType);
                    if (includeInactive != null && includeInactive.Length > 0)
                    {
                        gmInst = includeInactive[0];
                        Log.LogInfo($"[P6.5] GameManager found via FindObjectsOfTypeAll (count={includeInactive.Length}).");
                    }
                }
                if ((object)gmInst == null) { Log.LogWarning("[P6.5] GameManager instance not found (Instance/FindObjectOfType/FindObjectsOfTypeAll all null)"); return; }
                var mwType = AccessTools.TypeByName("MapWrapper");
                if ((object)mwType == null) { Log.LogWarning("[P6.5] MapWrapper type not found"); return; }

                int sceneIdx = (object)Instance != null ? Instance._currentSceneIndex : 6;
                var mapWrapper = Activator.CreateInstance(mwType);
                var mtField = AccessTools.Field(mwType, "MapType");
                var mdField = AccessTools.Field(mwType, "MapData");
                if ((object)mtField != null) mtField.SetValue(mapWrapper, (byte)0);
                if ((object)mdField != null) mdField.SetValue(mapWrapper, BitConverter.GetBytes(sceneIdx));

                var startMatchMethod = AccessTools.Method(gmType, "StartMatch", new[] { mwType, typeof(bool) });
                if ((object)startMatchMethod == null) { Log.LogWarning("[P6.5] StartMatch(MapWrapper,bool) method not found"); return; }
                Log.LogInfo($"[P6.5] Invoking GameManager.StartMatch(MapType=0, sceneIdx={sceneIdx}, MovePlayers=false).");
                startMatchMethod.Invoke(gmInst, new object[] { mapWrapper, false });
                Log.LogInfo("[P6.5] GameManager.StartMatch returned (no immediate exception).");
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.5] InvokeOracleStartMatch threw: {e}");
            }
        }

        // Boot is driven by Update() as a state machine because Unity 5.6's
        // Mono runtime is missing IteratorStateMachineAttribute (emitted by the
        // C# compiler for any method with `yield return`). Using a plain
        // state machine keeps the assembly compatible.
        private enum BootState { Idle, WaitForInit, LoadingScene, WaitingForSceneSettle, HostStarting, Running }

        private void StepBoot()
        {
            switch (_bootState)
            {
                case BootState.Idle:
                    return;

                case BootState.WaitForInit:
                    // 2 second settle to let BepInEx + Unity main-thread init.
                    if (Time.realtimeSinceStartup - _bootStartedAt < 2.0f) return;
                    Log.LogInfo($"Step 1: SceneManager.LoadScene({InitialScene}, Single)");
                    try
                    {
                        _loadOp = SceneManager.LoadSceneAsync(InitialScene, LoadSceneMode.Single);
                    }
                    catch (Exception e)
                    {
                        Log.LogError($"LoadSceneAsync({InitialScene}) threw: {e}");
                        _bootState = BootState.Idle;
                        return;
                    }
                    if (_loadOp == null)
                    {
                        Log.LogError($"LoadSceneAsync({InitialScene}) returned null.");
                        _bootState = BootState.Idle;
                        return;
                    }
                    _bootState = BootState.LoadingScene;
                    _stateSince = Time.realtimeSinceStartup;
                    return;

                case BootState.LoadingScene:
                    if (_loadOp == null || _loadOp.isDone)
                    {
                        var s = SceneManager.GetActiveScene();
                        Log.LogInfo($"Scene loaded: {s.name} (buildIndex={s.buildIndex})");
                        _bootState = BootState.WaitingForSceneSettle;
                        _settleFrames = 0;
                    }
                    else if (Time.realtimeSinceStartup - _stateSince > 30.0f)
                    {
                        Log.LogError("Scene load timed out after 30s — aborting.");
                        _bootState = BootState.Idle;
                    }
                    return;

                case BootState.WaitingForSceneSettle:
                    // Wait a few frames so Awake/Start on the new scene's objects finishes.
                    if (++_settleFrames < 3) return;
                    _bootState = BootState.HostStarting;
                    return;

                case BootState.HostStarting:
                    StartHost();
                    StartBridge();
                    // Cache playerPrefab while ControllerHandler still exists
                    // in MainScene — needed because subsequent loadMap(Single)
                    // destroys it but we still want to spawn rigs in any scene.
                    TryCachePlayerPrefab();
                    _bootState = BootState.Running;
                    _lastHeartbeat = Time.realtimeSinceStartup;
                    _lastStateEmit = Time.realtimeSinceStartup;
                    return;

                case BootState.Running:
                    // Drain any incoming bridge commands (debug bridge on 1341).
                    DrainBridgeCommands();
                    // Drain raw v25 protocol packets from patched DLL clients.
                    DrainSfServer();
                    // Drop stale clients so we don't keep forwarding broadcasts
                    // to ghosts after ungraceful disconnects.
                    SweepStaleClients();
                    // Fire scheduled match-start if /start was issued (user-driven now,
                    // no longer auto-armed by ClientRequestingToSpawn).
                    if (_autoStartAt > 0f && Time.realtimeSinceStartup >= _autoStartAt && !_matchStarted)
                    {
                        _autoStartAt = -1f;
                        FireMatchStart("scheduled");
                    }
                    // Phase 6.5 Step 2 — kick GameManager.StartMatch on the oracle
                    // so the StartMapSequence coroutine runs (additively loads
                    // the scene + sets up the map).
                    if (_oracleStartMatchAt > 0f && Time.realtimeSinceStartup >= _oracleStartMatchAt && !_oracleStartMatchFired)
                    {
                        _oracleStartMatchAt = -1f;
                        _oracleStartMatchFired = true;
                        InvokeOracleStartMatch();
                        // Schedule StartCountDown 3s later — after StartMapSequence
                        // has had time to do its TimeHandler decay + LoadMap +
                        // 1.1s WaitForSecondsRealtime. StartCountDown's own
                        // coroutine yields 1s then flips inFight=true, which is
                        // what makes the weapon-spawn counter actually tick.
                        _oracleCountDownAt = Time.realtimeSinceStartup + 3.0f;
                        _oracleCountDownFired = false;
                        Log.LogInfo("[P6.5] Scheduled GameManager.StartCountDown in 3s (flips inFight=true).");
                    }
                    // Phase 6.5 Step 2b — kick StartCountDown so inFight goes true.
                    if (_oracleCountDownAt > 0f && Time.realtimeSinceStartup >= _oracleCountDownAt && !_oracleCountDownFired)
                    {
                        _oracleCountDownAt = -1f;
                        _oracleCountDownFired = true;
                        InvokeOracleStartCountDown();
                        // Schedule NSO inventory 4s later — gives StartMapSequence
                        // + PrepareMapForTravel + InitSyncedObjects time to settle.
                        _nsoInventoryAt = Time.realtimeSinceStartup + 4.0f;
                        _nsoInventoryDone = false;
                    }
                    if (_nsoInventoryAt > 0f && Time.realtimeSinceStartup >= _nsoInventoryAt && !_nsoInventoryDone)
                    {
                        _nsoInventoryAt = -1f;
                        RunNetworkSyncableObjectInventory();
                        // Schedule authoritative-player spawn after NSO state is fixed.
                        _authSpawnAt = Time.realtimeSinceStartup + 1.0f;
                    }
                    // Phase 6.9 — spawn real NetworkPlayers per connected client.
                    // They're the server's authoritative copy; eventually driven
                    // by client inputs (Phase 6.12) and broadcast back to all
                    // clients as snapshot (Phase 6.10) for reconciliation.
                    if (_authSpawnAt > 0f && Time.realtimeSinceStartup >= _authSpawnAt && !_authSpawnDone)
                    {
                        _authSpawnAt = -1f;
                        _authSpawnDone = true;
                        SpawnAuthoritativePlayersForAllClients();
                    }
                    // Round advance: kill detected → fire MapChange after delay.
                    if (_pendingRoundAdvanceAt > 0f && Time.realtimeSinceStartup >= _pendingRoundAdvanceAt)
                    {
                        _pendingRoundAdvanceAt = -1f;
                        AdvanceRound();
                    }
                    if (_pendingClientStartMatchAt > 0f && Time.realtimeSinceStartup >= _pendingClientStartMatchAt && !_pendingClientStartMatchFired)
                    {
                        _pendingClientStartMatchAt = -1f;
                        _pendingClientStartMatchFired = true;
                        BroadcastStartMatch();
                        Log.LogInfo("[SF] Deferred StartMatch sent to clients (post MapChange load window).");
                    }
                    // After MapChange settles, send StartMatch to kick the next round's countdown.
                    if (_pendingStartMatchAt > 0f && Time.realtimeSinceStartup >= _pendingStartMatchAt)
                    {
                        _pendingStartMatchAt = -1f;
                        BroadcastStartMatch();
                        Log.LogInfo("[SF] Round advance: StartMatch sent.");
                    }
                    if (_pendingRearmCombatAt > 0f && Time.realtimeSinceStartup >= _pendingRearmCombatAt)
                    {
                        _pendingRearmCombatAt = -1f;
                        RearmOracleCombatLoop("delayed-post-StartMatch");
                        FlushGroundWeaponsAfterCheck("post-StartMatch");
                    }
                    TickOracleMapLoadTimeout();
                    TickPeriodicWeaponRearm();
                    TickOracleSkyWeaponSpawner();
                    // Push the latest per-slot inputs into each spawned rig's
                    // CharacterActions. Done every frame even if no new input
                    // arrived — analog sticks need their last value held so
                    // the rig keeps moving between input packets.
                    WriteInputsToRigs();
                    // Emit a state snapshot at 30 Hz if anyone has pinged us.
                    if (_bridgePeer != null && Time.realtimeSinceStartup - _lastStateEmit >= (1.0f / 30.0f))
                    {
                        _lastStateEmit = Time.realtimeSinceStartup;
                        EmitStateSnapshot();
                    }
                    var interval = Verbose ? 5.0f : 30.0f;
                    if (Time.realtimeSinceStartup - _lastHeartbeat >= interval)
                    {
                        float elapsed = Time.realtimeSinceStartup - _lastHeartbeat;
                        _lastHeartbeat = Time.realtimeSinceStartup;
                        _heartbeatTicks++;
                        // Rates over the interval window.
                        float pktRate   = (_sfPacketsRx        - _heartbeatLastPkt)   / elapsed;
                        float snapRate  = (_serverTick         - _heartbeatLastSnap)  / elapsed;
                        float inputRate = (_inputPacketsRx     - _heartbeatLastInput) / elapsed;
                        _heartbeatLastPkt   = _sfPacketsRx;
                        _heartbeatLastSnap  = _serverTick;
                        _heartbeatLastInput = _inputPacketsRx;
                        int spawned = 0, connected = 0;
                        foreach (var kv in _sfClients) { connected++; if (kv.Value.Spawned) spawned++; }
                        Log.LogInfo($"heartbeat: scene={SceneManager.GetActiveScene().name} tick={_heartbeatTicks} | clients={connected} spawned={spawned} | rx={pktRate:0.0}/s snap={snapRate:0.0}/s input={inputRate:0.0}/s | rigs={SlotToRig.Count} matchStarted={_matchStarted}");
                    }
                    // Phase 6.5 — periodic state probe (only after match has started).
                    if (_matchStarted)
                    {
                        StateProbe();
                        TickNsoProbe();
                        TickNsoFallGuard();
                        TickStaleNsoFreezer();
                        TickGroundWeaponsRetry();
                        TickMapSyncRetry();
                        TickOracleMapInfoBootstrap();
                        TickOraclePreCombatGrace();
                        TickBoxDiagnostic();
                        TickAuthRigDeathCheck();
                    }
                    // Phase 6.10 — 30Hz authoritative-state broadcast (msgType 39).
                    // (Projectiles advance in FixedUpdate now — fixed-step, FPS-independent.)
                    TickWorldStateSnapshot();
                    return;
            }
        }

        private void StartHost()
        {
            // Path A: oracle owns the patched DLL's wire protocol directly.
            // No Lidgren MatchMakingHandlerSockets.HostServer — the patched
            // DLL doesn't actually use Lidgren (its socket-mode receive is
            // commented out; P2PPackageHandler.Init opens a RAW UDP socket
            // via UDPClient(address, port)). We bind our OWN raw UDP socket
            // on BindPort and parse the 14-byte-wrapped v25 protocol that
            // sfdsrv speaks.
            try
            {
                _sfServer = new UdpClient(BindPort);
                _sfServer.Client.Blocking = false;
                Log.LogInfo($"SF server: listening on UDP {BindPort} (raw v25 protocol).");
            }
            catch (Exception e)
            {
                Log.LogError($"SF server bind on {BindPort} threw: {e}");
                return;
            }
            // Default tickrate: 60Hz on both server and client (was Unity's
            // stock 50Hz). Client plugin sets the same in SFClientRecon.Awake.
            // Operator can change live with /tickrate N.
            Time.fixedDeltaTime = 1f / 60f;
            {
                float fd = Time.fixedDeltaTime;
                int hz = (fd > 0f) ? (int)System.Math.Round(1.0 / fd) : 0;
                Log.LogInfo($"Server physics: {hz}Hz (Time.fixedDeltaTime={fd:0.0000}s). Snapshot broadcast: {SnapshotHz}Hz. Client FPS is independent.");
            }
            Log.LogInfo($"=== HEADLESS HOST READY on port {BindPort} ===");
        }
    }
}
