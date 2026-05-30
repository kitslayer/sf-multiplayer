using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFClientRecon
{
    public partial class Plugin
    {
        private const float MapSyncKeyQuantum = 0.01f;
        private const float InputFastInterval = 1.0f / 90.0f;
        private float _lastFastInputAt;
        private List<MapStateSnapEntry> _pendingMapStateSnap;
        private MethodInfo _mapSetDataMethod;
        private int _mapStateApplied;

        private struct MapStateSnapEntry
        {
            public float StartX, StartY;
            public byte[] Data;
        }

        internal static Vector2 QuantizeMapSyncKeyClient(Vector2 v)
        {
            float invQ = 1f / MapSyncKeyQuantum;
            return new Vector2(
                Mathf.Round(v.x * invQ) / invQ,
                Mathf.Round(v.y * invQ) / invQ);
        }

        private void InstallClientTerrainPatches()
        {
            try
            {
                // NSO stay kinematic on client (stock SF). Skipping DisableAllRigidBodies
                // caused NSO-on-NSO collisions → lag + ice breaking alone (P0-5 regression).
                var harmony = new Harmony(PluginGuid + ".terrain-client");
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType != null)
                {
                    var pick = AccessTools.Method(mmType, "RequestWeaponPickUp");
                    if ((object)pick != null)
                    {
                        harmony.Patch(pick, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(RequestWeaponPickUpPostfix))));
                        Log.LogInfo("[v26.6] Patched RequestWeaponPickUp postfix (pickup latency log).");
                    }
                    var thr = AccessTools.Method(mmType, "RequestWeaponThrow");
                    if ((object)thr != null)
                    {
                        harmony.Patch(thr, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(RequestWeaponThrowPostfix))));
                        Log.LogInfo("[v26.6] Patched RequestWeaponThrow postfix.");
                    }
                }
                InstallMapInfoSyncQuantizeClient();
                if ((object)mmType != null)
                {
                    var onMap = AccessTools.Method(mmType, "OnMapChanged");
                    if ((object)onMap != null)
                    {
                        harmony.Patch(onMap, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(OnMapChangedInvalidateCachePostfix))));
                        Log.LogInfo("[v26.6] Patched OnMapChanged (invalidate mapSync cache).");
                    }
                    var onGw = AccessTools.Method(mmType, "OnGroundWeaponsInit");
                    if ((object)onGw != null)
                    {
                        // PREFIX (replace), not postfix. Vanilla OnGroundWeaponsInit
                        // throws NullReferenceException when a weapon position in the
                        // server's list doesn't match a pre-placed GroundWeapon in
                        // the client scene (stale/accumulated/off-map entries). That
                        // NRE aborts the method AND prevents any postfix from running
                        // → zero map weapons appeared. Running our own fuzzy matcher
                        // as a skip-original prefix avoids the crash and spawns every
                        // weapon we CAN match by position.
                        harmony.Patch(onGw, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(OnGroundWeaponsInit_FuzzyPrefix))));
                        Log.LogInfo("[v26.6] Patched OnGroundWeaponsInit (fuzzy map weapons, replace vanilla).");
                    }
                    var onMapInfo = AccessTools.Method(mmType, "OnMapInfoRecieved");
                    if ((object)onMapInfo != null)
                    {
                        harmony.Patch(onMapInfo, prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(OnMapInfoRecievedClientPrefix))));
                        Log.LogInfo("[v0.3.3] Patched OnMapInfoRecieved (fan-out MapInfo to all tags).");
                    }
                }
                var codeAnimType = AccessTools.TypeByName("CodeAnimation");
                if ((object)codeAnimType != null)
                {
                    var recv = AccessTools.Method(codeAnimType, "RecieveMapInfo");
                    if ((object)recv != null)
                    {
                        harmony.Patch(recv, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(CodeAnimationRecieveMapInfoPostfix))));
                        Log.LogInfo("[v0.3.3] Patched CodeAnimation.RecieveMapInfo (restart animation).");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[v26.6] client terrain patches: {e.Message}"); }
        }

        internal static bool OnMapInfoRecievedClientPrefix(byte[] data)
        {
            if (data == null || data.Length < 1) return true;
            try
            {
                var tagType = AccessTools.TypeByName("MapInfoOnlineTag");
                if ((object)tagType == null) return true;
                var tags = UnityEngine.Object.FindObjectsOfType(tagType);
                if (tags == null || tags.Length == 0) return true;
                foreach (var tag in tags)
                {
                    var comp = tag as Component;
                    if ((object)comp == null) continue;
                    comp.SendMessage("RecieveMapInfo", data, SendMessageOptions.DontRequireReceiver);
                }
                return false;
            }
            catch { return true; }
        }

        internal static void CodeAnimationRecieveMapInfoPostfix(object __instance)
        {
            if ((object)__instance == null) return;
            try
            {
                if ((object)Instance != null)
                    Instance.StartCoroutine(PlayCodeAnimationWhenFightReady(__instance));
            }
            catch { }
        }

        private static IEnumerator PlayCodeAnimationWhenFightReady(object animInstance)
        {
            var gmType = AccessTools.TypeByName("GameManager");
            var loadingF = RefOk(gmType) ? AccessTools.Field(gmType, "isLoading") : null;
            var inFightF = RefOk(gmType) ? AccessTools.Field(gmType, "inFight") : null;
            for (int i = 0; i < 80; i++)
            {
                object gm = null;
                if (RefOk(gmType))
                {
                    var ig = AccessTools.PropertyGetter(gmType, "Instance");
                    if (RefOk(ig)) gm = ig.Invoke(null, null);
                }
                bool loading = RefOk(loadingF) && RefOk(gm) && (bool)loadingF.GetValue(gm);
                bool inFight = RefOk(inFightF) && RefOk(gm) && (bool)inFightF.GetValue(gm);
                if (!loading && inFight) break;
                yield return new WaitForSeconds(0.1f);
            }
            try
            {
                var play = AccessTools.Method(animInstance.GetType(), "Play");
                if (RefOk(play)) play.Invoke(animInstance, null);
            }
            catch { }
        }

        internal static void OnMapChangedInvalidateCachePostfix(byte[] data)
        {
            if (Instance == null) return;
            Instance._mapSyncCache.Clear();
            Instance._mapSyncTargets.Clear();
            Instance._mapSyncCacheRebuildAt = 0;
            Instance._mapStateApplied = 0;
            _countDownDeferred = false;
            Instance.StartCoroutine(Instance.RebuildMapCachesAfterLoadCoroutine());
            try { Instance.StartCoroutine(Instance.ForceMapLoadWatchdog(data)); }
            catch (Exception e) { Log.LogWarning($"[map-watchdog] start: {e.Message}"); }
        }

        // If the vanilla StartMapSequence gate skips LoadMapCourotine (download flags),
        // isLoading stays true forever and the view never switches. Force the load.
        private IEnumerator ForceMapLoadWatchdog(byte[] data)
        {
            var gmType = AccessTools.TypeByName("GameManager");
            if ((object)gmType == null) yield break;
            var instProp = AccessTools.Property(gmType, "Instance");
            var loadingF = AccessTools.Field(gmType, "isLoading");
            if ((object)instProp == null || (object)loadingF == null) yield break;

            // give the vanilla path a chance first
            float waited = 0f;
            while (waited < 6f)
            {
                yield return new WaitForSeconds(0.5f);
                waited += 0.5f;
                object gm0 = instProp.GetValue(null, null);
                if ((object)gm0 != null && !(bool)loadingF.GetValue(gm0)) yield break; // loaded fine
            }

            object gm = instProp.GetValue(null, null);
            if ((object)gm == null || !(bool)loadingF.GetValue(gm)) yield break;

            // Still stuck: force the load ourselves.
            Log.LogWarning("[map-watchdog] isLoading stuck >6s — forcing LoadMapCourotine.");
            try
            {
                byte mapType = data != null && data.Length >= 2 ? data[1] : (byte)0;
                byte[] mapData = new byte[(data != null ? data.Length : 0) - 2 < 0 ? 0 : (data.Length - 2)];
                if (data != null && data.Length > 2) Array.Copy(data, 2, mapData, 0, data.Length - 2);

                var mwType = AccessTools.TypeByName("MapWrapper");
                object mw = Activator.CreateInstance(mwType);
                AccessTools.Field(mwType, "MapType").SetValue(mw, mapType);
                AccessTools.Field(mwType, "MapData").SetValue(mw, mapData);

                var loadCo = AccessTools.Method(gmType, "LoadMapCourotine");
                loadCo.Invoke(gm, new object[] { mw });
                Log.LogInfo($"[map-watchdog] Forced LoadMapCourotine (mapType={mapType}, dataLen={mapData.Length}).");
            }
            catch (Exception e)
            {
                var real = e.InnerException ?? e;
                Log.LogWarning($"[map-watchdog] force load threw: {real.GetType().Name}: {real.Message}");
            }
        }

        private IEnumerator RebuildMapCachesAfterLoadCoroutine()
        {
            for (int pass = 0; pass < 5; pass++)
            {
                yield return new WaitForSeconds(1.5f + pass * 0.5f);
                RegisterClientPreSpawnedWeapons();
                RebuildMapSyncCacheFromScene();
            }
        }

        private void RebuildMapSyncCacheFromScene()
        {
            try
            {
                if ((object)_mapSyncBaseType == null)
                {
                    _mapSyncBaseType = AccessTools.TypeByName("MapInfoSyncableBase");
                    if ((object)_mapSyncBaseType == null) return;
                    _mapSyncStartPosField = AccessTools.Field(_mapSyncBaseType, "m_StartPos");
                }
                _mapSyncCache.Clear();
                int n = 0;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var sc = SceneManager.GetSceneAt(i);
                    if (!sc.isLoaded || sc.name == "MainScene") continue;
                    foreach (var root in sc.GetRootGameObjects())
                    {
                        var all = root.GetComponentsInChildren(_mapSyncBaseType, true);
                        if (all == null) continue;
                        foreach (var obj in all)
                        {
                            var c = obj as Component;
                            if ((object)c == null) continue;
                            Vector2 key = QuantizeMapSyncKeyClient((Vector2)_mapSyncStartPosField.GetValue(obj));
                            _mapSyncCache[key] = c;
                            n++;
                        }
                    }
                }
                _mapSyncCacheRebuildAt = 120;
                if (n > 0 && (_mapStateApplied == 0 || n != _mapSyncCache.Count))
                    Log.LogInfo($"[v26.6] Client mapSync cache rebuilt: {n} MapInfoSyncableBase");
            }
            catch (Exception ex) { Log.LogWarning($"[v26.6] RebuildMapSyncCache: {ex.Message}"); }
        }

        private void RegisterClientPreSpawnedWeapons()
        {
            try
            {
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                var wpType = AccessTools.TypeByName("WeaponPickUp");
                if ((object)mmType == null || (object)wpType == null) return;
                var mm = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)mm == null) return;
                var add = AccessTools.Method(mmType, "AddPreSpawnedWeapon");
                if ((object)add == null) return;
                int n = 0;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var sc = SceneManager.GetSceneAt(i);
                    if (!sc.isLoaded || sc.name == "MainScene") continue;
                    foreach (var root in sc.GetRootGameObjects())
                    {
                        var all = root.GetComponentsInChildren(wpType, true);
                        if (all == null) continue;
                        foreach (var wp in all)
                        {
                            var comp = wp as Component;
                            if ((object)comp == null) continue;
                            var p = comp.transform.position;
                            add.Invoke(mm, new object[] { new Vector2(p.y, p.z), wp });
                            n++;
                        }
                    }
                }
                if (n > 0 && _groundWeaponRegisterLog < 3)
                {
                    _groundWeaponRegisterLog++;
                    Log.LogInfo($"[v26.6] Client pre-spawned weapons registered: {n}");
                }
            }
            catch (Exception ex) { Log.LogWarning($"[v26.6] RegisterClientPreSpawnedWeapons: {ex.Message}"); }
        }
        private int _groundWeaponRegisterLog;

        /// <summary>Server keys often miss by ULP — match nearest WeaponPickUp in YZ plane.</summary>
        // Returns false → skip vanilla OnGroundWeaponsInit (which NREs on
        // unmatched positions). We do the position matching ourselves, tolerant
        // of stale/off-map entries.
        internal static bool OnGroundWeaponsInit_FuzzyPrefix(byte[] data)
        {
            if (Instance == null || data == null || data.Length < 2) return false;
            try { Instance.ApplyGroundWeaponsFuzzy(data); }
            catch (Exception ex) { Log.LogWarning($"[v26.6] GroundWeapons fuzzy: {ex.Message}"); }
            return false;
        }

        private void ApplyGroundWeaponsFuzzy(byte[] data)
        {
            var mmType = AccessTools.TypeByName("MultiplayerManager");
            var wpType = AccessTools.TypeByName("WeaponPickUp");
            if ((object)mmType == null || (object)wpType == null) return;
            var mm = UnityEngine.Object.FindObjectOfType(mmType);
            if ((object)mm == null) return;
            var onSpawned = AccessTools.Method(mmType, "OnWeaponSpawned");
            if ((object)onSpawned == null) return;

            var pickups = new List<Component>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded || sc.name == "MainScene") continue;
                foreach (var root in sc.GetRootGameObjects())
                {
                    var all = root.GetComponentsInChildren(wpType, true);
                    if (all == null) continue;
                    foreach (var wp in all)
                    {
                        var c = wp as Component;
                        if ((object)c != null) pickups.Add(c);
                    }
                }
            }
            var used = new HashSet<int>();
            int applied = 0;
            using (var input = new MemoryStream(data))
            using (var reader = new BinaryReader(input))
            {
                ushort count = reader.ReadUInt16();
                for (int i = 0; i < count; i++)
                {
                    if (input.Position + 12 > data.Length) break;
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    ushort weaponId = reader.ReadUInt16();
                    ushort syncIdx = reader.ReadUInt16();
                    var target = new Vector2(x, y);
                    Component best = null;
                    float bestD = 2.5f;
                    foreach (var comp in pickups)
                    {
                        if ((object)comp == null || used.Contains(comp.GetInstanceID())) continue;
                        var p = comp.transform.position;
                        float d = Vector2.Distance(new Vector2(p.y, p.z), target);
                        if (d < bestD) { bestD = d; best = comp; }
                    }
                    if ((object)best == null) continue;
                    used.Add(best.GetInstanceID());
                    onSpawned.Invoke(mm, new object[] { best, weaponId, syncIdx, true });
                    applied++;
                }
            }
            if (applied > 0 && _groundWeaponFuzzyLog < 5)
            {
                _groundWeaponFuzzyLog++;
                Log.LogInfo($"[v26.6] GroundWeaponsInit fuzzy applied {applied} map weapon(s)");
            }
        }
        private int _groundWeaponFuzzyLog;

        private static bool _mapSyncQuantizeClientInstalled;
        private static void InstallMapInfoSyncQuantizeClient()
        {
            if (_mapSyncQuantizeClientInstalled) return;
            _mapSyncQuantizeClientInstalled = true;
            try
            {
                var mgrType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mgrType == null) return;
                var harmony = new Harmony(PluginGuid + ".mapsync-quantize-client");
                var addM = AccessTools.Method(mgrType, "AddMapDataObject");
                if ((object)addM != null)
                    harmony.Patch(addM, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Plugin), nameof(AddMapDataObjectPrefixClient))));
                var recvM = AccessTools.Method(mgrType, "OnMapDataRecieved");
                if ((object)recvM != null)
                    harmony.Patch(recvM, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(Plugin), nameof(OnMapDataRecievedPrefixClient))));
            }
            catch { }
        }

        internal static bool AddMapDataObjectPrefixClient(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2 || !(__args[0] is Vector2 pos)) return true;
                var q = QuantizeMapSyncKeyClient(pos);
                __args[0] = q;
                if (__args[1] != null)
                {
                    var f = AccessTools.Field(__args[1].GetType(), "m_StartPos");
                    if ((object)f != null) f.SetValue(__args[1], q);
                }
            }
            catch { }
            return true;
        }

        internal static bool OnMapDataRecievedPrefixClient(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 8) return true;
                float x = BitConverter.ToSingle(data, 0);
                float y = BitConverter.ToSingle(data, 4);
                var q = QuantizeMapSyncKeyClient(new Vector2(x, y));
                var xb = BitConverter.GetBytes(q.x);
                var yb = BitConverter.GetBytes(q.y);
                Buffer.BlockCopy(xb, 0, data, 0, 4);
                Buffer.BlockCopy(yb, 0, data, 4, 4);
            }
            catch { }
            return true;
        }

        internal static void RequestWeaponPickUpPostfix()
        {
            if (_pickupLatencyLogged < 3)
            {
                _pickupLatencyLogged++;
                Log.LogInfo($"[v26.6] RequestWeaponPickUp local apply #{_pickupLatencyLogged}");
            }
        }
        private static int _pickupLatencyLogged;

        internal static void RequestWeaponThrowPostfix()
        {
            if (Instance != null) Instance._lastFastInputAt = 0f;
        }

        private void ParseMapStateSection(byte[] pkt, ref int o, int bodyEnd, out List<MapStateSnapEntry> list)
        {
            list = null;
            if (o + 2 > bodyEnd) return;
            ushort count = (ushort)(pkt[o] | (pkt[o + 1] << 8));
            o += 2;
            list = new List<MapStateSnapEntry>(count);
            for (int i = 0; i < count; i++)
            {
                if (o + 9 > bodyEnd) break;
                float sx = BitConverter.ToSingle(pkt, o);
                float sy = BitConverter.ToSingle(pkt, o + 4);
                byte dlen = pkt[o + 8];
                o += 9;
                if (o + dlen > bodyEnd) break;
                var data = new byte[dlen];
                if (dlen > 0) Buffer.BlockCopy(pkt, o, data, 0, dlen);
                o += dlen;
                list.Add(new MapStateSnapEntry { StartX = sx, StartY = sy, Data = data });
            }
        }

        private void ApplyMapStateSnapshot(List<MapStateSnapEntry> snap)
        {
            if (snap == null || snap.Count == 0) return;
            try
            {
                if ((object)_mapSyncBaseType == null)
                {
                    _mapSyncBaseType = AccessTools.TypeByName("MapInfoSyncableBase");
                    if ((object)_mapSyncBaseType == null) return;
                    _mapSyncStartPosField = AccessTools.Field(_mapSyncBaseType, "m_StartPos");
                    _mapSetDataMethod = AccessTools.Method(_mapSyncBaseType, "SetData");
                }
                if ((object)_mapSetDataMethod == null) return;
                if (_mapSyncCache.Count == 0 || _mapSyncCacheRebuildAt <= 0)
                {
                    _mapSyncCache.Clear();
                    var all = UnityEngine.Object.FindObjectsOfType(_mapSyncBaseType);
                    if (all != null)
                    {
                        foreach (var obj in all)
                        {
                            var c = obj as Component;
                            if ((object)c == null) continue;
                            Vector2 key = QuantizeMapSyncKeyClient((Vector2)_mapSyncStartPosField.GetValue(obj));
                            _mapSyncCache[key] = c;
                        }
                    }
                    _mapSyncCacheRebuildAt = 60;
                }
                int applied = 0;
                foreach (var e in snap)
                {
                    Vector2 key = QuantizeMapSyncKeyClient(new Vector2(e.StartX, e.StartY));
                    if (!_mapSyncCache.TryGetValue(key, out var comp) || (object)comp == null) continue;
                    _mapSetDataMethod.Invoke(comp, new object[] { e.Data });
                    applied++;
                }
                _mapStateApplied++;
                if (_mapStateApplied == 1 || _mapStateApplied % 60 == 0)
                    Log.LogInfo($"[v26.6] MapState SetData applied {applied}/{snap.Count} cache={_mapSyncCache.Count}");
            }
            catch (Exception ex) { Log.LogWarning($"[v26.6 MapState] {ex.Message}"); }
        }

        private void TickFastCombatInput()
        {
            if (Time.realtimeSinceStartup - _lastFastInputAt < InputFastInterval) return;
            if (!Input.GetKey(KeyCode.Q) && !Input.GetMouseButton(0)) return;
            _lastFastInputAt = Time.realtimeSinceStartup;
            SendPlayerInputPacket();
        }

        internal static bool IsIceDestructibleTarget(MonoBehaviour piece)
        {
            if ((object)piece == null) return false;
            if ((object)_clientDpType == null)
            {
                _clientDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_clientDpType != null)
                {
                    _clientDpSimpleField = AccessTools.Field(_clientDpType, "simpleDestruction");
                    _clientDpEventField = AccessTools.Field(_clientDpType, "eventDestruction");
                }
            }
            if ((object)_clientDpSimpleField == null) return false;
            bool simple = (bool)_clientDpSimpleField.GetValue(piece);
            bool ev = (object)_clientDpEventField != null && (bool)_clientDpEventField.GetValue(piece);
            return simple && !ev;
        }

        internal static bool IsLocalPlayerCollisionRigidbody(Rigidbody rb)
        {
            if ((object)rb == null) return false;
            var ctrlType = AccessTools.TypeByName("Controller");
            if ((object)ctrlType == null) return false;
            var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
            if ((object)hasCtrlF == null) return false;
            var root = rb.transform.root;
            foreach (var c in root.GetComponentsInChildren(ctrlType))
            {
                if ((bool)hasCtrlF.GetValue(c)) return true;
            }
            return false;
        }
    }
}
