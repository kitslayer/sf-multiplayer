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

        // Pickup: re-broadcast incoming ClientRequestingWeaponPickUp body as
        // WeaponWasPickedUp to ALL clients (including the sender, so their
        // local game updates the weapon-attached state). Body is identical.
        private void HandlePickupRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 3) { Log.LogWarning($"[SF] pickup request too short ({len} bytes)"); return; }
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            byte playerIdx = body[0];
            ushort weaponNetId = (ushort)(body[1] | (body[2] << 8));
            Log.LogInfo($"[SF] Pickup: player={playerIdx} weapon={weaponNetId} → broadcasting WeaponWasPickedUp");
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponWasPickedUp, body, 0uL, 1); // channel 1 (weapon-events)
            }
        }
        private void HandleDropRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 7) { Log.LogWarning($"[SF] drop request too short ({len} bytes)"); return; }
            byte[] body = new byte[len + 4];
            Buffer.BlockCopy(data, off, body, 0, len);
            ushort wid = _droppedWeaponNextId++;
            ushort sid = _droppedSyncableNextId++;
            body[len + 0] = (byte)(wid & 0xFF);
            body[len + 1] = (byte)((wid >> 8) & 0xFF);
            body[len + 2] = (byte)(sid & 0xFF);
            body[len + 3] = (byte)((sid >> 8) & 0xFF);
            Log.LogInfo($"[SF] Drop: assigning weaponSpawnID={wid} syncableID={sid}");
            // WeaponDropped is in P2PPackageHandler.CheckMessageType (line 268),
            // dispatched on channel 0 in the patched-DLL routing.
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponDropped, body, 0uL, 0);
            }
        }

        // Throw: client sends RequestingWeaponThrow (21) — same shape as drop
        // structurally: SF's OnPlayerThrowWeapon appends weaponSpawnID +
        // syncableObjectSpawnID and broadcasts as WeaponThrown (20).
        // WeaponThrown is NOT in CheckMessageType; it's dispatched via
        // NetworkPlayer.ListenForEventPackages on the SENDER's mEventChannel
        // (= slot*2 + 3). Wrong channel → packet arrives but nothing listens.
        private void HandleThrowRequest(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 1) { Log.LogWarning($"[SF] throw request too short ({len} bytes)"); return; }
            byte[] body = new byte[len + 4];
            Buffer.BlockCopy(data, off, body, 0, len);
            ushort wid = _droppedWeaponNextId++;
            ushort sid = _droppedSyncableNextId++;
            body[len + 0] = (byte)(wid & 0xFF);
            body[len + 1] = (byte)((wid >> 8) & 0xFF);
            body[len + 2] = (byte)(sid & 0xFF);
            body[len + 3] = (byte)((sid >> 8) & 0xFF);
            byte throwChannel = (byte)(sender.Slot * 2 + 3);
            Log.LogInfo($"[SF] Throw: assigning weaponSpawnID={wid} syncableID={sid} (incoming bodyLen={len}, slot={sender.Slot} → channel={throwChannel})");
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktWeaponThrown, body, 0uL, throwChannel);
            }

            // Phase 6.18 (throw-auth, SHADOW) — also model the throw as a server-side
            // projectile so its hit registration is FPS-independent. Seeded from this same
            // msgType-21 body, so detection needs no client change. Log-only for now.
            try { RegisterThrownWeaponShadow(sender, data, off, len); }
            catch (Exception ex) { Log.LogWarning($"[throw-auth] shadow register threw: {ex.Message}"); }
        }

        // Decode a RequestingWeaponThrow (21) body and register a server-side projectile
        // mirroring the thrown weapon's flight, so the hit is computed authoritatively on
        // the fixed server tick (fps-independent) instead of by each client's render-rate
        // ThrownWeapon.LateUpdate raycast.
        //
        // Body (NetworkPlayer.ThrowWeapon, real throw = 10 bytes):
        //   u8  justDrop           (0 for a real throw; 1 = empty-drop, no force)
        //   u8  weaponIndex
        //   i16 posY * 100   (LE)  ShortVector2 — throw origin Y  (SF plays on the YZ plane)
        //   i16 posZ * 100   (LE)  ShortVector2 — throw origin Z
        //   i8  rotY * 100         ByteVector2  — weapon rotation (unused here)
        //   i8  rotZ * 100         ByteVector2
        //   i8  aimY * 100         ByteVector2  — throw direction Y
        //   i8  aimZ * 100         ByteVector2  — throw direction Z
        private void RegisterThrownWeaponShadow(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 10) return;            // 8-byte body = justDrop, or malformed → no projectile
            if (data[off] != 0) return;      // justDrop==true → vanilla just releases it, no throw force
            if (sender.Slot < 0 || sender.Slot > 3) return;

            short sy = (short)(data[off + 2] | (data[off + 3] << 8));
            short sz = (short)(data[off + 4] | (data[off + 5] << 8));
            sbyte aY = (sbyte)data[off + 8];
            sbyte aZ = (sbyte)data[off + 9];
            Vector3 origin = new Vector3(0f, sy / 100f, sz / 100f);
            Vector3 aim    = new Vector3(0f, aY / 100f, aZ / 100f);
            if (aim.sqrMagnitude < 0.0001f) return;   // no aim direction → not a real throw
            aim.Normalize();

            // Self-validate the decode: the throw origin should sit on top of the thrower's rig.
            string val = "rig=?";
            if (SlotToRig.TryGetValue(sender.Slot, out var rig) && (object)rig != null)
                val = $"rigPos={rig.transform.position} Δ={Vector3.Distance(origin, rig.transform.position):0.00}u";

            var p = new Projectile
            {
                Id          = _nextProjId++,
                OwnerSlot   = (byte)sender.Slot,
                WeaponType  = ThrownWeaponType,
                Position    = origin,
                Velocity    = aim * ThrownWeaponSpeed,
                BornAt      = Time.realtimeSinceStartup,
                LifetimeSec = ThrownWeaponLifetime,
                IsThrown    = true,
                ShadowOnly  = _throwAuthShadow,
            };
            _projectiles.Add(p);
            Log.LogInfo($"[throw-auth] SHADOW throw registered: id={p.Id} slot={sender.Slot} origin={origin} aim={aim} v={ThrownWeaponSpeed}u/s {val} rawlen={len}");
        }

        // === packet handlers ===

        private void HandlePing(SfClient cli, byte[] data, int off, int len)
        {
            // Echo the body back as PingResponse.
            byte[] body = new byte[len];
            if (len > 0) System.Buffer.BlockCopy(data, off, body, 0, len);
            SendSfPacket(cli.Addr, PktPingResponse, body, cli.SteamID, 0);
        }

        private void HandleClientRequestingAccepting(SfClient cli)
        {
            Log.LogInfo($"[SF] ClientRequestingAccepting from {cli.Addr}; sending ClientAccepted.");
            SendSfPacket(cli.Addr, PktClientAccepted, new byte[0], cli.SteamID, 0);
            cli.Accepted = true;
        }

        // ClientRequestingIndex → ClientInit. Per reference_patched_dll_protocol.md:
        // Response body (50 bytes for solo Landfall-0):
        //   byte accept (1)
        //   byte playerIndex (assigned slot)
        //   byte maxPlayers (4)
        //   byte mapType (0 = Landfall)
        //   i32 mapSize (4)
        //   i32 sceneIndex
        //   for slot 0..3: u64 slotSteamID + (stats if non-joiner non-empty)
        //   u16 weaponCount (0)
        //   4 bytes networkOptions (mapToggle, health, regen, weaponSpawnRate)
        private void HandleClientRequestingIndex(SfClient cli, byte[] data, int off, int len)
        {
            // Body layout (per patched DLL's OnPlayerRequestingIndex):
            //   u64 SteamID  +  u8 clientPlayerCount  (+ optional protocol-version byte)
            // The SteamID here is the AUTHORITATIVE client identity — the
            // wrapper-tail steamID is 0 on join. Without this, our ClientInit
            // tells the client slot 0 has SteamID 0 → client doesn't match
            // it against its local Steam ID → ControlledLocally stays false
            // → no ClientRequestingToSpawn → stuck in lobby.
            if (len >= 8)
            {
                ulong newSid = ReadU64LE(data, off);
                if (cli.SteamID != 0 && cli.SteamID != newSid)
                    Log.LogWarning($"[SF DEBUG] cli {cli.Addr} slot={cli.Slot} SteamID CHANGING {cli.SteamID} → {newSid} (in HandleClientRequestingIndex)");
                cli.SteamID = newSid;
            }
            byte playerCount = (len >= 9) ? data[off + 8] : (byte)1;

            // Evict any prior _sfClients entry with the same SteamID — this
            // is a reconnect, and we want to reuse the original slot so the
            // client's view of "I am slot N" matches the oracle's view.
            // Without this, slot AllocSlot picks the next free slot (1, 2, …)
            // and the client's channel-routed packets (e.g. throw on
            // slot*2+3) go to wrong channels.
            if (cli.SteamID != 0)
            {
                List<string> evict = null;
                foreach (var kv in _sfClients)
                {
                    var other = kv.Value;
                    if (other == cli) continue;
                    if (other.SteamID == cli.SteamID)
                    {
                        if (evict == null) evict = new List<string>();
                        evict.Add(kv.Key);
                        Log.LogInfo($"[SF] Evicting stale reconnect: SteamID={other.SteamID} was on {kv.Key} slot={other.Slot}; new conn from {cli.Addr} reusing slot {other.Slot}.");
                        cli.Slot = other.Slot;
                        // (A2) Invalidate the prior occupant's slot-keyed state so
                        // it doesn't outlive them: the stale v26 endpoint (re-set on
                        // the reconnect's first input) and the death-handled mark
                        // (else the reused slot is treated as already-dead and the
                        // reconnected player's death won't advance the round).
                        _slotV26Endpoint.Remove(other.Slot);
                        _deathSlotsHandled.Remove(other.Slot);
                        // Also drop the prior occupant's last InputFrame: otherwise it
                        // drives the reused slot's new rig as a one-frame phantom (a held
                        // throw/fire/move) before the reconnecting client's first packet.
                        SlotInputs.Remove(other.Slot);
                    }
                }
                if (evict != null) foreach (var k in evict) _sfClients.Remove(k);
            }

            // Assign a slot only if eviction didn't reuse one.
            int slot = cli.Slot >= 0 ? cli.Slot : AllocSlot(cli);
            if (slot < 0)
            {
                // H-P1-2 — all 4 slots taken: don't send ClientInit (the client
                // will keep retrying / time out) and don't keep a tracked entry
                // that would receive broadcasts for a player who never joined.
                Log.LogWarning($"[SF] Server full — rejecting ClientRequestingIndex from {cli.Addr} steamID={cli.SteamID}.");
                _sfClients.Remove(cli.Addr.ToString());
                return;
            }
            cli.Slot = slot;
            Log.LogInfo($"[SF] ClientRequestingIndex from {cli.Addr} steamID={cli.SteamID} players={playerCount}; assigning slot {slot}; building ClientInit.");

            // The chosen Landfall scene to push (matches what oracle has loaded).
            int sceneIndex = 0; // MainScene first; the lobby UI flows from there.

            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write((byte)1);            // accept
                bw.Write((byte)slot);         // playerIndex
                bw.Write((byte)4);            // maxPlayers — patched-only field
                bw.Write((byte)0);            // mapType 0=Landfall
                bw.Write((int)4);             // mapSize
                bw.Write((int)sceneIndex);    // mapData (sceneIndex)

                // 4-slot loop
                for (int s = 0; s < 4; s++)
                {
                    if (s == slot)
                    {
                        bw.Write(cli.SteamID);          // u64 — joiner's own steamID
                    }
                    else
                    {
                        // Find any other connected client in slot s.
                        SfClient other = null;
                        foreach (var kv in _sfClients) if (kv.Value.Slot == s) { other = kv.Value; break; }
                        if (other != null)
                        {
                            bw.Write(other.SteamID);
                            // 13 × int32 stats (zeros for now)
                            for (int i = 0; i < 13; i++) bw.Write((int)0);
                            bw.Write((int)0); // colorCount (patched-only) — 0 = default
                        }
                        else
                        {
                            bw.Write((ulong)0);
                        }
                    }
                }
                bw.Write((ushort)0);          // weaponCount
                bw.Write((byte)0);            // mapToggle
                bw.Write((byte)100);          // health
                bw.Write((byte)1);            // regen
                bw.Write((byte)1);            // weaponSpawnRate

                byte[] body = ms.ToArray();
                SendSfPacket(cli.Addr, PktClientInit, body, cli.SteamID, 0);
                cli.Initialized = true;
            }

            // Post-init bundle. Per sfdsrv comment: without these (specifically
            // OptionsChanged) the client never sends ClientRequestingToSpawn
            // and the user gets stuck at a black/lobby screen.
            // WorkshopMapsLoaded: u16 count + count×u64 workshopIDs. We send 0 maps.
            SendSfPacket(cli.Addr, PktWorkshopMapsLoaded, new byte[] { 0, 0 }, cli.SteamID, 1);
            // OptionsChanged: 4 bytes [maps, health, regen, weaponSpawnRate].
            // weaponSpawnRate=2 stops the client from requesting weapon spawns.
            SendSfPacket(cli.Addr, PktOptionsChanged, new byte[] { 0, 100, 1, 2 }, cli.SteamID, 0);
            Log.LogInfo($"[SF] Post-init bundle sent (WorkshopMapsLoaded + OptionsChanged).");
        }

        // ClientRequestingToSpawn → ClientSpawned broadcast.
        // Incoming body: byte playerIndex + 6 × float32 (pos + euler) = 25 bytes
        // Outgoing body: byte playerIndex + 6×f32 + bool spawnFlag + i32 colorCount = 30 bytes
        private void HandleClientRequestingToSpawn(SfClient cli, byte[] data, int off, int len)
        {
            if (len < 25) { Log.LogWarning($"[SF] short spawn body len={len}"); return; }
            byte pIdx  = data[off];
            float px = ReadF32LE(data, off + 1);
            float py = ReadF32LE(data, off + 5);
            float pz = ReadF32LE(data, off + 9);
            float rx = ReadF32LE(data, off + 13);
            float ry = ReadF32LE(data, off + 17);
            float rz = ReadF32LE(data, off + 21);
            Log.LogInfo($"[SF] ClientRequestingToSpawn slot={pIdx} pos=({px:0.0},{py:0.0},{pz:0.0})");

            using (var ms = new System.IO.MemoryStream())
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                bw.Write(pIdx);
                bw.Write(px); bw.Write(py); bw.Write(pz);
                bw.Write(rx); bw.Write(ry); bw.Write(rz);
                bw.Write((byte)0);    // spawnFlag false = RevivePlayer at pos
                bw.Write((int)0);     // colorCount (patched-only)
                byte[] spawnBody = ms.ToArray();

                // ORDER MATTERS: existing clients need PktClientJoined BEFORE
                // PktClientSpawned so their mConnectedClients[slot] is
                // populated before OnPlayerSpawned reads it (line 1623 of
                // MultiplayerManager.cs decompile: reads slot then accesses
                // .ControlledLocally; null slot → NullRef → broken rig).
                //
                // Body: u8 slot + u64 steamID LE.
                byte[] joinBody = new byte[9];
                joinBody[0] = (byte)cli.Slot;
                ulong sid = cli.SteamID;
                for (int b = 0; b < 8; b++) joinBody[1 + b] = (byte)(sid >> (8 * b));
                int notified = 0;
                foreach (var kv in _sfClients)
                {
                    if (kv.Value == cli) continue;
                    SendSfPacket(kv.Value.Addr, PktClientJoined, joinBody, cli.SteamID, 0);
                    notified++;
                }
                if (notified > 0)
                    Log.LogInfo($"[SF] step1: sent PktClientJoined slot={cli.Slot} steamID={cli.SteamID} → {notified} existing client(s)");

                // Now safe to broadcast ClientSpawned. New client gets their
                // own echo; existing clients have mConnectedClients[slot]
                // populated so OnPlayerSpawned can read it cleanly.
                BroadcastSfPacket(PktClientSpawned, spawnBody, cli.SteamID, 0);
                Log.LogInfo($"[SF] step2: broadcast PktClientSpawned slot={cli.Slot} pos=({px:0.0},{py:0.0},{pz:0.0}) to all {_sfClients.Count} client(s)");
            }
            cli.Spawned = true;
            SendCachedGroundWeaponsToClient(cli);

            // Match no longer auto-starts. Players spawn into the lobby and
            // wait for /start in chat. Host can type /start to begin.

            // Phase 6.15.1 — welcome message via chat. Sent once per spawn so
            // the player knows the server's identity + commands available.
            // ALKA's sendJoinHelpMessages does the same on the Go server side.
            if (!cli.SentWelcome)
            {
                cli.SentWelcome = true;
                string code = Environment.GetEnvironmentVariable("SF_LOBBY_CODE") ?? "?";
                SendChatToPlayer(cli, $"Welcome to lobby {code}. Type /help for commands.");
            }
        }
        private void HandleClientReadyUp(SfClient cli, byte[] data, int off, int len)
        {
            // Match no longer auto-starts on ClientReadyUp. Host types /start
            // in chat. ClientReadyUp is still logged so we can see the
            // ready-button-walk-through.
            Log.LogInfo($"[SF] ClientReadyUp from {cli.Addr} bodyLen={len} — ignored; waiting for /start chat command.");
            if (_matchStarted && _pendingClientStartMatchFired)
            {
                Log.LogInfo($"[SF] Match already started; re-sending StartMatch to {cli.Addr} only.");
                SendSfPacket(cli.Addr, PktStartMatch, new byte[0], 0, 0);
            }
        }
        private void HandlePlayerInput(byte[] data, int off, int len, IPEndPoint from)
        {
            if (len < 25) return;
            uint seq    = (uint)(data[off] | (data[off + 1] << 8) | (data[off + 2] << 16) | (data[off + 3] << 24));
            byte slot   = data[off + 4];
            float sx    = BitConverter.ToSingle(data, off + 5);
            float sy    = BitConverter.ToSingle(data, off + 9);
            float ax    = BitConverter.ToSingle(data, off + 13);
            float ay    = BitConverter.ToSingle(data, off + 17);
            uint btns   = (uint)(data[off + 21] | (data[off + 22] << 8) | (data[off + 23] << 16) | (data[off + 24] << 24));

            // Defensive validation — drop obvious garbage / cheaty inputs so
            // they don't poison InjectInputPrefix → Movement.cs. Conservative:
            // accept slightly-over-1.0 magnitudes (analog stick noise) but
            // reject NaN/Inf/huge values. Phase 6.16+ slot↔SteamID validation
            // would also live here.
            if (slot > 3
                || float.IsNaN(sx) || float.IsInfinity(sx) || sx < -1.5f || sx > 1.5f
                || float.IsNaN(sy) || float.IsInfinity(sy) || sy < -1.5f || sy > 1.5f
                || float.IsNaN(ax) || float.IsInfinity(ax) || ax < -1.5f || ax > 1.5f
                || float.IsNaN(ay) || float.IsInfinity(ay) || ay < -1.5f || ay > 1.5f)
            {
                _inputPacketsDropped++;
                if (_inputPacketsDropped == 1 || _inputPacketsDropped % 50 == 0)
                    Log.LogWarning($"[P6.12] Dropped malformed PlayerInput: slot={slot} stick=({sx:0.00},{sy:0.00}) aim=({ax:0.00},{ay:0.00}) — total dropped {_inputPacketsDropped}");
                return;
            }
            // Clamp stick magnitudes to canonical [-1,1] so SF's Movement
            // doesn't see sub-noise > 1.0 values that bypass its own clamps.
            sx = Mathf.Clamp(sx, -1f, 1f);
            sy = Mathf.Clamp(sy, -1f, 1f);
            ax = Mathf.Clamp(ax, -1f, 1f);
            ay = Mathf.Clamp(ay, -1f, 1f);

            // H-P0-1 — slot ↔ source binding. Only the handshaken client that
            // owns this slot may drive it or move its snapshot endpoint. The
            // v26 socket uses a different PORT than the game socket, so we
            // bind at ADDRESS granularity. This also gates the P0-13 keyframe
            // reply (H-P1-1: ~20x amplification to spoofed sources otherwise).
            // Known residual: clients sharing one address (same NAT / same
            // machine / router-forwarded flows, which all arrive from the
            // router's address) can still cross-drive each other's slots —
            // the router's per-IP flow caps and the rate guards bound that.
            SfClient owner = null;
            foreach (var kv in _sfClients)
            {
                if (kv.Value.Slot == slot) { owner = kv.Value; break; }
            }
            if (owner == null || (object)owner.Addr == null || (object)from == null
                || !owner.Addr.Address.Equals(from.Address))
            {
                _inputPacketsDropped++;
                if (_inputPacketsDropped == 1 || _inputPacketsDropped % 200 == 0)
                    Log.LogWarning($"[P6.12] Dropped PlayerInput for slot {slot} from non-owner {from} (owner addr={(owner == null || (object)owner.Addr == null ? "<none>" : owner.Addr.Address.ToString())}) — total dropped {_inputPacketsDropped}");
                return;
            }
            SlotInputs[slot] = new InputFrame
            {
                StickX  = sx,
                StickY  = sy,
                AimX    = ax,
                AimY    = ay,
                Buttons = (int)btns,
            };
            // Stamp LastInputSeq AND refresh LastSeen on the owner. Without the
            // LastSeen update, a client that finished the lobby handshake and is
            // streaming v26 PlayerInput at 60Hz still got swept as "stale" (the
            // game-socket LastSeen went cold) → the player dropped from the
            // server seconds after connecting. v26 input IS proof of life.
            owner.LastInputSeq = seq;
            owner.LastSeen = Time.realtimeSinceStartup;
            // Record this client's v26 source addr — server snapshots get sent
            // back to this same IP:port (client uses single bidirectional socket).
            if (!_slotV26Endpoint.TryGetValue(slot, out var existing) || !existing.Equals(from))
            {
                _slotV26Endpoint[slot] = from;
                Log.LogInfo($"[P6.12] Slot {slot} v26 endpoint → {from}");
                // P0-13 — send a full-keyframe snapshot to this new
                // endpoint so it learns the current position of every
                // NSO, not just the ones currently moving. The regular
                // snapshot stream filters at-rest NSOs; without this
                // keyframe a late-joining client would never learn the
                // box positions until something pushed them.
                if (_matchStarted)
                {
                    try { SendKeyframeSnapshotToEndpoint(from); }
                    catch (Exception ex) { Log.LogWarning($"[P0-13] keyframe send failed: {ex.Message}"); }
                }
            }
            _inputPacketsRx++;
            if (_inputPacketsRx == 1 || _inputPacketsRx % 300 == 0)
                Log.LogInfo($"[P6.12] PlayerInput #{_inputPacketsRx} slot={slot} seq={seq} stick=({sx:0.00},{sy:0.00}) btns=0x{btns:X}");
        }

        // PlayerUpdate from client → broadcast to all OTHER clients (so they
        // render this player) AND teleport this client's auth ghost rig to
        // the reported position so it can physically push boxes server-side.
        //
        // CHANNEL ROUTING IS CRITICAL: SF's NetworkPlayer.InitNetworkSpawnID
        // assigns `mUpdateChannel = slot * 2 + 2`, and incoming packets get
        // dispatched to the matching NetworkPlayer by channel. Forwarding on
        // channel 0 (our old behavior) means the receiving client doesn't
        // route the update to the sender's NetworkPlayer — the remote player
        // appears frozen. We must forward on the SAME channel we received on.
        //
        // The incoming channel encodes the sender's slot, so we don't need
        // to look it up — just preserve the byte.
        //
        // Body format (from NetworkPlayer.SyncClientState): first 4 bytes
        // are posY + posZ as int16 / 100.
        private void HandlePlayerUpdate(SfClient cli, byte[] data, int off, int len, byte channel)
        {
            byte[] body = new byte[len];
            if (len > 0) System.Buffer.BlockCopy(data, off, body, 0, len);
            foreach (var kv in _sfClients)
            {
                if (kv.Value == cli) continue;
                // Gate on Initialized (set after ClientInit) — NOT on Spawned,
                // which BroadcastStartMatch resets to false at /start. Stock SF
                // expects re-spawn via new ClientRequestingToSpawn, but during
                // the gap PlayerUpdates would otherwise stop forwarding. Causes
                // the 'movement syncs in lobby but not after /start' bug.
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, PktPlayerUpdate, body, cli.SteamID, channel);
            }

            if (len < 4 || cli.Slot < 0) return;
            short rawY = (short)(body[0] | (body[1] << 8));
            short rawZ = (short)(body[2] | (body[3] << 8));
            float py = rawY / 100f;
            float pz = rawZ / 100f;
            UpdateGhostRigPosition(cli.Slot, new Vector3(0f, py, pz));
        }
    }
}
