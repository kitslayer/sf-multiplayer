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

        // Called at every round boundary to reset per-life damage accumulators.
        private void AcResetRound()
        {
            for (int i = 0; i < 4; i++) { _acRoundDmgToVictim[i] = 0f; _acRoundHitsToVictim[i] = 0; }
            _acRoundIndex++;
        }

        // Called for every accepted PlayerTookDamage. attackerIdx/dmg already
        // parsed + validated. victimSlot is the packet sender's slot.
        private void AcTrackDamage(int victimSlot, byte attackerIdx, float dmg, bool isKillingBlow)
        {
            if (!AcEnabled) return;
            if (victimSlot < 0 || victimSlot > 3) return;

            if (!isKillingBlow)
            {
                // Real incremental damage — accumulate for this victim's life.
                _acRoundDmgToVictim[victimSlot] += dmg;
                _acRoundHitsToVictim[victimSlot]++;
                return;
            }

            // Killing blow. Environment kills (lava/void) use 255 — never a cheat.
            if (attackerIdx > 3) return;
            // Self-kill (suicide / fell into the void / own explosive) — the
            // attacker and victim are the same player. Never a cheat; this is the
            // false-positive that kicked a lone player throwing himself off the map.
            if (attackerIdx == victimSlot) return;
            // Anti-cheat is about player-vs-player interactions. With fewer than 2
            // players connected there is no one to legitimately kill, so any kill
            // here is environmental/self — never flag it.
            if (_sfClients.Count < 2) return;
            if (_acKicked.Contains(attackerIdx)) return;

            float accum = _acRoundDmgToVictim[victimSlot];
            int hits = _acRoundHitsToVictim[victimSlot];
            if (accum >= AcSuspectMaxAccum || hits > 1) return;  // plenty of real damage → legit kill

            // Range gate — confirm the kill happened at melee distance so we
            // don't false-positive on a legit long-range one-shot weapon.
            bool meleeRange = true;
            if (SlotToRig.TryGetValue(attackerIdx, out var attRig) && (object)attRig != null
                && SlotToRig.TryGetValue(victimSlot, out var vicRig) && (object)vicRig != null)
            {
                float dist = Vector3.Distance(attRig.transform.position, vicRig.transform.position);
                meleeRange = dist <= AcMeleeRange;
            }
            if (!meleeRange) return;

            // Flag this round for the attacker.
            if (!_acFlaggedRounds.TryGetValue(attackerIdx, out var rounds))
            {
                rounds = new HashSet<int>();
                _acFlaggedRounds[attackerIdx] = rounds;
            }
            rounds.Add(_acRoundIndex);
            Log.LogWarning($"[anticheat behavior] Low-damage kill by slot={attackerIdx} on slot={victimSlot} " +
                           $"(victim took only {accum:0.#} dmg over {hits} hit(s) at melee range). " +
                           $"Flagged rounds: {rounds.Count}/{AcFlaggedRoundsToKick}. " +
                           $"{(AcKickEnabled ? "" : "[LOG-ONLY — set SF_AC_KICK=1 to auto-kick]")}");

            if (rounds.Count >= AcFlaggedRoundsToKick && AcKickEnabled)
            {
                _acKicked.Add(attackerIdx);
                AcKickForCheat(attackerIdx, "instant melee kills (impossible without cheats)");
            }
        }

        // Announce the kick to everyone, then boot the offender.
        private void AcKickForCheat(int slot, string reason)
        {
            string msg = $"Player {slot + 1} kicked: {reason}";
            BroadcastChatToAll(msg);
            SendAnnouncementToAll(msg);   // recon plugin shows a top banner for 3s
            Log.LogWarning($"[anticheat behavior] KICK slot={slot}: {reason}");
            byte[] kickBody = new byte[1] { (byte)slot };
            BroadcastSfPacket(PktKickPlayer, kickBody, 0uL, 0);
        }

        private bool ValidateDamagePacket(SfClient sender, byte[] data, int off, int len)
        {
            if (len < 5) return false;
            byte attackerIdx = data[off];
            float dmg = BitConverter.ToSingle(data, off + 1);
            // Magnitude check — SF's killing-blow marker is 666.666; anything
            // above 1000 is clearly out of band. Negative damage is healing
            // and stock SF doesn't use it.
            if (float.IsNaN(dmg) || float.IsInfinity(dmg) || dmg < 0f || dmg > 1000f)
            {
                _damagePacketsDropped++;
                Log.LogWarning($"[anticheat damage] Reject damage={dmg} from slot={sender.Slot} (attacker idx={attackerIdx}). Dropped #{_damagePacketsDropped}");
                return false;
            }
            // Attacker slot bound check.
            if (attackerIdx > 3 && attackerIdx != 255)  // 255 = environment kill (lava/void)
            {
                _damagePacketsDropped++;
                Log.LogWarning($"[anticheat damage] Reject — attacker idx {attackerIdx} out of range. Dropped #{_damagePacketsDropped}");
                return false;
            }
            // P1-8 REVERTED 2026-05-23 night — the original "reject if
            // attackerIdx != sender.Slot" check was WRONG. In stock SF,
            // PktPlayerTookDamage is emitted by the VICTIM's
            // NetworkPlayer.UnitWasDamaged after their HealthHandler took
            // damage. The body's attackerIdx is the SHOOTER (computed by
            // looking up mController.damager in ConnectedClients), while
            // the sender of the packet is the victim. So attackerIdx !=
            // sender.Slot is THE NORMAL CASE — and rejecting it blocks
            // all damage between players (void/lava worked because they
            // had attacker == sender, but bullets/punches did not).
            //
            // The audit's "spoofing" concern stands but the fix-shape was
            // wrong. Proper anticheat for damage-source spoofing requires
            // server-side hit detection (Phase 6.17 v0.2+, in progress —
            // server emits its own damage instead of trusting clients).
            // Until that's the only damage path, we trust clients and
            // rely on the existing range/magnitude checks below.
            // Phase 6.14.5 v0.2 — range plausibility with rewind buffer.
            // Damage packets don't carry a client-tick reference (would need
            // a patched-DLL extension), so we assume the hit happened
            // ~2 ticks ago (≈66ms at 30Hz snapshot rate — typical RTT/2 +
            // client-processing latency). LookupTickSample retrieves the
            // historical positions; if not available (still in early ticks),
            // fall back to current.
            // sender.Slot bounded to 0..3 here (not just >=0): below we index
            // sample.Alive[sender.Slot] / Positions[sender.Slot] into [4] arrays.
            // AllocSlot caps slots at 0..3 today (so this is defense-in-depth, not
            // a live bug), but the sibling handlers (e.g. :3918) all bound `> 3`
            // locally rather than rely on that far-away invariant — match them so a
            // future slot-allocation change can't turn this into an IndexOutOfRange
            // that silently drops a client's damage.
            if (attackerIdx != 255 && sender.Slot >= 0 && sender.Slot <= 3)
            {
                Vector3 attPos, vicPos;
                bool gotHistoric = false;
                if (_serverTick >= 2)
                {
                    var sample = LookupTickSample(_serverTick - 2);
                    if (sample != null && sample.Alive[attackerIdx] && sample.Alive[sender.Slot])
                    {
                        attPos = sample.Positions[attackerIdx];
                        vicPos = sample.Positions[sender.Slot];
                        gotHistoric = true;
                    }
                    else { attPos = vicPos = Vector3.zero; }
                }
                else { attPos = vicPos = Vector3.zero; }

                if (!gotHistoric)
                {
                    if (SlotToRig.TryGetValue(attackerIdx, out var attRig) && (object)attRig != null
                        && SlotToRig.TryGetValue(sender.Slot, out var vicRig) && (object)vicRig != null)
                    {
                        attPos = attRig.transform.position;
                        vicPos = vicRig.transform.position;
                    }
                    else return true;  // not enough info to validate; trust
                }

                float dist = Vector3.Distance(attPos, vicPos);
                const float MaxPlausibleReach = 50f;
                if (dist > MaxPlausibleReach)
                {
                    _damagePacketsDropped++;
                    Log.LogWarning($"[anticheat damage] Reject — distance {dist:0.0}u > {MaxPlausibleReach}u (attacker slot {attackerIdx}, victim slot {sender.Slot}, {(gotHistoric ? "rewind" : "live")}). Dropped #{_damagePacketsDropped}");
                    return false;
                }
            }
            // Future (Phase 6.16+): weapon-specific max-reach (sword=3.5u,
            // pistol=18u, RPG=22u). Requires per-slot weapon tracking which
            // we don't have yet on the oracle side.

            // Behavioral tracking — accumulate real damage / detect spoofed
            // instant kills. Does not reject here (handled via kick).
            bool isKillingBlow = System.Math.Abs(dmg - 666.666f) < 0.01f;
            AcTrackDamage(sender.Slot, attackerIdx, dmg, isKillingBlow);
            return true;
        }
        private void LogPlayerTalkedTelemetry(SfClient cli, byte[] data, int off, int len, byte channel)
        {
            if (_playerTalkedLogged >= 20) return;
            _playerTalkedLogged++;
            int dumpLen = System.Math.Min(len, 32);
            var hex = new System.Text.StringBuilder(dumpLen * 3);
            for (int i = 0; i < dumpLen; i++) hex.Append(data[off + i].ToString("X2")).Append(' ');
            // Best-effort UTF-8 with non-printable as '.'
            var ascii = new System.Text.StringBuilder(dumpLen);
            for (int i = 0; i < dumpLen; i++)
            {
                byte b = data[off + i];
                ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            Log.LogInfo($"[telemetry chat] slot={cli.Slot} ch={channel} len={len} hex={hex} ascii='{ascii}'");
        }
        private class RateGuard
        {
            public Queue<float> All        = new Queue<float>();
            public Queue<float> PlayerUpd  = new Queue<float>();
            public Queue<float> Damage     = new Queue<float>();
            public Queue<float> Object     = new Queue<float>();
            public int Violations;
            public float LastViolationLog;
            // H-P0-2 — last packet seen from this endpoint; SweepStaleClients
            // prunes guards whose LastTouch went cold.
            public float LastTouch;
        }
        // Returns true if the packet should be DROPPED (only under
        // SF_ANTICHEAT_ENFORCE=1). Always observes regardless.
        private bool AnticheatObserve(IPEndPoint from, byte msgType)
        {
            bool overLimit = false;
            try
            {
                string key = from.ToString();
                float now = Time.realtimeSinceStartup;
                if (!_rateGuards.TryGetValue(key, out var g))
                {
                    if (_rateGuards.Count >= MaxRateGuardEntries)
                    {
                        // Emergency prune of guards idle >10s, then re-check.
                        List<string> stale = null;
                        foreach (var kv in _rateGuards)
                        {
                            if (kv.Value.LastTouch < now - 10f)
                            {
                                if (stale == null) stale = new List<string>();
                                stale.Add(kv.Key);
                            }
                        }
                        if (stale != null) foreach (var k in stale) _rateGuards.Remove(k);
                        if (_rateGuards.Count >= MaxRateGuardEntries)
                        {
                            // Still saturated — active flood. Refuse to track or
                            // process packets from brand-new sources.
                            _rateGuardCapDrops++;
                            if (_rateGuardCapDrops == 1 || _rateGuardCapDrops % 1000 == 0)
                                Log.LogWarning($"[anticheat] rate-guard table full ({MaxRateGuardEntries}) — dropping packet from new source {key} (total cap-drops {_rateGuardCapDrops})");
                            return true;
                        }
                    }
                    g = new RateGuard();
                    _rateGuards[key] = g;
                }
                g.LastTouch = now;
                RotateQueue(g.All, now);
                g.All.Enqueue(now);
                if (g.All.Count > MaxAllPerSec) { ReportViolation(g, key, "total", g.All.Count); overLimit = true; }

                if (msgType == PktPlayerUpdate)
                {
                    RotateQueue(g.PlayerUpd, now);
                    g.PlayerUpd.Enqueue(now);
                    if (g.PlayerUpd.Count > MaxPlayerUpdPerSec) { ReportViolation(g, key, "playerUpdate", g.PlayerUpd.Count); overLimit = true; }
                }
                else if (msgType == PktPlayerTookDamage)
                {
                    RotateQueue(g.Damage, now);
                    g.Damage.Enqueue(now);
                    if (g.Damage.Count > MaxDamagePerSec) { ReportViolation(g, key, "damage", g.Damage.Count); overLimit = true; }
                }
                else if (msgType == PktObjectUpdate
                      || msgType == PktObjectSpawned
                      || msgType == PktObjectDestructionCollision
                      || msgType == PktObjectSimpleDestruction
                      || msgType == PktObjectInvokeDestructionEvent
                      || msgType == PktObjectHello)
                {
                    RotateQueue(g.Object, now);
                    g.Object.Enqueue(now);
                    if (g.Object.Count > MaxObjectPerSec) { ReportViolation(g, key, "object", g.Object.Count); overLimit = true; }
                }
            }
            catch { /* observation only — never let it crash the dispatch */ }
            return overLimit && AnticheatEnforce;
        }
        private static void RotateQueue(Queue<float> q, float now)
        {
            while (q.Count > 0 && now - q.Peek() > 1.0f) q.Dequeue();
        }
        private void ReportViolation(RateGuard g, string key, string label, int rate)
        {
            g.Violations++;
            float now = Time.realtimeSinceStartup;
            if (now - g.LastViolationLog < 5f) return;
            g.LastViolationLog = now;
            Log.LogWarning($"[anticheat] {key} exceeded {label} rate ({rate}/s) — violation #{g.Violations}. Observation only; not dropping.");
        }
    }
}
