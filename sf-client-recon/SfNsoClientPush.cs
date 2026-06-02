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

                // Bulletproof local physics: skip the network-driven transform lerp
                // for pushable crates. mIsListening gets re-set to true by Init /
                // InitNetworkIndex / UpdateHost (e.g. the pre-combat re-init ~3s in),
                // which would otherwise restart the lerp and make crates fall "por
                // partes" + feel rigid again. Patching LerpLocalDummy guarantees the
                // network never moves a pushable crate regardless of mIsListening.
                var lerpDummy = AccessTools.Method(_nsoPushType, "LerpLocalDummy");
                if ((object)lerpDummy != null)
                    harmony.Patch(lerpDummy, prefix: new HarmonyMethod(typeof(Plugin), nameof(LerpLocalDummy_PushPrefix)));

                Log.LogInfo("[nso-push] Pushable crates = pure local physics (LerpLocalDummy skipped); relay active.");
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

        // Cache: NSO instanceID -> is a pushable ground crate (true => skip the
        // network lerp; local physics owns it). Computed once per object.
        private static readonly Dictionary<int, bool> _pushableLerpCache = new Dictionary<int, bool>();
        internal static void ClearPushableLerpCache() { _pushableLerpCache.Clear(); }

        // Prefix on NetworkSyncableObject.LerpLocalDummy. Returns false to skip the
        // original (the network-driven transform lerp) for pushable crates.
        internal static bool LerpLocalDummy_PushPrefix(object __instance)
        {
            if (!_oraclePushMode) return true;
            var comp = __instance as Component;
            if ((object)comp == null) return true;
            int id = comp.GetInstanceID();
            bool pushable;
            if (!_pushableLerpCache.TryGetValue(id, out pushable))
            {
                var go = comp.gameObject;
                if ((object)_dontEnableRigType == null)
                    _dontEnableRigType = AccessTools.TypeByName("DontEnableRig");
                bool floating = (object)_dontEnableRigType != null
                    && (object)go.GetComponentInChildren(_dontEnableRigType, true) != null;
                pushable = !floating && IsPushableCrateRoot(go);
                _pushableLerpCache[id] = pushable;
            }
            return !pushable;   // pushable => skip lerp; everything else => normal
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
                        // Configure crate physics HERE — at map load, BEFORE the
                        // crate settles. Doing it later (in the NSO cache rebuild,
                        // which first fires ~3s in) re-applied constraints/collision
                        // mode to already-settled, touching crates and jolted them:
                        // that was the "a los 3 segundos de la nada explotan /
                        // desaparecen / se caen" bug. We also NO LONGER freeze
                        // position Z (locking a settled crate at a mismatched depth
                        // dropped it through the floor / destabilised stacks).
                        ConfigureCratePhysics(rb);
                    }
                // CRITICAL: mIsListening = FALSE for local-physics crates.
                // When a NetworkSyncableObject "listens", its Update/LerpLocalDummy
                // continuously lerps the crate's transform toward the position it
                // receives over the network (ObjectUpdate / msg 26). With it TRUE
                // the server's 30Hz stream was dragging our crates around: they
                // fell "por partes" (stepped lerp toward server pos) and felt
                // rigid / un-pushable (the lerp yanked them back the instant you
                // pushed). FALSE = the network never touches the transform, so our
                // local Unity physics fully owns them; we still push our positions
                // UP to the server via the relay.
                var listenF = AccessTools.Field(__instance.GetType(), "mIsListening");
                if ((object)listenF != null) listenF.SetValue(__instance, false);
            }
            catch { }
            return false;
        }

        // Applied once, at map-load, to every pushable ground-crate rigidbody.
        // Stable settling values so crates grip + stack, settle fast after a hit,
        // and can't spin up forever. NO position-Z freeze (the game owns the
        // play plane; forcing it here dropped crates through the floor).
        internal static void ConfigureCratePhysics(Rigidbody rb)
        {
            if ((object)rb == null) return;
            try
            {
                var cols = rb.GetComponentsInChildren<Collider>();
                if (cols != null)
                    foreach (var col in cols)
                        // sharedMaterial (NOT material): assigning `.material`
                        // clones a NEW PhysicMaterial per collider, and we do this
                        // for every crate every round → a steady material leak that
                        // made FPS degrade the longer you played. sharedMaterial
                        // reuses the one instance.
                        if ((object)col != null && !col.isTrigger) col.sharedMaterial = ClientGripMaterial;
                // Near-vanilla feel: crates REACT to bullets/pushes (low drag),
                // but a touch of angular drag so a spin decays instead of going
                // near-forever, and a sane spin cap (7 = Unity default-ish) so a
                // hit can't pump runaway energy. Heavy damping made them feel dead
                // ("no reaccionan a los tiros") — reverted.
                // Weight tuning. Vanilla crates (>=50) were immovable in a cluster;
                // mass 8 was the opposite — they had no weight and slid all over
                // from a walk/bump/bullet. 25 is the middle: they feel heavy (a
                // walk barely nudges them, bullets push a little) but a deliberate
                // push or an explosion still moves a whole row, because friction is
                // kept low (0.3 / Minimum) so a cluster doesn't fight the floor.
                // Player push is velocity-driven, so friction only changed how
                // crates behave with EACH OTHER (and high friction made stacks
                // weird). To resist the PLAYER you need real INERTIA: a high mass
                // means the player's collision impulse moves the crate much less,
                // so it feels heavy to shove. Friction stays low (set on the
                // material) so crate-vs-crate stays normal and clusters still slide.
                rb.mass                = 110f;
                // Very low sleep threshold so a crate balanced on the edge of
                // another doesn't "fall asleep" while it still has a tipping torque
                // — that was why it sat perched instead of toppling. It still
                // sleeps once truly settled.
                rb.sleepThreshold      = 0.005f;
                rb.drag                = 0.45f;
                // Near-zero angular drag so the crate freely TIPS/rotates (around
                // Z, the 2.5D plane axis) and topples off edges + tumbles in the
                // air like real physics. maxAngularVelocity keeps it from whirling
                // insanely.
                rb.angularDrag         = 0.05f;
                rb.maxAngularVelocity  = 7f;
                // Discrete = cheapest (ContinuousDynamic on every crate was a big
                // CPU cost → FPS lag). Crates are slow enough not to tunnel.
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                rb.interpolation       = RigidbodyInterpolation.Interpolate;          // smooth render
                // Rotation: freeze X/Y (out-of-plane tilt) but FORCE Z FREE so the
                // crate can tip/tumble in the 2.5D plane. The prefab ships crates
                // with Z rotation frozen (for stability), and a `|=` never unfroze
                // it → crates only slid, never rotated ("no giran, se salen de la
                // hitbox sin voltearse"). Clear the Z-rotation bit explicitly.
                var c = rb.constraints;
                c |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY;
                c &= ~RigidbodyConstraints.FreezeRotationZ;
                rb.constraints = c;
            }
            catch { }
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
