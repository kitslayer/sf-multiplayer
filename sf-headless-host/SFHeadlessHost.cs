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
        internal static Plugin Instance;
        internal static int BindPort = 1340;     // Game-traffic port (Lidgren)
        internal static int BridgePort = 1341;   // State-bridge port (this plugin)
        internal static int InitialScene = 0; // 0 = lobby (boots ControllerHandler + GameManager DontDestroyOnLoad infrastructure)
        internal static bool Verbose;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

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
                Log.LogInfo($"{PluginName} {PluginVersion}: interactive run — installing CLIENT-MODE shim.");
                InstallClientModePatches();
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
                if ((object)sockType != null)
                {
                    var ctor = AccessTools.Constructor(sockType, Type.EmptyTypes);
                    if ((object)ctor != null)
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

            // Harmony-prefix Controller.Update so that, right before SF's
            // own Update reads PlayerActions.Movement.X / .Y / button states,
            // we write our per-slot input buffer values into the relevant
            // backing fields. This bypasses InControl's tick + Commit
            // lifecycle (which was overwriting our injection from outside).
            try
            {
                var harmony = new Harmony(PluginGuid + ".controller-input-prefix");
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType != null)
                {
                    var updateMethod = AccessTools.Method(ctrlType, "Update");
                    if ((object)updateMethod != null)
                    {
                        var prefix = AccessTools.Method(typeof(Plugin), nameof(InjectInputPrefix));
                        harmony.Patch(updateMethod, prefix: new HarmonyMethod(prefix));
                        Log.LogInfo("Patched Controller.Update with input-injection prefix.");
                    }
                }
                // [INSTR3] Patch Movement.MoveRight / MoveLeft so we can see
                // whether Controller.Update actually invokes them after our
                // input injection.
                var movType = AccessTools.TypeByName("Movement");
                if ((object)movType != null)
                {
                    var mr = AccessTools.Method(movType, "MoveRight");
                    if ((object)mr != null)
                        harmony.Patch(mr, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(MoveRightPrefix))));
                    var ml = AccessTools.Method(movType, "MoveLeft");
                    if ((object)ml != null)
                        harmony.Patch(ml, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(MoveLeftPrefix))));
                    Log.LogInfo("[INSTR3] Patched Movement.MoveRight/MoveLeft entry-loggers.");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Controller.Update prefix patch failed: {e}");
            }

            // Phase 6.5 — host-side patches. Each runs in its own try/catch
            // so one failure (signature drift, missing type) doesn't silently
            // skip the rest. Failures accumulate in _p65MissingPatches and are
            // surfaced as a loud warning after all installs.
            {
                var harmony = new Harmony(PluginGuid + ".phase6-5-observe");

                var mmType = AccessTools.TypeByName("MultiplayerManager");
                TryPatch(harmony, "MultiplayerManager.IsServer (postfix → true)",
                    (object)mmType != null ? AccessTools.PropertyGetter(mmType, "IsServer") : null,
                    postfix: nameof(IsServerPostfix));
                TryPatch(harmony, "MultiplayerManager.SendMessageToAllClients (prefix log+forward)",
                    (object)mmType != null ? AccessTools.Method(mmType, "SendMessageToAllClients") : null,
                    prefix: nameof(SendBroadcastPrefix));

                var mhTypeP = AccessTools.TypeByName("MatchmakingHandler");
                TryPatch(harmony, "MatchmakingHandler.IsNetworkMatch (postfix → true)",
                    (object)mhTypeP != null ? AccessTools.PropertyGetter(mhTypeP, "IsNetworkMatch") : null,
                    postfix: nameof(IsNetworkMatchPostfix));
                // SetNetworkMatch prefix uses a named `ref bool v` to mutate the
                // arg in-place. Harmony binds prefix params by name, so verify
                // SF's first param really is named `v`; if SF ever renames it
                // (e.g. to `value`), the prefix silently no-ops and the fix
                // we depend on regresses.
                var setNetMatchMethod = (object)mhTypeP != null ? AccessTools.Method(mhTypeP, "SetNetworkMatch") : null;
                if ((object)setNetMatchMethod != null)
                {
                    var ps = setNetMatchMethod.GetParameters();
                    if (ps.Length == 0 || ps[0].Name != "v")
                    {
                        Log.LogError($"[P6.5] SetNetworkMatch first param is '{(ps.Length > 0 ? ps[0].Name : "<none>")}', expected 'v' — SetNetworkMatchPrefix will silently no-op. Update prefix signature.");
                    }
                }
                TryPatch(harmony, "MatchmakingHandler.SetNetworkMatch (prefix force arg=true)",
                    setNetMatchMethod, prefix: nameof(SetNetworkMatchPrefix));

                var wsType = AccessTools.TypeByName("WeaponSelectionHandler");
                TryPatch(harmony, "WeaponSelectionHandler.GetRandomWeaponIndex (prefix → valid index)",
                    (object)wsType != null ? AccessTools.Method(wsType, "GetRandomWeaponIndex") : null,
                    prefix: nameof(GetRandomWeaponIndexPrefix));

                var gmTypeP = AccessTools.TypeByName("GameManager");
                TryPatch(harmony, "GameManager.SpawnRandomWeapon (prefix replace impl)",
                    (object)gmTypeP != null ? AccessTools.Method(gmTypeP, "SpawnRandomWeapon") : null,
                    prefix: nameof(SpawnRandomWeaponPrefix));

                // P2PPackageHandler.SendP2PPacketToUser has two overloads;
                // we want the CSteamID one. AccessTools.Method without a
                // typeArray returns the first match which may be the wrong
                // overload, so probe explicitly.
                MethodInfo csteamSend = null;
                var ppType = AccessTools.TypeByName("P2PPackageHandler");
                if ((object)ppType != null)
                {
                    foreach (var m in ppType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "SendP2PPacketToUser") continue;
                        var ps = m.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType.Name == "CSteamID") { csteamSend = m; break; }
                    }
                }
                TryPatch(harmony, "P2PPackageHandler.SendP2PPacketToUser(CSteamID,…) (prefix log)",
                    csteamSend, prefix: nameof(SendDirectPrefix));

                // Phase 6.9 diagnostics — log when SF's host-side
                // PrepareMapForTravel coroutine reaches each critical step.
                // Tells us whether destructibles are getting the right init
                // (kinematic-settle, joint detach/reattach, InitSyncedObjects).
                TryPatch(harmony, "MultiplayerManager.InitSyncedObjects (postfix log)",
                    (object)mmType != null ? AccessTools.Method(mmType, "InitSyncedObjects") : null,
                    postfix: nameof(InitSyncedObjectsPostfix));
                TryPatch(harmony, "MultiplayerManager.InitMapDataObjects (postfix log)",
                    (object)mmType != null ? AccessTools.Method(mmType, "InitMapDataObjects") : null,
                    postfix: nameof(InitMapDataObjectsPostfix));
                TryPatch(harmony, "MultiplayerManager.ReadyUp (postfix log)",
                    (object)mmType != null ? AccessTools.Method(mmType, "ReadyUp") : null,
                    postfix: nameof(ReadyUpPostfix));

                if (_p65MissingPatches.Count == 0)
                {
                    Log.LogInfo($"[P6.5] All {_p65PatchesSucceeded}/{_p65PatchesAttempted} patches installed.");
                }
                else
                {
                    Log.LogError($"[P6.5] {_p65PatchesSucceeded}/{_p65PatchesAttempted} patches installed; MISSING: {string.Join("; ", _p65MissingPatches.ToArray())}");
                    Log.LogError("[P6.5] Oracle will boot, but Phase 6.5 host-side gameplay will be partial. Investigate above failures.");
                }
            }

            _bootStartedAt = Time.realtimeSinceStartup;
            _bootState = BootState.WaitForInit;
        }

        // Harmony prefix on Controller.Update. Runs once per controller per
        // frame, immediately before the original method body. We look up
        // our static input buffer by the controller's playerID, and write
        // those values directly into the Movement / Aiming / button-action
        // backing fields. The original Update then reads them and dispatches
        // movement.MoveRight() / etc with our values.
        //
        // Only runs for rigs WE spawned (gated by SlotToRig containing the
        // controller's GameObject) — never touches real-player rigs.
        private static int _prefixCallCount;
        private static int _prefixOurRigCount;
        private static int _applyInputCount;
        private static int _moveRightCallCount;
        private static int _moveLeftCallCount;
        internal static bool InjectInputPrefix(object __instance)
        {
            try
            {
                _prefixCallCount++;
                if ((object)__instance == null) return true;
                var ctrlComp = __instance as Component;
                if ((object)ctrlComp == null) return true;
                var rig = ctrlComp.gameObject;

                int slot = -1;
                foreach (var kv in SlotToRig)
                {
                    if (kv.Value == rig) { slot = kv.Key; break; }
                }
                if (slot < 0)
                {
                    if (_prefixCallCount % 600 == 1)
                        Log.LogDebug($"InjectInputPrefix: rig {rig.name} not ours (prefix call #{_prefixCallCount})");
                    return true;
                }
                _prefixOurRigCount++;

                if (!SlotInputs.TryGetValue(slot, out var input)) return true;

                bool verbose = (_prefixOurRigCount == 1 || _prefixOurRigCount % 120 == 0);
                if (verbose)
                    Log.LogInfo($"[INSTR2a] Prefix entry: slot={slot} SlotInputs.stick=({input.StickX:0.00},{input.StickY:0.00}) ourCallCount={_prefixOurRigCount}");

                var actionsField = AccessTools.Field(__instance.GetType(), "mPlayerActions");
                if ((object)actionsField == null) return true;
                var actions = actionsField.GetValue(__instance);
                if ((object)actions == null) return true;

                // Read CURRENT Movement.X/Y BEFORE our write — tells us whether
                // InControl rebuilt it since our last write last frame.
                if (verbose)
                {
                    var preX = ReadAxis(actions, "Movement", "X");
                    var preY = ReadAxis(actions, "Movement", "Y");
                    Log.LogInfo($"[INSTR2b] PRE-write Movement=({preX:0.00},{preY:0.00})");
                }

                // Stuff our values into Movement.X / .Y backing fields and
                // Movement.thisValue. Read by the original Update body
                // immediately after this prefix returns.
                ForceTwoAxis(actions, "Movement", input.StickX, input.StickY);
                ForceTwoAxis(actions, "Aiming",   input.AimX,   input.AimY);

                // Read again AFTER write — confirms write took effect synchronously.
                if (verbose)
                {
                    var postX = ReadAxis(actions, "Movement", "X");
                    var postY = ReadAxis(actions, "Movement", "Y");
                    // Probe Controller early-return gate variables.
                    var ctrlT = __instance.GetType();
                    var inactiveF = AccessTools.Field(ctrlT, "inactive");
                    var infoF    = AccessTools.Field(ctrlT, "info");
                    var hasCtrlF = AccessTools.Field(ctrlT, "mHasControl");
                    bool inactive = (object)inactiveF != null && (bool)inactiveF.GetValue(__instance);
                    bool hasCtrl  = (object)hasCtrlF != null && (bool)hasCtrlF.GetValue(__instance);
                    bool isDead = false;
                    if ((object)infoF != null) {
                        var infoVal = infoF.GetValue(__instance);
                        if ((object)infoVal != null) {
                            var deadF = AccessTools.Field(infoVal.GetType(), "isDead");
                            if ((object)deadF != null) isDead = (bool)deadF.GetValue(infoVal);
                        }
                    }
                    // GameManager.inFight + stillInMenu — the prime suspects.
                    var gmT = AccessTools.TypeByName("GameManager");
                    bool inFight = false, stillInMenu = false;
                    if ((object)gmT != null) {
                        var fF = AccessTools.Field(gmT, "inFight");
                        var mF = AccessTools.Field(gmT, "stillInMenu");
                        if ((object)fF != null) inFight = (bool)fF.GetValue(null);
                        if ((object)mF != null) stillInMenu = (bool)mF.GetValue(null);
                    }
                    bool willEarlyReturn = inactive || isDead || (!inFight && !stillInMenu);
                    Log.LogInfo($"[INSTR2c] POST-write Movement=({postX:0.00},{postY:0.00}) inactive={inactive} hasControl={hasCtrl} isDead={isDead} inFight={inFight} stillInMenu={stillInMenu} willEarlyReturn={willEarlyReturn}");
                }

                // Button-action backing field updates. PlayerAction is a
                // OneAxisInputControl with private InputControlState
                // (lastState, thisState, nextState). The IsPressed/WasPressed
                // accessors read thisState. We reach into thisState's .State
                // bool and .Value float to set the press.
                ForceButton(actions, "Jump",        (input.Buttons & 0x01) != 0);
                ForceButton(actions, "Jump2",       (input.Buttons & 0x01) != 0);
                ForceButton(actions, "PunchOrFire", (input.Buttons & 0x02) != 0);
                ForceButton(actions, "Block",       (input.Buttons & 0x04) != 0);
                ForceButton(actions, "Throw",       (input.Buttons & 0x08) != 0);
            }
            catch (Exception e)
            {
                if (Verbose && Log != null) Log.LogDebug($"InjectInputPrefix: {e.Message}");
            }
            return true; // always let original Update run
        }

        internal static void MoveRightPrefix(object __instance)
        {
            _moveRightCallCount++;
            if (_moveRightCallCount == 1 || _moveRightCallCount % 30 == 0)
            {
                try
                {
                    var c = __instance as Component;
                    string name = (object)c != null ? c.gameObject.name : "?";
                    Log.LogInfo($"[INSTR3] Movement.MoveRight#{_moveRightCallCount} on {name}");
                }
                catch { }
            }
        }

        internal static void MoveLeftPrefix(object __instance)
        {
            _moveLeftCallCount++;
            if (_moveLeftCallCount == 1 || _moveLeftCallCount % 30 == 0)
            {
                try
                {
                    var c = __instance as Component;
                    string name = (object)c != null ? c.gameObject.name : "?";
                    Log.LogInfo($"[INSTR3] Movement.MoveLeft#{_moveLeftCallCount} on {name}");
                }
                catch { }
            }
        }

        // Phase 6.5 Step 1 — IsServer=true. Static property getter so no __instance.
        internal static void IsServerPostfix(ref bool __result)
        {
            __result = true;
        }

        // Phase 6.5 Step 2d — IsNetworkMatch=true. Pin against Controller's reset.
        internal static void IsNetworkMatchPostfix(ref bool __result)
        {
            __result = true;
        }

        // Phase 6.9 diagnostics — track PrepareMapForTravel progress on oracle.
        private static int _initSyncedCallCount;
        internal static void InitSyncedObjectsPostfix()
        {
            _initSyncedCallCount++;
            Log.LogInfo($"[P6.9 init] InitSyncedObjects called (#{_initSyncedCallCount}). PrepareMapForTravel reached settle-end on the oracle.");
        }
        private static int _initMapDataCallCount;
        internal static void InitMapDataObjectsPostfix()
        {
            _initMapDataCallCount++;
            Log.LogInfo($"[P6.9 init] InitMapDataObjects called (#{_initMapDataCallCount}).");
        }
        private static int _readyUpCallCount;
        internal static void ReadyUpPostfix()
        {
            _readyUpCallCount++;
            Log.LogInfo($"[P6.9 init] MultiplayerManager.ReadyUp called (#{_readyUpCallCount}).");
        }

        // Phase 6.5 Step 2d — force every SetNetworkMatch(v) call to use v=true.
        // Defeats the inlined-getter problem because the backing field stays true.
        private static int _setNetMatchInterceptCount;
        internal static bool SetNetworkMatchPrefix(ref bool v)
        {
            _setNetMatchInterceptCount++;
            if (!v && _setNetMatchInterceptCount <= 5)
                Log.LogInfo($"[P6.5] SetNetworkMatch(false) intercepted #{_setNetMatchInterceptCount} → forcing true");
            v = true;
            return true; // run original with forced arg
        }

        // Phase 6.5 Step 2e — replace GameManager.SpawnRandomWeapon. Computes a
        // spawn position matching the original method's logic, picks a weapon
        // via the (already-patched) GetRandomWeaponIndex, and calls
        // MultiplayerManager.SpawnWeapon directly. Returns false to skip
        // original.
        private static int _srwCallCount;
        internal static bool SpawnRandomWeaponPrefix(object __instance)
        {
            try
            {
                _srwCallCount++;
                // Reset randomWeaponCounter to a new value (mirrors original lines 252-264).
                var gmType = __instance.GetType();
                var rwcField = AccessTools.Field(gmType, "randomWeaponCounter");
                var extraField = AccessTools.Field(gmType, "extraSpawnWeaponTime");
                float extra = (object)extraField != null ? (float)extraField.GetValue(__instance) : 0f;
                if ((object)rwcField != null)
                {
                    float newWait = UnityEngine.Random.Range(5f, 8f) + extra;
                    rwcField.SetValue(__instance, newWait);
                }

                // Pick a weapon index (cycled via our other prefix).
                int weaponIdx = _srwCallCount % 8;

                // Spawn position mirroring original: Y=11*scale, Z=Random(-8,8).
                float zOff = UnityEngine.Random.Range(0f, 8f);
                if (_srwCallCount % 2 == 0) zOff *= -1f;
                float scale = 1f;
                var lastAppliedScaleF = AccessTools.Field(gmType, "LastAppliedScale");
                if ((object)lastAppliedScaleF != null)
                {
                    var v = lastAppliedScaleF.GetValue(__instance);
                    if (v is float f) scale = f;
                }
                Vector3 spawnPos = Vector3.up * (11f * scale) + Vector3.forward * zOff;

                // Find MultiplayerManager.SpawnWeapon and invoke directly. SF
                // host code's `mNetworkManager` field is private; we go via
                // FindObjectOfType.
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType == null) return false;
                var mmInst = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)mmInst == null)
                {
                    if (_srwCallCount <= 3)
                        Log.LogWarning("[P6.5 SRW] MultiplayerManager instance is null; skipping.");
                    return false;
                }
                var spawnWeapon = AccessTools.Method(mmType, "SpawnWeapon", new[] { typeof(int), typeof(Vector3), typeof(bool) });
                if ((object)spawnWeapon == null)
                {
                    if (_srwCallCount <= 3)
                        Log.LogWarning("[P6.5 SRW] SpawnWeapon method not found.");
                    return false;
                }
                if (_srwCallCount <= 5 || _srwCallCount % 10 == 0)
                    Log.LogInfo($"[P6.5 SRW] call#{_srwCallCount} → SpawnWeapon(id={weaponIdx}, pos={spawnPos}, present=false)");
                spawnWeapon.Invoke(mmInst, new object[] { weaponIdx, spawnPos, false });
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5 SRW] threw: {e.Message}");
            }
            return false; // skip original SpawnRandomWeapon
        }

        // Phase 6.5 Step 2c — force GetRandomWeaponIndex to return a valid index.
        // Stock SF returns -1 if m_WeaponRaritiesArray is empty (UI never set up).
        // Network branch in SpawnRandomWeapon only uses the int index; weaponObject
        // is consumed only by the local-spawn path which we don't take.
        private static int _grwiCallCount;
        internal static bool GetRandomWeaponIndexPrefix(bool mustBeActive, ref GameObject weaponObject, ref int __result)
        {
            _grwiCallCount++;
            weaponObject = null;
            // Cycle through a few weapon IDs for variety. 0..7 are stock SF weapons.
            __result = _grwiCallCount % 8;
            if (_grwiCallCount <= 3 || _grwiCallCount % 5 == 0)
                Log.LogInfo($"[P6.5] GetRandomWeaponIndexPrefix call#{_grwiCallCount} → returning {__result}");
            return false; // skip original
        }

        // Phase 6.5 Step 1 — log host broadcasts. Observe-only: return true so the
        // original method runs (it's a no-op on the oracle because mConnectedClients
        // is empty; we just want to see which msgTypes SF host code wants to send).
        // Use object[] __args to dodge needing typed refs to EP2PSend (Steamworks).
        private static int _p65BroadcastCount;
        private static readonly Dictionary<byte, int> _p65BroadcastByType = new Dictionary<byte, int>();
        private static int _p65ObjUpdateIdxLogCount;
        private static readonly HashSet<ushort> _p65ObjUpdateSeenIndices = new HashSet<ushort>();
        private static int _p65ObjUpdateFilterCount;
        internal static bool SendBroadcastPrefix(object[] __args)
        {
            try
            {
                _p65BroadcastCount++;
                var data = __args.Length > 0 ? __args[0] as byte[] : null;
                // (byte)__args[1] on a boxed byte-backed enum may throw on
                // strict CLRs; Convert.ToInt32 unboxes via IConvertible and
                // works for either a raw byte or an enum.
                byte msgType = UnboxByte(__args.Length > 1 ? __args[1] : null);
                bool ignoreServer = __args.Length > 2 && __args[2] is bool b && b;
                // SF's signature is (..., ulong ignoreUserID, ...) — raw ulong,
                // NOT CSteamID. So a typed cast works, but use Convert for
                // robustness against future SF refactors.
                ulong ignoreUID = UnboxUlong(__args.Length > 3 ? __args[3] : null);

                int prev;
                _p65BroadcastByType.TryGetValue(msgType, out prev);
                _p65BroadcastByType[msgType] = prev + 1;

                bool first = prev == 0;
                bool sample = first || (_p65BroadcastByType[msgType] % 60 == 0);
                if (sample)
                {
                    Log.LogInfo($"[P6.5] HostBroadcast#{_p65BroadcastCount} msgType={msgType}({MsgTypeName(msgType)}) bodyLen={data?.Length ?? 0} ignoreSrv={ignoreServer} ignoreUID={ignoreUID} count[{msgType}]={_p65BroadcastByType[msgType]}");
                }
                // For ObjectUpdate, log the index every time so we can see
                // which scene NSOs are broadcasting (the index is the first
                // 2 bytes of the body, ushort LE).
                if (msgType == 26 && data != null && data.Length >= 2 && _p65ObjUpdateIdxLogCount < 30)
                {
                    ushort idx = (ushort)(data[0] | (data[1] << 8));
                    if (!_p65ObjUpdateSeenIndices.Contains(idx))
                    {
                        _p65ObjUpdateSeenIndices.Add(idx);
                        _p65ObjUpdateIdxLogCount++;
                        Log.LogInfo($"[P6.5] ObjectUpdate from new index={idx} (total unique={_p65ObjUpdateSeenIndices.Count})");
                    }
                }

                // Phase 6.5 Step 2 — forward the broadcast through our v25
                // protocol so the real client actually receives it. SF's own
                // SendMessageToAllClients loop iterates mConnectedClients which
                // is empty on the oracle (we never registered the user there),
                // so SF's loop is a no-op. We do the actual delivery here.
                // SF's MsgType enum byte values match our v25 protocol's Pkt*
                // constants 1:1 for the first 38 entries.
                //
                // Phase 6.7 filter: if the oracle's own mirror rig generates
                // PlayerUpdate (10) or PlayerTalked (12) broadcasts, do NOT
                // forward them. The client already receives a relay of the
                // real player's PlayerUpdate via HandlePlayerUpdate, and an
                // oracle-rig PlayerUpdate would appear as a phantom 2nd
                // player on the client's screen.
                if ((object)Instance != null && data != null
                    && msgType != 10  // PktPlayerUpdate
                    && msgType != 12) // PktPlayerTalked
                {
                    // Extract the channel arg (index 5 in SendMessageToAllClients).
                    // The patched DLL routes incoming packets by channel — using
                    // channel 0 for everything sends them to CheckMessageType
                    // which throws "Messagetype X is not setup!" for things like
                    // ObjectUpdate that should go to NSO.ListenForPackages instead.
                    byte channel = 0;
                    if (__args.Length > 5)
                    {
                        try { channel = (byte)Convert.ToInt32(__args[5]); } catch { }
                    }
                    // For ObjectUpdate, filter out broadcasts where the object's
                    // Y position is out of int16 range (overflow artifact).
                    bool skip = false;
                    if (msgType == 26 && data.Length >= 4)
                    {
                        short posYmul100 = (short)(data[2] | (data[3] << 8));
                        if (posYmul100 < -3000)
                        {
                            skip = true;
                            if (_p65ObjUpdateFilterCount < 5 || _p65ObjUpdateFilterCount % 100 == 0)
                                Log.LogInfo($"[P6.5] Skipping ObjectUpdate forward — Y={posYmul100/100f:0.0} out of playable range (#{_p65ObjUpdateFilterCount})");
                            _p65ObjUpdateFilterCount++;
                        }
                    }
                    if (!skip) Instance.ForwardBroadcastToV25Clients(msgType, data, ignoreUID, channel);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5] broadcast prefix threw: {e.Message}");
            }
            return true; // run original (no-op on oracle because mConnectedClients is empty)
        }

        // Forward an intercepted host broadcast through our v25 UDP socket.
        // Channel is critical — the patched DLL routes by channel; non-zero
        // channels (e.g. 10 for ObjectUpdate) dispatch via NSO.ListenForPackages,
        // while channel 0 goes to P2PPackageHandler.CheckMessageType.
        internal void ForwardBroadcastToV25Clients(byte msgType, byte[] body, ulong ignoreUID, byte channel = 0)
        {
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (!cli.Initialized) continue;
                if (ignoreUID != 0 && cli.SteamID == ignoreUID) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (sent > 0 && _p65BroadcastCount <= 5)
                Log.LogInfo($"[P6.5] Forwarded msgType={msgType}({MsgTypeName(msgType)}) bodyLen={body.Length} ch={channel} to {sent} v25 client(s).");
        }

        // Phase 6.5 Step 1 — log direct user-targeted sends (CSteamID overload).
        private static int _p65DirectCount;
        private static readonly Dictionary<byte, int> _p65DirectByType = new Dictionary<byte, int>();
        internal static bool SendDirectPrefix(object[] __args)
        {
            try
            {
                _p65DirectCount++;
                // args: [CSteamID clientID, byte[] data, MsgType type, EP2PSend, int channel]
                ulong sid = 0uL;
                if (__args.Length > 0 && __args[0] != null)
                {
                    var idObj = __args[0];
                    var f = AccessTools.Field(idObj.GetType(), "m_SteamID");
                    if ((object)f != null) sid = (ulong)f.GetValue(idObj);
                }
                var data = __args.Length > 1 ? __args[1] as byte[] : null;
                byte msgType = UnboxByte(__args.Length > 2 ? __args[2] : null);

                int prev;
                _p65DirectByType.TryGetValue(msgType, out prev);
                _p65DirectByType[msgType] = prev + 1;

                bool first = prev == 0;
                bool sample = first || (_p65DirectByType[msgType] % 60 == 0);
                if (sample)
                {
                    Log.LogInfo($"[P6.5] DirectSend#{_p65DirectCount} → sid={sid} msgType={msgType}({MsgTypeName(msgType)}) bodyLen={data?.Length ?? 0} count[{msgType}]={_p65DirectByType[msgType]}");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5] direct prefix threw: {e.Message}");
            }
            return true;
        }

        // Phase 6.5 Step 2 — schedule + invoke GameManager.StartMatch on the oracle.
        private static float _oracleStartMatchAt = -1f;
        private static bool _oracleStartMatchFired;
        private static float _oracleCountDownAt = -1f;
        private static bool _oracleCountDownFired;
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

                // Brute-force: set GameManager.inFight = true directly. The
                // CountDownCoroutine path depends on mCountDownHandler (UI
                // element) and m_CustomMapInfoHandler which may be null in
                // batchmode. Bypass them entirely.
                var inFightField = AccessTools.Field(gmType, "inFight");
                if ((object)inFightField != null)
                {
                    inFightField.SetValue(gmInst, true);
                    Log.LogInfo("[P6.5] Forced GameManager.inFight = true (bypassing countdown UI).");
                }
                else
                {
                    Log.LogWarning("[P6.5] GameManager.inFight field not found");
                }
                // Also reset randomWeaponCounter so a weapon will spawn soon.
                var rwcField = AccessTools.Field(gmType, "randomWeaponCounter");
                if ((object)rwcField != null)
                {
                    rwcField.SetValue(gmInst, 2.0f);
                    Log.LogInfo("[P6.5] randomWeaponCounter = 2.0 (first weapon spawn ~2s from now).");
                }

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

                var readyUp = AccessTools.Method(mmType, "ReadyUp");
                if ((object)readyUp != null)
                {
                    try { readyUp.Invoke(mmInst, null); Log.LogInfo("[P6.9] ReadyUp invoked manually."); }
                    catch (Exception e) { Log.LogError($"[P6.9] ReadyUp threw: {e.InnerException?.Message ?? e.Message}"); }
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
            }
            catch (Exception e)
            {
                Log.LogError($"[P6.9] InvokeMultiplayerManagerInitChain threw: {e}");
            }
        }

        // One-shot NetworkSyncableObject inventory — fires once after match-start
        // settles. Tells us how many syncable objects are in the loaded scene,
        // their listening state, and whether mHasControl is true (which gates
        // ObjectUpdate broadcasting on the host side).
        private static bool _nsoInventoryDone;
        private static float _nsoInventoryAt = -1f;
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
        private static readonly List<ProbeNsoEntry> _probeNsos = new List<ProbeNsoEntry>();
        private static float _probeNextLogAt = -1f;
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

        // Periodic state probe — log GameManager.inFight + randomWeaponCounter
        // so we can see whether the host-side game loop is actually running.
        // NB: Mono 2.0.50727 lacks FieldInfo.op_Inequality — must cast to
        // object before any reflection-type null comparison.
        private static float _stateProbeLastAt;
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

                int sceneIdx = 6;
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

        // === CLIENT-MODE SHIM ===
        // Runs on the user's graphical Steam client (NOT batchmode oracle).
        // Goal: make crate/destructible physics work locally so the user sees
        // boxes move when they push them. SF's stock client logic forces all
        // NSO rigidbodies kinematic (DisableAllRigidBodies in NSO.Init) and
        // sets static mHasControl=false (because IsServer is false on the
        // client) — both prevent local physics + local broadcasts.
        //
        // Two surgical patches let the client act as the local-physics
        // authority for boxes, with the oracle continuing as the network
        // coordinator. Doesn't try to flip IsServer entirely (which would
        // break weapon spawning on the client).
        private static void InstallClientModePatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid + ".client-shim");

                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType == null) { Log.LogWarning("[CLIENT] NetworkSyncableObject type not found."); return; }

                // Patch 1: skip DisableAllRigidBodies. Stops the client from
                // setting every NSO's rigidbody to kinematic on Init. Crates
                // remain dynamic → local physics works → pushing them moves
                // them visually on the user's screen.
                var dis = AccessTools.Method(nsoType, "DisableAllRigidBodies");
                if ((object)dis != null)
                {
                    harmony.Patch(dis, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SkipPrefix))));
                    Log.LogInfo("[CLIENT] Patched NetworkSyncableObject.DisableAllRigidBodies (skip).");
                }
                else Log.LogWarning("[CLIENT] DisableAllRigidBodies method not found.");

                // Patch 2: NSO.Start postfix to force static mHasControl=true.
                // Allows the client's NSO.LateUpdate to broadcast position
                // updates so a future multi-player setup would work too.
                // (Single-player: the local push is already visible without
                // any broadcast — this is just for forward compatibility.)
                var startM = AccessTools.Method(nsoType, "Start");
                if ((object)startM != null)
                {
                    harmony.Patch(startM, postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(NsoStartPostfix_Client))));
                    Log.LogInfo("[CLIENT] Patched NetworkSyncableObject.Start (postfix → mHasControl=true).");
                }
                else Log.LogWarning("[CLIENT] NetworkSyncableObject.Start method not found.");

                Log.LogInfo("[CLIENT] Client-mode shim installed. Crates should be dynamic + locally pushable.");
            }
            catch (Exception e)
            {
                Log.LogError($"[CLIENT] Client-mode shim install failed: {e}");
            }
        }
        // Generic skip-prefix: return false to skip the original method.
        internal static bool SkipPrefix() => false;

        // NSO.Start postfix on client: force the static mHasControl=true so
        // the client's NSO.LateUpdate broadcasts position deltas.
        internal static void NsoStartPostfix_Client(object __instance)
        {
            try
            {
                var t = __instance.GetType();
                var f = AccessTools.Field(t, "mHasControl");
                if ((object)f != null) f.SetValue(null, true);
            }
            catch { /* swallow — Mono inlining may have us miss */ }
        }

        // Per-patch install with status tracking. A single try/catch around
        // the whole block silently skipped patches if any one threw early.
        // Failures now accumulate in _p65MissingPatches for a post-install
        // summary line.
        private static int _p65PatchesAttempted;
        private static int _p65PatchesSucceeded;
        private static readonly List<string> _p65MissingPatches = new List<string>();
        private static void TryPatch(Harmony harmony, string label, MethodInfo target, string prefix = null, string postfix = null)
        {
            _p65PatchesAttempted++;
            if ((object)target == null)
            {
                _p65MissingPatches.Add($"{label} — target method not found");
                Log.LogError($"[P6.5] SKIP {label} — target method not found.");
                return;
            }
            try
            {
                var pfx = prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(Plugin), prefix)) : null;
                var pst = postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(Plugin), postfix)) : null;
                harmony.Patch(target, prefix: pfx, postfix: pst);
                _p65PatchesSucceeded++;
                Log.LogInfo($"[P6.5] Patched {label}.");
            }
            catch (Exception e)
            {
                _p65MissingPatches.Add($"{label} — {e.GetType().Name}: {e.Message}");
                Log.LogError($"[P6.5] FAIL {label}: {e}");
            }
        }

        // Safe-unbox helpers. Mono's runtime is permissive about typed casts on
        // boxed enums, but a direct `(byte)box` can throw InvalidCastException
        // on a stricter CLR. Convert.* uses IConvertible which handles both
        // raw primitives and byte-backed enums uniformly.
        private static byte UnboxByte(object o)
        {
            if (o == null) return (byte)255;
            try { return (byte)Convert.ToInt32(o); } catch { return (byte)255; }
        }
        private static ulong UnboxUlong(object o)
        {
            if (o == null) return 0uL;
            try { return Convert.ToUInt64(o); } catch { return 0uL; }
        }

        // SF MsgType enum (P2PPackageHandler.MsgType byte values, from decompile).
        private static string MsgTypeName(byte b) => b switch
        {
            0  => "Ping",
            1  => "PingResponse",
            2  => "ClientJoined",
            3  => "ClientRequestingAccepting",
            4  => "ClientAccepted",
            5  => "ClientInit",
            6  => "ClientRequestingIndex",
            7  => "ClientRequestingToSpawn",
            8  => "ClientSpawned",
            9  => "ClientReadyUp",
            10 => "PlayerUpdate",
            11 => "PlayerTookDamage",
            12 => "PlayerTalked",
            13 => "PlayerForceAdded",
            14 => "PlayerForceAddedAndBlock",
            15 => "PlayerLavaForceAdded",
            16 => "PlayerFallOut",
            17 => "PlayerWonWithRicochet",
            18 => "MapChange",
            19 => "WeaponSpawned",
            20 => "WeaponThrown",
            21 => "RequestingWeaponThrow",
            22 => "ClientRequestWeaponDrop",
            23 => "WeaponDropped",
            24 => "WeaponWasPickedUp",
            25 => "ClientRequestingWeaponPickUp",
            26 => "ObjectUpdate",
            27 => "ObjectSpawned",
            28 => "ObjectSimpleDestruction",
            29 => "ObjectInvokeDestructionEvent",
            30 => "ObjectDestructionCollision",
            31 => "GroundWeaponsInit",
            32 => "MapInfo",
            33 => "MapInfoSync",
            34 => "WorkshopMapsLoaded",
            35 => "StartMatch",
            36 => "ObjectHello",
            37 => "OptionsChanged",
            38 => "KickPlayer",
            _  => "?"
        };

        private static float ReadAxis(object actions, string fieldName, string axis)
        {
            try
            {
                var f = AccessTools.Field(actions.GetType(), fieldName);
                if ((object)f == null) return 0f;
                var ctrl = f.GetValue(actions);
                if ((object)ctrl == null) return 0f;
                var t = ctrl.GetType();
                var backing = AccessTools.Field(t, "<" + axis + ">k__BackingField");
                if ((object)backing != null) return (float)backing.GetValue(ctrl);
                var prop = AccessTools.Property(t, axis);
                if ((object)prop != null) return (float)prop.GetValue(ctrl, null);
            }
            catch { }
            return float.NaN;
        }

        // Force the named TwoAxisInputControl's X/Y to (x, y) by writing
        // its private fields. Run from the Harmony prefix on Controller.Update.
        private static void ForceTwoAxis(object actions, string fieldName, float x, float y)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, new Vector2(x, y));
            var xBacking = AccessTools.Field(t, "<X>k__BackingField");
            var yBacking = AccessTools.Field(t, "<Y>k__BackingField");
            if ((object)xBacking != null) xBacking.SetValue(ctrl, x);
            if ((object)yBacking != null) yBacking.SetValue(ctrl, y);
        }

        // Force a button's IsPressed / Value via InputControlState struct
        // backing fields. PlayerAction.thisState is an InputControlState
        // struct with public bool State and public float Value; reading
        // IsPressed returns thisState.State.
        private static void ForceButton(object actions, string fieldName, bool pressed)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var pa = f.GetValue(actions);
            if ((object)pa == null) return;
            // OneAxisInputControl has private thisState field.
            var thisStateField = AccessTools.Field(pa.GetType(), "thisState");
            if ((object)thisStateField == null) return;
            // thisState is a struct. We have to box, mutate, write back.
            object state = thisStateField.GetValue(pa);
            if ((object)state == null) return;
            var stateType = state.GetType();
            var stateField = AccessTools.Field(stateType, "State");
            var valueField = AccessTools.Field(stateType, "Value");
            if ((object)stateField != null) stateField.SetValue(state, pressed);
            if ((object)valueField != null) valueField.SetValue(state, pressed ? 1.0f : 0.0f);
            thisStateField.SetValue(pa, state);
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
        // Rolling counters for the heartbeat status line — diffed against
        // current totals each interval to compute per-second rates.
        private long  _heartbeatLastPkt;
        private uint  _heartbeatLastSnap;
        private uint  _heartbeatLastInput;

        private int _updateErrorTicks;
        private void Update()
        {
            try
            {
                StepBoot();
            }
            catch (Exception e)
            {
                _updateErrorTicks++;
                // Don't kill the boot state; just log periodically so we can see what's wrong.
                if (_updateErrorTicks <= 5 || _updateErrorTicks % 300 == 0)
                {
                    Log.LogError($"SFHeadlessHost.Update (count={_updateErrorTicks}): {e}");
                }
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
                    // Fire scheduled auto-match-start if armed and time reached.
                    if (_autoStartAt > 0f && Time.realtimeSinceStartup >= _autoStartAt && !_matchStarted)
                    {
                        _autoStartAt = -1f;
                        Log.LogInfo($"[SF] Auto-match-start firing: broadcast MapChange + StartMatch.");
                        BroadcastMapChange(_currentSceneIndex);
                        BroadcastStartMatch();
                        _matchStarted = true;
                        // Phase 6.5 Step 1.5a — also load the match scene on the
                        // oracle so its host-side gameplay code (weapon spawn
                        // timers, killboxes, projectile sim) actually runs. The
                        // IsServer=true postfix is useless if no gameplay scene
                        // is active to call SendMessageToAllClients.
                        try
                        {
                            // Flip MatchmakingHandler.IsNetworkMatch=true so
                            // GameManager.Update takes the SpawnWeapon network
                            // branch (line 289-292 of decompile). Without this,
                            // GameManager would Instantiate weapons locally and
                            // never call our intercept point.
                            var mhType = AccessTools.TypeByName("MatchmakingHandler");
                            if ((object)mhType != null)
                            {
                                var setNetMatch = AccessTools.Method(mhType, "SetNetworkMatch");
                                if ((object)setNetMatch != null)
                                {
                                    setNetMatch.Invoke(null, new object[] { true });
                                    Log.LogInfo("[P6.5] MatchmakingHandler.SetNetworkMatch(true).");
                                }
                            }

                            // No SceneManager.LoadScene here — GameManager.StartMatch
                            // internally does LoadMapCourotine → SceneManager.LoadScene(
                            // num, Additive). A Single-load destroys MainScene's
                            // GameManager (no DontDestroyOnLoad in stock SF), causing
                            // StartCoroutine NullRef on the dead instance.
                            _oracleStartMatchAt = Time.realtimeSinceStartup + 0.5f;
                            _oracleStartMatchFired = false;
                            Log.LogInfo($"[P6.5] Scheduled oracle GameManager.StartMatch in 0.5s (will Additively load scene {_currentSceneIndex} internally).");
                        }
                        catch (Exception e)
                        {
                            Log.LogError($"[P6.5] StartMatch scheduling failed: {e}");
                        }
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
                    // After MapChange settles, send StartMatch to kick the next round's countdown.
                    if (_pendingStartMatchAt > 0f && Time.realtimeSinceStartup >= _pendingStartMatchAt)
                    {
                        _pendingStartMatchAt = -1f;
                        BroadcastStartMatch();
                        Log.LogInfo("[SF] Round advance: StartMatch sent.");
                    }
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
                    if (_matchStarted) { StateProbe(); TickNsoProbe(); TickStaleNsoFreezer(); }
                    // Phase 6.10 — 30Hz authoritative-state broadcast (msgType 39).
                    TickWorldStateSnapshot();
                    return;
            }
        }

        // ========== Bridge: UDP socket the Go server talks to ==========
        // Wire format v0 (JSON, easy to debug; will go binary in v1 if needed):
        //   Go → Oracle commands:
        //     {"cmd":"ping"}
        //     {"cmd":"loadMap","scene":N}
        //     {"cmd":"snapshot"}  -- request a one-shot snapshot
        //     {"cmd":"sub"}       -- subscribe to 30Hz snapshot stream (default after first contact)
        //   Oracle → Go responses (always JSON, one packet each):
        //     {"reply":"pong","tick":N,"scene":"X"}
        //     {"reply":"snapshot","tick":N,"scene":"X","ents":[{"slot":i,"x":...,"y":...,"z":...,"vx":...,"vy":...,"vz":...}]}
        //     {"reply":"ack","cmd":"loadMap","ok":true}

        private UdpClient _bridge;
        private IPEndPoint _bridgePeer; // last sender; we reply to whoever pinged us last

        // Path A: oracle's own raw-UDP socket speaking the v25 protocol
        // directly to patched DLL clients. Bound on BindPort (typically 1337).
        private UdpClient _sfServer;
        private long _sfPacketsRx;
        private long _sfPacketsTx;

        // V25 protocol packet types (mirror packets.go iota order).
        private const byte PktPing                          = 0;
        private const byte PktPingResponse                  = 1;
        private const byte PktClientJoined                  = 2;
        private const byte PktClientRequestingAccepting     = 3;
        private const byte PktClientAccepted                = 4;
        private const byte PktClientInit                    = 5;
        private const byte PktClientRequestingIndex         = 6;
        private const byte PktClientRequestingToSpawn       = 7;
        private const byte PktClientSpawned                 = 8;
        private const byte PktClientReadyUp                 = 9;
        private const byte PktPlayerUpdate                  = 10;
        private const byte PktPlayerTookDamage              = 11;
        private const byte PktPlayerTalked                  = 12;
        private const byte PktPlayerForceAdded              = 13;
        private const byte PktPlayerForceAddedAndBlock      = 14;
        private const byte PktPlayerLavaForceAdded          = 15;
        private const byte PktPlayerFallOut                 = 16;
        private const byte PktPlayerWonWithRicochet         = 17;
        private const byte PktMapChange                     = 18;
        private const byte PktWeaponSpawned                 = 19;
        private const byte PktWeaponThrown                  = 20;
        private const byte PktRequestingWeaponThrow         = 21;
        private const byte PktClientRequestWeaponDrop       = 22;
        private const byte PktWeaponDropped                 = 23;
        private const byte PktWeaponWasPickedUp             = 24;
        private const byte PktClientRequestingWeaponPickUp  = 25;
        private const byte PktObjectUpdate                  = 26;
        private const byte PktObjectSpawned                 = 27;
        private const byte PktObjectSimpleDestruction       = 28;
        private const byte PktObjectInvokeDestructionEvent  = 29;
        private const byte PktObjectDestructionCollision    = 30;
        private const byte PktGroundWeaponsInit             = 31;
        private const byte PktMapInfo                       = 32;
        private const byte PktMapInfoSync                   = 33;
        private const byte PktWorkshopMapsLoaded            = 34;
        private const byte PktStartMatch                    = 35;
        private const byte PktObjectHello                   = 36;
        private const byte PktOptionsChanged                = 37;
        private const byte PktKickPlayer                    = 38;
        // === v26 extension (Phase 6.10+) — server-authoritative protocol ===
        // Stock SF's MsgType enum stops at KickPlayer=38. We extend with new
        // types for the prediction+reconciliation architecture. Stock clients
        // (no v26 plugin loaded) receive these and ignore via default case
        // in P2PPackageHandler.CheckMessageType.
        private const byte PktWorldStateSnapshot            = 39;  // server → all clients, 30Hz
        private const byte PktPlayerInput                   = 40;  // client → server, 60Hz (Phase 6.12)
        // === Patched-DLL extensions (kit's patched Assembly-CSharp.dll has
        // these beyond stock SF's 0-38 range). We don't synthesize them, but
        // we relay so peer clients see each other. From ALKA's
        // relay_handlers.go (his P1-4 fix).
        private const byte PktLerpPlayer                    = 56;  // empty body, triggers remote-lerp on NetworkPlayer
        private const byte PktColorChanged                  = 57;  // HTML color string body (4-64 bytes)

        // Per-client connection state. Keyed by remote address:port string so
        // the same SF instance keeps its slot/SteamID across packets.
        private class SfClient
        {
            public IPEndPoint Addr;
            public ulong SteamID;
            public int Slot;
            public float LastSeen;
            public bool Accepted;
            public bool Initialized;
            public bool Spawned;
            // Phase 6.12 — last PktPlayerInput sequence number consumed for
            // this slot. Phase 6.12.2 will stamp this into outgoing snapshots
            // so the client can do reconciliation replay.
            public uint LastInputSeq;
        }
        private readonly Dictionary<string, SfClient> _sfClients = new Dictionary<string, SfClient>();
        private float _lastStateEmit;
        private long _bridgeTick;

        // Slot → spawned Player rig GameObject (populated by TrySpawnPlayer).
        // Used by the input-injection path to find which rig to drive.
        private static readonly Dictionary<int, GameObject> SlotToRig = new Dictionary<int, GameObject>();

        // Cached player prefab — captured the first time we find ControllerHandler
        // in the active scene (MainScene). Survives subsequent scene changes so we
        // can spawn rigs in Landfall scenes (which have no ControllerHandler).
        private static GameObject _cachedPlayerPrefab;

        // Per-slot input frame the bridge has most recently received. Drained by
        // the per-frame input-write hook so values are written every Update
        // regardless of whether new inputs arrived (analog sticks need to keep
        // their last value across frames; otherwise the rig stops moving when
        // the input rate dips).
        private struct InputFrame
        {
            public float StickX, StickY, AimX, AimY;
            public int Buttons; // bit0=jump, bit1=fire, bit2=block, bit3=throw
        }
        private static readonly Dictionary<int, InputFrame> SlotInputs = new Dictionary<int, InputFrame>();

        // Pending teleport target for the next sceneLoaded callback (set by
        // the loadMap bridge command). Applied to every spawned rig once the
        // new scene's geometry is in place.
        private static Vector3 _pendingTeleport;
        private static bool _pendingTeleportArmed;

        private static void OnSceneLoadedTeleport(Scene scene, LoadSceneMode mode)
        {
            if (!_pendingTeleportArmed) return;
            _pendingTeleportArmed = false;
            SceneManager.sceneLoaded -= OnSceneLoadedTeleport;
            Log.LogInfo($"OnSceneLoadedTeleport: scene={scene.name} target={_pendingTeleport}; teleporting {SlotToRig.Count} rigs.");
            foreach (var kv in SlotToRig)
            {
                if ((object)kv.Value == null) continue;
                TeleportRig(kv.Value, _pendingTeleport);
            }
        }

        // TeleportRig moves the rig root + every BodyPart Rigidbody to the
        // target position. The root transform alone doesn't move the visible
        // rig (body parts have independent Rigidbody-driven positions); we
        // have to relocate them all and zero their velocity so they don't
        // immediately bounce back to the old location.
        private static void TeleportRig(GameObject rig, Vector3 target)
        {
            try
            {
                var rootPos = rig.transform.position;
                var delta = target - rootPos;
                rig.transform.position = target;

                var bpType = AccessTools.TypeByName("BodyPart");
                if ((object)bpType == null) return;
                var bps = rig.GetComponentsInChildren(bpType);
                int moved = 0;
                foreach (var bp in bps)
                {
                    var bpComp = bp as Component;
                    if ((object)bpComp == null) continue;
                    var rb = bpComp.GetComponent<Rigidbody>();
                    if ((object)rb == null) continue;
                    rb.position = rb.position + delta;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    bpComp.transform.position = bpComp.transform.position + delta;
                    moved++;
                }
                Log.LogInfo($"TeleportRig: moved {moved} body parts by delta={delta}");
            }
            catch (Exception e)
            {
                Log.LogError($"TeleportRig threw: {e.Message}");
            }
        }

        // ==================================================================
        // PATH A — sfdsrv-compatible raw UDP v25 protocol, server side
        // ==================================================================
        //
        // Wire format (mirrors packets.go::AsBytes):
        //   [u32 timestamp LE][u8 msgType][N body][u64 steamID LE][u8 channel]
        //
        // Minimum packet size: 14 bytes (5 SOPH header + 9 EOPH trailer).

        private void DrainSfServer()
        {
            if ((object)_sfServer == null) return;
            int processed = 0;
            while (processed++ < 64) // cap per frame
            {
                try
                {
                    if (_sfServer.Available <= 0) return;
                    IPEndPoint from = null;
                    byte[] data = _sfServer.Receive(ref from);
                    if (data == null || data.Length < 14) continue;
                    _sfPacketsRx++;
                    SfDispatch(data, from);
                }
                catch (Exception e)
                {
                    if (Verbose) Log.LogDebug($"SF server recv: {e.Message}");
                    return;
                }
            }
        }

        // Periodic sweep — drop _sfClients entries whose last seen exceeds
        // ClientTimeoutSec. Without this, ungracefully disconnected clients
        // accumulate and keep receiving broadcasts forever.
        private const float ClientTimeoutSec = 30f;
        private float _lastClientSweepAt;
        private void SweepStaleClients()
        {
            if (Time.realtimeSinceStartup - _lastClientSweepAt < 5f) return;
            _lastClientSweepAt = Time.realtimeSinceStartup;
            float cutoff = Time.realtimeSinceStartup - ClientTimeoutSec;
            List<string> toRemove = null;
            foreach (var kv in _sfClients)
            {
                if (kv.Value.LastSeen < cutoff)
                {
                    if (toRemove == null) toRemove = new List<string>();
                    toRemove.Add(kv.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var k in toRemove)
                {
                    var cli = _sfClients[k];
                    Log.LogInfo($"[SF] Dropping stale client {k} (slot={cli.Slot} steamID={cli.SteamID}, last seen {Time.realtimeSinceStartup - cli.LastSeen:0.0}s ago)");
                    _sfClients.Remove(k);
                    // Also forget the v26 endpoint + rate guard for the slot,
                    // otherwise we'd keep sending snapshots into the void.
                    if (cli.Slot >= 0) _slotV26Endpoint.Remove(cli.Slot);
                    _rateGuards.Remove(k);
                }
            }
        }

        // Parse the wrapper and route by msgType. Body bytes are forwarded
        // to handlers without copying — they read from offset 5 to length-9.
        private void SfDispatch(byte[] data, IPEndPoint from)
        {
            // SOPH: u32 timestamp + u8 msgType
            byte msgType = data[4];
            // EOPH: u64 steamID + u8 channel
            int bodyOffset = 5;
            int bodyLen = data.Length - 14;
            ulong steamID = ReadU64LE(data, data.Length - 9);
            byte channel = data[data.Length - 1];

            // Verbose log every Nth packet so we can see what's happening.
            if (_sfPacketsRx == 1 || _sfPacketsRx % 30 == 0)
                Log.LogInfo($"[SF] rx#{_sfPacketsRx} type={msgType} bodyLen={bodyLen} ch={channel} from={from} steamID={steamID}");

            // ALKA-style anticheat — per-client packet rate observation. Logs
            // when a client exceeds thresholds; doesn't drop packets yet (would
            // need careful tuning to not stomp legit bursts). Phase 6.13+ can
            // promote this to actually rate-limit once we have telemetry on
            // healthy traffic shape.
            AnticheatObserve(from, msgType);

            // Phase 6.12 — v26 PktPlayerInput is keyed by slot embedded in the
            // body, not by source IP+port (the SFClientRecon plugin sends from
            // its own ephemeral UDP socket, not the patched DLL's). Route it
            // directly to the slot-based handler and skip the auto-add path
            // below, which would create a phantom SfClient entry every time.
            if (msgType == PktPlayerInput)
            {
                try { HandlePlayerInput(data, bodyOffset, bodyLen, from); }
                catch (Exception ex) { Log.LogWarning($"[SF] HandlePlayerInput threw: {ex.Message}"); }
                return;
            }

            // Track client.
            string key = from.ToString();
            if (!_sfClients.TryGetValue(key, out var cli))
            {
                cli = new SfClient { Addr = from, Slot = -1 };
                _sfClients[key] = cli;
                Log.LogInfo($"[SF] new client appeared: {from}");
            }
            cli.LastSeen = Time.realtimeSinceStartup;
            if (steamID != 0) cli.SteamID = steamID;

            // ALKA P0-5 defense: a bad/malformed packet in one handler should
            // log + drop, not bubble out and skip the rest of the batch (which
            // would happen if it propagated up to DrainSfServer's catch).
            try
            {
            switch (msgType)
            {
                case PktPing:
                    HandlePing(cli, data, bodyOffset, bodyLen);
                    break;
                case PktClientRequestingAccepting:
                    HandleClientRequestingAccepting(cli);
                    break;
                case PktClientRequestingIndex:
                    HandleClientRequestingIndex(cli, data, bodyOffset, bodyLen);
                    break;
                case PktClientRequestingToSpawn:
                    HandleClientRequestingToSpawn(cli, data, bodyOffset, bodyLen);
                    break;
                case PktPlayerUpdate:
                    HandlePlayerUpdate(cli, data, bodyOffset, bodyLen);
                    break;
                case PktClientReadyUp:
                    HandleClientReadyUp(cli, data, bodyOffset, bodyLen);
                    break;

                // === Phase 6.6 — gameplay packets ===
                // Pickup: re-broadcast as WeaponWasPickedUp with the same body
                // (1 byte playerIndex + 2 byte weaponNetworkIndex). SF's
                // OnPlayerRequestingWeaponPickUp would validate against
                // mSpawnedWeapons which is empty on the oracle, so we
                // bypass validation. (1 client, no anti-cheat threat model.)
                case PktClientRequestingWeaponPickUp:
                    HandlePickupRequest(cli, data, bodyOffset, bodyLen);
                    break;

                // SF's host code broadcasts PlayerTookDamage with ignoreUserID=0
                // — INCLUDING the sender. That return-trip is the killing-blow
                // signal: client.SyncClientHealth applies the damage, sees
                // damage==666.666, sets health=0, calls Die(). Without the echo
                // back to the sender, void/lava damage never kills them.
                // PlayerWonWithRicochet similarly broadcasts to all.
                case PktPlayerTookDamage:
                case PktPlayerWonWithRicochet:
                    RelayBodyToAll(msgType, data, bodyOffset, bodyLen, channel);
                    break;

                // "Relay to all OTHER clients" — SF's host passes ignoreUserID =
                // sender so they don't get duplicate force events / fall-outs.
                case PktPlayerForceAdded:
                case PktPlayerForceAddedAndBlock:
                case PktPlayerLavaForceAdded:
                case PktPlayerFallOut:
                case PktObjectUpdate:
                case PktPlayerTalked:        // chat / voice / commands (see PlayerTalked hex log below)
                case PktOptionsChanged:      // lobby option toggles (ALKA BUGS_BACKLOG P0-4)
                case PktLerpPlayer:          // patched-DLL ext, remote-lerp trigger (ALKA P1-4)
                case PktColorChanged:        // patched-DLL ext, player color (ALKA P1-4)
                case PktKickPlayer:          // host kick — patched DLL emits, peer clients see who got booted
                    if (msgType == PktPlayerTalked)
                    {
                        LogPlayerTalkedTelemetry(cli, data, bodyOffset, bodyLen, channel);
                        TryProcessChatCommand(cli, data, bodyOffset, bodyLen);
                    }
                    RelayBodyToOthers(cli, msgType, data, bodyOffset, bodyLen, channel);
                    break;

                // "Relay to ALL including sender" for destruction events.
                // In vanilla SF, the host applies the break locally and broadcasts
                // to non-host clients. In our dedicated-server setup, NO client
                // is the host — the sender hasn't applied the break locally yet
                // either, so they need the echo back to actually see the ice/
                // crate/chain break. Without this, the breaker's screen shows
                // unbroken ice while others see it shattered. Spotted in ALKA's
                // BUGS_BACKLOG P0-3 — same fix shape as our PlayerTookDamage
                // include-sender for the killing-blow signal.
                case PktObjectSimpleDestruction:
                case PktObjectInvokeDestructionEvent:
                case PktObjectDestructionCollision:
                    RelayBodyToAll(msgType, data, bodyOffset, bodyLen, channel);
                    break;

                // Weapon drop: SF's OnPlayerRequestingWeaponDrop just appends
                // the next two IDs (weaponSpawnID + syncableObjectSpawnID) and
                // broadcasts as WeaponDropped. We replicate that logic in pure
                // C# so the IDs come from our counter and stay in sync with
                // weapon spawns.
                case PktClientRequestWeaponDrop:
                    HandleDropRequest(cli, data, bodyOffset, bodyLen);
                    break;

                // Weapon throw: client sends RequestingWeaponThrow (21) with
                // [bool justDrop][byte weaponIdx][ShortVector2 pos][ByteVector2 rot]
                // [optional ByteVector2 aim]. Host appends weaponSpawnID +
                // syncableObjectSpawnID and broadcasts as WeaponThrown (20).
                case PktRequestingWeaponThrow:
                    HandleThrowRequest(cli, data, bodyOffset, bodyLen);
                    break;

                default:
                    if (Verbose) Log.LogDebug($"[SF] unhandled type={msgType} from={from}");
                    break;
            }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[SF] dispatch threw on msgType={msgType} from={from}: {ex.Message}");
            }
        }

        // Phase 6.15 — server-emitted chat. Used for command responses.
        // Wire format: body = raw UTF-8 bytes of the message (no length
        // prefix; total length comes from the v25 wrapper). Channel encodes
        // the talker's slot as (slot*2)+3; we use the recipient's owner
        // channel so it shows up over their own player.
        private void SendChatToPlayer(SfClient target, string text)
        {
            if (target == null || target.Slot < 0) return;
            byte[] body = System.Text.Encoding.UTF8.GetBytes(text);
            byte ch = (byte)((target.Slot * 2) + 3);
            SendSfPacket(target.Addr, PktPlayerTalked, body, 0uL, ch);
        }

        // Phase 6.15 — chat command parser. Body of PktPlayerTalked is
        // raw UTF-8 (verified from decompiled NetworkPlayer.OnTalked). If the
        // text starts with '/' we treat it as a server command. Format mirrors
        // ALKA's MOD_CLIENT.md (/code, /room, /ping, /start initially).
        private void TryProcessChatCommand(SfClient sender, byte[] data, int off, int len)
        {
            try
            {
                if (len == 0) return;
                string text = System.Text.Encoding.UTF8.GetString(data, off, len);
                if (string.IsNullOrEmpty(text) || text[0] != '/') return;
                var space = text.IndexOf(' ');
                string cmd = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
                Log.LogInfo($"[chat] slot={sender.Slot} command='{text}'");
                switch (cmd)
                {
                    case "/code":
                    case "/room":
                        string code = Environment.GetEnvironmentVariable("SF_LOBBY_CODE");
                        SendChatToPlayer(sender, "Lobby code: " + (string.IsNullOrEmpty(code) ? "<unknown>" : code));
                        break;
                    case "/ping":
                        SendChatToPlayer(sender, "pong");
                        break;
                    case "/start":
                        if (_matchStarted)
                        {
                            SendChatToPlayer(sender, "Match already in progress.");
                        }
                        else
                        {
                            SendChatToPlayer(sender, "Starting match...");
                            BroadcastMapChange(_currentSceneIndex);
                            BroadcastStartMatch();
                            _matchStarted = true;
                        }
                        break;
                    case "/help":
                        SendChatToPlayer(sender, "Commands: /code /ping /start /help");
                        break;
                    default:
                        SendChatToPlayer(sender, "Unknown command. Type /help");
                        break;
                }
            }
            catch (Exception ex) { Log.LogWarning($"[chat parse] {ex.Message}"); }
        }

        // Telemetry for the chat-command research effort (notes/phase6/14-
        // chat-commands.md). The patched DLL sends '/start', '/code', etc.
        // via PktPlayerTalked on channel (slot*2)+3 — body format is raw UTF-8
        // (confirmed from NetworkPlayer.OnTalked decompile). We log the first
        // 20 packets' hex+ASCII as a redundant capture so we can confirm
        // format if the parser misbehaves on edge cases.
        private int _playerTalkedLogged;
        private void LogPlayerTalkedTelemetry(SfClient cli, byte[] data, int off, int len, byte channel)
        {
            if (_playerTalkedLogged >= 20) return;
            _playerTalkedLogged++;
            int dumpLen = System.Math.Min(len, 32);
            var hex = new System.Text.StringBuilder(dumpLen * 3);
            for (int i = 0; i < dumpLen; i++) hex.Append(data[off + i].ToString("X2")).Append(' ');
            // Best-effort UTF-8 with non-printable as '.'
            var ascii = new System.Text.StringBuilder(dumpLen);
            for (int i = 0; i < dumpLen; i++)
            {
                byte b = data[off + i];
                ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            Log.LogInfo($"[telemetry chat] slot={cli.Slot} ch={channel} len={len} hex={hex} ascii='{ascii}'");
        }

        // === ALKA-style anticheat — observation-only rate guard ===
        // Per-client sliding window of packet timestamps. Currently logs
        // warnings when thresholds are crossed; doesn't drop packets yet.
        // Ported from server-go/anticheat.go but tuned conservatively (3-4x
        // typical vanilla SF traffic) so it surfaces real anomalies, not
        // legitimate gameplay bursts.
        private class RateGuard
        {
            public Queue<float> All        = new Queue<float>();
            public Queue<float> PlayerUpd  = new Queue<float>();
            public Queue<float> Damage     = new Queue<float>();
            public Queue<float> Object     = new Queue<float>();
            public int Violations;
            public float LastViolationLog;
        }
        private const int MaxAllPerSec        = 240;   // vanilla ≈ 80-100
        private const int MaxPlayerUpdPerSec  = 120;   // vanilla ≈ 60
        private const int MaxDamagePerSec     = 30;    // vanilla bursts <10
        private const int MaxObjectPerSec     = 480;   // boxes/chains can be chatty
        private readonly Dictionary<string, RateGuard> _rateGuards = new Dictionary<string, RateGuard>();
        private void AnticheatObserve(IPEndPoint from, byte msgType)
        {
            try
            {
                string key = from.ToString();
                if (!_rateGuards.TryGetValue(key, out var g))
                {
                    g = new RateGuard();
                    _rateGuards[key] = g;
                }
                float now = Time.realtimeSinceStartup;
                RotateQueue(g.All, now);
                g.All.Enqueue(now);
                if (g.All.Count > MaxAllPerSec) ReportViolation(g, key, "total", g.All.Count);

                if (msgType == PktPlayerUpdate)
                {
                    RotateQueue(g.PlayerUpd, now);
                    g.PlayerUpd.Enqueue(now);
                    if (g.PlayerUpd.Count > MaxPlayerUpdPerSec) ReportViolation(g, key, "playerUpdate", g.PlayerUpd.Count);
                }
                else if (msgType == PktPlayerTookDamage)
                {
                    RotateQueue(g.Damage, now);
                    g.Damage.Enqueue(now);
                    if (g.Damage.Count > MaxDamagePerSec) ReportViolation(g, key, "damage", g.Damage.Count);
                }
                else if (msgType == PktObjectUpdate
                      || msgType == PktObjectSpawned
                      || msgType == PktObjectDestructionCollision
                      || msgType == PktObjectSimpleDestruction
                      || msgType == PktObjectInvokeDestructionEvent
                      || msgType == PktObjectHello)
                {
                    RotateQueue(g.Object, now);
                    g.Object.Enqueue(now);
                    if (g.Object.Count > MaxObjectPerSec) ReportViolation(g, key, "object", g.Object.Count);
                }
            }
            catch { /* observation only — never let it crash the dispatch */ }
        }
        private static void RotateQueue(Queue<float> q, float now)
        {
            while (q.Count > 0 && now - q.Peek() > 1.0f) q.Dequeue();
        }
        private void ReportViolation(RateGuard g, string key, string label, int rate)
        {
            g.Violations++;
            float now = Time.realtimeSinceStartup;
            if (now - g.LastViolationLog < 5f) return;
            g.LastViolationLog = now;
            Log.LogWarning($"[anticheat] {key} exceeded {label} rate ({rate}/s) — violation #{g.Violations}. Observation only; not dropping.");
        }

        // Re-broadcast body to all v25 clients except the sender. Used for
        // pure-relay gameplay msgTypes (force, falloff, etc).
        private void RelayBodyToOthers(SfClient sender, byte msgType, byte[] data, int off, int len, byte channel)
        {
            if (len <= 0) return;
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (cli == sender) continue;
                if (!cli.Initialized) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (Verbose && sent > 0)
                Log.LogDebug($"[SF] relay msgType={msgType} bodyLen={len} → {sent} other client(s)");
        }

        // Re-broadcast body to ALL v25 clients including sender. Used for
        // msgTypes that the sender's own client expects to receive back
        // (PlayerTookDamage carries the killing-blow signal; without the
        // echo, the sender never dies).
        private void RelayBodyToAll(byte msgType, byte[] data, int off, int len, byte channel)
        {
            if (len <= 0) return;
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (!cli.Initialized) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (sent > 0)
            {
                // Sample-log so we can see this firing without flooding.
                if (_relayAllCount++ < 5 || _relayAllCount % 30 == 0)
                    Log.LogInfo($"[SF] relay-to-all msgType={msgType} bodyLen={len} → {sent} client(s) (#{_relayAllCount})");
            }

            // Detect killing-blow for round-advance. PlayerTookDamage body
            // format (from NetworkPlayer.UnitWasDamaged): byte attacker, float
            // damage, bool playParticles, [particle dir bytes], byte dmgType.
            // damage == 666.666f signals "this hit kills."
            if (msgType == PktPlayerTookDamage && len >= 5)
            {
                float dmg = BitConverter.ToSingle(body, 1);
                if (System.Math.Abs(dmg - 666.666f) < 0.01f && _pendingRoundAdvanceAt < 0f)
                {
                    _pendingRoundAdvanceAt = Time.realtimeSinceStartup + 2.5f;
                    Log.LogInfo($"[SF] Killing-blow detected (damage={dmg}); scheduling round advance in 2.5s.");
                }
            }
        }
        private int _relayAllCount;
        private float _pendingRoundAdvanceAt = -1f;
        private float _pendingStartMatchAt = -1f;
        private int _roundCounter;

        // All 123 dumped Landfall map scene indices from /home/miles/sf-multiplayer/maps/.
        // Range 1-124 minus 102 (the stats / non-MP scene). Some early scenes
        // (1-5) may be menu / lobby — they're left in; user can re-die if one
        // doesn't load. SF's stock GetNextLevel uses MapSelectionHandler UI
        // which isn't initialized on the oracle, so we can't call it directly.
        private static readonly int[] _allLandfallMaps;
        private static readonly System.Random _mapRng = new System.Random();
        static Plugin()
        {
            var list = new List<int>();
            // Skip 1-5 (likely menu/lobby) and 102 (stats). Range 6-124.
            for (int i = 6; i <= 124; i++) { if (i != 102) list.Add(i); }
            _allLandfallMaps = list.ToArray();
        }
        // Recently-played history so we don't revisit the same map back-to-back
        // (or within the last few rounds).
        private static readonly Queue<int> _recentMaps = new Queue<int>();
        private const int _recentMapsAvoidWindow = 6;

        private void AdvanceRound()
        {
            _roundCounter++;
            // Pick a random scene we haven't visited in the last few rounds.
            int nextScene = _allLandfallMaps[_mapRng.Next(_allLandfallMaps.Length)];
            for (int attempt = 0; attempt < 8 && _recentMaps.Contains(nextScene); attempt++)
                nextScene = _allLandfallMaps[_mapRng.Next(_allLandfallMaps.Length)];
            _recentMaps.Enqueue(nextScene);
            while (_recentMaps.Count > _recentMapsAvoidWindow) _recentMaps.Dequeue();
            _currentSceneIndex = nextScene;
            Log.LogInfo($"[SF] Round advance #{_roundCounter}: MapChange → scene {nextScene}");
            // ChangeMap body: [byte winnerIndex=255 (no winner)][byte mapType=0 (Landfall)][int32 sceneIndex LE]
            byte[] body = new byte[1 + 1 + 4];
            body[0] = 255;
            body[1] = 0;
            WriteU32LE(body, 2, (uint)nextScene);
            BroadcastSfPacket(PktMapChange, body, 0, 0);
            // SF's host normally follows MapChange with StartMatch after
            // clients re-ready up. Schedule it ~3s later to give the client
            // time to load the scene + respawn.
            _pendingStartMatchAt = Time.realtimeSinceStartup + 3.0f;
            // Reset Spawned flags so next ClientRequestingToSpawn is honored.
            foreach (var kv in _sfClients) kv.Value.Spawned = false;
        }

        // Pickup: re-broadcast incoming ClientRequestingWeaponPickUp body as
        // WeaponWasPickedUp to ALL clients (including the sender, so their
        // local game updates the weapon-attached state). Body is identical.
        private void HandlePickupRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 3) { Log.LogWarning($"[SF] pickup request too short ({len} bytes)"); return; }
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            byte playerIdx = body[0];
            ushort weaponNetId = (ushort)(body[1] | (body[2] << 8));
            Log.LogInfo($"[SF] Pickup: player={playerIdx} weapon={weaponNetId} → broadcasting WeaponWasPickedUp");
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponWasPickedUp, body, 0uL, 1); // channel 1 (weapon-events)
            }
        }

        // Drop: client sends ClientRequestWeaponDrop with [playerIdx][posY i16][posZ i16][velY i8][velZ i8].
        // SF host appends GetNextWeaponSpawnID() + GetNextSyncableObjectSpawnID()
        // and broadcasts as WeaponDropped. We mirror that — the IDs are just
        // counters, no state lookup required.
        private ushort _droppedWeaponNextId = 32768;       // give drops a distinct range to avoid colliding with spawn IDs
        private ushort _droppedSyncableNextId = 32768;
        private void HandleDropRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 7) { Log.LogWarning($"[SF] drop request too short ({len} bytes)"); return; }
            byte[] body = new byte[len + 4];
            Buffer.BlockCopy(data, off, body, 0, len);
            ushort wid = _droppedWeaponNextId++;
            ushort sid = _droppedSyncableNextId++;
            body[len + 0] = (byte)(wid & 0xFF);
            body[len + 1] = (byte)((wid >> 8) & 0xFF);
            body[len + 2] = (byte)(sid & 0xFF);
            body[len + 3] = (byte)((sid >> 8) & 0xFF);
            Log.LogInfo($"[SF] Drop: assigning weaponSpawnID={wid} syncableID={sid}");
            // WeaponDropped is in P2PPackageHandler.CheckMessageType (line 268),
            // dispatched on channel 0 in the patched-DLL routing.
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponDropped, body, 0uL, 0);
            }
        }

        // Throw: client sends RequestingWeaponThrow (21) — same shape as drop
        // structurally: SF's OnPlayerThrowWeapon appends weaponSpawnID +
        // syncableObjectSpawnID and broadcasts as WeaponThrown (20).
        // WeaponThrown is NOT in CheckMessageType; it's dispatched via
        // NetworkPlayer.ListenForEventPackages on the SENDER's mEventChannel
        // (= slot*2 + 3). Wrong channel → packet arrives but nothing listens.
        private void HandleThrowRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 1) { Log.LogWarning($"[SF] throw request too short ({len} bytes)"); return; }
            byte[] body = new byte[len + 4];
            Buffer.BlockCopy(data, off, body, 0, len);
            ushort wid = _droppedWeaponNextId++;
            ushort sid = _droppedSyncableNextId++;
            body[len + 0] = (byte)(wid & 0xFF);
            body[len + 1] = (byte)((wid >> 8) & 0xFF);
            body[len + 2] = (byte)(sid & 0xFF);
            body[len + 3] = (byte)((sid >> 8) & 0xFF);
            byte throwChannel = (byte)(sender.Slot * 2 + 3);
            Log.LogInfo($"[SF] Throw: assigning weaponSpawnID={wid} syncableID={sid} (incoming bodyLen={len}, slot={sender.Slot} → channel={throwChannel})");
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponThrown, body, 0uL, throwChannel);
            }
        }

        // === packet handlers ===

        private void HandlePing(SfClient cli, byte[] data, int off, int len)
        {
            // Echo the body back as PingResponse.
            byte[] body = new byte[len];
            if (len > 0) System.Buffer.BlockCopy(data, off, body, 0, len);
            SendSfPacket(cli.Addr, PktPingResponse, body, cli.SteamID, 0);
        }

        private void HandleClientRequestingAccepting(SfClient cli)
        {
            Log.LogInfo($"[SF] ClientRequestingAccepting from {cli.Addr}; sending ClientAccepted.");
            SendSfPacket(cli.Addr, PktClientAccepted, new byte[0], cli.SteamID, 0);
            cli.Accepted = true;
        }

        // ClientRequestingIndex → ClientInit. Per reference_patched_dll_protocol.md:
        // Response body (50 bytes for solo Landfall-0):
        //   byte accept (1)
        //   byte playerIndex (assigned slot)
        //   byte maxPlayers (4)
        //   byte mapType (0 = Landfall)
        //   i32 mapSize (4)
        //   i32 sceneIndex
        //   for slot 0..3: u64 slotSteamID + (stats if non-joiner non-empty)
        //   u16 weaponCount (0)
        //   4 bytes networkOptions (mapToggle, health, regen, weaponSpawnRate)
        private void HandleClientRequestingIndex(SfClient cli, byte[] data, int off, int len)
        {
            // Body layout (per patched DLL's OnPlayerRequestingIndex):
            //   u64 SteamID  +  u8 clientPlayerCount  (+ optional protocol-version byte)
            // The SteamID here is the AUTHORITATIVE client identity — the
            // wrapper-tail steamID is 0 on join. Without this, our ClientInit
            // tells the client slot 0 has SteamID 0 → client doesn't match
            // it against its local Steam ID → ControlledLocally stays false
            // → no ClientRequestingToSpawn → stuck in lobby.
            if (len >= 8) cli.SteamID = ReadU64LE(data, off);
            byte playerCount = (len >= 9) ? data[off + 8] : (byte)1;

            // Evict any prior _sfClients entry with the same SteamID — this
            // is a reconnect, and we want to reuse the original slot so the
            // client's view of "I am slot N" matches the oracle's view.
            // Without this, slot AllocSlot picks the next free slot (1, 2, …)
            // and the client's channel-routed packets (e.g. throw on
            // slot*2+3) go to wrong channels.
            if (cli.SteamID != 0)
            {
                List<string> evict = null;
                foreach (var kv in _sfClients)
                {
                    var other = kv.Value;
                    if (other == cli) continue;
                    if (other.SteamID == cli.SteamID)
                    {
                        if (evict == null) evict = new List<string>();
                        evict.Add(kv.Key);
                        Log.LogInfo($"[SF] Evicting stale reconnect: SteamID={other.SteamID} was on {kv.Key} slot={other.Slot}; new conn from {cli.Addr} reusing slot {other.Slot}.");
                        cli.Slot = other.Slot;
                    }
                }
                if (evict != null) foreach (var k in evict) _sfClients.Remove(k);
            }

            // Assign a slot only if eviction didn't reuse one.
            int slot = cli.Slot >= 0 ? cli.Slot : AllocSlot(cli);
            cli.Slot = slot;
            Log.LogInfo($"[SF] ClientRequestingIndex from {cli.Addr} steamID={cli.SteamID} players={playerCount}; assigning slot {slot}; building ClientInit.");

            // The chosen Landfall scene to push (matches what oracle has loaded).
            int sceneIndex = 0; // MainScene first; the lobby UI flows from there.

            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write((byte)1);            // accept
                bw.Write((byte)slot);         // playerIndex
                bw.Write((byte)4);            // maxPlayers — patched-only field
                bw.Write((byte)0);            // mapType 0=Landfall
                bw.Write((int)4);             // mapSize
                bw.Write((int)sceneIndex);    // mapData (sceneIndex)

                // 4-slot loop
                for (int s = 0; s < 4; s++)
                {
                    if (s == slot)
                    {
                        bw.Write(cli.SteamID);          // u64 — joiner's own steamID
                    }
                    else
                    {
                        // Find any other connected client in slot s.
                        SfClient other = null;
                        foreach (var kv in _sfClients) if (kv.Value.Slot == s) { other = kv.Value; break; }
                        if (other != null)
                        {
                            bw.Write(other.SteamID);
                            // 13 × int32 stats (zeros for now)
                            for (int i = 0; i < 13; i++) bw.Write((int)0);
                            bw.Write((int)0); // colorCount (patched-only) — 0 = default
                        }
                        else
                        {
                            bw.Write((ulong)0);
                        }
                    }
                }
                bw.Write((ushort)0);          // weaponCount
                bw.Write((byte)0);            // mapToggle
                bw.Write((byte)100);          // health
                bw.Write((byte)1);            // regen
                bw.Write((byte)1);            // weaponSpawnRate

                byte[] body = ms.ToArray();
                SendSfPacket(cli.Addr, PktClientInit, body, cli.SteamID, 0);
                cli.Initialized = true;
            }

            // Post-init bundle. Per sfdsrv comment: without these (specifically
            // OptionsChanged) the client never sends ClientRequestingToSpawn
            // and the user gets stuck at a black/lobby screen.
            // WorkshopMapsLoaded: u16 count + count×u64 workshopIDs. We send 0 maps.
            SendSfPacket(cli.Addr, PktWorkshopMapsLoaded, new byte[] { 0, 0 }, cli.SteamID, 1);
            // OptionsChanged: 4 bytes [maps, health, regen, weaponSpawnRate].
            // weaponSpawnRate=2 stops the client from requesting weapon spawns.
            SendSfPacket(cli.Addr, PktOptionsChanged, new byte[] { 0, 100, 1, 2 }, cli.SteamID, 0);
            Log.LogInfo($"[SF] Post-init bundle sent (WorkshopMapsLoaded + OptionsChanged).");
        }

        // After first player spawns in the lobby, auto-start a match. The
        // stock SF lobby requires 2+ players to walk under the ready-hat
        // trigger; for solo testing that never fires. So we schedule the
        // match-start ourselves a few seconds after first spawn.
        private float _autoStartAt = -1f;

        // ClientRequestingToSpawn → ClientSpawned broadcast.
        // Incoming body: byte playerIndex + 6 × float32 (pos + euler) = 25 bytes
        // Outgoing body: byte playerIndex + 6×f32 + bool spawnFlag + i32 colorCount = 30 bytes
        private void HandleClientRequestingToSpawn(SfClient cli, byte[] data, int off, int len)
        {
            if (len < 25) { Log.LogWarning($"[SF] short spawn body len={len}"); return; }
            byte pIdx  = data[off];
            float px = ReadF32LE(data, off + 1);
            float py = ReadF32LE(data, off + 5);
            float pz = ReadF32LE(data, off + 9);
            float rx = ReadF32LE(data, off + 13);
            float ry = ReadF32LE(data, off + 17);
            float rz = ReadF32LE(data, off + 21);
            Log.LogInfo($"[SF] ClientRequestingToSpawn slot={pIdx} pos=({px:0.0},{py:0.0},{pz:0.0})");

            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write(pIdx);
                bw.Write(px); bw.Write(py); bw.Write(pz);
                bw.Write(rx); bw.Write(ry); bw.Write(rz);
                bw.Write((byte)0);    // spawnFlag false = RevivePlayer at pos
                bw.Write((int)0);     // colorCount (patched-only)
                byte[] body = ms.ToArray();
                // Echo to the asking client AND broadcast to others.
                BroadcastSfPacket(PktClientSpawned, body, cli.SteamID, 0);
            }
            cli.Spawned = true;

            // First spawn into the lobby — schedule auto-match-start in 4s
            // so player has time to register the spawn before the scene loads.
            if (!_matchStarted && _autoStartAt < 0f)
            {
                _autoStartAt = Time.realtimeSinceStartup + 4.0f;
                Log.LogInfo($"[SF] Auto-match-start scheduled in 4s.");
            }
        }

        // ClientReadyUp from client (walked through the ready hat in lobby).
        // Body: byte playerCount + playerCount × byte playerIndex.
        // Response: broadcast MapChange (load Landfall scene) + StartMatch.
        // Once both go out, clients drop the lobby map, load the new scene,
        // and send ClientRequestingToSpawn for it — we reply with ClientSpawned.
        private bool _matchStarted = false;
        private int _currentSceneIndex = 6; // Desert3 — known-good Landfall map
        private void HandleClientReadyUp(SfClient cli, byte[] data, int off, int len)
        {
            Log.LogInfo($"[SF] ClientReadyUp from {cli.Addr} bodyLen={len}; broadcasting MapChange+StartMatch.");
            if (_matchStarted) {
                Log.LogInfo($"[SF] Match already started; re-sending StartMatch to {cli.Addr} only.");
                SendSfPacket(cli.Addr, PktStartMatch, new byte[0], 0, 0);
                return;
            }
            BroadcastMapChange(_currentSceneIndex);
            BroadcastStartMatch();
            _matchStarted = true;
        }

        // MapChange body: byte winnerIndex + byte mapType + mapData.
        // For a fresh start: winnerIndex=255 (no winner), mapType=0 (Landfall),
        // mapData=i32 sceneIndex LE.
        private void BroadcastMapChange(int sceneIndex)
        {
            byte[] body = new byte[1 + 1 + 4];
            body[0] = 255;             // winnerIndex (no winner)
            body[1] = 0;               // mapType Landfall
            WriteU32LE(body, 2, (uint)sceneIndex);
            BroadcastSfPacket(PktMapChange, body, 0, 0);
            Log.LogInfo($"[SF] Broadcast MapChange → scene {sceneIndex}");
        }

        private void BroadcastStartMatch()
        {
            BroadcastSfPacket(PktStartMatch, new byte[0], 0, 0);
            // Clear per-round spawn flag so next ClientRequestingToSpawn
            // is treated as a fresh round-start rather than a respawn.
            foreach (var kv in _sfClients) kv.Value.Spawned = false;
            Log.LogInfo("[SF] Broadcast StartMatch");
        }

        // Phase 6.12 — inbound v26 PktPlayerInput from SFClientRecon plugin.
        // Body layout:
        //   u32 sequenceNum (LE)
        //   u8  slot
        //   f32 stickX (LE)
        //   f32 stickY (LE)
        //   f32 aimX (LE)
        //   f32 aimY (LE)
        //   u32 buttons (LE)  — bit0=jump, bit1=fire, bit2=block, bit3=throw
        //
        // We trust the slot byte for now (no anti-cheat enforcement). Phase
        // 6.13+ will validate slot ↔ SteamID. Populated InputFrame feeds
        // InjectInputPrefix → Movement.cs on the server-side authoritative
        // rig, producing real authoritative motion that's then broadcast back
        // via PktWorldStateSnapshot.
        // Per-slot v26 endpoint — where to send WorldStateSnapshot. Discovered
        // from the source IP+port of each client's first PlayerInput packet,
        // so multi-instance same-machine testing works (no more hardcoded port
        // collision when two clients on same host both want 1339).
        private readonly Dictionary<int, IPEndPoint> _slotV26Endpoint = new Dictionary<int, IPEndPoint>();

        private uint _inputPacketsRx;
        private uint _inputPacketsDropped;
        private void HandlePlayerInput(byte[] data, int off, int len, IPEndPoint from)
        {
            if (len < 25) return;
            uint seq    = (uint)(data[off] | (data[off + 1] << 8) | (data[off + 2] << 16) | (data[off + 3] << 24));
            byte slot   = data[off + 4];
            float sx    = BitConverter.ToSingle(data, off + 5);
            float sy    = BitConverter.ToSingle(data, off + 9);
            float ax    = BitConverter.ToSingle(data, off + 13);
            float ay    = BitConverter.ToSingle(data, off + 17);
            uint btns   = (uint)(data[off + 21] | (data[off + 22] << 8) | (data[off + 23] << 16) | (data[off + 24] << 24));

            // Defensive validation — drop obvious garbage / cheaty inputs so
            // they don't poison InjectInputPrefix → Movement.cs. Conservative:
            // accept slightly-over-1.0 magnitudes (analog stick noise) but
            // reject NaN/Inf/huge values. Phase 6.16+ slot↔SteamID validation
            // would also live here.
            if (slot > 3
                || float.IsNaN(sx) || float.IsInfinity(sx) || sx < -1.5f || sx > 1.5f
                || float.IsNaN(sy) || float.IsInfinity(sy) || sy < -1.5f || sy > 1.5f
                || float.IsNaN(ax) || float.IsInfinity(ax) || ax < -1.5f || ax > 1.5f
                || float.IsNaN(ay) || float.IsInfinity(ay) || ay < -1.5f || ay > 1.5f)
            {
                _inputPacketsDropped++;
                if (_inputPacketsDropped == 1 || _inputPacketsDropped % 50 == 0)
                    Log.LogWarning($"[P6.12] Dropped malformed PlayerInput: slot={slot} stick=({sx:0.00},{sy:0.00}) aim=({ax:0.00},{ay:0.00}) — total dropped {_inputPacketsDropped}");
                return;
            }
            // Clamp stick magnitudes to canonical [-1,1] so SF's Movement
            // doesn't see sub-noise > 1.0 values that bypass its own clamps.
            sx = Mathf.Clamp(sx, -1f, 1f);
            sy = Mathf.Clamp(sy, -1f, 1f);
            ax = Mathf.Clamp(ax, -1f, 1f);
            ay = Mathf.Clamp(ay, -1f, 1f);

            SlotInputs[slot] = new InputFrame
            {
                StickX  = sx,
                StickY  = sy,
                AimX    = ax,
                AimY    = ay,
                Buttons = (int)btns,
            };
            // Find the SfClient owning this slot to stamp LastInputSeq.
            foreach (var kv in _sfClients)
            {
                if (kv.Value.Slot == slot) { kv.Value.LastInputSeq = seq; break; }
            }
            // Record this client's v26 source addr — server snapshots get sent
            // back to this same IP:port (client uses single bidirectional socket).
            if ((object)from != null)
            {
                if (!_slotV26Endpoint.TryGetValue(slot, out var existing) || !existing.Equals(from))
                {
                    _slotV26Endpoint[slot] = from;
                    Log.LogInfo($"[P6.12] Slot {slot} v26 endpoint → {from}");
                }
            }
            _inputPacketsRx++;
            if (_inputPacketsRx == 1 || _inputPacketsRx % 300 == 0)
                Log.LogInfo($"[P6.12] PlayerInput #{_inputPacketsRx} slot={slot} seq={seq} stick=({sx:0.00},{sy:0.00}) btns=0x{btns:X}");
        }

        // PlayerUpdate from client → broadcast to all OTHER clients.
        // Phase 6.9+: this is now ONLY a relay between clients (so they see
        // each other render correctly). The server's own authoritative rig
        // for this client is driven by SlotInputs, not by the client's
        // self-reported position. Phase 6.10 will flip this further so the
        // server broadcasts ITS authoritative position back to all clients,
        // and Phase 6.11/6.12 patch the client to reconcile against that.
        private void HandlePlayerUpdate(SfClient cli, byte[] data, int off, int len)
        {
            byte[] body = new byte[len];
            if (len > 0) System.Buffer.BlockCopy(data, off, body, 0, len);
            foreach (var kv in _sfClients)
            {
                if (kv.Value == cli) continue;
                if (!kv.Value.Spawned) continue;
                SendSfPacket(kv.Value.Addr, PktPlayerUpdate, body, cli.SteamID, 0);
            }
        }

        // === Phase 6.9 — authoritative server-side player rigs ===
        // Spawn one real NetworkPlayer per connected client on the oracle.
        // The rig is the server's authoritative copy of the player; eventually
        // it'll be driven from client inputs (Phase 6.12 v26 protocol) and its
        // position will be broadcast back to all clients as the source of
        // truth (Phase 6.10 snapshots + Phase 6.11 client reconciliation).
        //
        // For now (post-mirror-rig rip), the rig is instantiated via
        // TrySpawnPlayer (real Player prefab + Controller + Movement + NSO
        // children) and left at its spawn position. The SlotInputs buffer
        // is empty until inputs start flowing, so Movement.cs has nothing to
        // act on. This is intentional: clean foundation, no fake teleport.
        private float _authSpawnAt = -1f;
        private bool _authSpawnDone;

        private void SpawnAuthoritativePlayersForAllClients()
        {
            Log.LogInfo($"[P6.9] SpawnAuthoritativePlayers: iterating {_sfClients.Count} clients.");
            int considered = 0, spawned = 0, skipped = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                considered++;
                if (!cli.Initialized)
                {
                    Log.LogInfo($"[P6.9] Skip {kv.Key}: not Initialized.");
                    skipped++;
                    continue;
                }
                if (SlotToRig.ContainsKey(cli.Slot))
                {
                    Log.LogInfo($"[P6.9] Skip {kv.Key}: rig already exists for slot {cli.Slot}.");
                    skipped++;
                    continue;
                }
                Vector3 startPos = new Vector3(0f, 8f, 0f);
                bool ok = TrySpawnPlayer(cli.Slot, startPos, out string err);
                if (ok)
                {
                    Log.LogInfo($"[P6.9] Spawned authoritative rig for client slot={cli.Slot} steamID={cli.SteamID}.");
                    ConfigureAuthoritativeRig(cli.Slot);
                    spawned++;
                }
                else
                {
                    Log.LogError($"[P6.9] Failed to spawn authoritative rig for slot {cli.Slot}: {err}");
                }
            }
            Log.LogInfo($"[P6.9] SpawnAuthoritativePlayers done: considered={considered} spawned={spawned} skipped={skipped}");
        }

        // Configure a freshly-spawned rig as the server's authoritative copy
        // of that player. Per-instance HasControl=true on the Controller so
        // SF's host-side gates (destructible piece OnCollisionEnter, etc.)
        // accept this rig as a legitimate authority source. The static
        // mHasControl is also forced true elsewhere via the Phase 6.5 NSO
        // patch, but per-instance avoids any "did the static actually stick"
        // surprises.
        private void ConfigureAuthoritativeRig(int slot)
        {
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType != null)
                {
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
                        if ((object)hasCtrlF != null)
                        {
                            hasCtrlF.SetValue(ctrl, true);
                            Log.LogInfo($"[P6.9] Slot {slot}: Controller.mHasControl set true (per-instance).");
                        }
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[P6.9 ConfigureAuthoritativeRig] {e.Message}"); }
        }

        // === Phase 6.10 — server-authoritative snapshots ===
        // 30Hz broadcast of the oracle's view of every authoritative player rig's
        // position. The wire format is intentionally simple for now — Phase 6.11
        // will ship the client-side reconciliation plugin that consumes these,
        // and Phase 6.12 adds the playerInput inbound side + tighter packing
        // (compressed int16 + delta encoding + the lastInputSeq field that drives
        // reconciliation rollback).
        //
        // Body (v26 draft):
        //   u32 serverTick (LE)
        //   u8  playerCount
        //   for each player:
        //     u8  slot
        //     f32 posX (LE)
        //     f32 posY (LE)
        //     f32 posZ (LE)
        //
        // Stock clients ignore msgType 39 (their MsgType enum stops at 38) so
        // this is wire-safe to broadcast even before the client plugin lands.
        // Port the v26 client plugin (SFClientRecon) binds on. Hardcoded for
        // now; env-var override comes when we add multi-client-on-same-host
        // testing (e.g. two SF instances on the dev machine).
        private const int V26_CLIENT_PORT = 1339;
        private float _lastSnapshotAt = -1f;
        private uint  _serverTick;
        private void TickWorldStateSnapshot()
        {
            if (!_matchStarted) return;
            if (_sfClients.Count == 0) return;
            if (Time.realtimeSinceStartup - _lastSnapshotAt < (1.0f / 30.0f)) return;
            _lastSnapshotAt = Time.realtimeSinceStartup;
            _serverTick++;
            BroadcastWorldStateSnapshot();
        }

        // Cached NSO Index field — looked up lazily once a scene has NSOs.
        private static FieldInfo _nsoIndexField;
        private static System.Reflection.PropertyInfo _nsoIndexProp;
        private static Type _nsoType;

        private void BroadcastWorldStateSnapshot()
        {
            try
            {
                int n = 0;
                foreach (var kv in SlotToRig) if ((object)kv.Value != null) n++;

                // Phase 6.14 — also pack NSO positions for server-determined
                // box / chain / ice-debris falling. Only include NSOs whose
                // rigidbody is non-kinematic (i.e. currently allowed to move).
                // Pre-placed static crates/chains stay kinematic until struck
                // and don't need bandwidth.
                var nsoEntries = CollectActiveNsoSnapshot();

                if (n == 0 && nsoEntries.Count == 0) return;

                // Body layout v26.1:
                //   u32 serverTick
                //   u8  playerCount
                //   players: [u8 slot, f32 x, f32 y, f32 z] × n              (13/each)
                //   u16 nsoCount
                //   NSOs:    [u16 id, f32 x, f32 y, f32 z, f32 rotZ] × m     (18/each)
                int bodyLen = 4 + 1 + n * 13 + 2 + nsoEntries.Count * 18;
                byte[] body = new byte[bodyLen];
                int off = 0;
                WriteU32LE(body, off, _serverTick); off += 4;
                body[off++] = (byte)n;
                foreach (var kv in SlotToRig)
                {
                    var rig = kv.Value;
                    if ((object)rig == null) continue;
                    body[off++] = (byte)kv.Key;
                    Vector3 p = rig.transform.position;
                    WriteF32LE(body, off, p.x); off += 4;
                    WriteF32LE(body, off, p.y); off += 4;
                    WriteF32LE(body, off, p.z); off += 4;
                }
                WriteU16LE(body, off, (ushort)nsoEntries.Count); off += 2;
                foreach (var e in nsoEntries)
                {
                    WriteU16LE(body, off, e.Id); off += 2;
                    WriteF32LE(body, off, e.X); off += 4;
                    WriteF32LE(body, off, e.Y); off += 4;
                    WriteF32LE(body, off, e.Z); off += 4;
                    WriteF32LE(body, off, e.RotZ); off += 4;
                }

                // Broadcast to ALL spawned clients on their v26 endpoint. Once
                // a client has sent a PlayerInput packet we know its actual
                // v26 source addr (recorded in _slotV26Endpoint); before that
                // we fall back to clientIP:V26_CLIENT_PORT. Lets two clients
                // on the same machine use different v26 ports without colliding.
                foreach (var kv in _sfClients)
                {
                    if (!kv.Value.Spawned) continue;
                    IPEndPoint v26Ep;
                    if (!_slotV26Endpoint.TryGetValue(kv.Value.Slot, out v26Ep))
                        v26Ep = new IPEndPoint(kv.Value.Addr.Address, V26_CLIENT_PORT);
                    SendSfPacket(v26Ep, PktWorldStateSnapshot, body, 0, 0);
                }
                if (_serverTick == 1 || _serverTick % 90 == 0)
                    Log.LogInfo($"[P6.10/14] Snapshot tick={_serverTick} players={n} nsos={nsoEntries.Count} bytes={bodyLen}");
            }
            catch (Exception e) { Log.LogWarning($"[P6.10/14] {e.Message}"); }
        }

        private struct NsoSnap { public ushort Id; public float X, Y, Z, RotZ; }

        // Gather active (non-kinematic) NSOs in the scene as snapshot entries.
        // Skips kinematic NSOs (static crates/chains) to keep packet size down
        // — those don't move so clients can keep their local position. Phase
        // 6.14.1 will add per-NSO active-flag tracking so a recently-moved NSO
        // stays in the snapshot for a few seconds after it settles.
        private List<NsoSnap> CollectActiveNsoSnapshot()
        {
            var result = new List<NsoSnap>();
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return result;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                if (all == null) return result;
                foreach (var nso in all)
                {
                    var comp = nso as Component;
                    if ((object)comp == null) continue;
                    var rb = comp.GetComponent<Rigidbody>();
                    if ((object)rb == null) continue;
                    if (rb.isKinematic) continue;

                    ushort id = 0;
                    if ((object)_nsoIndexProp != null)
                        id = (ushort)_nsoIndexProp.GetValue(nso, null);
                    else if ((object)_nsoIndexField != null)
                        id = (ushort)_nsoIndexField.GetValue(nso);

                    var p = comp.transform.position;
                    var e = comp.transform.eulerAngles;
                    result.Add(new NsoSnap { Id = id, X = p.x, Y = p.y, Z = p.z, RotZ = e.z });
                }
            }
            catch (Exception ex) { Log.LogWarning($"[P6.14 NSO collect] {ex.Message}"); }
            return result;
        }

        // Freeze NSO rigidbodies that fell out of the playable area.
        // Stock SF's host kills crates that cross the killbox (Y<-50);
        // we don't have that cleanup, so falling crates accelerate
        // forever, eventually slamming into destructibles (chains, ice)
        // and breaking them with no player input.
        // Fix: periodically scan all NSOs in scene; any with Y < -25
        // gets isKinematic=true. Stops the fall + the broadcast spam.
        private float _nsoFreezerNextAt = -1f;
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

        // === helpers ===

        private int AllocSlot(SfClient cli)
        {
            if (cli.Slot >= 0) return cli.Slot;
            for (int s = 0; s < 4; s++)
            {
                bool taken = false;
                foreach (var kv in _sfClients) if (kv.Value.Slot == s) { taken = true; break; }
                if (!taken) return s;
            }
            return 0; // overflow — should reject in real impl
        }

        // Serialize a packet with the 14-byte wrapper and send to one client.
        private void SendSfPacket(IPEndPoint to, byte msgType, byte[] body, ulong steamID, byte channel)
        {
            if ((object)_sfServer == null) return;
            int totalLen = 5 + (body?.Length ?? 0) + 9;
            byte[] pkt = new byte[totalLen];
            uint ts = (uint)(System.DateTime.UtcNow - new System.DateTime(1970, 1, 1)).TotalSeconds;
            WriteU32LE(pkt, 0, ts);
            pkt[4] = msgType;
            if (body != null && body.Length > 0) System.Buffer.BlockCopy(body, 0, pkt, 5, body.Length);
            int tailOff = 5 + (body?.Length ?? 0);
            WriteU64LE(pkt, tailOff, steamID);
            pkt[tailOff + 8] = channel;
            try
            {
                _sfServer.Send(pkt, pkt.Length, to);
                _sfPacketsTx++;
            }
            catch (Exception e) { if (Verbose) Log.LogDebug($"[SF] send: {e.Message}"); }
        }

        private void BroadcastSfPacket(byte msgType, byte[] body, ulong steamID, byte channel)
        {
            foreach (var kv in _sfClients)
                SendSfPacket(kv.Value.Addr, msgType, body, steamID, channel);
        }

        // === codec primitives ===

        private static void WriteU32LE(byte[] buf, int off, uint v)
        {
            buf[off    ] = (byte)(v       & 0xFF);
            buf[off + 1] = (byte)(v >>  8 & 0xFF);
            buf[off + 2] = (byte)(v >> 16 & 0xFF);
            buf[off + 3] = (byte)(v >> 24 & 0xFF);
        }

        private static void WriteF32LE(byte[] buf, int off, float v)
        {
            // BitConverter.GetBytes is little-endian on x86/x64. We target
            // x64 Linux/Windows for the oracle; if we ever support PowerPC
            // or BE clients this needs an endian guard.
            var bytes = System.BitConverter.GetBytes(v);
            buf[off    ] = bytes[0];
            buf[off + 1] = bytes[1];
            buf[off + 2] = bytes[2];
            buf[off + 3] = bytes[3];
        }

        private static void WriteU16LE(byte[] buf, int off, ushort v)
        {
            buf[off    ] = (byte)(v       & 0xFF);
            buf[off + 1] = (byte)(v >>  8 & 0xFF);
        }
        private static void WriteU64LE(byte[] buf, int off, ulong v)
        {
            for (int i = 0; i < 8; i++) buf[off + i] = (byte)((v >> (i * 8)) & 0xFF);
        }
        private static ulong ReadU64LE(byte[] buf, int off)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= ((ulong)buf[off + i]) << (i * 8);
            return v;
        }
        private static float ReadF32LE(byte[] buf, int off)
        {
            return System.BitConverter.ToSingle(buf, off);
        }

        private void StartBridge()
        {
            try
            {
                // Loopback-only: bridge commands (loadMap/teleport/addForce/...)
                // mutate gameplay state with no auth. Co-located Go server is on
                // the same host, so 0.0.0.0 exposure is gratuitous network risk.
                _bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, BridgePort));
                _bridge.Client.Blocking = false;
                Log.LogInfo($"Bridge: listening on UDP 127.0.0.1:{BridgePort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"Bridge: bind on 127.0.0.1:{BridgePort} failed: {e.Message}");
                _bridge = null;
            }
        }

        private void DrainBridgeCommands()
        {
            if ((object)_bridge == null) return;
            int processed = 0;
            while (processed++ < 16) // cap per frame
            {
                byte[] data;
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    if (_bridge.Available <= 0) return;
                    data = _bridge.Receive(ref remote);
                }
                catch (SocketException)
                {
                    return; // would-block / nothing to read
                }
                catch (Exception e)
                {
                    if (Verbose) Log.LogDebug($"Bridge recv: {e.Message}");
                    return;
                }
                _bridgePeer = remote;
                HandleBridgeCommand(data, remote);
            }
        }

        private void HandleBridgeCommand(byte[] data, IPEndPoint from)
        {
            string body = Encoding.UTF8.GetString(data);
            if (Verbose) Log.LogDebug($"Bridge ← {from}: {body}");
            // Tiny ad-hoc JSON parser — body shapes are trivial so we don't need a full lib.
            string cmd = ExtractStringField(body, "cmd");
            if (cmd == "ping")
            {
                SendBridgeJson(from, $"{{\"reply\":\"pong\",\"tick\":{_bridgeTick},\"scene\":\"{SceneManager.GetActiveScene().name}\"}}");
            }
            else if (cmd == "snapshot")
            {
                EmitStateSnapshotTo(from);
            }
            else if (cmd == "loadMap")
            {
                int scene = ExtractIntField(body, "scene", -1);
                if (scene >= 0)
                {
                    // Optional teleport-after-load coordinates. ALL THREE of
                    // x/y/z must be present, else we reject — silently
                    // defaulting missing coords to 0 was sending rigs to the
                    // origin (right above the killbox).
                    bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                    bool hasTeleport = hasX || hasY || hasZ;
                    if (hasTeleport && !(hasX && hasY && hasZ))
                    {
                        SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":false,\"err\":\"partial teleport coords — need x,y,z together\"}");
                        return;
                    }
                    float tx = hasTeleport ? ExtractFloatField(body, "x") : 0f;
                    float ty = hasTeleport ? ExtractFloatField(body, "y") : 0f;
                    float tz = hasTeleport ? ExtractFloatField(body, "z") : 0f;
                    Log.LogInfo($"Bridge: loadMap({scene}) requested; teleport=({tx},{ty},{tz}) hasTeleport={hasTeleport}");
                    if (hasTeleport)
                    {
                        _pendingTeleport = new Vector3(tx, ty, tz);
                        _pendingTeleportArmed = true;
                        SceneManager.sceneLoaded -= OnSceneLoadedTeleport;
                        SceneManager.sceneLoaded += OnSceneLoadedTeleport;
                    }
                    // Track current scene so subsequent BroadcastMapChange
                    // reflects reality, not the hardcoded boot default.
                    _currentSceneIndex = scene;
                    SceneManager.LoadScene(scene, LoadSceneMode.Single);
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":true,\"scene\":{scene}}}");
                }
                else
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":false,\"err\":\"missing or invalid scene\"}");
                }
            }
            else if (cmd == "teleport")
            {
                // Direct teleport command — no scene load. Useful when you
                // want to re-park the rig (e.g. after it falls into a void).
                // Require all of x/y/z to be present so a malformed payload
                // doesn't park the rig at origin (killbox-adjacent).
                int slot = ExtractIntField(body, "slot", -1);
                bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                if (!(hasX && hasY && hasZ))
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":false,\"err\":\"missing x/y/z\"}");
                    return;
                }
                float tx = ExtractFloatField(body, "x");
                float ty = ExtractFloatField(body, "y");
                float tz = ExtractFloatField(body, "z");
                if (slot >= 0 && SlotToRig.TryGetValue(slot, out var rigGo) && (object)rigGo != null)
                {
                    TeleportRig(rigGo, new Vector3(tx, ty, tz));
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":true,\"slot\":{slot}}}");
                }
                else
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":false,\"err\":\"slot not found\"}");
                }
            }
            else if (cmd == "sub")
            {
                // Just record peer for stream; no-op response.
                SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"sub\",\"ok\":true}");
            }
            else if (cmd == "spawnPlayer")
            {
                int slot = ExtractIntField(body, "slot", 0);
                // Optional x/y/z to spawn directly at — useful when spawning
                // into a Landfall scene where the default (0,8,0) is below the
                // killbox and the rig dies before any teleport can save it.
                // Must be all-or-nothing so a partial payload doesn't park the
                // rig at origin.
                bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                bool hasPos = hasX || hasY || hasZ;
                if (hasPos && !(hasX && hasY && hasZ))
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":false,\"err\":\"partial spawn coords — need x,y,z together\"}");
                    return;
                }
                Vector3 pos = new Vector3(0f, 8f, 0f);
                if (hasPos) pos = new Vector3(ExtractFloatField(body, "x"), ExtractFloatField(body, "y"), ExtractFloatField(body, "z"));
                bool ok = TrySpawnPlayer(slot, pos, out string err);
                if (ok)
                {
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":true,\"slot\":{slot}}}");
                }
                else
                {
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":false,\"err\":\"{err}\"}}");
                }
            }
            else if (cmd == "addForce")
            {
                // Most direct possible test: pick the first BodyPart child and
                // AddForce on its Rigidbody manually. If the rig moves, we
                // know physics is healthy and the issue is upstream.
                int slot = ExtractIntField(body, "slot", 0);
                float fz = ExtractFloatField(body, "fz");
                string err;
                bool ok = TryAddForce(slot, fz, out err);
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"addForce\",\"ok\":{(ok?"true":"false")},\"err\":\"{err}\"}}");
            }
            else if (cmd == "forceMove")
            {
                // Diagnostic: directly call Movement.MoveRight() for one tick.
                // If position changes, Controller.Update isn't routing our
                // inputs to MoveRight. If it doesn't, MoveRight itself is broken.
                int slot = ExtractIntField(body, "slot", 0);
                string dir = ExtractStringField(body, "dir") ?? "right";
                bool ok = TryForceMove(slot, dir, out string err);
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"forceMove\",\"ok\":{(ok?"true":"false")},\"err\":\"{err}\"}}");
            }
            else if (cmd == "inspect")
            {
                int slot = ExtractIntField(body, "slot", 0);
                string info = InspectRig(slot);
                SendBridgeJson(from, $"{{\"reply\":\"inspect\",\"slot\":{slot},\"info\":\"{info.Replace("\\","\\\\").Replace("\"","\\\"")}\"}}");
            }
            else if (cmd == "applyInput")
            {
                int slot = ExtractIntField(body, "slot", -1);
                if (slot < 0)
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"applyInput\",\"ok\":false,\"err\":\"bad slot\"}");
                }
                else
                {
                    var frame = new InputFrame
                    {
                        StickX  = ExtractFloatField(body, "stickX"),
                        StickY  = ExtractFloatField(body, "stickY"),
                        AimX    = ExtractFloatField(body, "aimX"),
                        AimY    = ExtractFloatField(body, "aimY"),
                        Buttons = ExtractIntField(body, "buttons", 0),
                    };
                    SlotInputs[slot] = frame;
                    _applyInputCount++;
                    if (_applyInputCount == 1 || _applyInputCount % 60 == 0)
                        Log.LogInfo($"[INSTR1] applyInput#{_applyInputCount}: slot={slot} stick=({frame.StickX:0.00},{frame.StickY:0.00}) buttons={frame.Buttons} SlotInputs.Count={SlotInputs.Count}");
                    // No reply for applyInput — comes 60 times/sec from Go,
                    // we don't want to flood the network with acks.
                }
            }
            else
            {
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"{cmd}\",\"ok\":false,\"err\":\"unknown cmd\"}}");
            }
        }

        private void SendBridgeJson(IPEndPoint to, string json)
        {
            if ((object)_bridge == null) return;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                _bridge.Send(data, data.Length, to);
            }
            catch (Exception e)
            {
                if (Verbose) Log.LogDebug($"Bridge send: {e.Message}");
            }
        }

        // EmitStateSnapshot pushes the current world entity state to the most
        // recently active peer (for the 30Hz stream). When the bridge has never
        // been pinged we don't emit, avoiding wasted work.
        private void EmitStateSnapshot()
        {
            if ((object)_bridgePeer == null) return;
            EmitStateSnapshotTo(_bridgePeer);
        }

        private static readonly StringBuilder _sb = new StringBuilder(2048);

        private void EmitStateSnapshotTo(IPEndPoint to)
        {
            _bridgeTick++;
            try
            {
                _sb.Length = 0;
                _sb.Append("{\"reply\":\"snapshot\",\"tick\":").Append(_bridgeTick);
                _sb.Append(",\"scene\":\"").Append(SceneManager.GetActiveScene().name).Append("\"");
                _sb.Append(",\"ents\":[");

                // Report only the rigs we spawned — slot-keyed via SlotToRig.
                // The root transform doesn't move under SF's physics model;
                // the actual position is determined by the ragdoll skeleton's
                // BodyPart Rigidbodies. Use the first BodyPart's position
                // (typically the hip/pelvis) as the canonical position.
                bool first = true;
                var bodyPartType = AccessTools.TypeByName("BodyPart");
                foreach (var kv in SlotToRig)
                {
                    var rig = kv.Value;
                    if ((object)rig == null) continue;
                    Vector3 p = rig.transform.position;
                    if ((object)bodyPartType != null)
                    {
                        var bp = rig.GetComponentInChildren(bodyPartType) as Component;
                        if ((object)bp != null) p = bp.transform.position;
                    }
                    if (!first) _sb.Append(",");
                    first = false;
                    _sb.Append("{\"slot\":").Append(kv.Key);
                    _sb.Append(",\"x\":").Append(p.x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    _sb.Append(",\"y\":").Append(p.y.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    _sb.Append(",\"z\":").Append(p.z.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    _sb.Append("}");
                }
                _sb.Append("]}");
                SendBridgeJson(to, _sb.ToString());
            }
            catch (Exception e)
            {
                if (Verbose) Log.LogDebug($"EmitStateSnapshot: {e.Message}");
            }
        }

        // TryAddForce directly AddForces on the rig's first BodyPart Rigidbody.
        // If THIS doesn't move the rig, the Rigidbody is constrained somehow
        // (joints, freezeAll, mass=infinity, etc.) — not a force-routing issue.
        private bool TryAddForce(int slot, float fz, out string err)
        {
            err = "";
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) { err = "no rig"; return false; }
                var bp = rig.GetComponentInChildren(AccessTools.TypeByName("BodyPart")) as Component;
                if ((object)bp == null) { err = "no BodyPart"; return false; }
                var rb = bp.GetComponent<Rigidbody>();
                if ((object)rb == null) { err = "no Rigidbody on BodyPart"; return false; }
                rb.AddForce(new Vector3(0f, 0f, fz), ForceMode.Impulse);
                err = $"applied F=(0,0,{fz}) Imp to {bp.gameObject.name}; rb.mass={rb.mass} kinematic={rb.isKinematic} constraints={rb.constraints}";
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // TryForceMove directly calls Movement.MoveRight/MoveLeft on the rig
        // for diagnostic purposes — bypassing Controller.Update's input read.
        private bool TryForceMove(int slot, string dir, out string err)
        {
            err = "";
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) { err = "no rig"; return false; }
                var mov = rig.GetComponent(AccessTools.TypeByName("Movement"));
                if ((object)mov == null) { err = "no Movement"; return false; }
                string methodName = dir == "left" ? "MoveLeft" : "MoveRight";
                var m = AccessTools.Method(mov.GetType(), methodName);
                if ((object)m == null) { err = "no " + methodName; return false; }
                m.Invoke(mov, null);
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // InspectRig dumps the slot's rig state — useful to diagnose why a
        // freshly-spawned player isn't moving / falling / responding to input.
        private string InspectRig(int slot)
        {
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null)
                {
                    return "no rig";
                }
                var sb = new StringBuilder(512);
                sb.Append("active=").Append(rig.activeSelf).Append("/").Append(rig.activeInHierarchy);
                sb.Append("; pos=").Append(rig.transform.position.ToString("0.00"));

                var rb = rig.GetComponent<Rigidbody>();
                if ((object)rb != null)
                {
                    sb.Append("; rb.kinematic=").Append(rb.isKinematic);
                    sb.Append(" useGravity=").Append(rb.useGravity);
                    sb.Append(" vel=").Append(rb.velocity.ToString("0.00"));
                }
                else sb.Append("; no Rigidbody");

                var ctrl = rig.GetComponent(AccessTools.TypeByName("Controller"));
                if ((object)ctrl != null)
                {
                    var hasControl = AccessTools.Field(ctrl.GetType(), "mHasControl");
                    if ((object)hasControl != null) sb.Append("; hasControl=").Append(hasControl.GetValue(ctrl));
                    var inactive = AccessTools.Field(ctrl.GetType(), "inactive");
                    if ((object)inactive != null) sb.Append(" inactive=").Append(inactive.GetValue(ctrl));
                }
                else sb.Append("; no Controller");

                var mov = rig.GetComponent(AccessTools.TypeByName("Movement"));
                if ((object)mov != null)
                {
                    sb.Append("; Movement=").Append(((Behaviour)mov).enabled);
                    var fm = AccessTools.Field(mov.GetType(), "forceMultiplier");
                    if ((object)fm != null) sb.Append(" forceMultiplier=").Append(fm.GetValue(mov));
                }
                else sb.Append("; no Movement");

                var fighting = rig.GetComponent(AccessTools.TypeByName("Fighting"));
                if ((object)fighting != null)
                {
                    var mm = AccessTools.Field(fighting.GetType(), "movementMultiplier");
                    if ((object)mm != null) sb.Append("; movementMultiplier=").Append(mm.GetValue(fighting));
                }

                var info = rig.GetComponent(AccessTools.TypeByName("CharacterInformation"));
                if ((object)info != null)
                {
                    var sf = AccessTools.Field(info.GetType(), "sinceFallen");
                    if ((object)sf != null) sb.Append("; sinceFallen=").Append(sf.GetValue(info));
                    var dead = AccessTools.Field(info.GetType(), "isDead");
                    if ((object)dead != null) sb.Append(" isDead=").Append(dead.GetValue(info));
                }

                // Dump CharacterActions Movement.X / Y / Left / Right values
                // so we can see whether our injection is taking effect.
                var ctrl2 = rig.GetComponent(AccessTools.TypeByName("Controller"));
                if ((object)ctrl2 != null)
                {
                    var pa = AccessTools.Field(ctrl2.GetType(), "mPlayerActions")?.GetValue(ctrl2);
                    if ((object)pa != null)
                    {
                        var movement = AccessTools.Field(pa.GetType(), "Movement")?.GetValue(pa);
                        if ((object)movement != null)
                        {
                            float mx = (float)AccessTools.Property(movement.GetType(), "X").GetValue(movement, null);
                            float my = (float)AccessTools.Property(movement.GetType(), "Y").GetValue(movement, null);
                            sb.Append("; Movement.X=").Append(mx.ToString("0.00")).Append(" .Y=").Append(my.ToString("0.00"));
                        }
                        var leftPa = AccessTools.Field(pa.GetType(), "Left")?.GetValue(pa);
                        var rightPa = AccessTools.Field(pa.GetType(), "Right")?.GetValue(pa);
                        if ((object)leftPa != null && (object)rightPa != null)
                        {
                            var leftVal = AccessTools.Property(leftPa.GetType(), "Value")?.GetValue(leftPa, null);
                            var rightVal = AccessTools.Property(rightPa.GetType(), "Value")?.GetValue(rightPa, null);
                            sb.Append("; Left.Value=").Append(leftVal).Append(" Right.Value=").Append(rightVal);
                        }
                    }
                }

                sb.Append("; Time.timeScale=").Append(Time.timeScale.ToString("0.000"));
                sb.Append("; Time.deltaTime=").Append(Time.deltaTime.ToString("0.000"));
                sb.Append("; fixedDelta=").Append(Time.fixedDeltaTime.ToString("0.000"));

                var standing = rig.GetComponent(AccessTools.TypeByName("Standing"));
                if ((object)standing != null) sb.Append("; Standing=").Append(((Behaviour)standing).enabled);
                else sb.Append("; no Standing");

                return sb.ToString();
            }
            catch (Exception e) { return "exc: " + e.Message; }
        }

        // TrySpawnPlayer instantiates a Player rig in the active scene at the
        // slot's spawn point, by grabbing ControllerHandler.playerPrefab and
        // calling Object.Instantiate directly. This sidesteps the InputDevice
        // pairing path (which requires real input hardware) — the rig will
        // exist but won't move until we inject inputs.
        //
        // Returns (true, "") on success or (false, "reason") on failure.
        private void TryCachePlayerPrefab()
        {
            if ((object)_cachedPlayerPrefab != null) return;
            try
            {
                var chType = AccessTools.TypeByName("ControllerHandler");
                if ((object)chType == null) { Log.LogWarning("CachePrefab: ControllerHandler type missing"); return; }
                var chInst = UnityEngine.Object.FindObjectOfType(chType);
                if ((object)chInst == null) { Log.LogWarning("CachePrefab: no ControllerHandler instance in active scene"); return; }
                var pf = AccessTools.Field(chType, "playerPrefab");
                if ((object)pf == null) { Log.LogWarning("CachePrefab: playerPrefab field missing"); return; }
                var go = pf.GetValue(chInst) as GameObject;
                if ((object)go == null) { Log.LogWarning("CachePrefab: playerPrefab value is null"); return; }
                _cachedPlayerPrefab = go;
                Log.LogInfo($"CachePrefab: cached playerPrefab '{go.name}' for cross-scene spawns.");
            }
            catch (Exception e) { Log.LogError($"TryCachePlayerPrefab threw: {e.Message}"); }
        }

        private bool TrySpawnPlayer(int slot, Vector3 spawnPosOverride, out string err)
        {
            err = "";
            try
            {
                GameObject prefab = _cachedPlayerPrefab;
                if ((object)prefab == null)
                {
                    var chType = AccessTools.TypeByName("ControllerHandler");
                    if ((object)chType == null) { err = "ControllerHandler type not found"; return false; }
                    var chInst = UnityEngine.Object.FindObjectOfType(chType);
                    if ((object)chInst == null) { err = "ControllerHandler instance not in scene (and no cached prefab)"; return false; }
                    var prefabField = AccessTools.Field(chType, "playerPrefab");
                    if ((object)prefabField == null) { err = "playerPrefab field not found"; return false; }
                    prefab = prefabField.GetValue(chInst) as GameObject;
                    if ((object)prefab == null) { err = "playerPrefab is null"; return false; }
                    _cachedPlayerPrefab = prefab;
                    Log.LogInfo("Cached playerPrefab for cross-scene spawns.");
                }
                var spawnPos = spawnPosOverride; // caller-supplied; defaults to (0,8,0) in bridge handler
                var go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity) as GameObject;
                if ((object)go == null) { err = "Instantiate returned null"; return false; }
                go.name = $"OracleSpawn_Slot{slot}";
                // Survive SceneManager.LoadScene switches. Without this, the
                // rig is destroyed when we transition from MainScene (where
                // ControllerHandler lives, needed to spawn the rig) to a
                // Landfall scene (which has real platforms but no spawn
                // infrastructure).
                UnityEngine.Object.DontDestroyOnLoad(go);

                // Bind a fresh CharacterActions so the Controller has somewhere
                // to read input from. Without this, mPlayerActions is null and
                // the Controller.Update path early-returns / no movement.
                //
                // Stock ControllerHandler.CreatePlayer calls AssignNewDevice
                // (which requires a real InputDevice we can't synthesize),
                // but Controller also exposes TakeLocalControl(CharacterActions)
                // which doesn't need a device — perfect for our bridge-driven
                // input flow.
                var ctrlType = AccessTools.TypeByName("Controller");
                var caType = AccessTools.TypeByName("CharacterActions");
                if ((object)ctrlType != null && (object)caType != null)
                {
                    var ctrl = go.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var createMethod = AccessTools.Method(caType, "CreateWithControllerBindings");
                        if ((object)createMethod != null)
                        {
                            var actions = createMethod.Invoke(null, null);
                            var takeMethod = AccessTools.Method(ctrlType, "TakeLocalControl");
                            if ((object)actions != null && (object)takeMethod != null)
                            {
                                takeMethod.Invoke(ctrl, new object[] { actions });
                                // Also assign a playerID so any code reading
                                // controller.playerID gets a sensible slot.
                                var pidField = AccessTools.Field(ctrlType, "playerID");
                                if ((object)pidField != null) pidField.SetValue(ctrl, slot);
                                Log.LogInfo($"Bound CharacterActions to slot {slot} via TakeLocalControl.");
                            }
                            else
                            {
                                Log.LogWarning("Could not bind CharacterActions: CreateWith* returned null or TakeLocalControl missing.");
                            }
                        }
                    }
                }

                SlotToRig[slot] = go;
                if (!SlotInputs.ContainsKey(slot))
                {
                    SlotInputs[slot] = new InputFrame();
                }

                // Clear regularBindings on every underlying PlayerAction in
                // this CharacterActions instance. InControl's PlayerAction.
                // UpdateBindings loops over regularBindings each frame and
                // calls UpdateWithValue(bindingSource.GetValue(Device), ...),
                // which writes 0 because we have no real device — that's what
                // clobbers our manually-injected values. With no bindings,
                // the loop is a no-op and our UpdateWithValue calls survive.
                ClearAllPlayerActionBindings(go);

                Log.LogInfo($"Spawned oracle player rig for slot {slot} at {spawnPos} (GO: {go.name})");
                return true;
            }
            catch (Exception e)
            {
                err = e.Message;
                return false;
            }
        }

        // ClearAllPlayerActionBindings walks the rig's CharacterActions and
        // clears each PlayerAction's regularBindings list. Required so our
        // per-frame UpdateWithValue calls aren't immediately overwritten by
        // InControl's UpdateBindings loop reading from null devices.
        private static void ClearAllPlayerActionBindings(GameObject rig)
        {
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType == null) return;
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) return;
                var actionsField = AccessTools.Field(ctrlType, "mPlayerActions");
                if ((object)actionsField == null) return;
                var actions = actionsField.GetValue(ctrl);
                if ((object)actions == null) return;

                var paType = AccessTools.TypeByName("InControl.PlayerAction");
                if ((object)paType == null) return;
                var bindingsField = AccessTools.Field(paType, "regularBindings");
                var visibleField  = AccessTools.Field(paType, "visibleBindings");
                if ((object)bindingsField == null) return;

                // Walk every field on the CharacterActions instance; any
                // PlayerAction we find, clear its bindings.
                int cleared = 0;
                foreach (var f in actions.GetType().GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var v = f.GetValue(actions);
                    if ((object)v == null) continue;
                    if (!paType.IsInstanceOfType(v)) continue;
                    var listObj = bindingsField.GetValue(v);
                    var clearMethod = listObj?.GetType().GetMethod("Clear");
                    clearMethod?.Invoke(listObj, null);
                    if ((object)visibleField != null)
                    {
                        var visObj = visibleField.GetValue(v);
                        visObj?.GetType().GetMethod("Clear")?.Invoke(visObj, null);
                    }
                    cleared++;
                }
                Log.LogInfo($"Cleared regularBindings on {cleared} PlayerActions.");
            }
            catch (Exception e)
            {
                Log.LogError($"ClearAllPlayerActionBindings: {e.Message}");
            }
        }

        // Lookup cache for InControl.PlayerAction.UpdateWithValue MethodInfo.
        private static MethodInfo _cachedUpdateWithValue;
        private static bool _loggedUpdateWithValue;
        private static MethodInfo GetUpdateWithValueMethod()
        {
            if (_cachedUpdateWithValue != null) return _cachedUpdateWithValue;
            var paType = AccessTools.TypeByName("InControl.PlayerAction");
            if ((object)paType == null)
            {
                if (!_loggedUpdateWithValue) { Log.LogWarning("UpdateWithValue: no InControl.PlayerAction type"); _loggedUpdateWithValue = true; }
                return null;
            }
            _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue",
                new Type[] { typeof(float), typeof(ulong), typeof(float) });
            if (_cachedUpdateWithValue == null && !_loggedUpdateWithValue)
            {
                Log.LogWarning("UpdateWithValue: method not found on PlayerAction. Trying without param-type filter…");
                _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue");
                if (_cachedUpdateWithValue == null) Log.LogWarning("UpdateWithValue: not found even without filter");
                else Log.LogInfo($"UpdateWithValue: found via fallback, signature: {_cachedUpdateWithValue}");
                _loggedUpdateWithValue = true;
            }
            else if (!_loggedUpdateWithValue)
            {
                Log.LogInfo($"UpdateWithValue: found, signature: {_cachedUpdateWithValue}");
                _loggedUpdateWithValue = true;
            }
            return _cachedUpdateWithValue;
        }

        private static bool _loggedPushPath;
        // PushPlayerAction calls PlayerAction.UpdateWithValue(value, tick, dt)
        // on the named PlayerAction field of the given CharacterActions.
        private static void PushPlayerAction(object actions, string fieldName, float value)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field not found on type {actions.GetType()}"); _loggedPushPath = true; }
                return;
            }
            var action = f.GetValue(actions);
            if ((object)action == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field value is null"); _loggedPushPath = true; }
                return;
            }
            var m = GetUpdateWithValueMethod();
            if ((object)m == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: UpdateWithValue method lookup failed; action type={action.GetType()}"); _loggedPushPath = true; }
                return;
            }
            try
            {
                m.Invoke(action, new object[] { value, (ulong)0, Time.deltaTime });
                if (!_loggedPushPath) { Log.LogInfo($"PushPlayerAction[{fieldName}]: invoke ok, value={value}"); _loggedPushPath = true; }
            }
            catch (Exception e)
            {
                if (!_loggedPushPath) { Log.LogError($"PushPlayerAction[{fieldName}] invoke threw: {e}"); _loggedPushPath = true; }
            }
        }

        // WriteInputsToRigs pushes the most recent per-slot input frame into
        // each spawned rig's CharacterActions via reflection. The Controller
        // reads these every frame in Update — so by writing them right before
        // Controller.Update runs (we're called from Plugin.Update which Unity
        // schedules before MonoBehaviours by default), our values become the
        // effective input for that frame.
        //
        // CharacterActions is an InControl PlayerActionSet. Its Movement /
        // Aiming fields are TwoAxisInputControl with a settable RawValue.
        // Buttons are PlayerAction with a settable RawValue / IsPressed.
        private static bool _loggedFirstWrite;
        private static bool _loggedFirstWriteIter;
        private void WriteInputsToRigs()
        {
            if (SlotToRig.Count == 0) return;
            if (!_loggedFirstWrite) { Log.LogInfo($"WriteInputsToRigs called for first time. SlotToRig.Count={SlotToRig.Count} SlotInputs.Count={SlotInputs.Count}"); _loggedFirstWrite = true; }
            try
            {
                foreach (var kv in SlotToRig)
                {
                    int slot = kv.Key;
                    GameObject rig = kv.Value;
                    if ((object)rig == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: rig null"); _loggedFirstWriteIter = true; } continue; }
                    if (!SlotInputs.TryGetValue(slot, out var input)) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: SlotInputs miss"); _loggedFirstWriteIter = true; } continue; }

                    var ctrlType = AccessTools.TypeByName("Controller");
                    if ((object)ctrlType == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no Controller type"); _loggedFirstWriteIter = true; } continue; }
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no Controller on rig"); _loggedFirstWriteIter = true; } continue; }
                    var actionsField = AccessTools.Field(ctrlType, "mPlayerActions");
                    if ((object)actionsField == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no mPlayerActions field"); _loggedFirstWriteIter = true; } continue; }
                    var actions = actionsField.GetValue(ctrl);
                    if ((object)actions == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: mPlayerActions is null"); _loggedFirstWriteIter = true; } continue; }

                    if (!_loggedFirstWriteIter) { Log.LogInfo($"WriteInputs iter: REACHED PushPlayerAction, actions type={actions.GetType().FullName}, stick=({input.StickX},{input.StickY})"); _loggedFirstWriteIter = true; }

                    // Feed the underlying L/R/U/D PlayerActions — that's
                    // what CharacterActions.Movement (a PlayerTwoAxisAction)
                    // computes its X/Y from. Setting Movement.thisValue
                    // directly gets overwritten next frame by
                    // PlayerTwoAxisAction.Update reading L/R/U/D.
                    PushPlayerAction(actions, "Left",  Mathf.Max(0f, -input.StickX));
                    PushPlayerAction(actions, "Right", Mathf.Max(0f,  input.StickX));
                    PushPlayerAction(actions, "Up",    Mathf.Max(0f,  input.StickY));
                    PushPlayerAction(actions, "Down",  Mathf.Max(0f, -input.StickY));

                    PushPlayerAction(actions, "AimLeft",  Mathf.Max(0f, -input.AimX));
                    PushPlayerAction(actions, "AimRight", Mathf.Max(0f,  input.AimX));
                    PushPlayerAction(actions, "AimUp",    Mathf.Max(0f,  input.AimY));
                    PushPlayerAction(actions, "AimDown",  Mathf.Max(0f, -input.AimY));

                    PushPlayerAction(actions, "Jump",         (input.Buttons & 0x01) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Jump2",        (input.Buttons & 0x01) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "PunchOrFire",  (input.Buttons & 0x02) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Block",        (input.Buttons & 0x04) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Throw",        (input.Buttons & 0x08) != 0 ? 1f : 0f);
                }
            }
            catch (Exception e)
            {
                if (Verbose) Log.LogDebug($"WriteInputsToRigs: {e.Message}");
            }
        }

        // SetTwoAxis writes (x, y) to the named TwoAxisInputControl on the
        // CharacterActions instance by poking its private `thisValue` Vector2
        // field directly. Stock InControl exposes Value as a getter only
        // and no setter API for "fake" input — we have to bypass.
        private static void SetTwoAxis(object actions, string fieldName, Vector2 v)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, v);
            // X / Y are protected properties; their backing fields are auto-
            // generated (<X>k__BackingField). Update them too so anything that
            // reads .X / .Y sees the new value.
            var xBacking = AccessTools.Field(t, "<X>k__BackingField");
            var yBacking = AccessTools.Field(t, "<Y>k__BackingField");
            if ((object)xBacking != null) xBacking.SetValue(ctrl, v.x);
            if ((object)yBacking != null) yBacking.SetValue(ctrl, v.y);
        }

        // SetOneAxisOrButton writes a button-press state by setting the
        // PlayerAction's private thisValue (float, 0.0 / 1.0).
        private static void SetOneAxisOrButton(object actions, string fieldName, bool pressed)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, pressed ? 1.0f : 0.0f);
        }

        // === tiny JSON field extractors (avoid dragging in JSON.NET) ===
        //
        // FindField returns the index of a `"field"` token where the preceding
        // character is a key boundary ({, ,, or whitespace). Without this, a
        // search for "x" matches "tx", "exit", etc. and a search for "slot"
        // matches "slotName". Returns -1 if no boundary-respecting match.
        private static int FindField(string json, string field)
        {
            string token = "\"" + field + "\"";
            int from = 0;
            while (from < json.Length)
            {
                int i = json.IndexOf(token, from);
                if (i < 0) return -1;
                bool boundaryOk = (i == 0);
                if (!boundaryOk)
                {
                    char prev = json[i - 1];
                    boundaryOk = prev == '{' || prev == ',' || prev == ' ' || prev == '\t' || prev == '\n' || prev == '\r';
                }
                if (boundaryOk) return i;
                from = i + 1;
            }
            return -1;
        }

        // HasField — quick presence check used by callers that need to
        // distinguish "field absent" from "field present with default value".
        private static bool HasField(string json, string field) => FindField(json, field) >= 0;

        private static string ExtractStringField(string json, string field)
        {
            int i = FindField(json, field);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static float ExtractFloatField(string json, string field)
        {
            int i = FindField(json, field);
            if (i < 0) return 0f;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return 0f;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.' || json[end] == 'e' || json[end] == 'E' || json[end] == '+')) end++;
            if (end == start) return 0f;
            float f;
            if (float.TryParse(json.Substring(start, end - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) return f;
            return 0f;
        }

        private static int ExtractIntField(string json, string field, int fallback)
        {
            int i = FindField(json, field);
            if (i < 0) return fallback;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return fallback;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == start) return fallback;
            int n;
            if (int.TryParse(json.Substring(start, end - start), out n)) return n;
            return fallback;
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
            Log.LogInfo($"=== HEADLESS HOST READY on port {BindPort} ===");
        }

        private static void ReadEnv()
        {
            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_PORT"), out var p);
            if (p > 0 && p < 65536) BindPort = p;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_BRIDGEPORT"), out var bp);
            if (bp > 0 && bp < 65536) BridgePort = bp;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_SCENE"), out var s);
            if (s >= 0) InitialScene = s;

            Verbose = Environment.GetEnvironmentVariable("SFHEADLESS_DEBUG") == "1";
            Log.LogInfo($"Config: BindPort={BindPort} BridgePort={BridgePort} InitialScene={InitialScene} Verbose={Verbose}");
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
                if ((object)serverProp == null) return;
                var netServer = serverProp.GetValue(__instance, null);
                if ((object)netServer == null) return;
                var configProp = AccessTools.Property(netServer.GetType(), "Configuration");
                if ((object)configProp == null) return;
                var config = configProp.GetValue(netServer, null);
                if ((object)config == null) return;
                var portProp = AccessTools.Property(config.GetType(), "Port");
                if ((object)portProp == null) return;
                portProp.SetValue(config, BindPort, null);
                Log.LogInfo($"NetworkSocketServer ctor postfix: rewrote Port → {BindPort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"PatchServerPort threw: {e}");
            }
        }

    }
}
