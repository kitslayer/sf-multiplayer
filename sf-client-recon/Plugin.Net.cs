using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFClientRecon
{
    public partial class Plugin
    {
        private void RxLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] pkt = _socket.Receive(ref ep);
                    // C-P0-A — the socket is bound on 0.0.0.0 and the server's
                    // address is public knowledge; without this check ANY host
                    // that can reach our port can inject snapshots/banners/
                    // SELECT-ACKs (teleport objects, spoof "banned" banners,
                    // stall lobby joins). Address-only compare: the oracle and
                    // the router share the address, but reply ports vary.
                    if (_serverEp != null && !ep.Address.Equals(_serverEp.Address))
                    {
                        _rxRejects++;
                        if (_rxRejects == 1 || _rxRejects % 500 == 0)
                            Log.LogWarning($"RX: dropped packet from non-server source {ep} (server={_serverEp.Address}, total dropped {_rxRejects})");
                        continue;
                    }
                    HandlePacket(pkt);
                }
                catch (SocketException e)
                {
                    // C-P1-A — on Windows, an ICMP port-unreachable for a
                    // datagram WE sent surfaces as a SocketException on the
                    // next Receive (WSAECONNRESET). Breaking here killed the
                    // snapshot listener for the rest of the session while TX
                    // kept flowing — "connected but frozen". Keep listening
                    // unless we're actually shutting down.
                    if (!_running) break;
                    _rxSockErrs++;
                    if (_rxSockErrs == 1 || _rxSockErrs % 100 == 0)
                        Log.LogWarning($"RX socket: {e.Message} (continuing, total {_rxSockErrs})");
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception e) { Log.LogWarning($"RX: {e.Message}"); }
            }
        }

        private void HandlePacket(byte[] pkt)
        {
            // v25 wrapper: 5 bytes prefix + body + 9 bytes suffix
            if (pkt.Length < 14) return;

            // sf-router SELECT-ACK (router-only framing, NOT a game msgType):
            // magic "SFRTR\0\0\x01" + op 0x81 + status + nonce. Log it so a bad
            // lobby code (status=1) is visible instead of silently resending.
            if (pkt[0] == 0x53 && pkt[1] == 0x46 && pkt[2] == 0x52 && pkt[3] == 0x54
                && pkt[4] == 0x52 && pkt[5] == 0x00 && pkt[6] == 0x00 && pkt[7] == 0x01
                && pkt[8] == 0x81)
            {
                byte ackStatus = pkt[9];
                if (ackStatus != 0)
                {
                    _bannerText = "Lobby '" + (SelectedLobbyCode ?? "?") + "' not found on server.";
                    _bannerUntilUtc = DateTime.UtcNow.AddSeconds(3);
                    if (_selectLogs < 8) { _selectLogs++; Log.LogWarning($"[SELECT-ACK] lobby={SelectedLobbyCode} not found (status={ackStatus})"); }
                }
                else if (_selectLogs < 8) { _selectLogs++; Log.LogInfo($"[SELECT-ACK] lobby={SelectedLobbyCode} accepted"); }
                return;
            }

            byte msgType = pkt[4];

            // msgType 42 — server announcement banner (anti-cheat kicks etc.).
            // Body is raw UTF-8 text. Show it centered at the top for 3s.
            if (msgType == 42)
            {
                int annOff = 5;
                int annLen = pkt.Length - 14;
                if (annLen <= 0 || annLen > 512) return;
                try
                {
                    string text = System.Text.Encoding.UTF8.GetString(pkt, annOff, annLen);
                    _bannerText = text;
                    _bannerUntilUtc = DateTime.UtcNow.AddSeconds(3);
                }
                catch { }
                return;
            }

            if (msgType != 39) return;

            int bodyOff = 5;
            int bodyLen = pkt.Length - 14;
            if (bodyLen < 5) return;  // need at least tick + count

            uint tick = (uint)(pkt[bodyOff] | (pkt[bodyOff + 1] << 8) | (pkt[bodyOff + 2] << 16) | (pkt[bodyOff + 3] << 24));
            byte playerCount = pkt[bodyOff + 4];
            int o = bodyOff + 5;
            int playerEntrySize = 1 + 12 + 4;  // slot + 3 floats + u32 lastInputSeq (v26.2)
            // C-P0-A — clamp every section count to what the body can actually
            // hold BEFORE sizing the list: counts are attacker-influencable and
            // a u16 of 65535 would reserve megabytes per datagram on a 32-bit
            // process even though the parse loop itself stays in bounds.
            int maxPlayers = (bodyOff + bodyLen - o) / playerEntrySize;
            if (playerCount > maxPlayers) playerCount = (byte)(maxPlayers < 0 ? 0 : maxPlayers);
            var list = new List<SnapshotEntry>(playerCount);
            for (int i = 0; i < playerCount; i++)
            {
                if (o + playerEntrySize > bodyOff + bodyLen) break;
                list.Add(new SnapshotEntry
                {
                    Slot = pkt[o],
                    X = BitConverter.ToSingle(pkt, o + 1),
                    Y = BitConverter.ToSingle(pkt, o + 5),
                    Z = BitConverter.ToSingle(pkt, o + 9),
                    LastInputSeq = (uint)(pkt[o + 13] | (pkt[o + 14] << 8) | (pkt[o + 15] << 16) | (pkt[o + 16] << 24)),
                });
                o += playerEntrySize;
            }

            // v26.1 (Phase 6.14): NSO entries follow the player section.
            // Old servers won't have these bytes; just leave nsoList empty.
            List<NsoSnapEntry> nsoList = null;
            if (o + 2 <= bodyOff + bodyLen)
            {
                ushort nsoCount = (ushort)(pkt[o] | (pkt[o + 1] << 8));
                o += 2;
                int nsoEntrySize = 2 + 16;
                int maxNso = (bodyOff + bodyLen - o) / nsoEntrySize;
                if (nsoCount > maxNso) nsoCount = (ushort)(maxNso < 0 ? 0 : maxNso);
                nsoList = new List<NsoSnapEntry>(nsoCount);
                for (int i = 0; i < nsoCount; i++)
                {
                    if (o + nsoEntrySize > bodyOff + bodyLen) break;
                    nsoList.Add(new NsoSnapEntry
                    {
                        Id   = (ushort)(pkt[o] | (pkt[o + 1] << 8)),
                        X    = BitConverter.ToSingle(pkt, o + 2),
                        Y    = BitConverter.ToSingle(pkt, o + 6),
                        Z    = BitConverter.ToSingle(pkt, o + 10),
                        RotZ = BitConverter.ToSingle(pkt, o + 14),
                        UpY  = float.NaN,
                        UpZ  = float.NaN,
                    });
                    o += nsoEntrySize;
                }
            }

            // v26.3 (Phase 6.17): projectile entries follow the NSO section.
            // We don't yet RENDER them client-side (local raycast still draws
            // the bullet); just skip past so the offset stays aligned for any
            // future sections appended after.
            if (o + 2 <= bodyOff + bodyLen)
            {
                ushort projCount = (ushort)(pkt[o] | (pkt[o + 1] << 8));
                o += 2;
                int projEntrySize = 4 + 1 + 1 + 12;  // u32 id, u8 slot, u8 wType, 3×f32 pos
                int wanted = projCount * projEntrySize;
                if (o + wanted <= bodyOff + bodyLen) o += wanted;
                // else: malformed snapshot — silently stop here. Counters
                // already recorded via _snapsReceived in HandlePacket.
            }

            // P0-14 v26.5: MapInfoSyncableBase positions for moving platforms,
            // pressure pillars, ghost platforms. Section is optional (older
            // servers won't write it; we tolerate truncated buffer).
            // Entry: [f32 startX, f32 startY, f32 x, f32 y, f32 z] = 20 bytes.
            // startX/Y is the stock MapInfoSyncableBase.m_StartPos key.
            List<MapSyncSnapEntry> mapSyncList = null;
            if (o + 2 <= bodyOff + bodyLen)
            {
                ushort mapSyncCount = (ushort)(pkt[o] | (pkt[o + 1] << 8));
                o += 2;
                int mapSyncEntrySize = 20;
                int maxMapSync = (bodyOff + bodyLen - o) / mapSyncEntrySize;
                if (mapSyncCount > maxMapSync) mapSyncCount = (ushort)(maxMapSync < 0 ? 0 : maxMapSync);
                mapSyncList = new List<MapSyncSnapEntry>(mapSyncCount);
                for (int i = 0; i < mapSyncCount; i++)
                {
                    if (o + mapSyncEntrySize > bodyOff + bodyLen) break;
                    mapSyncList.Add(new MapSyncSnapEntry
                    {
                        StartX = BitConverter.ToSingle(pkt, o),
                        StartY = BitConverter.ToSingle(pkt, o + 4),
                        X      = BitConverter.ToSingle(pkt, o + 8),
                        Y      = BitConverter.ToSingle(pkt, o + 12),
                        Z      = BitConverter.ToSingle(pkt, o + 16),
                    });
                    o += mapSyncEntrySize;
                }
            }

            List<MapStateSnapEntry> mapStateList = null;
            ParseMapStateSection(pkt, ref o, bodyOff + bodyLen, out mapStateList);

            // v26.7 — NSO up-vector appendix: [u16 count][u16 id, f32 upY,
            // f32 upZ]×count. Stock SF syncs NSO rotation as the up-vector's
            // y/z (tipping is about world X; eulerAngles.z carries ~nothing
            // for it), so this section is the real crate orientation. Optional
            // trailing section — absent on older hosts, in which case every
            // entry keeps UpY/UpZ = NaN and rotation falls back to RotZ.
            if (o + 2 <= bodyOff + bodyLen)
            {
                ushort upCount = (ushort)(pkt[o] | (pkt[o + 1] << 8));
                o += 2;
                int upEntrySize = 10;
                int maxUp = (bodyOff + bodyLen - o) / upEntrySize;
                if (upCount > maxUp) upCount = (ushort)(maxUp < 0 ? 0 : maxUp);
                if (upCount > 0 && nsoList != null && nsoList.Count > 0)
                {
                    var upById = new Dictionary<ushort, Vector2>(upCount);
                    for (int i = 0; i < upCount; i++)
                    {
                        if (o + upEntrySize > bodyOff + bodyLen) break;
                        ushort uid = (ushort)(pkt[o] | (pkt[o + 1] << 8));
                        upById[uid] = new Vector2(
                            BitConverter.ToSingle(pkt, o + 2),
                            BitConverter.ToSingle(pkt, o + 6));
                        o += upEntrySize;
                    }
                    for (int i = 0; i < nsoList.Count; i++)
                    {
                        Vector2 up;
                        if (!upById.TryGetValue(nsoList[i].Id, out up)) continue;
                        var e2 = nsoList[i];
                        e2.UpY = up.x;
                        e2.UpZ = up.y;
                        nsoList[i] = e2;
                    }
                }
                else
                {
                    // Still advance past the section so any future trailing
                    // section stays aligned.
                    int skip = upCount * upEntrySize;
                    if (o + skip <= bodyOff + bodyLen) o += skip;
                }
            }

            // NB: explicit Monitor.Enter(obj)/Exit, NOT lock(){}. The C# `lock`
            // keyword compiles to Monitor.Enter(obj, ref bool), an overload that
            // does NOT exist in this game's Mono 2.0 runtime — it throws
            // MissingMethodException on every snapshot, killing position sync and
            // leaving the player frozen.
            System.Threading.Monitor.Enter(_snapLock);
            try
            {
                _pendingSnap = list;
                _pendingNsoSnap = nsoList;
                _pendingMapSyncSnap = mapSyncList;
                _pendingMapStateSnap = mapStateList;
                _pendingTick = tick;
                _snapsReceived++;
            }
            finally { System.Threading.Monitor.Exit(_snapLock); }
        }

        private static bool TryGetPlayerSlotFromNetworkPlayer(object np, out int slot)
        {
            slot = -1;
            if (!RefOk(np)) return false;
            try
            {
                if (!_ctrlLookupTried) { _ctrlLookupTried = true; _ctrlTypeForNp = AccessTools.TypeByName("Controller"); }
                if (!RefOk(_ctrlTypeForNp)) return false;
                if (!_ctrlPidLookupTried) { _ctrlPidLookupTried = true; _ctrlPlayerIdField = AccessTools.Field(_ctrlTypeForNp, "playerID"); }
                if (!RefOk(_ctrlPlayerIdField)) return false;
                var npComp = np as Component;
                if (!RefOk(npComp)) return false;
                var ctrl = npComp.GetComponent(_ctrlTypeForNp);
                if (!RefOk(ctrl)) return false;
                slot = (int)_ctrlPlayerIdField.GetValue(ctrl);
                return true;
            }
            catch { return false; }
        }

        // Phase 6.12 — pack and send a PktPlayerInput packet to the oracle.
        // First cut: read raw keyboard state via UnityEngine.Input. Phase
        // 6.12.1 will read from the SF Controller's CharacterActions so we
        // catch gamepad input too and match exactly what the patched DLL's
        // Movement.cs is reading for local prediction.
        private void SendPlayerInputPacket()
        {
            int localSlot = FindLocalSlot();
            if (localSlot < 0) return;

            float sx = 0f, sy = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  sx -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) sx += 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    sy += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  sy -= 1f;
            float ax = 0f, ay = 0f;  // aim — placeholder until we read mouse properly
            uint btns = 0;
            if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.W))      btns |= 1u << 0;  // jump
            if (Input.GetMouseButton(0))                                     btns |= 1u << 1;  // fire
            if (Input.GetMouseButton(1) || Input.GetKey(KeyCode.LeftShift))  btns |= 1u << 2;  // block
            if (Input.GetKey(KeyCode.Q))                                     btns |= 1u << 3;  // throw

            // Body: 25 bytes  (u32 seq + u8 slot + 4 floats + u32 buttons)
            byte[] body = new byte[25];
            _inputSeq++;
            WriteU32LE(body, 0, _inputSeq);
            body[4] = (byte)localSlot;
            WriteF32LE(body, 5,  sx);
            WriteF32LE(body, 9,  sy);
            WriteF32LE(body, 13, ax);
            WriteF32LE(body, 17, ay);
            WriteU32LE(body, 21, btns);

            // v25 envelope wrap.
            int totalLen = 5 + body.Length + 9;
            byte[] pkt = new byte[totalLen];
            uint ts = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            WriteU32LE(pkt, 0, ts);
            pkt[4] = 40;  // PktPlayerInput
            Buffer.BlockCopy(body, 0, pkt, 5, body.Length);
            // tail: u64 steamID (zero — server identifies by slot byte) + u8 channel
            pkt[pkt.Length - 1] = 0;

            try { _socket.Send(pkt, pkt.Length, _serverEp); }
            catch (Exception e) { Log.LogWarning($"TX: {e.Message}"); }

            if (VerboseDiag && (_inputSeq == 1 || _inputSeq % 300 == 0))
                Log.LogInfo($"[P6.12] Sent PlayerInput #{_inputSeq} slot={localSlot} stick=({sx:0.00},{sy:0.00}) btns=0x{btns:X}");
        }

        // Emit a SELECT control datagram to the sf-router so it pins this client
        // (by source endpoint, with a per-IP fallback the game socket rides) to
        // SelectedLobbyCode's backend. Router-only framing — NOT a game msgType —
        // see notes/PROTOCOL.md. Sent on the v26 socket; the router learns our IP
        // here BEFORE the game socket connects in BeginOracleLobbyConnect.
        // Built with explicit byte writes (Mono 2.0: no LINQ, no Array.Empty).
        internal void SendSelectLobbyPacket()
        {
            if (_socket == null || _serverEp == null) return;
            string code = SelectedLobbyCode ?? "";
            byte[] codeBytes = System.Text.Encoding.ASCII.GetBytes(code);
            if (codeBytes.Length > 16) return;  // router maxCodeLen
            // [8 magic][1 op=SELECT][1 codeLen][code][4 nonce LE]
            byte[] pkt = new byte[8 + 1 + 1 + codeBytes.Length + 4];
            // magic "SFRTR\0\0\x01" — matches sf-router/select.go selectMagic.
            pkt[0] = 0x53; pkt[1] = 0x46; pkt[2] = 0x52; pkt[3] = 0x54; pkt[4] = 0x52;  // S F R T R
            pkt[5] = 0x00; pkt[6] = 0x00; pkt[7] = 0x01;
            pkt[8] = 0x01;                          // op = SELECT
            pkt[9] = (byte)codeBytes.Length;
            Buffer.BlockCopy(codeBytes, 0, pkt, 10, codeBytes.Length);
            _selectNonce++;
            WriteU32LE(pkt, 10 + codeBytes.Length, _selectNonce);
            try { _socket.Send(pkt, pkt.Length, _serverEp); }
            catch (Exception e) { Log.LogWarning($"[SELECT] send: {e.Message}"); }
            if (_selectLogs < 6) { _selectLogs++; Log.LogInfo($"[SELECT] lobby={code} nonce={_selectNonce} → {_serverEp}"); }
        }

        private void SendFireWeaponPacket(byte slot, byte weaponType, Vector3 origin, Vector3 dir, float speed)
        {
            // Body 30 bytes: u8 slot, u8 wType, 3×f32 origin, 3×f32 dir, f32 speed
            byte[] body = new byte[30];
            body[0] = slot;
            body[1] = weaponType;
            WriteF32LE(body, 2,  origin.x);
            WriteF32LE(body, 6,  origin.y);
            WriteF32LE(body, 10, origin.z);
            WriteF32LE(body, 14, dir.x);
            WriteF32LE(body, 18, dir.y);
            WriteF32LE(body, 22, dir.z);
            WriteF32LE(body, 26, speed);

            int totalLen = 5 + body.Length + 9;
            byte[] pkt = new byte[totalLen];
            uint ts = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            WriteU32LE(pkt, 0, ts);
            pkt[4] = 41;  // PktClientFireWeapon
            Buffer.BlockCopy(body, 0, pkt, 5, body.Length);
            try { _socket.Send(pkt, pkt.Length, _serverEp); }
            catch (Exception e) { Log.LogWarning($"SendFireWeaponPacket: {e.Message}"); }
            if (VerboseDiag) Log.LogInfo($"[P6.17] Sent FireWeapon slot={slot} w={weaponType} origin={origin} dir={dir}");
        }

        private static void WriteU32LE(byte[] b, int o, uint v)
        {
            b[o    ] = (byte)(v       & 0xFF);
            b[o + 1] = (byte)(v >>  8 & 0xFF);
            b[o + 2] = (byte)(v >> 16 & 0xFF);
            b[o + 3] = (byte)(v >> 24 & 0xFF);
        }
        private static void WriteF32LE(byte[] b, int o, float v)
        {
            var bytes = BitConverter.GetBytes(v);
            b[o    ] = bytes[0];
            b[o + 1] = bytes[1];
            b[o + 2] = bytes[2];
            b[o + 3] = bytes[3];
        }
    }
}
