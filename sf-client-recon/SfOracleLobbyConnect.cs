using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    /// <summary>
    /// When launched with -address/-port, Quick Match / Host Match must not call Steam matchmaking
    /// (hangs). Connect UDP to the oracle and enter the lobby hub like OnSocketServerJoined.
    /// </summary>
    public partial class Plugin
    {
        private static bool _oracleConnectMode;
        private static bool _oracleLobbyPatchesInstalled;
        private static bool _oracleAutoConnectStarted;
        private static bool _oracleConnectStarted;
        private static Type _pktType;
        private static Type _mmType;
        private static Type _mhType;
        private static Type _gmType;

        private static void DetectOracleConnectMode()
        {
            if (_oracleConnectMode) return;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-address" && !string.IsNullOrEmpty(args[i + 1]))
                {
                    _oracleConnectMode = true;
                    break;
                }
            }
        }

        internal void InstallOracleLobbyConnectPatches()
        {
            DetectOracleConnectMode();
            if (!_oracleConnectMode || _oracleLobbyPatchesInstalled) return;
            _oracleLobbyPatchesInstalled = true;
            try
            {
                _pktType = AccessTools.TypeByName("P2PPackageHandler");
                _mmType = AccessTools.TypeByName("MultiplayerManager");
                _mhType = AccessTools.TypeByName("MatchmakingHandler");
                _gmType = AccessTools.TypeByName("GameManager");
                if (!RefOk(_pktType) || !RefOk(_mmType) || !RefOk(_mhType))
                {
                    Log.LogWarning("[oracle-lobby] Game types not found — is Assembly-CSharp.srv installed?");
                    return;
                }

                var harmony = new Harmony(PluginGuid + ".oracle-lobby");
                var joinRandom = AccessTools.Method(_mhType, "JoinRandomServer");
                if (RefOk(joinRandom))
                    harmony.Patch(joinRandom, prefix: new HarmonyMethod(typeof(Plugin), nameof(JoinRandomServer_OraclePrefix)));
                var createLobby = AccessTools.Method(_mhType, "CreateSteamLobby");
                if (RefOk(createLobby))
                    harmony.Patch(createLobby, prefix: new HarmonyMethod(typeof(Plugin), nameof(CreateSteamLobby_OraclePrefix)));

                var pktUpdate = AccessTools.Method(_pktType, "Update");
                if (RefOk(pktUpdate))
                    harmony.Patch(pktUpdate, postfix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_Update_OraclePostfix)));
                var checkMsg = AccessTools.Method(_pktType, "CheckMessageType");
                if (RefOk(checkMsg))
                    harmony.Patch(checkMsg, prefix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_CheckMessageType_OraclePrefix)));
                Log.LogInfo($"[oracle-diag] patch attach: Update={RefOk(pktUpdate)} CheckMessageType={RefOk(checkMsg)}");

                // ReadMessageBuffer runs BEFORE the init-flag guard that can drop
                // MapChange. Prefix it to (a) log every inbound msgType so we can
                // see exactly what the game socket receives, and (b) force the
                // init flag true so the guard never drops a server packet.
                var readBuf = AccessTools.Method(_pktType, "ReadMessageBuffer");
                if (RefOk(readBuf))
                    harmony.Patch(readBuf, prefix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_ReadMessageBuffer_OraclePrefix)));
                Log.LogInfo($"[oracle-diag] patch attach: ReadMessageBuffer={RefOk(readBuf)}");

                var checkReady = AccessTools.Method(_mmType, "CheckReadyPlayers");
                if (RefOk(checkReady))
                    harmony.Patch(checkReady, prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckReadyPlayers_OraclePrefix)));

                var onMatchStart = AccessTools.Method(_mmType, "OnMatchStart");
                if (RefOk(onMatchStart))
                    harmony.Patch(onMatchStart, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnMatchStart_OraclePostfix)));

                // NOTE: Do NOT patch GameManager.StartMatch / StartCountDown.
                // On this Mono 2.0 runtime the HarmonyX-generated DMD wrapper for
                // StartMatch references System.Array.Empty (missing in Mono 2.0),
                // throwing MissingMethodException inside StartMatch → the map-load
                // coroutine (StartMapSequence) never runs → isLoading stays true
                // → the client never switches from lobby to the map. Leaving these
                // methods unpatched lets the vanilla map-load run natively.

                Log.LogInfo("[oracle-lobby] Patched Quick/Host Match → UDP oracle (no Steam lobby).");
                ScheduleOracleAutoConnect();
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] patch failed: {e.Message}"); }
        }

        private void ScheduleOracleAutoConnect()
        {
            // DISABLED. Auto-connecting at boot (while still on the main menu)
            // pre-joins the oracle and poisons SF's network state, so the user's
            // real QUICK MATCH / HOST click can no longer send a clean lobby
            // handshake → the game socket never re-handshakes and the player
            // can't enter the server. Connect ONLY via the QuickMatch/HostMatch
            // button prefixes (the path that always worked). No timer scheduled.
            _oracleAutoConnectStarted = true;
            _autoConnectAt = -1f;
        }

        private static float _autoConnectAt = -1f;

        // Ticked every frame from Update(). Fires the oracle lobby connect once,
        // 2.5s after Awake, if we haven't already started connecting. Uses only
        // reflection that takes explicit args (no zero-arg params → no Array.Empty).
        internal void TickOracleAutoConnect()
        {
            if (!_oracleConnectMode || _autoConnectAt < 0f) return;
            if (Time.realtimeSinceStartup < _autoConnectAt) return;
            _autoConnectAt = -1f;
            if (_oracleConnectStarted) return;   // already connecting/connected
            try
            {
                var pkt = GetPktHandler();
                if (RefOk(pkt) && RefOk(_pktType))
                {
                    var f = AccessTools.Field(_pktType, "mHasSentOrReceived");
                    if (RefOk(f) && (bool)f.GetValue(pkt)) return;   // already in a game
                }
            }
            catch { }
            Log.LogInfo("[oracle-lobby] Auto-connect → oracle (tienes -address/-port).");
            BeginOracleLobbyConnect("AutoConnect");
        }

        internal static bool JoinRandomServer_OraclePrefix()
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect("QuickMatch");
            return false;
        }

        internal static bool CreateSteamLobby_OraclePrefix(int maxPlayers, bool privateLobby)
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect(privateLobby ? "HostMatch-private" : "HostMatch");
            return false;
        }

        private static void BeginOracleLobbyConnect(string source)
        {
            try
            {
                _oracleConnectStarted = true;
                Log.LogInfo($"[oracle-lobby] {source} → connecting to dedicated server (no Steam).");
                var pkt = GetPktHandler();
                if (!RefOk(pkt))
                {
                    Log.LogError("[oracle-lobby] P2PPackageHandler missing.");
                    return;
                }
                var init = AccessTools.Method(_pktType, "Init");
                if (RefOk(init)) init.Invoke(pkt, null);

                var setNet = AccessTools.Method(_mhType, "SetNetworkMatch", new[] { typeof(bool) });
                if (RefOk(setNet)) setNet.Invoke(null, new object[] { true });

                var setLobbyType = AccessTools.Method(_mhType, "SetNewLobbyType");
                if (RefOk(setLobbyType))
                {
                    var lobbyTypeEnum = AccessTools.TypeByName("ELobbyType");
                    if (RefOk(lobbyTypeEnum))
                    {
                        object publicLobby = Enum.ToObject(lobbyTypeEnum, 2);
                        setLobbyType.Invoke(null, new object[] { publicLobby });
                    }
                }

                var mm = UnityEngine.Object.FindObjectOfType(_mmType);
                if (!RefOk(mm))
                {
                    Log.LogError("[oracle-lobby] MultiplayerManager not found in scene.");
                    return;
                }
                var onJoined = AccessTools.Method(_mmType, "OnSocketServerJoined");
                if (RefOk(onJoined))
                    onJoined.Invoke(mm, null);
                else
                {
                    var onScene = AccessTools.Method(_mmType, "OnSceneStarted");
                    if (RefOk(onScene)) onScene.Invoke(mm, null);
                }
                // OnSocketServerJoined (the joiner path) never sets
                // mHasBeenInitializedFromServer; only the host path and the very
                // end of OnClientInit do. ReadMessageBuffer drops EVERY gameplay
                // packet (MapChange, MapInfo, ObjectUpdate...) — only ClientInit/
                // ClientAccepted are exempt — until that flag is true. So the
                // client spawns but never changes map. Force it true here.
                ForceInitializedFromServer(mm);
            }
            catch (Exception e) { Log.LogError($"[oracle-lobby] BeginOracleLobbyConnect: {e.Message}"); }
        }

        private static object GetPktHandler()
        {
            var prop = AccessTools.Property(_pktType, "Instance");
            if (RefOk(prop)) return prop.GetValue(null, null);
            return UnityEngine.Object.FindObjectOfType(_pktType);
        }

        // Set MultiplayerManager.mHasBeenInitializedFromServer = true. The packet
        // dispatcher (ReadMessageBuffer) gates every non-init packet on this flag
        // via mNetworkHandler.HasBeenInitializedFromServer, and the joiner path
        // (OnSocketServerJoined) never sets it. Without this, MapChange is dropped.
        private static float _lastForceDiagAt = -1f;
        internal static void ForceInitializedFromServer(object mm)
        {
            try
            {
                // _mmType is normally set by the connect-mode install, but that
                // only runs when launched with -address. Server-browser joins
                // skip it, so resolve the type here too.
                if (!RefOk(_mmType)) _mmType = AccessTools.TypeByName("MultiplayerManager");
                if (!RefOk(_mmType)) return;
                if (!RefOk(mm)) mm = UnityEngine.Object.FindObjectOfType(_mmType);
                if (!RefOk(mm))
                {
                    if (Time.realtimeSinceStartup - _lastForceDiagAt > 3f)
                    {
                        _lastForceDiagAt = Time.realtimeSinceStartup;
                        Log.LogWarning("[oracle-diag] ForceInitializedFromServer: MultiplayerManager instance NOT found.");
                    }
                    return;
                }
                var f = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                if (!RefOk(f))
                {
                    if (Time.realtimeSinceStartup - _lastForceDiagAt > 3f)
                    {
                        _lastForceDiagAt = Time.realtimeSinceStartup;
                        Log.LogWarning("[oracle-diag] ForceInitializedFromServer: field mHasBeenInitializedFromServer NOT found.");
                    }
                    return;
                }
                if (!(bool)f.GetValue(mm))
                {
                    f.SetValue(mm, true);
                    Log.LogInfo("[oracle-lobby] Forced mHasBeenInitializedFromServer=true (unblocks MapChange/MapInfo).");
                }
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] ForceInitializedFromServer: {e.Message}"); }
        }

        internal static void P2PPackageHandler_Update_OraclePostfix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return;
            try
            {
                var t = Traverse.Create(__instance);
                if (t.Field<bool>("mPauseTraffic").Value) return;
                if (!t.Field<bool>("mHasHandler").Value) return;
                // Only re-assert the init flag once we've actually begun an
                // oracle connection. Setting it in the main menu (before join)
                // breaks the handshake — the client skips ClientRequesting* and
                // goes silent. Gate on _oracleConnectStarted.
                if (_oracleConnectStarted) ForceInitializedFromServer(null);
                t.Method("CheckForPackagesOnChannel", new object[] { 1, false }).GetValue();
                t.Method("CheckForPackagesOnChannel", new object[] { 0, false }).GetValue();
                t.Field("mHasSentOrReceived").SetValue(true);
            }
            catch { }
        }

        internal static bool P2PPackageHandler_CheckMessageType_OraclePrefix(object __instance, byte[] data, object type, object steamIdRemote)
        {
            if (!_oracleConnectMode) return true;
            byte t = 255;
            if (type is byte b) t = b;
            else if (type != null && byte.TryParse(type.ToString(), out var parsed)) t = parsed;
            // DIAG: surface map/start traffic + the init-flag state at receive time
            // so we can tell whether MapChange(18)/StartMatch(35) actually reach
            // the game socket and whether the guard would drop them.
            if (t == 18 || t == 35 || t == 4 || t == 5)
            {
                bool initFlag = false;
                try
                {
                    if (!RefOk(_mmType)) _mmType = AccessTools.TypeByName("MultiplayerManager");
                    var mm = UnityEngine.Object.FindObjectOfType(_mmType);
                    var f = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                    if (RefOk(mm) && RefOk(f)) initFlag = (bool)f.GetValue(mm);
                }
                catch { }
                Log.LogInfo($"[oracle-diag] CheckMessageType got t={t} initFromServer={initFlag}");
            }
            if (t == 3) return false;
            return true;
        }

        // Runs before the HasBeenInitializedFromServer guard. Logs every inbound
        // msgType (so we can see whether MapChange(18) actually reaches the game
        // socket) and force-clears the guard so no server packet is dropped.
        internal static void P2PPackageHandler_ReadMessageBuffer_OraclePrefix(byte[] rawData)
        {
            if (!_oracleConnectMode) return;
            try
            {
                ForceInitializedFromServer(null);
                if (rawData != null && rawData.Length >= 5)
                {
                    byte mt = rawData[4];
                    Log.LogInfo($"[oracle-diag] ReadMessageBuffer inbound msgType={mt} len={rawData.Length}");
                }
            }
            catch { }
        }

        internal static bool CheckReadyPlayers_OraclePrefix() => !_oracleConnectMode;

        internal static void OnMatchStart_OraclePostfix()
        {
            if (!_oracleConnectMode) return;
            try
            {
                if (RefOk(_gmType))
                {
                    var stillMenuF = AccessTools.Field(_gmType, "stillInMenu");
                    var inst = AccessTools.Property(_gmType, "Instance")?.GetValue(null, null)
                        ?? UnityEngine.Object.FindObjectOfType(_gmType);
                    if (RefOk(inst) && RefOk(stillMenuF))
                        stillMenuF.SetValue(inst, false);
                }
                Log.LogInfo("[oracle-lobby] OnMatchStart (countdown deferred until map load if needed).");
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] OnMatchStart postfix: {e.Message}"); }
        }

        internal static bool _countDownDeferred;
        // Re-entry guard: when the deferred coroutine re-invokes StartCountDown,
        // vanilla SF flips isLoading=true again while it loads pumpkin/boss
        // assets. Without this flag the prefix re-defers → coroutine re-invokes
        // → infinite loop (visible in client log as alternating "after map
        // load" / "deferred — map still loading" forever).
        internal static bool _countDownBypassPrefix;

        /// <summary>Halloween/boss: StartCountDown while isLoading skips pumpkin spawn — wait for load.</summary>
        internal static bool GameManager_StartCountDown_OraclePrefix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return true;
            if (_countDownBypassPrefix) return true;  // re-entry from coroutine — let original run
            try
            {
                var loadingF = AccessTools.Field(__instance.GetType(), "isLoading");
                if (!RefOk(loadingF) || !(bool)loadingF.GetValue(__instance)) return true;
                if (_countDownDeferred) return false;
                if (Instance != null)
                {
                    _countDownDeferred = true;
                    Instance.StartCoroutine(DeferredStartCountDownWhenLoaded(__instance));
                    Log.LogInfo("[oracle-lobby] StartCountDown deferred — map still loading (boss/Halloween).");
                }
                return false;
            }
            catch { return true; }
        }

        private static IEnumerator DeferredStartCountDownWhenLoaded(object gmInst)
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            var loadingF = RefOk(_gmType) ? AccessTools.Field(_gmType, "isLoading") : null;
            while (Time.realtimeSinceStartup < deadline)
            {
                bool loading = RefOk(loadingF) && RefOk(gmInst) && (bool)loadingF.GetValue(gmInst);
                if (!loading) break;
                yield return null;
            }
            _countDownDeferred = false;
            try
            {
                var m = AccessTools.Method(_gmType, "StartCountDown");
                if (RefOk(m) && RefOk(gmInst))
                {
                    _countDownBypassPrefix = true;
                    try { m.Invoke(gmInst, null); }
                    finally { _countDownBypassPrefix = false; }
                    Log.LogInfo("[oracle-lobby] StartCountDown after map load (pumpkin/boss hooks).");
                }
            }
            catch (Exception e)
            {
                var real = e.InnerException ?? e;
                Log.LogWarning($"[oracle-diag] deferred StartCountDown threw: {real.GetType().Name}: {real.Message}\n{real.StackTrace}");
            }
        }

        internal static bool GameManager_StartMatch_OraclePrefix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return true;
            try
            {
                var inLobby = Traverse.Create(__instance).Method("IsInLobby").GetValue<bool>();
                if (inLobby) return false;
            }
            catch { }
            return true;
        }
    }
}
