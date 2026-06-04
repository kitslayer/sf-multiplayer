# Match replay system (Tier-2, full-pose) — design + format spec

**Status:** design (branch `feature/replay`). Goal: record every match centrally
and play it back with **perfect forward / backward / slow-mo / scrub** and full
posed stick-figure fidelity.

## Architecture (2a — central)

```
each client ──(msgType 43 PlayerPose, 50Hz/FixedUpdate)──► oracle (SFHeadlessHost)
                                                             │  recorder assembles keyframes
oracle already has: NSO transforms, projectiles, events ────┘  (poses + NSO + proj + events)
                                                             ▼
                                          replay file per match  →  /replays HTTP  →  in-game viewer
```

- **Why state-recording (not input re-sim):** only recorded transforms give clean
  reverse/scrub/slow-mo — you *play back* states, never re-simulate (PhysX isn't
  deterministic and re-sim is forward-only).
- **Why each client reports its OWN pose:** the ragdoll limbs are simulated
  client-side; each player is authoritative over their own body. The headless
  oracle runs only kinematic mirror rigs, so it can't produce poses itself.
- **Capture rate = the physics rate, which on the client is 60 Hz** (SFClientRecon
  sets `Time.fixedDeltaTime = 1/60` at startup — so my earlier "50 Hz" was wrong).
  Sample in `FixedUpdate`; read `Time.fixedDeltaTime` at runtime and write it in the
  header so the file is self-describing. **v1 recommendation: send + record at
  30 Hz** (aligned to the server's 30 Hz snapshot tick) — half the bandwidth/disk,
  and the viewer interpolates so slow-mo stays smooth; bump to 60 Hz later if
  wanted (one-line send-rate change). Full plan: `REPLAY_PLAN.md`.

## Collision-minimal integration (ALKA is actively rewriting both plugins)
- **Client:** new standalone file `sf-client-recon/SfPoseReport.cs` — its own
  `MonoBehaviour` + `UdpClient` to the oracle (reads `-address` like
  SFClientRecon). **No edits to SFClientRecon.cs.**
- **Server:** new file `sf-headless-host/SfReplayRecorder.cs` (`partial class
  Plugin`) holds all recorder logic; **only ~3 one-line hooks** in
  SFHeadlessHost.cs: a `case PktPlayerPose:` in the packet dispatch, a
  `Replay.OnSnapshot(...)` in `BroadcastWorldStateSnapshot`, and event taps
  (MapChange / TookDamage / Death / pickup). Small merge surface.
- **Reserved: `msgType 43 = PktPlayerPose`** (39 snapshot, 40 input, 41 fire,
  42 box-force are taken; 43 free). Coordinate this byte with ALKA.

## Wire: msgType 43 `PlayerPose` (client → server, per physics tick)

```
u32 serverTickEcho (LE)    -- last serverTick the client saw (for alignment)
u8  slot
u8  flags                  -- bit0 alive, bit1 hasWeapon
u8  bodyCount              -- N rigidbodies captured (self-describing)
for each body (6 bytes):
  i16 y   (LE, world Y ×100)   -- 2.5D plane; X is ~0, omitted
  i16 z   (LE, world Z ×100)
  i16 rotZ(LE, degrees ×100)
if hasWeapon:
  u8  weaponType
  i16 wy, i16 wz, i16 wrotZ   (×100)
```
Body order = `GetComponentsInChildren<Rigidbody>()` hierarchy order, captured
once into the header so the viewer maps bodies 1:1. Quantized i16×100 keeps it
small (~56 B/player ≈ 1.7 KB/s up at 50Hz; server receives ~7 KB/s for 4).

## Replay file format (`.sfr`, written server-side, one per match)

```
HEADER
  magic "SFREPLAY\0", u16 formatVersion
  f32  fixedDeltaTime         -- the capture step (self-describing)
  str  mapScene (initial)
  u8   playerCount
  for each player: u8 slot, u64 steamID, str name, u8 colorIdx, u8 bodyCount,
                   [bodyName strings]   -- the body layout for puppets
KEYFRAME STREAM (one per physics tick)
  u32  tick, f32 tMatch (seconds since match start)
  players[]:  slot, flags, body[bodyCount]{y,z,rotZ}, [weapon]
  nso[]:      id, y, z, rotZ              (from the server's snapshot)
  proj[]:     id, ownerSlot, weaponType, y, z
  events[]:   type, slot/target, params  (fire, hit, death, pickup, mapchange, round)
```
Delta-encode positions between keyframes + gzip → small (raw ≈ 7–11 MB / 5-min
match; compressed far less). Recorder buffers + flushes async (never blocks the
game loop). Round/match boundaries open/close files; retention policy on disk.

## Playback (the "perfect" part)
Viewer loads `mapScene`, instantiates stock player prefabs (`SpawnPlayerDummy`)
as puppets with **every rigidbody kinematic**, drives each body's transform from
the interpolated keyframes. A playback clock you move freely: slow-mo = advance
<1× + LERP/SLERP between keyframes; reverse = advance backward; scrub = seek to
any time; frame-step = ±1 tick. Events fire effects as the clock crosses them
(suppressed/handled on reverse). Timeline UI reuses ALKA's uGUI kit.

## Build order
1. Format + a tiny inspector (read a `.sfr`, dump the timeline).
2. Client `SfPoseReport.cs` (capture + send msgType 43).
3. Server `SfReplayRecorder.cs` + the 3 hooks (assemble + write keyframes).
4. Verify end-to-end: local headless + a graphical client → record → inspect.
5. Viewer: load map, kinematic puppets, forward playback.
6. Scrub / reverse / slow-mo / frame-step + timeline UI.
7. `/replays` library endpoint + in-game Replays menu.

## Open questions
- Body set/order: auto-capture all child rigidbodies (self-describing) — confirm
  it's stable across spawn.
- Held-weapon fidelity: type + transform (above) vs just type.
- Recorder home: in SFHeadlessHost (has NSO/proj/events) — chosen, via the new
  partial file + minimal hooks.
- Retention/disk budget on `.115`.
