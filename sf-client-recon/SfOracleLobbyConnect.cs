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

                var checkReady = AccessTools.Method(_mmType, "CheckReadyPlayers");
                if (RefOk(checkReady))
                    harmony.Patch(checkReady, prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckReadyPlayers_OraclePrefix)));

                var onMatchStart = AccessTools.Method(_mmType, "OnMatchStart");
                if (RefOk(onMatchStart))
                    harmony.Patch(onMatchStart, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnMatchStart_OraclePostfix)));

                if (RefOk(_gmType))
                {
                    var startMatch = AccessTools.Method(_gmType, "StartMatch");
                    if (RefOk(startMatch))
                        harmony.Patch(startMatch, prefix: new HarmonyMethod(typeof(Plugin), nameof(GameManager_StartMatch_OraclePrefix)));
                    var startCountDown = AccessTools.Method(_gmType, "StartCountDown");
                    if (RefOk(startCountDown))
                        harmony.Patch(startCountDown, prefix: new HarmonyMethod(typeof(Plugin), nameof(GameManager_StartCountDown_OraclePrefix)));
                }

                Log.LogInfo("[oracle-lobby] Patched Quick/Host Match → UDP oracle (no Steam lobby).");
                ScheduleOracleAutoConnect();
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] patch failed: {e.Message}"); }
        }

        private void ScheduleOracleAutoConnect()
        {
            if (_oracleAutoConnectStarted) return;
            _oracleAutoConnectStarted = true;
            StartCoroutine(OracleAutoConnectRoutine());
        }

        private static IEnumerator OracleAutoConnectRoutine()
        {
            yield return new WaitForSeconds(2.5f);
            if (!_oracleConnectMode) yield break;
            try
            {
                var pkt = GetPktHandler();
                if (RefOk(pkt))
                {
                    var t = Traverse.Create(pkt);
                    if (t.Field<bool>("mHasSentOrReceived").Value) yield break;
                }
                var mm = UnityEngine.Object.FindObjectOfType(_mmType);
                if (RefOk(mm))
                {
                    var inLobby = Traverse.Create(mm).Method("IsInLobby").GetValue<bool>();
                    if (inLobby) yield break;
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
            }
            catch (Exception e) { Log.LogError($"[oracle-lobby] BeginOracleLobbyConnect: {e.Message}"); }
        }

        private static object GetPktHandler()
        {
            var prop = AccessTools.Property(_pktType, "Instance");
            if (RefOk(prop)) return prop.GetValue(null, null);
            return UnityEngine.Object.FindObjectOfType(_pktType);
        }

        internal static void P2PPackageHandler_Update_OraclePostfix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return;
            try
            {
                var t = Traverse.Create(__instance);
                if (t.Field<bool>("mPauseTraffic").Value) return;
                if (!t.Field<bool>("mHasHandler").Value) return;
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
            if (t == 3) return false;
            return true;
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

        /// <summary>Halloween/boss: StartCountDown while isLoading skips pumpkin spawn — wait for load.</summary>
        internal static bool GameManager_StartCountDown_OraclePrefix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return true;
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
                    m.Invoke(gmInst, null);
                    Log.LogInfo("[oracle-lobby] StartCountDown after map load (pumpkin/boss hooks).");
                }
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] deferred StartCountDown: {e.Message}"); }
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
