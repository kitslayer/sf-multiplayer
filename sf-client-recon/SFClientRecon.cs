using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFClientRecon
{
    // Phase 6.11+ — client-side reconciliation companion plugin.
    //
    // Deployed to each PLAYER's SF install. Listens on UDP 1339 (default;
    // env SFCLIENTRECON_PORT) for v26 snapshots from the oracle, applies
    // them, sends inputs back at 60Hz.
    //
    // ============================================================
    //                    TABLE OF CONTENTS
    // ============================================================
    //  Anchor (search this line)                          ~Line
    //  ─────────────────────────────────────────────────  ─────
    //  Awake — bootstrap, harmony patches, UDP bind       ~ 100
    //  RxLoop — UDP receive thread                        ~ 240
    //  HandlePacket — snapshot parser (v26.5)             ~ 250
    //  Update — apply pending snapshots + send inputs     ~ 350
    //  SmoothTowardTargets — per-frame exponential lerp   ~ 395
    //  ApplyNsoSnapshot (P0-15 large-lerp flag here)      ~ 490
    //  ApplyMapSyncSnapshot (P0-14)                       ~ 560
    //  SendPlayerInputPacket — 60Hz outbound              ~ 630
    //  DestructibleCollisionPrefix (P0-15 guard)          ~ 680
    //  WeaponShootPostfix — emits PktClientFireWeapon     ~ 740
    //  Input ring buffer + divergence-snap (Phase 6.12.2) ~ 790
    // ============================================================
    //
    // Wire protocol: see ../notes/PROTOCOL.md
    // Foundation for client-prediction + server-reconciliation:
    //   - Snapshot smoothing (Phase 6.11.2) ✓ shipped
    //   - Hard-snap on divergence > 2.5u (Phase 6.12.2 v0.2) ✓ shipped
    //   - Full input-replay rollback (Phase 6.12.2 v1.0) — pending
    //
    // This is the foundation for client-prediction + server-reconciliation:
    //   - For now, snap (no smoothing, no replay).
    //   - Phase 6.11.2 will smooth-interpolate over ~100ms.
    //   - Phase 6.12 adds the playerInput outbound side + sequence-numbered
    //     replay rollback (CSGO/Valorant model).
    //
    // Wire format consumed (matches sf-headless-host BroadcastWorldStateSnapshot):
    //   v25 envelope: [u32 ts][u8 msgType][N body][u64 steamID][u8 channel]
    //   msgType: 39 (PktWorldStateSnapshot)
    //   body:
    //     u32 serverTick (LE)
    //     u8  playerCount
    //     for each player: u8 slot, f32 posX, f32 posY, f32 posZ  (all LE)
    //
    // Skip-on-batchmode: the oracle's headless SF instance also loads this
    // plugin (it's in the same plugins/ dir) — we no-op there.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.client-recon";
        public const string PluginName = "SFClientRecon";
        public const string PluginVersion = "0.6.2";

        internal static ManualLogSource Log;
        // Verbose per-tick diagnostics OFF by default — they spammed the log and
        // cost string-format + file I/O every snapshot/input/shot. Set
        // SF_VERBOSE_LOG=1 to re-enable for debugging. Keeps the log clean + lean.
        internal static readonly bool VerboseDiag =
            System.Environment.GetEnvironmentVariable("SF_VERBOSE_LOG") == "1";
        internal static bool RefOk(object o) => !ReferenceEquals(o, null);
        // Default v26 listener port. Overridable via SFCLIENTRECON_PORT env
        // var for multi-instance same-machine testing (each instance picks a
        // different port). Server discovers the actual port from our PlayerInput
        // packet source addr — no protocol change required for non-default ports.
        private const int V26_DEFAULT_PORT = 1339;

        private int _listenPort;
        private UdpClient _socket;       // bidirectional: RX snapshots + TX inputs
        private volatile IPEndPoint _serverEp;    // oracle's address (read from -address/-port argv); volatile — read on RX thread (C5)
        private Thread _rxThread;
        private volatile bool _running;

        // Phase 6.12 — outbound input sequence numbers (monotonic).
        private uint _inputSeq;
        private float _lastInputSendAt;
        private const float InputSendInterval = 1.0f / 60.0f;  // 60Hz cap
        // (C2) Removed the input-history ring buffer (_historySeq/_historyPos/
        // _historyLookup), _serverLastAckedSeq, and _divergenceLogged/_lastShiftAt:
        // nothing read them (the ApplySnapshot drift-diff is disabled), yet the
        // recorder ran FindObjectsOfType(NetworkPlayer) on every input send. Re-add
        // when input-replay rollback is actually implemented.

        // Pending snapshot (set on RX thread, applied on main thread).
        private readonly object _snapLock = new object();
        private List<SnapshotEntry> _pendingSnap;
        private List<NsoSnapEntry>  _pendingNsoSnap;
        private List<MapSyncSnapEntry> _pendingMapSyncSnap;
        private uint _pendingTick;
        private uint _snapsReceived;
        private uint _snapsApplied;

        // --- Lobby SELECT (sf-router single-port front-door) --------------------
        // The lobby this client wants. Set from the in-game browser via
        // RequestJoinLobby (SfOracleLobbyConnect.cs); defaults to SF_LOBBY env or
        // "MAIN" so a no-browser Quick Match still routes somewhere. We emit a
        // SELECT control datagram (router-only framing, see notes/PROTOCOL.md) on
        // the v26 socket so the router pins this client to the lobby's backend.
        // A SELECT is harmless to a direct (no-router) backend — it parses as an
        // out-of-range msgType and is ignored — so we can always send it.
        internal static string SelectedLobbyCode = "";
        private const float SelectResendInterval = 0.2f;   // 5Hz while not connected
        private uint  _selectNonce;
        private float _lastSelectAt = -1f;
        private uint  _lastSeenSnapCount;
        private float _lastSnapGrowAt = -1f;
        private int   _selectLogs;

        // Cached local-player slot — discovered lazily once a Controller with
        // mHasControl=true appears in the scene.
        private int _localSlot = -1;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            InstallUnityConsoleTee();
            // v0.6.0 — track the current map scene (mirrors the oracle's
            // _currentMapSceneName). NSO ids are PER-MAP and collide while
            // both map scenes coexist during the additive transition; the
            // cache rebuild must only accept objects from the newest map or
            // the reconciler matches snapshot ids against LAST round's
            // objects (~40u away) and teleports them (observed live:
            // crates=162 on a 90-crate map, meanErr=37, thousands of snaps).
            SceneManager.sceneLoaded += OnSceneLoadedTrackMapScene;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-batchmode" || arg == "-nographics")
                {
                    Log.LogInfo($"{PluginName}: batchmode detected — client-recon does nothing on oracle. Bye.");
                    return;
                }
            }
            // Uncap FPS so local Movement prediction runs at the highest rate
            // the user's hardware allows — smoother feel + more accurate
            // reconciliation between snapshot boundaries. Cribbed from
            // ALKA's SFClientEnhancements; cost is zero, value is real.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            // Default physics tickrate: 60Hz on both client and server. SF's
            // stock default is 50Hz; bumping to 60 makes local prediction
            // match the per-tick step the server runs at (also 60 by default
            // in our plugin), so reconciliation has less per-tick error to
            // correct. Per-second force is preserved because Movement.cs
            // scales by Time.deltaTime.
            Time.fixedDeltaTime = 1f / 60f;
            Physics.sleepThreshold = 0.011f;
            Physics.defaultContactOffset = 0.01f;

            var nsoSmoothEnv = Environment.GetEnvironmentVariable("SFCLIENTRECON_NSO_SMOOTH");
            if (!string.IsNullOrEmpty(nsoSmoothEnv) && float.TryParse(nsoSmoothEnv, out var nsr) && nsr > 1f)
                _nsoSmoothRate = nsr;

            // Resolve listener port — env var override for multi-instance testing.
            _listenPort = V26_DEFAULT_PORT;
            var envPort = Environment.GetEnvironmentVariable("SFCLIENTRECON_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var pp)) _listenPort = pp;

            // Default lobby for a no-browser Quick Match (router routes by code).
            var envLobby = Environment.GetEnvironmentVariable("SF_LOBBY");
            SelectedLobbyCode = string.IsNullOrEmpty(envLobby) ? "MAIN" : envLobby.Trim().ToUpper();

            // Phase 6.17 v0.1 — hook Weapon.ActuallyShoot so we know when the
            // local player fires. Send PktClientFireWeapon to the oracle so it
            // can register + simulate a server-side projectile.
            try
            {
                var weaponType = AccessTools.TypeByName("Weapon");
                if ((object)weaponType != null)
                {
                    var actuallyShoot = AccessTools.Method(weaponType, "ActuallyShoot");
                    if ((object)actuallyShoot != null)
                    {
                        var harmony = new Harmony(PluginGuid + ".weapon-shoot");
                        var postfix = AccessTools.Method(typeof(Plugin), nameof(WeaponShootPostfix));
                        harmony.Patch(actuallyShoot, postfix: new HarmonyMethod(postfix));
                        Log.LogInfo("Patched Weapon.ActuallyShoot postfix → emits PktClientFireWeapon.");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"Weapon.ActuallyShoot patch failed: {e.Message}"); }

            // P0-15 — suppress DestructiblePiece collisions during/just-after
            // snapshot-lerp. Without this, swept-lerp motion of NSOs (boxes,
            // ice debris) can hit adjacent ice/destructibles with high
            // relativeVelocity → SendDestructMessage fires → server forwards →
            // ice/box "randomly" breaks for everyone. Solution: track which
            // NSO root transforms were lerped in the last ~150ms; the prefix
            // returns false (skip stock OnCollisionEnter) when the colliding
            // body's root is on that recent-lerp list.
            try
            {
                var dpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)dpType != null)
                {
                    var onColl = AccessTools.Method(dpType, "OnCollisionEnter");
                    if ((object)onColl != null)
                    {
                        var harmony = new Harmony(PluginGuid + ".destructible-guard");
                        var prefix = AccessTools.Method(typeof(Plugin), nameof(DestructibleCollisionPrefix));
                        harmony.Patch(onColl, prefix: new HarmonyMethod(prefix));
                        Log.LogInfo("Patched DestructiblePiece.OnCollisionEnter prefix → suppresses destructions on recently-lerped bodies.");
                    }
                    else Log.LogWarning("[P0-15] DestructiblePiece.OnCollisionEnter not found.");
                }
                else Log.LogWarning("[P0-15] DestructiblePiece type not found.");
            }
            catch (Exception e) { Log.LogWarning($"[P0-15] DestructiblePiece patch failed: {e.Message}"); }

            // Crate-cull fix — stock IgnorePlayerWhenOffScreen.Update sets the
            // object's layer to 24 (no-collision) whenever y < -11f. That -11f
            // is hardcoded for the default 10-unit map; on larger maps a crate
            // that is still in-bounds sits below -11 and loses collision, so it
            // "ghosts"/vanishes client-side independent of the server killbox.
            // Transpile the constant to scale with MapSizeHandler.mapSize.
            try
            {
                var ipType = AccessTools.TypeByName("IgnorePlayerWhenOffScreen");
                if ((object)ipType != null)
                {
                    var upd = AccessTools.Method(ipType, "Update");
                    if ((object)upd != null)
                    {
                        var harmony = new Harmony(PluginGuid + ".crate-cull-fix");
                        var transpiler = AccessTools.Method(typeof(Plugin), nameof(IgnoreOffScreenCullTranspiler));
                        harmony.Patch(upd, transpiler: new HarmonyMethod(transpiler));
                        Log.LogInfo("Patched IgnorePlayerWhenOffScreen.Update → cull threshold scales with map size.");
                    }
                    else Log.LogWarning("[crate-cull] IgnorePlayerWhenOffScreen.Update not found.");
                }
                else Log.LogWarning("[crate-cull] IgnorePlayerWhenOffScreen type not found.");
            }
            catch (Exception e) { Log.LogWarning($"[crate-cull] patch failed: {e.Message}"); }

            // v0.6.0 — null-channels guard on P2PPackageHandler.IsPacketAvailable.
            // In oracle-connect mode the Steam P2P channel array never
            // initializes, so SyncableObjectManager.LateUpdate's per-frame
            // packet poll threw ~150 NullReferenceExceptions PER SECOND on
            // every client (22k+/session in output_log) — a chronic hidden
            // frame-rate tax. The oracle has the same guard in SFHeadlessHost;
            // it must live HERE too because p2-style clients don't load the
            // host plugin (and its patch suite is batchmode-gated anyway).
            // NRE-storm fix attempt history (2026-06-11): a Harmony PREFIX on
            // P2PPackageHandler.IsPacketAvailable broke the v25 handshake
            // ("Connecting to the server..." hang) — and so did a FINALIZER,
            // so the breakage comes from patching that method AT ALL (it is
            // the patched DLL's packet pump). The storm is now fixed at the
            // SOURCE instead: TickChannelNullFill (Update) fills null channel
            // slots with empty queue instances, so stock code sees "empty
            // queue = no packet" with zero patching. DO NOT Harmony-patch
            // IsPacketAvailable on clients.

            InstallClientTerrainPatches();
            InstallOracleLobbyConnectPatches();
            InstallNsoClientPushPatches();
            // Per-type ONLY (transpiles the 3 gimmick types' Update; never touches
            // crate NSOs). The earlier "imposibles de mover" was the 0.7 friction,
            // not this patch — friction is back to normal now, so re-enabled.
            InstallMapScriptLocalPatches();
            InstallMusicCrashGuard();

            // Phase 6.12 — resolve the server endpoint BEFORE starting the RX
            // thread so the RX source-address filter (_serverEp != null) is armed
            // from the first datagram (C5: no brief unfiltered window at startup).
            // Mirrors the patched DLL's CLI parsing: -address X -port Y, or
            // BepInEx/config/sf-oracle-endpoint.txt when launched from Steam.
            ResolveOracleEndpoint();
            try
            {
                IPAddress ip;
                string serverHost = OracleServerHost;
                int serverPort = OracleServerPort;
                if (!IPAddress.TryParse(serverHost, out ip))
                    ip = Dns.GetHostAddresses(serverHost)[0];
                _serverEp = new IPEndPoint(ip, serverPort);
                Log.LogInfo($"PlayerInput TX → {_serverEp} via :{_listenPort} (msgType 40, 60Hz).");
            }
            catch (Exception e)
            {
                Log.LogError($"Server addr parse failed: {e.Message}. PlayerInput disabled.");
            }

            Log.LogInfo($"{PluginName} {PluginVersion}: starting v26 snapshot listener on UDP :{_listenPort}. vSync off, FPS uncapped.");
            try
            {
                _socket = new UdpClient(_listenPort);
                _running = true;
                _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "SFClientRecon-RX" };
                _rxThread.Start();
                Log.LogInfo("RX thread started (bidirectional — same socket also TXes PlayerInput).");
            }
            catch (Exception e)
            {
                Log.LogError($"UDP bind on {_listenPort} failed: {e.Message}. Reconciliation disabled.");
            }
        }

        private void OnDestroy()
        {
            _running = false;
            try { _socket?.Close(); } catch { }
        }

        // Top-of-screen announcement banner (driven by msgType 42).
        private volatile string _bannerText;
        private DateTime _bannerUntilUtc = DateTime.MinValue;
        private GUIStyle _bannerStyle;

        private void OnGUI()
        {
            var text = _bannerText;
            if (string.IsNullOrEmpty(text) || DateTime.UtcNow >= _bannerUntilUtc) return;

            if (_bannerStyle == null)
            {
                _bannerStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 26,
                    fontStyle = FontStyle.Bold,
                    wordWrap = true,
                };
            }

            float w = Mathf.Min(900f, Screen.width * 0.9f);
            float h = 56f;
            var rect = new Rect((Screen.width - w) / 2f, 24f, w, h);

            // Drop shadow for legibility over any map background.
            var shadow = new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(shadow, text, _bannerStyle);
            GUI.color = new Color(1f, 0.85f, 0.2f, 1f);
            GUI.Label(rect, text, _bannerStyle);
            GUI.color = prev;
        }

        private uint _rxRejects;
        private uint _rxSockErrs;

        private static Type _ctrlTypeForNp;
        private static FieldInfo _ctrlPlayerIdField;
        // "Attempt once" guards: AccessTools.Field/TypeByName logs a warning and
        // rescans the whole type every call when the member is genuinely absent.
        // A plain `if (field == null) field = AccessTools.Field(...)` therefore
        // re-attempts (and re-warns) EVERY frame for a missing field — that is the
        // per-frame log-spam that tanked FPS (the crates "se ven lentas" because
        // the game is drowning in reflection). These flags make us look up exactly
        // once and then cache the result (even null) forever.
        private static bool _ctrlLookupTried, _ctrlPidLookupTried;

        // Shared, attempt-once cache of the Controller type + its mHasControl /
        // playerID fields, reused by FindLocalSlot and WeaponShootPostfix so
        // neither re-scans (and re-warns) per frame / per shot.
        private static FieldInfo _ctrlHasControlField;
        private static bool _ctrlHasCtrlLookupTried;

        private static float _nextFpsReapplyAt;
        private void Update()
        {
            if (!_running) return;
            // KEEP FPS UNCAPPED. Stock SF (and some menu transitions) re-assert
            // Application.targetFrameRate = 60 / vSyncCount = 1 after our Awake,
            // re-capping the game to 60 ("limitado a 60 fps"). Re-apply the uncap
            // ~once a second so it always sticks. Cheap (two field writes).
            if (Time.unscaledTime >= _nextFpsReapplyAt)
            {
                _nextFpsReapplyAt = Time.unscaledTime + 1f;
                if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;
                if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            }
            try { TickOracleAutoConnect(); } catch { }
            try { TickDebugConsole(); } catch { }
            try { TickChannelNullFill(); } catch { }
            List<SnapshotEntry> snap;
            List<NsoSnapEntry>  nsoSnap;
            List<MapSyncSnapEntry> mapSyncSnap;
            List<MapStateSnapEntry> mapStateSnap;
            uint tick;
            System.Threading.Monitor.Enter(_snapLock);
            try
            {
                snap    = _pendingSnap;
                nsoSnap = _pendingNsoSnap;
                mapSyncSnap = _pendingMapSyncSnap;
                mapStateSnap = _pendingMapStateSnap;
                tick    = _pendingTick;
                _pendingSnap    = null;
                _pendingNsoSnap = null;
                _pendingMapSyncSnap = null;
                _pendingMapStateSnap = null;
            }
            finally { System.Threading.Monitor.Exit(_snapLock); }
            if (snap != null)    ApplySnapshot(snap, tick);
            if (nsoSnap != null) ApplyNsoSnapshot(nsoSnap, tick);
            if (mapSyncSnap != null) ApplyMapSyncSnapshot(mapSyncSnap, tick);
            if (mapStateSnap != null) ApplyMapStateSnapshot(mapStateSnap);

            // Joiner path (OnSocketServerJoined) never sets
            // mHasBeenInitializedFromServer, so MapChange(18) is dropped and the
            // client stays stuck in the lobby on /start. Snapshots flowing proves
            // we're connected; re-assert the flag here on the main thread
            // (FindObjectOfType is main-thread only). The connect-mode postfix
            // that normally does this doesn't install on server-browser joins.
            if (_oracleConnectStarted && Time.realtimeSinceStartup - _lastInitForceAt > 1f)
            {
                _lastInitForceAt = Time.realtimeSinceStartup;
                ForceInitializedFromServer(null);
            }

            // Phase 6.11.2 — between snapshots, exponentially lerp current
            // positions toward latest targets so the visual feel is smooth
            // instead of teleporting every 33ms (30Hz snapshot rate).
            SmoothTowardTargets();
            TickNsoClientPushRelay();
            TickFastCombatInput();

            // Lobby SELECT: keep the router pinned to our chosen lobby until
            // snapshots are flowing. Snapshot-flow is the "connected" signal;
            // tracked on the main thread (the RX thread can't read Unity time).
            // Self-heals on a lobby switch (snapshots stop → SELECT resumes).
            if (!string.IsNullOrEmpty(SelectedLobbyCode) && _socket != null && _serverEp != null)
            {
                float nowSel = Time.realtimeSinceStartup;
                if (_snapsReceived != _lastSeenSnapCount) { _lastSeenSnapCount = _snapsReceived; _lastSnapGrowAt = nowSel; }
                bool snapsFlowing = _lastSnapGrowAt > 0f && (nowSel - _lastSnapGrowAt) < 1f;
                if (!snapsFlowing && nowSel - _lastSelectAt >= SelectResendInterval)
                {
                    _lastSelectAt = nowSel;
                    SendSelectLobbyPacket();
                }
            }

            // Phase 6.12 — send input packets at 60Hz once local slot is known.
            if (_socket != null && _serverEp != null && Time.realtimeSinceStartup - _lastInputSendAt >= InputSendInterval)
            {
                _lastInputSendAt = Time.realtimeSinceStartup;
                SendPlayerInputPacket();
            }
            else if (_serverEp != null && FindLocalSlot() < 0
                     && Time.realtimeSinceStartup - _lastInputWarnAt > 5f)
            {
                _lastInputWarnAt = Time.realtimeSinceStartup;
                Log.LogWarning("[P6.12] No local slot yet — PlayerInput not sent. Wait for spawn.");
            }
        }

        // v0.6.0 — full Unity console tee. Subscribes to the engine's log
        // callback and streams EVERY console line (all log levels, stack
        // traces on errors, TIMESTAMPED — which output_log.txt lacks) to a
        // per-instance file, so the live console can be followed from outside
        // the process and correlated across instances. Path is unique per
        // instance via the v26 listen port (p1=1340, p2=1342, default 1339).
        private static System.IO.StreamWriter _consoleTee;
        private static readonly object _consoleTeeLock = new object();

        // v0.6.0 — kill the per-frame NRE storm AT THE SOURCE. In
        // oracle-connect mode some P2PPackageHandler channel queue slots are
        // never created, so the stock per-frame packet poll
        // (SyncableObjectManager.LateUpdate → ListenForPackages →
        // IsPacketAvailable) threw ~150 NREs/s per client. Filling the null
        // slots with empty queue instances gives stock code exactly the
        // semantics it expects ("empty queue → no packet") without Harmony-
        // patching the packet pump (which broke the v25 handshake — see the
        // install-site comment). Re-checked every 5s: scene/reconnect churn
        // can recreate the handler.
        private static FieldInfo _ppChannelsFillField;
        private static bool _ppChannelsFillLookupTried;
        private static Type _ppTypeForFill;
        private float _chanFillNextAt = -1f;
        private int _chanFillTotal;

        // Smoothing targets — apply each frame in Update via exponential lerp.
        // Higher SmoothRate = snappier (less lag, more jitter).
        // Lower = smoother (more lag, less jitter). 15/s is a reasonable middle
        // ground at 30Hz snapshot rate (settles ~95% of error in 200ms).
        private const float SmoothRate = 15f;
        // With velocity extrapolation the render target already moves smoothly
        // frame-to-frame, so a moderate chase rate keeps boxes tight AND smooth.
        // (Was 100 = teleport-to-target each frame → looked like the snapshot
        // rate, i.e. "5fps" boxes.)
        private float _nsoSmoothRate = 35f;
        private const float NsoSnapDistance = 0.5f;
        // Cap how far we extrapolate past the last snapshot. If updates stop
        // (box landed / server hiccup) we stop drifting after this window.
        private const float NsoMaxExtrapSec = 0.18f;
        private float _lastInputWarnAt = -1f;
        private float _lastInitForceAt = -1f;
        private static Type _weaponPickUpTypeClient;
        private readonly Dictionary<int, Vector3> _playerTargets = new Dictionary<int, Vector3>();
        private readonly Dictionary<ushort, PoseTarget> _nsoTargets = new Dictionary<ushort, PoseTarget>();
        // Briefly make pushed crates non-kinematic for local collision feedback
        // (no mHasControl — server remains authoritative via v26 snapshots).
        // v0.2.1 — client NSOs stay kinematic; no _nsoClientDynamicUntil (caused ice/box chaos).
        private static Type _clientDpType;
        private static System.Reflection.FieldInfo _clientDpSimpleField;
        private static System.Reflection.FieldInfo _clientDpEventField;
        // (Removed the v2 ReconcilePushableCrate spring reconciler — dead code.
        // Pushable crates are now pure local physics with no per-frame server
        // correction; only the velocity clamp below + the hard-snap in
        // ApplyNsoSnapshot touch them.)

        // Cap how fast a pushable crate can move. SF bullets impart velocity in a
        // mass-INDEPENDENT way (direct velocity change), so a heavy crate still got
        // launched "disparada muy rápido" by gunfire while explosions (mass-scaled
        // AddExplosionForce) behaved fine. Clamping linear velocity per frame stops
        // the fling without touching the player push (which never reaches the cap)
        // or explosions (good distances stay under it).
        // Axis-aware caps. Y is the vertical axis in SF (gravity = -Y, killbox at
        // Y<-30). We cap HORIZONTAL speed (X/Z — that's the direction bullets fling
        // a crate) and the UPWARD impulse, but let a crate FALL naturally (large
        // downward cap) so it doesn't descend in slow motion.
        // Bullets vs explosions distinction: bullets fly nearly pure-horizontal
        // (v.y ~= 0), explosions impart a vertical component too (they launch the
        // crate up + outward). So we cap horizontal STRICTLY when there's no
        // vertical motion (bullets), but allow a MUCH higher horizontal cap when
        // a vertical kick is present (explosions feel powerful, fly visibly).
        // 2.6 m/s was far too low — a player push barely moved a crate, so they
        // felt sluggish / "lentas". 6 m/s lets a deliberate push be responsive
        // while still well under a bullet's fling speed (which the blast cap and
        // the server governor catch).
        // v0.6.0 — caps mirror SFBoxFix's GovernCrateVelocity exactly. The
        // client used to cap harder (2.5) than the server (6.0); under
        // reconciliation an asymmetric cap means the authority moves crates
        // faster than the prediction allows → constant forward-drag corrections.
        private const float CrateMaxHoriz       = 6.0f;   // m/s — SFBoxFix.CrateMaxHoriz
        private const float CrateMaxHorizBlast  = 14.0f;  // m/s — SFBoxFix.CrateMaxHorizBlast
        private const float CrateVertTrigger    = 2.0f;   // |v.y| above this enables the explosion cap
        private const float CrateMaxUp          = 9.0f;   // m/s upward — lets explosions launch crates
        private const float CrateMaxFall        = 30.0f;  // m/s downward — natural gravity fall
        // Air-tumble: crates falling through the air with no spin look frozen/stiff
        // ("no giran ni rotan en el aire"). Nudge a gentle WORLD-X tumble — X is the
        // visible in-plane spin axis for the Y-Z play plane (now freed in the crate
        // constraint mask). Bounded + deterministic sign per body so it's stable.
        private const float CrateAirTumbleSpeed   = 3.0f; // |down v| above which we add tumble
        private const float CrateAirTumbleTorque  = 0.16f;// mass-scaled gentle spin
        private const float CrateAirTumbleMaxAngSqr = 9f; // skip if already spinning (~3 rad/s)
        // Settle dead-zone: a crate creeping below these speeds has no business
        // drifting — zero it so it sits still ("poco a poco se resbalan/deslizan").
        // Doesn't touch the rotation LOGIC, only kills sub-perceptual residual motion.
        private const float CrateSettleLin = 0.16f;  // m/s
        private const float CrateSettleAng = 0.22f;  // rad/s
        private float _crateDiagAt2 = -1f;

        // ====================================================================
        //  STACK BEHAVIOR + PLAYER PUSH DAMPING + OVERHANG TIP
        //  Three coordinated behaviours run each FixedUpdate after the velocity
        //  clamp, all driven by per-crate "is something above? is a player
        //  touching? am I supported?" probes:
        //
        //  (A) STACK COHESION — if another crate is RIGHT ABOVE this one (we're
        //      supporting load), damp our angular velocity strongly so the stack
        //      doesn't twist apart when impacted (a vertical impulse lifts the
        //      whole column instead of spreading sideways). Also skip the
        //      overhang tip on load-bearing crates so a stack on a ledge doesn't
        //      collapse from a phantom tip while still under load.
        //
        //  (B) PLAYER PUSH DAMPING — the player's character controller can shove
        //      crates around easily via collision impulses. While a player is
        //      touching this crate, attenuate the horizontal velocity each tick
        //      → crates feel HEAVY when walked-into without being immovable
        //      (the player can still push, just slower). Independent of mass so
        //      explosions/bullets aren't affected.
        //
        //  (C) OVERHANG TIP — gravity torque about the unsupported edge, applied
        //      explicitly because PhysX's box-on-box contact persistence cancels
        //      the natural torque. Now also skipped when in a stack (A).
        // ====================================================================
        private static readonly Collider[] _contactBuf = new Collider[12];
        private static Type _ctrlTypeClient, _npTypeClient;

        // ─────────────────  CRATE BEHAVIOUR TUNABLES ─────────────────
        // Stack cohesion (anti-twist when the column is at rest)
        private const float StackAngularDamp     = 0.84f;   // light damp on settled stacks (no weld)
        private const float StackSettledSpeed    = 1.6f;    // ≤ this, the stack is "settled"
        // Player shove (heavy feel when player walks/jumps into a crate)
        private const float PlayerHorizDamp      = 0.22f;   // very strong damp — player feels heavy
        private const float PlayerAngularDamp   = 0.65f;   // damp spin while player in contact
        // Hard player-impulse cap: a single tick can only impart this much horiz vel
        private const float PlayerImpartCapH    = 0.9f;    // m/s per tick
        // Edge-tipping torque (mass × gravity × multiplier)
        private const float OverhangTorqueMul   = 0.85f;   // perched/static crate → strong tip
        private const float OverhangFallTorqueMul = 0.45f; // already-falling crate → moderate extra tumble
        // Free-fall tumble — once a crate is fully airborne, nudge a small spin so it tumbles visually
        private const float FallTumbleStartSpeed = 2.4f;    // |v.y| above this → consider as "falling"
        private const float FallTumbleTorque     = 0.18f;   // mass-scaled tumble kick
        // Stack disassembly — when a load-bearing crate is hit from below, scatter the upper crate
        private const float StackPopUpwardImpulse  = 0.42f; // upward separation impulse on the crate above (× mass)
        private const float StackPopLateralImpulse = 0.28f; // sideways jitter to break the column visually
        // Void rescue — teleport crates back instead of letting them fall forever
        private const float CrateVoidRescueY      = -22f;
        private const float CrateVoidRescueMinSafeY = -20f;   // safe pos must be above this to be usable
        private static readonly Dictionary<int, Vector3> _crateSafePos = new Dictionary<int, Vector3>(64);
        private static readonly Dictionary<int, float>   _crateSafeAt  = new Dictionary<int, float>(64);
        private static readonly Dictionary<int, float>   _crateFallStartedAt = new Dictionary<int, float>(64);

        // Match-active gate: never touch crate physics outside an active match.
        // The PLAY ONLINE menu (and main menu) renders decorative barrels that
        // are themselves NetworkSyncableObjects; running ApplyStackAndContactBehavior
        // there applied torques + clamps to those decorations → menu barrels flew
        // around like crazy. We check GameManager.inFight via reflection (cached).
        private static Type _gmTypeFx; private static System.Reflection.FieldInfo _gmInFightFx;
        private static bool _gmFxLooked;

        // Clamp in FixedUpdate (the physics step). All crate behaviours gated on
        // IsMatchActive() so menus / lobbies are NEVER touched. The aggressive
        // tip/pop/tumble code was reverted — it created more visual chaos than
        // benefit. Only the velocity clamp remains (kills bullet fling) which is
        // both safe and necessary. Stack/player damping kept but minimal.
        private void FixedUpdate()
        {
            if (!_running) return;
            if (!IsMatchActive()) return;
            try { ClampCrateVelocities(); } catch { }
            // v0.6.0 — steer predicted crates toward the oracle's authoritative
            // pose (runs in the physics step; velocity-based, see SfNsoClientPush).
            try { ReconcilePushableCrates(); } catch { }
        }
        private static Type _dontEnableRigType;
        // Rigidbody instance IDs already given the crate physics config (grip,
        // continuous collision, constraints) — so the 2s cache rebuild doesn't
        // re-touch / wake settled crates.
        private readonly HashSet<int> _crateConfigured = new HashSet<int>();
        private float _lastSmoothErrAt = -1f;

        // High-friction, no-bounce material so locally-simulated crates grip and
        // stack instead of sliding off each other. Single shared instance.
        private static PhysicMaterial _clientGripMaterial;
        private static PhysicMaterial ClientGripMaterial
        {
            get
            {
                if ((object)_clientGripMaterial == null)
                {
                    // Low friction + Minimum combine: a crate (even with neighbours)
                    // slides easily when pushed/shot/blasted — pushing a row no
                    // longer has to fight the floor friction of every crate. A
                    // resting flat stack still holds (no lateral force at rest), so
                    // we don't reintroduce the "slide apart" problem.
                    _clientGripMaterial = new PhysicMaterial("CrateGripClient")
                    {
                        // Higher friction so a crate pushed to a ledge GRIPS the
                        // edge and pivots/topples instead of frictionlessly sliding
                        // off flat. This is what makes the barrel-like gravity tip
                        // actually happen.
                        // Lower friction + Multiply combine so crates feel responsive
                        // and don't get "pegajosas" against each other (the high
                        // friction + Maximum combine made the cluster feel stuck).
                        // Tipping is now carried by CoM + solver iterations + scaled
                        // inertia, not by friction grip.
                        // Friction up slightly so stacked crates GRIP each other
                        // (helps the new stack-cohesion behavior — a column of
                        // crates moves together when impacted). Still Multiply
                        // combine so they don't get pegajosas against the floor.
                        staticFriction = 0.55f,
                        dynamicFriction = 0.5f,
                        bounciness = 0.05f,
                        frictionCombine = PhysicMaterialCombine.Multiply,
                        bounceCombine = PhysicMaterialCombine.Minimum
                    };
                }
                return _clientGripMaterial;
            }
        }
        private readonly Dictionary<ushort, NsoCacheEntry> _nsoCache = new Dictionary<ushort, NsoCacheEntry>();
        private int _nsoCacheRebuildAt;
        private Type _nsoType;
        private System.Reflection.PropertyInfo _nsoIndexProp;
        private System.Reflection.FieldInfo _nsoIndexField;

        // Current map scene tracking — see the Awake comment. Empty (menu,
        // pre-first-map) = no filtering.
        private static string _clientMapSceneName;

        // P0-14 — apply MapInfoSyncableBase positions. Cache by m_StartPos
        // Vector2 (stock SF's stable cross-machine key, quantized by P0-12).
        // Make their rigidbodies kinematic on first sight so local AddForce
        // (MoveAlongPathUsingForce) / spring integrator (PillarHandler)
        // doesn't fight the snapshot stream.
        private readonly Dictionary<Vector2, Component> _mapSyncCache = new Dictionary<Vector2, Component>();
        private readonly Dictionary<Vector2, Vector3> _mapSyncTargets = new Dictionary<Vector2, Vector3>();
        private int _mapSyncCacheRebuildAt;
        private Type _mapSyncBaseType;
        private System.Reflection.FieldInfo _mapSyncStartPosField;

        // P0-15 — recently snapshot-lerped root transforms.
        // SmoothTowardTargets writes here on every NSO lerp tick; the
        // DestructiblePiece.OnCollisionEnter prefix checks here to decide
        // whether to suppress the destruction broadcast. Dictionary is
        // pruned lazily by the prefix to avoid unbounded growth.
        private static readonly Dictionary<int, float> _recentLerpAt = new Dictionary<int, float>();
        private const float LerpSuppressWindowSec = 0.35f;
        // P0-15 — only flag NSOs whose snapshot target arrived more than
        // this far from the local position. Below this threshold the lerp
        // motion is gentle enough that the resulting OnCollisionEnter has
        // low relativeVelocity (force < threshold) anyway.
        private const float NsoLargeLerpThreshold = 0.45f;

        // Phase 6.21 — cached WeaponPickUp Type for the destruction guard.
        // Lazy-resolved on FIRST PREFIX CALL (not static-init) so we don't
        // race the Assembly-CSharp.dll load order. Static so the prefix
        // can use it without instance overhead.
        private static Type _weaponPickUpType;
        private static int _weaponSkipCount;
        // Cached (attempt-once) NSO type for the per-collision destructible filter.
        private static Type _nsoTypeForCollision;
        private static bool _nsoTypeForCollisionTried;
        private static int _destructibleGuardCallCount;

        // Crate-cull fix — cached MapSizeHandler reflection.
        private static System.Type _mapSizeHandlerType;
        private static FieldInfo _mapSizeInstanceField;
        private static FieldInfo _mapSizeField;

        // Plugin instance accessor for static Harmony postfixes.
        internal static Plugin Instance { get; private set; }

        private static Type _mmTypeForSlot;
        private static FieldInfo _mmLocalPlayerIndexField;
        private static bool _mmSlotLookupTried;
        private static FieldInfo _npHasLocalControlField;
        private static bool _npLocalCtlLookupTried;

        // Slot announcements only on CHANGE — the cache resets every map
        // change and re-deriving the same slot each round was log spam.
        private int _lastAnnouncedSlot = -2;
    }
}
