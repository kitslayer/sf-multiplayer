using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
// Mono 2.0 (Unity 5.6.3) note: NEVER use lambdas, Action<T>, or `yield return`
// iterators in this assembly — the C# 9 compiler emits IteratorStateMachineAttribute
// references that Mono 2.0 cannot resolve. Use plain delegate types / Update() timers.

namespace SFBoxFix
{
    // SFBoxFix — multi-fix server-side companion plugin to SFHeadlessHost.
    // Coexists with the host plugin without modifying its binary. Since
    // v0.3.0 crate body values are vanilla-first: only the constraint mask
    // and CCD mode are configured; mass/CoM/drag/materials stay prefab.
    //
    // ALL fixes are surgical Harmony patches. If a target type/method isn't
    // found, that individual fix is silently skipped.
    //
    // Server-managed (non-negotiable per user requirement):
    //   • CAJAS-1: IsExplosiveWeaponType — bullets no longer trigger 900f blast
    //   • CAJAS-2: DestructiblePiece.OnCollisionEnter — skip NSO-vs-NSO (boxes
    //              colliding with boxes no longer explode each other)
    //   • CAJAS-3: NSO force-unkinematic on scene load (Christmas/Halloween
    //              prefabs sometimes ship as kinematic by default)
    //   • SNAKES-1: SnakeAI.damageMultiplier = 0 (snakes don't deal damage)
    //   • DEATH-1: Reduce RoundMinPlaySec from 12s → 2s (faster round advance
    //              when somebody dies quickly)

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.stickfightdev.headless-host", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.box-fix";
        public const string PluginName = "SFBoxFix";
        public const string PluginVersion = "0.3.2";

