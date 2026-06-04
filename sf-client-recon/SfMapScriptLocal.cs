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
                // gate reads true → it self-drives locally. ONLY deterministic,
                // time-driven gimmicks belong here (so both clients compute the
                // same motion → no divergence). Non-deterministic ones (random
                // object enabling, player-triggered levers) must stay server-fed.
                patched += PatchMapTypeMethod(harmony, "MoveAlongPathUsingForce", "Update");
                patched += PatchMapTypeMethod(harmony, "PillarHandler", "Update");
                patched += PatchMapTypeMethod(harmony, "GhostPlatform", "Update");

                // EXTENSIBLE: add more frozen gimmick types WITHOUT a recompile via
                // SF_MAP_LOCAL_TYPES="TypeA,TypeB:Start,TypeC". Each entry is a type
                // name, optionally with the method to patch (defaults to Update).
                // Use this to self-drive the exact gimmick that's frozen on a map
                // (e.g. a rotating bar) once you know its class — the transpiler
                // forces any IsServer / m_NetworkControl gate inside it to true.
                try
                {
                    string extra = Environment.GetEnvironmentVariable("SF_MAP_LOCAL_TYPES");
                    if (!string.IsNullOrEmpty(extra))
                        foreach (var tok in extra.Split(','))
                        {
                            var raw = tok.Trim();
                            if (raw.Length == 0) continue;
                            string typeName = raw, method = "Update";
                            int colon = raw.IndexOf(':');
                            if (colon > 0) { typeName = raw.Substring(0, colon).Trim(); method = raw.Substring(colon + 1).Trim(); }
                            int added = PatchMapTypeMethod(harmony, typeName, method);
                            patched += added;
                            Log.LogInfo($"[map-local] SF_MAP_LOCAL_TYPES: {typeName}.{method} → {(added > 0 ? "patched" : "NOT FOUND")}.");
                        }
                }
                catch (Exception e) { Log.LogWarning($"[map-local] SF_MAP_LOCAL_TYPES: {e.Message}"); }

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

        private int PatchMapTypeMethod(Harmony harmony, string typeName, string methodName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if ((object)t == null) return 0;
                var m = AccessTools.Method(t, methodName);
                if ((object)m == null) return 0;
                harmony.Patch(m, transpiler: new HarmonyMethod(typeof(Plugin), nameof(ForceMapScriptLocalTranspiler)));
                _selfDrivenMapTypes.Add(t);   // so ApplyMapStateSnapshot ignores its SetData
                return 1;
            }
            catch (Exception e) { Log.LogWarning($"[map-local] patch {typeName}.{methodName}: {e.Message}"); return 0; }
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
            // Match by NAME, not a specific resolved member, so this works for any
            // gimmick regardless of WHICH IsServer it reads (MultiplayerManager.
            // IsServer, MatchMakingHandlerSockets.IsServer, or a class-local
            // IsServer property) and for the static m_NetworkControl flag. Applied
            // only to the gimmick methods we explicitly patch, so forcing the
            // server-side branch on the client is exactly the intent.
            var codes = new List<CodeInstruction>(instructions);
            int n = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                var c = codes[i];
                bool isIsServer = (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt)
                    && c.operand is MethodInfo mi && mi.Name == "get_IsServer";
                bool isNetCtrl = c.opcode == OpCodes.Ldsfld
                    && c.operand is FieldInfo fi && fi.Name == "m_NetworkControl";
                if (isIsServer || isNetCtrl)
                {
                    c.opcode = OpCodes.Ldc_I4_1;
                    c.operand = null;
                    n++;
                }
            }
            if (n > 0) Log.LogInfo($"[map-local] gate forced local ({n} site(s)).");
            return codes;
        }
    }
}
