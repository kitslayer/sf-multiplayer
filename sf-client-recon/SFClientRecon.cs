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
    // Phase 6.11 — client-side reconciliation.
    //
    // Listens on UDP port 1339 for v26 WorldStateSnapshot packets (msgType 39)
    // sent by the oracle (sf-headless-host), and snap-corrects the local
    // NetworkPlayer's position to the server's authoritative view.
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
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.client-recon";
        public const string PluginName = "SFClientRecon";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
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

            lock (_snapLock)
            {
                _pendingSnap = list;
                _pendingNsoSnap = nsoList;
                _pendingTick = tick;
                _snapsReceived++;
            }
        }

        private void Update()
        {
            if (!_running) return;
            List<SnapshotEntry> snap;
            List<NsoSnapEntry>  nsoSnap;
            uint tick;
            lock (_snapLock)
            {
                snap    = _pendingSnap;
                nsoSnap = _pendingNsoSnap;
                tick    = _pendingTick;
                _pendingSnap    = null;
                _pendingNsoSnap = null;
            }
            if (snap != null)    ApplySnapshot(snap, tick);
            if (nsoSnap != null) ApplyNsoSnapshot(nsoSnap, tick);

            // Phase 6.11.2 — between snapshots, exponentially lerp current
            // positions toward latest targets so the visual feel is smooth
            // instead of teleporting every 33ms (30Hz snapshot rate).
            SmoothTowardTargets();

            // Phase 6.12 — send input packets at 60Hz once local slot is known.
            if (_socket != null && _serverEp != null && Time.realtimeSinceStartup - _lastInputSendAt >= InputSendInterval)
            {
                _lastInputSendAt = Time.realtimeSinceStartup;
                SendPlayerInputPacket();
            }
        }

        // Smoothing targets — apply each frame in Update via exponential lerp.
        // Higher SmoothRate = snappier (less lag, more jitter).
        // Lower = smoother (more lag, less jitter). 15/s is a reasonable middle
        // ground at 30Hz snapshot rate (settles ~95% of error in 200ms).
        private const float SmoothRate = 15f;
        private readonly Dictionary<int, Vector3> _playerTargets = new Dictionary<int, Vector3>();
        private struct PoseTarget { public Vector3 Pos; public Quaternion Rot; }
        private readonly Dictionary<ushort, PoseTarget> _nsoTargets = new Dictionary<ushort, PoseTarget>();
        private void SmoothTowardTargets()
        {
            if (_playerTargets.Count == 0 && _nsoTargets.Count == 0) return;
            float t = 1f - Mathf.Exp(-SmoothRate * Time.deltaTime);
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
                                if ((object)rb != null) rb.position = Vector3.Lerp(rb.position, target, t);
                                else npComp.transform.position = Vector3.Lerp(npComp.transform.position, target, t);
                            }
                        }
                    }
                }

                // NSOs: smooth all entries in the target dict against cached refs.
                if (_nsoTargets.Count > 0 && _nsoCache.Count > 0)
                {
                    foreach (var kv in _nsoTargets)
                    {
                        if (!_nsoCache.TryGetValue(kv.Key, out var comp) || (object)comp == null) continue;
                        var rb = comp.GetComponent<Rigidbody>();
                        if ((object)rb != null)
                        {
                            rb.position = Vector3.Lerp(rb.position, kv.Value.Pos, t);
                            rb.rotation = Quaternion.Slerp(rb.rotation, kv.Value.Rot, t);
                        }
                        else
                        {
                            comp.transform.position = Vector3.Lerp(comp.transform.position, kv.Value.Pos, t);
                            comp.transform.rotation = Quaternion.Slerp(comp.transform.rotation, kv.Value.Rot, t);
                        }
                    }
                }
            }
            catch (Exception ex) { Log.LogWarning($"[P6.11.2 smooth] {ex.Message}"); }
        }

        // Phase 6.14 — apply server-authoritative NSO positions (boxes,
        // chains, ice debris). For now: snap (no smoothing). 6.14.1 will
        // lerp between snapshots since broadcast rate is 30Hz but client
        // Update is 60-144Hz.
        //
        // Maintain a cached id → NetworkSyncableObject map. Rebuild on miss
        // since scene changes invalidate entries.
        private readonly Dictionary<ushort, Component> _nsoCache = new Dictionary<ushort, Component>();
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
                            _nsoCache[id] = nso as Component;
                        }
                    }
                    _nsoCacheRebuildAt = 60;
                }
                _nsoCacheRebuildAt--;

                int applied = 0;
                foreach (var e in snap)
                {
                    if (!_nsoCache.ContainsKey(e.Id)) continue;
                    // Phase 6.11.2 — record target; SmoothTowardTargets lerps each frame.
                    _nsoTargets[e.Id] = new PoseTarget { Pos = new Vector3(e.X, e.Y, e.Z), Rot = Quaternion.Euler(0f, 0f, e.RotZ) };
                    applied++;
                }
                if (_snapsApplied == 1 || _snapsApplied % 90 == 0)
                    Log.LogInfo($"[P6.14] NSO snap tick={tick} targeted {applied}/{snap.Count}");
            }
            catch (Exception ex) { Log.LogWarning($"[P6.14 NSO apply] {ex.Message}"); }
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
                    // Phase 6.11 default: only correct LOCAL player. Other
                    // players come from forwarded PlayerUpdate (msgType 10).
                    // Opt-in via SFCLIENTRECON_SMOOTH_REMOTE=1 to also apply
                    // server positions for remote slots — fully server-
                    // authoritative view, more consistent across clients, but
                    // depends on server having accurate positions (gated until
                    // server-side simulation is rock solid).
                    if (!isLocal && !smoothRemote) continue;
                    var target = new Vector3(entry.X, entry.Y, entry.Z);
                    _playerTargets[entry.Slot] = target;
                    if (isLocal) _serverLastAckedSeq = entry.LastInputSeq;
                    // Phase 6.12.2 — divergence detection + hard snap.
                    // Look up local-predicted position at the same seq the
                    // server is reporting. If drift > tolerance, hard-snap
                    // the rigidbody to server position (overriding the
                    // gentle SmoothTowardTargets lerp). Full input-replay
                    // rollback (re-running Movement.cs from snapped seq to
                    // current) is next; this is the corrective floor.
                    if (isLocal && _historyLookup.TryGetValue(entry.LastInputSeq, out var predictedAtSeq))
                    {
                        float drift = Vector3.Distance(predictedAtSeq, target);
                        const float HardSnapThreshold = 2.5f;  // hard-snap above 2.5u
                        const float SoftSnapThreshold = 1.0f;  // log + smooth above 1.0u
                        if (drift > SoftSnapThreshold)
                        {
                            _divergenceLogged++;
                            if (_divergenceLogged == 1 || _divergenceLogged % 30 == 0)
                                Log.LogWarning($"[P6.12.2 divergence] seq={entry.LastInputSeq} predicted={predictedAtSeq} server={target} drift={drift:0.00}u — total events {_divergenceLogged}");

                            if (drift > HardSnapThreshold)
                            {
                                // Hard snap — find local player's rigidbody and slam it.
                                // Bypasses the lerp so correction is instant when drift
                                // is bad (player in wall, fell through platform, etc.).
                                // npType / nps are already in scope from the enclosing
                                // ApplySnapshot lookup.
                                foreach (var np2 in nps)
                                {
                                    var pidObj2 = pidField.GetValue(np2);
                                    if (!(pidObj2 is int pi2) || pi2 != localSlot) continue;
                                    var npComp2 = np2 as Component;
                                    var rb2 = npComp2.GetComponent<Rigidbody>() ?? npComp2.GetComponentInChildren<Rigidbody>();
                                    if ((object)rb2 != null) { rb2.position = target; rb2.velocity = Vector3.zero; }
                                    else npComp2.transform.position = target;
                                    Log.LogWarning($"[P6.12.2 HARD SNAP] drift {drift:0.00}u > {HardSnapThreshold}u — snapped to {target}");
                                    break;
                                }
                            }
                        }
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
                if ((object)ctrlType == null) return -1;
                var ctrls = UnityEngine.Object.FindObjectsOfType(ctrlType);
                if (ctrls == null) return -1;
                var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
                var pidF = AccessTools.Field(ctrlType, "playerID");
                if ((object)hasCtrlF == null || (object)pidF == null) return -1;
                foreach (var c in ctrls)
                {
                    bool has = (bool)hasCtrlF.GetValue(c);
                    if (!has) continue;
                    int pid = (int)pidF.GetValue(c);
                    _localSlot = pid;
                    Log.LogInfo($"[P6.11] Discovered localSlot={pid}.");
                    return pid;
                }
            }
            catch (Exception e) { Log.LogWarning($"FindLocalSlot: {e.Message}"); }
            return -1;
        }
    }
}
