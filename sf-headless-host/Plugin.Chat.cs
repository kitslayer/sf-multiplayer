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

        // Push a top-of-screen banner string to every recon client over the
        // v26 channel (their :1339 endpoint). Stock clients without the recon
        // plugin never listen here, so this is a no-op for them.
        private void SendAnnouncementToAll(string text)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(text ?? "");
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                IPEndPoint v26Ep;
                if (!_slotV26Endpoint.TryGetValue(kv.Value.Slot, out v26Ep))
                    v26Ep = new IPEndPoint(kv.Value.Addr.Address, V26_CLIENT_PORT);
                SendSfPacket(v26Ep, PktV26Announce, body, 0, 0);
            }
        }

        // Send a server chat line to every connected player. SF chat bubbles
        // fade on their own after ~3s, satisfying the "auto-disappear" ask.
        private void BroadcastChatToAll(string text)
        {
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (cli == null || !cli.Initialized) continue;
                SendChatToPlayer(cli, text);
            }
        }

        // Phase 6.15 — server-emitted chat. Used for command responses.
        // Wire format: body = raw UTF-8 bytes of the message (no length
        // prefix; total length comes from the v25 wrapper). Channel encodes
        // the talker's slot as (slot*2)+3; we use the recipient's owner
        // channel so it shows up over their own player.
        private void SendChatToPlayer(SfClient target, string text)
        {
            if (target == null || target.Slot < 0) return;
            byte[] body = System.Text.Encoding.UTF8.GetBytes(text);
            byte ch = (byte)((target.Slot * 2) + 3);
            SendSfPacket(target.Addr, PktPlayerTalked, body, 0uL, ch);
        }
        private static void EnsureAdminEnvLoaded()
        {
            if (_adminEnvLoaded) return;
            _adminEnvLoaded = true;
            _adminSteamIds = new HashSet<ulong>();
            string ids = Environment.GetEnvironmentVariable("SF_ADMIN_STEAMIDS");
            if (!string.IsNullOrEmpty(ids))
            {
                foreach (var part in ids.Split(','))
                {
                    ulong sid;
                    if (ulong.TryParse(part.Trim(), out sid) && sid != 0) _adminSteamIds.Add(sid);
                }
            }
            string pass = Environment.GetEnvironmentVariable("SF_ADMIN_PASS");
            _adminPass = string.IsNullOrEmpty(pass) ? null : pass;
        }
        private static bool IsAdminSender(SfClient sender)
        {
            EnsureAdminEnvLoaded();
            if (sender.IsAdmin) return true;
            return sender.SteamID != 0 && _adminSteamIds.Contains(sender.SteamID);
        }
        // H-P1-4 — chat text is attacker-controlled; embedded control chars
        // (\n, \r, ESC) would let a player forge log lines (which feed
        // sf-monitor) or splatter terminal escapes. Replace with spaces and
        // cap the length before anything logs or parses it.
        private static string SanitizeChatText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            int cap = raw.Length > 256 ? 256 : raw.Length;
            var sb = new System.Text.StringBuilder(cap);
            for (int i = 0; i < cap; i++)
            {
                char c = raw[i];
                sb.Append(c < ' ' ? ' ' : c);
            }
            return sb.ToString();
        }

        private void TryProcessChatCommand(SfClient sender, byte[] data, int off, int len)
        {
            try
            {
                if (len == 0) return;
                string text = SanitizeChatText(System.Text.Encoding.UTF8.GetString(data, off, len));
                if (string.IsNullOrEmpty(text) || text[0] != '/') return;
                var space = text.IndexOf(' ');
                string cmd = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
                // Don't echo /admin arguments (the password) into the log.
                Log.LogInfo($"[chat] slot={sender.Slot} command='{(cmd == "/admin" ? "/admin ***" : text)}'");
                switch (cmd)
                {
                    case "/code":
                    case "/room":
                        string code = Environment.GetEnvironmentVariable("SF_LOBBY_CODE");
                        SendChatToPlayer(sender, "Lobby code: " + (string.IsNullOrEmpty(code) ? "<unknown>" : code));
                        break;
                    case "/ping":
                        SendChatToPlayer(sender, "pong");
                        break;
                    case "/start":
                        if (_matchStarted)
                        {
                            SendChatToPlayer(sender, "Match already in progress.");
                        }
                        else
                        {
                            SendChatToPlayer(sender, "Starting match...");
                            FireMatchStart($"chat /start from slot {sender.Slot}");
                        }
                        break;
                    case "/version":
                        SendChatToPlayer(sender, $"sf-multiplayer {PluginVersion} (v26 protocol)");
                        break;
                    case "/restart":
                    case "/next":
                        if (_pendingRoundAdvanceAt > 0f)
                            SendChatToPlayer(sender, "Round advance already pending.");
                        else
                        {
                            SendChatToPlayer(sender, "Advancing to next map...");
                            _pendingRoundAdvanceAt = Time.realtimeSinceStartup + 1.0f;
                        }
                        break;
                    case "/map":
                    {
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim());
                        if (string.IsNullOrEmpty(arg))
                        {
                            SendChatToPlayer(sender, $"Current map: scene {_currentSceneIndex}. Usage: /map <1-124>. Random next: /next.");
                            break;
                        }
                        if (!int.TryParse(arg, out int sceneIdx) || sceneIdx < 1 || sceneIdx > 124 || sceneIdx == 102)
                        {
                            SendChatToPlayer(sender, "Usage: /map <1-124> (102 excluded — non-MP scene). Use /listmaps to browse.");
                            break;
                        }
                        bool valid = false;
                        foreach (var m in _allLandfallMaps) if (m == sceneIdx) { valid = true; break; }
                        if (!valid)
                        {
                            SendChatToPlayer(sender, $"Scene {sceneIdx} isn't in the playable Landfall set.");
                            break;
                        }
                        _currentSceneIndex = sceneIdx;
                        SendChatToPlayer(sender, $"Map set to scene {sceneIdx}. Switching now...");
                        Log.LogInfo($"[chat] /map {sceneIdx} by slot={sender.Slot}");
                        // Reuse AdvanceRound's MapChange + StartMatch chain via the pending timer.
                        // AdvanceRound picks a random map though, so we need a direct call shape.
                        _pendingRoundAdvanceAt = -1f;
                        BroadcastMapChange(_currentSceneIndex);
                        _pendingStartMatchAt = Time.realtimeSinceStartup + NextMatchDelaySec;
                        ScheduleOracleReloadCurrentMap("chat-/map");
                        foreach (var kv in _sfClients) kv.Value.Spawned = false;
                        break;
                    }
                    case "/listmaps":
                    case "/maps":
                    {
                        var sb = new System.Text.StringBuilder("Maps (1-124, 102 excluded): ");
                        int shown = 0;
                        foreach (var m in _allLandfallMaps)
                        {
                            if (shown > 0) sb.Append(",");
                            sb.Append(m);
                            shown++;
                            if (shown >= 40) { sb.Append("..."); break; }
                        }
                        SendChatToPlayer(sender, sb.ToString());
                        break;
                    }
                    case "/players":
                        int up = 0, sp = 0;
                        foreach (var ckv in _sfClients) { up++; if (ckv.Value.Spawned) sp++; }
                        SendChatToPlayer(sender, $"Players: {up} connected, {sp} spawned, rigs={SlotToRig.Count}");
                        break;
                    case "/lobbies":
                        SendChatToPlayer(sender, ListOtherLobbiesFromRegistry());
                        break;
                    case "/admin":
                    {
                        EnsureAdminEnvLoaded();
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim());
                        if ((object)_adminPass == null)
                        {
                            SendChatToPlayer(sender, "Admin login is not configured on this server.");
                        }
                        else if (arg == _adminPass)
                        {
                            sender.IsAdmin = true;
                            Log.LogInfo($"[chat] slot={sender.Slot} (steamID={sender.SteamID}) authenticated as admin.");
                            SendChatToPlayer(sender, "Admin granted for this session.");
                        }
                        else
                        {
                            Log.LogWarning($"[chat] slot={sender.Slot} (steamID={sender.SteamID}) failed /admin auth.");
                            SendChatToPlayer(sender, "Wrong password.");
                        }
                        break;
                    }
                    case "/kick":
                    {
                        if (!IsAdminSender(sender))
                        {
                            SendChatToPlayer(sender, "/kick is admin-only. Authenticate with /admin <password>.");
                            break;
                        }
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim());
                        if (string.IsNullOrEmpty(arg) || !int.TryParse(arg, out int targetSlot) || targetSlot < 0 || targetSlot > 3)
                        {
                            SendChatToPlayer(sender, "Usage: /kick <slot 0-3>. Use /players to see slots.");
                            break;
                        }
                        if (targetSlot == sender.Slot)
                        {
                            SendChatToPlayer(sender, "Can't kick yourself. Use Steam's Disconnect.");
                            break;
                        }
                        // Send PktKickPlayer to everyone (including the victim, who'll
                        // disconnect on receipt). Body = single byte slot.
                        byte[] kickBody = new byte[1] { (byte)targetSlot };
                        BroadcastSfPacket(PktKickPlayer, kickBody, 0uL, 0);
                        Log.LogInfo($"[chat] /kick slot={targetSlot} by slot={sender.Slot}");
                        SendChatToPlayer(sender, $"Kicked slot {targetSlot}.");
                        break;
                    }
                    case "/anticheat":
                    {
                        if (!IsAdminSender(sender))
                        {
                            SendChatToPlayer(sender, "/anticheat is admin-only. Authenticate with /admin <password>.");
                            break;
                        }
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim()).ToLowerInvariant();
                        if (arg == "on" || arg == "1" || arg == "true" || arg == "enforce")
                        {
                            AnticheatEnforce = true;
                            SendChatToPlayer(sender, "Anticheat: ENFORCE (rate-limited packets will be dropped)");
                        }
                        else if (arg == "off" || arg == "0" || arg == "false" || arg == "observe")
                        {
                            AnticheatEnforce = false;
                            SendChatToPlayer(sender, "Anticheat: observe-only (offending packets logged, not dropped)");
                        }
                        else
                        {
                            SendChatToPlayer(sender, $"Anticheat: {(AnticheatEnforce ? "ENFORCE" : "observe-only")}. Toggle: /anticheat on|off");
                        }
                        break;
                    }
                    case "/weapons":
                    {
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim()).ToLowerInvariant();
                        if (string.IsNullOrEmpty(arg))
                        {
                            if (_allowedWeaponIds.Count == 0)
                            {
                                SendChatToPlayer(sender, "Weapons: all (default 0-7 round-robin). Set: /weapons 0,1,3");
                            }
                            else
                            {
                                var arr = new int[_allowedWeaponIds.Count];
                                _allowedWeaponIds.CopyTo(arr);
                                System.Array.Sort(arr);
                                SendChatToPlayer(sender, $"Weapons allow-list: {string.Join(",", System.Array.ConvertAll(arr, i => i.ToString()))}");
                            }
                        }
                        else if (arg == "all" || arg == "clear" || arg == "default")
                        {
                            _allowedWeaponIds.Clear();
                            _allowedWeaponCycleIdx = 0;
                            SendChatToPlayer(sender, "Weapons reset to default (all).");
                        }
                        else
                        {
                            var parts = arg.Split(',');
                            var newList = new System.Collections.Generic.List<int>();
                            foreach (var part in parts)
                            {
                                if (int.TryParse(part.Trim(), out int idx) && idx >= 0 && idx <= 31) newList.Add(idx);
                            }
                            if (newList.Count == 0)
                            {
                                SendChatToPlayer(sender, "Usage: /weapons <0-31 comma list> | all");
                            }
                            else
                            {
                                _allowedWeaponIds.Clear();
                                foreach (var i in newList) _allowedWeaponIds.Add(i);
                                _allowedWeaponCycleIdx = 0;
                                SendChatToPlayer(sender, $"Weapons set to: {string.Join(",", newList.ConvertAll(i => i.ToString()).ToArray())}");
                            }
                        }
                        break;
                    }
                    case "/tickrate":
                    case "/tick":
                    {
                        string arg = (space < 0 ? "" : text.Substring(space + 1).Trim());
                        if (string.IsNullOrEmpty(arg))
                        {
                            float fd = Time.fixedDeltaTime;
                            int hz = (fd > 0f) ? (int)System.Math.Round(1.0 / fd) : 0;
                            SendChatToPlayer(sender, $"Server physics tickrate: {hz}Hz (fixedDeltaTime={fd:0.0000}s). Snapshot broadcast: 30Hz.");
                        }
                        else if (!IsAdminSender(sender))
                        {
                            SendChatToPlayer(sender, "Setting /tickrate is admin-only. Authenticate with /admin <password>.");
                        }
                        else
                        {
                            int hz;
                            if (!int.TryParse(arg, out hz) || hz < 20 || hz > 240)
                            {
                                SendChatToPlayer(sender, "Usage: /tickrate <20-240>. Default 50.");
                            }
                            else
                            {
                                float newFd = 1.0f / hz;
                                float oldFd = Time.fixedDeltaTime;
                                Time.fixedDeltaTime = newFd;
                                Log.LogInfo($"[chat] /tickrate {hz}Hz — Time.fixedDeltaTime: {oldFd:0.0000} → {newFd:0.0000}");
                                SendChatToPlayer(sender, $"Server physics tickrate set to {hz}Hz. (was {(int)System.Math.Round(1.0/oldFd)}Hz). Snapshot broadcast still 30Hz — client FPS is independent.");
                            }
                        }
                        break;
                    }
                    case "/help":
                        SendChatToPlayer(sender, "Commands: /code /ping /start /restart /next /map /listmaps /players /lobbies /weapons /tickrate /version /help — admin (/admin <pass>): /kick /anticheat /tickrate <hz>");
                        break;
                    default:
                        SendChatToPlayer(sender, "Unknown command. Type /help");
                        break;
                }
            }
            catch (Exception ex) { Log.LogWarning($"[chat parse] {ex.Message}"); }
        }
    }
}
