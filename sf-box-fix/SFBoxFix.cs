using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SFBoxFix
{
    // Minimal Harmony patch plugin that runs ALONGSIDE SFHeadlessHost v0.3.9
    // without modifying its binary. Targets specific bugs:
    //
    // BUG 1 (CAJAS-1): IsExplosiveWeaponType treats speed<50f as explosive,
    //   so every bullet (slow projectile) triggers 900f blast force on boxes.
    //   FIX: prefix that returns only true for actual rocket/grenade weapon
    //   types (5-8), skipping the speed predicate.
    //
    // Loads on BOTH server and client (BepInEx loads all plugins). On client
    // it's a harmless no-op because the patched method only exists in the
    // server's SFHeadlessHost assembly. On server, it patches in-place.
    //
    // Designed to NEVER break what works: if the target method/type isn't
    // found, this plugin silently does nothing.

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.stickfightdev.headless-host", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.box-fix";
        public const string PluginName = "SFBoxFix";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} starting up.");

            try
            {
                ApplyExplosiveWeaponTypeFix();
            }
            catch (Exception e)
            {
                Log.LogError($"Patch failed (continuing without fix): {e.Message}");
            }
        }

        private void ApplyExplosiveWeaponTypeFix()
        {
            // SFHeadlessHost.Plugin lives in its own assembly. Find via type
            // discovery rather than direct reference (would need to add the
            // dll as a build dep, brittle across versions).
            var hostType = AccessTools.TypeByName("SFHeadlessHost.Plugin");
            if ((object)hostType == null)
            {
                Log.LogInfo("SFHeadlessHost.Plugin not present — skipping (probably running on client).");
                return;
            }

            // Method is `private static bool IsExplosiveWeaponType(byte, float)`.
            var target = AccessTools.Method(hostType, "IsExplosiveWeaponType",
                new Type[] { typeof(byte), typeof(float) });
            if ((object)target == null)
            {
                Log.LogWarning("IsExplosiveWeaponType method not found on SFHeadlessHost.Plugin — patch skipped. Either method renamed or wrong host version.");
                return;
            }

            var prefix = AccessTools.Method(typeof(Plugin), nameof(IsExplosiveWeaponType_Prefix));
            var harmony = new Harmony(PluginGuid);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));

            Log.LogInfo("[CAJAS-1] Patched SFHeadlessHost.IsExplosiveWeaponType — bullets no longer trigger 900f blast on boxes.");
        }

        /// <summary>
        /// Replacement for vanilla SFHeadlessHost.IsExplosiveWeaponType.
        /// Original: returned true for speed<50 (any bullet) OR weaponType 5-8.
        /// Fix: only true for actual explosive weapon types (rocket/grenade types 5-8).
        /// Returns false → skips original method → caller gets our value.
        /// </summary>
        private static bool IsExplosiveWeaponType_Prefix(byte weaponType, float speed, ref bool __result)
        {
            __result = weaponType == 5 || weaponType == 6 || weaponType == 7 || weaponType == 8;
            return false; // skip original
        }
    }
}
