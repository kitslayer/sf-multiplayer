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
    // SFHeadlessHost — drives Stick Fight headlessly so the Go server can use
    // it as a physics oracle for one lobby.
    //
    // When SF is launched with `-batchmode -nographics`, Unity loads but no UI
    // is available to navigate the menus. This plugin:
    //   1. Detects batchmode on Awake; no-ops in interactive runs.
    //   2. Waits for the chainloader to finish, then forces SceneManager.LoadScene
    //      to skip the splash/menu and jump to a Landfall gameplay scene.
    //   3. Ensures MatchmakingHandler is in Sockets mode, then calls
    //      MatchMakingHandlerSockets.HostServer() to bind a Lidgren NetServer.
    //   4. Patches the hardcoded port 1337 in NetworkSocketServer's ctor with
    //      whatever SFHEADLESS_PORT env var says (default: 1340 to avoid
    //      colliding with the Go server on 1337).
    //
    // Configuration via env vars (read once at Awake):
    //   SFHEADLESS_PORT   — Lidgren bind port (default 1340).
    //   SFHEADLESS_SCENE  — Initial scene index to load (default 6, Landfall 6).
    //   SFHEADLESS_DEBUG  — "1" enables verbose tick logging.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.headless-host";
        public const string PluginName = "SFHeadlessHost";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static int BindPort = 1340;     // Game-traffic port (Lidgren)
        internal static int BridgePort = 1341;   // State-bridge port (this plugin)
        internal static int InitialScene = 0; // 0 = lobby (boots ControllerHandler + GameManager DontDestroyOnLoad infrastructure)
        internal static bool Verbose;

        private void Awake()
        {
            Log = Logger;

            // Unity 5.6 doesn't have Application.isBatchMode — fall back to
            // checking the command-line for -batchmode.
            bool batchMode = false;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-batchmode" || arg == "-nographics")
                {
                    batchMode = true;
                    break;
                }
            }
            if (!batchMode)
            {
                Log.LogInfo($"{PluginName} {PluginVersion}: interactive run detected (no -batchmode arg). No-op.");
                return;
            }
            Log.LogInfo($"{PluginName} {PluginVersion}: batchmode detected, bootstrapping headless host.");

            ReadEnv();

            // P0 — Harmony-patch NetworkSocketServer to bind on BindPort instead
            // of the hardcoded 1337. We do this before HostServer() is called so
            // the patched ctor sees the override.
            try
            {
                var harmony = new Harmony(PluginGuid);
                var sockType = AccessTools.TypeByName("Landfall.Network.Sockets.NetworkSocketServer");
                if ((object)sockType != null)
                {
                    var ctor = AccessTools.Constructor(sockType, Type.EmptyTypes);
                    if ((object)ctor != null)
                    {
                        harmony.Patch(ctor, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(Plugin), nameof(PatchServerPort))));
                        Log.LogInfo($"Patched NetworkSocketServer ctor — bind port will be {BindPort}.");
                    }
                    else
                    {
                        Log.LogWarning("Could not find NetworkSocketServer parameterless ctor.");
                    }
                }
                else
                {
                    Log.LogWarning("Could not find type Landfall.Network.Sockets.NetworkSocketServer.");
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Port-patch failed: {e}");
            }

            _bootStartedAt = Time.realtimeSinceStartup;
            _bootState = BootState.WaitForInit;
        }

        // Boot is driven by Update() as a state machine because Unity 5.6's
        // Mono runtime is missing IteratorStateMachineAttribute (emitted by the
        // C# compiler for any method with `yield return`). Using a plain
        // state machine keeps the assembly compatible.
        private enum BootState { Idle, WaitForInit, LoadingScene, WaitingForSceneSettle, HostStarting, Running }

        private BootState _bootState = BootState.Idle;
        private float _bootStartedAt;
        private float _stateSince;
        private AsyncOperation _loadOp;
        private int _settleFrames;
        private int _heartbeatTicks;
        private float _lastHeartbeat;

        private int _updateErrorTicks;
        private void Update()
        {
            try
            {
                StepBoot();
            }
            catch (Exception e)
            {
                _updateErrorTicks++;
                // Don't kill the boot state; just log periodically so we can see what's wrong.
                if (_updateErrorTicks <= 5 || _updateErrorTicks % 300 == 0)
                {
                    Log.LogError($"SFHeadlessHost.Update (count={_updateErrorTicks}): {e}");
                }
            }
        }

        private void StepBoot()
        {
            switch (_bootState)
            {
                case BootState.Idle:
                    return;

                case BootState.WaitForInit:
                    // 2 second settle to let BepInEx + Unity main-thread init.
                    if (Time.realtimeSinceStartup - _bootStartedAt < 2.0f) return;
                    Log.LogInfo($"Step 1: SceneManager.LoadScene({InitialScene}, Single)");
                    try
                    {
                        _loadOp = SceneManager.LoadSceneAsync(InitialScene, LoadSceneMode.Single);
                    }
                    catch (Exception e)
                    {
                        Log.LogError($"LoadSceneAsync({InitialScene}) threw: {e}");
                        _bootState = BootState.Idle;
                        return;
                    }
                    if (_loadOp == null)
                    {
                        Log.LogError($"LoadSceneAsync({InitialScene}) returned null.");
                        _bootState = BootState.Idle;
                        return;
                    }
                    _bootState = BootState.LoadingScene;
                    _stateSince = Time.realtimeSinceStartup;
                    return;

                case BootState.LoadingScene:
                    if (_loadOp == null || _loadOp.isDone)
                    {
                        var s = SceneManager.GetActiveScene();
                        Log.LogInfo($"Scene loaded: {s.name} (buildIndex={s.buildIndex})");
                        _bootState = BootState.WaitingForSceneSettle;
                        _settleFrames = 0;
                    }
                    else if (Time.realtimeSinceStartup - _stateSince > 30.0f)
                    {
                        Log.LogError("Scene load timed out after 30s — aborting.");
                        _bootState = BootState.Idle;
                    }
                    return;

                case BootState.WaitingForSceneSettle:
                    // Wait a few frames so Awake/Start on the new scene's objects finishes.
                    if (++_settleFrames < 3) return;
                    _bootState = BootState.HostStarting;
                    return;

                case BootState.HostStarting:
                    StartHost();
                    StartBridge();
                    _bootState = BootState.Running;
                    _lastHeartbeat = Time.realtimeSinceStartup;
                    _lastStateEmit = Time.realtimeSinceStartup;
                    return;

                case BootState.Running:
                    // Drain any incoming bridge commands.
                    DrainBridgeCommands();
                    // Push the latest per-slot inputs into each spawned rig's
                    // CharacterActions. Done every frame even if no new input
                    // arrived — analog sticks need their last value held so
                    // the rig keeps moving between input packets.
                    WriteInputsToRigs();
                    // Emit a state snapshot at 30 Hz if anyone has pinged us.
                    if (_bridgePeer != null && Time.realtimeSinceStartup - _lastStateEmit >= (1.0f / 30.0f))
                    {
                        _lastStateEmit = Time.realtimeSinceStartup;
                        EmitStateSnapshot();
                    }
                    var interval = Verbose ? 5.0f : 30.0f;
                    if (Time.realtimeSinceStartup - _lastHeartbeat >= interval)
                    {
                        _lastHeartbeat = Time.realtimeSinceStartup;
                        _heartbeatTicks++;
                        Log.LogInfo($"heartbeat: scene={SceneManager.GetActiveScene().name} tick={_heartbeatTicks}");
                    }
                    return;
            }
        }

        // ========== Bridge: UDP socket the Go server talks to ==========
        // Wire format v0 (JSON, easy to debug; will go binary in v1 if needed):
        //   Go → Oracle commands:
        //     {"cmd":"ping"}
        //     {"cmd":"loadMap","scene":N}
        //     {"cmd":"snapshot"}  -- request a one-shot snapshot
        //     {"cmd":"sub"}       -- subscribe to 30Hz snapshot stream (default after first contact)
        //   Oracle → Go responses (always JSON, one packet each):
        //     {"reply":"pong","tick":N,"scene":"X"}
        //     {"reply":"snapshot","tick":N,"scene":"X","ents":[{"slot":i,"x":...,"y":...,"z":...,"vx":...,"vy":...,"vz":...}]}
        //     {"reply":"ack","cmd":"loadMap","ok":true}

        private UdpClient _bridge;
        private IPEndPoint _bridgePeer; // last sender; we reply to whoever pinged us last
        private float _lastStateEmit;
        private long _bridgeTick;

        // Slot → spawned Player rig GameObject (populated by TrySpawnPlayer).
        // Used by the input-injection path to find which rig to drive.
        private static readonly Dictionary<int, GameObject> SlotToRig = new Dictionary<int, GameObject>();

        // Per-slot input frame the bridge has most recently received. Drained by
        // the per-frame input-write hook so values are written every Update
        // regardless of whether new inputs arrived (analog sticks need to keep
        // their last value across frames; otherwise the rig stops moving when
        // the input rate dips).
        private struct InputFrame
        {
            public float StickX, StickY, AimX, AimY;
            public int Buttons; // bit0=jump, bit1=fire, bit2=block, bit3=throw
        }
        private static readonly Dictionary<int, InputFrame> SlotInputs = new Dictionary<int, InputFrame>();

        private void StartBridge()
        {
            try
            {
                _bridge = new UdpClient(BridgePort);
                _bridge.Client.Blocking = false;
                Log.LogInfo($"Bridge: listening on UDP {BridgePort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"Bridge: bind on {BridgePort} failed: {e.Message}");
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
                    Log.LogInfo($"Bridge: loadMap({scene}) requested by {from}.");
                    SceneManager.LoadScene(scene, LoadSceneMode.Single);
                    SendBridgeJson(from, $"{{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":true,\"scene\":{scene}}}");
                }
                else
                {
                    SendBridgeJson(from, "{\"reply\":\"ack\",\"cmd\":\"loadMap\",\"ok\":false,\"err\":\"missing or invalid scene\"}");
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
                bool ok = TrySpawnPlayer(slot, out string err);
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
                    SlotInputs[slot] = new InputFrame
                    {
                        StickX  = ExtractFloatField(body, "stickX"),
                        StickY  = ExtractFloatField(body, "stickY"),
                        AimX    = ExtractFloatField(body, "aimX"),
                        AimY    = ExtractFloatField(body, "aimY"),
                        Buttons = ExtractIntField(body, "buttons", 0),
                    };
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

        private static readonly StringBuilder _sb = new StringBuilder(2048);

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

        // TrySpawnPlayer instantiates a Player rig in the active scene at the
        // slot's spawn point, by grabbing ControllerHandler.playerPrefab and
        // calling Object.Instantiate directly. This sidesteps the InputDevice
        // pairing path (which requires real input hardware) — the rig will
        // exist but won't move until we inject inputs.
        //
        // Returns (true, "") on success or (false, "reason") on failure.
        private bool TrySpawnPlayer(int slot, out string err)
        {
            err = "";
            try
            {
                var chType = AccessTools.TypeByName("ControllerHandler");
                if ((object)chType == null) { err = "ControllerHandler type not found"; return false; }
                var chInst = UnityEngine.Object.FindObjectOfType(chType);
                if ((object)chInst == null) { err = "ControllerHandler instance not in scene"; return false; }
                var prefabField = AccessTools.Field(chType, "playerPrefab");
                if ((object)prefabField == null) { err = "playerPrefab field not found"; return false; }
                var prefab = prefabField.GetValue(chInst) as GameObject;
                if ((object)prefab == null) { err = "playerPrefab is null"; return false; }
                var spawnPos = new Vector3(0f, 8f, 0f); // matches ControllerHandler.CreatePlayer's default
                var go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity) as GameObject;
                if ((object)go == null) { err = "Instantiate returned null"; return false; }
                go.name = $"OracleSpawn_Slot{slot}";

                // Bind a fresh CharacterActions so the Controller has somewhere
                // to read input from. Without this, mPlayerActions is null and
                // the Controller.Update path early-returns / no movement.
                //
                // Stock ControllerHandler.CreatePlayer calls AssignNewDevice
                // (which requires a real InputDevice we can't synthesize),
                // but Controller also exposes TakeLocalControl(CharacterActions)
                // which doesn't need a device — perfect for our bridge-driven
                // input flow.
                var ctrlType = AccessTools.TypeByName("Controller");
                var caType = AccessTools.TypeByName("CharacterActions");
                if ((object)ctrlType != null && (object)caType != null)
                {
                    var ctrl = go.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var createMethod = AccessTools.Method(caType, "CreateWithControllerBindings");
                        if ((object)createMethod != null)
                        {
                            var actions = createMethod.Invoke(null, null);
                            var takeMethod = AccessTools.Method(ctrlType, "TakeLocalControl");
                            if ((object)actions != null && (object)takeMethod != null)
                            {
                                takeMethod.Invoke(ctrl, new object[] { actions });
                                // Also assign a playerID so any code reading
                                // controller.playerID gets a sensible slot.
                                var pidField = AccessTools.Field(ctrlType, "playerID");
                                if ((object)pidField != null) pidField.SetValue(ctrl, slot);
                                Log.LogInfo($"Bound CharacterActions to slot {slot} via TakeLocalControl.");
                            }
                            else
                            {
                                Log.LogWarning("Could not bind CharacterActions: CreateWith* returned null or TakeLocalControl missing.");
                            }
                        }
                    }
                }

                SlotToRig[slot] = go;
                if (!SlotInputs.ContainsKey(slot))
                {
                    SlotInputs[slot] = new InputFrame();
                }

                // Clear regularBindings on every underlying PlayerAction in
                // this CharacterActions instance. InControl's PlayerAction.
                // UpdateBindings loops over regularBindings each frame and
                // calls UpdateWithValue(bindingSource.GetValue(Device), ...),
                // which writes 0 because we have no real device — that's what
                // clobbers our manually-injected values. With no bindings,
                // the loop is a no-op and our UpdateWithValue calls survive.
                ClearAllPlayerActionBindings(go);

                Log.LogInfo($"Spawned oracle player rig for slot {slot} at {spawnPos} (GO: {go.name})");
                return true;
            }
            catch (Exception e)
            {
                err = e.Message;
                return false;
            }
        }

        // ClearAllPlayerActionBindings walks the rig's CharacterActions and
        // clears each PlayerAction's regularBindings list. Required so our
        // per-frame UpdateWithValue calls aren't immediately overwritten by
        // InControl's UpdateBindings loop reading from null devices.
        private static void ClearAllPlayerActionBindings(GameObject rig)
        {
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType == null) return;
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) return;
                var actionsField = AccessTools.Field(ctrlType, "mPlayerActions");
                if ((object)actionsField == null) return;
                var actions = actionsField.GetValue(ctrl);
                if ((object)actions == null) return;

                var paType = AccessTools.TypeByName("InControl.PlayerAction");
                if ((object)paType == null) return;
                var bindingsField = AccessTools.Field(paType, "regularBindings");
                var visibleField  = AccessTools.Field(paType, "visibleBindings");
                if ((object)bindingsField == null) return;

                // Walk every field on the CharacterActions instance; any
                // PlayerAction we find, clear its bindings.
                int cleared = 0;
                foreach (var f in actions.GetType().GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var v = f.GetValue(actions);
                    if ((object)v == null) continue;
                    if (!paType.IsInstanceOfType(v)) continue;
                    var listObj = bindingsField.GetValue(v);
                    var clearMethod = listObj?.GetType().GetMethod("Clear");
                    clearMethod?.Invoke(listObj, null);
                    if ((object)visibleField != null)
                    {
                        var visObj = visibleField.GetValue(v);
                        visObj?.GetType().GetMethod("Clear")?.Invoke(visObj, null);
                    }
                    cleared++;
                }
                Log.LogInfo($"Cleared regularBindings on {cleared} PlayerActions.");
            }
            catch (Exception e)
            {
                Log.LogError($"ClearAllPlayerActionBindings: {e.Message}");
            }
        }

        // Lookup cache for InControl.PlayerAction.UpdateWithValue MethodInfo.
        private static MethodInfo _cachedUpdateWithValue;
        private static bool _loggedUpdateWithValue;
        private static MethodInfo GetUpdateWithValueMethod()
        {
            if (_cachedUpdateWithValue != null) return _cachedUpdateWithValue;
            var paType = AccessTools.TypeByName("InControl.PlayerAction");
            if ((object)paType == null)
            {
                if (!_loggedUpdateWithValue) { Log.LogWarning("UpdateWithValue: no InControl.PlayerAction type"); _loggedUpdateWithValue = true; }
                return null;
            }
            _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue",
                new Type[] { typeof(float), typeof(ulong), typeof(float) });
            if (_cachedUpdateWithValue == null && !_loggedUpdateWithValue)
            {
                Log.LogWarning("UpdateWithValue: method not found on PlayerAction. Trying without param-type filter…");
                _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue");
                if (_cachedUpdateWithValue == null) Log.LogWarning("UpdateWithValue: not found even without filter");
                else Log.LogInfo($"UpdateWithValue: found via fallback, signature: {_cachedUpdateWithValue}");
                _loggedUpdateWithValue = true;
            }
            else if (!_loggedUpdateWithValue)
            {
                Log.LogInfo($"UpdateWithValue: found, signature: {_cachedUpdateWithValue}");
                _loggedUpdateWithValue = true;
            }
            return _cachedUpdateWithValue;
        }

        private static bool _loggedPushPath;
        // PushPlayerAction calls PlayerAction.UpdateWithValue(value, tick, dt)
        // on the named PlayerAction field of the given CharacterActions.
        private static void PushPlayerAction(object actions, string fieldName, float value)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field not found on type {actions.GetType()}"); _loggedPushPath = true; }
                return;
            }
            var action = f.GetValue(actions);
            if ((object)action == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field value is null"); _loggedPushPath = true; }
                return;
            }
            var m = GetUpdateWithValueMethod();
            if ((object)m == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: UpdateWithValue method lookup failed; action type={action.GetType()}"); _loggedPushPath = true; }
                return;
            }
            try
            {
                m.Invoke(action, new object[] { value, (ulong)0, Time.deltaTime });
                if (!_loggedPushPath) { Log.LogInfo($"PushPlayerAction[{fieldName}]: invoke ok, value={value}"); _loggedPushPath = true; }
            }
            catch (Exception e)
            {
                if (!_loggedPushPath) { Log.LogError($"PushPlayerAction[{fieldName}] invoke threw: {e}"); _loggedPushPath = true; }
            }
        }

        // WriteInputsToRigs pushes the most recent per-slot input frame into
        // each spawned rig's CharacterActions via reflection. The Controller
        // reads these every frame in Update — so by writing them right before
        // Controller.Update runs (we're called from Plugin.Update which Unity
        // schedules before MonoBehaviours by default), our values become the
        // effective input for that frame.
        //
        // CharacterActions is an InControl PlayerActionSet. Its Movement /
        // Aiming fields are TwoAxisInputControl with a settable RawValue.
        // Buttons are PlayerAction with a settable RawValue / IsPressed.
        private static bool _loggedFirstWrite;
        private static bool _loggedFirstWriteIter;
        private void WriteInputsToRigs()
        {
            if (SlotToRig.Count == 0) return;
            if (!_loggedFirstWrite) { Log.LogInfo($"WriteInputsToRigs called for first time. SlotToRig.Count={SlotToRig.Count} SlotInputs.Count={SlotInputs.Count}"); _loggedFirstWrite = true; }
            try
            {
                foreach (var kv in SlotToRig)
                {
                    int slot = kv.Key;
                    GameObject rig = kv.Value;
                    if ((object)rig == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: rig null"); _loggedFirstWriteIter = true; } continue; }
                    if (!SlotInputs.TryGetValue(slot, out var input)) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: SlotInputs miss"); _loggedFirstWriteIter = true; } continue; }

                    var ctrlType = AccessTools.TypeByName("Controller");
                    if ((object)ctrlType == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no Controller type"); _loggedFirstWriteIter = true; } continue; }
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no Controller on rig"); _loggedFirstWriteIter = true; } continue; }
                    var actionsField = AccessTools.Field(ctrlType, "mPlayerActions");
                    if ((object)actionsField == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no mPlayerActions field"); _loggedFirstWriteIter = true; } continue; }
                    var actions = actionsField.GetValue(ctrl);
                    if ((object)actions == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: mPlayerActions is null"); _loggedFirstWriteIter = true; } continue; }

                    if (!_loggedFirstWriteIter) { Log.LogInfo($"WriteInputs iter: REACHED PushPlayerAction, actions type={actions.GetType().FullName}, stick=({input.StickX},{input.StickY})"); _loggedFirstWriteIter = true; }

                    // Feed the underlying L/R/U/D PlayerActions — that's
                    // what CharacterActions.Movement (a PlayerTwoAxisAction)
                    // computes its X/Y from. Setting Movement.thisValue
                    // directly gets overwritten next frame by
                    // PlayerTwoAxisAction.Update reading L/R/U/D.
                    PushPlayerAction(actions, "Left",  Mathf.Max(0f, -input.StickX));
                    PushPlayerAction(actions, "Right", Mathf.Max(0f,  input.StickX));
                    PushPlayerAction(actions, "Up",    Mathf.Max(0f,  input.StickY));
                    PushPlayerAction(actions, "Down",  Mathf.Max(0f, -input.StickY));

                    PushPlayerAction(actions, "AimLeft",  Mathf.Max(0f, -input.AimX));
                    PushPlayerAction(actions, "AimRight", Mathf.Max(0f,  input.AimX));
                    PushPlayerAction(actions, "AimUp",    Mathf.Max(0f,  input.AimY));
                    PushPlayerAction(actions, "AimDown",  Mathf.Max(0f, -input.AimY));

                    PushPlayerAction(actions, "Jump",         (input.Buttons & 0x01) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Jump2",        (input.Buttons & 0x01) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "PunchOrFire",  (input.Buttons & 0x02) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Block",        (input.Buttons & 0x04) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Throw",        (input.Buttons & 0x08) != 0 ? 1f : 0f);
                }
            }
            catch (Exception e)
            {
                if (Verbose) Log.LogDebug($"WriteInputsToRigs: {e.Message}");
            }
        }

        // SetTwoAxis writes (x, y) to the named TwoAxisInputControl on the
        // CharacterActions instance by poking its private `thisValue` Vector2
        // field directly. Stock InControl exposes Value as a getter only
        // and no setter API for "fake" input — we have to bypass.
        private static void SetTwoAxis(object actions, string fieldName, Vector2 v)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, v);
            // X / Y are protected properties; their backing fields are auto-
            // generated (<X>k__BackingField). Update them too so anything that
            // reads .X / .Y sees the new value.
            var xBacking = AccessTools.Field(t, "<X>k__BackingField");
            var yBacking = AccessTools.Field(t, "<Y>k__BackingField");
            if ((object)xBacking != null) xBacking.SetValue(ctrl, v.x);
            if ((object)yBacking != null) yBacking.SetValue(ctrl, v.y);
        }

        // SetOneAxisOrButton writes a button-press state by setting the
        // PlayerAction's private thisValue (float, 0.0 / 1.0).
        private static void SetOneAxisOrButton(object actions, string fieldName, bool pressed)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, pressed ? 1.0f : 0.0f);
        }

        // === tiny JSON field extractors (avoid dragging in JSON.NET) ===
        private static string ExtractStringField(string json, string field)
        {
            // Looks for "field":"VALUE" — fragile but adequate for our 2-3 field commands.
            int i = json.IndexOf("\"" + field + "\"");
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
            int i = json.IndexOf("\"" + field + "\"");
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
            int i = json.IndexOf("\"" + field + "\"");
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

        private void StartHost()
        {
            // Step 2: ensure MatchmakingHandler is in Sockets mode.
            var mmType = AccessTools.TypeByName("MatchmakingHandler");
            if ((object)mmType != null)
            {
                var runningOnSockets = AccessTools.Field(mmType, "mRunningOnSockets")
                                       ?? AccessTools.Field(mmType, "m_RunningOnSockets");
                var instance = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)instance != null && (object)runningOnSockets != null)
                {
                    runningOnSockets.SetValue(instance, true); /* FieldInfo — no compat fix needed */
                    Log.LogInfo("Set MatchmakingHandler.mRunningOnSockets = true.");
                }
                else
                {
                    Log.LogWarning("MatchmakingHandler instance or mRunningOnSockets field not found.");
                }

                var runningOnSocketsStatic = AccessTools.Property(mmType, "RunningOnSockets");
                if ((object)runningOnSocketsStatic != null && runningOnSocketsStatic.CanWrite)
                {
                    runningOnSocketsStatic.SetValue(null, true, null);
                }
                else
                {
                    var backing = AccessTools.Field(mmType, "<RunningOnSockets>k__BackingField");
                    if ((object)backing != null) backing.SetValue(null, true);
                }
            }

            // Step 3: HostServer on MatchMakingHandlerSockets.
            var hostType = AccessTools.TypeByName("MatchMakingHandlerSockets");
            if ((object)hostType == null)
            {
                Log.LogError("MatchMakingHandlerSockets type not found.");
                return;
            }
            var hostInstance = UnityEngine.Object.FindObjectOfType(hostType);
            if ((object)hostInstance == null)
            {
                Log.LogWarning("No MatchMakingHandlerSockets instance in scene; creating one.");
                var go = new GameObject("SFHeadlessHost_MMSockets");
                UnityEngine.Object.DontDestroyOnLoad(go);
                hostInstance = go.AddComponent(hostType);
            }
            var hostMethod = AccessTools.Method(hostType, "HostServer");
            if ((object)hostMethod == null)
            {
                Log.LogError("MatchMakingHandlerSockets.HostServer method not found.");
                return;
            }
            try
            {
                var result = hostMethod.Invoke(hostInstance, null);
                Log.LogInfo($"HostServer() returned: {result}");
            }
            catch (Exception e)
            {
                Log.LogError($"HostServer() threw: {e}");
                return;
            }
            Log.LogInfo($"=== HEADLESS HOST READY on port {BindPort} ===");
        }

        private static void ReadEnv()
        {
            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_PORT"), out var p);
            if (p > 0 && p < 65536) BindPort = p;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_BRIDGEPORT"), out var bp);
            if (bp > 0 && bp < 65536) BridgePort = bp;

            int.TryParse(Environment.GetEnvironmentVariable("SFHEADLESS_SCENE"), out var s);
            if (s >= 0) InitialScene = s;

            Verbose = Environment.GetEnvironmentVariable("SFHEADLESS_DEBUG") == "1";
            Log.LogInfo($"Config: BindPort={BindPort} BridgePort={BridgePort} InitialScene={InitialScene} Verbose={Verbose}");
        }

        // Harmony postfix on NetworkSocketServer ctor. The stock ctor sets
        // Server = new NetServer(config{Port = 1337}). We can't easily unwind
        // that, but Lidgren NetServer hasn't been Start()ed yet at this point —
        // we can mutate its Configuration.Port before Init() is called.
        private static void PatchServerPort(object __instance)
        {
            try
            {
                var serverProp = AccessTools.Property(__instance.GetType(), "Server");
                if ((object)serverProp == null) return;
                var netServer = serverProp.GetValue(__instance, null);
                if ((object)netServer == null) return;
                var configProp = AccessTools.Property(netServer.GetType(), "Configuration");
                if ((object)configProp == null) return;
                var config = configProp.GetValue(netServer, null);
                if ((object)config == null) return;
                var portProp = AccessTools.Property(config.GetType(), "Port");
                if ((object)portProp == null) return;
                portProp.SetValue(config, BindPort, null);
                Log.LogInfo($"NetworkSocketServer ctor postfix: rewrote Port → {BindPort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"PatchServerPort threw: {e}");
            }
        }

    }
}
