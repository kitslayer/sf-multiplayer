# Simplification & breakdown plan

> What can be split up, thinned out, or deduplicated to make this repo easier to
> work on. Ordered by value (impact ÷ risk). Nothing here changes runtime
> behavior — every item is a structural / hygiene change. Written 2026-07-01.

## Status (2026-07-01, branch `claude/project-simplify-breakdown`)

Landed on this branch (all structural — no runtime behavior change; the C#
still needs a local build against `refs/` since there's no compiler here):

- ✅ **Item 1** — `SFHeadlessHost.cs` split 7146 → 1174 lines across 13 partial
  files + `PerLobbyLogListener.cs`. Verified: every original line preserved
  exactly once + every file brace-balanced.
- ✅ **Item 4** — `SFClientRecon.cs` split 2359 → 743 lines across 6 partial
  files. Same verification.
- ✅ **Item 2** — `PROJECT_STATE.md` + `STATUS.md` moved to `notes/archive/`.
  (Left the aggressive `NEXT_STEPS.md` blockquote trim to the maintainer — it's
  editorial and the detail is duplicated in `WHATS_NEW.md`.)
- ✅ **Item 3** — three identical `Mono2Polyfills.cs` collapsed to `shared/`.
- ✅ **Item 6** — `.github/workflows/ci.yml` (Go router + Python), verified
  green locally.
- ✅ **Item 8** — `archive/` + this doc indexed from `notes/README.md`.
- ⏸️ **Items 5, 7** (committed build artifacts; loose root scripts) — left as-is
  on purpose: both have external blast radius (player-facing download links;
  live-server systemd units referencing absolute script paths) and should be
  confirmed by the maintainer before moving.

---

The project works and is deployed; the friction is not the code *logic*, it's
that the code and the docs have grown into a few very large blobs. Two 7k/2k-line
source files and six overlapping top-level status docs are where most of the
"where does this live / what's current" cost comes from.

---

## The two big wins

### 1. Split `SFHeadlessHost.cs` (7,146 lines) along the seams it already has

`sf-headless-host/SFHeadlessHost.cs` is a single `public partial class Plugin`.
Because it's **already `partial`**, it can be split across files with **zero
behavior change** — the compiler treats the parts as one class. This is the
single highest-value change: it turns "scroll through 7k lines" into "open the
file named for the concern."

The method groups are already clustered in the file. A natural split:

| New file | Concern | Representative members (current line) |
|---|---|---|
| `Plugin.cs` | Lifecycle, fields, `Awake`/`Update`/`FixedUpdate` | `Awake` (105), `Update` (1919), `FixedUpdate` (1951) |
| `Plugin.HarmonyPatches.cs` | All `[HarmonyPatch]` prefixes/postfixes + `TryPatch` | `InjectInputPrefix` (366) … `PatchServerPort` (7075), `TryPatch` (1755) |
| `Plugin.Boot.cs` | Boot state machine + scene settle | `BootState` enum (1902), `StepBoot` (2012), `OnAnySceneLoadedRunSettle` (885) |
| `Plugin.Net.cs` | UDP bridge, packet send/broadcast, dispatch | `DrainSfServer` (2401), `SfDispatch` (2516), `SendSfPacket` (6033), `BroadcastSfPacket` (6053) |
| `Plugin.ClientHandlers.cs` | `Handle*` request handlers | `HandlePickupRequest` (3864) … `HandleClientFireWeapon` (5034) |
| `Plugin.MatchFlow.cs` | Rounds, spawn, match start | `AdvanceRound` (3765), `FireMatchStart` (4241), `SpawnAuthoritativePlayersForAllClients` (4469) |
| `Plugin.Anticheat.cs` | Rate guards + damage validation | `RateGuard` (3392), `AnticheatObserve` (3416), `ValidateDamagePacket` (2921), `Ac*` (2815+) |
| `Plugin.Chat.cs` | Admin chat commands | `TryProcessChatCommand` (3089), `SendChatToPlayer` (3026), `IsAdminSender` (3066) |
| `Plugin.Nso.cs` | NetworkSyncableObject physics sync | `TickNsoFallGuard` (4778), `RebuildNsoIndexCache` (5819), `ApplyClientObjectUpdate` (5764) |
| `Plugin.Projectiles.cs` | Server-side bullets + blast | `Projectile` (4976), `TickProjectiles` (5091), `ApplyExplosiveBlastAt` (5219) |
| `Plugin.Rpc.cs` | Bridge JSON command surface + inspectors | `HandleBridgeCommand` (6176), `EmitStateSnapshot` (6361), `InspectRig` (6454) |

Nested helper types (`SfClient`, `InputFrame`, `Projectile`, `NsoSrvEntry`,
`RateGuard`, `TickSample`, the snapshot structs) move next to the code that owns
them. `PerLobbyLogListener` (7115) is a separate top-level class already — pull it
into its own `PerLobbyLogListener.cs`.

- **Impact:** high — this is the file everyone edits.
- **Risk:** low, but **must be compile-verified** (net46, needs the game/BepInEx
  DLLs in `refs/`). A misplaced brace fails the build; nothing subtler can go
  wrong. Do it as one mechanical commit, build once, done.
- **Do NOT** attempt this without a local build — it's deployed to the live
  server and there's no CI to catch a break (see item 6).

### 2. Collapse the top-level status docs from six to two

Current top-level Markdown (excluding README):

| File | Size | State |
|---|---|---|
| `README.md` | 22 KB | current — keep |
| `NEXT_STEPS.md` | 21 KB | "living" doc — keep, but trim (see below) |
| `WHATS_NEW.md` | 47 KB | running session log |
| `BUGS_LOOP.md` | 59 KB | one autonomous loop's evidence log |
| `PROJECT_STATE.md` | 11 KB | **explicitly "Superseded (2026-06-06)"** |
| `STATUS.md` | 7 KB | **explicitly "Superseded (2026-06-06)"** |

Four of these are historical. `NEXT_STEPS.md` itself opens with a ~250-line stack
of dated `> Latest (…)` blockquotes that duplicate `WHATS_NEW.md` verbatim.

Proposal:
- Move `PROJECT_STATE.md`, `STATUS.md`, `BUGS_LOOP.md` into `notes/archive/`
  (they're snapshots — they read like archive, not root docs). They already
  point readers to `NEXT_STEPS.md` as the source of truth.
- Keep **`README.md`** (how to use it) and **`NEXT_STEPS.md`** (current state +
  what's next) as the only two root status docs.
- In `NEXT_STEPS.md`, keep only the most-recent 1–2 `> Latest` entries and link
  the rest to `WHATS_NEW.md`, which is the canonical running log.

- **Impact:** high — "which doc is current?" is the first question anyone
  (including future-you) hits.
- **Risk:** near-zero (moving/trimming prose). This is the **safest** big win and
  a good first commit. It's the one item worth doing without a build environment.

---

## Medium wins

### 3. Dedupe `Mono2Polyfills.cs` (3 byte-identical copies)

`sf-headless-host/`, `sf-client-recon/`, and `sf-server-browser/` each carry a
byte-identical `Mono2Polyfills.cs` (md5 `feab8b2…`). Three copies drift.
Put one canonical copy in a shared location and reference it with a linked
compile item in each `.csproj`:

```xml
<Compile Include="..\shared\Mono2Polyfills.cs" Link="Mono2Polyfills.cs" />
```

(`sf-client-recon` already symlinks its `refs/` to the host's — same pattern.)
- **Impact:** medium. **Risk:** low, but rebuild all three plugins to confirm.

### 4. Split `sf-client-recon/SFClientRecon.cs` (2,359 lines)

Same story as item 1, one tier down: another single `partial class Plugin`. The
sibling files (`SfMapTerrainClient.cs`, `SfOracleLobbyConnect.cs`,
`SfNsoClientPush.cs`, `SfDebugConsole.cs`) show the split pattern already works
here. Break the remaining monolith into e.g. `Plugin.Patches.cs`,
`Plugin.Snapshot.cs`, `Plugin.Reconcile.cs`. Lower priority than the host only
because it's a third the size. Same "must compile-verify" caveat.

### 5. Stop committing build artifacts

`.gitignore` already ignores `*.dll` / `*.exe`, yet these are force-added and
tracked:
- `dist/*.dll`, `dist/SFLauncher.exe`
- `1-click-install/files/*.dll`
- `sf-multiplayer-StickFight-Installer.zip` (**1.1 MB**, duplicates `dist/`)
- `legacy/StickFightLauncher/*.syso`, `winres/*.bin`

These bloat clones and go stale against source (`NEXT_STEPS.md` already notes the
shipped installer is behind the source). Recommend: attach the installer zip +
plugin DLLs to a **GitHub Release** per version and drop them from the tree, or
at minimum move the release zip out of git. This is a judgment call for the
maintainer — the current model is "the repo *is* the distribution channel," so
**confirm before removing** anything a player-facing install link points at.
- **Impact:** medium (clone size, staleness). **Risk:** low mechanically, but
  check no README/installer link 404s first.

---

## Smaller / structural

### 6. Add a minimal build check (there is no CI)

`.github/workflows/` is empty. There's no guardrail that a split like item 1 even
compiles. The plugins build against copyrighted `refs/` DLLs that can't live in
CI, but two cheap things are possible:
- CI for the parts that *can* build in the open: **`sf-router` (Go)** — run
  `go build ./... && go test ./...` (there's already a real test suite:
  `router_test.go`, `routing_test.go`, `registry_test.go`, `select_test.go`).
- CI for the **Python** tests already in the repo (`test_serve_lobbies.py`,
  `test_sf_monitor.py`) — `python -m pytest`.

This won't catch a C# break, but it locks down the two subsystems that *can* be
checked and makes the "split the big files" work safer to accept via PR.
- **Impact:** medium (long-term). **Risk:** none.

### 7. Consolidate the loose root-level scripts

11 `*.sh`/`*.ps1` scripts sit in the repo root (`launch-*.sh`, `stop-*.sh`,
`list-lobbies.sh`, `oracle-watchdog.sh`, `setup-all.sh`, `deploy-physics-fix.ps1`)
alongside more in `deploy/`. Grouping the operator scripts under `scripts/` (or
folding them into `deploy/`) declutters the root and puts the ops surface in one
place. Update the systemd units / docs that reference them.
- **Impact:** low–medium (root readability). **Risk:** low — grep for each
  script name in `deploy/*.service`, `*.md`, and other scripts before moving.

### 8. Prune / index `notes/` (49 files)

`notes/` has 49 Markdown files across `bug-investigations/`, `design/`, `phase6/`,
`recon/`. Most are point-in-time investigations that are done. `notes/SUMMARY.md`
exists but a short **index at `notes/README.md`** ("start here / active vs
archived") would make the design record navigable instead of a flat pile.
- **Impact:** low. **Risk:** none.

---

## Suggested order

1. **Item 2** (doc consolidation) — safe, no build needed, biggest day-to-day
   clarity win. Good first PR.
2. **Item 6** (Go + Python CI) — cheap, and it de-risks everything after it.
3. **Item 1** (split the host file) — the headline change; do it in a build
   environment, one mechanical commit, verify it compiles.
4. **Items 3, 4** (dedupe polyfill, split client) — same build session.
5. **Items 5, 7, 8** (artifacts, scripts, notes index) — cleanup, any time.

Only item 2 (and the doc/notes items 8) are safe to land from an environment
that can't build the C# plugins. Everything touching `.cs`/`.csproj` needs a
local build against `refs/` before it's pushed, because the live server has no CI
net under it.
