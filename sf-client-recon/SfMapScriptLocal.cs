using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // ===================================================================
    //  MAP-SCRIPT LOCAL DRIVE  (per-type, surgical)
    // -------------------------------------------------------------------
    //  Map gimmick objects (MapInfoSyncableBase subclasses) gate their motion /
    //  toggle logic behind `MultiplayerManager.IsServer` or the static
    //  `m_NetworkControl` (= mHasSentOrReceived && IsServer). On a client both are
    //  FALSE, so the client never advances a moving platform's target nor toggles
    //  a ghost block — it depends entirely on the host's SetData stream, which on
    //  our oracle is sparse/lossy → platforms got stuck pushing toward target 0
    //  and ghost blocks toggled collision late / without the visual.
    //
    //  FIRST ATTEMPT flipped the *global static* m_NetworkControl=true. That was
    //  too broad: on crate-grid maps EVERY structural MapInfoSyncableBase block
    //  reacted and they scattered/jumped at spawn. REVERTED.
    //
    //  This version is SURGICAL: it transpiles ONLY the Update() of the three
    //  gimmick types so that, *inside those methods only*, the IsServer call and
    //  the m_NetworkControl field read evaluate to `true`. Every other map object
    //  is untouched. The gimmicks then run their own deterministic local script
    //  (smooth, no network dependency). SyncMapData (outbound) is neutralised, and
    //  inbound SetData for these self-driven types is ignored (see
    //  ApplyMapStateSnapshot) so the server stream can't fight the local sim.
    //  Only loaded on the client (SFClientRecon) — the host is unaffected.
    // ===================================================================
    public partial class Plugin
    {
        private static bool _mapScriptLocalInstalled;
        private static readonly HashSet<Type> _selfDrivenMapTypes = new HashSet<Type>();

        private void InstallMapScriptLocalPatches()
        {
            DetectOracleConnectMode();
            if (!_oracleConnectMode || _mapScriptLocalInstalled) return;
            _mapScriptLocalInstalled = true;
            try
            {
                var harmony = new Harmony(PluginGuid + ".map-local");
                int patched = 0;

                // Per-type: transpile each gimmick's Update so its server/network
                // gate reads true → it self-drives locally.
                patched += PatchMapTypeUpdate(harmony, "MoveAlongPathUsingForce");
                patched += PatchMapTypeUpdate(harmony, "PillarHandler");
                patched += PatchMapTypeUpdate(harmony, "GhostPlatform");

                // Neutralise outbound state send (client owns these locally now).
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                var syncMapData = (object)mmType != null ? AccessTools.Method(mmType, "SyncMapData") : null;
                if ((object)syncMapData != null)
                {
                    harmony.Patch(syncMapData, prefix: new HarmonyMethod(typeof(Plugin), nameof(SyncMapData_SkipPrefix)));
                    patched++;
                }

                Log.LogInfo($"[map-local] Gimmick scripts run locally, per-type ({patched} patch(es)). Structural blocks untouched.");
            }
            catch (Exception e) { Log.LogWarning($"[map-local] install failed: {e.Message}"); }
        }

        private int PatchMapTypeUpdate(Harmony harmony, string typeName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if ((object)t == null) return 0;
                var update = AccessTools.Method(t, "Update");
                if ((object)update == null) return 0;
                harmony.Patch(update, transpiler: new HarmonyMethod(typeof(Plugin), nameof(ForceMapScriptLocalTranspiler)));
                _selfDrivenMapTypes.Add(t);   // so ApplyMapStateSnapshot ignores its SetData
                return 1;
            }
            catch (Exception e) { Log.LogWarning($"[map-local] patch {typeName}: {e.Message}"); return 0; }
        }

        // True when this component is one of the gimmick types we now drive locally
        // — ApplyMapStateSnapshot skips its SetData so the server can't fight us.
        internal static bool IsSelfDrivenMapObject(Component c)
        {
            if ((object)c == null || _selfDrivenMapTypes.Count == 0) return false;
            var ct = c.GetType();
            foreach (var t in _selfDrivenMapTypes)
                if (t.IsAssignableFrom(ct)) return true;
            return false;
        }

        internal static bool SyncMapData_SkipPrefix() { return false; }

        // Inside the patched Update only: make `MultiplayerManager.IsServer` and the
        // static `MapInfoSyncableBase.m_NetworkControl` both read `true`, so the
        // self-drive branch runs on the client. Stack-neutral: a static getter call
        // (no args) and a static field load each push one value — replacing either
        // with ldc.i4.1 pushes the same one bool.
        internal static IEnumerable<CodeInstruction> ForceMapScriptLocalTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo isServerGetter = null;
            FieldInfo netCtrlField = null;
            try
            {
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType != null) isServerGetter = AccessTools.PropertyGetter(mmType, "IsServer");
                var baseType = AccessTools.TypeByName("MapInfoSyncableBase");
                if ((object)baseType != null) netCtrlField = AccessTools.Field(baseType, "m_NetworkControl");
            }
            catch { }

            var codes = new List<CodeInstruction>(instructions);
            int n = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                var c = codes[i];
                bool isIsServer = (object)isServerGetter != null
                    && (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt)
                    && (object)(c.operand as MethodInfo) == (object)isServerGetter;
                bool isNetCtrl = (object)netCtrlField != null
                    && c.opcode == OpCodes.Ldsfld
                    && (object)(c.operand as FieldInfo) == (object)netCtrlField;
                if (isIsServer || isNetCtrl)
                {
                    c.opcode = OpCodes.Ldc_I4_1;
                    c.operand = null;
                    n++;
                }
            }
            if (n > 0) Log.LogInfo($"[map-local] Update gate forced local ({n} site(s)).");
            return codes;
        }
    }
}
