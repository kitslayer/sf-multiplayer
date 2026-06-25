using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    /// <summary>
    /// Oracle client: dynamic (predicted) pushable crates + server-auth reconciliation.
    /// </summary>
    public partial class Plugin
    {
        private static bool _oraclePushMode;
        // v0.6.0 — the 5Hz msg-26 relay is LEGACY-mode only. In the default
        // (server-authoritative) mode the oracle drops client ObjectUpdates,
        // and crate state flows one way: oracle sim → v26 snapshot → client.
        private static bool _crateRelayEnabled;
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

        // Crate modes (v0.6.0). Default = PREDICTED + RECONCILED: crates run
        // dynamic local physics (instant, full-framerate push feel) and are
        // continuously steered toward the oracle's sim — the single authority —
        // by ReconcilePushableCrates. The old default (pure local physics +
        // 5Hz relay, no incoming correction) meant every client lived in its
        // own crate universe: A's pushes never appeared on B, and the server's
        // "truth" ping-ponged between the clients' divergent relays.
        //   SF_CRATES_LOCAL_PHYSICS=1        → legacy local+relay mode (A/B rollback)
        //   SF_CRATES_SERVER_AUTHORITATIVE=1 → stock kinematic-follow (debug)
        private static bool ServerAuthoritativeCrates()
        {
            string v = Environment.GetEnvironmentVariable("SF_CRATES_SERVER_AUTHORITATIVE");
            return !string.IsNullOrEmpty(v) && (v == "1" || v.ToLowerInvariant() == "true");
        }

        private static bool LegacyLocalCrates()
        {
            string v = Environment.GetEnvironmentVariable("SF_CRATES_LOCAL_PHYSICS");
            return !string.IsNullOrEmpty(v) && (v == "1" || v.ToLowerInvariant() == "true");
        }

        // Reconciliation runs only in the default mode (not legacy, not stock).
        private static bool CrateReconcileActive => _oraclePushMode && !_crateRelayEnabled;

        private void InstallNsoClientPushPatches()
        {
            DetectOracleConnectMode();
            if (!_oracleConnectMode) return;
            if (ServerAuthoritativeCrates())
            {
                Log.LogInfo("[nso-push] STOCK kinematic-follow crates forced (SF_CRATES_SERVER_AUTHORITATIVE=1) — "
                          + "client local physics OFF; crates follow server snapshots.");
                return;
            }
            _oraclePushMode = true;
            _crateRelayEnabled = LegacyLocalCrates();
            if (_crateRelayEnabled)
                Log.LogInfo("[nso-push] LEGACY local-crates mode (SF_CRATES_LOCAL_PHYSICS=1) — pure local physics + 5Hz relay, no reconciliation.");
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

                if (_crateRelayEnabled)
                    Log.LogInfo("[nso-push] Pushable crates = pure local physics (LerpLocalDummy skipped); relay active.");
                else
                    Log.LogInfo("[nso-push] Pushable crates = predicted local physics + server reconciliation (v0.6.0); oracle sim is authoritative, relay OFF.");
            }
            catch (Exception e) { Log.LogWarning($"[nso-push] install failed: {e.Message}"); }
        }

        internal void TickNsoClientPushRelay()
        {
            // LEGACY MODE ONLY (SF_CRATES_LOCAL_PHYSICS=1). In the default
            // server-auth mode nothing is relayed up — the oracle drops client
            // ObjectUpdates anyway, and crate state flows oracle → client.
            if (!_oraclePushMode || !_crateRelayEnabled || !_running) return;
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
            // Scene gate — leave MainScene NSOs alone (menu decorations).
            try
            {
                var sn = comp.gameObject.scene.name;
                if (string.IsNullOrEmpty(sn) || sn == "MainScene") return true;
            }
            catch { return true; }
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
            // SCENE GATE: NEVER touch NSOs that live in MainScene (the persistent
            // menu / lobby scene with decorative barrels). Match maps are loaded
            // ADDITIVELY into their own scene; only crates IN those map scenes get
            // our physics config. Without this gate, the PLAY ONLINE menu barrels
            // were being given CoM=0.58, mass=36, Z-rotation-free → they fell over
            // and bounced around the menu like crazy.
            try
            {
                var sceneName = rootGo != null ? rootGo.scene.name : null;
                if (string.IsNullOrEmpty(sceneName) || sceneName == "MainScene") return true;
            }
            catch { return true; }
            // Floating crates (DontEnableRig) are suspended on purpose. Do NOT
            // force them dynamic or they fall "porque sí". BUT we still want our
            // physics tuning (CoM, mass, grip, free Z rotation) to be on them so
            // that WHEN the game activates them (hit / event), they immediately
            // fall + tip naturally instead of using vanilla physics. So:
            //   1) pre-configure every rigidbody (idempotent; doesn't touch
            //      isKinematic),
            //   2) let vanilla run (keeps them kinematic / floating).
            if ((object)_dontEnableRigType == null)
                _dontEnableRigType = AccessTools.TypeByName("DontEnableRig");
            bool isFloating = (object)_dontEnableRigType != null && (object)rootGo != null
                && (object)rootGo.GetComponentInChildren(_dontEnableRigType, true) != null;
            if (isFloating)
            {
                try
                {
                    var rbsF = (__instance as Component)?.GetComponentsInChildren<Rigidbody>();
                    if (rbsF != null)
                        foreach (var rbF in rbsF)
                            if ((object)rbF != null) ConfigureCratePhysics(rbF, floatingPreset: true);
                }
                catch { }
                return true;   // run vanilla → stays kinematic/floating until activated
            }
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
                        ConfigureCratePhysics(rb, floatingPreset: false);
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

        // Applied at map-load to every pushable crate rigidbody. v0.6.0
        // VANILLA-FIRST (mirrors SFBoxFix v0.3.0): mass / CoM / inertia / drag /
        // materials are NOT overridden anymore. Stock crates are heavy (some
        // prefabs ship mass ≈ 1500) — the old 45-mass override made players
        // shove them around "like they are nothing" on every sim. Keeping
        // prefab values is both perfect client↔server parity (neither side
        // touches them) and the vanilla feel. Only what sync REQUIRES is set:
        // the unified constraint mask, tunneling-safe collision detection, and
        // render interpolation (client-only; no sim effect).
        internal static void ConfigureCratePhysics(Rigidbody rb, bool floatingPreset = false)
        {
            if ((object)rb == null) return;
            try
            {
                ApplyCrateConstraintMask(rb);
                rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
            catch { }
        }

        private static void ApplyCrateConstraintMask(Rigidbody rb)
        {
            // GROUND TRUTH (decompile NetworkSyncableObject:498-512 + LerpLocalDummy
            // :270-274): stock SF syncs NSO rotation as the up-vector's (y,z) and
            // reconstructs LookRotation(Cross(Vector3.right, up), up) — i.e. the
            // one real, network-visible rotation axis is world X (the up vector
            // tilting within the Y-Z play plane). Unified mask, mirrored in
            // SFBoxFix v0.3.0: free X (tip axis — synced via the v26.7 up-vector
            // section), freeze Y (yaw — unsyncable, so locked identically on both
            // sims) and Z (vanilla crate prefabs ship Z frozen).
            var c = rb.constraints;
            c |= RigidbodyConstraints.FreezeRotationY | RigidbodyConstraints.FreezeRotationZ;
            c &= ~RigidbodyConstraints.FreezeRotationX;
            rb.constraints = c;
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

        // ====================================================================
        //  SERVER-AUTH RECONCILIATION (v0.6.0)
        //  Crates stay DYNAMIC locally — your push is instant, full-framerate
        //  local physics — while the oracle's sim is the single authority.
        //  Each FixedUpdate every pushable crate is steered toward the
        //  latency-compensated server pose. Each May-2026 failure mode has an
        //  explicit countermeasure here:
        //    · compare against the server pose EXTRAPOLATED by its own
        //      velocity, never the raw last snapshot — measuring live local
        //      state against an RTT-stale reference made every moving crate
        //      look "wrong" and the correction yanked it backward mid-push;
        //    · corrections steer VELOCITY (lerp toward serverVel + error
        //      term), never write position — position writes inject
        //      penetration and the solver ejects violently;
        //    · inside the deadband nothing is injected at all, and when the
        //      server says the crate is at rest, residual creep is zeroed so
        //      idle crates can't drift apart;
        //    · while any player rig is near the crate the blend weakens —
        //      local prediction owns active contacts; convergence resumes
        //      the moment the contact ends;
        //    · only a huge error hard-snaps, and that path zeroes velocities
        //      and marks the root for the P0-15 destruction guard so the
        //      warp can't break adjacent ice/chains.
        // ====================================================================
        private const float ReconDeadband         = 0.20f;  // u — agreeing sims get zero injected motion
        private const float ReconHardSnap         = 1.8f;   // u — beyond this, clean teleport
        private const float ReconGain             = 4.0f;   // err (u) → corrective velocity (u/s)
        private const float ReconMaxCorrVel       = 2.5f;   // u/s cap on the error term
        private const float ReconBlendRate        = 6.0f;   // 1/s velocity-steer rate
        private const float ReconTouchGraceMul    = 0.25f;  // blend multiplier near a player rig
        private const float ReconTouchRadiusSqr   = 1.5f * 1.5f;
        private const float ReconContactMaxCorr   = 4.0f;   // u/s — in-contact hard-pull cap (no snaps mid-push)
        private const float ReconGlideMax         = 0.8f;   // u — settled divergence below this glides into place
        private const float ReconGlideSpeed       = 3.0f;   // u/s — glide rate for settled resolution
        private const float ReconRotDeadbandDeg   = 8f;
        private const float ReconStaleAfter       = 0.6f;   // s without a snapshot → hands off
        private const float ReconServerRestVelSqr = 0.01f;
        // Errors beyond this are an identity/scene mismatch (NSO ids are
        // reassigned every round and collide while both map scenes coexist),
        // not physics divergence — "correcting" them teleports crates toward
        // ghosts of the previous map. A real server-side void-rescue also
        // looks like this, so a persistent big error is accepted after 1.2s.
        private const float ReconIdentityBound    = 8f;
        private const float ReconIdentityAcceptSec = 2.0f;   // was 1.2 — too eager during slow boss-map transitions
        internal static float _reconSuppressUntil;  // set on MapChange; transition guard
        private readonly Dictionary<ushort, float> _reconBigErrSince = new Dictionary<ushort, float>();
        private int _reconHardSnaps;
        private float _boxSyncLogAt = -1f;

        // Defense-in-depth: a non-finite server pose must never reach rb.position —
        // a NaN/Inf would poison this crate's PhysX state and spread through
        // contacts. The host sanitizes its snapshot at the source now too; this
        // guards against a buggy/rogue server. net46 has no float.IsFinite, so
        // test components with the IsNaN/IsInfinity pair.
        private static bool IsFiniteVec(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        internal void ReconcilePushableCrates()
        {
            if (!CrateReconcileActive) return;
            if (Time.realtimeSinceStartup < _reconSuppressUntil) return;
            if (_nsoTargets.Count == 0 || _nsoCache.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            RefreshReconRigCache(now);
            float blendBase = 1f - Mathf.Exp(-ReconBlendRate * Time.fixedDeltaTime);
            int tracked = 0; float errSum = 0f, errMax = 0f;
            foreach (var kv in _nsoTargets)
            {
                NsoCacheEntry entry;
                if (!_nsoCache.TryGetValue(kv.Key, out entry) || entry == null) continue;
                if (!entry.Pushable) continue;
                var rb = entry.Rb;
                if (rb == null || rb.isKinematic) continue;
                var pose = kv.Value;
                if (pose == null || !pose.HasRender) continue;
                if (!IsFiniteVec(pose.Pos) || !IsFiniteVec(pose.Vel)) continue;
                float age = now - pose.LastRecvAt;
                if (age < 0f) age = 0f;
                // No fresh server data (round transition, packet loss burst):
                // leave the crate to local physics rather than dragging it
                // toward a stale pose.
                if (age > ReconStaleAfter) continue;
                float extrapAge = age > NsoMaxExtrapSec ? NsoMaxExtrapSec : age;
                Vector3 target = pose.Pos + pose.Vel * extrapAge;
                Vector3 err = target - rb.position;
                float errMag = err.magnitude;
                tracked++; errSum += errMag; if (errMag > errMax) errMax = errMag;

                if (errMag > ReconIdentityBound)
                {
                    float bigSince;
                    if (!_reconBigErrSince.TryGetValue(kv.Key, out bigSince))
                    {
                        _reconBigErrSince[kv.Key] = now;
                        continue;
                    }
                    if (now - bigSince < ReconIdentityAcceptSec) continue;
                    // Persistence alone is NOT enough: during a wrong-scene
                    // window every crate shows a persistent ~37u error and a
                    // time-based acceptance mass-teleported the whole field
                    // toward the previous map's layout (live, 2026-06-11).
                    // The only legitimate >8u correction is a void-rescue —
                    // and in a rescue OUR copy has fallen too. A crate in
                    // normal play space with a far-away target is an identity
                    // mismatch, never to be "corrected".
                    if (rb.position.y > -20f) continue;
                    _reconBigErrSince.Remove(kv.Key);
                }
                else
                {
                    _reconBigErrSince.Remove(kv.Key);
                }

                if (errMag > ReconHardSnap)
                {
                    // Mid-contact, a teleport under the player's hands is the
                    // worst possible artifact (first live test: 5 snaps in
                    // seconds while pushing). Pull hard-but-smooth instead;
                    // the snap only happens once the contact has ended.
                    // (Identity-scale errors skip this — they're never a push.)
                    if (errMag <= ReconIdentityBound && AnyReconRigNear(rb.position))
                    {
                        Vector3 corrHard = err * ReconGain;
                        float chSqr = corrHard.sqrMagnitude;
                        if (chSqr > ReconContactMaxCorr * ReconContactMaxCorr)
                            corrHard *= ReconContactMaxCorr / Mathf.Sqrt(chSqr);
                        rb.velocity = Vector3.Lerp(rb.velocity, pose.Vel + corrHard, blendBase);
                        continue;
                    }
                    var rootT = (entry.Comp != null) ? entry.Comp.transform.root : null;
                    if (rootT != null) _recentLerpAt[rootT.GetInstanceID()] = now;
                    rb.position = target;
                    if (pose.HasFullRot) rb.rotation = pose.Rot;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    _reconHardSnaps++;
                    continue;
                }
                if (errMag < ReconDeadband)
                {
                    if (pose.Vel.sqrMagnitude < ReconServerRestVelSqr
                        && rb.velocity.sqrMagnitude < CrateSettleLin * CrateSettleLin
                        && rb.angularVelocity.sqrMagnitude < CrateSettleAng * CrateSettleAng)
                    {
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    continue;
                }

                // SETTLED-DIVERGENCE RESOLUTION — both sims at rest but apart
                // (the post-blast scatter). Velocity corrections cannot move a
                // resting crate: ground friction (~μ0.4 → ~8 u/s² decel) eats
                // the injected ~0.2 u/s within one step, so without this branch
                // the error persists forever (first live test: meanErr ~0.4
                // across the field, flat). A resting crate away from players is
                // safe to position-resolve: glide small errors into place,
                // cleanly snap larger ones. P0-15-marked either way so the
                // motion can't fire destruction events.
                bool serverAtRest = pose.Vel.sqrMagnitude < ReconServerRestVelSqr;
                bool localAtRest = rb.velocity.sqrMagnitude < 0.05f
                                && rb.angularVelocity.sqrMagnitude < 0.1f;
                if (serverAtRest && localAtRest)
                {
                    // The GLIDE is allowed even with a player rig nearby: a
                    // settled crate easing a few cm per tick can't fight a
                    // push (pushing implies motion, which un-rests the crate
                    // and exits this branch). Gating glide on rig distance
                    // PARKED crates at a permanent ~1.5u offset whenever
                    // players idled next to them, with periodic >1.8u snaps
                    // (observed live 2026-06-12: flat 1.3-1.5u, snap, repark).
                    // Only the teleport stays rig-gated.
                    bool rigNear = AnyReconRigNear(rb.position);
                    if (errMag <= ReconGlideMax || !rigNear)
                    {
                        var restRoot = (entry.Comp != null) ? entry.Comp.transform.root : null;
                        if (restRoot != null) _recentLerpAt[restRoot.GetInstanceID()] = now;
                        if (errMag <= ReconGlideMax)
                        {
                            Vector3 step = err;
                            float stepMax = ReconGlideSpeed * Time.fixedDeltaTime;
                            if (errMag > stepMax) step *= stepMax / errMag;
                            rb.position = rb.position + step;
                            if (pose.HasFullRot && Quaternion.Angle(rb.rotation, pose.Rot) > 2f)
                                rb.rotation = Quaternion.Slerp(rb.rotation, pose.Rot, 0.2f);
                        }
                        else
                        {
                            rb.position = target;
                            if (pose.HasFullRot) rb.rotation = pose.Rot;
                            _reconHardSnaps++;
                        }
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                        continue;
                    }
                    // big error + rig near: fall through to the soft pull.
                }

                float blend = blendBase;
                if (AnyReconRigNear(rb.position)) blend *= ReconTouchGraceMul;
                Vector3 corr = err * ReconGain;
                float corrSqr = corr.sqrMagnitude;
                if (corrSqr > ReconMaxCorrVel * ReconMaxCorrVel)
                    corr *= ReconMaxCorrVel / Mathf.Sqrt(corrSqr);
                rb.velocity = Vector3.Lerp(rb.velocity, pose.Vel + corr, blend);
                if (pose.HasFullRot)
                {
                    float angErr = Quaternion.Angle(rb.rotation, pose.Rot);
                    if (angErr > ReconRotDeadbandDeg)
                        rb.MoveRotation(Quaternion.Slerp(rb.rotation, pose.Rot, blend));
                }
            }
            if (now >= _boxSyncLogAt)
            {
                _boxSyncLogAt = now + 5f;
                if (tracked > 0)
                    Log.LogInfo($"[BOX-SYNC] crates={tracked} meanErr={errSum / tracked:0.000} maxErr={errMax:0.000} hardSnaps={_reconHardSnaps}");
            }
        }

        // Player-rig transform cache for the touch-grace test. Transforms are
        // cached for 1s (rigs persist across a round); positions are read live.
        private static Type _npTypeForRecon;
        private readonly List<Transform> _reconRigCache = new List<Transform>(8);
        private float _reconRigCacheAt = -1f;
        private void RefreshReconRigCache(float now)
        {
            if (_reconRigCacheAt > 0f && now - _reconRigCacheAt < 1f) return;
            _reconRigCacheAt = now;
            _reconRigCache.Clear();
            if ((object)_npTypeForRecon == null) _npTypeForRecon = AccessTools.TypeByName("NetworkPlayer");
            if ((object)_npTypeForRecon == null) return;
            var nps = UnityEngine.Object.FindObjectsOfType(_npTypeForRecon);
            if (nps == null) return;
            foreach (var np in nps)
            {
                var c = np as Component;
                if (c != null) _reconRigCache.Add(c.transform);
            }
        }

        private bool AnyReconRigNear(Vector3 pos)
        {
            for (int i = 0; i < _reconRigCache.Count; i++)
            {
                var t = _reconRigCache[i];
                if (t == null) continue;   // Unity-aware: destroyed rig
                Vector3 d = t.position - pos;
                if (d.sqrMagnitude <= ReconTouchRadiusSqr) return true;
            }
            return false;
        }

    }
}
