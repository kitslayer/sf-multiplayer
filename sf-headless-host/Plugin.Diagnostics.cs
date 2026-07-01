using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFHeadlessHost
{
    public partial class Plugin
    {
        private void TickOracleDebugConsole()
        {
            if (!_batchModeHost) return;
            float now = Time.realtimeSinceStartup;
            if (_oracleDbgNextPollAt > 0f && now < _oracleDbgNextPollAt) return;
            _oracleDbgNextPollAt = now + 0.5f;
            try
            {
                if (!System.IO.File.Exists(OracleDbgCmdPath)) return;
                string text = System.IO.File.ReadAllText(OracleDbgCmdPath);
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
                System.IO.File.WriteAllText(OracleDbgCmdPath, "");   // consume
                string[] lines = text.Split('\n');
                foreach (var raw in lines)
                {
                    string cmd = raw.Trim();
                    if (cmd.Length == 0) continue;
                    if (cmd == "boxes") { OracleDumpBoxes(); continue; }
                    if (cmd == "rigs") { OracleDumpRigs(); continue; }
                    Log.LogInfo("[dbg] commands: boxes | rigs | help");
                }
            }
            catch (Exception e) { Log.LogWarning($"[dbg] poll error: {e.Message}"); }
        }

        private void OracleDumpBoxes()
        {
            EnsureNsoSrvCache();
            var sb = new System.Text.StringBuilder();
            sb.Append("[dbg] ORACLE BOXES id p=(y,z) v slp kin\n");
            int n = 0;
            foreach (var ent in _nsoSrvEntries)
            {
                if (!ent.Pushable) continue;
                var rb = ent.Rb;
                if ((object)rb == null || !ent.Comp) continue;
                Vector3 p;
                try { p = rb.position; } catch { continue; }
                sb.Append("  #").Append(ent.Id)
                  .Append(" p=(").Append(p.y.ToString("0.0")).Append(",").Append(p.z.ToString("0.0"))
                  .Append(") v=").Append(rb.velocity.magnitude.ToString("0.00"))
                  .Append(" slp=").Append(rb.IsSleeping() ? "1" : "0")
                  .Append(" kin=").Append(rb.isKinematic ? "1" : "0").Append("\n");
                n++;
            }
            sb.Append("[dbg] total ").Append(n).Append(" pushable crates (authority)");
            Log.LogInfo(sb.ToString());
        }

        private void OracleDumpRigs()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[dbg] ORACLE RIGS:\n");
            int n = 0;
            foreach (var kv in SlotToRig)
            {
                var rig = kv.Value;
                if ((object)rig == null) continue;
                sb.Append("  slot=").Append(kv.Key)
                  .Append(" pos=").Append(rig.transform.position.ToString("0.00")).Append("\n");
                n++;
            }
            sb.Append("[dbg] total ").Append(n).Append(" rigs");
            Log.LogInfo(sb.ToString());
        }
        private void TickBoxDiagnostic()
        {
            if (Time.realtimeSinceStartup - _boxDiagLastAt < 5f) return;
            _boxDiagLastAt = Time.realtimeSinceStartup;
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                int total = all != null ? all.Length : 0;
                int voided = 0;
                float yMin = float.MaxValue, yMax = float.MinValue;
                Component sample = null;
                if (all != null)
                {
                    foreach (var nso in all)
                    {
                        var comp = nso as Component;
                        if ((object)comp == null) continue;
                        if ((object)sample == null && IsPushableCrateNso(comp.gameObject)) sample = comp;
                        float y = comp.transform.position.y;
                        if (y < yMin) yMin = y;
                        if (y > yMax) yMax = y;
                        if (y < -30f) voided++;
                    }
                }
                if (total == 0) yMin = yMax = 0f;

                // P0-23 floor probe. Unity 5.6 has ONE shared physics world (no
                // per-scene physics, transforms auto-sync), so a downward raycast
                // that hits nothing means the map's floor colliders are NOT
                // registered server-side — the root cause of boxes falling to the
                // void. Probe map-center and a sample crate's X/Z; also census all
                // colliders + a sample crate's physics state (layer/kinematic/enabled)
                // so we can also catch "floor exists but box can't collide with it".
                var colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
                int colCount = colliders != null ? colliders.Length : 0;
                string floorCenter = ProbeFloorBelow(new Vector3(0f, 30f, 0f));
                string floorCrate = (object)sample != null
                    ? ProbeFloorBelow(new Vector3(sample.transform.position.x, 30f, sample.transform.position.z))
                    : "n/a";
                string crate = (object)sample != null ? DescribeNsoPhysics(sample) : "no-crate";
                Log.LogInfo($"[BOX-DIAG] nsos={total} void(y<-30)={voided} y=[{yMin:0.0},{yMax:0.0}] rigs={SlotToRig.Count} scene={SceneManager.GetActiveScene().name} colliders={colCount} gravityY={Physics.gravity.y:0.0} floor@center=[{floorCenter}] floor@crate=[{floorCrate}] crate={{{crate}}}");
            }
            catch (Exception e) { Log.LogWarning($"[BOX-DIAG] {e.Message}"); }
        }

        // Raycast straight down 200u from `from` and describe the first collider
        // hit — the definitive test for "is there a floor under the boxes on the
        // headless server?". "NONE" => no floor in the physics world (P0-23 root cause).
        private static string ProbeFloorBelow(Vector3 from)
        {
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 200f))
                return $"y={hit.point.y:0.0},obj='{hit.collider.gameObject.name}',layer={hit.collider.gameObject.layer},trig={hit.collider.isTrigger}";
            return "NONE(no floor!)";
        }

        // Sample crate physics state — if a floor IS found above but boxes still
        // fall, the box's own collider/layer/kinematic flag is the next suspect.
        private static string DescribeNsoPhysics(Component nso)
        {
            var rb = nso.GetComponentInChildren<Rigidbody>();
            var col = nso.GetComponentInChildren<Collider>();
            string rbs = (object)rb != null ? $"kinematic={rb.isKinematic}" : "no-rb";
            string cols = (object)col != null ? $"col.enabled={col.enabled},layer={col.gameObject.layer}" : "no-col";
            return $"{rbs},{cols}";
        }
    }
}
