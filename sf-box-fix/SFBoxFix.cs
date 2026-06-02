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
    // SFBoxFix v0.2.0 — Multi-fix server-side companion plugin to v0.3.8.
    // Coexists with kit's SFHeadlessHost without modifying its binary.
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
        public const string PluginVersion = "0.2.4";

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
            try { ApplyInstantDeathDetection();  } catch (Exception e) { Log.LogError($"[DEATH-2] {e.Message}"); }
            try { ApplyMoreDeathPaths();         } catch (Exception e) { Log.LogError($"[DEATH-3] {e.Message}"); }
            try { ApplyBoxRotationFreeze();      } catch (Exception e) { Log.LogError($"[BOX-ROT] {e.Message}"); }

            // Scene-level pass via SceneManager event — no coroutine, just a
            // deferred timer in Update() to wait for settle.
            SceneManager.sceneLoaded += OnSceneLoadedUnfreezeXmas;
        }

        private static Harmony _harmony;
        private static object _cachedHostPlugin;
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

        // ========== DEATH-2: Instant death detection — bypass roundAdvanceBlocked ==========
        // When a player dies, vanilla HealthHandler fires OnDeath events. Oracle
        // gates round advance behind _roundAdvanceBlockedUntil. If a death happens
        // before the gate clears, the round waits — feels laggy.
        //
        // Fix: postfix CharacterInformation property `isDead = true` setter (the
        // canonical death event in SF). When we see it, force the oracle's
        // _roundAdvanceBlockedUntil to NOW so the next advance check fires
        // immediately. Doesn't change WHO died — just clears the timer.
        private void ApplyInstantDeathDetection()
        {
            var ciType = AccessTools.TypeByName("CharacterInformation");
            if ((object)ciType == null) { Log.LogInfo("CharacterInformation not found, DEATH-2 skipped."); return; }

            // SF's CharacterInformation has `isDead` field. Watch for SetDead /
            // similar method. Most likely `CharacterStats.Die` or
            // `HealthHandler.Die` is the actual entry point.
            var hhType = AccessTools.TypeByName("HealthHandler");
            if ((object)hhType == null) { Log.LogInfo("HealthHandler not found, DEATH-2 skipped."); return; }

            var dieMethod = AccessTools.Method(hhType, "Die");
            if ((object)dieMethod == null) dieMethod = AccessTools.Method(hhType, "OnDeath");
            if ((object)dieMethod == null) { Log.LogInfo("HealthHandler.Die not found, DEATH-2 skipped."); return; }

            var postfix = AccessTools.Method(typeof(Plugin), nameof(OnPlayerDied_Postfix));
            _harmony.Patch(dieMethod, postfix: new HarmonyMethod(postfix));
            Log.LogInfo("[DEATH-2] Patched HealthHandler.Die — clears roundAdvanceBlocked on death.");
        }

        private static void OnPlayerDied_Postfix(object __instance)
        {
            try
            {
                var hostType = AccessTools.TypeByName("SFHeadlessHost.Plugin");
                if ((object)hostType == null) return;
                if ((object)_cachedHostPlugin == null)
                    _cachedHostPlugin = UnityEngine.Object.FindObjectOfType(hostType);
                if ((object)_cachedHostPlugin == null) return;
                var onDeath = AccessTools.Method(hostType, "ScheduleRoundAdvanceOnDeath");
                if ((object)onDeath != null)
                {
                    onDeath.Invoke(_cachedHostPlugin, new object[] { "SFBoxFix.HealthHandler.Die" });
                    Log.LogInfo("[DEATH-2] Player died — host ScheduleRoundAdvanceOnDeath invoked.");
                    return;
                }
                var clear = AccessTools.Method(hostType, "ClearRoundAdvanceBlockedGate");
                if ((object)clear != null)
                    clear.Invoke(_cachedHostPlugin, new object[] { "SFBoxFix.HealthHandler.Die" });
            }
            catch (Exception e) { Log.LogWarning($"[DEATH-2 postfix] {e.Message}"); }
        }

        // ========== DEATH-3: Patch additional death entry points ==========
        // Death in SF can fire through several methods. Patch them all so the
        // round-advance gate clears no matter which path triggers.
        private void ApplyMoreDeathPaths()
        {
            int patched = 0;
            var hhType = AccessTools.TypeByName("HealthHandler");
            var ciType = AccessTools.TypeByName("CharacterInformation");

            // HealthHandler death-related methods. TakeDamage has overloads
            // so we patch ALL of them by enumerating MethodInfos via reflection
            // and filtering by name. Skip TakeDamage (multiple overloads cause
            // ambiguity errors and we already have DEATH-2 covering Die).
            if ((object)hhType != null)
            {
                foreach (var m in hhType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if ((object)m == null) continue;
                    string n = m.Name;
                    if (n != "Kill" && n != "DealDamage" && n != "SetDead" && n != "OnDeath") continue;
                    try
                    {
                        var pf = AccessTools.Method(typeof(Plugin), nameof(OnPlayerDamageOrDeath_Postfix));
                        _harmony.Patch(m, postfix: new HarmonyMethod(pf));
                        patched++;
                    }
                    catch { }
                }
            }
            // CharacterInformation death methods
            if ((object)ciType != null)
            {
                foreach (var m in ciType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if ((object)m == null) continue;
                    string n = m.Name;
                    if (n != "Die" && n != "OnDeath" && n != "SetDead") continue;
                    try
                    {
                        var pf = AccessTools.Method(typeof(Plugin), nameof(OnPlayerDamageOrDeath_Postfix));
                        _harmony.Patch(m, postfix: new HarmonyMethod(pf));
                        patched++;
                    }
                    catch { }
                }
            }
            // Killbox triggers
            var kfType = AccessTools.TypeByName("KillingFloor");
            if ((object)kfType != null)
            {
                var m = AccessTools.Method(kfType, "OnTriggerEnter");
                if ((object)m != null)
                {
                    try
                    {
                        var pf = AccessTools.Method(typeof(Plugin), nameof(OnPlayerDamageOrDeath_Postfix));
                        _harmony.Patch(m, postfix: new HarmonyMethod(pf));
                        patched++;
                    }
                    catch { }
                }
            }
            Log.LogInfo($"[DEATH-3] Patched {patched} additional death entry points.");
        }

        // Common postfix used by all death paths — only clears the gate if
        // somebody actually died. Cheap: a single field read + write.
        private static void OnPlayerDamageOrDeath_Postfix(object __instance)
        {
            try
            {
                if ((object)__instance == null) return;
                var ciF = AccessTools.Field(__instance.GetType(), "characterInformation");
                if ((object)ciF == null)
                    ciF = AccessTools.Field(__instance.GetType(), "mCharacterInformation");
                if ((object)ciF != null)
                {
                    var ci = ciF.GetValue(__instance);
                    if ((object)ci != null)
                    {
                        var deadF = AccessTools.Field(ci.GetType(), "isDead");
                        if ((object)deadF != null && !(bool)deadF.GetValue(ci)) return;
                    }
                }
                var hostType = AccessTools.TypeByName("SFHeadlessHost.Plugin");
                if ((object)hostType == null) return;
                if ((object)_cachedHostPlugin == null)
                    _cachedHostPlugin = UnityEngine.Object.FindObjectOfType(hostType);
                if ((object)_cachedHostPlugin == null) return;
                var onDeath = AccessTools.Method(hostType, "ScheduleRoundAdvanceOnDeath");
                if ((object)onDeath != null)
                    onDeath.Invoke(_cachedHostPlugin, new object[] { "SFBoxFix.death-path" });
                else
                {
                    var clear = AccessTools.Method(hostType, "ClearRoundAdvanceBlockedGate");
                    if ((object)clear != null)
                        clear.Invoke(_cachedHostPlugin, new object[] { "SFBoxFix.death-path" });
                }
            }
            catch { /* silent */ }
        }

        // ========== BOX-ROT: Freeze box rotation to X/Y axes ==========
        // v0.3.9's NSO snapshot wire format only syncs rotZ (the camera-facing
        // axis). When server physics tumbles a box around X or Y (e.g., from
        // off-center collision impulse), the rotation is NEVER sent to clients
        // — they only see the Z component, which makes the box appear to spin
        // randomly. User reported: "cajas giran porque si".
        //
        // Fix: freeze rotation on X+Y axes for pushable crates' rigidbodies
        // immediately after settle, so server physics only produces Z rotation
        // — which IS sync'd. Net: box rotation matches between server and clients.
        private static Type _rbConstraintsType;
        private void ApplyBoxRotationFreeze()
        {
            // Frame-driven on scene load instead of patching every NSO Awake
            // (would be expensive). Triggered via Update() timer like unfreeze.
            // Implemented inside UnfreezeAllPushableNsos — see below.
            Log.LogInfo("[BOX-ROT] Will freeze X/Y rotation on pushable crates post-settle (4s).");
        }

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
                float fireAt = _pendingUnfreezeAt;
                _pendingUnfreezeAt = -1f;
                try { UnfreezeAllPushableNsos(_pendingUnfreezeScene); }
                catch (Exception e) { Log.LogWarning($"[CAJAS-3 unfreeze] {e.Message}"); }
            }
        }

        // Server-authoritative crate velocity cap. SF bullets impart a big
        // (mass-independent) impulse on hit → the server crate launches and every
        // client (which just follows the server) sees it "salir volando". Capping
        // the crate's velocity each physics step on the SERVER stops the launch at
        // the source. A deliberate push stays under the cap; explosions stay near
        // it. Runs in FixedUpdate so it catches the impulse the same step.
        // Axis-aware caps (Y = vertical in SF). Cap HORIZONTAL (X/Z — bullet fling
        // direction) and UPWARD impulse, but allow a natural downward fall so
        // crates don't descend in slow motion. (A single magnitude cap throttled
        // the fall speed → "caen en camara lenta".)
        private const float CrateMaxHorizSrv = 3.5f;
        private const float CrateMaxUpSrv    = 4.0f;
        private const float CrateMaxFallSrv  = 30.0f;
        private static readonly List<Rigidbody> _crateRbs = new List<Rigidbody>();
        private void FixedUpdate()
        {
            if (_crateRbs.Count == 0) return;
            float hSqr = CrateMaxHorizSrv * CrateMaxHorizSrv;
            for (int i = _crateRbs.Count - 1; i >= 0; i--)
            {
                var rb = _crateRbs[i];
                if ((object)rb == null) { _crateRbs.RemoveAt(i); continue; }
                if (rb.isKinematic) continue;
                var v = rb.velocity;
                bool changed = false;
                float hMagSqr = v.x * v.x + v.z * v.z;
                if (hMagSqr > hSqr) { float s = CrateMaxHorizSrv / Mathf.Sqrt(hMagSqr); v.x *= s; v.z *= s; changed = true; }
                if (v.y > CrateMaxUpSrv) { v.y = CrateMaxUpSrv; changed = true; }
                else if (v.y < -CrateMaxFallSrv) { v.y = -CrateMaxFallSrv; changed = true; }
                if (changed) rb.velocity = v;
            }
        }

        // High-friction, no-bounce material so stacked crates grip instead of
        // sliding off each other. Shared single instance (don't leak per-collider).
        private static PhysicMaterial _gripMaterial;
        private static PhysicMaterial GripMaterial
        {
            get
            {
                if ((object)_gripMaterial == null)
                {
                    _gripMaterial = new PhysicMaterial("CrateGrip")
                    {
                        staticFriction = 0.6f,
                        dynamicFriction = 0.55f,
                        bounciness = 0f,
                        frictionCombine = PhysicMaterialCombine.Minimum,
                        bounceCombine = PhysicMaterialCombine.Minimum
                    };
                }
                return _gripMaterial;
            }
        }

        private static void UnfreezeAllPushableNsos(Scene scene)
        {
            var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
            if ((object)nsoType == null) return;
            var all = UnityEngine.Object.FindObjectsOfType(nsoType);
            if (all == null) return;
            _crateRbs.Clear();   // rebuild the velocity-clamp set for this scene

            // BOX-ROT applies to ALL maps. Unfreeze (CAJAS-3) only to Xmas-style.
            string lower = scene.name.ToLowerInvariant();
            bool doUnfreeze = lower.Contains("xmas") || lower.Contains("christmas")
                           || lower.Contains("halloween") || lower.Contains("winter");

            int flipped = 0, rotFrozen = 0;
            foreach (var nso in all)
            {
                var comp = nso as Component;
                if ((object)comp == null) continue;
                if (comp.gameObject.scene.buildIndex != scene.buildIndex) continue;

                if ((object)_dpType == null) continue;
                var dps = comp.GetComponentsInChildren(_dpType);
                if (dps == null || dps.Length == 0) continue;

                bool anyPushable = false;
                foreach (var dp in dps)
                {
                    bool simple = (object)_dpSimpleField != null && (bool)_dpSimpleField.GetValue(dp);
                    bool ev = (object)_dpEventField != null && (bool)_dpEventField.GetValue(dp);
                    if (simple && !ev) { anyPushable = true; break; }
                }
                if (!anyPushable) continue;

                var rbs = comp.GetComponentsInChildren<Rigidbody>();
                if (rbs == null) continue;
                foreach (var rb in rbs)
                {
                    if ((object)rb == null) continue;

                    // Skip jointed (preserves barrels on chains etc).
                    if ((object)rb.GetComponent<Joint>() != null) continue;
                    if ((object)rb.GetComponent<ConfigurableJoint>() != null) continue;
                    if ((object)rb.GetComponent<HingeJoint>() != null) continue;

                    // Register EVERY crate rigidbody (incl. light sub-pieces) for
                    // the velocity clamp — a bullet's force lands on whichever piece
                    // it hits, and the light ones were the ones still flying.
                    if (!_crateRbs.Contains(rb)) _crateRbs.Add(rb);

                    // Weight/grip only for the main heavy bodies (mass>=50). Light
                    // sub-pieces keep their mass but are still velocity-capped above.
                    if (rb.mass < 50f) continue;

                    // BOX-ROT: freeze X+Y rotation. Server NSO snapshot only
                    // sends rotZ; without this freeze, server physics tumbles
                    // boxes around X/Y axes that clients never see → "cajas
                    // giran porque si". Now server physics only rotates in Z,
                    // which IS sync'd → boxes rotate consistently across clients.
                    // (Do NOT freeze position Z here — locking a box's current
                    // depth can trap two crates in different planes so they never
                    // collide and visually overlap/vanish. Vanilla box physics
                    // already keeps them coplanar; let it run.)
                    // Freeze X/Y rotation but FORCE Z FREE — the prefab freezes Z
                    // rotation, and a `|=` never unfroze it, so crates only slid
                    // and never tipped/tumbled. Clear the Z-rotation bit.
                    var prevConstraints = rb.constraints;
                    var newConstraints = (prevConstraints
                        | RigidbodyConstraints.FreezeRotationX
                        | RigidbodyConstraints.FreezeRotationY)
                        & ~RigidbodyConstraints.FreezeRotationZ;
                    if (prevConstraints != newConstraints)
                    {
                        rb.constraints = newConstraints;
                        rotFrozen++;
                    }

                    // BOX-GRIP: crates ship with near-zero friction → stacked
                    // boxes slide off each other and a tower can't hold ("se van
                    // empujando de a poquito"). Apply a high-friction material so
                    // they grip and stack. Also raise the rigidbody's sleep
                    // threshold so a settled stack actually goes to sleep instead
                    // of micro-jittering itself apart.
                    var cols = rb.GetComponentsInChildren<Collider>();
                    if (cols != null)
                        foreach (var col in cols)
                            // sharedMaterial, not material — `.material` clones a new
                            // PhysicMaterial per collider per round (memory leak).
                            if ((object)col != null && !col.isTrigger) col.sharedMaterial = GripMaterial;
                    // Very low sleep threshold + near-zero angular drag so a crate
                    // balanced on an edge keeps its tipping torque and TOPPLES /
                    // tumbles instead of sitting perched (matches client).
                    rb.sleepThreshold = 0.005f;
                    // Lower mass = responsive rotation/tipping (fling still bounded
                    // by the velocity clamp, not mass). Matches client.
                    rb.mass = 60f;
                    rb.drag = 0.45f;
                    rb.angularDrag = 0.05f;
                    rb.maxAngularVelocity = 7f;
                    // GRAVITY-TIP: high CoM + clean inertia tensor so a crate
                    // overhanging an edge actually pivots (matches client).
                    rb.ResetInertiaTensor();
                    rb.centerOfMass = new Vector3(0f, 1.0f, 0f);
                    rb.useGravity = true;

                    // NOTE: the old CAJAS-3 "unfreeze" (flip kinematic→dynamic on
                    // Xmas-style maps) is REMOVED. It fired ~2s after the scene
                    // loaded — i.e. while the player was already fighting — and
                    // dropped crates the map intentionally left floating/kinematic
                    // ("cajas en el aire caen porque sí" + "anda 3s y explota").
                    // We now NEVER force a crate dynamic: a kinematic/floating
                    // crate only starts moving when the game itself activates it
                    // (hit / destruction event). We only add grip + freeze X/Y rot.
                    _ = doUnfreeze;
                }
            }
            if (flipped > 0)
                Log.LogInfo($"[CAJAS-3] Unfroze {flipped} kinematic pushable NSO rigidbodies in scene '{scene.name}'.");
            if (rotFrozen > 0)
                Log.LogInfo($"[BOX-ROT] Froze X/Y rotation on {rotFrozen} pushable crate rigidbodies in '{scene.name}' (sync stays in Z plane).");
        }
    }
}
