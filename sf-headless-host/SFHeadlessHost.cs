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
    // SFHeadlessHost — turns a headless Stick Fight instance into a UDP
    // dedicated server. Loaded by BepInEx on the oracle (`-batchmode`) and
    // also on each player's SF install (interactive mode, where only the
    // CLIENT-MODE SHIM runs).
    //
    // ============================================================
    //                    TABLE OF CONTENTS
    // ============================================================
    //  Anchor (search this line)                          ~Line
    //  ─────────────────────────────────────────────────  ─────
    //  private void Awake()                               ~  56   bootstrap, mode detect
    //  Phase 6.5 Step 1 — IsServer=true                   ~ 397   Harmony postfixes to fake host mode
    //  SendBroadcastPrefix                                ~ 524   intercepts host-side broadcasts → forwards
    //                                                              over our v25 UDP socket (P0-11 lives here)
    //  Phase 6.5 Step 2 — invoke GameManager.StartMatch   ~ 724
    //  InvokeMultiplayerManagerInitChain                  ~ 776   manual init chain after scene load
    //  NSO inventory + diagnostics                        ~ 815
    //  === CLIENT-MODE SHIM ===                           ~1137   patches applied on player-side SF
    //  P0-12 — MapInfoSync Vector2 quantize               ~1149   prefix patches (server + client)
    //  InstallClientModePatches                           ~1247   dynamic NSO patch (skip DisableAllRigidBodies)
    //  Bridge: UDP socket                                 ~1674   the v25 raw socket
    //  v26 extension constants (msgTypes 39/40/41)        ~1735
    //  Patched-DLL extensions (msgTypes 56/57)            ~1743
    //  SfDispatch                                         ~1917   inbound packet router
    //  ValidateDamagePacket (P1-8 lives here)             ~2140   anticheat damage validation
    //  Chat / admin                                       ~2224   /code /ping /start /tickrate /help…
    //  RateGuard / AnticheatObserve                       ~2356
    //  Pickup / Drop / Throw handlers                     ~2558
    //  Handshake handlers                                 ~2626   ClientRequestingAccepting → Spawned
    //  HandlePlayerInput (v26)                            ~2933   inbound PktPlayerInput; P0-13 keyframe send here
    //  HandlePlayerUpdate                                 ~3022   v25 client position relay
    //  Phase 6.9 — auth rig spawn + ghost rig update      ~3046
    //  Phase 6.10 — server-authoritative snapshots        ~3214   v26 WorldStateSnapshot broadcast
    //  Phase 6.17 v0.1+v0.2 — projectile sim + hit reg    ~3240
    //  EmitServerDamage                                   ~3369
    //  Tick-history ring buffer (lag-comp)                ~3388
    //  BroadcastWorldStateSnapshot                        ~3443   v26.5 (players + NSOs + projs + mapSync)
    //  CollectAllNsoSnapshot + SendKeyframeSnapshot       ~3556   P0-13 first-snap-on-late-join
    //  P0-14 MapInfoSyncableBase position broadcast       ~3553+  CollectMapSyncSnapshot, etc.
    //  CollectActiveNsoSnapshot                           ~3690+
    //  ApplyClientObjectUpdate (legacy)                   ~3850
    //  ReadEnv                                            ~4530
    // ============================================================
    //
    // Architecture: see ../notes/ARCHITECTURE.md
    // Wire protocol: see ../notes/PROTOCOL.md
    // Object sync model: see ../notes/OBJECT_SYNC.md
    // Open bugs: see ../notes/BUGS_BACKLOG.md (P0-11..P0-15, P1-8)
    //
    // Configuration via env vars (read once at Awake):
    //   SFHEADLESS_PORT       — v25 UDP bind port (default 1337).
    //   SFHEADLESS_BRIDGEPORT — internal bridge port (default 1341).
    //   SFHEADLESS_SCENE      — initial scene index (default 0 = lobby).
    //   SFHEADLESS_DEBUG      — "1" enables verbose tick logging.
    //   SF_ROUND_END_DELAY    — seconds before MapChange after a kill (default 0.5).
    //   SF_NEXT_MATCH_DELAY   — seconds before StartMatch after MapChange (default 2.0).
    //   SF_PRE_COMBAT_DELAY   — seconds after map load before weapons/countdown/MapInfo (default 3.0).
    //   SF_ANTICHEAT_ENFORCE  — "1" turns anticheat into drop-mode (default observe-only).
    //   SF_LOBBY_CODE         — 4-char lobby code returned by /code chat command.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.headless-host";
        public const string PluginName = "SFHeadlessHost";
        public const string PluginVersion = "0.4.4";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        internal static int BindPort = 1340;     // Game-traffic port (Lidgren)
        internal static int BridgePort = 1341;   // State-bridge port (this plugin)
        internal static int InitialScene = 0; // 0 = lobby (boots ControllerHandler + GameManager DontDestroyOnLoad infrastructure)
        internal static bool Verbose;
        // Round pacing. Stock SF fires ChangeMap instantly when last player dies
        // (KillPlayer in GameManager.cs). The 0.5s default here gives clients a
        // beat to render the death animation before the map-swoosh starts.
        // SF_ROUND_END_DELAY env var override.
        internal static float RoundEndDelaySec = 0.5f;
        // Time between MapChange broadcast and StartMatch broadcast — must be
        // long enough for clients to load the scene and respawn. Stock SF's
        // k_MAX_SECONDS_UNTIL_AUTO_START is 3s. SF_NEXT_MATCH_DELAY env override.
        internal static float NextMatchDelaySec = 2.0f;
        // Minimum seconds on a map before another kill can advance the round (stops double MapChange / skip).
        internal static float RoundMinPlaySec = 0f;
        private float _roundAdvanceBlockedUntil = -1f;
        private bool _roundAdvanceQueuedAfterMapLoad;
        private readonly HashSet<int> _deathSlotsHandled = new HashSet<int>();
        private float _authDeathCheckAt = -1f;
        private float _pendingClientStartMatchAt = -1f;
        private bool _pendingClientStartMatchFired;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Phase 6.22 — per-lobby plugin log file. The shared BepInEx
            // LogOutput.log gets trampled when multiple oracles run from the
            // same install (last-writer-wins). Set SFHEADLESS_LOGFILE to a
            // unique path per oracle so each gets its own tee'd log.
            // launch-lobby.sh sets this automatically to
            // /tmp/sf-oracle-plugin-<BRIDGE>.log.
            try
            {
                var perLobbyPath = Environment.GetEnvironmentVariable("SFHEADLESS_LOGFILE");
                if (!string.IsNullOrEmpty(perLobbyPath))
                {
                    BepInEx.Logging.Logger.Listeners.Add(new PerLobbyLogListener(perLobbyPath));
                    Log.LogInfo($"Per-lobby log tee → {perLobbyPath}");
                }
            }
            catch (Exception e) { Log.LogWarning($"per-lobby log init failed: {e.Message}"); }

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
                InstallMapInfoSyncQuantize();  // P0-12 — also on the client side
                return;
            }
            Log.LogInfo($"{PluginName} {PluginVersion}: batchmode detected, bootstrapping headless host.");
            _batchModeHost = true;
            InstallMapTerrainAuthorityPatches();
            EnsureOracleP2PNetworkReady("batchmode-boot");

            // Phase 6.9 — settle on scene load (ported from CustomServers).
            // Stock PrepareMapForTravel never runs its kinematic-settle branch
            // on the oracle; without this, chains stress-break and crates fall.
            SceneManager.sceneLoaded -= OnAnySceneLoadedRunSettle;
            SceneManager.sceneLoaded += OnAnySceneLoadedRunSettle;

            // P0-12 — install on the server side too. AddMapDataObject runs
            // in MapInfoSyncableBase.Awake on the oracle's scene; without
            // matching quantization the server's dict key would diverge from
            // the client's even though both call the same function (different
            // process, different float arithmetic).
            InstallMapInfoSyncQuantize();

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

                // v0.4.0 — installed on BOTH oracle and clients (was batchmode-
                // only; clients hit the identical null-channels NRE storm).
                var ppTypeHeadless = AccessTools.TypeByName("P2PPackageHandler");
                if ((object)ppTypeHeadless != null)
                {
                    var isPkt = AccessTools.Method(ppTypeHeadless, "IsPacketAvailable");
                    if ((object)isPkt != null)
                        TryPatch(harmony, "P2PPackageHandler.IsPacketAvailable (null-channels guard)",
                            isPkt, prefix: nameof(IsPacketAvailableHeadlessPrefix));
                }

                if (_batchModeHost)
                    TryPatchHealthHandlerDieForRoundAdvance(harmony);

                // v0.4.0 — stock IgnorePlayerWhenOffScreen.Update moves any
                // object below y=-11 to layer 24 (no collision). It's a RENDER
                // cull, hardcoded for small maps — on big maps (e.g. Desert9,
                // crates at y=-14) the oracle's crates silently lost collision
                // and fell through the world. Clients patch this with a
                // map-size-aware transpiler (SFClientRecon "crate-cull"); the
                // headless oracle renders nothing, so kill the cull outright.
                // Before server-auth this was masked: client relays kept
                // teleporting the fallen server crates back up.
                if (_batchModeHost)
                {
                    SceneManager.sceneLoaded += OnOracleSceneLoadedTrackMap;
                    Log.LogInfo("[v26.7] Oracle map-scene tracker registered (sceneLoaded hook).");
                }

                if (_batchModeHost)
                {
                    var cullType = AccessTools.TypeByName("IgnorePlayerWhenOffScreen");
                    var cullUpd = (object)cullType != null ? AccessTools.Method(cullType, "Update") : null;
                    if ((object)cullUpd != null)
                        TryPatch(harmony, "IgnorePlayerWhenOffScreen.Update (headless cull kill)",
                            cullUpd, prefix: nameof(SkipPrefix));
                    else
                        Log.LogWarning("[P6.5] IgnorePlayerWhenOffScreen.Update not found — offscreen cull NOT disabled.");
                }

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

        // Phase 6.9 diagnostics — track PrepareMapForTravel progress on oracle.
        private static int _initSyncedCallCount;
        private static int _initMapDataCallCount;
        private static int _readyUpCallCount;

        // Phase 6.5 Step 2d — force every SetNetworkMatch(v) call to use v=true.
        // Defeats the inlined-getter problem because the backing field stays true.
        private static int _setNetMatchInterceptCount;

        // Phase 6.5 Step 2e — replace GameManager.SpawnRandomWeapon. Computes a
        // spawn position matching the original method's logic, picks a weapon
        // via the (already-patched) GetRandomWeaponIndex, and calls
        // MultiplayerManager.SpawnWeapon directly. Returns false to skip
        // original.
        private static int _srwCallCount;

        // Phase 6.5 Step 2c — force GetRandomWeaponIndex to return a valid index.
        // Stock SF returns -1 if m_WeaponRaritiesArray is empty (UI never set up).
        // Network branch in SpawnRandomWeapon only uses the int index; weaponObject
        // is consumed only by the local-spawn path which we don't take.
        private static int _grwiCallCount;

        // Phase 6.8 — chat-driven weapon allow-list. When empty, picks
        // from a round-robin 0..7 (stock SF's first 8 weapons — pistol
        // through shotgun in stock id order). When set via /weapons
        // chat command, picks uniformly from the allow-list.
        // Static so /weapons handler (instance method) and the static
        // GRWI/SRW prefixes share state.
        internal static readonly System.Collections.Generic.HashSet<int> _allowedWeaponIds = new System.Collections.Generic.HashSet<int>();
        private static int _allowedWeaponCycleIdx;

        // Phase 6.5 Step 1 — log host broadcasts. Observe-only: return true so the
        // original method runs (it's a no-op on the oracle because mConnectedClients
        // is empty; we just want to see which msgTypes SF host code wants to send).
        // Use object[] __args to dodge needing typed refs to EP2PSend (Steamworks).
        private static int _p65BroadcastCount;
        private static readonly Dictionary<byte, int> _p65BroadcastByType = new Dictionary<byte, int>();
        private static int _p65ObjUpdateIdxLogCount;
        private static readonly HashSet<ushort> _p65ObjUpdateSeenIndices = new HashSet<ushort>();
        private static int _p65ObjUpdateFilterCount;
        private static int _p65DestructionFilterCount;

        // Phase 6.5 Step 1 — log direct user-targeted sends (CSteamID overload).
        private static int _p65DirectCount;
        private static readonly Dictionary<byte, int> _p65DirectByType = new Dictionary<byte, int>();

        // Phase 6.5 Step 2 — schedule + invoke GameManager.StartMatch on the oracle.
        private static float _oracleStartMatchAt = -1f;
        private static bool _oracleStartMatchFired;
        private static float _oracleCountDownAt = -1f;
        private static bool _oracleCountDownFired;

        // === File-driven live debug console (v0.4.0, batchmode only) ===
        // Write commands — one per line — into /tmp/sf-cmd-oracle.txt; replies
        // land in the plugin log tee (SFHEADLESS_LOGFILE) within ~0.5s. Gives
        // an outside operator on-demand live crate/rig state from the
        // AUTHORITY sim, diffable against the clients' own `boxes` dumps.
        // Commands: boxes | rigs | help
        private float _oracleDbgNextPollAt = -1f;
        private const string OracleDbgCmdPath = "/tmp/sf-cmd-oracle.txt";

        // Scene gate for id-keyed NSO state. Empty (lobby, pre-first-map) = no
        // filtering.
        private static string _currentMapSceneName;

        // One-shot NetworkSyncableObject inventory — fires once after match-start
        // settles. Tells us how many syncable objects are in the loaded scene,
        // their listening state, and whether mHasControl is true (which gates
        // ObjectUpdate broadcasting on the host side).
        private static bool _nsoInventoryDone;
        private static float _nsoInventoryAt = -1f;
        private static readonly List<ProbeNsoEntry> _probeNsos = new List<ProbeNsoEntry>();
        private static float _probeNextLogAt = -1f;

        // Periodic state probe — log GameManager.inFight + randomWeaponCounter
        // so we can see whether the host-side game loop is actually running.
        // NB: Mono 2.0.50727 lacks FieldInfo.op_Inequality — must cast to
        // object before any reflection-type null comparison.
        private static float _stateProbeLastAt;

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
        // P0-12 — quantize the Vector2 key used for MapInfoSyncableBase
        // dictionary lookup. Stock SF stores objects by world-space
        // (position.y, position.z) with bit-exact float comparison. Float32
        // can differ by a few ULPs between server and clients at Awake
        // time, causing silent lookup failures → MapInfoSync packets
        // arrive but client never applies SetData. Round to 0.01 (1 cm) —
        // well below the spacing between platforms, well above any
        // realistic precision drift.
        private const float MapSyncKeyQuantum = 0.01f;

        private static bool _mapSyncQuantizeInstalled;

        // Headless oracle ONLY: channels[channel] can be null → 40k+
        // NullRef/frame in ListenForPackages. BATCHMODE-GATED ON PURPOSE —
        // do NOT widen to clients: the patched DLL lazily creates channel
        // queues INSIDE IsPacketAvailable, and a prefix-skip there blocks
        // the v25 handshake entirely ("Connecting to the server..." hang,
        // live-debugged 2026-06-11). Clients get an NRE-suppressing
        // FINALIZER from SFClientRecon instead.
        private static FieldInfo _ppChannelsField;
        private static bool _ppChannelsLookupTried;

        // Per-patch install with status tracking. A single try/catch around
        // the whole block silently skipped patches if any one threw early.
        // Failures now accumulate in _p65MissingPatches for a post-install
        // summary line.
        private static int _p65PatchesAttempted;
        private static int _p65PatchesSucceeded;
        private static readonly List<string> _p65MissingPatches = new List<string>();

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
        private string _updateErrorFirstStackTrace;
        private void Update()
        {
            try { TickOracleDebugConsole(); } catch { }
            try
            {
                StepBoot();
            }
            catch (Exception e)
            {
                _updateErrorTicks++;
                // Don't kill the boot state; just log periodically so we can see what's wrong.
                // Print full stack trace separately — `{e}` formatting via Mono sometimes
                // elides frames. Capture first stack trace + log it on every rate-limited
                // print so it survives in long-running log truncation. Resolves diagnostic
                // half of Bug #45 in notes/bug-investigations/2026-05-24_v0.3.4-session-bugs.md.
                if (_updateErrorFirstStackTrace == null)
                    _updateErrorFirstStackTrace = e.StackTrace ?? "(no stack)";
                if (_updateErrorTicks <= 5 || _updateErrorTicks % 300 == 0)
                {
                    Log.LogError($"SFHeadlessHost.Update (count={_updateErrorTicks}) {e.GetType().Name}: {e.Message}");
                    Log.LogError($"  inner: {e.InnerException?.GetType().Name}: {e.InnerException?.Message}");
                    Log.LogError($"  stack[first]: {_updateErrorFirstStackTrace}");
                    Log.LogError($"  stack[current]: {e.StackTrace}");
                }
            }
        }

        private int _fixedUpdateErrors;
        // Phase 6.17/6.18 — advance virtual projectiles on the FIXED timestep
        // (Time.fixedDeltaTime, 60Hz here) so server-side swept-sphere hit
        // registration is deterministic + FPS-independent, and the
        // Physics.Linecast/OverlapSphere queries run in sync with Unity physics.
        private void FixedUpdate()
        {
            try { TickProjectiles(); }
            catch (Exception e)
            {
                _fixedUpdateErrors++;
                if (_fixedUpdateErrors <= 5 || _fixedUpdateErrors % 300 == 0)
                    Log.LogError($"SFHeadlessHost.FixedUpdate (count={_fixedUpdateErrors}) {e.GetType().Name}: {e.Message}");
            }
            try { TickGhostContactGovernor(); } catch { }
        }

        // v0.4.0 — bound the ghost rig's push on crates. The auth rigs are
        // KINEMATIC and swept to the client-asserted position, and a kinematic
        // body is infinite mass to PhysX: a crate in its path must move at
        // full rig speed (a fall/dash = 10-20 u/s shove). The client predicts
        // the same push with its real, light, dynamic player rig vs a 45-mass
        // crate — barely a nudge. That asymmetry made the authority's crates
        // run away from every client's prediction during contact (the
        // "rubber-band on push" of the first live test). While a crate is in
        // ghost-rig contact range, cap its horizontal speed to a believable
        // walk-push; vertical and blast motion (|v.y| > 2) stay untouched so
        // explosions and drops behave.
        private const float GhostPushCrateCap   = 2.6f;  // u/s horizontal while rig-adjacent
        private const float GhostPushContactSqr = 1.7f * 1.7f;
        private int _ghostGovernedCount;
        private void TickGhostContactGovernor()
        {
            if (SlotToRig.Count == 0) return;
            EnsureNsoSrvCache();
            if (_nsoSrvEntries.Count == 0) return;
            float capSqr = GhostPushCrateCap * GhostPushCrateCap;
            foreach (var ent in _nsoSrvEntries)
            {
                if (!ent.Pushable) continue;
                var rb = ent.Rb;
                if ((object)rb == null || rb.isKinematic) continue;
                Vector3 v;
                Vector3 p;
                try { v = rb.velocity; p = rb.position; } catch { continue; }
                if (Mathf.Abs(v.y) > 2f) continue;            // blast/fall — leave it
                float hSqr = v.x * v.x + v.z * v.z;
                if (hSqr <= capSqr) continue;
                bool rigNear = false;
                foreach (var kv in SlotToRig)
                {
                    var rig = kv.Value;
                    if ((object)rig == null) continue;
                    Vector3 d = rig.transform.position - p;
                    if (d.sqrMagnitude <= GhostPushContactSqr) { rigNear = true; break; }
                }
                if (!rigNear) continue;
                float s = GhostPushCrateCap / Mathf.Sqrt(hSqr);
                v.x *= s; v.z *= s;
                rb.velocity = v;
                _ghostGovernedCount++;
                if (_ghostGovernedCount == 1 || _ghostGovernedCount % 200 == 0)
                    Log.LogInfo($"[P6.9 ghost] Capped rig-push on crate #{_ghostGovernedCount} (→{GhostPushCrateCap:0.0} u/s)");
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
        private const byte PktClientFireWeapon              = 41;  // client → server, on Weapon.ActuallyShoot (Phase 6.17)
        private const byte PktV26Announce                   = 42;  // server → all clients, UTF-8 banner text (recon plugin draws it 3s)
        // === Patched-DLL extensions (kit's patched Assembly-CSharp.dll has
        // these beyond stock SF's 0-38 range). We don't synthesize them, but
        // we relay so peer clients see each other. From ALKA's
        // relay_handlers.go (his P1-4 fix).
        private const byte PktLerpPlayer                    = 56;  // empty body, triggers remote-lerp on NetworkPlayer
        private const byte PktColorChanged                  = 57;  // HTML color string body (4-64 bytes)
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
        private static readonly Dictionary<int, InputFrame> SlotInputs = new Dictionary<int, InputFrame>();

        // Pending teleport target for the next sceneLoaded callback (set by
        // the loadMap bridge command). Applied to every spawned rig once the
        // new scene's geometry is in place.
        private static Vector3 _pendingTeleport;
        private static bool _pendingTeleportArmed;

        // Periodic sweep — drop _sfClients entries whose last seen exceeds
        // ClientTimeoutSec. Without this, ungracefully disconnected clients
        // accumulate and keep receiving broadcasts forever.
        private const float ClientTimeoutSec = 30f;
        private float _lastClientSweepAt;

        // Phase 6.16 v0.1 — basic damage validation (no rewind yet).
        // Body shape: byte attackerIdx, f32 damage, bool playParticles, ...
        // The full rewind-based authority is designed in
        // notes/phase6/13-rewind-buffer.md. For now we just reject obvious
        // anomalies so a malicious client can't one-shot people with
        // arbitrary damage values.
        private uint _damagePacketsDropped;

        // === Behavioral anti-cheat — impossible-melee / instakill detection ===
        //
        // Goal (per design): stay PERMISSIVE. We only act on behavior that is
        // physically impossible in legit play and only after it repeats across
        // more than two distinct rounds — so a single fluke never kicks anyone.
        //
        // The signature we catch: a player dies (server sees the 666.666
        // killing-blow marker) having received almost no real accumulated
        // damage this round (<AcSuspectMaxAccum) from at most one hit, at melee
        // range. Legit kills always accumulate ~full-HP worth of real damage
        // before the killing blow, so this only trips on faked/spoofed instant
        // kills. We require it in >2 distinct rounds before kicking.
        private const float AcSuspectMaxAccum = 60f;   // victim HP is ~100; <60 received = couldn't legitimately die
        private const float AcMeleeRange = 4.0f;        // melee reach is short; spoofed kills register here
        private const int   AcFlaggedRoundsToKick = 3;  // strictly >2 distinct rounds
        private readonly float[] _acRoundDmgToVictim = new float[4];
        private readonly int[]   _acRoundHitsToVictim = new int[4];
        private readonly Dictionary<int, HashSet<int>> _acFlaggedRounds = new Dictionary<int, HashSet<int>>();
        private readonly HashSet<int> _acKicked = new HashSet<int>();
        private int _acRoundIndex;
        private bool AcEnabled => Environment.GetEnvironmentVariable("SF_AC_BEHAVIOR") != "0";
        // The behavioral "impossible kill" heuristic flags a kill whose victim
        // took little/no SERVER-RECORDED damage. That is NOT sound evidence of
        // cheating: damage is still largely client-authoritative, so legit melee
        // punches, throws, environmental/fall kills, and the intended comp
        // quick-draw instant-shot routinely reach the server as a killing blow
        // with ~0 prior accumulated damage — and get flagged. It has kicked
        // real players repeatedly. So the flag is now LOG-ONLY telemetry by
        // default; the auto-kick requires explicit opt-in (SF_AC_KICK=1) for an
        // operator who has corroborated a real cheater from the logs.
        private bool AcKickEnabled => Environment.GetEnvironmentVariable("SF_AC_KICK") == "1";

        // Phase 6.15 — chat command parser. Body of PktPlayerTalked is
        // raw UTF-8 (verified from decompiled NetworkPlayer.OnTalked). If the
        // text starts with '/' we treat it as a server command. Format mirrors
        // ALKA's MOD_CLIENT.md (/code, /room, /ping, /start initially).
        // === H-P0-3 — admin gating for destructive chat commands ===
        // Two ways to be admin: (a) your handshake SteamID is listed in
        // SF_ADMIN_STEAMIDS (comma-separated SteamID64s), or (b) you run
        // /admin <password> matching SF_ADMIN_PASS this session. With neither
        // env set, the gated commands are simply unavailable (fail closed).
        // SteamIDs are client-asserted in this protocol (no auth ticket), so
        // the password path is the stronger one; the ID list is convenience
        // for trusted regulars on a server whose operator accepts that risk.
        private static HashSet<ulong> _adminSteamIds;
        private static string _adminPass;
        private static bool _adminEnvLoaded;

        // Telemetry for the chat-command research effort (notes/phase6/14-
        // chat-commands.md). The patched DLL sends '/start', '/code', etc.
        // via PktPlayerTalked on channel (slot*2)+3 — body format is raw UTF-8
        // (confirmed from NetworkPlayer.OnTalked decompile). We log the first
        // 20 packets' hex+ASCII as a redundant capture so we can confirm
        // format if the parser misbehaves on edge cases.
        private int _playerTalkedLogged;

        // === ALKA-style anticheat — observation + optional rate-limit ===
        // Per-client sliding window of packet timestamps. By default we only
        // observe + log; set SF_ANTICHEAT_ENFORCE=1 to actually drop excess
        // packets (and return true from AnticheatObserve to signal "drop"
        // — caller in SfDispatch then returns early).
        //
        // Ported from server-go/anticheat.go but tuned conservatively (3-4x
        // typical vanilla SF traffic) so it surfaces real anomalies, not
        // legitimate gameplay bursts.
        private static bool? _enforceCache;
        private static bool AnticheatEnforce
        {
            get
            {
                if (_enforceCache.HasValue) return _enforceCache.Value;
                _enforceCache = Environment.GetEnvironmentVariable("SF_ANTICHEAT_ENFORCE") == "1";
                return _enforceCache.Value;
            }
            set { _enforceCache = value; }  // /anticheat chat command writes here
        }
        // Hard ceiling on distinct tracked source endpoints. When full (active
        // spoofed flood), packets from NEW sources are dropped outright —
        // fail-closed beats unbounded memory growth.
        private const int MaxRateGuardEntries = 256;
        private uint _rateGuardCapDrops;
        private const int MaxAllPerSec        = 240;   // vanilla ≈ 80-100
        private const int MaxPlayerUpdPerSec  = 120;   // vanilla ≈ 60
        private const int MaxDamagePerSec     = 30;    // vanilla bursts <10
        private const int MaxObjectPerSec     = 480;   // boxes/chains can be chatty
        private readonly Dictionary<string, RateGuard> _rateGuards = new Dictionary<string, RateGuard>();
        private int _relayAllCount;
        private float _pendingRoundAdvanceAt = -1f;

        private float _pendingRearmCombatAt = -1f;
        private float _lastPeriodicRearmAt = -1f;

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
            // Build the playable scene set. Excludes:
            //  - 1-5  : menu / lobby scenes
            //  - 102  : the stats / non-MP scene
            //  - SF_EXCLUDE_MAPS : a comma list of extra scene indices to drop —
            //           this is how you remove the LEVEL EDITOR scene (and any
            //           other map that bugs the round logic) from the rotation
            //           WITHOUT a recompile, e.g. SF_EXCLUDE_MAPS="7,118".
            var excluded = new HashSet<int> { 102 };
            try
            {
                string ex = System.Environment.GetEnvironmentVariable("SF_EXCLUDE_MAPS");
                if (!string.IsNullOrEmpty(ex))
                    foreach (var tok in ex.Split(','))
                    {
                        int v;
                        if (int.TryParse(tok.Trim(), out v)) excluded.Add(v);
                    }
            }
            catch { }
            var list = new List<int>();
            for (int i = 6; i <= 124; i++) { if (!excluded.Contains(i)) list.Add(i); }
            _allLandfallMaps = list.ToArray();
        }
        // Recently-played history so we don't revisit the same map back-to-back
        // (or within the last few rounds).
        private static readonly Queue<int> _recentMaps = new Queue<int>();
        private const int _recentMapsAvoidWindow = 6;

        // Drop: client sends ClientRequestWeaponDrop with [playerIdx][posY i16][posZ i16][velY i8][velZ i8].
        // SF host appends GetNextWeaponSpawnID() + GetNextSyncableObjectSpawnID()
        // and broadcasts as WeaponDropped. We mirror that — the IDs are just
        // counters, no state lookup required.
        private ushort _droppedWeaponNextId = 32768;       // give drops a distinct range to avoid colliding with spawn IDs
        private ushort _droppedSyncableNextId = 32768;

        // After first player spawns in the lobby, auto-start a match. The
        // stock SF lobby requires 2+ players to walk under the ready-hat
        // trigger; for solo testing that never fires. So we schedule the
        // match-start ourselves a few seconds after first spawn.
        private float _autoStartAt = -1f;

        // ClientReadyUp from client (walked through the ready hat in lobby).
        // Body: byte playerCount + playerCount × byte playerIndex.
        // Response: broadcast MapChange (load Landfall scene) + StartMatch.
        // Once both go out, clients drop the lobby map, load the new scene,
        // and send ClientRequestingToSpawn for it — we reply with ClientSpawned.
        private bool _matchStarted = false;
        private int _currentSceneIndex = 6; // Desert3 — known-good Landfall map

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

        // Phase 6.9 ghost mode — teleport the auth rig to client position
        // each PlayerUpdate. With body kinematic, Rigidbody.MovePosition does
        // a swept collision check which can push dynamic rigidbodies (boxes/
        // crates) it overlaps. The auth rig is invisible to clients (we
        // don't broadcast ClientSpawned for it) so the player only sees
        // their own player + others; the ghost is a server-side physical
        // body for collision purposes only.
        private static int _ghostMoveLogCount;
        private static int _ghostWakeLogCount;
        private static Type _wakeDpType;
        private static FieldInfo _wakeDpSimpleField;
        private static FieldInfo _wakeDpEventField;

        private static Type _weaponPickUpType;

        // P0-16 — spawn positions captured post-settle; used to reset fallthrough.
        private readonly Dictionary<ushort, Vector3> _nsoSpawnPos = new Dictionary<ushort, Vector3>();
        private float _nsoFallGuardNextAt = -1f;
        private float _nsoPeriodicKeyframeNextAt = -1f;
        private float _sceneLoadRealtime = -1f;
        private int _nsoFallthroughResetCount;
        private readonly Dictionary<ushort, int> _nsoVoidResetCount = new Dictionary<ushort, int>();
        private const float NsoFallResetY = -32f;
        private const int NsoFallMaxResetPerTick = 16;
        // After this many void rescues, the crate clearly has no floor under
        // its spawn (e.g. lobby storage objects high above the map). Freeze it
        // kinematic at spawn so it stops churning the snapshot every tick.
        private const int NsoVoidFreezeAfter = 3;
        private const float NsoPeriodicKeyframeSec = 2f;

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
        // Authoritative-state broadcast rate. Bumped 30→60Hz so server-driven
        // NSOs (boxes) update twice as often → far smoother on clients (combined
        // with client-side velocity extrapolation). Physics already runs 60Hz.
        // Snapshot broadcast rate (server → clients, msg39). Default 60 Hz to
        // match the 60 Hz physics + 60 Hz input — boxes and remote players
        // update every physics tick (combined with client-side extrapolation,
        // maximally smooth). 60 Hz doubles downstream bandwidth vs the old 30;
        // operator confirmed ample bandwidth. Dial per-server with
        // SFHEADLESS_SNAPSHOT_HZ (clamped 10–120) without recompiling.
        private static float _snapshotHzCache = -1f;
        private static float SnapshotHz
        {
            get
            {
                if (_snapshotHzCache < 0f)
                {
                    float v = 60f;
                    var s = Environment.GetEnvironmentVariable("SFHEADLESS_SNAPSHOT_HZ");
                    if (!string.IsNullOrEmpty(s) && float.TryParse(s, out var p) && p > 0f) v = p;
                    _snapshotHzCache = Mathf.Clamp(v, 10f, 120f);
                }
                return _snapshotHzCache;
            }
        }
        private uint  _serverTick;
        private readonly List<Projectile> _projectiles = new List<Projectile>();
        private uint _nextProjId = 1;
        private const float DefaultProjectileSpeed = 60f;     // SF-units/s for pistol
        private const float DefaultProjectileLifetime = 3f;   // 3s before expire
        // Ceiling on client-asserted projectile speed (issue #2). Fastest stock
        // bullets are well under 120u/s; an uncapped value lets a hostile
        // client tunnel projectiles through wall-occlusion linecasts or blow
        // up the swept-sphere math.
        private const float MaxProjectileSpeed = 200f;

        // === Phase 6.18 — server-authoritative thrown weapons (fixes the high-FPS "whiff") ===
        // The client's ThrownWeapon.LateUpdate hit-check is a per-render-frame raycast, so
        // whether a throw lands depends on the thrower's FPS (whiffs high, hits at 60). We
        // model the thrown weapon as a server-side projectile instead — the swept-sphere
        // TestProjectileHit runs on the fixed server tick, so detection is FPS-independent.
        private const float ThrownWeaponSpeed    = 35f;   // Fighting.ThrowWeapon: AddForce(aim*35, VelocityChange)
        private const float ThrownWeaponLifetime = 1.2f;  // after that it's just a pickup on the ground
        private const byte  ThrownWeaponType     = 254;   // marker WeaponType for thrown (not a bullet type)
        // v0 ships in SHADOW mode: detect + LOG hits, emit NO damage. Validates fps-independent
        // detection on live data with zero double-damage / break risk. Flip false (next step)
        // once confirmed + client-side dedup (suppress the vanilla local throw hit) is in.
        private bool _throwAuthShadow = true;

        // Server-emitted bullet damage opt-in (see HandleClientFireWeapon).
        private static bool? _bulletDamageCache;
        private static bool BulletDamageEnabled
        {
            get
            {
                if (_bulletDamageCache == null)
                    _bulletDamageCache = Environment.GetEnvironmentVariable("SFHEADLESS_BULLET_DAMAGE") == "1";
                return _bulletDamageCache.Value;
            }
        }

        // EXPLOSION PARITY (v0.4.1) — blast tunables, env-overridable for live
        // tuning sessions (the three-way `boxes` debug-console diff is the
        // measuring stick). Defaults keep the historical 5u/900f and add the
        // stock-shaped VelocityChange component.
        private static float _blastRadius = -1f, _blastForce = -1f, _blastVelChange = -1f;
        private static float BlastRadius
        {
            get { if (_blastRadius < 0f) _blastRadius = BlastEnvFloat("SFHEADLESS_BLAST_RADIUS", 5f); return _blastRadius; }
        }
        private static float BlastForce
        {
            get { if (_blastForce < 0f) _blastForce = BlastEnvFloat("SFHEADLESS_BLAST_FORCE", 900f); return _blastForce; }
        }
        private static float BlastVelocityChange
        {
            get { if (_blastVelChange < 0f) _blastVelChange = BlastEnvFloat("SFHEADLESS_BLAST_VELCHANGE", 5f); return _blastVelChange; }
        }

        // Stock SF bullets impart a mass-independent velocity change on the
        // crate they hit (which is why heavy crates still fling in vanilla).
        // The wall-hit linecast above already stops the projectile at the
        // crate; this adds the shove. Kick is confined to the Y-Z play plane
        // and SFBoxFix's per-FixedUpdate velocity governor caps the result,
        // so sustained fire can't build up a launch.
        private const float BulletCrateKick = 2.5f; // m/s per hit
        private int _bulletCrateKicks;

        // Returns the slot of the first rig (other than the owner) whose
        // position is within HitRadiusSq of the projectile's new position
        // OR whose segment-from-prev-to-new-position passes within
        // HitRadius of the rig (swept sphere check). -1 if none.
        private const float ProjectileHitRadius   = 1.2f;
        private const float ProjectileHitRadiusSq = ProjectileHitRadius * ProjectileHitRadius;
        private readonly Queue<TickSample> _tickHistory = new Queue<TickSample>(64);
        private const int MaxHistoryTicks = 60;  // ~2s at 30Hz snapshot

        // Cached NSO Index field — looked up lazily once a scene has NSOs.
        private static FieldInfo _nsoIndexField;
        private static System.Reflection.PropertyInfo _nsoIndexProp;
        private static Type _nsoType;

        // Apply an incoming PktObjectUpdate (msgType 26) to the server's
        // local NSO scene state. Body layout (from
        // NetworkSyncableObject.SendNewObjectStatePackage decompile):
        //   u16 Index
        //   i16 PosY/100         (corresponds to Unity world Y)
        //   i16 PosZ/100         (corresponds to Unity world Z)
        //   i16 RotY/100         (unused here)
        //   i16 RotZ/100         (rotation about forward, applied as eulerZ)
        // Without this, every client's box positions diverged because the
        // server didn't know boxes had moved → its v26 snapshot broadcast
        // the boxes' spawn positions, snapping clients back.
        private readonly Dictionary<ushort, Component> _nsoByIndexCache = new Dictionary<ushort, Component>();
        private float _nsoCacheLastRebuildAt = -1f;
        private int _objectUpdateAppliedCount;
        private int _objectUpdateDroppedCount;
        private int _forceBlockRxCount;   // OPEN-3 punch-block (PlayerForceAddedAndBlock) receipts, sample-logged
        // Cached once — checked per inbound ObjectUpdate packet.
        private static bool? _acceptClientCratesCache;
        private static bool AcceptClientCrates
        {
            get
            {
                if (_acceptClientCratesCache == null)
                    _acceptClientCratesCache = Environment.GetEnvironmentVariable("SFHEADLESS_ACCEPT_CLIENT_CRATES") == "1";
                return _acceptClientCratesCache.Value;
            }
        }

        // v0.4.x — apply a client-originated destruction to the server's own
        // DestructiblePiece, so the oracle's world matches the clients'. Uses
        // the stock "NetworkForce*" apply methods (network-applied, NO
        // re-broadcast — we already relayed separately, so no loop). All three
        // packet types start with the i16 NSO index; type 30 also carries
        // force.y/z (i16/100) + multiplier (f32). Mirrors DestructiblePiece.
        // ReceivedDestruction + ReceivedPackage dispatch.
        private static Type _dpDestType;
        private static MethodInfo _dpSimpleDestM, _dpEventDestM, _dpForceDestM;
        private static bool _dpDestLookupTried;
        private int _destructAppliedCount, _destructMissCount;
        private readonly List<NsoSrvEntry> _nsoSrvEntries = new List<NsoSrvEntry>();

        // Gather NSOs that need broadcasting this tick. Three include cases:
        //   1. Non-kinematic NSO with current velocity (boxes being pushed,
        //      crates falling).
        //   2. Kinematic NSO whose position changed since last snapshot
        //      (moving platforms, animator-driven kinematic bodies — these
        //      have isKinematic=true but their transform is being driven by
        //      Animator/script).
        //   3. NSO that was in case 1 or 2 within the last ~1s ("keepalive"
        //      so smoothing on the client catches the final settle frame
        //      after motion stops, not a stale snap from 33ms ago).
        //
        // Static crates that haven't moved skip — saves bandwidth.
        private readonly Dictionary<ushort, Vector3> _nsoLastBroadcastPos = new Dictionary<ushort, Vector3>();
        private readonly Dictionary<ushort, float>   _nsoLastMovedAt      = new Dictionary<ushort, float>();
        private const float NsoPosDeltaThreshold = 0.01f;   // ~1 cm
        private const float NsoKeepaliveSec      = 3.0f;
        private const float NsoCrateKeepaliveSec = 25.0f;

        // Freeze NSO rigidbodies that fell out of the playable area.
        // Stock SF's host kills crates that cross the killbox (Y<-50);
        // we don't have that cleanup, so falling crates accelerate
        // forever, eventually slamming into destructibles (chains, ice)
        // and breaking them with no player input.
        // Fix: periodically scan all NSOs in scene; any with Y < -25
        // gets isKinematic=true. Stops the fall + the broadcast spam.
        private float _nsoFreezerNextAt = -1f;

        private static readonly StringBuilder _sb = new StringBuilder(2048);

        // Lookup cache for InControl.PlayerAction.UpdateWithValue MethodInfo.
        private static MethodInfo _cachedUpdateWithValue;
        private static bool _loggedUpdateWithValue;

        private static bool _loggedPushPath;
        private static readonly Dictionary<string, FieldInfo> _pushFieldCache = new Dictionary<string, FieldInfo>(64);
        private static readonly object[] _pushArgsBuffer = new object[3];

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
        private static float _writeInputsErrLogAt = -1f;
        private float _boxDiagLastAt = -1f;

        private static void ReadEnv()
        {
            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_PORT"), out var p);
            if (p > 0 && p < 65536) BindPort = p;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_BRIDGEPORT"), out var bp);
            if (bp > 0 && bp < 65536) BridgePort = bp;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_SCENE"), out var s);
            if (s >= 0) InitialScene = s;

            Verbose = Environment.GetEnvironmentVariable("SFHEADLESS_DEBUG") == "1";

            float fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_ROUND_END_DELAY"), out fv) && fv >= 0f && fv <= 10f)
                RoundEndDelaySec = fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_NEXT_MATCH_DELAY"), out fv) && fv >= 0f && fv <= 10f)
                NextMatchDelaySec = fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_ROUND_MIN_PLAY"), out fv) && fv >= 3f && fv <= 60f)
                RoundMinPlaySec = fv;
            if (float.TryParse(Environment.GetEnvironmentVariable("SF_PRE_COMBAT_DELAY"), out fv) && fv >= 1f && fv <= 10f)
                OraclePreCombatGraceSec = fv;

            Log.LogInfo($"Config: BindPort={BindPort} BridgePort={BridgePort} InitialScene={InitialScene} Verbose={Verbose} RoundEndDelay={RoundEndDelaySec:0.0}s NextMatchDelay={NextMatchDelaySec:0.0}s RoundMinPlay={RoundMinPlaySec:0.0}s PreCombatGrace={OraclePreCombatGraceSec:0.0}s");
        }

    }
}
