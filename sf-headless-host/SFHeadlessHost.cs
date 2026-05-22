using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFHeadlessHost
{
    // SFHeadlessHost — drives Stick Fight headlessly so the Go server can use
    // it as a physics oracle for one lobby.
    //
    // When SF is launched with `-batchmode -nographics`, Unity loads but no UI
    // is available to navigate the menus. This plugin:
    //   1. Detects batchmode on Awake; no-ops in interactive runs.
    //   2. Waits for the chainloader to finish, then forces SceneManager.LoadScene
    //      to skip the splash/menu and jump to a Landfall gameplay scene.
    //   3. Ensures MatchmakingHandler is in Sockets mode, then calls
    //      MatchMakingHandlerSockets.HostServer() to bind a Lidgren NetServer.
    //   4. Patches the hardcoded port 1337 in NetworkSocketServer's ctor with
    //      whatever SFHEADLESS_PORT env var says (default: 1340 to avoid
    //      colliding with the Go server on 1337).
    //
    // Configuration via env vars (read once at Awake):
    //   SFHEADLESS_PORT   — Lidgren bind port (default 1340).
    //   SFHEADLESS_SCENE  — Initial scene index to load (default 6, Landfall 6).
    //   SFHEADLESS_DEBUG  — "1" enables verbose tick logging.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.headless-host";
        public const string PluginName = "SFHeadlessHost";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static int BindPort = 1340;
        internal static int InitialScene = 6;
        internal static bool Verbose;

        private void Awake()
        {
            Log = Logger;

            // Unity 5.6 doesn't have Application.isBatchMode — fall back to
            // checking the command-line for -batchmode.
            bool batchMode = false;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-batchmode" || arg == "-nographics")
                {
                    batchMode = true;
                    break;
                }
            }
            if (!batchMode)
            {
                Log.LogInfo($"{PluginName} {PluginVersion}: interactive run detected (no -batchmode arg). No-op.");
                return;
            }
            Log.LogInfo($"{PluginName} {PluginVersion}: batchmode detected, bootstrapping headless host.");

            ReadEnv();

            // P0 — Harmony-patch NetworkSocketServer to bind on BindPort instead
            // of the hardcoded 1337. We do this before HostServer() is called so
            // the patched ctor sees the override.
            try
            {
                var harmony = new Harmony(PluginGuid);
                var sockType = AccessTools.TypeByName("Landfall.Network.Sockets.NetworkSocketServer");
                if (sockType != null)
                {
                    var ctor = AccessTools.Constructor(sockType, Type.EmptyTypes);
                    if (ctor != null)
                    {
                        harmony.Patch(ctor, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(PatchServerPort))));
                        Log.LogInfo($"Patched NetworkSocketServer ctor — bind port will be {BindPort}.");
                    }
                    else
                    {
                        Log.LogWarning("Could not find NetworkSocketServer parameterless ctor.");
                    }
                }
                else
                {
                    Log.LogWarning("Could not find type Landfall.Network.Sockets.NetworkSocketServer.");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Port-patch failed: {e}");
            }

            _bootStartedAt = Time.realtimeSinceStartup;
            _bootState = BootState.WaitForInit;
        }

        // Boot is driven by Update() as a state machine because Unity 5.6's
        // Mono runtime is missing IteratorStateMachineAttribute (emitted by the
        // C# compiler for any method with `yield return`). Using a plain
        // state machine keeps the assembly compatible.
        private enum BootState { Idle, WaitForInit, LoadingScene, WaitingForSceneSettle, HostStarting, Running }

        private BootState _bootState = BootState.Idle;
        private float _bootStartedAt;
        private float _stateSince;
        private AsyncOperation _loadOp;
        private int _settleFrames;
        private int _heartbeatTicks;
        private float _lastHeartbeat;

        private void Update()
        {
            try
            {
                StepBoot();
            }
            catch (Exception e)
            {
                Log.LogError($"SFHeadlessHost.Update: {e}");
                _bootState = BootState.Idle; // give up; don't spam errors every frame
            }
        }

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
                    _bootState = BootState.Running;
                    _lastHeartbeat = Time.realtimeSinceStartup;
                    return;

                case BootState.Running:
                    var interval = Verbose ? 5.0f : 30.0f;
                    if (Time.realtimeSinceStartup - _lastHeartbeat >= interval)
                    {
                        _lastHeartbeat = Time.realtimeSinceStartup;
                        _heartbeatTicks++;
                        Log.LogInfo($"heartbeat: scene={SceneManager.GetActiveScene().name} tick={_heartbeatTicks}");
                    }
                    return;
            }
        }

        private void StartHost()
        {
            // Step 2: ensure MatchmakingHandler is in Sockets mode.
            var mmType = AccessTools.TypeByName("MatchmakingHandler");
            if (mmType != null)
            {
                var runningOnSockets = AccessTools.Field(mmType, "mRunningOnSockets")
                                       ?? AccessTools.Field(mmType, "m_RunningOnSockets");
                var instance = UnityEngine.Object.FindObjectOfType(mmType);
                if (instance != null && runningOnSockets != null)
                {
                    runningOnSockets.SetValue(instance, true);
                    Log.LogInfo("Set MatchmakingHandler.mRunningOnSockets = true.");
                }
                else
                {
                    Log.LogWarning("MatchmakingHandler instance or mRunningOnSockets field not found.");
                }

                var runningOnSocketsStatic = AccessTools.Property(mmType, "RunningOnSockets");
                if (runningOnSocketsStatic != null && runningOnSocketsStatic.CanWrite)
                {
                    runningOnSocketsStatic.SetValue(null, true);
                }
                else
                {
                    var backing = AccessTools.Field(mmType, "<RunningOnSockets>k__BackingField");
                    if (backing != null) backing.SetValue(null, true);
                }
            }

            // Step 3: HostServer on MatchMakingHandlerSockets.
            var hostType = AccessTools.TypeByName("MatchMakingHandlerSockets");
            if (hostType == null)
            {
                Log.LogError("MatchMakingHandlerSockets type not found.");
                return;
            }
            var hostInstance = UnityEngine.Object.FindObjectOfType(hostType);
            if (hostInstance == null)
            {
                Log.LogWarning("No MatchMakingHandlerSockets instance in scene; creating one.");
                var go = new GameObject("SFHeadlessHost_MMSockets");
                UnityEngine.Object.DontDestroyOnLoad(go);
                hostInstance = go.AddComponent(hostType);
            }
            var hostMethod = AccessTools.Method(hostType, "HostServer");
            if (hostMethod == null)
            {
                Log.LogError("MatchMakingHandlerSockets.HostServer method not found.");
                return;
            }
            try
            {
                var result = hostMethod.Invoke(hostInstance, null);
                Log.LogInfo($"HostServer() returned: {result}");
            }
            catch (Exception e)
            {
                Log.LogError($"HostServer() threw: {e}");
                return;
            }
            Log.LogInfo($"=== HEADLESS HOST READY on port {BindPort} ===");
        }

        private static void ReadEnv()
        {
            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_PORT"), out var p);
            if (p > 0 && p < 65536) BindPort = p;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_SCENE"), out var s);
            if (s >= 0) InitialScene = s;

            Verbose = Environment.GetEnvironmentVariable("SFHEADLESS_DEBUG") == "1";
            Log.LogInfo($"Config: BindPort={BindPort} InitialScene={InitialScene} Verbose={Verbose}");
        }

        // Harmony postfix on NetworkSocketServer ctor. The stock ctor sets
        // Server = new NetServer(config{Port = 1337}). We can't easily unwind
        // that, but Lidgren NetServer hasn't been Start()ed yet at this point —
        // we can mutate its Configuration.Port before Init() is called.
        private static void PatchServerPort(object __instance)
        {
            try
            {
                var serverProp = AccessTools.Property(__instance.GetType(), "Server");
                if (serverProp == null) return;
                var netServer = serverProp.GetValue(__instance);
                if (netServer == null) return;
                var configProp = AccessTools.Property(netServer.GetType(), "Configuration");
                if (configProp == null) return;
                var config = configProp.GetValue(netServer);
                if (config == null) return;
                var portProp = AccessTools.Property(config.GetType(), "Port");
                if (portProp == null) return;
                portProp.SetValue(config, BindPort);
                Log.LogInfo($"NetworkSocketServer ctor postfix: rewrote Port → {BindPort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"PatchServerPort threw: {e}");
            }
        }

    }
}
