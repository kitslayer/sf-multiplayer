using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFHeadlessHost
{
    // v26.6 map terrain + ground weapons (Fases 1–4).
    public partial class Plugin
    {
        private const int MapStateMaxPayload = 32;
        private const float MapSyncLogIntervalSec = 30f;
        private static bool _batchModeHost;
        private byte[] _cachedGroundWeaponsBody;
        private int _groundWeaponsEntryCount;
        private float _groundWeaponsRetryAt = -1f;
        private float _mapSyncDiagNextAt = -1f;
        private int _mapSyncObjectsRegistered;
        private Type _mapSyncBaseType;
        private FieldInfo _mapSyncStartPosField;
        private MethodInfo _mapGetDataMethod;
        private MethodInfo _mapSetDataMethod;
        private FieldInfo _mapNetworkControlField;
        private float _skyWeaponTickAt = -1f;
        private int _skyWeaponSpawnCount;
        private static int _mapAwakeRegisterCount;
        private bool _oracleMapLoadInProgress;
        private float _oracleMapLoadStartedAt = -1f;
        private float _oracleMapLoadForceCompleteAt = -1f;
        private bool _roundAdvanceQueuedWhileLoading;
        private const float OracleMapLoadForceCompleteSec = 8f;

        private struct MapStateSnap
        {
            public float StartX, StartY;
            public byte[] Data;
        }

        private void InstallMapTerrainAuthorityPatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid + ".map-terrain");
                var mapBase = AccessTools.TypeByName("MapInfoSyncableBase");
                if ((object)mapBase != null)
                {
                    var awake = AccessTools.Method(mapBase, "Awake");
                    if ((object)awake != null)
                    {
                        harmony.Patch(awake, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(MapInfoSyncableBaseAwakePostfix))));
                        Log.LogInfo("[v26.6] Patched MapInfoSyncableBase.Awake (oracle network control + dict register).");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] map terrain patches: {e.Message}"); }
        }

        internal static void MapInfoSyncableBaseAwakePostfix(object __instance)
        {
            if (!_batchModeHost || __instance == null) return;
            try
            {
                var t = __instance.GetType();
                var netF = AccessTools.Field(t, "m_NetworkControl");
                if ((object)netF != null) netF.SetValue(__instance, true);
                var startF = AccessTools.Field(t, "m_StartPos");
                if ((object)startF == null) return;
                Vector2 sp = (Vector2)startF.GetValue(__instance);
                sp = QuantizeMapSyncKey(sp);
                startF.SetValue(__instance, sp);
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                var add = AccessTools.Method(mm.GetType(), "AddMapDataObject");
                if ((object)add == null) return;
                add.Invoke(mm, new object[] { sp, __instance });
                _mapAwakeRegisterCount++;
            }
            catch { }
        }

        private static object GetMultiplayerManagerInstance()
        {
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) return null;
                var instProp = AccessTools.Property(gmType, "Instance");
                object gm = null;
                if ((object)instProp != null) gm = instProp.GetValue(null, null);
                if (gm == null) return null;
                var mmF = AccessTools.Field(gmType, "mMultiplayerManager");
                if ((object)mmF == null) return null;
                return mmF.GetValue(gm);
            }
            catch { return null; }
        }

        internal void ClearMapDataObjectsOnOracle()
        {
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                var clear = AccessTools.Method(mm.GetType(), "ClearMapDataObjects");
                if ((object)clear != null)
                {
                    clear.Invoke(mm, null);
                    Log.LogInfo("[v26.6] ClearMapDataObjects invoked on oracle.");
                    return;
                }
                var dictF = AccessTools.Field(mm.GetType(), "mMapDataObjectToSync");
                if ((object)dictF != null)
                {
                    var dict = dictF.GetValue(mm) as IDictionary;
                    if (dict != null)
                    {
                        dict.Clear();
                        Log.LogInfo("[v26.6] mMapDataObjectToSync cleared on oracle.");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] ClearMapDataObjects: {e.Message}"); }
        }

        /// <summary>Mono 2.x lacks FieldInfo/PropertyInfo op_Inequality — never use `fi != null`.</summary>
        private static bool RefOk(object o) => !ReferenceEquals(o, null);

        /// <summary>Re-enable sky weapon spawns and inFight on the oracle after each round.</summary>
        internal void RearmOracleCombatLoop(string reason)
        {
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) return;
                object gmInst = null;
                var instanceGetter = AccessTools.PropertyGetter(gmType, "Instance");
                if ((object)instanceGetter != null) gmInst = instanceGetter.Invoke(null, null);
                if ((object)gmInst == null) gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                if ((object)gmInst == null)
                {
                    Log.LogWarning($"[P6.5] RearmOracleCombatLoop({reason}): GameManager not found.");
                    return;
                }
                var inFightF = AccessTools.Field(gmType, "inFight");
                if ((object)inFightF != null) inFightF.SetValue(gmInst, true);
                var stillMenuF = AccessTools.Field(gmType, "stillInMenu");
                if ((object)stillMenuF != null) stillMenuF.SetValue(gmInst, false);
                var matchTimeF = AccessTools.Field(gmType, "matchTime");
                if ((object)matchTimeF != null) matchTimeF.SetValue(gmInst, 0f);
                var rwcField = AccessTools.Field(gmType, "randomWeaponCounter");
                if ((object)rwcField != null) rwcField.SetValue(gmInst, 2.0f);
                Log.LogInfo($"[P6.5] RearmOracleCombatLoop({reason}): inFight=true matchTime=0 randomWeaponCounter=2.0");
                _skyWeaponTickAt = Time.realtimeSinceStartup + 1.5f;
            }
            catch (Exception e) { Log.LogWarning($"[P6.5] RearmOracleCombatLoop({reason}): {e.Message}"); }
        }

        /// <summary>
        /// Headless oracle: GameManager.Update often stalls matchTime / randomWeaponCounter after
        /// round advance (logs showed matchTime≈2.43 and rwc≈1.19 forever). Manually tick both.
        /// </summary>
        internal void TickOracleCombatTimers()
        {
            if (!_matchStarted || !_batchModeHost) return;
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if (!RefOk(gmType)) return;
                object gmInst = null;
                var ig = AccessTools.PropertyGetter(gmType, "Instance");
                if (RefOk(ig)) gmInst = ig.Invoke(null, null);
                if (!RefOk(gmInst)) gmInst = UnityEngine.Object.FindObjectOfType(gmType);
                if (!RefOk(gmInst)) return;

                var inFightF = AccessTools.Field(gmType, "inFight");
                if (RefOk(inFightF) && !(bool)inFightF.GetValue(gmInst))
                    inFightF.SetValue(gmInst, true);

                var matchTimeF = AccessTools.Field(gmType, "matchTime");
                if (RefOk(matchTimeF))
                {
                    float mt = (float)matchTimeF.GetValue(gmInst);
                    if (mt < 1f) mt = 1f;
                    matchTimeF.SetValue(gmInst, mt + Time.deltaTime);
                }

                var rwcF = AccessTools.Field(gmType, "randomWeaponCounter");
                if (!RefOk(rwcF)) return;
                float rwc = (float)rwcF.GetValue(gmInst);
                rwc -= Time.deltaTime;
                if (rwc <= 0f)
                {
                    rwc = 0f;
                    SpawnRandomWeaponPrefix(gmInst);
                    _skyWeaponSpawnCount++;
                    if (_skyWeaponSpawnCount <= 8 || _skyWeaponSpawnCount % 10 == 0)
                        Log.LogInfo($"[P6.5] CombatTimer: sky spawn #{_skyWeaponSpawnCount}");
                }
                rwcF.SetValue(gmInst, rwc);
            }
            catch (Exception e) { Log.LogWarning($"[P6.5] TickOracleCombatTimers: {e.Message}"); }
        }

        /// <summary>Legacy entry — combat timers now drive sky weapons every frame.</summary>
        internal void TickOracleSkyWeaponSpawner()
        {
            TickOracleCombatTimers();
        }

        private void EnsureMapSyncObjectsRegistered()
        {
            try
            {
                if ((object)_mapSyncBaseType == null)
                {
                    _mapSyncBaseType = AccessTools.TypeByName("MapInfoSyncableBase");
                    _mapSyncStartPosField = (object)_mapSyncBaseType != null
                        ? AccessTools.Field(_mapSyncBaseType, "m_StartPos") : null;
                }
                if ((object)_mapSyncBaseType == null) return;
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                var add = AccessTools.Method(mm.GetType(), "AddMapDataObject");
                if ((object)add == null) return;
                var all = UnityEngine.Object.FindObjectsOfType(_mapSyncBaseType);
                int added = 0;
                if (all != null)
                {
                    foreach (var obj in all)
                    {
                        if (obj == null) continue;
                        var startF = AccessTools.Field(obj.GetType(), "m_StartPos");
                        var netF = AccessTools.Field(obj.GetType(), "m_NetworkControl");
                        if ((object)netF != null) netF.SetValue(obj, true);
                        Vector2 sp = (object)startF != null
                            ? QuantizeMapSyncKey((Vector2)startF.GetValue(obj))
                            : QuantizeMapSyncKey(new Vector2(
                                (obj as Component).transform.position.y,
                                (obj as Component).transform.position.z));
                        if ((object)startF != null) startF.SetValue(obj, sp);
                        add.Invoke(mm, new object[] { sp, obj });
                        added++;
                    }
                }
                _mapSyncObjectsRegistered = added;
                Log.LogInfo($"[v26.6] EnsureMapSyncObjectsRegistered: {added} MapInfoSyncableBase (awake-registers={_mapAwakeRegisterCount})");
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] EnsureMapSync: {e.Message}"); }
        }

        private void EnsurePreSpawnedWeaponsRegistered()
        {
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                var wpType = AccessTools.TypeByName("WeaponPickUp");
                if ((object)wpType == null) return;
                var add = AccessTools.Method(mm.GetType(), "AddPreSpawnedWeapon");
                if ((object)add == null) return;
                var all = UnityEngine.Object.FindObjectsOfType(wpType);
                int n = 0;
                if (all == null) return;
                foreach (var wp in all)
                {
                    var comp = wp as Component;
                    if ((object)comp == null) continue;
                    var p = comp.transform.position;
                    var pos = new Vector2(p.y, p.z);
                    add.Invoke(mm, new object[] { pos, wp });
                    n++;
                }
                if (n > 0) Log.LogInfo($"[v26.6] EnsurePreSpawnedWeaponsRegistered: {n} WeaponPickUp");
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] EnsurePreSpawned: {e.Message}"); }
        }

        internal void InvokeCheckForGroundWeapons(string reason)
        {
            try
            {
                EnsurePreSpawnedWeaponsRegistered();
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                var check = AccessTools.Method(mm.GetType(), "CheckForGroundWeapons");
                if ((object)check == null) return;
                check.Invoke(mm, null);
                Log.LogInfo($"[P6.8/v26.6] CheckForGroundWeapons ({reason})");
            }
            catch (Exception e) { Log.LogError($"[P6.8] CheckForGroundWeapons ({reason}): {e.InnerException?.Message ?? e.Message}"); }
        }

        internal void CacheGroundWeaponsBroadcast(byte[] body)
        {
            if (body == null || body.Length < 2) return;
            _cachedGroundWeaponsBody = (byte[])body.Clone();
            _groundWeaponsEntryCount = body[0] | (body[1] << 8);
            Log.LogInfo($"[P6.8] Cached GroundWeaponsInit count={_groundWeaponsEntryCount} bytes={body.Length}");
        }

        private void SendCachedGroundWeaponsToClient(SfClient cli)
        {
            if (_cachedGroundWeaponsBody == null || _cachedGroundWeaponsBody.Length < 2) return;
            if (!cli.Initialized) return;
            SendSfPacket(cli.Addr, PktGroundWeaponsInit, _cachedGroundWeaponsBody, 0uL, 0);
            Log.LogInfo($"[P6.8] Resent GroundWeaponsInit to slot={cli.Slot} count={_groundWeaponsEntryCount}");
        }

        private void BroadcastGroundWeaponsToAllClients()
        {
            if (_cachedGroundWeaponsBody == null || _cachedGroundWeaponsBody.Length < 2) return;
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktGroundWeaponsInit, _cachedGroundWeaponsBody, 0uL, 0);
                sent++;
            }
            if (sent > 0)
                Log.LogInfo($"[P6.8] Broadcast GroundWeaponsInit to {sent} client(s), entries={_groundWeaponsEntryCount}");
        }

        /// <summary>
        /// Runs on the ORACLE after additive map scene settles. Clients already got MapChange;
        /// this wires server-side map objects (weapons in geometry, GhostPlatform, barrels, NSOs).
        /// </summary>
        internal void RunPostMapLoadServerInit(Scene scene)
        {
            try
            {
                if (scene.name == "MainScene" || string.IsNullOrEmpty(scene.name)) return;
                if (scene.buildIndex != _currentSceneIndex)
                {
                    Log.LogWarning($"[v26.6] PostMapLoad skip stale scene buildIndex={scene.buildIndex} expected={_currentSceneIndex} name='{scene.name}'");
                    return;
                }
                Log.LogInfo($"[v26.6] PostMapLoad init scene='{scene.name}' buildIndex={scene.buildIndex} matchScene={_currentSceneIndex}");
                ClearMapDataObjectsOnOracle();
                _mapAwakeRegisterCount = 0;
                try { InvokeMultiplayerManagerInitChain(); } catch { }
                EnsureMapSyncObjectsRegistered();
                InvokeCheckForGroundWeapons("post-map-settle");
                BroadcastGroundWeaponsToAllClients();
                foreach (var kv in _sfClients)
                    if (kv.Value.Spawned) SendCachedGroundWeaponsToClient(kv.Value);
                _groundWeaponsRetryAt = Time.realtimeSinceStartup + 3f;
                RearmOracleCombatLoop("PostMapLoad");
                try { InvokeOracleStartCountDown(); } catch (Exception e) { Log.LogWarning($"[P6.5] PostMapLoad StartCountDown: {e.Message}"); }
            }
            finally
            {
                FinishOracleMapLoad("PostMapLoad");
            }
        }

        internal void FinishOracleMapLoad(string reason)
        {
            _oracleMapLoadInProgress = false;
            _oracleMapLoadForceCompleteAt = -1f;
            _oracleMapLoadStartedAt = -1f;
            Log.LogInfo($"[v26.6] Oracle map load finished ({reason}) scene={_currentSceneIndex}");
            if (_roundAdvanceQueuedWhileLoading)
            {
                _roundAdvanceQueuedWhileLoading = false;
                if ((object)Instance != null)
                {
                    Log.LogInfo("[SF] Processing queued round advance after map load.");
                    Instance.TryScheduleRoundAdvance("queued-after-map-load");
                }
            }
        }

        /// <summary>When SceneManager does not re-fire loaded (same map reload), still complete init.</summary>
        internal void ForceCompleteOracleMapLoadIfNeeded(string reason)
        {
            if (!_oracleMapLoadInProgress) return;
            Scene scene;
            if (!TryFindLoadedSceneForCurrentMapIndex(out scene))
            {
                Log.LogWarning($"[v26.6] ForceCompleteMapLoad({reason}): no scene for index {_currentSceneIndex}");
                FinishOracleMapLoad(reason + "-no-scene");
                return;
            }
            Log.LogInfo($"[v26.6] ForceCompleteMapLoad({reason}) scene='{scene.name}' buildIndex={scene.buildIndex}");
            RunPostMapLoadServerInit(scene);
        }

        private bool TryFindLoadedSceneForCurrentMapIndex(out Scene scene)
        {
            scene = default(Scene);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded || s.name == "MainScene") continue;
                if (s.buildIndex == _currentSceneIndex)
                {
                    scene = s;
                    return true;
                }
            }
            return false;
        }

        internal void QueueRoundAdvanceWhileMapLoading()
        {
            _roundAdvanceQueuedWhileLoading = true;
            Log.LogInfo("[SF] Round advance queued — will run when oracle map load completes.");
        }

        internal bool IsOracleMapLoadInProgress() => _oracleMapLoadInProgress;

        internal void TickOracleMapLoadTimeout()
        {
            if (!_oracleMapLoadInProgress) return;
            float now = Time.realtimeSinceStartup;
            if (_oracleMapLoadForceCompleteAt > 0f && now >= _oracleMapLoadForceCompleteAt)
            {
                Log.LogWarning($"[v26.6] Map load force-complete after {OracleMapLoadForceCompleteSec:0.0}s (scene={_currentSceneIndex})");
                ForceCompleteOracleMapLoadIfNeeded("timeout");
            }
        }

        internal void ScheduleOracleReloadCurrentMap(string reason)
        {
            _oracleMapLoadInProgress = true;
            _oracleMapLoadStartedAt = Time.realtimeSinceStartup;
            _oracleMapLoadForceCompleteAt = Time.realtimeSinceStartup + OracleMapLoadForceCompleteSec;
            _mapAwakeRegisterCount = 0;
            _sceneLoadRealtime = Time.realtimeSinceStartup;
            _oracleStartMatchAt = Time.realtimeSinceStartup + 0.5f;
            _oracleStartMatchFired = false;
            _oracleCountDownAt = -1f;
            _oracleCountDownFired = false;
            _nsoInventoryAt = -1f;
            _nsoInventoryDone = false;
            _groundWeaponsRetryAt = Time.realtimeSinceStartup + 6f;
            Log.LogInfo($"[v26.6] Oracle will load additive scene {_currentSceneIndex} ({reason})");
        }

        private void TickGroundWeaponsRetry()
        {
            if (_groundWeaponsRetryAt < 0f) return;
            if (Time.realtimeSinceStartup < _groundWeaponsRetryAt) return;
            _groundWeaponsRetryAt = -1f;
            InvokeCheckForGroundWeapons("post-match-retry");
        }

        private void LogMapSyncDiagnostics(int posCount, int stateCount)
        {
            if (_mapSyncDiagNextAt < 0f) _mapSyncDiagNextAt = Time.realtimeSinceStartup + 5f;
            if (Time.realtimeSinceStartup < _mapSyncDiagNextAt) return;
            _mapSyncDiagNextAt = Time.realtimeSinceStartup + MapSyncLogIntervalSec;
            int ghost = 0, move = 0, pillar = 0, other = 0;
            foreach (var comp in EnumerateMapSyncComponents())
            {
                if ((object)comp == null) continue;
                string tn = comp.GetType().Name;
                if (tn.IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0) ghost++;
                else if (tn.IndexOf("MoveAlong", StringComparison.OrdinalIgnoreCase) >= 0) move++;
                else if (tn.IndexOf("Pillar", StringComparison.OrdinalIgnoreCase) >= 0) pillar++;
                else other++;
            }
            Log.LogInfo($"[v26.6] mapSync pos={posCount} state={stateCount} types ghost={ghost} move={move} pillar={pillar} other={other} registered={_mapSyncObjectsRegistered}");
        }

        private List<Component> EnumerateMapSyncComponents()
        {
            var list = new List<Component>();
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm != null)
                {
                    var dictF = AccessTools.Field(mm.GetType(), "mMapDataObjectToSync");
                    IDictionary dict = null;
                    if ((object)dictF != null) dict = dictF.GetValue(mm) as IDictionary;
                    if (dict != null)
                    {
                        foreach (DictionaryEntry e in dict)
                        {
                            var c = e.Value as Component;
                            if ((object)c != null) list.Add(c);
                        }
                    }
                }
                if (list.Count == 0 && (object)_mapSyncBaseType != null)
                {
                    var all = UnityEngine.Object.FindObjectsOfType(_mapSyncBaseType);
                    if (all != null)
                        foreach (var o in all)
                            if (o is Component c) list.Add(c);
                }
            }
            catch (Exception ex) { Log.LogWarning($"[v26.6] EnumerateMapSync: {ex.Message}"); }
            return list;
        }

        private void EnsureMapReflection()
        {
            if ((object)_mapSyncBaseType == null)
            {
                _mapSyncBaseType = AccessTools.TypeByName("MapInfoSyncableBase");
                if ((object)_mapSyncBaseType != null)
                {
                    _mapSyncStartPosField = AccessTools.Field(_mapSyncBaseType, "m_StartPos");
                    _mapGetDataMethod = AccessTools.Method(_mapSyncBaseType, "GetData");
                    _mapSetDataMethod = AccessTools.Method(_mapSyncBaseType, "SetData");
                    _mapNetworkControlField = AccessTools.Field(_mapSyncBaseType, "m_NetworkControl");
                }
            }
        }

        private Vector2 ReadMapSyncStartPos(Component comp)
        {
            var p = comp.transform.position;
            Vector2 sp = new Vector2(p.y, p.z);
            try
            {
                EnsureMapReflection();
                if (RefOk(_mapSyncStartPosField))
                    sp = (Vector2)_mapSyncStartPosField.GetValue(comp);
            }
            catch { }
            return QuantizeMapSyncKey(sp);
        }

        private List<MapStateSnap> CollectMapStateSnapshot()
        {
            var result = new List<MapStateSnap>();
            EnsureMapReflection();
            if (!RefOk(_mapGetDataMethod)) return result;
            foreach (var comp in EnumerateMapSyncComponents())
            {
                if (!RefOk(comp)) continue;
                try
                {
                    byte[] data = _mapGetDataMethod.Invoke(comp, null) as byte[];
                    if (data == null) data = new byte[0];
                    if (data.Length > MapStateMaxPayload)
                    {
                        var t = new byte[MapStateMaxPayload];
                        Buffer.BlockCopy(data, 0, t, 0, MapStateMaxPayload);
                        data = t;
                    }
                    Vector2 sp = ReadMapSyncStartPos(comp);
                    result.Add(new MapStateSnap { StartX = sp.x, StartY = sp.y, Data = data });
                }
                catch (Exception ex)
                {
                    if (result.Count == 0)
                        Log.LogWarning($"[v26.6 mapState collect] {ex.Message}");
                }
            }
            return result;
        }

        private static int MapStateSectionByteLen(List<MapStateSnap> entries)
        {
            int n = 2;
            if (entries == null) return n;
            foreach (var e in entries)
                n += 8 + 1 + (e.Data?.Length ?? 0);
            return n;
        }

        private static int WriteMapStateSection(byte[] body, int off, List<MapStateSnap> entries)
        {
            ushort count = (ushort)(entries?.Count ?? 0);
            WriteU16LE(body, off, count);
            off += 2;
            if (entries == null) return off;
            foreach (var e in entries)
            {
                WriteF32LE(body, off, e.StartX); off += 4;
                WriteF32LE(body, off, e.StartY); off += 4;
                byte len = (byte)Math.Min(MapStateMaxPayload, e.Data?.Length ?? 0);
                body[off++] = len;
                if (len > 0 && e.Data != null)
                    Buffer.BlockCopy(e.Data, 0, body, off, len);
                off += len;
            }
            return off;
        }

        private List<MapSyncSnap> CollectMapSyncSnapshot()
        {
            var result = new List<MapSyncSnap>();
            EnsureMapReflection();
            foreach (var comp in EnumerateMapSyncComponents())
            {
                if (!RefOk(comp)) continue;
                try
                {
                    var p = comp.transform.position;
                    if (p.y < -30f) continue;
                    Vector2 startPos = ReadMapSyncStartPos(comp);
                    result.Add(new MapSyncSnap
                    {
                        StartX = startPos.x, StartY = startPos.y,
                        X = p.x, Y = p.y, Z = p.z
                    });
                }
                catch (Exception ex)
                {
                    if (result.Count == 0)
                        Log.LogWarning($"[P0-14 mapSync collect] {ex.Message}");
                }
            }
            return result;
        }
    }
}
