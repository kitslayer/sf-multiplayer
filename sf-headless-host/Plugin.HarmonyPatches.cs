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
        internal static bool InjectInputPrefix(object __instance)
        {
            try
            {
                _prefixCallCount++;
                if ((object)__instance == null) return true;
                var ctrlComp = __instance as Component;
                if ((object)ctrlComp == null) return true;
                var rig = ctrlComp.gameObject;

                int slot = -1;
                foreach (var kv in SlotToRig)
                {
                    if (kv.Value == rig) { slot = kv.Key; break; }
                }
                if (slot < 0)
                {
                    if (_prefixCallCount % 600 == 1)
                        Log.LogDebug($"InjectInputPrefix: rig {rig.name} not ours (prefix call #{_prefixCallCount})");
                    return true;
                }
                _prefixOurRigCount++;

                if (!SlotInputs.TryGetValue(slot, out var input)) return true;

                bool verbose = (_prefixOurRigCount == 1 || _prefixOurRigCount % 120 == 0);
                if (verbose)
                    Log.LogInfo($"[INSTR2a] Prefix entry: slot={slot} SlotInputs.stick=({input.StickX:0.00},{input.StickY:0.00}) ourCallCount={_prefixOurRigCount}");

                var actionsField = AccessTools.Field(__instance.GetType(), "mPlayerActions");
                if ((object)actionsField == null) return true;
                var actions = actionsField.GetValue(__instance);
                if ((object)actions == null) return true;

                // Read CURRENT Movement.X/Y BEFORE our write — tells us whether
                // InControl rebuilt it since our last write last frame.
                if (verbose)
                {
                    var preX = ReadAxis(actions, "Movement", "X");
                    var preY = ReadAxis(actions, "Movement", "Y");
                    Log.LogInfo($"[INSTR2b] PRE-write Movement=({preX:0.00},{preY:0.00})");
                }

                // Stuff our values into Movement.X / .Y backing fields and
                // Movement.thisValue. Read by the original Update body
                // immediately after this prefix returns.
                ForceTwoAxis(actions, "Movement", input.StickX, input.StickY);
                ForceTwoAxis(actions, "Aiming",   input.AimX,   input.AimY);

                // Read again AFTER write — confirms write took effect synchronously.
                if (verbose)
                {
                    var postX = ReadAxis(actions, "Movement", "X");
                    var postY = ReadAxis(actions, "Movement", "Y");
                    // Probe Controller early-return gate variables.
                    var ctrlT = __instance.GetType();
                    var inactiveF = AccessTools.Field(ctrlT, "inactive");
                    var infoF    = AccessTools.Field(ctrlT, "info");
                    var hasCtrlF = AccessTools.Field(ctrlT, "mHasControl");
                    bool inactive = (object)inactiveF != null && (bool)inactiveF.GetValue(__instance);
                    bool hasCtrl  = (object)hasCtrlF != null && (bool)hasCtrlF.GetValue(__instance);
                    bool isDead = false;
                    if ((object)infoF != null) {
                        var infoVal = infoF.GetValue(__instance);
                        if ((object)infoVal != null) {
                            var deadF = AccessTools.Field(infoVal.GetType(), "isDead");
                            if ((object)deadF != null) isDead = (bool)deadF.GetValue(infoVal);
                        }
                    }
                    // GameManager.inFight + stillInMenu — the prime suspects.
                    var gmT = AccessTools.TypeByName("GameManager");
                    bool inFight = false, stillInMenu = false;
                    if ((object)gmT != null) {
                        var fF = AccessTools.Field(gmT, "inFight");
                        var mF = AccessTools.Field(gmT, "stillInMenu");
                        if ((object)fF != null) inFight = (bool)fF.GetValue(null);
                        if ((object)mF != null) stillInMenu = (bool)mF.GetValue(null);
                    }
                    bool willEarlyReturn = inactive || isDead || (!inFight && !stillInMenu);
                    Log.LogInfo($"[INSTR2c] POST-write Movement=({postX:0.00},{postY:0.00}) inactive={inactive} hasControl={hasCtrl} isDead={isDead} inFight={inFight} stillInMenu={stillInMenu} willEarlyReturn={willEarlyReturn}");
                }

                // Button-action backing field updates. PlayerAction is a
                // OneAxisInputControl with private InputControlState
                // (lastState, thisState, nextState). The IsPressed/WasPressed
                // accessors read thisState. We reach into thisState's .State
                // bool and .Value float to set the press.
                ForceButton(actions, "Jump",        (input.Buttons & 0x01) != 0);
                ForceButton(actions, "Jump2",       (input.Buttons & 0x01) != 0);
                ForceButton(actions, "PunchOrFire", (input.Buttons & 0x02) != 0);
                ForceButton(actions, "Block",       (input.Buttons & 0x04) != 0);
                ForceButton(actions, "Throw",       (input.Buttons & 0x08) != 0);
            }
            catch (Exception e)
            {
                if (Verbose && Log != null) Log.LogDebug($"InjectInputPrefix: {e.Message}");
            }
            return true; // always let original Update run
        }

        // [INSTR3] MoveRight/MoveLeft entry-logger prefixes removed — they ran on
        // every frame a player moved and only logged; their diagnostic job is done.

        // Phase 6.5 Step 1 — IsServer=true. Static property getter so no __instance.
        internal static void IsServerPostfix(ref bool __result)
        {
            __result = true;
        }

        // Phase 6.5 Step 2d — IsNetworkMatch=true. Pin against Controller's reset.
        internal static void IsNetworkMatchPostfix(ref bool __result)
        {
            __result = true;
        }
        internal static void InitSyncedObjectsPostfix()
        {
            _initSyncedCallCount++;
            Log.LogInfo($"[P6.9 init] InitSyncedObjects called (#{_initSyncedCallCount}). PrepareMapForTravel reached settle-end on the oracle.");
        }
        internal static void InitMapDataObjectsPostfix()
        {
            _initMapDataCallCount++;
            Log.LogInfo($"[P6.9 init] InitMapDataObjects called (#{_initMapDataCallCount}).");
        }
        internal static void ReadyUpPostfix()
        {
            _readyUpCallCount++;
            Log.LogInfo($"[P6.9 init] MultiplayerManager.ReadyUp called (#{_readyUpCallCount}).");
        }
        internal static bool SetNetworkMatchPrefix(ref bool v)
        {
            _setNetMatchInterceptCount++;
            if (!v && _setNetMatchInterceptCount <= 5)
                Log.LogInfo($"[P6.5] SetNetworkMatch(false) intercepted #{_setNetMatchInterceptCount} → forcing true");
            v = true;
            return true; // run original with forced arg
        }
        internal static bool SpawnRandomWeaponPrefix(object __instance)
        {
            try
            {
                _srwCallCount++;
                // Reset randomWeaponCounter to a new value (mirrors original lines 252-264).
                var gmType = __instance.GetType();
                var rwcField = AccessTools.Field(gmType, "randomWeaponCounter");
                var extraField = AccessTools.Field(gmType, "extraSpawnWeaponTime");
                float extra = (object)extraField != null ? (float)extraField.GetValue(__instance) : 0f;
                if ((object)rwcField != null)
                {
                    float newWait = UnityEngine.Random.Range(5f, 8f) + extra;
                    rwcField.SetValue(__instance, newWait);
                }

                // Pick a weapon index. Honors the /weapons chat allow-list
                // if set, otherwise round-robin 0..7.
                int weaponIdx = PickWeaponId(_srwCallCount);

                // Spawn position mirroring original: Y=11*scale, Z=Random(-8,8).
                float zOff = UnityEngine.Random.Range(0f, 8f);
                if (_srwCallCount % 2 == 0) zOff *= -1f;
                float scale = 1f;
                var lastAppliedScaleF = AccessTools.Field(gmType, "LastAppliedScale");
                if ((object)lastAppliedScaleF != null)
                {
                    var v = lastAppliedScaleF.GetValue(__instance);
                    if (v is float f) scale = f;
                }
                Vector3 spawnPos = Vector3.up * (11f * scale) + Vector3.forward * zOff;

                // Find MultiplayerManager.SpawnWeapon and invoke directly. SF
                // host code's `mNetworkManager` field is private; we go via
                // FindObjectOfType.
                var mmType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mmType == null) return false;
                var mmInst = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)mmInst == null)
                {
                    var all = Resources.FindObjectsOfTypeAll(mmType);
                    if (all != null && all.Length > 0) mmInst = all[0];
                }
                if ((object)mmInst == null)
                {
                    if (_srwCallCount <= 3)
                        Log.LogWarning("[P6.5 SRW] MultiplayerManager instance is null; skipping.");
                    return false;
                }
                var spawnWeapon = AccessTools.Method(mmType, "SpawnWeapon", new[] { typeof(int), typeof(Vector3), typeof(bool) });
                if ((object)spawnWeapon == null)
                {
                    if (_srwCallCount <= 3)
                        Log.LogWarning("[P6.5 SRW] SpawnWeapon method not found.");
                    return false;
                }
                if (_srwCallCount <= 5 || _srwCallCount % 10 == 0)
                    Log.LogInfo($"[P6.5 SRW] call#{_srwCallCount} → SpawnWeapon(id={weaponIdx}, pos={spawnPos}, present=false)");
                spawnWeapon.Invoke(mmInst, new object[] { weaponIdx, spawnPos, false });
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5 SRW] threw: {e.Message}");
            }
            return false; // skip original SpawnRandomWeapon
        }
        internal static bool GetRandomWeaponIndexPrefix(bool mustBeActive, ref GameObject weaponObject, ref int __result)
        {
            _grwiCallCount++;
            weaponObject = null;
            __result = PickWeaponId(_grwiCallCount);
            if (_grwiCallCount <= 3 || _grwiCallCount % 5 == 0)
                Log.LogInfo($"[P6.5] GetRandomWeaponIndexPrefix call#{_grwiCallCount} → returning {__result}");
            return false; // skip original
        }
        internal static int PickWeaponId(int seed)
        {
            if (_allowedWeaponIds.Count > 0)
            {
                // Round-robin across the allow-list. Could randomize but
                // deterministic order makes tournaments more predictable.
                var arr = new int[_allowedWeaponIds.Count];
                _allowedWeaponIds.CopyTo(arr);
                System.Array.Sort(arr);
                int pick = arr[_allowedWeaponCycleIdx % arr.Length];
                _allowedWeaponCycleIdx++;
                return pick;
            }
            return seed % 8;
        }
        internal static bool SendBroadcastPrefix(object[] __args)
        {
            try
            {
                _p65BroadcastCount++;
                var data = __args.Length > 0 ? __args[0] as byte[] : null;
                // (byte)__args[1] on a boxed byte-backed enum may throw on
                // strict CLRs; Convert.ToInt32 unboxes via IConvertible and
                // works for either a raw byte or an enum.
                byte msgType = UnboxByte(__args.Length > 1 ? __args[1] : null);
                bool ignoreServer = __args.Length > 2 && __args[2] is bool b && b;
                // SF's signature is (..., ulong ignoreUserID, ...) — raw ulong,
                // NOT CSteamID. So a typed cast works, but use Convert for
                // robustness against future SF refactors.
                ulong ignoreUID = UnboxUlong(__args.Length > 3 ? __args[3] : null);

                int prev;
                _p65BroadcastByType.TryGetValue(msgType, out prev);
                _p65BroadcastByType[msgType] = prev + 1;

                bool first = prev == 0;
                bool sample = first || (_p65BroadcastByType[msgType] % 60 == 0);
                if (sample)
                {
                    Log.LogInfo($"[P6.5] HostBroadcast#{_p65BroadcastCount} msgType={msgType}({MsgTypeName(msgType)}) bodyLen={data?.Length ?? 0} ignoreSrv={ignoreServer} ignoreUID={ignoreUID} count[{msgType}]={_p65BroadcastByType[msgType]}");
                }
                // For ObjectUpdate, log the index every time so we can see
                // which scene NSOs are broadcasting (the index is the first
                // 2 bytes of the body, ushort LE).
                    if (msgType == 31 && data != null && (object)Instance != null)
                        Instance.CacheGroundWeaponsBroadcast(data);
                    if (msgType == 33 && sample)
                        Log.LogInfo($"[v26.6] Host MapInfoSync forward count={_p65BroadcastByType[msgType]} bodyLen={data?.Length ?? 0}");
                    if (msgType == 26 && data != null && data.Length >= 2 && _p65ObjUpdateIdxLogCount < 30)
                {
                    ushort idx = (ushort)(data[0] | (data[1] << 8));
                    if (!_p65ObjUpdateSeenIndices.Contains(idx))
                    {
                        _p65ObjUpdateSeenIndices.Add(idx);
                        _p65ObjUpdateIdxLogCount++;
                        Log.LogInfo($"[P6.5] ObjectUpdate from new index={idx} (total unique={_p65ObjUpdateSeenIndices.Count})");
                    }
                }

                // Phase 6.5 Step 2 — forward the broadcast through our v25
                // protocol so the real client actually receives it. SF's own
                // SendMessageToAllClients loop iterates mConnectedClients which
                // is empty on the oracle (we never registered the user there),
                // so SF's loop is a no-op. We do the actual delivery here.
                // SF's MsgType enum byte values match our v25 protocol's Pkt*
                // constants 1:1 for the first 38 entries.
                //
                // Phase 6.7 filter: if the oracle's own mirror rig generates
                // PlayerUpdate (10) or PlayerTalked (12) broadcasts, do NOT
                // forward them. The client already receives a relay of the
                // real player's PlayerUpdate via HandlePlayerUpdate, and an
                // oracle-rig PlayerUpdate would appear as a phantom 2nd
                // player on the client's screen.
                if ((object)Instance != null && data != null
                    && msgType != 10  // PktPlayerUpdate
                    && msgType != 12) // PktPlayerTalked
                {
                    // Extract the channel arg (index 5 in SendMessageToAllClients).
                    // The patched DLL routes incoming packets by channel — using
                    // channel 0 for everything sends them to CheckMessageType
                    // which throws "Messagetype X is not setup!" for things like
                    // ObjectUpdate that should go to NSO.ListenForPackages instead.
                    byte channel = 0;
                    if (__args.Length > 5)
                    {
                        try { channel = (byte)Convert.ToInt32(__args[5]); } catch { }
                    }
                    bool skip = false;
                    // For ObjectUpdate, filter out broadcasts where the object's
                    // Y position is out of int16 range (overflow artifact).
                    if (msgType == 26 && data.Length >= 4)
                    {
                        short posYmul100 = (short)(data[2] | (data[3] << 8));
                        if (posYmul100 < -3000)
                        {
                            skip = true;
                            if (_p65ObjUpdateFilterCount < 5 || _p65ObjUpdateFilterCount % 100 == 0)
                                Log.LogInfo($"[P6.5] Skipping ObjectUpdate forward — Y={posYmul100/100f:0.0} out of playable range (#{_p65ObjUpdateFilterCount})");
                            _p65ObjUpdateFilterCount++;
                        }
                    }
                    // BOXES/ICE VANISHING FIX (reverted from P0-11 Y-aware
                    // heuristic 2026-05-23 night). Back to drop-ALL for
                    // server-originated destructions. The Y-aware filter
                    // introduced random ice/chain breaks because chains
                    // stress-break on the oracle's scene under joint
                    // forces (above Y=-30 obviously) and we were forwarding
                    // those.
                    //
                    // The "ghost box" tradeoff (server destroys box, client
                    // still has it) is rare and recoverable. The "ice
                    // randomly breaks" was constant during play. Pick the
                    // less-bad failure mode.
                    //
                    // Legit destructions still propagate from clients via
                    // the INBOUND RelayBodyToAll path — kicking ice as a
                    // player still works because the player rig (dynamic)
                    // colliding with the ice (kinematic on client now)
                    // fires OnCollisionEnter → SendDestructMessage → server.
                    if (msgType == 28 || msgType == 29 || msgType == 30)
                    {
                        skip = (object)Instance != null
                            && Instance.ShouldSkipServerOriginatedDestruction(data, data?.Length ?? 0);
                        if (skip)
                        {
                            if (_p65DestructionFilterCount < 5 || _p65DestructionFilterCount % 50 == 0)
                                Log.LogInfo($"[destruction] Skip server-originated msgType={msgType} (#{_p65DestructionFilterCount}) — killbox/chain-load");
                            _p65DestructionFilterCount++;
                        }
                    }
                    if (!skip) Instance.ForwardBroadcastToV25Clients(msgType, data, ignoreUID, channel);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5] broadcast prefix threw: {e.Message}");
            }
            return true; // run original (no-op on oracle because mConnectedClients is empty)
        }

        // Forward an intercepted host broadcast through our v25 UDP socket.
        // Channel is critical — the patched DLL routes by channel; non-zero
        // channels (e.g. 10 for ObjectUpdate) dispatch via NSO.ListenForPackages,
        // while channel 0 goes to P2PPackageHandler.CheckMessageType.
        internal void ForwardBroadcastToV25Clients(byte msgType, byte[] body, ulong ignoreUID, byte channel = 0)
        {
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (!cli.Initialized) continue;
                if (ignoreUID != 0 && cli.SteamID == ignoreUID) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (sent > 0 && _p65BroadcastCount <= 5)
                Log.LogInfo($"[P6.5] Forwarded msgType={msgType}({MsgTypeName(msgType)}) bodyLen={body.Length} ch={channel} to {sent} v25 client(s).");
        }
        internal static bool SendDirectPrefix(object[] __args)
        {
            try
            {
                _p65DirectCount++;
                // args: [CSteamID clientID, byte[] data, MsgType type, EP2PSend, int channel]
                ulong sid = 0uL;
                if (__args.Length > 0 && __args[0] != null)
                {
                    var idObj = __args[0];
                    var f = AccessTools.Field(idObj.GetType(), "m_SteamID");
                    if ((object)f != null) sid = (ulong)f.GetValue(idObj);
                }
                var data = __args.Length > 1 ? __args[1] as byte[] : null;
                byte msgType = UnboxByte(__args.Length > 2 ? __args[2] : null);

                int prev;
                _p65DirectByType.TryGetValue(msgType, out prev);
                _p65DirectByType[msgType] = prev + 1;

                bool first = prev == 0;
                bool sample = first || (_p65DirectByType[msgType] % 60 == 0);
                if (sample)
                {
                    Log.LogInfo($"[P6.5] DirectSend#{_p65DirectCount} → sid={sid} msgType={msgType}({MsgTypeName(msgType)}) bodyLen={data?.Length ?? 0} count[{msgType}]={_p65DirectByType[msgType]}");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5] direct prefix threw: {e.Message}");
            }
            return true;
        }
        internal static Vector2 QuantizeMapSyncKey(Vector2 v)
        {
            float invQ = 1f / MapSyncKeyQuantum;
            return new Vector2(
                Mathf.Round(v.x * invQ) / invQ,
                Mathf.Round(v.y * invQ) / invQ);
        }

        // Prefix on MultiplayerManager.AddMapDataObject(Vector2, MapInfoSyncableBase).
        // We can't change a struct argument by ref in a Harmony prefix on a
        // non-out parameter, but we CAN modify the dictionary via the
        // postfix side. Instead, intercept by writing the quantized key
        // back into the MapInfoSyncableBase's m_StartPos AND replacing
        // the pos arg via the __args array (Harmony allows this).
        internal static bool AddMapDataObjectPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return true;
                if (!(__args[0] is Vector2 pos)) return true;
                var quantized = QuantizeMapSyncKey(pos);
                __args[0] = quantized;
                // Also update m_StartPos on the MapInfoSyncableBase so
                // outbound SyncMapData broadcasts the quantized key.
                if (__args[1] != null)
                {
                    var t = __args[1].GetType();
                    var f = AccessTools.Field(t, "m_StartPos");
                    if ((object)f != null) f.SetValue(__args[1], quantized);
                }
            }
            catch { }
            return true;
        }

        // Prefix on MultiplayerManager.OnMapDataRecieved(byte[]).
        // The body's first 8 bytes are the Vector2 key the server sent.
        // After our AddMapDataObjectPrefix the server's keys are already
        // quantized, so the wire's key matches our dict — no action
        // needed here. But: if for some reason the server didn't quantize
        // (older oracle, race), an inbound un-quantized key would still
        // miss our quantized dict. Rewrite the body's first 8 bytes to
        // the quantized form as a belt-and-suspenders.
        internal static bool OnMapDataRecievedPrefix(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 8) return true;
                float x = BitConverter.ToSingle(data, 0);
                float y = BitConverter.ToSingle(data, 4);
                if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)) return true;
                var q = QuantizeMapSyncKey(new Vector2(x, y));
                var xBytes = BitConverter.GetBytes(q.x);
                var yBytes = BitConverter.GetBytes(q.y);
                Buffer.BlockCopy(xBytes, 0, data, 0, 4);
                Buffer.BlockCopy(yBytes, 0, data, 4, 4);
            }
            catch { }
            return true;
        }
        private static void InstallMapInfoSyncQuantize()
        {
            if (_mapSyncQuantizeInstalled) return;
            _mapSyncQuantizeInstalled = true;
            try
            {
                var mgrType = AccessTools.TypeByName("MultiplayerManager");
                if ((object)mgrType == null) { Log.LogWarning("[P0-12] MultiplayerManager not found."); return; }
                var harmony = new Harmony(PluginGuid + ".mapsync-quantize");
                var addM = AccessTools.Method(mgrType, "AddMapDataObject");
                if ((object)addM != null)
                {
                    harmony.Patch(addM, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(AddMapDataObjectPrefix))));
                    Log.LogInfo("[P0-12] Patched MultiplayerManager.AddMapDataObject (quantize Vector2 key to 0.01).");
                }
                else Log.LogWarning("[P0-12] AddMapDataObject not found.");
                var recvM = AccessTools.Method(mgrType, "OnMapDataRecieved");
                if ((object)recvM != null)
                {
                    harmony.Patch(recvM, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(OnMapDataRecievedPrefix))));
                    Log.LogInfo("[P0-12] Patched MultiplayerManager.OnMapDataRecieved (quantize inbound Vector2 key).");
                }
                else Log.LogWarning("[P0-12] OnMapDataRecieved not found.");
            }
            catch (Exception e) { Log.LogWarning($"[P0-12] install failed: {e.Message}"); }
        }

        private static void InstallClientModePatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid + ".client-shim");

                var nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                if ((object)nsoType == null) { Log.LogWarning("[CLIENT] NetworkSyncableObject type not found."); return; }

                // REVERTED 2026-05-23 night — the "dynamic NSOs locally"
                // patch (Patch 1) caused random ice/chain destruction events
                // during normal gameplay: each client's local box physics
                // would do NSO-on-NSO collisions that fire DestructiblePiece
                // .Collide → SendDestructMessage → server → all clients see
                // a spurious break.
                //
                // Stock SF kinematic NSOs on clients:
                //   - NSO-on-NSO collisions don't fire OnCollisionEnter
                //     (both bodies kinematic — no contact resolution)
                //   - Player-on-NSO collisions still fire (player rig is
                //     dynamic) — so kicking ice still destructs correctly,
                //     just via the server-relay round-trip
                //   - Cost: pushing a box has ~RTT latency before it
                //     visually moves locally (the v26 snapshot drives it)
                //
                // Net: stable destruction model > instant push feedback.
                // Tradeoff is the same one P0-5's original fix took.
                Log.LogInfo("[CLIENT] Stock-default kinematic NSOs (no DisableAllRigidBodies skip — prevents spurious NSO-on-NSO destruction events).");
            }
            catch (Exception e)
            {
                Log.LogError($"[CLIENT] Client-mode shim install failed: {e}");
            }
        }
        // Generic skip-prefix: return false to skip the original method.
        internal static bool SkipPrefix() => false;
        internal static bool IsPacketAvailableHeadlessPrefix(object __instance, int channel, ref bool __result)
        {
            if (!_batchModeHost) return true;
            try
            {
                if (!_ppChannelsLookupTried)
                {
                    _ppChannelsLookupTried = true;
                    _ppChannelsField = AccessTools.Field(__instance.GetType(), "channels");
                }
                var chField = _ppChannelsField;
                if ((object)chField == null) { __result = false; return false; }
                var channels = chField.GetValue(__instance) as Array;
                if (channels == null || channel < 0 || channel >= channels.Length)
                {
                    __result = false;
                    return false;
                }
                if (channels.GetValue(channel) == null)
                {
                    __result = false;
                    return false;
                }
            }
            catch
            {
                __result = false;
                return false;
            }
            return true;
        }
        private static void TryPatch(Harmony harmony, string label, MethodInfo target, string prefix = null, string postfix = null)
        {
            _p65PatchesAttempted++;
            if ((object)target == null)
            {
                _p65MissingPatches.Add($"{label} — target method not found");
                Log.LogError($"[P6.5] SKIP {label} — target method not found.");
                return;
            }
            try
            {
                var pfx = prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(Plugin), prefix)) : null;
                var pst = postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(Plugin), postfix)) : null;
                harmony.Patch(target, prefix: pfx, postfix: pst);
                _p65PatchesSucceeded++;
                Log.LogInfo($"[P6.5] Patched {label}.");
            }
            catch (Exception e)
            {
                _p65MissingPatches.Add($"{label} — {e.GetType().Name}: {e.Message}");
                Log.LogError($"[P6.5] FAIL {label}: {e}");
            }
        }

        // Safe-unbox helpers. Mono's runtime is permissive about typed casts on
        // boxed enums, but a direct `(byte)box` can throw InvalidCastException
        // on a stricter CLR. Convert.* uses IConvertible which handles both
        // raw primitives and byte-backed enums uniformly.
        private static byte UnboxByte(object o)
        {
            if (o == null) return (byte)255;
            try { return (byte)Convert.ToInt32(o); } catch { return (byte)255; }
        }
        private static ulong UnboxUlong(object o)
        {
            if (o == null) return 0uL;
            try { return Convert.ToUInt64(o); } catch { return 0uL; }
        }

        // SF MsgType enum (P2PPackageHandler.MsgType byte values, from decompile).
        private static string MsgTypeName(byte b) => b switch
        {
            0  => "Ping",
            1  => "PingResponse",
            2  => "ClientJoined",
            3  => "ClientRequestingAccepting",
            4  => "ClientAccepted",
            5  => "ClientInit",
            6  => "ClientRequestingIndex",
            7  => "ClientRequestingToSpawn",
            8  => "ClientSpawned",
            9  => "ClientReadyUp",
            10 => "PlayerUpdate",
            11 => "PlayerTookDamage",
            12 => "PlayerTalked",
            13 => "PlayerForceAdded",
            14 => "PlayerForceAddedAndBlock",
            15 => "PlayerLavaForceAdded",
            16 => "PlayerFallOut",
            17 => "PlayerWonWithRicochet",
            18 => "MapChange",
            19 => "WeaponSpawned",
            20 => "WeaponThrown",
            21 => "RequestingWeaponThrow",
            22 => "ClientRequestWeaponDrop",
            23 => "WeaponDropped",
            24 => "WeaponWasPickedUp",
            25 => "ClientRequestingWeaponPickUp",
            26 => "ObjectUpdate",
            27 => "ObjectSpawned",
            28 => "ObjectSimpleDestruction",
            29 => "ObjectInvokeDestructionEvent",
            30 => "ObjectDestructionCollision",
            31 => "GroundWeaponsInit",
            32 => "MapInfo",
            33 => "MapInfoSync",
            34 => "WorkshopMapsLoaded",
            35 => "StartMatch",
            36 => "ObjectHello",
            37 => "OptionsChanged",
            38 => "KickPlayer",
            _  => "?"
        };

        private static float ReadAxis(object actions, string fieldName, string axis)
        {
            try
            {
                var f = AccessTools.Field(actions.GetType(), fieldName);
                if ((object)f == null) return 0f;
                var ctrl = f.GetValue(actions);
                if ((object)ctrl == null) return 0f;
                var t = ctrl.GetType();
                var backing = AccessTools.Field(t, "<" + axis + ">k__BackingField");
                if ((object)backing != null) return (float)backing.GetValue(ctrl);
                var prop = AccessTools.Property(t, axis);
                if ((object)prop != null) return (float)prop.GetValue(ctrl, null);
            }
            catch { }
            return float.NaN;
        }

        // Force the named TwoAxisInputControl's X/Y to (x, y) by writing
        // its private fields. Run from the Harmony prefix on Controller.Update.
        private static void ForceTwoAxis(object actions, string fieldName, float x, float y)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, new Vector2(x, y));
            var xBacking = AccessTools.Field(t, "<X>k__BackingField");
            var yBacking = AccessTools.Field(t, "<Y>k__BackingField");
            if ((object)xBacking != null) xBacking.SetValue(ctrl, x);
            if ((object)yBacking != null) yBacking.SetValue(ctrl, y);
        }

        // Force a button's IsPressed / Value via InputControlState struct
        // backing fields. PlayerAction.thisState is an InputControlState
        // struct with public bool State and public float Value; reading
        // IsPressed returns thisState.State.
        private static void ForceButton(object actions, string fieldName, bool pressed)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var pa = f.GetValue(actions);
            if ((object)pa == null) return;
            // OneAxisInputControl has private thisState field.
            var thisStateField = AccessTools.Field(pa.GetType(), "thisState");
            if ((object)thisStateField == null) return;
            // thisState is a struct. We have to box, mutate, write back.
            object state = thisStateField.GetValue(pa);
            if ((object)state == null) return;
            var stateType = state.GetType();
            var stateField = AccessTools.Field(stateType, "State");
            var valueField = AccessTools.Field(stateType, "Value");
            if ((object)stateField != null) stateField.SetValue(state, pressed);
            if ((object)valueField != null) valueField.SetValue(state, pressed ? 1.0f : 0.0f);
            thisStateField.SetValue(pa, state);
        }

        private void TryPatchHealthHandlerDieForRoundAdvance(Harmony harmony)
        {
            try
            {
                var hhType = AccessTools.TypeByName("HealthHandler");
                if ((object)hhType == null) { Log.LogWarning("[DEATH] HealthHandler type not found."); return; }
                var dieMethod = AccessTools.Method(hhType, "Die");
                if ((object)dieMethod == null) dieMethod = AccessTools.Method(hhType, "OnDeath");
                if ((object)dieMethod == null) { Log.LogWarning("[DEATH] HealthHandler.Die not found."); return; }
                var postfix = AccessTools.Method(typeof(Plugin), nameof(HealthHandlerDiePostfix));
                harmony.Patch(dieMethod, postfix: new HarmonyMethod(postfix));
                Log.LogInfo("[DEATH] Patched HealthHandler.Die — schedules round advance when isDead.");
            }
            catch (Exception e) { Log.LogWarning($"[DEATH] HealthHandler patch failed: {e.Message}"); }
        }

        private static void HealthHandlerDiePostfix(object __instance)
        {
            if ((object)Instance == null) return;
            Instance.OnOraclePlayerDied(__instance, "HealthHandler.Die");
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
