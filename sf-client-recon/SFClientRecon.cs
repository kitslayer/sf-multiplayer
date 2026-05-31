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
        public const string PluginVersion = "0.3.6";

        internal static ManualLogSource Log;
        internal static bool RefOk(object o) => !ReferenceEquals(o, null);
        // Default v26 listener port. Overridable via SFCLIENTRECON_PORT env
        // var for multi-instance same-machine testing (each instance picks a
        // different port). Server discovers the actual port from our PlayerInput
        // packet source addr — no protocol change required for non-default ports.
        private const int V26_DEFAULT_PORT = 1339;

        private int _listenPort;
        private UdpClient _socket;       // bidirectional: RX snapshots + TX inputs
        private IPEndPoint _serverEp;    // oracle's address (read from -address/-port argv)
        private Thread _rxThread;
        private volatile bool _running;

        // Phase 6.12 — outbound input sequence numbers (monotonic).
        private uint _inputSeq;
        private float _lastInputSendAt;
        private const float InputSendInterval = 1.0f / 60.0f;  // 60Hz cap
        // Phase 6.12.2 prep — latest server-acked input seq from snapshots.
        private uint _serverLastAckedSeq;
        // Ring buffer of (sequenceNum → local player position at time of send).
        // When a snapshot arrives we look up the position WE thought we were at
        // when input N was sent, compare to server's reported position, and log
        // divergence. Foundation for the future replay-rollback loop.
        private const int InputHistoryCap = 240;  // 4s at 60Hz
        private readonly Queue<uint>     _historySeq = new Queue<uint>(InputHistoryCap);
        private readonly Queue<Vector3>  _historyPos = new Queue<Vector3>(InputHistoryCap);
        private readonly Dictionary<uint, Vector3> _historyLookup = new Dictionary<uint, Vector3>(InputHistoryCap);
        private uint _divergenceLogged;

        // Pending snapshot (set on RX thread, applied on main thread).
        private readonly object _snapLock = new object();
        private List<SnapshotEntry> _pendingSnap;
        private List<NsoSnapEntry>  _pendingNsoSnap;
        private List<MapSyncSnapEntry> _pendingMapSyncSnap;
        private uint _pendingTick;
        private uint _snapsReceived;
        private uint _snapsApplied;

        // Cached local-player slot — discovered lazily once a Controller with
        // mHasControl=true appears in the scene.
        private int _localSlot = -1;

        private struct SnapshotEntry
        {
            public int Slot;
            public float X, Y, Z;
            public uint LastInputSeq;  // v26.2 — server's last-acked input seq for this slot
        }

        // P0-14 v26.5 — MapInfoSyncableBase position entry.
        // Identified by its m_StartPos Vector2 (stock SF's stable cross-
        // machine key for MapInfoSync). 20 bytes wire size.
        private struct MapSyncSnapEntry
        {
            public float StartX, StartY;
            public float X, Y, Z;
        }

        private struct NsoSnapEntry
        {
            public ushort Id;
            public float X, Y, Z, RotZ;
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;
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

            var nsoSmoothEnv = Environment.GetEnvironmentVariable("SFCLIENTRECON_NSO_SMOOTH");
            if (!string.IsNullOrEmpty(nsoSmoothEnv) && float.TryParse(nsoSmoothEnv, out var nsr) && nsr > 1f)
                _nsoSmoothRate = nsr;

            // Resolve listener port — env var override for multi-instance testing.
            _listenPort = V26_DEFAULT_PORT;
            var envPort = Environment.GetEnvironmentVariable("SFCLIENTRECON_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var pp)) _listenPort = pp;

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

            InstallClientTerrainPatches();
            InstallOracleLobbyConnectPatches();
            InstallNsoClientPushPatches();
            // InstallMapScriptLocalPatches();  // OFF — reverted to the known-good
            // state (crates working). Map-script local-drive caused regressions.
            InstallMusicCrashGuard();

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

            // Phase 6.12 — server endpoint for outbound PktPlayerInput.
            // Mirrors the patched DLL's CLI parsing: -address X -port Y.
            string serverHost = "127.0.0.1";
            int serverPort = 1337;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-address") serverHost = args[i + 1];
                else if (args[i] == "-port" && int.TryParse(args[i + 1], out var p)) serverPort = p;
            }
            try
            {
                IPAddress ip;
                if (!IPAddress.TryParse(serverHost, out ip))
                    ip = Dns.GetHostAddresses(serverHost)[0];
                _serverEp = new IPEndPoint(ip, serverPort);
                Log.LogInfo($"PlayerInput TX → {_serverEp} via :{_listenPort} (msgType 40, 60Hz).");
            }
            catch (Exception e)
            {
                Log.LogError($"Server addr parse failed: {e.Message}. PlayerInput disabled.");
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

        private void RxLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] pkt = _socket.Receive(ref ep);
                    HandlePacket(pkt);
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e) { Log.LogWarning($"RX: {e.Message}"); }
            }
        }

        private void HandlePacket(byte[] pkt)
        {
            // v25 wrapper: 5 bytes prefix + body + 9 bytes suffix
            if (pkt.Length < 14) return;
            byte msgType = pkt[4];

            // msgType 42 — server announcement banner (anti-cheat kicks etc.).
            // Body is raw UTF-8 text. Show it centered at the top for 3s.
            if (msgType == 42)
            {
                int annOff = 5;
                int annLen = pkt.Length - 14;
                if (annLen <= 0 || annLen > 512) return;
                try
                {
                    string text = System.Text.Encoding.UTF8.GetString(pkt, annOff, annLen);
                    _bannerText = text;
                    _bannerUntilUtc = DateTime.UtcNow.AddSeconds(3);
                }
                catch { }
                return;
            }

            if (msgType != 39) return;

            int bodyOff = 5;
            int bodyLen = pkt.Length - 14;
            if (bodyLen < 5) return;  // need at least tick + count

            uint tick = (uint)(pkt[bodyOff] | (pkt[bodyOff + 1] << 8) | (pkt[bodyOff + 2] << 16) | (pkt[bodyOff + 3] << 24));
            byte playerCount = pkt[bodyOff + 4];
            int o = bodyOff + 5;
            var list = new List<SnapshotEntry>(playerCount);
            int playerEntrySize = 1 + 12 + 4;  // slot + 3 floats + u32 lastInputSeq (v26.2)
            for (int i = 0; i < playerCount; i++)
            {
                if (o + playerEntrySize > bodyOff + bodyLen) break;
                list.Add(new SnapshotEntry
                {
                    Slot = pkt[o],
                    X = BitConverter.ToSingle(pkt, o + 1),
                    Y = BitConverter.ToSingle(pkt, o + 5),
                    Z = BitConverter.ToSingle(pkt, o + 9),
                    LastInputSeq = (uint)(pkt[o + 13] | (pkt[o + 14] << 8) | (pkt[o + 15] << 16) | (pkt[o + 16] << 24)),
                });
                o += playerEntrySize;
            }

            // v26.1 (Phase 6.14): NSO entries follow the player section.
            // Old servers won't have these bytes; just leave nsoList empty.
            List<NsoSnapEntry> nsoList = null;
            if (o + 2 <= bodyOff + bodyLen)
            {
                ushort nsoCount = (ushort)(pkt[o] | (pkt[o + 1] << 8));
                o += 2;
                nsoList = new List<NsoSnapEntry>(nsoCount);
                int nsoEntrySize = 2 + 16;
                for (int i = 0; i < nsoCount; i++)
                {
                    if (o + nsoEntrySize > bodyOff + bodyLen) break;
                    nsoList.Add(new NsoSnapEntry
                    {
                        Id   = (ushort)(pkt[o] | (pkt[o + 1] << 8)),
                        X    = BitConverter.ToSingle(pkt, o + 2),
                        Y    = BitConverter.ToSingle(pkt, o + 6),
                        Z    = BitConverter.ToSingle(pkt, o + 10),
                        RotZ = BitConverter.ToSingle(pkt, o + 14),
                    });
                    o += nsoEntrySize;
                }
            }

            // v26.3 (Phase 6.17): projectile entries follow the NSO section.
            // We don't yet RENDER them client-side (local raycast still draws
            // the bullet); just skip past so the offset stays aligned for any
            // future sections appended after.
            if (o + 2 <= bodyOff + bodyLen)
            {
                ushort projCount = (ushort)(pkt[o] | (pkt[o + 1] << 8));
                o += 2;
                int projEntrySize = 4 + 1 + 1 + 12;  // u32 id, u8 slot, u8 wType, 3×f32 pos
                int wanted = projCount * projEntrySize;
                if (o + wanted <= bodyOff + bodyLen) o += wanted;
                // else: malformed snapshot — silently stop here. Counters
                // already recorded via _snapsReceived in HandlePacket.
            }

            // P0-14 v26.5: MapInfoSyncableBase positions for moving platforms,
            // pressure pillars, ghost platforms. Section is optional (older
            // servers won't write it; we tolerate truncated buffer).
            // Entry: [f32 startX, f32 startY, f32 x, f32 y, f32 z] = 20 bytes.
            // startX/Y is the stock MapInfoSyncableBase.m_StartPos key.
            List<MapSyncSnapEntry> mapSyncList = null;
            if (o + 2 <= bodyOff + bodyLen)
            {
                ushort mapSyncCount = (ushort)(pkt[o] | (pkt[o + 1] << 8));
                o += 2;
                int mapSyncEntrySize = 20;
                mapSyncList = new List<MapSyncSnapEntry>(mapSyncCount);
                for (int i = 0; i < mapSyncCount; i++)
                {
                    if (o + mapSyncEntrySize > bodyOff + bodyLen) break;
                    mapSyncList.Add(new MapSyncSnapEntry
                    {
                        StartX = BitConverter.ToSingle(pkt, o),
                        StartY = BitConverter.ToSingle(pkt, o + 4),
                        X      = BitConverter.ToSingle(pkt, o + 8),
                        Y      = BitConverter.ToSingle(pkt, o + 12),
                        Z      = BitConverter.ToSingle(pkt, o + 16),
                    });
                    o += mapSyncEntrySize;
                }
            }

            List<MapStateSnapEntry> mapStateList = null;
            ParseMapStateSection(pkt, ref o, bodyOff + bodyLen, out mapStateList);

            // NB: explicit Monitor.Enter(obj)/Exit, NOT lock(){}. The C# `lock`
            // keyword compiles to Monitor.Enter(obj, ref bool), an overload that
            // does NOT exist in this game's Mono 2.0 runtime — it throws
            // MissingMethodException on every snapshot, killing position sync and
            // leaving the player frozen.
            System.Threading.Monitor.Enter(_snapLock);
            try
            {
                _pendingSnap = list;
                _pendingNsoSnap = nsoList;
                _pendingMapSyncSnap = mapSyncList;
                _pendingMapStateSnap = mapStateList;
                _pendingTick = tick;
                _snapsReceived++;
            }
            finally { System.Threading.Monitor.Exit(_snapLock); }
            // Snapshots only ever flow once we're truly connected to the oracle.
            // The lobby-connect path (BeginOracleLobbyConnect) isn't the only way
            // in (server browser / autoconnect bypass it), so use snapshot flow as
            // the universal "connected" signal that unblocks the init flag.
            _oracleConnectStarted = true;
        }

        private void Update()
        {
            if (!_running) return;
            try { TickOracleAutoConnect(); } catch { }
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
        // Reference type so SmoothTowardTargets can mutate RenderPos/Rot in place
        // while iterating the dictionary (no struct copy-back needed).
        private class PoseTarget
        {
            public Vector3 Pos;          // latest server position
            public Quaternion Rot;       // latest server rotation
            public Vector3 Vel;          // estimated velocity (u/s) from last two snapshots
            public Vector3 RenderPos;    // dead-reckoned position actually shown
            public Quaternion RenderRot;
            public float LastRecvAt;     // realtime the latest snapshot was applied
            public bool HasRender;
        }
        private readonly Dictionary<ushort, PoseTarget> _nsoTargets = new Dictionary<ushort, PoseTarget>();
        // Briefly make pushed crates non-kinematic for local collision feedback
        // (no mHasControl — server remains authoritative via v26 snapshots).
        // v0.2.1 — client NSOs stay kinematic; no _nsoClientDynamicUntil (caused ice/box chaos).
        private static Type _clientDpType;
        private static System.Reflection.FieldInfo _clientDpSimpleField;
        private static System.Reflection.FieldInfo _clientDpEventField;
        // ====================================================================
        // PUSHABLE CRATE RECONCILER (v2 — force/velocity based)
        // --------------------------------------------------------------------
        // The previous reconciler did `rb.position = Lerp(rb.position, server)`
        // on a DYNAMIC rigidbody every frame. Teleporting a dynamic body that is
        // touching another collider forces interpenetration; Unity's solver then
        // ejects both bodies at high speed on the next step → the crates
        // "explode / salen volando" the instant they touch. It also fought the
        // local push simulation, so behaviour differed per map.
        //
        // v2 keeps the nice local-physics feel (crates are dynamic, the player
        // pushes them directly, they fall + stack under real gravity + friction)
        // but reconciles toward the authoritative server position using ONLY
        // collision-respecting means:
        //   * small error  -> nothing (deadzone); local physics + grip hold it.
        //   * medium error -> a clamped, critically-damped corrective velocity
        //                     (the solver still resolves contacts, so a crate
        //                     pressed against another just leans on it — never
        //                     tunnels/explodes).
        //   * huge error   -> a single clean hard reset (respawn / round change /
        //                     teleport), with momentum zeroed so it can't fling.
        // Correction strength is scaled DOWN while the crate is essentially at
        // rest (a settled stack) so towers stay glued, and UP while it is moving
        // fast (catch a diverging crate before it drifts far). Rotation is only
        // gently nudged in Z (X/Y are frozen server-side via BOX-ROT).
        // ====================================================================
        private const float CrateDeadzone      = 0.06f;  // m: below this, leave it to local physics
        private const float CrateHardSnapDist  = 2.5f;   // m: above this, hard reset (rare)
        private const float CrateMaxCorrSpeed  = 6.0f;   // m/s cap on corrective velocity (anti-fling)
        private const float CrateStiffness     = 9.0f;   // 1/s spring gain toward server pos
        private const float CrateRestSpeed     = 0.35f;  // m/s below which a crate counts as "resting"
        private const float CrateRestCorrScale = 0.25f;  // correction multiplier while resting (hold stacks)
        private const float CrateRotCorrRate   = 6.0f;   // 1/s Z-rotation chase rate

        private void ReconcilePushableCrate(Rigidbody rb, PoseTarget pose)
        {
            // Guard: should be dynamic. (Floating/DontEnableRig crates never get
            // here — they are not flagged Pushable.)
            if (rb.isKinematic) rb.isKinematic = false;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 cur = rb.position;
            Vector3 err = pose.Pos - cur;
            float errMag = err.magnitude;

            // --- Huge divergence: clean hard reset (respawn / round change) ---
            if (errMag > CrateHardSnapDist)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = pose.Pos;
                rb.rotation = pose.Rot;
                return;
            }

            // --- Inside deadzone: trust local physics + grip; do nothing. ---
            // This is what lets a settled stack stay perfectly still instead of
            // being nudged every frame.
            if (errMag > CrateDeadzone)
            {
                // Critically-damped spring expressed as a target velocity. We
                // push the crate's velocity toward (error * stiffness), clamped,
                // so the physics solver still owns collision resolution.
                Vector3 desiredVel = err * CrateStiffness;
                float dvMag = desiredVel.magnitude;
                if (dvMag > CrateMaxCorrSpeed)
                    desiredVel *= CrateMaxCorrSpeed / dvMag;

                // Scale correction down when the crate is basically at rest so a
                // stack doesn't get jiggled by sub-deadzone server noise, and up
                // (full) when it is moving / diverging.
                float speed = rb.velocity.magnitude;
                float scale = speed < CrateRestSpeed ? CrateRestCorrScale : 1f;

                // Blend current velocity toward the corrective velocity. Using a
                // velocity change (not a teleport) means a crate jammed against a
                // neighbour simply presses on it — the solver prevents overlap, so
                // there is no ejection/explosion.
                float blend = 1f - Mathf.Exp(-CrateStiffness * scale * dt);
                Vector3 newVel = Vector3.Lerp(rb.velocity, desiredVel, blend);

                // Preserve gravity's downward pull when the crate is airborne and
                // the server isn't asking it to go UP (prevents the correction
                // from cancelling a legitimate fall into a floaty hover).
                if (err.y < 0.1f && newVel.y > rb.velocity.y)
                    newVel.y = rb.velocity.y;

                rb.velocity = newVel;
            }

            // --- Rotation: gentle Z-only chase (X/Y frozen by BOX-ROT). Done via
            // MoveRotation-equivalent slerp; rotation correction is far less
            // explosive than positional teleport and keeps crates visually
            // aligned with the server. Skipped while spinning fast (a real
            // tumble in progress). ---
            if (rb.angularVelocity.sqrMagnitude < 9f)
            {
                Quaternion target = pose.Rot;
                float rotBlend = 1f - Mathf.Exp(-CrateRotCorrRate * dt);
                Quaternion next = Quaternion.Slerp(rb.rotation, target, rotBlend);
                rb.MoveRotation(next);
            }
        }

        private void SmoothTowardTargets()
        {
            if (_playerTargets.Count == 0 && _nsoTargets.Count == 0 && _mapSyncTargets.Count == 0) return;
            float playerT = 1f - Mathf.Exp(-SmoothRate * Time.deltaTime);
            float nsoT = 1f - Mathf.Exp(-_nsoSmoothRate * Time.deltaTime);
            try
            {
                // Players: smooth every slot that has a target recorded.
                // _playerTargets is filtered at ApplySnapshot time per the
                // SFCLIENTRECON_SMOOTH_REMOTE env var — so iterating all here
                // is correct whether the user enabled remote-player smoothing
                // or not.
                if (_playerTargets.Count > 0)
                {
                    var npType = AccessTools.TypeByName("NetworkPlayer");
                    if ((object)npType != null)
                    {
                        var nps = UnityEngine.Object.FindObjectsOfType(npType);
                        var pidField = AccessTools.Field(npType, "playerID");
                        if (nps != null && (object)pidField != null)
                        {
                            foreach (var np in nps)
                            {
                                var pidObj = pidField.GetValue(np);
                                if (!(pidObj is int pi)) continue;
                                if (!_playerTargets.TryGetValue(pi, out var target)) continue;
                                var npComp = np as Component;
                                var rb = npComp.GetComponent<Rigidbody>() ?? npComp.GetComponentInChildren<Rigidbody>();
                                if ((object)rb != null) rb.position = Vector3.Lerp(rb.position, target, playerT);
                                else npComp.transform.position = Vector3.Lerp(npComp.transform.position, target, playerT);
                            }
                        }
                    }
                }

                // NSOs: smooth all entries in the target dict against cached refs.
                if (_nsoTargets.Count > 0 && _nsoCache.Count > 0)
                {
                    foreach (var kv in _nsoTargets)
                    {
                        // Unity-aware null checks (NO (object) cast): a crate/NSO
                        // destroyed on a round change leaves a stale ref in the
                        // cache. `(object)comp == null` is FALSE for a destroyed
                        // Unity object, so the old guard let it through and every
                        // subsequent member access threw — flooding [smooth] and
                        // aborting ALL smoothing each frame ("anda raro"). The
                        // Unity == overload detects destroyed objects.
                        if (!_nsoCache.TryGetValue(kv.Key, out var entry) || entry == null) continue;
                        if (entry.Comp == null) continue;
                        if (entry.SkipSmooth) continue;   // weapon / ice / chain
                        var comp = entry.Comp;
                        var rb = entry.Rb;
                        if (rb == null && comp == null) continue;
                        var pose = kv.Value;
                        if (pose == null || !pose.HasRender) continue;

                        // ===== OMEGA FIX — pushable crates: LOCAL PHYSICS + soft sync =====
                        // Keep crates DYNAMIC so local Unity physics drives them at
                        // full render framerate: instant, smooth, collides with the
                        // player + other crates, stacks/balances naturally (this is
                        // the "no lag" feel). The server stays authoritative via a
                        // GENTLE continuous correction toward its position — small
                        // enough not to fight the local sim, strong enough that the
                        // box can never drift far. Only a LARGE desync triggers a
                        // clean hard-snap, and we zero velocity on snap so the body
                        // can't explode/fly. No kinematic flips, no relay → nothing
                        // to tug-of-war with.
                        if (entry.Pushable && (object)rb != null)
                        {
                            // PURE LOCAL PHYSICS. We deliberately apply NO server
                            // position to pushable ground crates. Every previous
                            // reconciliation scheme (position teleport OR corrective
                            // velocity) injected motion the player never caused →
                            // crates exploded on contact, slid, drifted, moved on
                            // their own, or tunnelled. Letting the local Unity
                            // simulation fully own them (dynamic + grip friction +
                            // continuous collision, configured at cache build) gives
                            // exact vanilla behaviour: they only move when the
                            // player or another body pushes them, they stack, and
                            // they never self-correct. The server learns their
                            // positions from the outgoing relay (TickNsoClientPushRelay)
                            // instead of pushing positions back at us.
                            continue;
                        }

                        // ===== Non-pushable NSOs: kinematic + velocity extrapolation =====
                        // (ice debris, moving platforms, etc.) Driven purely by
                        // server snapshots; extrapolate between sparse updates and
                        // chase with an exponential render-lerp.
                        float age = Time.realtimeSinceStartup - pose.LastRecvAt;
                        if (age < 0f) age = 0f;
                        if (age > NsoMaxExtrapSec) age = NsoMaxExtrapSec;
                        Vector3 extrap = pose.Pos + pose.Vel * age;

                        float dist = Vector3.Distance(pose.RenderPos, extrap);
                        if (dist > NsoSnapDistance * 6f)
                        {
                            pose.RenderPos = extrap;
                            pose.RenderRot = pose.Rot;
                        }
                        else
                        {
                            pose.RenderPos = Vector3.Lerp(pose.RenderPos, extrap, nsoT);
                            pose.RenderRot = Quaternion.Slerp(pose.RenderRot, pose.Rot, nsoT);
                        }

                        if ((object)rb != null)
                        {
                            if (!rb.isKinematic) rb.isKinematic = true;   // P0-5 / ice regression guard
                            rb.position = pose.RenderPos;
                            rb.rotation = pose.RenderRot;
                        }
                        else
                        {
                            comp.transform.position = pose.RenderPos;
                            comp.transform.rotation = pose.RenderRot;
                        }
                        // P0-15 — moved to ApplyNsoSnapshot below; only mark
                        // recentLerpAt when a snapshot delivers a LARGE
                        // position delta (the actual "teleport-into-ice"
                        // case). Marking every frame here suppressed
                        // legitimate kick-into-ice destructions too.
                    }
                }

                // P0-14 — smooth MapInfoSyncableBase positions toward server
                // targets. Same exponential lerp as NSOs. Rigidbodies for
                // these were already made kinematic at first sight, so
                // setting transform.position here doesn't fight physics.
                if (_mapSyncTargets.Count > 0 && _mapSyncCache.Count > 0)
                {
                    foreach (var kv in _mapSyncTargets)
                    {
                        // Unity-aware null check (destroyed map objects between rounds).
                        if (!_mapSyncCache.TryGetValue(kv.Key, out var comp) || comp == null) continue;
                        var rb = comp.GetComponent<Rigidbody>();
                        if (rb != null) rb.position = Vector3.Lerp(rb.position, kv.Value, nsoT);
                        else comp.transform.position = Vector3.Lerp(comp.transform.position, kv.Value, nsoT);
                    }
                }
            }
            catch (Exception ex)
            {
                // Throttle: a per-frame throw here used to flood the log (which
                // itself tanks FPS) and gave no type. Log type+message once/sec.
                if (Time.realtimeSinceStartup - _lastSmoothErrAt > 1f)
                {
                    _lastSmoothErrAt = Time.realtimeSinceStartup;
                    Log.LogWarning($"[P6.11.2 smooth] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Phase 6.14 — apply server-authoritative NSO positions (boxes,
        // chains, ice debris). For now: snap (no smoothing). 6.14.1 will
        // lerp between snapshots since broadcast rate is 30Hz but client
        // Update is 60-144Hz.
        //
        // Maintain a cached id → NetworkSyncableObject map. Rebuild on miss
        // since scene changes invalidate entries.
        // Cache entry precomputes the per-NSO classification + rigidbody ONCE at
        // rebuild time. Previously SmoothTowardTargets/ApplyNsoSnapshot called
        // IsChainStyle/IsIceOnly/IsWeapon (each a GetComponentsInChildren) plus
        // GetComponent<Rigidbody> for every box every frame — on box-heavy maps
        // that was thousands of hierarchy walks/sec → frame drops scaling with
        // crate count. Now it's a single dictionary lookup per frame.
        private class NsoCacheEntry { public Component Comp; public Rigidbody Rb; public bool SkipSmooth; public bool Pushable; public bool Floating; }
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
                        staticFriction = 0.4f,
                        dynamicFriction = 0.4f,
                        bounciness = 0f,
                        frictionCombine = PhysicMaterialCombine.Average,
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

        private void ApplyNsoSnapshot(List<NsoSnapEntry> snap, uint tick)
        {
            if (snap.Count == 0) return;
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                // Rebuild cache every 60 ticks (~2s at 30Hz) or on first run.
                if (_nsoCache.Count == 0 || _nsoCacheRebuildAt <= 0)
                {
                    _nsoCache.Clear();
                    var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                    if (all != null)
                    {
                        foreach (var nso in all)
                        {
                            ushort id = 0;
                            if ((object)_nsoIndexProp != null)
                                id = (ushort)_nsoIndexProp.GetValue(nso, null);
                            else if ((object)_nsoIndexField != null)
                                id = (ushort)_nsoIndexField.GetValue(nso);
                            var c = nso as Component;
                            if ((object)c == null) continue;
                            var go = c.gameObject;
                            bool skip = IsWeaponNsoRootClient(go)
                                     || IsIceOnlyDestructibleRoot(go)
                                     || IsChainStyleDestructibleRoot(go);
                            bool pushable = !skip && IsPushableCrateNsoClient(go);
                            var rbc = c.GetComponent<Rigidbody>();
                            // Floating crates (DontEnableRig) are intentionally
                            // suspended in the map. They must NOT run local physics
                            // (they'd fall "porque sí"); they only move if the game
                            // activates them, in which case the server tells us via
                            // snapshot. Treat them as kinematic-follow.
                            if ((object)_dontEnableRigType == null)
                                _dontEnableRigType = AccessTools.TypeByName("DontEnableRig");
                            bool floating = (object)_dontEnableRigType != null
                                && (object)c.GetComponentInChildren(_dontEnableRigType, true) != null;
                            // NOTE: crate physics (grip, drag, spin cap, continuous
                            // collision) is configured EARLY at map-load in
                            // DisableAllRigidBodies_PushPrefix → ConfigureCratePhysics,
                            // NOT here. Doing it at cache-build time (first fires
                            // ~3s in) re-touched already-settled crates and jolted
                            // them ("a los 3 segundos explotan/desaparecen"). The
                            // cache build now only records flags/refs.
                            _nsoCache[id] = new NsoCacheEntry
                            {
                                Comp = c,
                                Rb = rbc,
                                SkipSmooth = skip,
                                Pushable = pushable && !floating,
                                Floating = floating
                            };
                        }
                    }
                    _nsoCacheRebuildAt = 60;
                }
                _nsoCacheRebuildAt--;

                int applied = 0;
                float nowTs = Time.realtimeSinceStartup;
                foreach (var e in snap)
                {
                    if (!_nsoCache.TryGetValue(e.Id, out var nsoEntry) || nsoEntry == null || (object)nsoEntry.Comp == null) continue;
                    var nsoComp = nsoEntry.Comp;
                    if (nsoEntry.SkipSmooth) continue;   // weapon / ice / chain — handled elsewhere
                    Vector3 newTarget = new Vector3(e.X, e.Y, e.Z);
                    // P0-15 — only flag for destruction-suppression when the
                    // snapshot delivered a LARGE position jump. The
                    // exponential lerp covers the jump over the next few
                    // frames; during those frames the body sweeps through
                    // adjacent geometry. Threshold (0.3u) is large enough to
                    // not catch normal active-push deltas (which arrive in
                    // ~0.05u increments between 30Hz snapshots) but small
                    // enough to catch the "teleport across map after a
                    // missed snapshot or scene reload" case that produces
                    // spurious destructions.
                    Vector3 currentPos = ((object)nsoEntry.Rb != null)
                        ? nsoEntry.Rb.position : nsoComp.transform.position;
                    if (Vector3.Distance(currentPos, newTarget) > NsoLargeLerpThreshold)
                    {
                        var rootT = nsoComp.transform.root;
                        if ((object)rootT != null) _recentLerpAt[rootT.GetInstanceID()] = nowTs;
                    }
                    Quaternion newRot = Quaternion.Euler(0f, 0f, e.RotZ);
                    float nowRt = Time.realtimeSinceStartup;
                    PoseTarget pt;
                    if (_nsoTargets.TryGetValue(e.Id, out pt) && pt != null && pt.HasRender)
                    {
                        float dtv = nowRt - pt.LastRecvAt;
                        // Estimate velocity from consecutive snapshots so we can
                        // extrapolate between them (server delivers ~30Hz or less;
                        // this keeps boxes moving smoothly at render framerate).
                        if (dtv > 0.0001f && dtv < 0.5f)
                            pt.Vel = (newTarget - pt.Pos) / dtv;
                        else
                            pt.Vel = Vector3.zero;
                        // Big jump = teleport / respawn: drop extrapolation history.
                        if (Vector3.Distance(pt.RenderPos, newTarget) > 3f)
                        {
                            pt.RenderPos = newTarget;
                            pt.RenderRot = newRot;
                            pt.Vel = Vector3.zero;
                        }
                        pt.Pos = newTarget;
                        pt.Rot = newRot;
                        pt.LastRecvAt = nowRt;
                    }
                    else
                    {
                        pt = new PoseTarget
                        {
                            Pos = newTarget, Rot = newRot, Vel = Vector3.zero,
                            RenderPos = newTarget, RenderRot = newRot,
                            LastRecvAt = nowRt, HasRender = true
                        };
                        _nsoTargets[e.Id] = pt;
                    }
                    applied++;
                }
                if (_snapsApplied == 1 || _snapsApplied % 90 == 0)
                    Log.LogInfo($"[P6.14] NSO snap tick={tick} targeted {applied}/{snap.Count}");
            }
            catch (Exception ex) { Log.LogWarning($"[P6.14 NSO apply] {ex.Message}"); }
        }

        private static bool IsWeaponNsoRootClient(GameObject root)
        {
            if ((object)root == null) return false;
            if ((object)_weaponPickUpTypeClient == null)
            {
                try { _weaponPickUpTypeClient = AccessTools.TypeByName("WeaponPickUp"); } catch { }
            }
            return (object)_weaponPickUpTypeClient != null
                && root.GetComponentInChildren(_weaponPickUpTypeClient, true) != null;
        }

        private static bool IsIceOnlyDestructibleRoot(GameObject root)
        {
            if ((object)_clientDpType == null)
            {
                _clientDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_clientDpType != null)
                {
                    _clientDpSimpleField = AccessTools.Field(_clientDpType, "simpleDestruction");
                    _clientDpEventField = AccessTools.Field(_clientDpType, "eventDestruction");
                }
            }
            if ((object)_clientDpType == null) return false;
            var dps = root.GetComponentsInChildren(_clientDpType);
            if (dps == null || dps.Length == 0) return false;
            bool any = false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                any = true;
                bool simple = (object)_clientDpSimpleField != null && (bool)_clientDpSimpleField.GetValue(dp);
                bool ev = (object)_clientDpEventField != null && (bool)_clientDpEventField.GetValue(dp);
                if (!simple || ev) return false;
            }
            return any;
        }

        private static bool IsChainStyleDestructibleRoot(GameObject root)
        {
            if ((object)_clientDpType == null)
            {
                _clientDpType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_clientDpType != null)
                {
                    _clientDpSimpleField = AccessTools.Field(_clientDpType, "simpleDestruction");
                    _clientDpEventField = AccessTools.Field(_clientDpType, "eventDestruction");
                }
            }
            if ((object)_clientDpType == null) return false;
            var dps = root.GetComponentsInChildren(_clientDpType);
            if (dps == null || dps.Length == 0) return false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_clientDpSimpleField != null && (bool)_clientDpSimpleField.GetValue(dp);
                bool ev = (object)_clientDpEventField != null && (bool)_clientDpEventField.GetValue(dp);
                if (!simple && !ev) return true;
            }
            return false;
        }

        // Bug F fix: mirror server's IsPushableCrateNso. Pushable crates need
        // to stay dynamic so SfNsoClientPush.RelayPushableCrateUpdates can
        // relay local push positions back to the server. Forcing kinematic
        // here (the old behavior) killed the relay (it skips when isKinematic)
        // and made boxes feel laggy/ghost-through-players.
        private static bool IsPushableCrateNsoClient(GameObject root)
        {
            if ((object)root == null) return false;
            if (IsChainStyleDestructibleRoot(root) || IsWeaponNsoRootClient(root)) return false;
            if ((object)_clientDpType == null) return false;
            var dps = root.GetComponentsInChildren(_clientDpType);
            if (dps == null || dps.Length == 0) return false;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_clientDpSimpleField != null && (bool)_clientDpSimpleField.GetValue(dp);
                bool ev = (object)_clientDpEventField != null && (bool)_clientDpEventField.GetValue(dp);
                if (simple && !ev) return true;
            }
            return false;
        }

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
        private void ApplyMapSyncSnapshot(List<MapSyncSnapEntry> snap, uint tick)
        {
            // DISABLED (P0-14 client position sync). Moving platforms / pillars /
            // animated map pieces run their OWN deterministic local scripts
            // (MoveAlongPathUsingForce, PillarHandler spring, Animator). We used to
            // force them kinematic and lerp their position from the 30Hz server
            // stream, which KILLED the local script and replaced smooth motion with
            // a laggy/jittery network follow → "los mapas con algo que se mueve se
            // bugean / se mueven raro al empezar". Letting the local script run
            // (vanilla behaviour) is correct and smooth. mapState/event sync
            // (visibility, on/off) still flows via ApplyMapStateSnapshot.
            return;
        }

        // Phase 6.12 — pack and send a PktPlayerInput packet to the oracle.
        // First cut: read raw keyboard state via UnityEngine.Input. Phase
        // 6.12.1 will read from the SF Controller's CharacterActions so we
        // catch gamepad input too and match exactly what the patched DLL's
        // Movement.cs is reading for local prediction.
        private void SendPlayerInputPacket()
        {
            int localSlot = FindLocalSlot();
            if (localSlot < 0) return;

            float sx = 0f, sy = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  sx -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) sx += 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    sy += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  sy -= 1f;
            float ax = 0f, ay = 0f;  // aim — placeholder until we read mouse properly
            uint btns = 0;
            if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.W))      btns |= 1u << 0;  // jump
            if (Input.GetMouseButton(0))                                     btns |= 1u << 1;  // fire
            if (Input.GetMouseButton(1) || Input.GetKey(KeyCode.LeftShift))  btns |= 1u << 2;  // block
            if (Input.GetKey(KeyCode.Q))                                     btns |= 1u << 3;  // throw

            // Body: 25 bytes  (u32 seq + u8 slot + 4 floats + u32 buttons)
            byte[] body = new byte[25];
            _inputSeq++;
            WriteU32LE(body, 0, _inputSeq);
            body[4] = (byte)localSlot;
            WriteF32LE(body, 5,  sx);
            WriteF32LE(body, 9,  sy);
            WriteF32LE(body, 13, ax);
            WriteF32LE(body, 17, ay);
            WriteU32LE(body, 21, btns);

            // v25 envelope wrap.
            int totalLen = 5 + body.Length + 9;
            byte[] pkt = new byte[totalLen];
            uint ts = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            WriteU32LE(pkt, 0, ts);
            pkt[4] = 40;  // PktPlayerInput
            Buffer.BlockCopy(body, 0, pkt, 5, body.Length);
            // tail: u64 steamID (zero — server identifies by slot byte) + u8 channel
            pkt[pkt.Length - 1] = 0;

            try { _socket.Send(pkt, pkt.Length, _serverEp); }
            catch (Exception e) { Log.LogWarning($"TX: {e.Message}"); }

            // Phase 6.12.2 — snapshot the local player's position at the time
            // of this send, keyed by sequenceNum. Server replies with this
            // seq + the position IT thinks we were at, and we diff them in
            // ApplySnapshot above. Ring-buffer size capped to ~4s of inputs.
            try
            {
                var npType = AccessTools.TypeByName("NetworkPlayer");
                if ((object)npType != null)
                {
                    var nps = UnityEngine.Object.FindObjectsOfType(npType);
                    var pidField = AccessTools.Field(npType, "playerID");
                    if (nps != null && (object)pidField != null)
                    {
                        foreach (var np in nps)
                        {
                            var pidObj = pidField.GetValue(np);
                            if (!(pidObj is int pi) || pi != localSlot) continue;
                            var npComp = np as Component;
                            if ((object)npComp == null) break;
                            Vector3 currentPos = npComp.transform.position;
                            _historySeq.Enqueue(_inputSeq);
                            _historyPos.Enqueue(currentPos);
                            _historyLookup[_inputSeq] = currentPos;
                            while (_historySeq.Count > InputHistoryCap)
                            {
                                uint dropSeq = _historySeq.Dequeue();
                                _historyPos.Dequeue();
                                _historyLookup.Remove(dropSeq);
                            }
                            break;
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            if (_inputSeq == 1 || _inputSeq % 300 == 0)
                Log.LogInfo($"[P6.12] Sent PlayerInput #{_inputSeq} slot={localSlot} stick=({sx:0.00},{sy:0.00}) btns=0x{btns:X} hist={_historySeq.Count}");
        }

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

        // Harmony prefix on DestructiblePiece.OnCollisionEnter — returns false
        // (skip stock) in two cases:
        //
        // (1) [P0-15] when the colliding rigidbody's root was snapshot-lerped
        // in the last ~150ms. Prevents the swept-lerp-into-ice-block path.
        //
        // (2) [Phase 6.21] when the colliding rigidbody is a WeaponPickUp.
        // Server-spawned shooting guns (heavy mass) fall onto chains/ice and
        // the mass>300 multiplier (10x) or mass>1000 multiplier (50x) pushes
        // the collision force past the destruction threshold → spurious
        // chain/ice break across all clients. Vanilla SF doesn't see this
        // because the host's authoritative ObjectUpdate stream pulls weapons
        // out of free-fall before they hit anything; our oracle's update
        // stream has different timing. Cheapest fix: don't let weapons
        // ever drive destruction. Player hits, throws, kicks still work
        // (those go through the player's rigid body, not the weapon's).
        internal static bool DestructibleCollisionPrefix(MonoBehaviour __instance, Collision collision)
        {
            try
            {
                if ((object)collision == null) return true;
                var rb = collision.rigidbody;
                if ((object)rb == null) return true;
                var rootT = rb.transform.root;
                if ((object)rootT == null) return true;

                // Ice only breaks from LOCAL player rig — not from boxes/lerped NSOs/weapons.
                if (IsIceDestructibleTarget(__instance))
                {
                    if (!IsLocalPlayerCollisionRigidbody(rb))
                        return false;
                }

                // (2) — skip if the colliding body's root has a WeaponPickUp
                // anywhere in its hierarchy. WeaponPickUp lives on the
                // weapon prefab's root in stock SF.
                if ((object)_weaponPickUpType == null)
                {
                    try { _weaponPickUpType = AccessTools.TypeByName("WeaponPickUp"); } catch { }
                }
                if ((object)_weaponPickUpType != null && rootT.GetComponentInChildren(_weaponPickUpType, true) != null)
                {
                    _weaponSkipCount++;
                    if (_weaponSkipCount == 1 || _weaponSkipCount % 20 == 0)
                        Log.LogInfo($"[Phase 6.21] Suppressed destruction from WeaponPickUp collision (#{_weaponSkipCount}) on '{__instance?.name}'");
                    return false;
                }

                // Non-ice destructibles: block NSO/chain roots that aren't the local player.
                if (!IsIceDestructibleTarget(__instance))
                {
                    if (IsChainStyleDestructibleRoot(rootT.gameObject)
                        || IsWeaponNsoRootClient(rootT.gameObject))
                        return false;
                    if (!IsLocalPlayerCollisionRigidbody(rb)
                        && (IsIceOnlyDestructibleRoot(rootT.gameObject)
                            || rootT.GetComponent(AccessTools.TypeByName("NetworkSyncableObject")) != null))
                        return false;
                }

                float now = Time.realtimeSinceStartup;
                int id = rootT.GetInstanceID();
                if (_recentLerpAt.TryGetValue(id, out float lastLerp))
                {
                    if (now - lastLerp < LerpSuppressWindowSec)
                    {
                        // Recently teleported by snapshot apply — don't let
                        // stock code interpret this as a player-driven impact.
                        return false; // skip stock OnCollisionEnter
                    }
                }

                // Opportunistic cleanup: prune stale entries every ~100 calls.
                if ((++_destructibleGuardCallCount % 100) == 0)
                {
                    var staleKeys = new List<int>();
                    foreach (var kv in _recentLerpAt)
                        if (now - kv.Value > 2.0f) staleKeys.Add(kv.Key);
                    foreach (var k in staleKeys) _recentLerpAt.Remove(k);
                }
            }
            catch { /* let stock run on any error */ }
            return true;
        }
        private static int _destructibleGuardCallCount;

        // Crate-cull fix — cached MapSizeHandler reflection.
        private static System.Type _mapSizeHandlerType;
        private static FieldInfo _mapSizeInstanceField;
        private static FieldInfo _mapSizeField;

        // ===== Music crash guard =====
        // SF's MusicHandler.PlayNext() calls AudioSource.Play(), which crashes
        // natively in this Unity 5.6 build when it streams the next track
        // (observed: hard crash, stack MusicHandler.Update→PlayNext→AudioSource.Play).
        // We neutralize the music system so the game can't crash there. Music is
        // cosmetic; stability wins.
        private void InstallMusicCrashGuard()
        {
            try
            {
                var mhType = AccessTools.TypeByName("MusicHandler");
                if ((object)mhType == null) { Log.LogInfo("[music-guard] MusicHandler not found — skip."); return; }
                var harmony = new Harmony(PluginGuid + ".music-guard");
                int n = 0;
                var playNext = AccessTools.Method(mhType, "PlayNext");
                if ((object)playNext != null)
                {
                    harmony.Patch(playNext, prefix: new HarmonyMethod(typeof(Plugin), nameof(MusicHandler_Skip_Prefix)));
                    n++;
                }
                var tryStart = AccessTools.Method(mhType, "TryStartMusic");
                if ((object)tryStart != null)
                {
                    harmony.Patch(tryStart, prefix: new HarmonyMethod(typeof(Plugin), nameof(MusicHandler_Skip_Prefix)));
                    n++;
                }
                Log.LogInfo($"[music-guard] Disabled MusicHandler audio ({n} method(s)) to prevent native AudioSource.Play crash.");
            }
            catch (Exception e) { Log.LogWarning($"[music-guard] install failed: {e.Message}"); }
        }

        // Returns false → original method body is skipped entirely.
        internal static bool MusicHandler_Skip_Prefix() { return false; }

        // Returns the y-threshold below which IgnorePlayerWhenOffScreen flips a
        // crate to the no-collision layer. Stock value is -11f for a 10-unit
        // map; scale it so larger maps don't cull in-bounds crates.
        public static float GetCrateCullThreshold()
        {
            try
            {
                if ((object)_mapSizeHandlerType == null)
                {
                    _mapSizeHandlerType = AccessTools.TypeByName("MapSizeHandler");
                    if ((object)_mapSizeHandlerType != null)
                    {
                        _mapSizeInstanceField = AccessTools.Field(_mapSizeHandlerType, "Instance");
                        _mapSizeField = AccessTools.Field(_mapSizeHandlerType, "mapSize");
                    }
                }
                if ((object)_mapSizeInstanceField != null && (object)_mapSizeField != null)
                {
                    var inst = _mapSizeInstanceField.GetValue(null);
                    if (inst != null)
                    {
                        float size = (float)_mapSizeField.GetValue(inst);
                        if (size > 0.01f) return -11f * (size / 10f);
                    }
                }
            }
            catch { /* fall through to stock value */ }
            return -11f;
        }

        private static IEnumerable<CodeInstruction> IgnoreOffScreenCullTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            // NOTE: do NOT use `yield return` here. The C# compiler turns iterator
            // methods into a state machine decorated with IteratorStateMachineAttribute,
            // which Mono 2.0 (this SF build) cannot load → TypeLoadException at plugin
            // load. Build a concrete List and return it instead.
            var call = AccessTools.Method(typeof(Plugin), nameof(GetCrateCullThreshold));
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == System.Reflection.Emit.OpCodes.Ldc_R4 && (float)codes[i].operand == -11f)
                {
                    codes[i].opcode = System.Reflection.Emit.OpCodes.Call;
                    codes[i].operand = call;
                }
            }
            return codes;
        }

        // Phase 6.17 v0.1 — Harmony postfix on Weapon.ActuallyShoot.
        // Fires after the local Shoot ran. We capture the muzzle position +
        // forward direction from the Weapon instance and send a
        // PktClientFireWeapon (msgType 41) to the oracle. Server simulates
        // the projectile + broadcasts to all clients in WorldStateSnapshot.
        //
        // Only sends for the LOCAL player's weapon (HasControl=true on the
        // Controller holding this weapon) — remote players' Shoot postfix
        // also fires when their player rig replays the action, and we don't
        // want to double-emit.
        private static void WeaponShootPostfix(object __instance,
            bool networkForce,
            Vector3 shootVectorOverride,
            Vector3 shootPositionOverride)
        {
            try
            {
                if (Instance == null || Instance._socket == null || Instance._serverEp == null) return;

                var weaponComp = __instance as Component;
                if ((object)weaponComp == null) return;

                // Find owning Controller — Weapon is a child of the player rig.
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType == null) return;
                var ctrl = weaponComp.GetComponentInParent(ctrlType);
                if ((object)ctrl == null) return;
                var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
                if ((object)hasCtrlF != null && !(bool)hasCtrlF.GetValue(ctrl)) return;  // not the local player
                var pidF = AccessTools.Field(ctrlType, "playerID");
                byte slot = 0;
                if ((object)pidF != null) slot = (byte)(int)pidF.GetValue(ctrl);

                // Origin = shootPositionOverride if set, else weapon's shootPosition.position
                Vector3 origin = networkForce ? shootPositionOverride : weaponComp.transform.position;
                Vector3 dir    = networkForce ? shootVectorOverride   : weaponComp.transform.forward;
                if (dir.sqrMagnitude < 0.001f) return;
                dir.Normalize();

                Instance.SendFireWeaponPacket(slot, 0 /*weaponType placeholder*/, origin, dir, 0f);
            }
            catch (Exception e) { Log.LogWarning($"WeaponShootPostfix: {e.Message}"); }
        }

        private void SendFireWeaponPacket(byte slot, byte weaponType, Vector3 origin, Vector3 dir, float speed)
        {
            // Body 30 bytes: u8 slot, u8 wType, 3×f32 origin, 3×f32 dir, f32 speed
            byte[] body = new byte[30];
            body[0] = slot;
            body[1] = weaponType;
            WriteF32LE(body, 2,  origin.x);
            WriteF32LE(body, 6,  origin.y);
            WriteF32LE(body, 10, origin.z);
            WriteF32LE(body, 14, dir.x);
            WriteF32LE(body, 18, dir.y);
            WriteF32LE(body, 22, dir.z);
            WriteF32LE(body, 26, speed);

            int totalLen = 5 + body.Length + 9;
            byte[] pkt = new byte[totalLen];
            uint ts = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            WriteU32LE(pkt, 0, ts);
            pkt[4] = 41;  // PktClientFireWeapon
            Buffer.BlockCopy(body, 0, pkt, 5, body.Length);
            try { _socket.Send(pkt, pkt.Length, _serverEp); }
            catch (Exception e) { Log.LogWarning($"SendFireWeaponPacket: {e.Message}"); }
            Log.LogInfo($"[P6.17] Sent FireWeapon slot={slot} w={weaponType} origin={origin} dir={dir}");
        }

        // Plugin instance accessor for static Harmony postfixes.
        internal static Plugin Instance { get; private set; }

        private static void WriteU32LE(byte[] b, int o, uint v)
        {
            b[o    ] = (byte)(v       & 0xFF);
            b[o + 1] = (byte)(v >>  8 & 0xFF);
            b[o + 2] = (byte)(v >> 16 & 0xFF);
            b[o + 3] = (byte)(v >> 24 & 0xFF);
        }
        private static void WriteF32LE(byte[] b, int o, float v)
        {
            var bytes = BitConverter.GetBytes(v);
            b[o    ] = bytes[0];
            b[o + 1] = bytes[1];
            b[o + 2] = bytes[2];
            b[o + 3] = bytes[3];
        }

        private void ApplySnapshot(List<SnapshotEntry> snap, uint tick)
        {
            try
            {
                int localSlot = FindLocalSlot();
                if (localSlot < 0) return;

                var npType = AccessTools.TypeByName("NetworkPlayer");
                if ((object)npType == null) return;
                var nps = UnityEngine.Object.FindObjectsOfType(npType);
                if (nps == null) return;
                var pidField = AccessTools.Field(npType, "playerID");
                if ((object)pidField == null) return;

                bool smoothRemote = Environment.GetEnvironmentVariable("SFCLIENTRECON_SMOOTH_REMOTE") == "1";
                foreach (var entry in snap)
                {
                    bool isLocal = entry.Slot == localSlot;
                    var target = new Vector3(entry.X, entry.Y, entry.Z);

                    // Phase 6.12.2 v1.0 — SHIFT correction for the local
                    // player. Instead of "lerp / snap to the stale server
                    // position" (which pulls the predicting player visually
                    // BACKWARD by ~RTT-equivalent distance and feels awful),
                    // compute the drift between server's view at sequence N
                    // and local's predicted position at sequence N, then
                    // apply that drift as an OFFSET to the player's CURRENT
                    // position. Mathematically: new_pos = current_local +
                    // (server_at_N - predicted_at_N). This is the canonical
                    // CSGO / Valorant / Overwatch correction — the server's
                    // adjustment is incorporated WITHOUT the visual snap-back.
                    // Below SoftDriftThresholdSq → ignore (natural RTT jitter).
                    if (isLocal)
                    {
                        _serverLastAckedSeq = entry.LastInputSeq;
                        if (_historyLookup.TryGetValue(entry.LastInputSeq, out var predictedAtSeq))
                        {
                            Vector3 driftVec = target - predictedAtSeq;
                            float driftSq = driftVec.sqrMagnitude;
                            const float SoftDriftThresholdSq = 0.3f * 0.3f; // ignore below 0.3u
                            const float HardDriftThresholdSq = 2.5f * 2.5f; // log loud above 2.5u
                            if (driftSq > SoftDriftThresholdSq)
                            {
                                foreach (var np2 in nps)
                                {
                                    var pidObj2 = pidField.GetValue(np2);
                                    if (!(pidObj2 is int pi2) || pi2 != localSlot) continue;
                                    var npComp2 = np2 as Component;
                                    var rb2 = npComp2.GetComponent<Rigidbody>() ?? npComp2.GetComponentInChildren<Rigidbody>();
                                    if ((object)rb2 != null)
                                    {
                                        Vector3 newPos = rb2.position + driftVec;
                                        rb2.position = newPos;
                                        // Velocity is preserved — local
                                        // prediction continues unchanged.
                                    }
                                    else
                                    {
                                        npComp2.transform.position += driftVec;
                                    }
                                    // Re-stamp our history with the corrected
                                    // position so subsequent comparisons are
                                    // against the post-correction baseline,
                                    // not the pre-correction prediction.
                                    _historyLookup[entry.LastInputSeq] = target;
                                    break;
                                }
                                _divergenceLogged++;
                                if (driftSq > HardDriftThresholdSq
                                    && (_divergenceLogged == 1 || _divergenceLogged % 15 == 0))
                                {
                                    Log.LogWarning($"[P6.12.2 v1.0 SHIFT] seq={entry.LastInputSeq} drift={Mathf.Sqrt(driftSq):0.00}u — applied as offset, current local pos shifted");
                                }
                                else if (_divergenceLogged == 1 || _divergenceLogged % 60 == 0)
                                {
                                    Log.LogInfo($"[P6.12.2 v1.0 shift] seq={entry.LastInputSeq} drift={Mathf.Sqrt(driftSq):0.00}u corrected #{_divergenceLogged}");
                                }
                            }
                        }
                        else
                        {
                            // No history for this seq (just connected, or
                            // buffer wrapped). Fall back to absolute snap
                            // so the player can't be permanently lost.
                            foreach (var np2 in nps)
                            {
                                var pidObj2 = pidField.GetValue(np2);
                                if (!(pidObj2 is int pi2) || pi2 != localSlot) continue;
                                var npComp2 = np2 as Component;
                                var rb2 = npComp2.GetComponent<Rigidbody>() ?? npComp2.GetComponentInChildren<Rigidbody>();
                                if ((object)rb2 != null) { rb2.position = target; rb2.velocity = Vector3.zero; }
                                else npComp2.transform.position = target;
                                break;
                            }
                        }
                    }
                    else
                    {
                        // REMOTE player. Opt-in smoothing via env var; default
                        // is to let stock forwarded PlayerUpdate (msgType 10)
                        // drive remote positions.
                        if (smoothRemote) _playerTargets[entry.Slot] = target;
                    }
                }
                _snapsApplied++;
                if (_snapsApplied == 1 || _snapsApplied % 90 == 0)
                    Log.LogInfo($"[P6.11] Applied snapshot tick={tick} localSlot={localSlot} (received={_snapsReceived}, applied={_snapsApplied}).");
            }
            catch (Exception e) { Log.LogWarning($"[P6.11 apply] {e.Message}"); }
        }

        private int FindLocalSlot()
        {
            if (_localSlot >= 0) return _localSlot;
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType != null)
                {
                    var ctrls = UnityEngine.Object.FindObjectsOfType(ctrlType);
                    var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
                    var pidF = AccessTools.Field(ctrlType, "playerID");
                    if (ctrls != null && (object)hasCtrlF != null && (object)pidF != null)
                    {
                        foreach (var c in ctrls)
                        {
                            if (!(bool)hasCtrlF.GetValue(c)) continue;
                            _localSlot = (int)pidF.GetValue(c);
                            Log.LogInfo($"[P6.11] Discovered localSlot={_localSlot} (Controller).");
                            return _localSlot;
                        }
                    }
                }
                var npType = AccessTools.TypeByName("NetworkPlayer");
                if ((object)npType != null)
                {
                    var pidField = AccessTools.Field(npType, "playerID");
                    var hasCtrlNp = AccessTools.Field(npType, "mHasControl");
                    foreach (var np in UnityEngine.Object.FindObjectsOfType(npType))
                    {
                        if ((object)hasCtrlNp != null && !(bool)hasCtrlNp.GetValue(np)) continue;
                        if ((object)pidField != null)
                        {
                            _localSlot = (int)pidField.GetValue(np);
                            Log.LogInfo($"[P6.11] Discovered localSlot={_localSlot} (NetworkPlayer).");
                            return _localSlot;
                        }
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"FindLocalSlot: {e.Message}"); }
            return -1;
        }
    }
}
