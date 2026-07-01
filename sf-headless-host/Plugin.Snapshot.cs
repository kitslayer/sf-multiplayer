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
        private void TickWorldStateSnapshot()
        {
            if (!_matchStarted) return;
            if (_sfClients.Count == 0) return;
            if (Time.realtimeSinceStartup - _lastSnapshotAt < (1.0f / SnapshotHz)) return;
            _lastSnapshotAt = Time.realtimeSinceStartup;
            _serverTick++;
            RecordTickSample();        // Phase 6.14.5 — history before broadcast
            BroadcastWorldStateSnapshot();
        }

        private void BroadcastWorldStateSnapshot()
        {
            try
            {
                int n = 0;
                foreach (var kv in SlotToRig) if ((object)kv.Value != null) n++;

                // Phase 6.14 — also pack NSO positions for server-determined
                // box / chain / ice-debris falling. Only include NSOs whose
                // rigidbody is non-kinematic (i.e. currently allowed to move).
                // Pre-placed static crates/chains stay kinematic until struck
                // and don't need bandwidth.
                bool periodicKeyframe = Time.realtimeSinceStartup >= _nsoPeriodicKeyframeNextAt;
                if (periodicKeyframe)
                    _nsoPeriodicKeyframeNextAt = Time.realtimeSinceStartup + NsoPeriodicKeyframeSec;
                var nsoEntries = periodicKeyframe
                    ? CollectAllNsoSnapshot()
                    : CollectActiveNsoSnapshot();

                // P0-14 — also pack MapInfoSyncableBase positions (moving
                // platforms, pressure pillars, ghost platforms) so the
                // oracle is authoritative for these too. Without this they
                // drift independently on each client.
                var mapSyncEntries = CollectMapSyncSnapshot();
                var mapStateEntries = CollectMapStateSnapshot();
                LogMapSyncDiagnostics(mapSyncEntries.Count, mapStateEntries.Count);

                if (n == 0 && nsoEntries.Count == 0 && mapSyncEntries.Count == 0 && mapStateEntries.Count == 0) return;

                byte[] body = BuildWorldStateBody(nsoEntries, mapSyncEntries, mapStateEntries);

                // Broadcast to ALL spawned clients on their v26 endpoint. Once
                // a client has sent a PlayerInput packet we know its actual
                // v26 source addr (recorded in _slotV26Endpoint); before that
                // we fall back to clientIP:V26_CLIENT_PORT. Lets two clients
                // on the same machine use different v26 ports without colliding.
                foreach (var kv in _sfClients)
                {
                    if (!kv.Value.Initialized) continue;
                    IPEndPoint v26Ep;
                    if (!_slotV26Endpoint.TryGetValue(kv.Value.Slot, out v26Ep))
                        v26Ep = new IPEndPoint(kv.Value.Addr.Address, V26_CLIENT_PORT);
                    SendSfPacket(v26Ep, PktWorldStateSnapshot, body, 0, 0);
                }
                if (_serverTick == 1 || _serverTick % 90 == 0)
                    Log.LogInfo($"[P6.10/14/v26.6] Snapshot tick={_serverTick} players={n} nsos={nsoEntries.Count} mapSync={mapSyncEntries.Count} mapState={mapStateEntries.Count} fallResets={_nsoFallthroughResetCount} keyframe={periodicKeyframe} bytes={body.Length}");
            }
            catch (Exception e) { Log.LogWarning($"[P6.10/14] {e.Message}"); }
        }

        // Serialize a v26.6 world-state body. This is the SINGLE place the wire
        // layout lives — both the periodic broadcast and the per-endpoint keyframe
        // build through here, so the two can never drift apart.
        //   u32 serverTick
        //   u8  playerCount
        //   players: [u8 slot, f32 x, f32 y, f32 z, u32 lastInputSeq] × n  (17/each)
        //   u16 nsoCount;  NSOs:  [u16 id, f32 x, f32 y, f32 z, f32 rotZ]  × m (18/each)
        //   u16 projCount; projs: [u32 id, u8 slot, u8 wType, f32 x,y,z]   × k (18/each)
        //   u16 mapSyncCount (v26.5 positions)
        //   mapState section (v26.6 GetData payloads — GhostPlatform isOn, etc.)
        //   u16 nsoUpCount; [u16 id, f32 upY, f32 upZ] × m  (v26.7 appendix —
        //       stock SF syncs NSO rotation as the up-vector's y/z (tipping is
        //       about world X; eulerAngles.z carries ~nothing for it). The
        //       18-byte NSO entry can't grow without breaking deployed 0.5.x
        //       parsers, so the up-vector rides in a trailing section old
        //       clients never read. Reconstruct exactly like stock
        //       LerpLocalDummy: LookRotation(Cross(right, up), up).
        private byte[] BuildWorldStateBody(List<NsoSnap> nsoEntries, List<MapSyncSnap> mapSyncEntries, List<MapStateSnap> mapStateEntries)
        {
            int n = 0;
            foreach (var kv in SlotToRig) if ((object)kv.Value != null) n++;
            int bodyLen = 4 + 1 + n * 17 + 2 + nsoEntries.Count * 18 + 2 + _projectiles.Count * 18
                          + 2 + mapSyncEntries.Count * 20 + MapStateSectionByteLen(mapStateEntries)
                          + 2 + nsoEntries.Count * 10;
            byte[] body = new byte[bodyLen];
            int off = 0;
            WriteU32LE(body, off, _serverTick); off += 4;
            body[off++] = (byte)n;
            // slot → LastInputSeq lookup once (avoids an O(n²) per-player scan).
            var slotSeq = new Dictionary<int, uint>(_sfClients.Count);
            foreach (var ckv in _sfClients) if (ckv.Value.Slot >= 0) slotSeq[ckv.Value.Slot] = ckv.Value.LastInputSeq;
            foreach (var kv in SlotToRig)
            {
                var rig = kv.Value;
                if ((object)rig == null) continue;
                body[off++] = (byte)kv.Key;
                Vector3 p = rig.transform.position;
                WriteF32LE(body, off, Finite(p.x)); off += 4;
                WriteF32LE(body, off, Finite(p.y)); off += 4;
                WriteF32LE(body, off, Finite(p.z)); off += 4;
                uint lastSeq = 0;
                slotSeq.TryGetValue(kv.Key, out lastSeq);
                WriteU32LE(body, off, lastSeq); off += 4;
            }
            WriteU16LE(body, off, (ushort)nsoEntries.Count); off += 2;
            foreach (var e in nsoEntries)
            {
                WriteU16LE(body, off, e.Id); off += 2;
                WriteF32LE(body, off, e.X); off += 4;
                WriteF32LE(body, off, e.Y); off += 4;
                WriteF32LE(body, off, e.Z); off += 4;
                WriteF32LE(body, off, e.RotZ); off += 4;
            }
            // Phase 6.17 — projectile entries.
            WriteU16LE(body, off, (ushort)_projectiles.Count); off += 2;
            foreach (var p in _projectiles)
            {
                WriteU32LE(body, off, p.Id); off += 4;
                body[off++] = p.OwnerSlot;
                body[off++] = p.WeaponType;
                WriteF32LE(body, off, Finite(p.Position.x)); off += 4;
                WriteF32LE(body, off, Finite(p.Position.y)); off += 4;
                WriteF32LE(body, off, Finite(p.Position.z)); off += 4;
            }
            // P0-14 — MapInfoSyncableBase entries (v26.5 section).
            WriteU16LE(body, off, (ushort)mapSyncEntries.Count); off += 2;
            foreach (var m in mapSyncEntries)
            {
                WriteF32LE(body, off, Finite(m.StartX)); off += 4;
                WriteF32LE(body, off, Finite(m.StartY)); off += 4;
                WriteF32LE(body, off, Finite(m.X)); off += 4;
                WriteF32LE(body, off, Finite(m.Y)); off += 4;
                WriteF32LE(body, off, Finite(m.Z)); off += 4;
            }
            off = WriteMapStateSection(body, off, mapStateEntries);
            // v26.7 — NSO up-vector appendix (same ids/order as the NSO section).
            WriteU16LE(body, off, (ushort)nsoEntries.Count); off += 2;
            foreach (var e in nsoEntries)
            {
                WriteU16LE(body, off, e.Id); off += 2;
                WriteF32LE(body, off, e.UpY); off += 4;
                WriteF32LE(body, off, e.UpZ); off += 4;
            }
            return body;
        }

        private struct NsoSnap { public ushort Id; public float X, Y, Z, RotZ, UpY, UpZ; }

        // P0-14 — MapInfoSyncableBase position snapshot entry.
        // Identified by Vector2 startPos (same key stock SF uses in its
        // mMapDataObjectToSync dictionary). We can't use transform.GetInstanceID()
        // because Unity assigns those per-process — server's IDs never
        // match client's. With P0-12 active, both sides quantize the
        // startPos to 0.01 precision so the Vector2 keys ARE stable
        // cross-process.
        private struct MapSyncSnap { public float StartX, StartY, X, Y, Z; }

        // P0-13 — full-keyframe variant of CollectActiveNsoSnapshot that
        // includes every NSO regardless of position-delta / activity. Used
        // exactly once per new v26 endpoint so newly-joining clients learn
        // the current resting position of at-rest NSOs. Still respects the
        // Y > -30 filter (don't ship killbox-fallen NSOs).
        private List<NsoSnap> CollectAllNsoSnapshot()
        {
            var result = new List<NsoSnap>();
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = AccessTools.TypeByName("NetworkSyncableObject");
                    if ((object)_nsoType == null) return result;
                    _nsoIndexProp = AccessTools.Property(_nsoType, "Index");
                    _nsoIndexField = AccessTools.Field(_nsoType, "m_Index");
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                if (all == null) return result;
                foreach (var nso in all)
                {
                    var comp = nso as Component;
                    if ((object)comp == null) continue;
                    if (!SceneMatchesCurrentMap(comp)) continue;
                    if (IsWeaponNsoRoot(comp.gameObject)) continue;
                    ushort id = 0;
                    if ((object)_nsoIndexProp != null) id = (ushort)_nsoIndexProp.GetValue(nso, null);
                    else if ((object)_nsoIndexField != null) id = (ushort)_nsoIndexField.GetValue(nso);
                    var p = comp.transform.position;
                    if (!IsFiniteVec3(p) || p.y < -30f) continue;
                    var e = comp.transform.eulerAngles;
                    var up = comp.transform.up;
                    result.Add(new NsoSnap { Id = id, X = p.x, Y = p.y, Z = p.z, RotZ = e.z, UpY = up.y, UpZ = up.z });
                }
            }
            catch (Exception ex) { Log.LogWarning($"[P0-13 keyframe collect] {ex.Message}"); }
            return result;
        }

        // P0-13 — build a v26 snapshot containing all current players +
        // every NSO and send it to a single endpoint. Wire format is
        // identical to BroadcastWorldStateSnapshot so existing client
        // parsers handle it without changes.
        private void SendKeyframeSnapshotToEndpoint(IPEndPoint target)
        {
            int n = 0;
            foreach (var kv in SlotToRig) if ((object)kv.Value != null) n++;
            var nsoEntries = CollectAllNsoSnapshot();
            var mapSyncEntries = CollectMapSyncSnapshot();
            var mapStateEntries = CollectMapStateSnapshot();
            byte[] body = BuildWorldStateBody(nsoEntries, mapSyncEntries, mapStateEntries);
            SendSfPacket(target, PktWorldStateSnapshot, body, 0, 0);
            Log.LogInfo($"[P0-13/v26.6] Sent keyframe snapshot to {target} — players={n} nsos={nsoEntries.Count} mapSync={mapSyncEntries.Count} mapState={mapStateEntries.Count} bytes={body.Length}");
        }
    }
}
