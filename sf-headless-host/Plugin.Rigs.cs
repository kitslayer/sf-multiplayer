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

        private static void OnSceneLoadedTeleport(Scene scene, LoadSceneMode mode)
        {
            if (!_pendingTeleportArmed) return;
            _pendingTeleportArmed = false;
            SceneManager.sceneLoaded -= OnSceneLoadedTeleport;
            Log.LogInfo($"OnSceneLoadedTeleport: scene={scene.name} target={_pendingTeleport}; teleporting {SlotToRig.Count} rigs.");
            foreach (var kv in SlotToRig)
            {
                if ((object)kv.Value == null) continue;
                TeleportRig(kv.Value, _pendingTeleport);
            }
        }

        // TeleportRig moves the rig root + every BodyPart Rigidbody to the
        // target position. The root transform alone doesn't move the visible
        // rig (body parts have independent Rigidbody-driven positions); we
        // have to relocate them all and zero their velocity so they don't
        // immediately bounce back to the old location.
        private static void TeleportRig(GameObject rig, Vector3 target)
        {
            try
            {
                var rootPos = rig.transform.position;
                var delta = target - rootPos;
                rig.transform.position = target;

                var bpType = AccessTools.TypeByName("BodyPart");
                if ((object)bpType == null) return;
                var bps = rig.GetComponentsInChildren(bpType);
                int moved = 0;
                foreach (var bp in bps)
                {
                    var bpComp = bp as Component;
                    if ((object)bpComp == null) continue;
                    var rb = bpComp.GetComponent<Rigidbody>();
                    if ((object)rb == null) continue;
                    rb.position = rb.position + delta;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    bpComp.transform.position = bpComp.transform.position + delta;
                    moved++;
                }
                Log.LogInfo($"TeleportRig: moved {moved} body parts by delta={delta}");
            }
            catch (Exception e)
            {
                Log.LogError($"TeleportRig threw: {e.Message}");
            }
        }

        private void SpawnAuthoritativePlayersForAllClients()
        {
            Log.LogInfo($"[P6.9] SpawnAuthoritativePlayers: iterating {_sfClients.Count} clients.");
            // [SF DEBUG] dump every SfClient's state before we spawn — this
            // is the ground-truth snapshot of who-is-who at this moment.
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                Log.LogInfo($"[SF DEBUG]   _sfClients[{kv.Key}] → Slot={c.Slot} SteamID={c.SteamID} Spawned={c.Spawned} Initialized={c.Initialized}");
            }
            int considered = 0, spawned = 0, skipped = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                considered++;
                if (!cli.Initialized)
                {
                    Log.LogInfo($"[P6.9] Skip {kv.Key}: not Initialized.");
                    skipped++;
                    continue;
                }
                if (SlotToRig.ContainsKey(cli.Slot))
                {
                    Log.LogInfo($"[P6.9] Skip {kv.Key}: rig already exists for slot {cli.Slot}.");
                    skipped++;
                    continue;
                }
                Vector3 startPos = new Vector3(0f, 8f, 0f);
                bool ok = TrySpawnPlayer(cli.Slot, startPos, out string err);
                if (ok)
                {
                    Log.LogInfo($"[P6.9] Spawned authoritative rig for client slot={cli.Slot} steamID={cli.SteamID}.");
                    ConfigureAuthoritativeRig(cli.Slot);
                    spawned++;
                }
                else
                {
                    Log.LogError($"[P6.9] Failed to spawn authoritative rig for slot {cli.Slot}: {err}");
                }
            }
            Log.LogInfo($"[P6.9] SpawnAuthoritativePlayers done: considered={considered} spawned={spawned} skipped={skipped}");
        }

        // Configure a freshly-spawned rig as the server's authoritative copy
        // of that player. Per-instance HasControl=true on the Controller so
        // SF's host-side gates (destructible piece OnCollisionEnter, etc.)
        // accept this rig as a legitimate authority source.
        //
        // Also configure as a "physics ghost" — kinematic body parts that
        // get teleported to client position each PlayerUpdate. The ghost
        // pushes NSOs (boxes/crates) via kinematic sweep so box collisions
        // happen server-side, then NSO snapshots broadcast box positions
        // back to all clients. NSO components on the rig itself are
        // disabled so they don't broadcast wrong indices.
        //
        // This is "mirror rig 2.0" — real NetworkPlayer with per-instance
        // HasControl, behaving as a ghost until v26 PlayerInput properly
        // drives Movement.cs. The transition will be: when inputs are
        // verified flowing reliably, un-kinematic the root and the rig
        // becomes input-driven (no more position-from-client mirror).
        private void ConfigureAuthoritativeRig(int slot)
        {
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType != null)
                {
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
                        if ((object)hasCtrlF != null)
                        {
                            hasCtrlF.SetValue(ctrl, true);
                            Log.LogInfo($"[P6.9] Slot {slot}: Controller.mHasControl set true (per-instance).");
                        }
                    }
                }

                // Make all body part rigidbodies kinematic — no gravity, no
                // Movement-driven forces, just position-driven sweeps.
                var rbs = rig.GetComponentsInChildren<Rigidbody>();
                int kinSet = 0;
                foreach (var rb in rbs)
                {
                    if ((object)rb == null) continue;
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    kinSet++;
                }

                // Disable NSO components on the rig — they'd otherwise
                // broadcast ObjectUpdate with whatever Index the rig parts
                // carry, potentially corrupting scene-object indices on
                // clients.
                int nsoOff = 0;
                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType != null)
                {
                    var nsos = rig.GetComponentsInChildren(nsoType);
                    foreach (var nso in nsos)
                    {
                        var beh = nso as Behaviour;
                        if ((object)beh != null) { beh.enabled = false; nsoOff++; }
                    }
                }
                Log.LogInfo($"[P6.9 ghost] Slot {slot}: {kinSet} rbs kinematic, {nsoOff} NSO components disabled.");
            }
            catch (Exception e) { Log.LogWarning($"[P6.9 ConfigureAuthoritativeRig] {e.Message}"); }
        }

        private void UpdateGhostRigPosition(int slot, Vector3 target)
        {
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            var rootPos = rig.transform.position;
            var delta = target - rootPos;
            if (delta.sqrMagnitude < 0.0001f) return;
            // BOXES FIX v3: large jumps (first update from spawn-point to client's
            // real position, or scene transition) use direct rb.position writes
            // so we DON'T sweep through box stacks and knock them off platforms.
            // Subsequent small deltas use MovePosition's swept collision so the
            // rig CAN push boxes as it walks into them.
            bool teleport = delta.magnitude > 5f;
            rig.transform.position = target;
            var rbs = rig.GetComponentsInChildren<Rigidbody>();
            foreach (var rb in rbs)
            {
                if ((object)rb == null) continue;
                if (teleport)
                {
                    rb.position = rb.position + delta;
                    if (!rb.isKinematic) rb.velocity = Vector3.zero;
                }
                else if (rb.isKinematic)
                    rb.MovePosition(rb.position + delta);
                else
                    rb.position += delta;
            }
            if (!teleport)
                WakeNsosNearGhostSweep(rootPos, target);
            if (_ghostMoveLogCount < 5 || _ghostMoveLogCount % 600 == 0)
                Log.LogInfo($"[P6.9 ghost] slot={slot} moved to {target} (delta={delta.magnitude:0.00} {(teleport?"TELEPORT":"sweep")})");
            _ghostMoveLogCount++;
        }

        // TrySpawnPlayer instantiates a Player rig in the active scene at the
        // slot's spawn point, by grabbing ControllerHandler.playerPrefab and
        // calling Object.Instantiate directly. This sidesteps the InputDevice
        // pairing path (which requires real input hardware) — the rig will
        // exist but won't move until we inject inputs.
        //
        // Returns (true, "") on success or (false, "reason") on failure.
        private void TryCachePlayerPrefab()
        {
            if ((object)_cachedPlayerPrefab != null) return;
            try
            {
                var chType = AccessTools.TypeByName("ControllerHandler");
                if ((object)chType == null) { Log.LogWarning("CachePrefab: ControllerHandler type missing"); return; }
                var chInst = UnityEngine.Object.FindObjectOfType(chType);
                if ((object)chInst == null) { Log.LogWarning("CachePrefab: no ControllerHandler instance in active scene"); return; }
                var pf = AccessTools.Field(chType, "playerPrefab");
                if ((object)pf == null) { Log.LogWarning("CachePrefab: playerPrefab field missing"); return; }
                var go = pf.GetValue(chInst) as GameObject;
                if ((object)go == null) { Log.LogWarning("CachePrefab: playerPrefab value is null"); return; }
                _cachedPlayerPrefab = go;
                Log.LogInfo($"CachePrefab: cached playerPrefab '{go.name}' for cross-scene spawns.");
            }
            catch (Exception e) { Log.LogError($"TryCachePlayerPrefab threw: {e.Message}"); }
        }

        private bool TrySpawnPlayer(int slot, Vector3 spawnPosOverride, out string err)
        {
            err = "";
            try
            {
                GameObject prefab = _cachedPlayerPrefab;
                if ((object)prefab == null)
                {
                    var chType = AccessTools.TypeByName("ControllerHandler");
                    if ((object)chType == null) { err = "ControllerHandler type not found"; return false; }
                    var chInst = UnityEngine.Object.FindObjectOfType(chType);
                    if ((object)chInst == null) { err = "ControllerHandler instance not in scene (and no cached prefab)"; return false; }
                    var prefabField = AccessTools.Field(chType, "playerPrefab");
                    if ((object)prefabField == null) { err = "playerPrefab field not found"; return false; }
                    prefab = prefabField.GetValue(chInst) as GameObject;
                    if ((object)prefab == null) { err = "playerPrefab is null"; return false; }
                    _cachedPlayerPrefab = prefab;
                    Log.LogInfo("Cached playerPrefab for cross-scene spawns.");
                }
                var spawnPos = spawnPosOverride; // caller-supplied; defaults to (0,8,0) in bridge handler
                var go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity) as GameObject;
                if ((object)go == null) { err = "Instantiate returned null"; return false; }
                go.name = $"OracleSpawn_Slot{slot}";
                // Survive SceneManager.LoadScene switches. Without this, the
                // rig is destroyed when we transition from MainScene (where
                // ControllerHandler lives, needed to spawn the rig) to a
                // Landfall scene (which has real platforms but no spawn
                // infrastructure).
                UnityEngine.Object.DontDestroyOnLoad(go);

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
        private static MethodInfo GetUpdateWithValueMethod()
        {
            if ((object)_cachedUpdateWithValue != null) return _cachedUpdateWithValue;
            var paType = AccessTools.TypeByName("InControl.PlayerAction");
            if ((object)paType == null)
            {
                if (!_loggedUpdateWithValue) { Log.LogWarning("UpdateWithValue: no InControl.PlayerAction type"); _loggedUpdateWithValue = true; }
                return null;
            }
            _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue",
                new Type[] { typeof(float), typeof(ulong), typeof(float) });
            if ((object)_cachedUpdateWithValue == null && !_loggedUpdateWithValue)
            {
                Log.LogWarning("UpdateWithValue: method not found on PlayerAction. Trying without param-type filter…");
                _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue");
                if ((object)_cachedUpdateWithValue == null) Log.LogWarning("UpdateWithValue: not found even without filter");
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
        // PushPlayerAction calls PlayerAction.UpdateWithValue(value, tick, dt)
        // on the named PlayerAction field of the given CharacterActions.
        // Mono 2.x: never compare MethodInfo/FieldInfo with != — use (object)x == null.
        private static void PushPlayerAction(object actions, string fieldName, float value)
        {
            if ((object)actions == null) return;
            var actionsType = actions.GetType();
            string cacheKey = actionsType.FullName + "|" + fieldName;
            FieldInfo f;
            if (!_pushFieldCache.TryGetValue(cacheKey, out f))
            {
                f = AccessTools.Field(actionsType, fieldName);
                _pushFieldCache[cacheKey] = f;
            }
            if ((object)f == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field not found on type {actionsType}"); _loggedPushPath = true; }
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
                _pushArgsBuffer[0] = value;
                _pushArgsBuffer[1] = (ulong)0;
                _pushArgsBuffer[2] = Time.deltaTime;
                m.Invoke(action, _pushArgsBuffer);
                if (!_loggedPushPath) { Log.LogInfo($"PushPlayerAction[{fieldName}]: invoke ok, value={value}"); _loggedPushPath = true; }
            }
            catch (Exception e)
            {
                if (!_loggedPushPath) { Log.LogError($"PushPlayerAction[{fieldName}] invoke threw: {e}"); _loggedPushPath = true; }
            }
        }

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
                float now = Time.realtimeSinceStartup;
                if (now - _writeInputsErrLogAt >= 5f)
                {
                    _writeInputsErrLogAt = now;
                    Log.LogWarning($"WriteInputsToRigs: {e.Message}");
                }
            }
        }
    }
}
