using System;
using System.Collections;
using System.IO;
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
        private static bool _endpointResolved;
        private static string _resolvedOracleHost;
        private static int _resolvedOraclePort;
        private static Type _pktType;
        private static Type _mmType;
        private static Type _mhType;
        private static Type _gmType;
        private static Type _mmSocketsType;

        internal static string OracleServerHost
        {
            get { ResolveOracleEndpoint(); return string.IsNullOrEmpty(_resolvedOracleHost) ? "127.0.0.1" : _resolvedOracleHost; }
        }

        internal static int OracleServerPort
        {
            get { ResolveOracleEndpoint(); return _resolvedOraclePort > 0 ? _resolvedOraclePort : 1337; }
        }

        internal static void ResolveOracleEndpoint()
        {
            if (_endpointResolved) return;
            _endpointResolved = true;
            _resolvedOraclePort = 1337;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-address" && !string.IsNullOrEmpty(args[i + 1]))
                {
                    _resolvedOracleHost = args[i + 1];
                    _oracleConnectMode = true;
                }
                else if (args[i] == "-port" && int.TryParse(args[i + 1], out var p) && p > 0)
                    _resolvedOraclePort = p;
            }

            if (string.IsNullOrEmpty(_resolvedOracleHost))
            {
                var envHost = Environment.GetEnvironmentVariable("SF_ORACLE_ADDRESS");
                if (!string.IsNullOrEmpty(envHost))
                {
                    _resolvedOracleHost = envHost.Trim();
                    _oracleConnectMode = true;
                }
            }
            var envPort = Environment.GetEnvironmentVariable("SF_ORACLE_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var ep) && ep > 0)
                _resolvedOraclePort = ep;

            if (string.IsNullOrEmpty(_resolvedOracleHost))
            {
                try
                {
                    string gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    string[] candidates =
                    {
                        Path.Combine(Path.Combine(Path.Combine(gameRoot, "BepInEx"), "config"), "sf-oracle-endpoint.txt"),
                        Path.Combine(Path.Combine(Path.Combine(gameRoot, "BepInEx"), "plugins"), "sf-oracle-endpoint.txt"),
                    };
                    foreach (var path in candidates)
                    {
                        if (!File.Exists(path)) continue;
                        var lines = File.ReadAllLines(path);
                        if (lines.Length == 0 || string.IsNullOrEmpty(lines[0]) || lines[0].Trim().Length == 0) continue;
                        var line = lines[0].Trim();
                        if (line.Contains(":"))
                        {
                            var parts = line.Split(':');
                            _resolvedOracleHost = parts[0].Trim();
                            if (parts.Length > 1) int.TryParse(parts[1].Trim(), out _resolvedOraclePort);
                        }
                        else _resolvedOracleHost = line;
                        if (lines.Length > 1 && int.TryParse(lines[1].Trim(), out var filePort) && filePort > 0)
                            _resolvedOraclePort = filePort;
                        _oracleConnectMode = true;
                        Log.LogInfo($"[oracle-lobby] Endpoint from {path} → {_resolvedOracleHost}:{_resolvedOraclePort}");
                        break;
                    }
                }
                catch (Exception e) { Log.LogWarning($"[oracle-lobby] endpoint file read: {e.Message}"); }
            }

            if (_oracleConnectMode && !string.IsNullOrEmpty(_resolvedOracleHost))
                Log.LogInfo($"[oracle-lobby] Oracle mode ON → {_resolvedOracleHost}:{_resolvedOraclePort}");
        }

        private static void DetectOracleConnectMode()
        {
            ResolveOracleEndpoint();
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
                var joinDefault = AccessTools.Method(_mhType, "JoinDefaultSocketServer");
                if (RefOk(joinDefault))
                    harmony.Patch(joinDefault, prefix: new HarmonyMethod(typeof(Plugin), nameof(JoinDefaultSocketServer_OraclePrefix)));

                _mmSocketsType = AccessTools.TypeByName("MatchMakingHandlerSockets");
                if (RefOk(_mmSocketsType))
                {
                    var hostServer = AccessTools.Method(_mmSocketsType, "HostServer");
                    if (RefOk(hostServer))
                        harmony.Patch(hostServer, prefix: new HarmonyMethod(typeof(Plugin), nameof(HostServer_OraclePrefix)));
                    var joinServer = AccessTools.Method(_mmSocketsType, "JoinServer");
                    if (RefOk(joinServer))
                        harmony.Patch(joinServer, prefix: new HarmonyMethod(typeof(Plugin), nameof(JoinServer_OraclePrefix)));
                }

                var pktUpdate = AccessTools.Method(_pktType, "Update");
                if (RefOk(pktUpdate))
                    harmony.Patch(pktUpdate, postfix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_Update_OraclePostfix)));
                var pktInit = AccessTools.Method(_pktType, "Init");
                if (RefOk(pktInit))
                    harmony.Patch(pktInit, postfix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_Init_OraclePostfix)));
                var reqInit = AccessTools.Method(_mmType, "RequestClientInit");
                if (RefOk(reqInit))
                    harmony.Patch(reqInit, postfix: new HarmonyMethod(typeof(Plugin), nameof(MultiplayerManager_RequestClientInit_OraclePostfix)));
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
            // After the premature-init fix (0.3.8+), a delayed auto-connect is safe:
            // it covers PLAY ONLINE → walk into lobby box without clicking Quick Match.
            _oracleAutoConnectStarted = true;
            _autoConnectAt = Time.realtimeSinceStartup + 4f;
            Log.LogInfo("[oracle-lobby] Auto-connect scheduled in 4s (backup if Quick Match not clicked).");
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
            if (_oracleConnectStarted) return;
            try
            {
                if (!RefOk(_mmType)) _mmType = AccessTools.TypeByName("MultiplayerManager");
                var mm = UnityEngine.Object.FindObjectOfType(_mmType);
                if (RefOk(mm))
                {
                    var accF = AccessTools.Field(_mmType, "mHasBeenAcceptedFromServer");
                    var initF = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                    if (RefOk(accF) && (bool)accF.GetValue(mm)) return;
                    if (RefOk(initF) && (bool)initF.GetValue(mm)) return;
                }
                var pkt = GetPktHandler();
                if (RefOk(pkt) && RefOk(_pktType))
                {
                    var sentF = AccessTools.Field(_pktType, "mHasSentOrReceived");
                    if (RefOk(sentF) && (bool)sentF.GetValue(pkt)) return;
                }
            }
            catch { }
            Log.LogInfo("[oracle-lobby] Auto-connect → oracle (endpoint file / -address).");
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
            // Host Match now spins up a brand-new backend lobby (visible in the
            // browser list) and hosts it, instead of silently joining MAIN. If no
            // create endpoint is configured CreateAndHostLobby falls back to MAIN.
            CreateAndHostLobby();
            return false;
        }

        internal static bool JoinDefaultSocketServer_OraclePrefix()
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect("QuickMatch-sockets");
            return false;
        }

        internal static bool HostServer_OraclePrefix()
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect("HostMatch-sockets");
            return false;
        }

        internal static bool JoinServer_OraclePrefix()
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect("QuickMatch-lan");
            return false;
        }

        private static void BeginOracleLobbyConnect(string source)
        {
            try
            {
                _oracleConnectStarted = true;
                Log.LogInfo($"[oracle-lobby] {source} → connecting to {OracleServerHost}:{OracleServerPort} (no Steam).");
                var pkt = GetPktHandler();
                if (!RefOk(pkt))
                {
                    Log.LogError("[oracle-lobby] P2PPackageHandler missing.");
                    return;
                }
                var init = AccessTools.Method(_pktType, "Init");
                if (RefOk(init)) init.Invoke(pkt, null);

                // Clean handshake: the joiner path must send ClientRequesting*
                // in order. A stale mHasBeenInitializedFromServer=true (e.g.
                // from an earlier partial session) makes ReadMessageBuffer drop
                // init packets and the client never leaves PLAY ONLINE.
                try
                {
                    var mmReset = UnityEngine.Object.FindObjectOfType(_mmType);
                    var initF = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                    if (RefOk(mmReset) && RefOk(initF) && (bool)initF.GetValue(mmReset))
                    {
                        initF.SetValue(mmReset, false);
                        Log.LogInfo("[oracle-lobby] Reset mHasBeenInitializedFromServer=false for clean join.");
                    }
                }
                catch { }

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

        // Entry point for the in-game server browser (called cross-assembly by
        // reflection from SFServerBrowser): join a specific lobby by code over
        // the sf-router single port — no game restart. Sets the SELECT code,
        // emits a SELECT immediately so the router pins our IP→backend before
        // the game socket connects, then runs the normal oracle-connect path.
        // Works from the main menu; to switch lobbies, leave to menu and call
        // again (a fresh SELECT + connect; no live-connection rebind needed).
        internal static void RequestJoinLobby(string code)
        {
            code = (code ?? "").Trim().ToUpper();
            if (code.Length == 0) { Log.LogWarning("[oracle-lobby] RequestJoinLobby: empty code ignored."); return; }
            SelectedLobbyCode = code;
            Log.LogInfo($"[oracle-lobby] RequestJoinLobby({code}) — SELECT + connect via router.");
            var inst = Instance;
            if (RefOk(inst))
            {
                // Re-arm the SELECT resend loop for the NEW lobby: capture the
                // current (monotonic) snapshot count so "snapshots flowing" only
                // becomes true once the NEW lobby's snapshots actually arrive.
                inst._lastSeenSnapCount = inst._snapsReceived;
                inst._lastSnapGrowAt = -1f;
                inst._lastSelectAt = -1f;
                try { inst.SendSelectLobbyPacket(); }
                catch (Exception e) { Log.LogWarning($"[oracle-lobby] SELECT send: {e.Message}"); }
            }
            // Allow a re-connect from the menu (the autoconnect guard latches this).
            _oracleConnectStarted = false;
            BeginOracleLobbyConnect("ServerBrowser:" + code);
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

        internal static void P2PPackageHandler_Init_OraclePostfix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return;
            _oracleConnectStarted = true;
            try
            {
                var host = OracleServerHost;
                int port = OracleServerPort;
                bool hasCliAddr = false;
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-address") { hasCliAddr = true; break; }
                if (!hasCliAddr && host != "127.0.0.1" && host != "localhost")
                {
                    var udpClient = AccessTools.Method(__instance.GetType(), "UDPClient");
                    if (RefOk(udpClient))
                    {
                        udpClient.Invoke(__instance, new object[] { host, port });
                        Log.LogInfo($"[oracle-lobby] Init rebound UDP client → {host}:{port}");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] Init postfix: {e.Message}"); }
            Log.LogInfo("[oracle-lobby] P2PPackageHandler.Init → oracle RX armed.");
        }

        internal static void MultiplayerManager_RequestClientInit_OraclePostfix(object __instance)
        {
            if (!_oracleConnectMode) return;
            _oracleConnectStarted = true;
            Log.LogInfo("[oracle-lobby] RequestClientInit → handshake started (ClientRequestingAccepting).");
        }

        internal static void P2PPackageHandler_Update_OraclePostfix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return;
            try
            {
                var t = Traverse.Create(__instance);
                if (t.Field<bool>("mPauseTraffic").Value) return;
                // Do NOT require mHasHandler — during OnlineBox / scene load it can
                // be false while UDP replies (ClientAccepted) are already queued.
                bool sentOrRx = t.Field<bool>("mHasSentOrReceived").Value;
                if (_oracleConnectStarted || sentOrRx)
                    ForceInitializedFromServer(null);
                t.Method("CheckForPackagesOnChannel", new object[] { 1, false }).GetValue();
                t.Method("CheckForPackagesOnChannel", new object[] { 0, false }).GetValue();
                if (sentOrRx) t.Field("mHasSentOrReceived").SetValue(true);
            }
            catch (Exception e)
            {
                if (Time.realtimeSinceStartup - _lastUpdateDiagAt > 5f)
                {
                    _lastUpdateDiagAt = Time.realtimeSinceStartup;
                    Log.LogWarning($"[oracle-diag] Update postfix: {e.Message}");
                }
            }
        }

        private static float _lastUpdateDiagAt = -1f;

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
                if (rawData != null && rawData.Length >= 5)
                {
                    byte mt = rawData[4];
                    if (VerboseDiag && (mt == 4 || mt == 5 || mt == 18 || mt == 35))
                        Log.LogInfo($"[oracle-diag] ReadMessageBuffer inbound msgType={mt} len={rawData.Length}");
                }
                if (_oracleConnectStarted)
                    ForceInitializedFromServer(null);
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
