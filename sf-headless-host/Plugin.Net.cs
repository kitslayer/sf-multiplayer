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

        // Per-client connection state. Keyed by remote address:port string so
        // the same SF instance keeps its slot/SteamID across packets.
        private class SfClient
        {
            public IPEndPoint Addr;
            public ulong SteamID;
            public int Slot;
            public float LastSeen;
            public bool Accepted;
            public bool Initialized;
            public bool Spawned;
            // Phase 6.12 — last PktPlayerInput sequence number consumed for
            // this slot. Phase 6.12.2 will stamp this into outgoing snapshots
            // so the client can do reconciliation replay.
            public uint LastInputSeq;
            // Phase 6.15.1 — has the server-emitted welcome chat been sent yet?
            public bool SentWelcome;
            // H-P0-3 — granted via /admin <pass> (or SF_ADMIN_STEAMIDS match).
            public bool IsAdmin;
        }

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

        // ==================================================================
        // PATH A — sfdsrv-compatible raw UDP v25 protocol, server side
        // ==================================================================
        //
        // Wire format (mirrors packets.go::AsBytes):
        //   [u32 timestamp LE][u8 msgType][N body][u64 steamID LE][u8 channel]
        //
        // Minimum packet size: 14 bytes (5 SOPH header + 9 EOPH trailer).

        private void DrainSfServer()
        {
            if ((object)_sfServer == null) return;
            int processed = 0;
            while (processed++ < 64) // cap per frame
            {
                try
                {
                    if (_sfServer.Available <= 0) return;
                    IPEndPoint from = null;
                    byte[] data = _sfServer.Receive(ref from);
                    if (data == null || data.Length < 14) continue;
                    _sfPacketsRx++;
                    SfDispatch(data, from);
                }
                catch (Exception e)
                {
                    if (Verbose) Log.LogDebug($"SF server recv: {e.Message}");
                    return;
                }
            }
        }
        private void SweepStaleClients()
        {
            if (Time.realtimeSinceStartup - _lastClientSweepAt < 5f) return;
            _lastClientSweepAt = Time.realtimeSinceStartup;
            float cutoff = Time.realtimeSinceStartup - ClientTimeoutSec;
            List<string> toRemove = null;
            foreach (var kv in _sfClients)
            {
                if (kv.Value.LastSeen < cutoff)
                {
                    if (toRemove == null) toRemove = new List<string>();
                    toRemove.Add(kv.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (var k in toRemove)
                {
                    var cli = _sfClients[k];
                    Log.LogInfo($"[SF] Dropping stale client {k} (slot={cli.Slot} steamID={cli.SteamID}, last seen {Time.realtimeSinceStartup - cli.LastSeen:0.0}s ago)");
                    _sfClients.Remove(k);
                    // Also forget the v26 endpoint + death-tracking for the slot,
                    // otherwise we'd keep sending snapshots into the void. (A2)
                    // Only clear slot-keyed state if no LIVE client has since taken
                    // this slot — a reconnect reuses the slot, and clearing then
                    // would delete the live client's endpoint and mark its slot
                    // already-dead. (cli is already removed from _sfClients above.)
                    if (cli.Slot >= 0)
                    {
                        bool slotReused = false;
                        foreach (var kv2 in _sfClients) if (kv2.Value.Slot == cli.Slot) { slotReused = true; break; }
                        if (!slotReused)
                        {
                            _slotV26Endpoint.Remove(cli.Slot);
                            _deathSlotsHandled.Remove(cli.Slot);
                            // Destroy this slot's authoritative rig and drop its last
                            // input. Otherwise the rig lingers until the next round
                            // advance: still serialized into snapshots (a frozen
                            // phantom), still tested by projectile hit-reg, and still
                            // driven by the stale SlotInputs frame (a held move/fire/
                            // throw keeps applying). A disconnect that leaves a single
                            // player alive advances no round, so it would otherwise
                            // persist indefinitely. (Mirrors the reconnect-eviction
                            // cleanup above, plus a Destroy since no one reuses it.)
                            if (SlotToRig.TryGetValue(cli.Slot, out var staleRig))
                            {
                                if ((object)staleRig != null) UnityEngine.Object.Destroy(staleRig);
                                SlotToRig.Remove(cli.Slot);
                            }
                            SlotInputs.Remove(cli.Slot);
                        }
                    }
                    _rateGuards.Remove(k);
                }
                // Lobby emptied out: clear the match flag so the next player's
                // /start fires a fresh MapChange. _matchStarted was a one-way
                // latch — once a match started it never reset, so any later
                // /start hit the "already in progress" no-op and the client
                // sat in the lobby forever.
                if (_sfClients.Count == 0 && _matchStarted)
                {
                    Log.LogInfo("[SF] Lobby empty — resetting match state for next /start.");
                    ResetMatchStateForLobby();
                }
            }
            // H-P0-2 — prune rate guards by their own last-touch, independent of
            // _sfClients membership. Guards are keyed per source ENDPOINT and are
            // created for msgType 40/41 sources that never get an _sfClients
            // entry, so the per-client removal above never reaps them — a
            // spoofed-source flood would grow the dict without bound.
            List<string> guardsToRemove = null;
            foreach (var kv in _rateGuards)
            {
                if (kv.Value.LastTouch < cutoff)
                {
                    if (guardsToRemove == null) guardsToRemove = new List<string>();
                    guardsToRemove.Add(kv.Key);
                }
            }
            if (guardsToRemove != null)
                foreach (var k in guardsToRemove) _rateGuards.Remove(k);
        }

        private void ResetMatchStateForLobby()
        {
            _matchStarted = false;
            _autoStartAt = -1f;
            _pendingClientStartMatchAt = -1f;
            _pendingClientStartMatchFired = false;
            _pendingRoundAdvanceAt = -1f;
            // B1 (code-review): also reset the oracle's per-match latches + clear
            // stale authoritative rigs. _authSpawnDone is otherwise reset ONLY on
            // round-advance, so across a lobby empty→refill it stays true and the
            // NEXT match never spawns server-authoritative rigs — server-side
            // box-push / hit-reg / auth-death silently stop until the first round
            // advance flips the latch. ResetOracleStateForRoundAdvance resets the
            // whole chain (auth-spawn + NSO inventory + map-sync) and clears rigs.
            ResetOracleStateForRoundAdvance();
        }

        // Parse the wrapper and route by msgType. Body bytes are forwarded
        // to handlers without copying — they read from offset 5 to length-9.
        private void SfDispatch(byte[] data, IPEndPoint from)
        {
            // SOPH: u32 timestamp + u8 msgType
            byte msgType = data[4];
            // EOPH: u64 steamID + u8 channel
            int bodyOffset = 5;
            int bodyLen = data.Length - 14;
            ulong steamID = ReadU64LE(data, data.Length - 9);
            byte channel = data[data.Length - 1];

            // Verbose log every Nth packet so we can see what's happening.
            if (_sfPacketsRx == 1 || _sfPacketsRx % 30 == 0)
                Log.LogInfo($"[SF] rx#{_sfPacketsRx} type={msgType} bodyLen={bodyLen} ch={channel} from={from} steamID={steamID}");

            // ALKA-style anticheat — per-client packet rate observation. Logs
            // when a client exceeds thresholds. Drops the packet only when
            // SF_ANTICHEAT_ENFORCE=1 is set (off by default — needs healthy
            // traffic telemetry to tune without dropping legit bursts).
            if (AnticheatObserve(from, msgType)) return;

            // Phase 6.12 — v26 PktPlayerInput is keyed by slot embedded in the
            // body, not by source IP+port (the SFClientRecon plugin sends from
            // its own ephemeral UDP socket, not the patched DLL's). Route it
            // directly to the slot-based handler and skip the auto-add path
            // below, which would create a phantom SfClient entry every time.
            if (msgType == PktPlayerInput)
            {
                try { HandlePlayerInput(data, bodyOffset, bodyLen, from); }
                catch (Exception ex) { Log.LogWarning($"[SF] HandlePlayerInput threw: {ex.Message}"); }
                return;
            }
            if (msgType == PktClientFireWeapon)
            {
                try { HandleClientFireWeapon(data, bodyOffset, bodyLen, from); }
                catch (Exception ex) { Log.LogWarning($"[SF] HandleClientFireWeapon threw: {ex.Message}"); }
                return;
            }

            // Track client.
            string key = from.ToString();
            if (!_sfClients.TryGetValue(key, out var cli))
            {
                cli = new SfClient { Addr = from, Slot = -1 };
                _sfClients[key] = cli;
                Log.LogInfo($"[SF] new client appeared: {from}");
            }
            cli.LastSeen = Time.realtimeSinceStartup;
            // CRITICAL: do NOT overwrite cli.SteamID from the envelope here.
            // SF's SendP2PPacketToUser puts the DESTINATION's SteamID in the
            // envelope, not the sender's. When P1's OnClientJoined fires it
            // calls PingAllUsers → P1 sends a Ping with envelope steamID=P2's,
            // and a blind overwrite would clobber P1's record. cli.SteamID is
            // set exactly once from ClientRequestingIndex's body (which DOES
            // carry the sender's identity) — that's enough.
            if (cli.SteamID == 0 && steamID != 0)
            {
                // First-ever steamID for this addr — accept it (covers e.g.
                // direct ClientRequestingAccepting before ClientRequestingIndex).
                cli.SteamID = steamID;
            }

            // ALKA P0-5 defense: a bad/malformed packet in one handler should
            // log + drop, not bubble out and skip the rest of the batch (which
            // would happen if it propagated up to DrainSfServer's catch).
            try
            {
            switch (msgType)
            {
                case PktPing:
                    HandlePing(cli, data, bodyOffset, bodyLen);
                    break;
                case PktClientRequestingAccepting:
                    HandleClientRequestingAccepting(cli);
                    break;
                case PktClientRequestingIndex:
                    HandleClientRequestingIndex(cli, data, bodyOffset, bodyLen);
                    break;
                case PktClientRequestingToSpawn:
                    HandleClientRequestingToSpawn(cli, data, bodyOffset, bodyLen);
                    break;
                case PktPlayerUpdate:
                    HandlePlayerUpdate(cli, data, bodyOffset, bodyLen, channel);
                    break;
                case PktClientReadyUp:
                    HandleClientReadyUp(cli, data, bodyOffset, bodyLen);
                    break;

                // === Phase 6.6 — gameplay packets ===
                // Pickup: re-broadcast as WeaponWasPickedUp with the same body
                // (1 byte playerIndex + 2 byte weaponNetworkIndex). SF's
                // OnPlayerRequestingWeaponPickUp would validate against
                // mSpawnedWeapons which is empty on the oracle, so we
                // bypass validation. (1 client, no anti-cheat threat model.)
                case PktClientRequestingWeaponPickUp:
                    HandlePickupRequest(cli, data, bodyOffset, bodyLen);
                    break;

                // SF's host code broadcasts PlayerTookDamage with ignoreUserID=0
                // — INCLUDING the sender. That return-trip is the killing-blow
                // signal: client.SyncClientHealth applies the damage, sees
                // damage==666.666, sets health=0, calls Die(). Without the echo
                // back to the sender, void/lava damage never kills them.
                case PktPlayerTookDamage:
                    if (!ValidateDamagePacket(cli, data, bodyOffset, bodyLen)) break;
                    RelayBodyToAll(msgType, data, bodyOffset, bodyLen, channel);
                    break;
                // PlayerWonWithRicochet has no abuse vector worth validating yet.
                case PktPlayerWonWithRicochet:
                    RelayBodyToAll(msgType, data, bodyOffset, bodyLen, channel);
                    break;

                // SECURITY (code-review A1): drop client-originated PktKickPlayer.
                // The patched DLL acts on a received KickPlayer, so relaying one
                // would let any client boot another player. Legitimate kicks are
                // emitted server-side only (see /kick → BroadcastSfPacket(PktKickPlayer)).
                case PktKickPlayer:
                    if (Verbose) Log.LogWarning($"[SF] Dropped client-originated PktKickPlayer from {from}.");
                    break;

                // "Relay to all OTHER clients" — SF's host passes ignoreUserID =
                // sender so they don't get duplicate force events / fall-outs.
                case PktPlayerForceAdded:
                case PktPlayerForceAddedAndBlock:
                case PktPlayerLavaForceAdded:
                case PktPlayerFallOut:
                case PktPlayerTalked:        // chat / voice / commands (see PlayerTalked hex log below)
                case PktOptionsChanged:      // lobby option toggles (ALKA BUGS_BACKLOG P0-4)
                case PktLerpPlayer:          // patched-DLL ext, remote-lerp trigger (ALKA P1-4)
                case PktColorChanged:        // patched-DLL ext, player color (ALKA P1-4)
                    if (msgType == PktPlayerTalked)
                    {
                        LogPlayerTalkedTelemetry(cli, data, bodyOffset, bodyLen, channel);
                        TryProcessChatCommand(cli, data, bodyOffset, bodyLen);
                    }
                    // OPEN-3 ("can't hit guns out of hands") telemetry. Punching a
                    // BLOCKING player is the only emitter of PlayerForceAddedAndBlock
                    // (PunchForce.cs:205 → victim NetworkPlayer's event channel). The
                    // relay below is unconditional (no validation/filter), so if this
                    // line fires during kit's live test the server-side path is sound
                    // and the bug is client-side (emit or block/force apply). If it
                    // NEVER fires when punching a blocker, the patched DLL isn't
                    // emitting msgType 14 (or sends it on a channel we don't read).
                    // Sample-logged so frequent punches can't flood. No PII (slot/
                    // channel/len only).
                    if (msgType == PktPlayerForceAddedAndBlock
                        && (_forceBlockRxCount++ < 5 || _forceBlockRxCount % 30 == 0))
                        Log.LogInfo($"[OPEN-3] rx PlayerForceAddedAndBlock (punch-block) slot={cli.Slot} ch={channel} body={bodyLen}B #{_forceBlockRxCount} → relaying to other client(s)");
                    RelayBodyToOthers(cli, msgType, data, bodyOffset, bodyLen, channel);
                    // Void/lava: FallOut often arrives without a 666 relay (solo or last player).
                    if (msgType == PktPlayerFallOut && _matchStarted)
                        ScheduleRoundAdvanceOnDeath("player-fallout");
                    break;

                case PktObjectUpdate:
                    // SERVER-AUTH BOXES (v0.4.0) — the oracle's own sim is
                    // the single authority for crate state. Client
                    // ObjectUpdates are no longer applied OR relayed:
                    // applying let any client teleport any crate (zero
                    // validation), and with two clients the last-writer-wins
                    // overwrite ping-ponged the server's crates between two
                    // divergent local sims. Clients learn crate state from
                    // the v26 snapshot; they influence crates only through
                    // their player rig (ghost-rig collisions) and the
                    // server-side bullet kick.
                    // SFHEADLESS_ACCEPT_CLIENT_CRATES=1 restores legacy.
                    if (AcceptClientCrates)
                    {
                        ApplyClientObjectUpdate(data, bodyOffset, bodyLen);
                        RelayBodyToOthers(cli, msgType, data, bodyOffset, bodyLen, channel);
                    }
                    else
                    {
                        _objectUpdateDroppedCount++;
                        if (_objectUpdateDroppedCount == 1 || _objectUpdateDroppedCount % 600 == 0)
                            Log.LogInfo($"[BOXES] Dropped client ObjectUpdate #{_objectUpdateDroppedCount} (server-authoritative crates)");
                    }
                    break;

                // "Relay to ALL including sender" for destruction events.
                // In vanilla SF, the host applies the break locally and broadcasts
                // to non-host clients. In our dedicated-server setup, NO client
                // is the host — the sender hasn't applied the break locally yet
                // either, so they need the echo back to actually see the ice/
                // crate/chain break. Without this, the breaker's screen shows
                // unbroken ice while others see it shattered. Spotted in ALKA's
                // BUGS_BACKLOG P0-3 — same fix shape as our PlayerTookDamage
                // include-sender for the killing-blow signal.
                case PktObjectSimpleDestruction:
                case PktObjectInvokeDestructionEvent:
                case PktObjectDestructionCollision:
                    RelayBodyToAll(msgType, data, bodyOffset, bodyLen, channel);
                    // v0.4.x — ALSO apply the destruction to the server's own
                    // scene. The server used to only relay, so ice/boxes that
                    // clients shot/broke stayed intact server-side: the server's
                    // world drifted from the clients' (a player's server-side
                    // rig could stand on phantom ice; hit-reg + anti-cheat ran
                    // against a stale world). Now the oracle breaks them too.
                    ApplyDestructionLocally(msgType, data, bodyOffset, bodyLen);
                    break;

                // Weapon drop: SF's OnPlayerRequestingWeaponDrop just appends
                // the next two IDs (weaponSpawnID + syncableObjectSpawnID) and
                // broadcasts as WeaponDropped. We replicate that logic in pure
                // C# so the IDs come from our counter and stay in sync with
                // weapon spawns.
                case PktClientRequestWeaponDrop:
                    HandleDropRequest(cli, data, bodyOffset, bodyLen);
                    break;

                // Weapon throw: client sends RequestingWeaponThrow (21) with
                // [bool justDrop][byte weaponIdx][ShortVector2 pos][ByteVector2 rot]
                // [optional ByteVector2 aim]. Host appends weaponSpawnID +
                // syncableObjectSpawnID and broadcasts as WeaponThrown (20).
                case PktRequestingWeaponThrow:
                    HandleThrowRequest(cli, data, bodyOffset, bodyLen);
                    break;

                default:
                    if (Verbose) Log.LogDebug($"[SF] unhandled type={msgType} from={from}");
                    break;
            }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[SF] dispatch threw on msgType={msgType} from={from}: {ex.Message}");
            }
        }

        // /lobbies handler — read the multi-process registry directly so we can
        // tell a player about OTHER lobbies running on this same host.
        private string ListOtherLobbiesFromRegistry()
        {
            try
            {
                string dir = Environment.GetEnvironmentVariable("SF_LOBBIES_DIR") ?? "/tmp/sf-lobbies";
                if (!System.IO.Directory.Exists(dir)) return "No other lobbies (registry not found).";
                var entries = new List<string>();
                string myCode = Environment.GetEnvironmentVariable("SF_LOBBY_CODE") ?? "";
                foreach (var path in System.IO.Directory.GetFiles(dir, "*.conf"))
                {
                    string code = "?", port = "?";
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        var eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                        if (k == "code") code = v;
                        else if (k == "port") port = v;
                    }
                    if (code == myCode) continue;
                    entries.Add($"{code}:{port}");
                }
                if (entries.Count == 0) return "No other lobbies running.";
                return "Other lobbies: " + string.Join(", ", entries.ToArray());
            }
            catch (Exception ex) { return $"(error: {ex.Message})"; }
        }

        // Re-broadcast body to all v25 clients except the sender. Used for
        // pure-relay gameplay msgTypes (force, falloff, etc).
        private void RelayBodyToOthers(SfClient sender, byte msgType, byte[] data, int off, int len, byte channel)
        {
            if (len <= 0) return;
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (cli == sender) continue;
                if (!cli.Initialized) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (Verbose && sent > 0)
                Log.LogDebug($"[SF] relay msgType={msgType} bodyLen={len} → {sent} other client(s)");
        }

        // Re-broadcast body to ALL v25 clients including sender. Used for
        // msgTypes that the sender's own client expects to receive back
        // (PlayerTookDamage carries the killing-blow signal; without the
        // echo, the sender never dies).
        private void RelayBodyToAll(byte msgType, byte[] data, int off, int len, byte channel)
        {
            if (len <= 0) return;
            byte[] body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (!cli.Initialized) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (sent > 0)
            {
                // Sample-log so we can see this firing without flooding.
                if (_relayAllCount++ < 5 || _relayAllCount % 30 == 0)
                    Log.LogInfo($"[SF] relay-to-all msgType={msgType} bodyLen={len} → {sent} client(s) (#{_relayAllCount})");
            }

            // Detect killing-blow for round-advance. PlayerTookDamage body
            // format (from NetworkPlayer.UnitWasDamaged): byte attacker, float
            // damage, bool playParticles, [particle dir bytes], byte dmgType.
            // damage == 666.666f signals "this hit kills."
            if (msgType == PktPlayerTookDamage && len >= 5)
            {
                float dmg = BitConverter.ToSingle(body, 1);
                if (System.Math.Abs(dmg - 666.666f) < 0.01f)
                {
                    if (!_matchStarted)
                        FireMatchStart($"lobby-kill dmg={dmg:0.###}");
                    else
                        ScheduleRoundAdvanceOnDeath($"killing-blow dmg={dmg:0.###}");
                }
            }
        }

        // === helpers ===

        private int AllocSlot(SfClient cli)
        {
            if (cli.Slot >= 0) return cli.Slot;
            for (int s = 0; s < 4; s++)
            {
                bool taken = false;
                foreach (var kv in _sfClients) if (kv.Value.Slot == s) { taken = true; break; }
                if (!taken) return s;
            }
            // H-P1-2 — server full. Returning 0 here crammed a 5th client into
            // slot 0: channel routing (slot*2+2 / slot*2+3), SlotToRig[0] and
            // the snapshot endpoint all collided for both occupants. Caller
            // drops the join instead.
            return -1;
        }

        // Serialize a packet with the 14-byte wrapper and send to one client.
        private void SendSfPacket(IPEndPoint to, byte msgType, byte[] body, ulong steamID, byte channel)
        {
            if ((object)_sfServer == null) return;
            int totalLen = 5 + (body?.Length ?? 0) + 9;
            byte[] pkt = new byte[totalLen];
            uint ts = (uint)(System.DateTime.UtcNow - new System.DateTime(1970, 1, 1)).TotalSeconds;
            WriteU32LE(pkt, 0, ts);
            pkt[4] = msgType;
            if (body != null && body.Length > 0) System.Buffer.BlockCopy(body, 0, pkt, 5, body.Length);
            int tailOff = 5 + (body?.Length ?? 0);
            WriteU64LE(pkt, tailOff, steamID);
            pkt[tailOff + 8] = channel;
            try
            {
                _sfServer.Send(pkt, pkt.Length, to);
                _sfPacketsTx++;
            }
            catch (Exception e) { if (Verbose) Log.LogDebug($"[SF] send: {e.Message}"); }
        }

        private void BroadcastSfPacket(byte msgType, byte[] body, ulong steamID, byte channel)
        {
            // Gate on Initialized (issue #2): a client between
            // ClientRequestingAccepting and ClientInit has no slot/roster yet, and
            // the patched DLL NREs in ReadMessageBuffer if it processes an early
            // gameplay broadcast (e.g. PktMapChange) before its own ClientInit
            // lands. The handshake packets (ClientAccepted/ClientInit) are unicast
            // via SendSfPacket, and PktClientSpawned only fires after Initialized is
            // set, so gating the broadcast here is safe.
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                SendSfPacket(kv.Value.Addr, msgType, body, steamID, channel);
            }
        }

        // === codec primitives ===

        private static void WriteU32LE(byte[] buf, int off, uint v)
        {
            buf[off    ] = (byte)(v       & 0xFF);
            buf[off + 1] = (byte)(v >>  8 & 0xFF);
            buf[off + 2] = (byte)(v >> 16 & 0xFF);
            buf[off + 3] = (byte)(v >> 24 & 0xFF);
        }

        // Defense-in-depth for the outbound world-state snapshot: a non-finite
        // position copied straight from a live transform would be serialized and
        // broadcast, then written to rb.position on every client — poisoning that
        // body's PhysX state and propagating through contacts (the best in-repo
        // match for the live +Infinity incident, notes/REVIEW_2026-06-10 §0). The
        // `p.y < -30f` void skips do NOT catch this: NaN < -30f is false. net46
        // has no float.IsFinite, so use the IsNaN/IsInfinity pair like the inbound
        // validators (:2926/:4315). NSO entries are dropped at collect; the inline
        // rig/projectile/mapsync writes (pre-counted, can't skip) are coerced.
        private static bool IsFiniteVec3(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }
        private static float Finite(float v)
        {
            return (float.IsNaN(v) || float.IsInfinity(v)) ? 0f : v;
        }

        private static void WriteF32LE(byte[] buf, int off, float v)
        {
            // BitConverter.GetBytes is little-endian on x86/x64. We target
            // x64 Linux/Windows for the oracle; if we ever support PowerPC
            // or BE clients this needs an endian guard.
            var bytes = System.BitConverter.GetBytes(v);
            buf[off    ] = bytes[0];
            buf[off + 1] = bytes[1];
            buf[off + 2] = bytes[2];
            buf[off + 3] = bytes[3];
        }

        private static void WriteU16LE(byte[] buf, int off, ushort v)
        {
            buf[off    ] = (byte)(v       & 0xFF);
            buf[off + 1] = (byte)(v >>  8 & 0xFF);
        }
        private static void WriteU64LE(byte[] buf, int off, ulong v)
        {
            for (int i = 0; i < 8; i++) buf[off + i] = (byte)((v >> (i * 8)) & 0xFF);
        }
        private static ulong ReadU64LE(byte[] buf, int off)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= ((ulong)buf[off + i]) << (i * 8);
            return v;
        }
        private static float ReadF32LE(byte[] buf, int off)
        {
            return System.BitConverter.ToSingle(buf, off);
        }
    }
}