        internal static ManualLogSource Log;
        private static Type _dpType;
        private static FieldInfo _dpSimpleField;
        private static FieldInfo _dpEventField;
        private static FieldInfo _dpForceThresholdField;
        private static Type _ctrlType;
        private static FieldInfo _ctrlHasControlField;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} starting up.");

            _harmony = new Harmony(PluginGuid);

            // Apply each fix independently. Failures logged but don't block others.
            // (No lambdas → Mono 2.0 safe.)
            try { ApplyMono2OpInequalityFix();   } catch (Exception e) { Log.LogError($"[MONO-FIX] {e.Message}"); }
            try { ApplyExplosiveWeaponTypeFix(); } catch (Exception e) { Log.LogError($"[CAJAS-1] {e.Message}"); }
            try { ApplyBoxCollisionFilter();     } catch (Exception e) { Log.LogError($"[CAJAS-2] {e.Message}"); }
            try { ApplySnakeDamageZero();        } catch (Exception e) { Log.LogError($"[SNAKES-1] {e.Message}"); }
            try { ApplyFastRoundAdvance();       } catch (Exception e) { Log.LogError($"[DEATH-1] {e.Message}"); }
            // (D1/D2 code-review) DEATH-2/DEATH-3 removed — see note below.

            // Scene-level pass via SceneManager event — no coroutine, just a
            // deferred timer in Update() to wait for settle.
            SceneManager.sceneLoaded += OnSceneLoadedUnfreezeXmas;
        }

        private static Harmony _harmony;
        private Scene _pendingUnfreezeScene;
        private float _pendingUnfreezeAt = -1f;

        // ========== MONO-FIX: SFHeadlessHost.PushPlayerAction op_Inequality ==========
        // Kit's SFHeadlessHost v0.3.8 uses `_cachedUpdateWithValue != null` on
        // a MethodInfo in GetUpdateWithValueMethod. C# 9 emits a call to
        // MethodInfo.op_Inequality (a .NET 4.5+ operator) which Mono 2.0
        // (Unity 5.6.3) does NOT have → MissingMethodException every frame
        // → WriteInputsToRigs catch swallows it → server inputs never reach
        // player rig → character freezes, server drops client after 31s of
        // "no input" → LOBBY stuck bug returns.
        //
        // Fix: prefix PushPlayerAction (the only caller that triggers the
        // bug) and do the entire dispatch ourselves with `(object) != null`
        // checks (which compile to raw `ceq` opcodes — never call op_Inequality).
        private static MethodInfo _safeCachedUpdateWithValue;
        private static bool _safeCacheLooked;
        private void ApplyMono2OpInequalityFix()
        {
            var hostType = AccessTools.TypeByName("SFHeadlessHost.Plugin");
            if ((object)hostType == null) return;
            var target = AccessTools.Method(hostType, "PushPlayerAction",
                new Type[] { typeof(object), typeof(string), typeof(float) });
            if ((object)target == null) { Log.LogWarning("PushPlayerAction not found."); return; }
            var prefix = AccessTools.Method(typeof(Plugin), nameof(PushPlayerAction_Prefix));
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Log.LogInfo("[MONO-FIX] Patched PushPlayerAction — Mono 2.0 op_Inequality bypass.");
        }
        // PERFORMANCE: cache field lookups by (Type, fieldName) — without this
        // PushPlayerAction does ~600 reflection lookups per second per player.
        // Cached, it's a single dict lookup. Eliminates the server lag from v0.2.1.
        private static readonly Dictionary<string, FieldInfo> _pushFieldCache = new Dictionary<string, FieldInfo>(64);
        private static readonly object[] _pushArgsBuffer = new object[3];

        private static bool PushPlayerAction_Prefix(object actions, string fieldName, float value)
        {
            // Replace the body entirely. Returns false → original is skipped.
            try
            {
                if ((object)actions == null) return false;
                var actionsType = actions.GetType();
                string cacheKey = actionsType.FullName + "|" + fieldName;

                FieldInfo f;
                if (!_pushFieldCache.TryGetValue(cacheKey, out f))
                {
                    f = AccessTools.Field(actionsType, fieldName);
                    _pushFieldCache[cacheKey] = f;  // store even if null — avoid repeated lookups
                }
                if ((object)f == null) return false;

                var action = f.GetValue(actions);
                if ((object)action == null) return false;

                // Get the UpdateWithValue MethodInfo (cached, safe null check).
                if ((object)_safeCachedUpdateWithValue == null && !_safeCacheLooked)
                {
                    _safeCacheLooked = true;
                    var paType = AccessTools.TypeByName("InControl.PlayerAction");
                    if ((object)paType != null)
                    {
                        _safeCachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue",
                            new Type[] { typeof(float), typeof(ulong), typeof(float) });
                        if ((object)_safeCachedUpdateWithValue == null)
                            _safeCachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue");
                        if ((object)_safeCachedUpdateWithValue != null)
                            Log.LogInfo($"[MONO-FIX] Cached UpdateWithValue: {_safeCachedUpdateWithValue}");
                    }
                }
                if ((object)_safeCachedUpdateWithValue == null) return false;

                // Reuse the args buffer — avoids GC pressure from allocating
                // new object[3] every input frame (60Hz × 10 actions × N players).
                _pushArgsBuffer[0] = value;
                _pushArgsBuffer[1] = (ulong)0;
                _pushArgsBuffer[2] = Time.deltaTime;
                _safeCachedUpdateWithValue.Invoke(action, _pushArgsBuffer);
            }
            catch { /* swallow per original behavior */ }
            return false;  // skip original (which has the op_Inequality bug)
        }

        // ========== CAJAS-1: IsExplosiveWeaponType ==========
        private void ApplyExplosiveWeaponTypeFix()
        {
            var hostType = AccessTools.TypeByName("SFHeadlessHost.Plugin");
            if ((object)hostType == null)
            {
                Log.LogInfo("SFHeadlessHost.Plugin not present — running on client.");
                return;
            }
            var target = AccessTools.Method(hostType, "IsExplosiveWeaponType",
                new Type[] { typeof(byte), typeof(float) });
            if ((object)target == null) { Log.LogWarning("IsExplosiveWeaponType not found."); return; }
            var prefix = AccessTools.Method(typeof(Plugin), nameof(IsExplosiveWeaponType_Prefix));
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Log.LogInfo("[CAJAS-1] Patched IsExplosiveWeaponType — bullets no longer blast boxes.");
        }
        private static bool IsExplosiveWeaponType_Prefix(byte weaponType, float speed, ref bool __result)
        {
            __result = weaponType == 5 || weaponType == 6 || weaponType == 7 || weaponType == 8;
            return false;
        }

        // ========== CAJAS-2: DestructiblePiece.OnCollisionEnter — skip NSO-vs-NSO ==========
        // Vanilla DestructiblePiece.OnCollisionEnter triggers destruction when
        // velocity * multiplier > forceThreshold. When two NSO boxes collide
        // (no player involved), this can spuriously fire — boxes destroy each
        // other on impact. Vanilla expected this (it's an emergent behavior),
        // BUT on a server-authoritative oracle, server-side position snapshots
        // can cause artificial velocity spikes that trigger destruction.
        //
        // Fix: prefix that skips Collide() entirely when the colliding rigidbody
        // is NOT a player controller. Player-vs-box hits still work.
        private void ApplyBoxCollisionFilter()
        {
            _dpType = AccessTools.TypeByName("DestructiblePiece");
            if ((object)_dpType == null) { Log.LogInfo("DestructiblePiece not present — skipping."); return; }
            _dpSimpleField = AccessTools.Field(_dpType, "simpleDestruction");
            _dpEventField = AccessTools.Field(_dpType, "eventDestruction");
            _dpForceThresholdField = AccessTools.Field(_dpType, "forceThreshold");
            _ctrlType = AccessTools.TypeByName("Controller");
            if ((object)_ctrlType != null)
                _ctrlHasControlField = AccessTools.Field(_ctrlType, "mHasControl");

            var target = AccessTools.Method(_dpType, "OnCollisionEnter");
            if ((object)target == null) { Log.LogWarning("DestructiblePiece.OnCollisionEnter not found."); return; }
            var prefix = AccessTools.Method(typeof(Plugin), nameof(DestructibleOnCollision_Prefix));
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Log.LogInfo("[CAJAS-2] Patched DestructiblePiece.OnCollisionEnter — NSO-vs-NSO no longer destroys.");
        }
        // PERFORMANCE: cache "is this rigidbody root a player rig?" per
        // instance-id. CRITICAL because OnCollisionEnter fires hundreds of
        // times per frame in dense scenes — uncached GetComponent lookups
        // were contributing to the server-thread saturation Kit identified
        // (game thread = network thread → backed-up exceptions overflow the
        // kernel UDP buffer → server unresponsive).
        private static readonly Dictionary<int, bool> _rigIsPlayerCache = new Dictionary<int, bool>(64);
        private static int _cacheCounter;

        private static bool DestructibleOnCollision_Prefix(object __instance, Collision collision)
        {
            if ((object)collision == null) return true;
            var rb = collision.rigidbody;
            if ((object)rb == null) return true;
            if ((object)_ctrlType == null) return true;

            // Cache the "is player" check per transform-root instance id.
            // Player rigs are persistent — their root doesn't change.
            int rootId;
            try { rootId = rb.transform.root.GetInstanceID(); }
            catch { return true; }

            bool isPlayer;
            if (!_rigIsPlayerCache.TryGetValue(rootId, out isPlayer))
            {
                try { isPlayer = (object)rb.transform.root.GetComponent(_ctrlType) != null; }
                catch { isPlayer = true; }  // on error, default to vanilla behavior
                _rigIsPlayerCache[rootId] = isPlayer;

                // Periodic prune to avoid unbounded growth (rigs get destroyed
                // between rounds, instance ids get reused).
                _cacheCounter++;
                if (_cacheCounter > 500)
                {
                    _cacheCounter = 0;
                    _rigIsPlayerCache.Clear();
                }
            }

            if (isPlayer) return true;  // player hit → vanilla behavior (boxes can break)

            // NSO-vs-NSO or NSO-vs-static: skip destruction entirely.
            return false;
        }

        // ========== SNAKES-1: SnakeAI damage = 0 ==========
        // SnakeAI.Update applies HealthHandler.TakeDamage(5f * damageMultiplier).
        // Set damageMultiplier=0 in Awake postfix → no damage, snakes still alive
        // and animate normally (just harmless).
        private void ApplySnakeDamageZero()
        {
            var snakeType = AccessTools.TypeByName("SnakeAI");
            if ((object)snakeType == null) { Log.LogInfo("SnakeAI not present — skipping."); return; }
            var awake = AccessTools.Method(snakeType, "Awake");
            if ((object)awake == null) { Log.LogWarning("SnakeAI.Awake not found."); return; }
            var postfix = AccessTools.Method(typeof(Plugin), nameof(SnakeAwake_Postfix));
            _harmony.Patch(awake, postfix: new HarmonyMethod(postfix));
            Log.LogInfo("[SNAKES-1] Patched SnakeAI.Awake — damage neutralized.");
        }
        private static void SnakeAwake_Postfix(object __instance)
        {
            try
            {
                var f = AccessTools.Field(__instance.GetType(), "damageMultiplier");
                if ((object)f != null) f.SetValue(__instance, 0f);
                // Also zero the push force so they don't push players around violently
                var fp = AccessTools.Field(__instance.GetType(), "playerForceMultiplier");
                if ((object)fp != null) fp.SetValue(__instance, 0f);
                var fpu = AccessTools.Field(__instance.GetType(), "playerForceMultiplierUp");
                if ((object)fpu != null) fpu.SetValue(__instance, 0f);
            }
            catch (Exception e) { Log.LogWarning($"[SNAKES-1 awake] {e.Message}"); }
        }

        // ========== DEATH-1: Reduce RoundMinPlaySec ==========
        // SFHeadlessHost has `internal static float RoundMinPlaySec = 12f`. This
        // blocks round advance for 12s after match start. If a player dies in
        // the first 2-3 seconds, the round still waits until 12s elapse.
        // Reduce to 2f for snappy round advance.
        private void ApplyFastRoundAdvance()
        {
            var hostType = AccessTools.TypeByName("SFHeadlessHost.Plugin");
            if ((object)hostType == null) return;
            var f = AccessTools.Field(hostType, "RoundMinPlaySec");
            if ((object)f == null) { Log.LogWarning("RoundMinPlaySec field not found."); return; }
            try
            {
                float current = (float)f.GetValue(null);
                f.SetValue(null, 0f);
                Log.LogInfo($"[DEATH-1] RoundMinPlaySec: {current}s → 0s (no post-map kill gate).");
            }
            catch (Exception e) { Log.LogWarning($"[DEATH-1] {e.Message}"); }
        }

        // (D1/D2 code-review) Removed DEATH-2 (redundant — SFHeadlessHost already
        // postfixes HealthHandler.Die, and runs in the same oracle process) and
        // DEATH-3 (dead — it patched 0 methods: Kill/DealDamage/SetDead/OnDeath
        // don't exist on the real HealthHandler/CharacterInformation, and
        // KillingFloor has no OnTriggerEnter; its postfix also read the wrong field
        // name). Death-driven round advance lives entirely in SFHeadlessHost (Die
        // postfix + PktPlayerFallOut + killing-blow relay + auth-rig death poll).

        // BOX-ROT note: the NSO snapshot wire format only syncs rotZ (the
        // camera-facing axis). If server physics tumbled a box around X/Y the
        // clients would never see it ("cajas giran porque sí"). ApplyCrateConstraints
        // (in the crate engine below) freezes X/Y rotation and forces Z free, so
        // server physics only rotates in the synced plane.

        // ========== CAJAS-3 + MAPA-AZUL: Unfreeze Christmas/Halloween kinematic NSOs ==========
        // Some level prefabs ship NSO rigidbodies as kinematic by default. Vanilla
        // expects host to flip them dynamic after settle. If oracle's settle path
        // doesn't catch a particular prefab (e.g., Xmas crates, blue boxes), they
        // stay kinematic and feel "hiper-rigidas" / can't be pushed.
        //
        // Fix: 4s after every scene load, walk all NSO rigidbodies with simple
        // destructibles, flip kinematic=false if mass > 50 (real boxes), useGravity=true.
        private void OnSceneLoadedUnfreezeXmas(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "MainScene") return;
            // ALWAYS schedule a post-settle pass — needed for BOX-ROT (freeze
            // X/Y rotation on all pushable crates). Unfreeze pass inside is
            // gated to Xmas scenes only.
            _pendingUnfreezeScene = scene;
            _pendingUnfreezeAt = Time.realtimeSinceStartup + 2f;
        }

        private void Update()
        {
            if (_pendingUnfreezeAt > 0f && Time.realtimeSinceStartup >= _pendingUnfreezeAt)
            {
                _pendingUnfreezeAt = -1f;
                try { ConfigureSceneCrates(_pendingUnfreezeScene); }
                catch (Exception e) { Log.LogWarning($"[CRATES] scene config failed: {e.Message}"); }
            }
        }

        // ====================================================================
        //  SERVER-AUTHORITATIVE CRATE ENGINE (vanilla-feel, single source of truth)
        //
        //  The oracle/host simulates every pushable crate; clients just render the
        //  NSO snapshots. So ALL crate physics lives HERE: body configuration at
        //  map load, then per-FixedUpdate velocity governance + edge tipping. No
        //  client-side crate physics is required for this to be correct (the
        //  client push-mode is a separate, optional smoothing layer).
        //
        //  Design goals (per user): keep it VANILLA-simple — trust PhysX for the
        //  motion, and only (a) configure bodies so they tip/stack naturally and
        //  (b) cap pathological velocities (bullet fling) that the snapshot wire
        //  format would otherwise broadcast to everyone as "cajas volando".
        // ====================================================================

        // ── Velocity governance (axis-aware; Y is vertical in SF) ──────────────
        // Horizontal: STRICT cap for pure-horizontal motion (bullet fling), a
        // higher cap when there's a vertical component (explosions throw crates).
        // Vertical: cap upward impulse but allow a natural downward fall.
        private const float CrateMaxHoriz      = 6.0f;   // player-push cap (m/s) — aligned with client
        private const float CrateMaxHorizBlast = 14.0f;  // explosion cap (m/s)
        private const float CrateVertTrigger   = 2.0f;   // |v.y| above this ⇒ treat as blast
        private const float CrateMaxUp         = 9.0f;   // upward impulse cap
        private const float CrateMaxFall       = 30.0f;  // terminal fall cap

        private const float CrateMinConfigMass = 1f;     // only skip massless markers; configure real crates

        // The set of crate rigidbodies the governor manages this scene.
        private static readonly List<Rigidbody> _crateRbs = new List<Rigidbody>();
        // The whole crate authority is two cheap passes per managed body: clamp
        // the velocity (so the snapshot never broadcasts a bullet-fling launch)
        // and, when near rest, nudge an overhanging crate so it actually topples.
        private void FixedUpdate()
        {
            if (_crateRbs.Count == 0) return;
            for (int i = _crateRbs.Count - 1; i >= 0; i--)
            {
                var rb = _crateRbs[i];
                if ((object)rb == null) { _crateRbs.RemoveAt(i); continue; }   // prune destroyed
                if (rb.isKinematic) continue;                                  // floating/parked crate
                GovernCrateVelocity(rb);
                // v0.3.0 — OverhangAssist retired. Its probes sampled the depth
                // axis (X) so it had never actually fired; fixing the axis made
                // it torque every partially-supported crate forever — crates
                // rocked, never slept, and streamed endless corrections to
                // clients. With vanilla prefab mass/CoM (no longer overridden),
                // crates topple naturally when they should.
            }
        }

        // Axis-aware velocity cap. Horizontal motion is capped strictly unless the
        // crate is clearly in a blast (vertical component present), in which case a
        // higher cap lets explosions throw it. Downward fall is left near-natural.
        private static void GovernCrateVelocity(Rigidbody rb)
        {
            Vector3 v = rb.velocity;
            bool isBlast = Mathf.Abs(v.y) > CrateVertTrigger;
            float hCap   = isBlast ? CrateMaxHorizBlast : CrateMaxHoriz;
            bool changed = false;

            float hMagSqr = v.x * v.x + v.z * v.z;
            if (hMagSqr > hCap * hCap)
            {
                float s = hCap / Mathf.Sqrt(hMagSqr);
                v.x *= s; v.z *= s; changed = true;
            }
            if (v.y > CrateMaxUp) { v.y = CrateMaxUp; changed = true; }
            else if (v.y < -CrateMaxFall) { v.y = -CrateMaxFall; changed = true; }

            if (changed) rb.velocity = v;
        }

        // Walk every NSO in the freshly-loaded scene, find the pushable crates and
        // hand each rigidbody to ConfigureCrateBody. Rebuilds the governed set so
        // FixedUpdate only iterates this scene's crates. Called ~2s after load
        // (post-settle) from Update(); MainScene is already excluded by the caller.
        private static void ConfigureSceneCrates(Scene scene)
        {
            var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
            if ((object)nsoType == null) return;
            var all = UnityEngine.Object.FindObjectsOfType(nsoType);
            if (all == null) return;

            _crateRbs.Clear();
            int configured = 0;
            foreach (var nso in all)
            {
                var comp = nso as Component;
                if ((object)comp == null) continue;
                if (comp.gameObject.scene.buildIndex != scene.buildIndex) continue;
                if (!IsPushableCrate(comp)) continue;

                var rbs = comp.GetComponentsInChildren<Rigidbody>();
                if (rbs == null) continue;
                foreach (var rb in rbs)
                    if (ConfigureCrateBody(rb)) configured++;
            }
            if (configured > 0)
                Log.LogInfo($"[CRATES] Configured {configured} server-authoritative crate bodies in '{scene.name}'.");
        }

        // A crate root is "pushable" when it carries a DestructiblePiece that is a
        // SIMPLE (non-event) destruction — the same test the rest of the kit uses.
        private static bool IsPushableCrate(Component comp)
        {
            if ((object)_dpType == null) return false;
            var dps = comp.GetComponentsInChildren(_dpType);
            if (dps == null || dps.Length == 0) return false;
            foreach (var dp in dps)
            {
                bool simple = (object)_dpSimpleField != null && (bool)_dpSimpleField.GetValue(dp);
                bool ev = (object)_dpEventField != null && (bool)_dpEventField.GetValue(dp);
                if (simple && !ev) return true;
            }
            return false;
        }

        // Configure one crate rigidbody and register it for velocity governance.
        // Returns true if it was a real crate body we configured. We DO NOT touch
        // isKinematic: a floating/parked crate stays exactly as the map shipped it
        // and only joins live physics when the game activates it (hit / event) —
        // configuring it ahead of time means it then falls + tips naturally.
        private static bool ConfigureCrateBody(Rigidbody rb)
        {
            if ((object)rb == null) return false;
            // Preserve jointed props (barrels on chains, swinging crates, etc.).
            if ((object)rb.GetComponent<Joint>() != null) return false;
            if ((object)rb.GetComponent<ConfigurableJoint>() != null) return false;
            if ((object)rb.GetComponent<HingeJoint>() != null) return false;

            // Register EVERY rigidbody (incl. light sub-pieces) for the velocity
            // clamp — a bullet's impulse lands on whichever piece it hits.
            if (!_crateRbs.Contains(rb)) _crateRbs.Add(rb);

            // Skip massless markers/sensors; configure everything with real mass.
            if (rb.mass < CrateMinConfigMass) return false;

            // v0.3.0 — VANILLA-FIRST: mass / CoM / inertia / drag / materials are
            // NOT overridden anymore. Stock crates are heavy (some prefabs ship
            // mass ≈ 1500); our 45-mass override made players shove them around
            // "like they are nothing" on every sim. Keeping prefab values is both
            // perfect client↔server parity (neither side touches them) and the
            // vanilla feel. Only what sync REQUIRES is configured: the unified
            // constraint mask and tunneling-safe collision detection.
            ApplyCrateConstraints(rb);
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            return true;
        }

        // GROUND-TRUTH FIX (v0.3.0): stock SF syncs NSO rotation as the
        // up-vector's (y,z) — the up vector tilts within the Y-Z play plane,
        // i.e. crates tip about world X (LerpLocalDummy reconstructs
        // LookRotation(Cross(Vector3.right, up), up)). The previous mask froze
        // X and freed Z on the belief that "the wire syncs rotZ" — true only
        // of our own v26 field, not of vanilla — so the server tipped crates
        // about the into-the-screen axis while clients tip about X:
        // permanently divergent orientation between the authority sim and the
        // predicted sims. Unified mask (mirrored in SFClientRecon): free X
        // (the visible tip axis, now carried on the wire as up-vector y/z),
        // freeze Y (yaw — unsyncable, so locked identically on both sims) and
        // Z (vanilla crate prefabs ship Z frozen).
        private static void ApplyCrateConstraints(Rigidbody rb)
        {
            var c = (rb.constraints
                     | RigidbodyConstraints.FreezeRotationY
                     | RigidbodyConstraints.FreezeRotationZ)
                    & ~RigidbodyConstraints.FreezeRotationX;
            rb.constraints = c;
        }

    }
}
