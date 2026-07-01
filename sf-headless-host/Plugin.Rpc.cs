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

        private void StartBridge()
        {
            try
            {
                // Loopback-only: bridge commands (loadMap/teleport/addForce/...)
                // mutate gameplay state with no auth. Co-located Go server is on
                // the same host, so 0.0.0.0 exposure is gratuitous network risk.
                _bridge = new UdpClient(new IPEndPoint(IPAddress.Loopback, BridgePort));
                _bridge.Client.Blocking = false;
                Log.LogInfo($"Bridge: listening on UDP 127.0.0.1:{BridgePort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"Bridge: bind on 127.0.0.1:{BridgePort} failed: {e.Message}");
                _bridge = null;
            }
        }

        private void DrainBridgeCommands()
        {
            if ((object)_bridge == null) return;
            int processed = 0;
            while (processed++ < 16) // cap per frame
            {
                byte[] data;
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    if (_bridge.Available <= 0) return;
                    data = _bridge.Receive(ref remote);
                }
                catch (SocketException)
                {
                    return; // would-block / nothing to read
                }
                catch (Exception e)
                {
                    if (Verbose) Log.LogDebug($"Bridge recv: {e.Message}");
                    return;
                }
                _bridgePeer = remote;
                HandleBridgeCommand(data, remote);
            }
        }

        private void HandleBridgeCommand(byte[] data, IPEndPoint from)
        {
            string body = Encoding.UTF8.GetString(data);
            if (Verbose) Log.LogDebug($"Bridge ← {from}: {body}");
            // Tiny ad-hoc JSON parser — body shapes are trivial so we don't need a full lib.
            string cmd = ExtractStringField(body, "cmd");
            if (cmd == "ping")
            {
                SendBridgeJson(from, $"{{\"reply\":\"pong\",\"tick\":{_bridgeTick},\"scene\":\"{SceneManager.GetActiveScene().name}\"}}");
            }
            else if (cmd == "snapshot")
            {
                EmitStateSnapshotTo(from);
            }
            else if (cmd == "loadMap")
            {
                int scene = ExtractIntField(body, "scene", -1);
                if (scene >= 0)
                {
                    // Optional teleport-after-load coordinates. ALL THREE of
                    // x/y/z must be present, else we reject — silently
                    // defaulting missing coords to 0 was sending rigs to the
                    // origin (right above the killbox).
                    bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                    bool hasTeleport = hasX || hasY || hasZ;
                    if (hasTeleport && !(hasX && hasY && hasZ))
                    {
                        SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":false,\"err\":\"partial teleport coords — need x,y,z together\"}");
                        return;
                    }
                    float tx = hasTeleport ? ExtractFloatField(body, "x") : 0f;
                    float ty = hasTeleport ? ExtractFloatField(body, "y") : 0f;
                    float tz = hasTeleport ? ExtractFloatField(body, "z") : 0f;
                    Log.LogInfo($"Bridge: loadMap({scene}) requested; teleport=({tx},{ty},{tz}) hasTeleport={hasTeleport}");
                    if (hasTeleport)
                    {
                        _pendingTeleport = new Vector3(tx, ty, tz);
                        _pendingTeleportArmed = true;
                        SceneManager.sceneLoaded -= OnSceneLoadedTeleport;
                        SceneManager.sceneLoaded += OnSceneLoadedTeleport;
                    }
                    // Track current scene so subsequent BroadcastMapChange
                    // reflects reality, not the hardcoded boot default.
                    _currentSceneIndex = scene;
                    SceneManager.LoadScene(scene, LoadSceneMode.Single);
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":true,\"scene\":{scene}}}");
                }
                else
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":false,\"err\":\"missing or invalid scene\"}");
                }
            }
            else if (cmd == "teleport")
            {
                // Direct teleport command — no scene load. Useful when you
                // want to re-park the rig (e.g. after it falls into a void).
                // Require all of x/y/z to be present so a malformed payload
                // doesn't park the rig at origin (killbox-adjacent).
                int slot = ExtractIntField(body, "slot", -1);
                bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                if (!(hasX && hasY && hasZ))
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":false,\"err\":\"missing x/y/z\"}");
                    return;
                }
                float tx = ExtractFloatField(body, "x");
                float ty = ExtractFloatField(body, "y");
                float tz = ExtractFloatField(body, "z");
                if (slot >= 0 && SlotToRig.TryGetValue(slot, out var rigGo) && (object)rigGo != null)
                {
                    TeleportRig(rigGo, new Vector3(tx, ty, tz));
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":true,\"slot\":{slot}}}");
                }
                else
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"teleport\",\"ok\":false,\"err\":\"slot not found\"}");
                }
            }
            else if (cmd == "sub")
            {
                // Just record peer for stream; no-op response.
                SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"sub\",\"ok\":true}");
            }
            else if (cmd == "spawnPlayer")
            {
                int slot = ExtractIntField(body, "slot", 0);
                // Optional x/y/z to spawn directly at — useful when spawning
                // into a Landfall scene where the default (0,8,0) is below the
                // killbox and the rig dies before any teleport can save it.
                // Must be all-or-nothing so a partial payload doesn't park the
                // rig at origin.
                bool hasX = HasField(body, "x"), hasY = HasField(body, "y"), hasZ = HasField(body, "z");
                bool hasPos = hasX || hasY || hasZ;
                if (hasPos && !(hasX && hasY && hasZ))
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":false,\"err\":\"partial spawn coords — need x,y,z together\"}");
                    return;
                }
                Vector3 pos = new Vector3(0f, 8f, 0f);
                if (hasPos) pos = new Vector3(ExtractFloatField(body, "x"), ExtractFloatField(body, "y"), ExtractFloatField(body, "z"));
                bool ok = TrySpawnPlayer(slot, pos, out string err);
                if (ok)
                {
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":true,\"slot\":{slot}}}");
                }
                else
                {
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"spawnPlayer\",\"ok\":false,\"err\":\"{err}\"}}");
                }
            }
            else if (cmd == "addForce")
            {
                // Most direct possible test: pick the first BodyPart child and
                // AddForce on its Rigidbody manually. If the rig moves, we
                // know physics is healthy and the issue is upstream.
                int slot = ExtractIntField(body, "slot", 0);
                float fz = ExtractFloatField(body, "fz");
                string err;
                bool ok = TryAddForce(slot, fz, out err);
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"addForce\",\"ok\":{(ok?"true":"false")},\"err\":\"{err}\"}}");
            }
            else if (cmd == "forceMove")
            {
                // Diagnostic: directly call Movement.MoveRight() for one tick.
                // If position changes, Controller.Update isn't routing our
                // inputs to MoveRight. If it doesn't, MoveRight itself is broken.
                int slot = ExtractIntField(body, "slot", 0);
                string dir = ExtractStringField(body, "dir") ?? "right";
                bool ok = TryForceMove(slot, dir, out string err);
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"forceMove\",\"ok\":{(ok?"true":"false")},\"err\":\"{err}\"}}");
            }
            else if (cmd == "inspect")
            {
                int slot = ExtractIntField(body, "slot", 0);
                string info = InspectRig(slot);
                SendBridgeJson(from, $"{{\"reply\":\"inspect\",\"slot\":{slot},\"info\":\"{info.Replace("\\","\\\\").Replace("\"","\\\"")}\"}}");
            }
            else if (cmd == "applyInput")
            {
                int slot = ExtractIntField(body, "slot", -1);
                if (slot < 0)
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"applyInput\",\"ok\":false,\"err\":\"bad slot\"}");
                }
                else
                {
                    var frame = new InputFrame
                    {
                        StickX  = ExtractFloatField(body, "stickX"),
                        StickY  = ExtractFloatField(body, "stickY"),
                        AimX    = ExtractFloatField(body, "aimX"),
                        AimY    = ExtractFloatField(body, "aimY"),
                        Buttons = ExtractIntField(body, "buttons", 0),
                    };
                    SlotInputs[slot] = frame;
                    _applyInputCount++;
                    if (_applyInputCount == 1 || _applyInputCount % 60 == 0)
                        Log.LogInfo($"[INSTR1] applyInput#{_applyInputCount}: slot={slot} stick=({frame.StickX:0.00},{frame.StickY:0.00}) buttons={frame.Buttons} SlotInputs.Count={SlotInputs.Count}");
                    // No reply for applyInput — comes 60 times/sec from Go,
                    // we don't want to flood the network with acks.
                }
            }
            else
            {
                SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"{cmd}\",\"ok\":false,\"err\":\"unknown cmd\"}}");
            }
        }

        private void SendBridgeJson(IPEndPoint to, string json)
        {
            if ((object)_bridge == null) return;
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                _bridge.Send(data, data.Length, to);
            }
            catch (Exception e)
            {
                if (Verbose) Log.LogDebug($"Bridge send: {e.Message}");
            }
        }

        // EmitStateSnapshot pushes the current world entity state to the most
        // recently active peer (for the 30Hz stream). When the bridge has never
        // been pinged we don't emit, avoiding wasted work.
        private void EmitStateSnapshot()
        {
            if ((object)_bridgePeer == null) return;
            EmitStateSnapshotTo(_bridgePeer);
        }

        private void EmitStateSnapshotTo(IPEndPoint to)
        {
            _bridgeTick++;
            try
            {
                _sb.Length = 0;
                _sb.Append("{\"reply\":\"snapshot\",\"tick\":").Append(_bridgeTick);
                _sb.Append(",\"scene\":\"").Append(SceneManager.GetActiveScene().name).Append("\"");
                _sb.Append(",\"ents\":[");

                // Report only the rigs we spawned — slot-keyed via SlotToRig.
                // The root transform doesn't move under SF's physics model;
                // the actual position is determined by the ragdoll skeleton's
                // BodyPart Rigidbodies. Use the first BodyPart's position
                // (typically the hip/pelvis) as the canonical position.
                bool first = true;
                var bodyPartType = AccessTools.TypeByName("BodyPart");
                foreach (var kv in SlotToRig)
                {
                    var rig = kv.Value;
                    if ((object)rig == null) continue;
                    Vector3 p = rig.transform.position;
                    if ((object)bodyPartType != null)
                    {
                        var bp = rig.GetComponentInChildren(bodyPartType) as Component;
                        if ((object)bp != null) p = bp.transform.position;
                    }
                    if (!first) _sb.Append(",");
                    first = false;
                    _sb.Append("{\"slot\":").Append(kv.Key);
                    _sb.Append(",\"x\":").Append(p.x.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    _sb.Append(",\"y\":").Append(p.y.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    _sb.Append(",\"z\":").Append(p.z.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    _sb.Append("}");
                }
                _sb.Append("]}");
                SendBridgeJson(to, _sb.ToString());
            }
            catch (Exception e)
            {
                if (Verbose) Log.LogDebug($"EmitStateSnapshot: {e.Message}");
            }
        }

        // TryAddForce directly AddForces on the rig's first BodyPart Rigidbody.
        // If THIS doesn't move the rig, the Rigidbody is constrained somehow
        // (joints, freezeAll, mass=infinity, etc.) — not a force-routing issue.
        private bool TryAddForce(int slot, float fz, out string err)
        {
            err = "";
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) { err = "no rig"; return false; }
                var bp = rig.GetComponentInChildren(AccessTools.TypeByName("BodyPart")) as Component;
                if ((object)bp == null) { err = "no BodyPart"; return false; }
                var rb = bp.GetComponent<Rigidbody>();
                if ((object)rb == null) { err = "no Rigidbody on BodyPart"; return false; }
                rb.AddForce(new Vector3(0f, 0f, fz), ForceMode.Impulse);
                err = $"applied F=(0,0,{fz}) Imp to {bp.gameObject.name}; rb.mass={rb.mass} kinematic={rb.isKinematic} constraints={rb.constraints}";
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // TryForceMove directly calls Movement.MoveRight/MoveLeft on the rig
        // for diagnostic purposes — bypassing Controller.Update's input read.
        private bool TryForceMove(int slot, string dir, out string err)
        {
            err = "";
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) { err = "no rig"; return false; }
                var mov = rig.GetComponent(AccessTools.TypeByName("Movement"));
                if ((object)mov == null) { err = "no Movement"; return false; }
                string methodName = dir == "left" ? "MoveLeft" : "MoveRight";
                var m = AccessTools.Method(mov.GetType(), methodName);
                if ((object)m == null) { err = "no " + methodName; return false; }
                m.Invoke(mov, null);
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // InspectRig dumps the slot's rig state — useful to diagnose why a
        // freshly-spawned player isn't moving / falling / responding to input.
        private string InspectRig(int slot)
        {
            try
            {
                if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null)
                {
                    return "no rig";
                }
                var sb = new StringBuilder(512);
                sb.Append("active=").Append(rig.activeSelf).Append("/").Append(rig.activeInHierarchy);
                sb.Append("; pos=").Append(rig.transform.position.ToString("0.00"));

                var rb = rig.GetComponent<Rigidbody>();
                if ((object)rb != null)
                {
                    sb.Append("; rb.kinematic=").Append(rb.isKinematic);
                    sb.Append(" useGravity=").Append(rb.useGravity);
                    sb.Append(" vel=").Append(rb.velocity.ToString("0.00"));
                }
                else sb.Append("; no Rigidbody");

                var ctrl = rig.GetComponent(AccessTools.TypeByName("Controller"));
                if ((object)ctrl != null)
                {
                    var hasControl = AccessTools.Field(ctrl.GetType(), "mHasControl");
                    if ((object)hasControl != null) sb.Append("; hasControl=").Append(hasControl.GetValue(ctrl));
                    var inactive = AccessTools.Field(ctrl.GetType(), "inactive");
                    if ((object)inactive != null) sb.Append(" inactive=").Append(inactive.GetValue(ctrl));
                }
                else sb.Append("; no Controller");

                var mov = rig.GetComponent(AccessTools.TypeByName("Movement"));
                if ((object)mov != null)
                {
                    sb.Append("; Movement=").Append(((Behaviour)mov).enabled);
                    var fm = AccessTools.Field(mov.GetType(), "forceMultiplier");
                    if ((object)fm != null) sb.Append(" forceMultiplier=").Append(fm.GetValue(mov));
                }
                else sb.Append("; no Movement");

                var fighting = rig.GetComponent(AccessTools.TypeByName("Fighting"));
                if ((object)fighting != null)
                {
                    var mm = AccessTools.Field(fighting.GetType(), "movementMultiplier");
                    if ((object)mm != null) sb.Append("; movementMultiplier=").Append(mm.GetValue(fighting));
                }

                var info = rig.GetComponent(AccessTools.TypeByName("CharacterInformation"));
                if ((object)info != null)
                {
                    var sf = AccessTools.Field(info.GetType(), "sinceFallen");
                    if ((object)sf != null) sb.Append("; sinceFallen=").Append(sf.GetValue(info));
                    var dead = AccessTools.Field(info.GetType(), "isDead");
                    if ((object)dead != null) sb.Append(" isDead=").Append(dead.GetValue(info));
                }

                // Dump CharacterActions Movement.X / Y / Left / Right values
                // so we can see whether our injection is taking effect.
                var ctrl2 = rig.GetComponent(AccessTools.TypeByName("Controller"));
                if ((object)ctrl2 != null)
                {
                    var pa = AccessTools.Field(ctrl2.GetType(), "mPlayerActions")?.GetValue(ctrl2);
                    if ((object)pa != null)
                    {
                        var movement = AccessTools.Field(pa.GetType(), "Movement")?.GetValue(pa);
                        if ((object)movement != null)
                        {
                            float mx = (float)AccessTools.Property(movement.GetType(), "X").GetValue(movement, null);
                            float my = (float)AccessTools.Property(movement.GetType(), "Y").GetValue(movement, null);
                            sb.Append("; Movement.X=").Append(mx.ToString("0.00")).Append(" .Y=").Append(my.ToString("0.00"));
                        }
                        var leftPa = AccessTools.Field(pa.GetType(), "Left")?.GetValue(pa);
                        var rightPa = AccessTools.Field(pa.GetType(), "Right")?.GetValue(pa);
                        if ((object)leftPa != null && (object)rightPa != null)
                        {
                            var leftVal = AccessTools.Property(leftPa.GetType(), "Value")?.GetValue(leftPa, null);
                            var rightVal = AccessTools.Property(rightPa.GetType(), "Value")?.GetValue(rightPa, null);
                            sb.Append("; Left.Value=").Append(leftVal).Append(" Right.Value=").Append(rightVal);
                        }
                    }
                }

                sb.Append("; Time.timeScale=").Append(Time.timeScale.ToString("0.000"));
                sb.Append("; Time.deltaTime=").Append(Time.deltaTime.ToString("0.000"));
                sb.Append("; fixedDelta=").Append(Time.fixedDeltaTime.ToString("0.000"));

                var standing = rig.GetComponent(AccessTools.TypeByName("Standing"));
                if ((object)standing != null) sb.Append("; Standing=").Append(((Behaviour)standing).enabled);
                else sb.Append("; no Standing");

                return sb.ToString();
            }
            catch (Exception e) { return "exc: " + e.Message; }
        }

        // === tiny JSON field extractors (avoid dragging in JSON.NET) ===
        //
        // FindField returns the index of a `"field"` token where the preceding
        // character is a key boundary ({, ,, or whitespace). Without this, a
        // search for "x" matches "tx", "exit", etc. and a search for "slot"
        // matches "slotName". Returns -1 if no boundary-respecting match.
        private static int FindField(string json, string field)
        {
            string token = "\"" + field + "\"";
            int from = 0;
            while (from < json.Length)
            {
                int i = json.IndexOf(token, from);
                if (i < 0) return -1;
                bool boundaryOk = (i == 0);
                if (!boundaryOk)
                {
                    char prev = json[i - 1];
                    boundaryOk = prev == '{' || prev == ',' || prev == ' ' || prev == '\t' || prev == '\n' || prev == '\r';
                }
                if (boundaryOk) return i;
                from = i + 1;
            }
            return -1;
        }

        // HasField — quick presence check used by callers that need to
        // distinguish "field absent" from "field present with default value".
        private static bool HasField(string json, string field) => FindField(json, field) >= 0;

        private static string ExtractStringField(string json, string field)
        {
            int i = FindField(json, field);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static float ExtractFloatField(string json, string field)
        {
            int i = FindField(json, field);
            if (i < 0) return 0f;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return 0f;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-' || json[end] == '.' || json[end] == 'e' || json[end] == 'E' || json[end] == '+')) end++;
            if (end == start) return 0f;
            float f;
            if (float.TryParse(json.Substring(start, end - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) return f;
            return 0f;
        }

        private static int ExtractIntField(string json, string field, int fallback)
        {
            int i = FindField(json, field);
            if (i < 0) return fallback;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return fallback;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == start) return fallback;
            int n;
            if (int.TryParse(json.Substring(start, end - start), out n)) return n;
            return fallback;
        }
    }
}
