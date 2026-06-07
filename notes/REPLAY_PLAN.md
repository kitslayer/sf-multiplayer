# Replay system — full execution plan (Tier-2 central, full-pose)

> ⚠️ **Historical record (2026-05).** Kept for reference; current state in [NEXT_STEPS.md](../NEXT_STEPS.md). (Unbuilt design proposal; references to coordinating with ALKA predate ALKA's departure.)

Companion to `REPLAY.md` (the format spec). This is the grounded, file:line-level
build plan, from three code investigations. Branch: `feature/replay` (off current
`main`). Goal: every match recorded centrally; viewer with **perfect
forward/backward/slow-mo/scrub** and full posed stick-figures.

## 0. Corrections / decided facts (grounded this pass)
- **Capture rate = 60 Hz, not 50.** `SFClientRecon.Awake` sets `Time.fixedDeltaTime
  = 1/60` (`SFClientRecon.cs:179`), so the client physics step — and thus
  `FixedUpdate` pose capture — is 60 Hz. Read `Time.fixedDeltaTime` at runtime and
  store it in the header (self-describing). **Recommendation for v1: send + record
  poses at 30 Hz** (aligned to the server's 30 Hz snapshot tick) — half the
  bandwidth/disk, and the viewer interpolates so slow-mo stays smooth. Bumping to
  60 Hz later is a one-line send-rate change. (Flag for kit when back.)
- **msgType 43 = `PktPlayerPose`** (39 snapshot / 40 input / 41 fire / 42 box-force
  taken; 43 free, confirmed). Coordinate this byte with ALKA.
- **Wire envelope** (confirmed `SendPlayerInputPacket`): `[u32 ts LE][u8 msgType]
  [body][u64 steamID LE][u8 channel]`, total = body+14; bodyOffset 5, bodyLen =
  len-14.
- **2.5D rotation axis is uncertain** — investigator says player in-plane rotation
  reads from `eulerAngles.x`, while NSOs use `.z`. **Capture the full rotation per
  body** (quaternion or 3 euler) for v1 so fidelity can't be wrong; compress to one
  axis only after confirming in-game.

## 1. Client pose-reporter — `sf-client-recon/SfPoseReport.cs` (NEW, standalone)
Own `MonoBehaviour` on its own GameObject + own `UdpClient`; **zero edits to
SFClientRecon.cs**. Registered from a tiny `Awake` (or its own `[BepInPlugin]`).

- **Oracle endpoint:** reuse the resolution logic from `SfOracleLobbyConnect.
  ResolveOracleEndpoint` (`SfOracleLobbyConnect.cs:38-104`): `-address`/`-port`,
  else `SF_ORACLE_ADDRESS`/`PORT`, else the config file; default port 1337.
- **Local player + slot:** `AccessTools.TypeByName("Controller")`, fields
  `playerID` (`Controller.cs:47`, public) + `mHasControl` (`Controller.cs:65`,
  private). `FindObjectsOfType(Controller)` → the one with `mHasControl==true` →
  `playerID` = slot; its `gameObject` is the player root. (Mirror
  `SFClientRecon.FindLocalSlot` `:2005-2039`.)
- **Body enumeration (STABLE, fixed order):** by marker type via
  `root.GetComponentInChildren(type).GetComponent<Rigidbody>()`, in this fixed
  list (record the layout in the header so it's self-describing):
  `Torso, Hip, LeftHand, RightHand, LeftElbow, RightElbow, LeftKnee, RightKnee`
  (+ `Head` if it has a Rigidbody). **Null-check Hip/LeftKnee/RightKnee/Head**
  (null-guarded in `Fighting.Start` `:167-185`; some are per-prefab). Do NOT use
  `GetComponentsInChildren<Rigidbody>()` (DFS, unstable) or `Standing.rigsToLift`
  (prefab-defined order). Marker classes exist for each (`Torso.cs`, `Hip.cs`, …).
- **Per body:** world `position` (Y, Z — X is the frozen depth axis) + **full
  rotation** (capture all of it v1). Quantize to i16×100 for send (positions in
  ±100 fit i16; rotation as i16 degrees×100, or send a packed quaternion).
- **Weapon:** `GetComponent<Fighting>()` on root; `weapon != null` (has weapon),
  `CurrentWeaponIndex` (public prop, byte 1-indexed, `Fighting.cs:153`),
  `weapon.GetComponent<Rigidbody>()` transform.
- **Send in `FixedUpdate`** (60 Hz on this client; gate to 30 Hz for v1 with a
  send-interval timer). Frame with the v25 envelope + `WriteU32LE`/`WriteF32LE`
  (copy the helpers, `SFClientRecon.cs:1933-1946`). Own `UdpClient`, send to the
  oracle endpoint.
- **Mono 2.0:** no `lock{}` — explicit `Monitor.Enter/Exit` if any cross-thread
  state. net35. No LINQ/ValueTuple/Span.

## 2. msgType 43 `PktPlayerPose` wire (client → server) — final
```
u32 serverTickEcho (last serverTick seen, for alignment)
u8  slot
u8  flags        bit0 alive, bit1 hasWeapon
u8  bodyCount
per body: i16 y(×100), i16 z(×100), i16 rotPacked   (or 3×i16 euler / packed quat — v1: full)
if hasWeapon: u8 weaponType, i16 wy, i16 wz, i16 wrot
```

## 3. Server recorder — `sf-headless-host/SFReplayRecorder.cs` (NEW partial) + minimal hooks
All logic in the new `partial class Plugin` file (sibling of `SfMapTerrainHost.cs`).
**Exactly one structural edit** to `SFHeadlessHost.cs`; everything else is one-line
call sites (from investigation):

| Hook | SFHeadlessHost.cs site | Recorder call | Data |
|---|---|---|---|
| dispatch (STRUCTURAL, 4 lines) | after `:2334` (PktClientFireWeapon block) | `case 43 → HandlePlayerPose(data,bodyOffset,bodyLen,from)` | raw pose pkt + sender |
| match open | `FireMatchStart` `:3787` | `Replay.OnMatchStart(_currentSceneIndex, players)` | scene idx, players |
| round close/open | `AdvanceRound` `:3395` (first line) | `Replay.OnRoundEnd(_roundCounter, _currentSceneIndex)` | round#, scene |
| keyframe | `TickWorldStateSnapshot` `:4786` (after `RecordTickSample`) | `Replay.OnTick(_serverTick, SlotToRig, _projectiles, CollectAllNsoSnapshot())` | positions, proj, NSO |
| fire event | `HandleClientFireWeapon` `:4509` (after `_projectiles.Add`) | `Replay.OnFire(p)` | projectile |
| damage/death | after `ValidateDamagePacket` true `:2742` | `Replay.OnDamage(victim,attacker,dmg,isKill)` | slots, dmg, killflag |
| pickup | `HandlePickupRequest` `:3484` | `Replay.OnPickup(sender.Slot, weaponNetId)` | slot, weapon id |
| match end | `ResetMatchStateForLobby` `:2287` | `Replay.OnMatchEnd()` | — |

- **Per-slot latest pose** held from `HandlePlayerPose`; **keyframe written on the
  30 Hz snapshot tick** (`OnTick`) = latest poses + NSOs (`CollectAllNsoSnapshot`,
  `NsoSnap{Id,X,Y,Z,RotZ}`) + projectiles (`_projectiles`, `Projectile{Id,
  OwnerSlot,WeaponType,Position,...}`) + events accumulated since last tick.
- **File lifecycle:** open on `OnMatchStart`, **close+reopen each `OnRoundEnd`**
  (one file per round, or one per match with round markers — decide; one-per-match
  with markers is simpler for the library), final close on `OnMatchEnd`.
- **File path:** `SF_REPLAY_DIR` (default `/tmp/sf-replays` → use a persistent disk
  dir like `~/sf-replays` on .115), name `<code>-<bridge>-<utc>-r<N>.sfr`. Mirror
  `PerLobbyLogListener` (`:6345`): `[ThreadStatic]` re-entry guard (NO `lock{}`),
  buffered `StreamWriter`, flush on round/match boundary or every N ticks. **Never
  block the game loop** (buffer + async flush, like sf-monitor).

## 4. Replay file format (`.sfr`) — see REPLAY.md §"file format" (header + keyframe stream)
Header carries `fixedDeltaTime`, the **map build index + MapType** (viewer loads
`SceneManager.LoadScene(buildIndex, Additive)`), the player roster, and the
**body layout** (names/order) so the viewer maps bones 1:1. Delta-encode + gzip.

## 5. Viewer — `sf-replay-viewer/` (NEW BepInEx plugin) or a tab in sf-server-browser
- **Load map:** `int buildIndex = BitConverter.ToInt32(MapData,0); SceneManager.
  LoadScene(buildIndex, Additive)`; after load `FindObjectOfType<MapInfo>()`,
  `SetActive(true)`, `dontFollowTheSwoosher=true` (`GameManager.cs:878-934`).
- **Puppets:** `SpawnPlayerDummy` (`MultiplayerManager.cs:1706-1736`) or
  `Instantiate(MultiplayerManagerAssets.Instance.PlayerPrefab,…)` per player.
  **GOTCHA:** `NetworkPlayer.Start()` self-destructs if `MatchmakingHandler.
  IsNetworkMatch != true` (`NetworkPlayer.cs:205`) — set that flag (or strip
  NetworkPlayer) for the viewer.
- **Make kinematic + freeze logic:** `GetComponentsInChildren<Rigidbody>()` →
  `isKinematic=true` (mirror `InitRigidBodies` `NetworkPlayer.cs:183-196`); then
  **disable `Standing`, `Fighting`, `Controller`, `Movement`** (they `AddForce`
  every FixedUpdate and will fight your transforms — `Standing.cs:79`,
  `Fighting.cs:217`). Don't use `PickUpWeapon` (it builds a dynamic joint) — just
  `SetActive` the right `weapons` child and drive its transform.
- **Drive each playback frame:** set every bone `transform.position/rotation` from
  the interpolated keyframe; set the weapon GO transform.
- **Playback engine (the "perfect" part):** a free playback clock `t`. Slow-mo =
  advance `t` at speed s (incl. <1) + LERP/SLERP between the two bracketing
  keyframes; **reverse** = advance `t` backward; **scrub** = set `t`, instant seek;
  **frame-step** = ±1 keyframe. Everything kinematic → no physics fights it.
- **Events on the timeline:** fire muzzle-flash/blood/sound/kill-feed as `t` crosses
  an event forward at ~1×; on reverse/scrub, suppress one-shots and reconstruct
  persistent state (broken crates simply aren't in the snapshot at that t).
- **Timeline UI:** reuse `Ugui` kit (`UguiKit.cs` — `CreateCanvas/Panel/Label/Btn/
  Pill/CenteredCard`). Play/pause/reverse/frame-step = `Ugui.Btn`; speed = `Pill`;
  time readout = `Label`. **Missing: a scrub slider** — add `Ugui.MakeSlider`
  (uGUI `UnityEngine.UI.Slider`; investigator supplied a working factory). Build the
  bar in a `CenteredCard` at screen bottom.

## 6. Distribution
`/replays` list+download endpoint (extend `serve-lobbies.py` or a small sibling,
sf-monitor-style) serving the `.sfr` library; in-game "Replays" menu (a tab in
ALKA's lobby overlay) → pick → the viewer loads + plays. Retention policy on disk.

## 7. End-to-end test loop (local, no beta needed)
1. Local headless lobby + a graphical client (sf-mirror-local) → play a short match.
2. Confirm the client emits msgType 43 (recon log) and the server writes a `.sfr`.
3. Inspector CLI dumps the timeline (player/NSO/proj counts per keyframe, events).
4. Viewer loads the `.sfr`, plays forward, then scrub/reverse/slow-mo/frame-step.

## 8. Phases / tasks
1. Format + inspector CLI (read a `.sfr`, dump timeline). — no game.
2. `SfPoseReport.cs` (capture + send 43). — client, standalone.
3. `SFReplayRecorder.cs` + the 1 structural + 7 one-line hooks. — server.
4. End-to-end record + inspect (test loop above).
5. Viewer: load map, spawn kinematic puppets, drive bones, forward playback.
6. Scrub/reverse/slow-mo/frame-step + `Ugui.MakeSlider` timeline.
7. Events on the timeline (effects/sound/kill-feed).
8. `/replays` library + in-game Replays menu.

## 9. Risks / mitigations
- **ALKA churn on SFHeadlessHost/SFClientRecon:** client side is a new file (zero
  collision); server side is 1 structural + 7 one-line hooks (tiny merge surface).
  Coordinate msgType 43 + the hook insert with ALKA.
- **Uncertain bodies (RightKnee/Head/Hip null on some prefabs):** capture-all +
  null-check + self-describing body layout in the header.
- **Rotation axis (x vs z):** capture full rotation v1; compress after confirming.
- **`IsNetworkMatch` puppet self-destruct:** set the flag / strip NetworkPlayer.
- **Components fighting transforms:** disable Standing/Fighting/Controller/Movement
  on puppets (must, or they jitter).
- **Rate/bandwidth:** 30 Hz v1 (~0.8–1 KB/player/frame ≈ ~28 B i16-pose; server
  ~few KB/s); 60 Hz optional. Recorder buffered/async — never blocks the loop.
- **Events on reverse/scrub:** one-shots suppressed off-forward; persistent state
  comes from the snapshot (self-correcting).

## 10. Open decisions for kit (when back)
- Capture/record rate: **30 Hz (rec.)** vs 60 Hz.
- Replay file: one-per-match (markers) vs one-per-round. (rec: per-match.)
- Viewer home: standalone plugin vs a tab in ALKA's lobby overlay.
- Rotation encoding once axis confirmed.
- msgType 43 sign-off with ALKA + the SFHeadlessHost hook insert.
