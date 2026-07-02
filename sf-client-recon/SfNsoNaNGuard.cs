using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Defensive guard against the NetworkSyncableObject NaN/+Inf "vanish"
    // cascade (boxes, platforms, explosive barrels, lasers disappearing on a
    // laggy link, eventually crashing the client).
    //
    // Credit: this failure mode and the three-part fix strategy were
    // identified by z7572's NaNFixer plugin (https://github.com/z7572/NaNFixer).
    // This is an independent reimplementation for client-recon — no upstream
    // code is copied (NaNFixer ships without a license).
    //
    // The bug, verified against our decompile (refs .../NetworkSyncableObject.cs).
    // SyncObjectState (the inbound object-sync handler) does:
    //     m_TimeBetweenPackages = Time.time - m_TimeOfLastPackage;   // :427
    //     m_PositionSpeed       = distance / m_TimeBetweenPackages;  // :431
    //     m_RotationSpeed       = angle    / m_TimeBetweenPackages;  // :433
    // When two sync packets for the same object arrive in one frame (packet
    // pile-up on a bad connection) the delta is ~0, so both speeds become
    // +Inf. The next LerpLocalDummy (:272) applies +Inf * deltaTime to the
    // transform, the object flies to infinity, and m_DistanceToTravel.normalized
    // turns NaN — self-perpetuating every frame, so it never recovers and the
    // NaN eventually crashes the client.
    //
    // Scope note: this only bites *clients* (mHasControl == false). Our
    // authoritative host forces the static NetworkSyncableObject.mHasControl =
    // true, which gates LerpLocalDummy off entirely (:239 requires !mHasControl),
    // so the host is NOT protected by this guard and its periodic +Inf must be
    // chased on a separate track. Every real player on the server is a client,
    // though, so this is where the fix earns its keep.
    //
    // Three guards, mirroring NaNFixer's structure:
    //   1) prefix  — on a same-frame burst, back-date the timestamp so the
    //                division sees a sane interval instead of ~0.
    //   2) postfix — replace a degenerate zero up-vector (which would make
    //                Quaternion.LookRotation in LerpLocalDummy return NaN) with
    //                Vector3.up.
    //   3) postfix — if the transform has already gone NaN, snap it back to the
    //                last legit network position (m_EndPos), reset rotation, and
    //                zero the interpolators so the next lerp stops re-poisoning.
    public partial class Plugin
    {
        private static Type _ngType;
        private static FieldInfo _ngTimeOfLastPackage;   // float
        private static FieldInfo _ngTimeBetweenPackages; // float
        private static FieldInfo _ngTargetAngle;         // Vector3
        private static FieldInfo _ngDistanceToTravel;    // Vector3
        private static FieldInfo _ngPositionSpeed;       // float
        private static FieldInfo _ngEndPos;              // Vector3
        private static bool _ngReady;

        // Bounded logging: a bad link can trip guard 1 many times a second, so
        // events are counted and summarised at most once per interval rather
        // than logged individually (don't turn a NaN storm into a log flood).
        private static float _ngNextLogAt;
        private static int _ngBurstFixes, _ngZeroVecFixes, _ngNanRescues;
        private const float NgLogInterval = 5f;
        private const float NgBurstThreshold = 0.001f;   // "same frame" delta / near-zero magnitude
        private const float NgFallbackInterval = 0.016f; // ~60fps, used when no prior interval exists

        private static bool NaNGuardDisabled()
        {
            string v = Environment.GetEnvironmentVariable("SF_NANGUARD");
            return v == "0" || (v != null && v.ToLowerInvariant() == "false");
        }

        // Runs unconditionally (not gated on oracle mode): it is pure safety
        // and only acts under degenerate burst/NaN conditions, so it also helps
        // in plain Steam lobbies. SF_NANGUARD=0 disables it.
        private void InstallNsoNaNGuardPatches()
        {
            if (NaNGuardDisabled())
            {
                Log.LogInfo("[nan-guard] disabled (SF_NANGUARD=0).");
                return;
            }
            try
            {
                _ngType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)_ngType == null)
                {
                    Log.LogWarning("[nan-guard] NetworkSyncableObject type not found; guard inactive.");
                    return;
                }

                var sync = AccessTools.Method(_ngType, "SyncObjectState");
                if ((object)sync == null)
                {
                    Log.LogWarning("[nan-guard] SyncObjectState method not found; guard inactive.");
                    return;
                }

                _ngTimeOfLastPackage   = AccessTools.Field(_ngType, "m_TimeOfLastPackage");
                _ngTimeBetweenPackages = AccessTools.Field(_ngType, "m_TimeBetweenPackages");
                _ngTargetAngle         = AccessTools.Field(_ngType, "m_TargetAngle");
                _ngDistanceToTravel    = AccessTools.Field(_ngType, "m_DistanceToTravel");
                _ngPositionSpeed       = AccessTools.Field(_ngType, "m_PositionSpeed");
                _ngEndPos              = AccessTools.Field(_ngType, "m_EndPos");

                // If any field is missing (a DLL that drifted from our decompile)
                // bail rather than half-patch and act on stale reads.
                if ((object)_ngTimeOfLastPackage == null || (object)_ngTimeBetweenPackages == null
                    || (object)_ngTargetAngle == null || (object)_ngDistanceToTravel == null
                    || (object)_ngPositionSpeed == null || (object)_ngEndPos == null)
                {
                    Log.LogWarning("[nan-guard] one or more NSO fields not found; guard inactive.");
                    return;
                }

                var harmony = new Harmony(PluginGuid + ".nan-guard");
                harmony.Patch(sync,
                    prefix:  new HarmonyMethod(typeof(Plugin), nameof(SyncObjectState_NaNGuardPrefix)),
                    postfix: new HarmonyMethod(typeof(Plugin), nameof(SyncObjectState_NaNGuardPostfix)));
                _ngReady = true;
                Log.LogInfo("[nan-guard] active — NetworkSyncableObject packet-burst / NaN protection installed "
                          + "(credit: z7572/NaNFixer). SF_NANGUARD=0 to disable.");
            }
            catch (Exception e) { Log.LogWarning($"[nan-guard] install failed: {e.Message}"); }
        }

        // Guard 1: same-frame packet burst. Read the *previous* timestamp before
        // the original overwrites it; if the delta is ~0, back-date it so the
        // original's m_TimeBetweenPackages (:427) resolves to a sane interval and
        // the speed divisions (:431/:433) never produce +Inf.
        internal static void SyncObjectState_NaNGuardPrefix(object __instance)
        {
            if (!_ngReady || (object)__instance == null) return;
            try
            {
                float lastPkt = (float)_ngTimeOfLastPackage.GetValue(__instance);
                if (lastPkt == 0f) return; // first packet: interval not used yet

                float sinceLast = Time.time - lastPkt;
                if (sinceLast < NgBurstThreshold)
                {
                    float prevInterval = (float)_ngTimeBetweenPackages.GetValue(__instance);
                    float safeInterval = prevInterval > NgBurstThreshold ? prevInterval : NgFallbackInterval;
                    _ngTimeOfLastPackage.SetValue(__instance, Time.time - safeInterval);
                    _ngBurstFixes++;
                }
            }
            catch { /* never let the guard break the packet pump */ }
        }

        // Guard 2 (zero up-vector) + Guard 3 (NaN transform rescue), applied
        // after the original has recomputed its interpolation targets.
        internal static void SyncObjectState_NaNGuardPostfix(object __instance)
        {
            if (!_ngReady || (object)__instance == null) return;
            try
            {
                // Guard 2: a zero-length target up-vector makes the
                // Quaternion.LookRotation in LerpLocalDummy (:274) undefined →
                // NaN. Force a valid up direction.
                Vector3 targetAngle = (Vector3)_ngTargetAngle.GetValue(__instance);
                if (targetAngle.sqrMagnitude < NgBurstThreshold)
                {
                    _ngTargetAngle.SetValue(__instance, Vector3.up);
                    _ngZeroVecFixes++;
                }

                // Guard 3: the transform was already poisoned by a previous
                // frame's lerp. Snap to the last legit network position and
                // clear the interpolators so it stops re-poisoning next frame.
                var comp = __instance as Component;
                if ((object)comp == null) { MaybeLogNaNGuard(); return; }
                Vector3 pos = comp.transform.position;
                Quaternion rot = comp.transform.rotation;
                if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)
                    || float.IsNaN(rot.x) || float.IsNaN(rot.y) || float.IsNaN(rot.z) || float.IsNaN(rot.w))
                {
                    Vector3 safe = (Vector3)_ngEndPos.GetValue(__instance);
                    comp.transform.position = safe;
                    comp.transform.rotation = Quaternion.identity;
                    _ngDistanceToTravel.SetValue(__instance, Vector3.zero);
                    _ngPositionSpeed.SetValue(__instance, 0f);
                    _ngNanRescues++;
                }

                MaybeLogNaNGuard();
            }
            catch { /* never let the guard break the packet pump */ }
        }

        private static void MaybeLogNaNGuard()
        {
            if (_ngBurstFixes == 0 && _ngZeroVecFixes == 0 && _ngNanRescues == 0) return;
            if (Time.time < _ngNextLogAt) return;
            _ngNextLogAt = Time.time + NgLogInterval;
            Log.LogWarning($"[nan-guard] guarded NSO instability in the last ~{NgLogInterval:F0}s: "
                         + $"{_ngBurstFixes} packet-burst div-by-zero, {_ngZeroVecFixes} zero-vector rotation, "
                         + $"{_ngNanRescues} NaN transform rescue(s).");
            _ngBurstFixes = _ngZeroVecFixes = _ngNanRescues = 0;
        }
    }
}
