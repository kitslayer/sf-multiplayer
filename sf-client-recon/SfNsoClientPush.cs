using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    /// <summary>
    /// Oracle client: dynamic pushable crates + relay ObjectUpdate (msg 26) to server.
    /// </summary>
    public partial class Plugin
    {
        private static bool _oraclePushMode;
        private static Type _nsoPushType;
        private static PropertyInfo _nsoPushIndexProp;
        private static FieldInfo _nsoPushIndexField;
        private static Type _dpPushType;
        private static FieldInfo _dpSimpleField;
        private static FieldInfo _dpEventField;
        private static float _nextPushRelayAt;
        private static readonly Dictionary<ushort, Vector3> _lastRelayPos = new Dictionary<ushort, Vector3>();
        private const float PushRelayInterval = 0.2f;
        private const float PushRelayMinDelta = 0.04f;

        private void InstallNsoClientPushPatches()
        {
            DetectOracleConnectMode();
            if (!_oracleConnectMode) return;
            _oraclePushMode = true;
            try
            {
                _nsoPushType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)_nsoPushType == null) return;

                var harmony = new Harmony(PluginGuid + ".nso-push");
                var disableRb = AccessTools.Method(_nsoPushType, "DisableAllRigidBodies");
                if ((object)disableRb != null)
                    harmony.Patch(disableRb, prefix: new HarmonyMethod(typeof(Plugin), nameof(DisableAllRigidBodies_PushPrefix)));

                Log.LogInfo("[nso-push] Pushable crates stay dynamic; ObjectUpdate relay active.");
            }
            catch (Exception e) { Log.LogWarning($"[nso-push] install failed: {e.Message}"); }
        }

        internal void TickNsoClientPushRelay()
        {
            // Crates are now PURE LOCAL PHYSICS and we apply NO incoming server
            // position to them, so the old tug-of-war is gone — it is safe (and
            // necessary for other clients) to relay our authoritative crate
            // positions UP to the server. Only fires while a match is live and
            // throttled to PushRelayInterval; only sends crates that actually
            // moved (RelayPushableCrateUpdates skips at-rest crates), so a still
            // stack costs no bandwidth and triggers no server-side motion.
            if (!_oraclePushMode || !_running) return;
            if (Time.realtimeSinceStartup < _nextPushRelayAt) return;
            _nextPushRelayAt = Time.realtimeSinceStartup + PushRelayInterval;
            try { RelayPushableCrateUpdates(); } catch { }
        }

        internal static bool DisableAllRigidBodies_PushPrefix(object __instance)
        {
            // OMEGA FIX: keep pushable crates DYNAMIC so local Unity physics runs
            // (smooth, instant, collides + stacks). Server stays authoritative via
            // the gentle soft-correction in SmoothTowardTargets — NOT via the relay
            // (relay is disabled below) and NOT by forcing kinematic.
            var rootGo = (__instance as Component)?.gameObject;
            if (!_oraclePushMode || !IsPushableCrateRoot(rootGo)) return true;
            // Floating crates (DontEnableRig) are suspended on purpose. Do NOT
            // force them dynamic or they fall "porque sí". Let vanilla disable
            // their rigidbody (keep kinematic); the game re-enables them only on
            // activation. Only ground/pushable crates stay dynamic for local feel.
            if ((object)_dontEnableRigType == null)
                _dontEnableRigType = AccessTools.TypeByName("DontEnableRig");
            if ((object)_dontEnableRigType != null && (object)rootGo != null
                && (object)rootGo.GetComponentInChildren(_dontEnableRigType, true) != null)
                return true;   // run vanilla → stays kinematic/floating
            try
            {
                var rbs = (__instance as Component)?.GetComponentsInChildren<Rigidbody>();
                if (rbs != null)
                    foreach (var rb in rbs)
                    {
                        if ((object)rb == null) continue;
                        // Per-rigidbody guard: a sub-piece may carry DontEnableRig.
                        if ((object)_dontEnableRigType != null
                            && (object)rb.GetComponent(_dontEnableRigType) != null) continue;
                        rb.isKinematic = false;
                    }
                var listenF = AccessTools.Field(__instance.GetType(), "mIsListening");
                if ((object)listenF != null) listenF.SetValue(__instance, true);
            }
            catch { }
            return false;
        }

        private void RelayPushableCrateUpdates()
        {
            if ((object)_nsoPushType == null) return;
            var pktType = AccessTools.TypeByName("P2PPackageHandler");
            var mmType = AccessTools.TypeByName("MultiplayerManager");
            if ((object)pktType == null || (object)mmType == null) return;

            var pkt = AccessTools.Property(pktType, "Instance")?.GetValue(null, null)
                ?? UnityEngine.Object.FindObjectOfType(pktType);
            if (!RefOk(pkt)) return;

            var mm = UnityEngine.Object.FindObjectOfType(mmType);
            if (!RefOk(mm)) return;
            var gmType = AccessTools.TypeByName("GameManager");
            if ((object)gmType != null)
            {
                var inFightF = AccessTools.Field(gmType, "inFight");
                if ((object)inFightF != null && !(bool)inFightF.GetValue(null)) return;
            }

            var all = UnityEngine.Object.FindObjectsOfType(_nsoPushType);
            if (all == null) return;

            foreach (var nso in all)
            {
                var comp = nso as Component;
                if ((object)comp == null || !IsPushableCrateRoot(comp.gameObject)) continue;
                var rb = comp.GetComponent<Rigidbody>();
                if ((object)rb == null || rb.isKinematic) continue;
                if (rb.velocity.sqrMagnitude < 0.0001f && rb.angularVelocity.sqrMagnitude < 0.0001f) continue;

                ushort id = GetNsoIndex(nso);
                var p = rb.position;
                var e = rb.rotation.eulerAngles;
                if (_lastRelayPos.TryGetValue(id, out var last) && Vector3.Distance(last, p) < PushRelayMinDelta) continue;
                _lastRelayPos[id] = p;

                byte[] body = new byte[10];
                body[0] = (byte)(id & 0xFF);
                body[1] = (byte)((id >> 8) & 0xFF);
                short ry = (short)Mathf.RoundToInt(p.y * 100f);
                short rz = (short)Mathf.RoundToInt(p.z * 100f);
                short rotZ = (short)Mathf.RoundToInt(e.z * 100f);
                body[2] = (byte)(ry & 0xFF);
                body[3] = (byte)((ry >> 8) & 0xFF);
                body[4] = (byte)(rz & 0xFF);
                body[5] = (byte)((rz >> 8) & 0xFF);
                body[8] = (byte)(rotZ & 0xFF);
                body[9] = (byte)((rotZ >> 8) & 0xFF);

                var send = AccessTools.Method(pktType, "SendPacketToServer");
                if ((object)send == null) continue;
                var msgTypeEnum = AccessTools.Inner(pktType, "MsgType");
                if ((object)msgTypeEnum == null) continue;
                object msg26 = Enum.ToObject(msgTypeEnum, 26);
                try { send.Invoke(pkt, new object[] { body, msg26, 10 }); } catch { }
            }
        }

        private static ushort GetNsoIndex(object nso)
        {
            if ((object)_nsoPushIndexProp == null)
            {
                _nsoPushIndexProp = AccessTools.Property(_nsoPushType, "Index");
                _nsoPushIndexField = AccessTools.Field(_nsoPushType, "m_Index");
            }
            if ((object)_nsoPushIndexProp != null) return (ushort)_nsoPushIndexProp.GetValue(nso, null);
            if ((object)_nsoPushIndexField != null) return (ushort)_nsoPushIndexField.GetValue(nso);
            return 0;
        }

        private static bool IsPushableCrateRoot(GameObject root)
        {
            if ((object)root == null) return false;
            if ((object)_dpPushType == null)
            {
                _dpPushType = AccessTools.TypeByName("DestructiblePiece");
                if ((object)_dpPushType != null)
                {
                    _dpSimpleField = AccessTools.Field(_dpPushType, "simpleDestruction");
                    _dpEventField = AccessTools.Field(_dpPushType, "eventDestruction");
                }
            }
            if ((object)_dpPushType == null) return true;
            var dps = root.GetComponentsInChildren(_dpPushType);
            if (dps == null || dps.Length == 0) return true;
            foreach (var dp in dps)
            {
                if ((object)dp == null) continue;
                bool simple = (object)_dpSimpleField != null && (bool)_dpSimpleField.GetValue(dp);
                bool ev = (object)_dpEventField != null && (bool)_dpEventField.GetValue(dp);
                if (simple && !ev) return true;
            }
            return false;
        }

    }
}
