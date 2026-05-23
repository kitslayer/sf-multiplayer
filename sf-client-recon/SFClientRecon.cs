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
        // Hardcoded for now; matches sf-headless-host V26_CLIENT_PORT. Env-var
        // override comes when we add multi-client-on-same-host testing.
        private const int V26_LISTEN_PORT = 1339;

        private UdpClient _socket;
        private Thread _rxThread;
        private volatile bool _running;

        // Pending snapshot (set on RX thread, applied on main thread).
        private readonly object _snapLock = new object();
        private List<SnapshotEntry> _pendingSnap;
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
        }

        private void Awake()
        {
            Log = Logger;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-batchmode" || arg == "-nographics")
                {
                    Log.LogInfo($"{PluginName}: batchmode detected — client-recon does nothing on oracle. Bye.");
                    return;
                }
            }
            Log.LogInfo($"{PluginName} {PluginVersion}: starting v26 snapshot listener on UDP :{V26_LISTEN_PORT}.");
            try
            {
                _socket = new UdpClient(V26_LISTEN_PORT);
                _running = true;
                _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "SFClientRecon-RX" };
                _rxThread.Start();
                Log.LogInfo("RX thread started.");
            }
            catch (Exception e)
            {
                Log.LogError($"UDP bind on {V26_LISTEN_PORT} failed: {e.Message}. Reconciliation disabled.");
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
            byte count = pkt[bodyOff + 4];
            int o = bodyOff + 5;
            var list = new List<SnapshotEntry>(count);
            int snapEntrySize = 1 + 12;
            for (int i = 0; i < count; i++)
            {
                if (o + snapEntrySize > bodyOff + bodyLen) break;
                var e = new SnapshotEntry
                {
                    Slot = pkt[o],
                    X = BitConverter.ToSingle(pkt, o + 1),
                    Y = BitConverter.ToSingle(pkt, o + 5),
                    Z = BitConverter.ToSingle(pkt, o + 9),
                };
                o += snapEntrySize;
                list.Add(e);
            }

            lock (_snapLock)
            {
                _pendingSnap = list;
                _pendingTick = tick;
                _snapsReceived++;
            }
        }

        private void Update()
        {
            if (!_running) return;
            List<SnapshotEntry> snap;
            uint tick;
            lock (_snapLock)
            {
                snap = _pendingSnap;
                tick = _pendingTick;
                _pendingSnap = null;
            }
            if (snap == null) return;
            ApplySnapshot(snap, tick);
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

                foreach (var entry in snap)
                {
                    // Phase 6.11 minimum: only correct LOCAL player. Other
                    // players continue rendering from forwarded PlayerUpdate.
                    if (entry.Slot != localSlot) continue;

                    UnityEngine.Object matched = null;
                    foreach (var np in nps)
                    {
                        var pidObj = pidField.GetValue(np);
                        if (pidObj is int pi && pi == entry.Slot) { matched = np; break; }
                    }
                    if ((object)matched == null) continue;

                    var npComp = matched as Component;
                    if ((object)npComp == null) continue;

                    var target = new Vector3(entry.X, entry.Y, entry.Z);
                    // Snap the root rigidbody (and transform fallback).
                    var rb = npComp.GetComponent<Rigidbody>();
                    if ((object)rb == null) rb = npComp.GetComponentInChildren<Rigidbody>();
                    if ((object)rb != null) rb.position = target;
                    npComp.transform.position = target;
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
