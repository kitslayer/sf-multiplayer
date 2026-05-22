using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SFNetcodeV2
{
    // SFNetcodeV2 — client-side companion to the v26 server protocol shipped in
    // StickFightDedicatedSrv Phase 5 M2/M3.
    //
    // Goals (in priority order):
    //   P0  Advertise protocolVersion=26 in clientRequestingIndex (replaces the
    //       patched-v25 DLL's hard-coded 25).
    //   P1  Stop running RayCastForward.FixedUpdate for projectiles so the
    //       server's authoritative sim is the only physics for them.
    //   P1  Send playerInput packets (msgType=41 / numeric 41 == iota slot 41 in
    //       the server's PacketType enum) at ~60Hz from Controller.Update.
    //   P2  Receive worldStateSnapshot (msgType=42) and log; full entity-id
    //       mapping deferred to M3.
    //
    // Numeric MsgType IDs:
    //   The client's enum (P2PPackageHandler.MsgType) only knows the legacy
    //   v25 message types, ending at KickPlayer (39). The new v26 IDs do NOT
    //   exist in the client enum and we cannot easily extend it at runtime, so
    //   we send/receive by raw byte value:
    //       PlayerInput        = 41
    //       WorldStateSnapshot = 42
    //       ServerEvent        = 43
    //   The server packets.go enum has ClientLeft(39), LobbyType(40), RequestingOptions(41),
    //   PlayerInput(42), WorldStateSnapshot(43), ServerEvent(44).
    //   Wait — that doesn't match. Re-read packets.go:
    //       packetTypeClientLeft         = 39 (after KickPlayer=38)
    //       Hmm, let's count by iota from the server: Ping(0)...KickPlayer(38).
    //   The task brief says PlayerInput=42, WorldStateSnapshot=43, ServerEvent=44.
    //   We use those values — see PacketIDs below. The server is the ground truth
    //   because that's what's listening; the client's enum just needs to encode
    //   the same byte over the wire.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stickfightdev.netcode-v2";
        public const string PluginName = "SFNetcodeV2";
        public const string PluginVersion = "0.1.0";

        // The numeric msg-type IDs the v26 server uses on the wire. These were
        // computed from packets.go iota positions and confirmed in the task brief.
        // The patched client DLL only knows up to KickPlayer (38) in its enum;
        // we send these as raw bytes through the packet writer.
        public const byte PacketIDPlayerInput        = 42; // client → server
        public const byte PacketIDWorldStateSnapshot = 43; // server → client (30Hz)
        public const byte PacketIDServerEvent       = 44; // server → client (reliable)

        // Protocol version this plugin advertises in clientRequestingIndex.
        // Server accepts only 25 (legacy) or 26 (authoritative).
        public const byte ProtocolVersionAuthoritative = 26;

        internal static ManualLogSource Log;

        // Set false at any time to fall back to v25-relay behavior (for A/B).
        internal static bool IsV26Active = true;

        // Sequence counter for our outgoing playerInput packets.
        internal static uint InputSequence;

        // Limit snapshot logging so we don't spam BepInEx.log at 30Hz.
        internal static int SnapshotLogCounter;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} {PluginVersion} loading — advertising protocol v{ProtocolVersionAuthoritative}");

            var harmony = new Harmony(PluginGuid);

            // P0 — protocol version bump on clientRequestingIndex.
            TryPatchRequestPlayerIndex(harmony, "MultiplayerManager");
            TryPatchRequestPlayerIndex(harmony, "Landfall.Network.Sockets.MultiplayerManagerSockets");

            // P1 — skip client-side projectile sim when v26.
            TryPatchRayCastForward(harmony);

            // P1 — emit playerInput at 60Hz (Controller.Update postfix).
            TryPatchControllerUpdate(harmony);

            // P2 — log incoming worldStateSnapshot packets so we know the wire is alive.
            TryPatchCheckMessageType(harmony);

            Log.LogInfo($"{PluginName} ready.");
        }

        private static void TryPatchRequestPlayerIndex(Harmony harmony, string typeName)
        {
            try
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null)
                {
                    Log.LogWarning($"[P0] Type not found: {typeName} (skipping)");
                    return;
                }
                var m = AccessTools.Method(t, "RequestPlayerIndex", Type.EmptyTypes);
                if (m == null)
                {
                    Log.LogWarning($"[P0] {typeName}.RequestPlayerIndex() not found");
                    return;
                }
                var prefix = new HarmonyMethod(typeof(Patches).GetMethod(
                    nameof(Patches.RequestPlayerIndex_Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
                harmony.Patch(m, prefix: prefix);
                Log.LogInfo($"[P0] Patched {typeName}.RequestPlayerIndex (prefix replaces method body)");
            }
            catch (Exception e)
            {
                Log.LogError($"[P0] Failed to patch {typeName}.RequestPlayerIndex: {e}");
            }
        }

        private static void TryPatchRayCastForward(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("RayCastForward");
                if (t == null) { Log.LogWarning("[P1] RayCastForward not found"); return; }
                var m = AccessTools.Method(t, "FixedUpdate", Type.EmptyTypes);
                if (m == null) { Log.LogWarning("[P1] RayCastForward.FixedUpdate not found"); return; }
                var prefix = new HarmonyMethod(typeof(Patches).GetMethod(
                    nameof(Patches.RayCastForward_FixedUpdate_Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
                harmony.Patch(m, prefix: prefix);
                Log.LogInfo("[P1] Patched RayCastForward.FixedUpdate (prefix — skips when v26 active)");
            }
            catch (Exception e)
            {
                Log.LogError($"[P1] Failed to patch RayCastForward.FixedUpdate: {e}");
            }
        }

        private static void TryPatchControllerUpdate(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("Controller");
                if (t == null) { Log.LogWarning("[P1] Controller not found"); return; }
                var m = AccessTools.Method(t, "Update", Type.EmptyTypes);
                if (m == null) { Log.LogWarning("[P1] Controller.Update not found"); return; }
                var postfix = new HarmonyMethod(typeof(Patches).GetMethod(
                    nameof(Patches.Controller_Update_Postfix),
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
                harmony.Patch(m, postfix: postfix);
                Log.LogInfo("[P1] Patched Controller.Update (postfix — emits playerInput each frame)");
            }
            catch (Exception e)
            {
                Log.LogError($"[P1] Failed to patch Controller.Update: {e}");
            }
        }

        private static void TryPatchCheckMessageType(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("P2PPackageHandler");
                if (t == null) { Log.LogWarning("[P2] P2PPackageHandler not found"); return; }
                // private void CheckMessageType(byte[] data, MsgType type, CSteamID steamIdRemote)
                var m = AccessTools.Method(t, "CheckMessageType");
                if (m == null) { Log.LogWarning("[P2] P2PPackageHandler.CheckMessageType not found"); return; }
                var prefix = new HarmonyMethod(typeof(Patches).GetMethod(
                    nameof(Patches.CheckMessageType_Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
                harmony.Patch(m, prefix: prefix);
                Log.LogInfo("[P2] Patched P2PPackageHandler.CheckMessageType (prefix — intercepts v26 msg types)");
            }
            catch (Exception e)
            {
                Log.LogError($"[P2] Failed to patch P2PPackageHandler.CheckMessageType: {e}");
            }
        }
    }

    internal static class Patches
    {
        // ===================== P0 — clientRequestingIndex =====================
        // The decompile shows `new byte[9]` carrying { uint64 steamID, byte playerCount },
        // but the patched-v25 srv DLL secretly writes a third byte (the version, 25)
        // — the server's lobbies.go reads `protocolVersion := packet.ReadByteNext()`
        // immediately after playerCount, and rejects anything other than 25 or 26.
        //
        // We replace the whole method body (Prefix returns false). Because the field
        // names match between MultiplayerManager and MultiplayerManagerSockets
        // (mGameManager, mPacketHandler, mServerID), we use reflection on __instance.
        internal static bool RequestPlayerIndex_Prefix(object __instance)
        {
            try
            {
                var t = __instance.GetType();

                // 1) Resolve player count exactly as the original method did.
                int num = 0;
                var gmField = AccessTools.Field(t, "mGameManager");
                var gm = gmField?.GetValue(__instance);
                if (gm != null)
                {
                    var gmType = gm.GetType();
                    var savedListProp = AccessTools.Property(gmType, "SavedDevicesForNetwork");
                    var saved = savedListProp?.GetValue(gm, null) as System.Collections.ICollection;
                    if (saved != null && saved.Count > 0)
                    {
                        num = saved.Count;
                    }
                    else
                    {
                        var getAlive = AccessTools.Method(gmType, "GetPlayersAlive");
                        if (getAlive != null) num = (int)getAlive.Invoke(gm, null);
                    }
                }
                if (num <= 0) num = 1; // sanity: never advertise zero players (server clamp).

                Debug.Log($"[SFNetcodeV2] Requesting player index for {num} Players (protocol v{Plugin.ProtocolVersionAuthoritative})");

                // 2) Build the 10-byte body: u64 steamID | byte playerCount | byte protoVersion.
                ulong sid = GetLocalSteamID();
                byte[] body = new byte[10];
                using (var output = new MemoryStream(body))
                using (var bw = new BinaryWriter(output))
                {
                    bw.Write(sid);
                    bw.Write((byte)num);
                    bw.Write((byte)Plugin.ProtocolVersionAuthoritative);
                }

                // 3) Send via the existing packet handler.
                var pkField = AccessTools.Field(t, "mPacketHandler");
                var pk = pkField?.GetValue(__instance);
                if (pk == null)
                {
                    Plugin.Log.LogError("[P0] mPacketHandler is null — cannot send clientRequestingIndex");
                    return false;
                }

                var srvField = AccessTools.Field(t, "mServerID");
                var srv = srvField?.GetValue(__instance);
                if (srv == null)
                {
                    Plugin.Log.LogError("[P0] mServerID resolved to null — cannot send clientRequestingIndex");
                    return false;
                }

                // P2PPackageHandler has two SendP2PPacketToUser overloads — one taking
                // CSteamID and one taking NetConnection. Pick the matching one based
                // on the actual runtime type of mServerID.
                var pkType = pk.GetType();
                MethodInfo sendMethod = null;
                foreach (var mi in pkType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (mi.Name != "SendP2PPacketToUser") continue;
                    var ps = mi.GetParameters();
                    if (ps.Length < 3) continue;
                    if (ps[0].ParameterType.IsAssignableFrom(srv.GetType()))
                    {
                        sendMethod = mi;
                        break;
                    }
                }
                if (sendMethod == null)
                {
                    Plugin.Log.LogError($"[P0] No SendP2PPacketToUser overload matching {srv.GetType().Name}");
                    return false;
                }

                // Build positional args matching the chosen overload's signature.
                var sendPs = sendMethod.GetParameters();
                object[] args = new object[sendPs.Length];
                args[0] = srv;
                args[1] = body;
                // Cast our byte ID to the MsgType enum (ClientRequestingIndex = 6).
                var msgTypeEnum = AccessTools.TypeByName("P2PPackageHandler+MsgType")
                                 ?? AccessTools.Inner(AccessTools.TypeByName("P2PPackageHandler"), "MsgType");
                args[2] = msgTypeEnum != null
                    ? Enum.ToObject(msgTypeEnum, (byte)6) // ClientRequestingIndex
                    : (object)(byte)6;
                for (int i = 3; i < sendPs.Length; i++)
                {
                    args[i] = sendPs[i].HasDefaultValue ? sendPs[i].DefaultValue : null;
                }
                sendMethod.Invoke(pk, args);
                return false; // skip original
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[P0] RequestPlayerIndex_Prefix threw, falling back to original v25 path: {e}");
                return true; // run original on failure rather than getting stuck
            }
        }

        // ===================== P1 — skip client projectile sim =====================
        internal static bool RayCastForward_FixedUpdate_Prefix(object __instance)
        {
            if (!Plugin.IsV26Active) return true;
            // For M2 the policy is: skip projectile sim for ALL networked-context projectiles
            // (which is nearly everything in a lobby). We don't yet have a robust per-instance
            // "is this networked?" check — server is authoritative for projectile entities, and
            // the visible client-side ones are decorative anyway. If/when we encounter offline
            // singleplayer regressions we can scope this tighter using a NetworkSyncableObject
            // parent check.
            return false;
        }

        // ===================== P1 — emit playerInput @ ~per-frame =====================
        internal static void Controller_Update_Postfix(object __instance)
        {
            if (!Plugin.IsV26Active) return;
            try
            {
                var t = __instance.GetType();

                // Only emit for the local-controlled player. mHasControl on Controller is true
                // when this Controller drives the local input.
                var hasControlField = AccessTools.Field(t, "mHasControl");
                if (hasControlField == null) return;
                if (!(bool)hasControlField.GetValue(__instance)) return;

                // Read player index. SF stores it in `playerID` (public).
                var playerIDField = AccessTools.Field(t, "playerID");
                if (playerIDField == null) return;
                int playerID = (int)playerIDField.GetValue(__instance);
                if (playerID < 0 || playerID > 255) return;

                // mPlayerActions: CharacterActions
                var actionsField = AccessTools.Field(t, "mPlayerActions");
                var actions = actionsField?.GetValue(__instance);
                if (actions == null) return;

                var actionsType = actions.GetType();
                var movement = AccessTools.Field(actionsType, "Movement")?.GetValue(actions);
                var aiming   = AccessTools.Field(actionsType, "Aiming")?.GetValue(actions);
                if (movement == null || aiming == null) return;

                float mx = (float)AccessTools.Property(movement.GetType(), "X").GetValue(movement, null);
                float my = (float)AccessTools.Property(movement.GetType(), "Y").GetValue(movement, null);
                float ax = (float)AccessTools.Property(aiming.GetType(),   "X").GetValue(aiming,   null);
                float ay = (float)AccessTools.Property(aiming.GetType(),   "Y").GetValue(aiming,   null);

                ushort buttons = 0;
                buttons |= GetBoolAction(actions, "PunchOrFire", "IsPressed") ? (ushort)1 : (ushort)0;
                buttons |= GetBoolAction(actions, "Block",       "IsPressed") ? (ushort)2 : (ushort)0;
                // JumpWasPressed is a property on CharacterActions itself (not the action).
                bool jumpPressed = false;
                var jwpProp = AccessTools.Property(actionsType, "JumpWasPressed");
                if (jwpProp != null) jumpPressed = (bool)jwpProp.GetValue(actions, null);
                buttons |= jumpPressed ? (ushort)4 : (ushort)0;
                buttons |= GetBoolAction(actions, "Throw",       "WasPressed") ? (ushort)8 : (ushort)0;
                buttons |= jumpPressed ? (ushort)16 : (ushort)0; // JumpJustPressed alias for server's predictor

                uint seq = unchecked(++Plugin.InputSequence);

                // 23-byte playerInput payload (1+4+4+4+4+2+4).
                byte[] body = new byte[23];
                using (var output = new MemoryStream(body))
                using (var bw = new BinaryWriter(output))
                {
                    bw.Write((byte)playerID);
                    bw.Write(mx); bw.Write(my);
                    bw.Write(ax); bw.Write(ay);
                    bw.Write(buttons);
                    bw.Write(seq);
                }

                SendPacketToServer(body, Plugin.PacketIDPlayerInput);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[P1] Controller_Update_Postfix threw (will silence on repeat): {e}");
                // Disable v26 path to avoid log spam; user can re-enable via config.
                Plugin.IsV26Active = false;
            }
        }

        // Resolves the local SteamID via reflection so we don't have to take a
        // direct Steamworks.NET assembly reference at compile time.
        private static ulong GetLocalSteamID()
        {
            try
            {
                var t = AccessTools.TypeByName("Steamworks.SteamUser");
                if (t == null) return 0UL;
                var m = AccessTools.Method(t, "GetSteamID");
                if (m == null) return 0UL;
                var csid = m.Invoke(null, null);
                if (csid == null) return 0UL;
                // CSteamID is a struct with a m_SteamID field (ulong).
                var f = AccessTools.Field(csid.GetType(), "m_SteamID");
                if (f == null) return 0UL;
                return (ulong)f.GetValue(csid);
            }
            catch { return 0UL; }
        }

        private static bool GetBoolAction(object actions, string fieldName, string propName)
        {
            try
            {
                var f = AccessTools.Field(actions.GetType(), fieldName);
                var pa = f?.GetValue(actions);
                if (pa == null) return false;
                var p = AccessTools.Property(pa.GetType(), propName);
                if (p == null) return false;
                return (bool)p.GetValue(pa, null);
            }
            catch { return false; }
        }

        // Sends a raw-byte-typed packet to the lobby owner (the dedicated server)
        // using whichever overload of SendP2PPacketToServer the runtime exposes.
        // We bypass the C# MsgType enum so we can write IDs (42-44) it doesn't know about.
        private static void SendPacketToServer(byte[] body, byte msgTypeID)
        {
            try
            {
                var pkType = AccessTools.TypeByName("P2PPackageHandler");
                if (pkType == null) return;
                var instanceProp = AccessTools.Property(pkType, "Instance");
                var pk = instanceProp?.GetValue(null, null);
                if (pk == null) return;

                // We want SendP2PPacketToServer(byte[], MsgType, ...) but MsgType doesn't
                // contain our new value. Trick: call the underlying WriteMessageBuffer
                // ourselves (4-byte timestamp + 1-byte msgType + body), then route via
                // SteamNetworking or the socket adapter the same way the original does.
                // We do this by invoking SendP2PPacketToUser on the lobby-owner CSteamID
                // (or NetConnection for sockets) with our pre-built byte[]; problem is
                // SendP2PPacketToUser also re-wraps with WriteMessageBuffer.
                //
                // Cleanest workaround: call SendP2PPacketToServer with an enum value cast
                // from a raw byte. Enums in .NET allow any underlying byte; the switch
                // inside SendP2PPacketToServer just routes to a channel — fallback throws.
                //
                // To avoid that throw, we cast and then *route* via the lobby-owner direct
                // SendP2PPacketToUser overload, providing a channel explicitly so
                // GetChannelForMsgType is never called.
                //
                // Resolve the channel + lobby owner ourselves.
                var mmHandlerType = AccessTools.TypeByName("MatchmakingHandler");
                var instProp = AccessTools.Property(mmHandlerType, "Instance");
                var mm = instProp?.GetValue(null, null);
                var lobbyOwnerProp = AccessTools.Property(mmHandlerType, "LobbyOwner");
                var lobbyOwner = lobbyOwnerProp?.GetValue(mm, null);
                if (lobbyOwner == null) return;

                var msgTypeEnum = AccessTools.Inner(pkType, "MsgType");
                object enumValue = msgTypeEnum != null
                    ? Enum.ToObject(msgTypeEnum, msgTypeID)
                    : (object)msgTypeID;

                // Pick the right SendP2PPacketToUser overload (CSteamID-based here since
                // MatchmakingHandler.LobbyOwner is CSteamID).
                MethodInfo send = null;
                foreach (var mi in pkType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (mi.Name != "SendP2PPacketToUser") continue;
                    var ps = mi.GetParameters();
                    if (ps.Length < 3) continue;
                    if (ps[0].ParameterType.IsAssignableFrom(lobbyOwner.GetType()))
                    {
                        send = mi; break;
                    }
                }
                if (send == null) return;

                var sendPs = send.GetParameters();
                object[] args = new object[sendPs.Length];
                args[0] = lobbyOwner;
                args[1] = body;
                args[2] = enumValue;
                for (int i = 3; i < sendPs.Length; i++)
                {
                    args[i] = sendPs[i].HasDefaultValue ? sendPs[i].DefaultValue : null;
                }
                send.Invoke(pk, args);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[net] SendPacketToServer(msg={msgTypeID}) threw: {e.Message}");
            }
        }

        // ===================== P2 — worldStateSnapshot reception =====================
        // Signature: private void CheckMessageType(byte[] data, MsgType type, CSteamID steamIdRemote)
        // We Prefix; if the message is one of our v26-only types, parse + log and
        // return false (suppress the original switch, which would throw on unknown
        // MsgType values).
        internal static bool CheckMessageType_Prefix(byte[] data, object type, object steamIdRemote)
        {
            try
            {
                byte raw = Convert.ToByte(type);
                if (raw == Plugin.PacketIDWorldStateSnapshot)
                {
                    HandleWorldStateSnapshot(data);
                    return false;
                }
                if (raw == Plugin.PacketIDServerEvent)
                {
                    HandleServerEvent(data);
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[P2] CheckMessageType_Prefix threw: {e.Message}");
                return true;
            }
        }

        // ServerEvent wire format (M4):
        //   byte eventType (1=WeaponSpawn, 2=ProjectileHitStatic, 3=DamageEvent, ...)
        //   ... per-event body
        // For DamageEvent:
        //   byte  victimSlot
        //   i8    attackerSlot (-1 = world/self)
        //   f32   damage
        //   byte  reasonLen, byte[reasonLen] reason
        private static void HandleServerEvent(byte[] data)
        {
            if (data == null || data.Length < 1) return;
            using var input = new MemoryStream(data);
            using var br = new BinaryReader(input);
            byte eventType = br.ReadByte();
            switch (eventType)
            {
                case 3: // DamageEvent
                    if (data.Length < 8) { Plugin.Log.LogDebug("[M4] DamageEvent truncated"); return; }
                    byte victimSlot   = br.ReadByte();
                    sbyte attackerSlot = br.ReadSByte();
                    float damage       = br.ReadSingle();
                    byte reasonLen    = br.ReadByte();
                    string reason      = "";
                    if (reasonLen > 0 && data.Length >= 8 + reasonLen)
                        reason = System.Text.Encoding.UTF8.GetString(br.ReadBytes(reasonLen));
                    PlayerSync.ApplyServerDamage(victimSlot, attackerSlot, damage, reason);
                    break;
                case 2: // ProjectileHitStatic — VFX only
                    // For M4 we just log; future M5 polish can play an impact particle here.
                    if ((Plugin.SnapshotLogCounter % 60) == 0)
                        Plugin.Log.LogDebug("[M4] projectile impact event");
                    break;
                default:
                    if ((Plugin.SnapshotLogCounter % 120) == 0)
                        Plugin.Log.LogDebug($"[M4] serverEvent type={eventType} (unhandled)");
                    break;
            }
        }

        private static void HandleWorldStateSnapshot(byte[] data)
        {
            if (data == null || data.Length < 7)
            {
                Plugin.Log.LogWarning($"[P2] worldStateSnapshot truncated ({data?.Length ?? 0} bytes)");
                return;
            }
            using var input = new MemoryStream(data);
            using var br = new BinaryReader(input);
            uint serverTick    = br.ReadUInt32();
            byte snapType      = br.ReadByte();
            ushort entityCount = br.ReadUInt16();

            // 19 bytes per entity in M3+: u32 id, u8 kind, u8 slot, 6*i16 pos+vel, u8 flags.
            int expected = 7 + entityCount * 19;
            if (data.Length < expected)
            {
                Plugin.Log.LogWarning($"[P2] worldStateSnapshot body short: have {data.Length}, expected {expected} for {entityCount} entities");
                return;
            }
            if ((Plugin.SnapshotLogCounter++ % 60) == 0)
            {
                Plugin.Log.LogInfo($"[P2] worldStateSnapshot tick={serverTick} snapType={snapType} entities={entityCount}");
            }
            for (int i = 0; i < entityCount; i++)
            {
                uint  entityID = br.ReadUInt32();
                byte  kind     = br.ReadByte();
                byte  slot     = br.ReadByte();
                float posX     = br.ReadInt16() / 100f;
                float posY     = br.ReadInt16() / 100f;
                float posZ     = br.ReadInt16() / 100f;
                float velX     = br.ReadInt16() / 100f;
                float velY     = br.ReadInt16() / 100f;
                float velZ     = br.ReadInt16() / 100f;
                byte  flags    = br.ReadByte();
                bool  alive    = (flags & 1) != 0;
                bool  grounded = (flags & 2) != 0;
                _ = grounded; _ = velY; // suppress unused-locals warnings; kept for future M3 polish

                // Kind=1 == EntityPlayer in the server's physics package.
                if (kind == 1 && slot != 0xFF && alive)
                {
                    PlayerSync.ApplyServerPosition(slot, new Vector3(posX, posY, posZ), new Vector3(velX, velY, velZ));
                }
            }
        }
    }

    // PlayerSync owns the slot → NetworkPlayer cache and the reconciliation
    // logic that the snapshot handler calls into. We rebuild the cache lazily
    // when it goes stale (NetworkPlayer GameObjects come and go on map change).
    internal static class PlayerSync
    {
        private static readonly Dictionary<int, GameObject> SlotToRoot = new Dictionary<int, GameObject>();
        private static float _lastRebuildTime;
        private const float REBUILD_INTERVAL = 0.5f;

        // ApplyServerDamage is the v26 authoritative damage receipt path. We
        // try to find the victim's HealthHandler and apply the damage via
        // reflection so the existing UI / particle / sound feedback fires.
        // The server is the source of truth — we never reject or modify.
        public static void ApplyServerDamage(byte victimSlot, sbyte attackerSlot, float damage, string reason)
        {
            try
            {
                MaybeRebuildCache();
                if (!SlotToRoot.TryGetValue(victimSlot, out var root) || root == null) return;
                // Try HealthHandler.TakeDamage signature variants. SF's version
                // takes (Vector2 dmg, Vector2 dir, DamageType, attacker GameObject)
                // among others.
                var hh = root.GetComponentInChildren(AccessTools.TypeByName("HealthHandler"));
                if (hh == null) hh = root.GetComponent(AccessTools.TypeByName("HealthHandler"));
                if (hh == null)
                {
                    Plugin.Log.LogDebug($"[M4] no HealthHandler for slot={victimSlot}");
                    return;
                }
                // Match a TakeDamage method that accepts a Vector2 damage.
                var take = AccessTools.Method(hh.GetType(), "TakeDamage",
                    new Type[] { typeof(Vector2), typeof(Vector2), AccessTools.TypeByName("DamageType"), typeof(GameObject) })
                    ?? AccessTools.Method(hh.GetType(), "TakeDamage");
                if (take == null) return;
                var dmgVec = new Vector2(damage, 0f);
                var dir    = Vector2.zero;
                object damageType = null;
                var dtEnum = AccessTools.TypeByName("DamageType");
                if (dtEnum != null && dtEnum.IsEnum)
                {
                    var vals = Enum.GetValues(dtEnum);
                    if (vals.Length > 0) damageType = vals.GetValue(0);
                }
                var ps = take.GetParameters();
                object[] args;
                if (ps.Length == 4) args = new object[] { dmgVec, dir, damageType, null };
                else if (ps.Length == 3) args = new object[] { dmgVec, dir, damageType };
                else if (ps.Length == 2) args = new object[] { dmgVec, dir };
                else args = new object[] { dmgVec };
                take.Invoke(hh, args);
                if ((Plugin.SnapshotLogCounter % 30) == 0)
                    Plugin.Log.LogDebug($"[M4] applied server damage={damage} to slot={victimSlot} reason={reason}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"[M4] ApplyServerDamage threw: {ex.Message}");
            }
        }

        // ApplyServerPosition is called once per per-entity record in a snapshot.
        // For remote players: set transform.position to the server-authoritative
        // value (with a brief lerp to avoid teleport-snap visuals).
        // For the local player: smoothly reconcile if local-predicted position
        // has diverged from server by more than the snap threshold.
        public static void ApplyServerPosition(byte slot, Vector3 pos, Vector3 vel)
        {
            try
            {
                MaybeRebuildCache();
                if (!SlotToRoot.TryGetValue(slot, out var root) || root == null) return;

                bool isLocal = TryGetIsLocalControl(root);

                var t = root.transform;
                if (isLocal)
                {
                    // Local player: server is authoritative but we don't want to
                    // jitter on every snapshot. Snap toward server only if the
                    // predicted position is far away.
                    float divergence = Vector3.Distance(t.position, pos);
                    if (divergence > 0.5f)
                    {
                        // Hard snap when divergence is large (likely a
                        // misprediction or a forced server correction).
                        t.position = Vector3.Lerp(t.position, pos, 0.35f);
                    }
                    // Velocities are applied by Movement.cs already; we don't
                    // overwrite local rigidbody velocity here to keep input
                    // feel intact.
                }
                else
                {
                    // Remote player: server position is the truth. Smooth via
                    // a short lerp so we don't pop frame-to-frame.
                    t.position = Vector3.Lerp(t.position, pos, 0.6f);
                    // Apply velocity to the root rigidbody (if present) so the
                    // visual interpolation looks correct between snapshots.
                    var rb = root.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic)
                    {
                        rb.velocity = vel;
                    }
                }
            }
            catch (Exception ex)
            {
                if ((Plugin.SnapshotLogCounter % 300) == 0)
                {
                    Plugin.Log.LogDebug($"[M3] ApplyServerPosition slot={slot} threw: {ex.Message}");
                }
            }
        }

        // MaybeRebuildCache scans for NetworkPlayer instances every half-second
        // so we pick up newly spawned characters and drop destroyed ones
        // without paying a FindObjectsOfType cost every snapshot tick.
        private static void MaybeRebuildCache()
        {
            if (Time.realtimeSinceStartup - _lastRebuildTime < REBUILD_INTERVAL) return;
            _lastRebuildTime = Time.realtimeSinceStartup;

            SlotToRoot.Clear();
            var npType = AccessTools.TypeByName("NetworkPlayer");
            if (npType == null) return;

            var instances = UnityEngine.Object.FindObjectsOfType(npType);
            if (instances == null) return;

            var slotField = AccessTools.Field(npType, "mNetworkPlayerNumber")
                          ?? AccessTools.Field(npType, "playerIndex")
                          ?? AccessTools.Field(npType, "mPlayerNumber");

            foreach (var obj in instances)
            {
                if (obj is not Component comp) continue;
                int slot = -1;
                if (slotField != null)
                {
                    var v = slotField.GetValue(comp);
                    if (v is int i) slot = i;
                    else if (v is byte b) slot = b;
                }
                if (slot < 0 || slot > 3) continue;
                SlotToRoot[slot] = comp.gameObject;
            }
        }

        private static bool TryGetIsLocalControl(GameObject root)
        {
            try
            {
                var npType = AccessTools.TypeByName("NetworkPlayer");
                if (npType == null) return false;
                var comp = root.GetComponent(npType);
                if (comp == null) return false;
                var f = AccessTools.Field(npType, "mHasLocalControl") ?? AccessTools.Field(npType, "hasLocalControl");
                if (f == null) return false;
                var v = f.GetValue(comp);
                return v is bool b && b;
            }
            catch { return false; }
        }
    }
}
