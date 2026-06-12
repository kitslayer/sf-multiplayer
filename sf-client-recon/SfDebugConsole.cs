using System;
using HarmonyLib;
using UnityEngine;

// Mono 2.0 (Unity 5.6.3) note: NO lambdas, Action<T>, yield iterators, or
// lock(){} in this assembly — see Mono2Polyfills.cs / project conventions.

namespace SFClientRecon
{
    /// <summary>
    /// File-driven live debug console (v0.6.0). Write commands — one per
    /// line — into /tmp/sf-cmd-&lt;v26port&gt;.txt and the reply prints to the
    /// Unity console (and therefore the /tmp/sf-console-&lt;port&gt;.log tee)
    /// within ~0.5s. Lets an outside operator query live crate/rig state
    /// from a running client without rebuilds or UI.
    /// Commands: help | boxes | box &lt;id&gt; | rigs
    /// </summary>
    public partial class Plugin
    {
        private float _dbgNextPollAt = -1f;
        private string _dbgCmdPath;

        internal void TickDebugConsole()
        {
            float now = Time.realtimeSinceStartup;
            if (_dbgNextPollAt > 0f && now < _dbgNextPollAt) return;
            _dbgNextPollAt = now + 0.5f;
            try
            {
                if (_dbgCmdPath == null)
                {
                    string port = Environment.GetEnvironmentVariable("SFCLIENTRECON_PORT");
                    if (string.IsNullOrEmpty(port)) port = "1339";
                    _dbgCmdPath = "/tmp/sf-cmd-" + port + ".txt";
                }
                if (!System.IO.File.Exists(_dbgCmdPath)) return;
                string text = System.IO.File.ReadAllText(_dbgCmdPath);
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
                System.IO.File.WriteAllText(_dbgCmdPath, "");   // consume
                string[] lines = text.Split('\n');
                foreach (var raw in lines)
                {
                    string cmd = raw.Trim();
                    if (cmd.Length == 0) continue;
                    RunDebugCommand(cmd);
                }
            }
            catch (Exception e) { Debug.Log("[dbg] poll error: " + e.Message); }
        }

        private void RunDebugCommand(string cmd)
        {
            try
            {
                if (cmd == "boxes") { DumpBoxes(); return; }
                if (cmd.StartsWith("box ")) { DumpBox(cmd.Substring(4).Trim()); return; }
                if (cmd == "rigs") { DumpRigs(); return; }
                Debug.Log("[dbg] commands: boxes | box <id> | rigs | help");
            }
            catch (Exception e) { Debug.Log("[dbg] '" + cmd + "' failed: " + e.GetType().Name + ": " + e.Message); }
        }

        // One line per pushable crate: id, position (y,z — the play plane),
        // |velocity|, sleeping/kinematic flags, and the current error vs the
        // server pose target (the reconciler's own view).
        private void DumpBoxes()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[dbg] BOXES id p=(y,z) v slp kin err\n");
            int n = 0;
            foreach (var kv in _nsoCache)
            {
                var e = kv.Value;
                if (e == null || !e.Pushable) continue;
                var rb = e.Rb;
                if (rb == null) continue;
                Vector3 p = rb.position;
                string err = "-";
                PoseTarget pt;
                if (_nsoTargets.TryGetValue(kv.Key, out pt) && pt != null && pt.HasRender)
                    err = (pt.Pos - p).magnitude.ToString("0.00");
                sb.Append("  #").Append(kv.Key)
                  .Append(" p=(").Append(p.y.ToString("0.0")).Append(",").Append(p.z.ToString("0.0"))
                  .Append(") v=").Append(rb.velocity.magnitude.ToString("0.00"))
                  .Append(" slp=").Append(rb.IsSleeping() ? "1" : "0")
                  .Append(" kin=").Append(rb.isKinematic ? "1" : "0")
                  .Append(" err=").Append(err).Append("\n");
                n++;
            }
            sb.Append("[dbg] total ").Append(n).Append(" pushable crates");
            Debug.Log(sb.ToString());
        }

        private void DumpBox(string idStr)
        {
            ushort id;
            if (!ushort.TryParse(idStr, out id)) { Debug.Log("[dbg] box: bad id '" + idStr + "'"); return; }
            NsoCacheEntry e;
            if (!_nsoCache.TryGetValue(id, out e) || e == null || e.Rb == null)
            {
                Debug.Log("[dbg] box #" + id + ": not in cache (or no rigidbody)");
                return;
            }
            var rb = e.Rb;
            var sb = new System.Text.StringBuilder();
            sb.Append("[dbg] BOX #").Append(id)
              .Append("\n  pos=").Append(rb.position.ToString("0.00"))
              .Append(" rot(euler)=").Append(rb.rotation.eulerAngles.ToString("0.0"))
              .Append("\n  vel=").Append(rb.velocity.ToString("0.00"))
              .Append(" angVel=").Append(rb.angularVelocity.ToString("0.00"))
              .Append("\n  mass=").Append(rb.mass.ToString("0.#"))
              .Append(" constraints=").Append((int)rb.constraints)
              .Append(" slp=").Append(rb.IsSleeping() ? "1" : "0")
              .Append(" kin=").Append(rb.isKinematic ? "1" : "0")
              .Append(" pushable=").Append(e.Pushable ? "1" : "0");
            PoseTarget pt;
            if (_nsoTargets.TryGetValue(id, out pt) && pt != null && pt.HasRender)
            {
                float age = Time.realtimeSinceStartup - pt.LastRecvAt;
                sb.Append("\n  serverPose=").Append(pt.Pos.ToString("0.00"))
                  .Append(" serverVel=").Append(pt.Vel.ToString("0.00"))
                  .Append(" age=").Append(age.ToString("0.00")).Append("s")
                  .Append(" fullRot=").Append(pt.HasFullRot ? "1" : "0")
                  .Append(" err=").Append((pt.Pos - rb.position).magnitude.ToString("0.000"));
            }
            else sb.Append("\n  serverPose=<none>");
            Debug.Log(sb.ToString());
        }

        private void DumpRigs()
        {
            var npType = AccessTools.TypeByName("NetworkPlayer");
            if ((object)npType == null) { Debug.Log("[dbg] rigs: NetworkPlayer type not found"); return; }
            var nps = UnityEngine.Object.FindObjectsOfType(npType);
            var sb = new System.Text.StringBuilder();
            sb.Append("[dbg] RIGS:\n");
            int n = 0;
            if (nps != null)
            {
                foreach (var np in nps)
                {
                    var c = np as Component;
                    if (c == null) continue;
                    int slot;
                    string slotStr = TryGetPlayerSlotFromNetworkPlayer(np, out slot) ? slot.ToString() : "?";
                    sb.Append("  slot=").Append(slotStr)
                      .Append(" pos=").Append(c.transform.position.ToString("0.00")).Append("\n");
                    n++;
                }
            }
            sb.Append("[dbg] total ").Append(n).Append(" rigs");
            Debug.Log(sb.ToString());
        }
    }
}
