# SF polish loop — queue, protocol & evidence log

**Branch:** `loop/polish-2026-06-22`. This file is the loop's single source of truth and is designed to survive context resets. **Read it in full at the start of every iteration.**

## Mission
Aggressive full-fix pass on three problem areas kit flagged as broken: **(1)** lobby browser + lobby-switching, **(2)** server-authoritative boxes, **(3)** anti-cheat. Attempt real end-to-end fixes — *including* parts that ultimately need kit's live 2-player test — but always be honest about which level of verification each fix actually reached.

**STANDING DIRECTIVE (kit, 2026-06-22): do NOT limit yourself to the seeded queue — there is "way more hidden in plain sight or hidden deep." Actively HUNT.** Every iteration, while investigating your chosen item, code-review the surrounding code + the paths it touches and log any new suspicions in the **DISCOVERY queue** (with a confidence + evidence). Promote confirmed ones to the main queue. Stay skeptical of your OWN finds too — a "bug" that turns out to be a sampling artifact or an intentional trade-off must be marked DISPROVEN/by-design, not shipped (see AC-1's rx=141 false alarm).

## HARD RULES — never violate
- **KEEP the quick-draw / pickup-instant-shot behavior** (pickup with no cooldown + no recoil). It is *wanted* in the comp scene. Do NOT "fix" it, ever.
- **The anti-cheat low-damage-kill kick is intentionally log-only** (gated behind `SF_AC_KICK=1`). Anticheat is observation-only by default (`SF_ANTICHEAT_ENFORCE=1` flips it) — that is **by design** (backlog P2-6). Do NOT "re-enable" either as if it were a bug.
- **Stay on this branch.** Do NOT push, do NOT deploy to `.115`, do NOT touch `main`, do NOT touch any live infra. Merges to main happen only after kit live-verifies.
- **`main` is PUBLIC — keep secrets + personal info out of everything you commit.** No VM/SSH creds or passwords (`.115`), no private keys, no real names / emails / Discord handles, no real-player SteamIDs — not in commit messages, not in `BUGS_LOOP.md`, not in code comments, and not in captured evidence (scrub IPs/usernames/SteamIDs out of logs + screenshots before saving to `loop-evidence/`). Don't over-sanitize otherwise: normal code, bug details, and the already-public server address are fine. The loop never pushes (kit controls merge→main) — this just keeps the branch clean so the eventual merge needs zero scrubbing.
- **Compile-verify EVERY code change before committing.** C# host/client/box/browser: `~/.dotnet/dotnet build <proj>` (8.0.422). Router: `cd sf-router && go build ./... && go test ./...` (go 1.26.2). Never commit a non-building tree.
- **Process discipline (I have killed kit's game twice).** The oracle AND player2 both run from `sf-mirror-local`; distinguish by the `-batchmode` flag, NEVER by install path. NEVER blanket `pkill` SF. Before launching or killing anything, list the matching processes and confirm none is kit's interactive game.
- **When something looks "wrong," check `notes/` + `notes/BUGS_BACKLOG.md` + memory BEFORE changing it.** It may be intentional, or a known accepted trade-off (the P2 table). Several "obvious bugs" in this codebase were deliberate (see backlog "Closed without code change").

## Per-iteration protocol — do ALL of it, every time. Do NOT get into a groove.
1. **Re-read this file's queue.** Pick the single highest-value UNBLOCKED item. State *why that one* and not the others.
2. **Investigate from scratch.** Do not inherit last iteration's framing. Read the actual code (cite `file:line`), logs, `notes/PROTOCOL.md`, and the backlog row. Write a root-cause hypothesis backed by specific evidence.
3. **SKEPTIC PASS (written, mandatory, BEFORE you change anything):**
   - (a) What evidence would prove this hypothesis **wrong**?
   - (b) Could this "bug" be **intentional** / a known trade-off? (check first)
   - (c) What could this change **break** — side effects, other call sites, other maps, other players?
   - (d) Is there a **simpler or safer** fix?
   - Assign a **confidence: N%**.
4. **Make the MINIMAL change.** One bug = one commit. No drive-by edits.
5. **VERIFY at the highest feasible level** (hierarchy below). **Capture evidence**: screenshots → `loop-evidence/<ID>/`, test output, or the exact log lines you grepped. If you can only reach CANDIDATE, say so *loudly*.
6. **SKEPTIC PASS, post-change:** did the evidence actually *confirm* the fix, or does it just *look right*? State residual risk and the **exact** live test kit must run.
7. **Commit** (format below). **Append a full LOG entry** at the bottom of this file. **Update the queue** item's status.
8. **Stop-and-flag if** confidence < ~50% on an unverifiable change, or the change is risky and can't get past CANDIDATE. Better to hand kit a tight, well-documented batch than pile up untested edits.

## Verification hierarchy — always reach for the highest feasible level
- **A — Tests (strongest, fully autonomous).** `sf-router` go tests; `./stress-test-anticheat.py` fired at a local headless oracle. Reproduce the bug as a *failing* test first when you can, then fix to green.
- **B — Web-UI screenshots (autonomous, safe).** `serve-lobbies.py` lobby browser via the Playwright MCP: navigate `localhost`, exercise switch/copy/refresh, screenshot before+after.
- **C — Headless log checks (autonomous, safe — NO display).** Run the headless oracle locally, fire synthetic packets, grep `BepInEx/LogOutput.log` + the Unity log for expected (or forbidden) lines.
- **D — Single-client in-game screenshot (display-coupled, process-risky).** Launch ONE SF client against a local test oracle, screenshot the in-game browser overlay / map. Only if safe; obey process discipline; label as partial.
- **E — Two-player live (NOT autonomous — cannot be done in the loop).** Do NOT fake it. Write it into the **Punch list for kit** with a precise repro + pass/fail criterion.

### Running the headless oracle — Option B (AUTHORIZED by kit 2026-06-22)
The loop MAY run a LOCAL headless oracle for level-C/D checks. It is process-risky (killing kit's game twice is on record), so follow this EXACTLY:
- **Dedicated isolation marker — ALWAYS.** Bridge `1441`, game port `1437`, prefix `/tmp/sf-oracle-prefix-loop1441`, logfile `/tmp/sf-oracle-unity-1441.log`. The token `1441`/`loop1441` is the ONLY thing you may target/kill. The live oracle (`.115`, remote machine) and kit's Steam game never use it.
- **1. ORPHAN GUARD (FIRST, every time):** reap any leftover loop oracle from a crashed prior iteration — `kill -KILL -- -$(cat /tmp/sf-oracle-loop-1441.pgid 2>/dev/null) 2>/dev/null`, then bracket-trick sweep `pkill -KILL -f 'oracle-unity-144[1]'` and `pkill -KILL -f 'prefix-loop144[1]'`; confirm `pgrep -fc 'loop144[1]'` is `0`. Anti-accumulation net for unattended runs.
- **2. Precheck:** `pgrep -afi 'stickfight|proton'` and log it. If kit's game is up it's still safe (you only ever touch the `1441` marker), but note it.
- **3. Launch in its OWN process group + a HARD TIMEOUT** (so the whole tree is killable by PGID and self-dies even if cleanup fails): `setsid bash -c 'echo $$ > /tmp/sf-oracle-loop-1441.pgid; exec timeout 200 env SFHEADLESS_BRIDGEPORT=1441 SFHEADLESS_PORT=1437 SFHEADLESS_DEBUG=1 SFHEADLESS_PREFIX=/tmp/sf-oracle-prefix-loop1441 bash /home/miles/sf-multiplayer/launch-sf-headless.sh' >/tmp/sf-oracle-loop-1441.out 2>&1 & disown` (verified 2026-06-22: boots under Proton in well under 200s).
- **4. Wait for boot via Monitor or a bounded poll** on `/tmp/sf-oracle-unity-1441.log` for a boot/heartbeat line — do NOT bare-`sleep`.
- **5. Drive via the debug bridge** (UDP → `127.0.0.1:1441`) to probe WITHOUT a real client: `loadMap <n>` → `spawnPlayer` → `boxes`/`rigs` dumps; or read the periodic `[BOX-DIAG]` once a match has started. (Match-start needs a kill/`/start`/client; if unreachable via bridge, the `boxes`/`rigs` dumps still give NSO state.)
- **6. MANDATORY cleanup (END + on ANY error path):** `kill -KILL -- -$(cat /tmp/sf-oracle-loop-1441.pgid 2>/dev/null) 2>/dev/null`, then bracket-trick sweep `pkill -KILL -f 'oracle-unity-144[1]'`; confirm `pgrep -fc 'loop144[1]'` == `0`. NEVER leave it running between iterations.
- **NEVER** blanket-`pkill` stickfight/proton/wine without the `1441` marker; never target by install path; never touch a process that doesn't match `1441`.
- **⚠ SELF-MATCH HAZARD (proven in the 2026-06-22 smoke test):** NEVER `pkill -f` with a plain marker your OWN command line contains (e.g. `pkill -f 'sf-oracle-unity-1441'`) — pkill matches the iteration's own shell and kills it mid-cleanup (exit 144), orphaning the oracle. ALWAYS kill by saved PID/PGID or the bracket-trick (`'oracle-unity-144[1]'`), and verify with `pgrep -fc 'loop144[1]'`. Same trap if a verify command echoes plain trigger words ('StickFight'/'proton') — they self-match `pgrep -af`.
- **Scrub evidence:** copy only the relevant `[BOX-DIAG]`/dump lines into `loop-evidence/`, IP/username/SteamID redacted (public-`main` rule).

## Commit message format
```
<scope>: <one-line summary>  [VERIFIED via A/B/C/D | CANDIDATE — needs E]

Hypothesis: ...
Evidence:   file:line ...
Fix:        ...
UPS:        what this should fix
DOWNS:      what it risks / might break / assumptions it rests on
Confidence: N%
Test:       exact test that confirms (or that kit must run)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

## Queue — seeded from `notes/BUGS_BACKLOG.md`; re-rank as you learn
Status: `TODO` / `WIP` / `CANDIDATE` (fix made, needs kit live test) / `VERIFIED` / `BLOCKED` / `WONTFIX`.

| ID | Area | Status | Summary & pointer |
|----|------|--------|-------------------|
| BOX-1 | boxes | NEEDS-LIVE | **P0-23 — code-complete + instrumented; runtime-gated (backlog "diagnostic staged" is STALE).** Already shipped: PostMapLoad collider-refresh every load (SfMapTerrainHost.cs:668), opt-in server safety-floor (695; `SF_SERVER_SAFETY_FLOOR=1`, correctly OFF by default — a flat Y=0 collider would break pit/multi-level maps), NSO fall-guard (SFHeadlessHost.cs:4736), and a thorough `[BOX-DIAG]` line (6747: void count, floor probes @center/@crate w/ layer+trigger, crate physics). No safe high-confidence code change — flipping the floor default = risky; adding to the diagnostic = churn. **Cannot progress without a live `[BOX-DIAG]` capture.** → Punch list. |
| BOX-2 | boxes | STALE | **P0-24 — NOT a live bug (static cross-file trace).** Backlog claimed the round-advance re-arm "never happens." In fact `AdvanceRound`(SFHeadlessHost.cs:3742)→`ResetOracleStateForRoundAdvance`(3783, resets the auth/nso flags + clears rigs)→`ScheduleOracleReloadCurrentMap`(SfMapTerrainHost.cs:1083) which re-arms the FULL cascade incl. `_oracleStartMatchFired=false`(1091). Cascade: StartMatch→CountDown→NSO inventory→`SpawnAuthoritativePlayersForAllClients`. Fixed by `472f447` (2026-05-24). **No code change** — adding a trigger would double-spawn rigs. Runtime confirm → kit punch list. |
| LOBBY-1 | lobby | BLOCKED | Backend switching is **sound** (`go test` green; SELECT/rebind/stale-reresolve all tested). The one switching issue found = the **DOCUMENTED, accepted per-IP limit**: two same-IP players in DIFFERENT lobbies mis-route the non-SELECTing game socket (notes/ROUTER.md:93-96, ROUTER_LIVE_TEST.md:60). Reproduced + pinned as a skipped regression in `colocated_gamesocket_test.go`. Real fix is client-side (out of router scope). **BLOCKED on kit:** is this your symptom (two LOCAL instances, different lobbies)? If not, need the exact repro → Punch list. |
| LOBBY-2 | lobby | TODO | In-game browser UI / switching UX — `sf-server-browser/` (`ServerBrowserScreens.cs`, `LobbyOverlay.cs`). Verify: **D** (single-client overlay screenshot) or **E**. |
| AC-1 | anticheat | VERIFIED | **Live-tested 2026-06-22** (Option B oracle, 500pps→port 1437): rate-guard fires at the documented threshold (`exceeded total rate (241/s)` then `(497/s)`) and is **observe-only** ("not dropping") with `SF_ANTICHEAT_ENFORCE` unset — exactly as designed. Thresholds All=240/PlayerUpd=120/Damage=30/Object=480 (SFHeadlessHost.cs:3386-3389). Evidence: `loop-evidence/AC-1/`. No change. Self-check: an early `rx=141/s` heartbeat looked like packet loss but was a mid-ramp sample — guard confirmed 497/s received → DISPROVEN. |
| OPEN-1 | dmg | NEEDS-LIVE | **Damage path is code-correct.** `ValidateDamagePacket` allows env (`attackerIdx==255`, :2920) and skips its distance check for it (:2950); self-attacker would also pass (dist 0). Killing-blow `PktPlayerTookDamage` is echoed to the victim **incl. sender** (:2615-2617) so they `Die()`; `PktPlayerFallOut` → `ScheduleRoundAdvanceOnDeath` (:2649). Client reconciliation does NOT touch the local player (shift-correction disabled, SFClientRecon.cs:2215-2225) → cannot rescue them from the void. No code bug → live confirm only. |
| OPEN-2 | dmg | NEEDS-LIVE | Lava — same code path + verdict as OPEN-1 (env/self damage validates; relay echoes the killing blow; no reconciliation interference). No code bug → live confirm. |
| OPEN-3 | boxes/dmg | TODO | Can't hit guns out of players' hands. Trace `PktPlayerForceAddedAndBlock` / damage-type filtering in `SfDispatch`. Verify: **C** then **E**. |
| OPEN-4 | boxes | TODO | Chains randomly break. Likely fixed by P0-11 revert (`4affabc`) — verify it stays fixed. Verify: **C** then **E**. |
| OPEN-5 | boxes | TODO | Ice randomly breaks. Likely fixed by dynamic-NSO revert. Verify: **C** then **E**. |
| OPEN-6 | boxes | TODO | Boxes disappear randomly. Same family as OPEN-5. Verify: **C** then **E**. |

## DISCOVERY queue — bugs/leads the loop found by HUNTING (not from the backlog)
Per the standing directive. Mark confidence; promote confirmed ones to the main queue; mark false alarms DISPROVEN.
| ID | Area | Confidence | Lead |
|----|------|-----------|------|
| DISC-1 | anticheat | LOW (eval, partly by-design) | Rate-guard keys on `IP:port` (per-endpoint), not per-IP (SFHeadlessHost.cs:3398). A source-port-randomizing flood from ONE IP makes a new `RateGuard` per port → fills the 256 cap → emergency-prune idle → if still full, NEW sources are fail-closed dropped (3402-3423). The cap is an *intentional* anti-OOM mitigation (comment 3381-3383), but the residual is: a legit NEW client joining *during* such a flood could be dropped, and per-endpoint keying means the per-source rate limits never trip for the spoofer. Evaluate per-IP aggregation / connection-level gating. Found while verifying AC-1. |
| DISC-2 | netcode | DISPROVEN | Hypothesis: server-auth-rig snapshot reconciliation snaps the LOCAL player back out of the void → "can't die" (OPEN-1/2). DISPROVEN from code: local-player shift-correction is DISABLED (SFClientRecon.cs:2215-2225) — local prediction owns the player; only REMOTE slots are ever lerped, opt-in via `SFCLIENTRECON_SMOOTH_REMOTE`. No void-rescue path exists. |
| DISC-X | — | — | (next hunt findings append here) |

## Punch list for kit — live 2-player tests only kit can run
- **⚠ DECISION (unblocks the loop): how should it verify runtime-gated bugs?** Three top items (P0-24, LOBBY-1, P0-23) turned out code-complete; the rest (boxes, anti-cheat, OPEN-1..6) need a *running oracle* to verify. Pick one — **(A)** kit drops in live logs from a normal session (a `[BOX-DIAG]` line, the oracle heartbeat, any `[anticheat]` lines); or **(B)** kit OKs the loop running the headless oracle itself for level-C checks (process-risky — needs a safe port + confirmation no live/local game is using it). Until then the loop sticks to code-only investigation (OPEN-1..6 damage paths), which needs neither.
- **P0-23 — capture one `[BOX-DIAG]` line** during a live match (logs every 5s after match start). One line resolves it: does `floor@crate` say `NONE`? does `void(y<-30)` climb? does the crate's layer collide with the floor's?
- **OPEN-1/2 — confirm void + lava death in a live 2p match.** Walk into the killbox / stand in lava → expect death (~0.5s) + round advance. All server+client code paths check out (validator allows env/self, killing-blow echoes to the victim, fallout→round-advance, reconciliation doesn't touch the local player). If it still FAILS live, grab the oracle log around the death — the bug would then be in *emission* (the patched DLL not sending the damage/fallout in the routed setup), which I can't see from the repo.
- **LOBBY-1 — confirm the switching symptom.** Are you testing two LOCAL SF instances (same IP) in DIFFERENT lobbies? If so, "switching doesn't work right" is the *documented* per-IP game-socket limit (notes/ROUTER.md) — workaround: put both in the SAME lobby, or test from distinct IPs; the real fix is client-side (game socket SELECTs, or SELECT registers its port). If the symptom is something else (same lobby, or the in-game browser UI itself), give the exact repro so I can target LOBBY-2.
- **P0-24 — confirm rigs respawn rounds 2+.** 2 clients, start a match, score a kill so the round advances. After the new map loads, watch the oracle heartbeat: `rigs=` should return to the connected-client count (not stay `rigs=0`) and boxes should be pushable again. If `rigs=0 matchStarted=True` persists past round 1, P0-24 is real after all — reopen. (Static trace says the re-arm is intact; this only confirms it at runtime.)

## LOG — append-only, newest at the bottom
### Iteration 1 — BOX-2 / P0-24 — VERDICT: STALE (no code change)  [VERIFIED via static cross-file trace; runtime → kit]
- **Picked because:** backlog flagged it high-value + "tractable ~5-line fix" + headless-verifiable.
- **Hypothesis (backlog):** `AdvanceRound` clears the auth rig but never re-arms `_authSpawnAt`/`_nsoInventoryDone`, so `SpawnAuthoritativePlayersForAllClients` never re-fires → `rigs=0` rounds 2+.
- **Skeptic pass (a) — disproof found:** the re-arm DOES exist. `AdvanceRound`(3742)→`ResetOracleStateForRoundAdvance`(3783) resets `_authSpawnDone`,`_authSpawnAt`,`_nsoInventoryDone`,`_nsoInventoryAt` + clears rigs; then `ScheduleOracleReloadCurrentMap`(SfMapTerrainHost.cs:1083) sets `_oracleStartMatchAt=now+0.5` AND `_oracleStartMatchFired=false`(1091) — the precise guard at SFHeadlessHost.cs:2091 I suspected might stick. Cascade re-fires end-to-end (2091→2106→2116→2127→`SpawnAuthoritativePlayersForAllClients`).
- **Skeptic pass (c) — DOWNS of "fixing" it:** adding any extra spawn trigger would double-spawn authoritative rigs (2 `NetworkPlayer`s/slot) — a real regression. Correct action = NO CHANGE.
- **Evidence:** SFHeadlessHost.cs:2091-2132, 3742-3805; SfMapTerrainHost.cs:1083-1100. Fixed by `472f447` ("Fix Oracle multiplayer round flow", 2026-05-24) — same day the backlog row was written, hence the staleness.
- **Confidence P0-24 is stale:** ~88%. The cited mechanism is definitively false; residual ~12% = runtime timing, or `SpawnAuthoritativePlayersForAllClients` early-returning for some *other* reason — covered by the kit punch-list test.
- **Follow-up:** `notes/BUGS_BACKLOG.md` P0-24 row should move to "fixed" once kit confirms at runtime.

### Iteration 2 (start) — LOBBY-1 — baseline established  [VERIFIED via A: go test]
- **Picked because:** verifiable at level A (router has go tests) → cleanest place to start the lobby work.
- **Baseline:** `go -C sf-router test ./...` → `ok` (router/select/registry all green, 1.18s).
- **Implication:** kit's "switching doesn't work right" is NOT in the *tested* router logic — it's either an untested router transition or the UI layer (`serve-lobbies.py` / `sf-server-browser` → LOBBY-2).
- **Skeptic note:** "tests green" ≠ "no bug" — green only proves *covered* paths. Do NOT close LOBBY-1; treat green as "narrow the search," not "case solved."
- **Next (cron fire):** read `select.go`/`registry.go` for untested switching paths; Playwright-screenshot `serve-lobbies.py`; reproduce the exact symptom as a failing test before fixing.

### Iteration 3 — LOBBY-1 (cont.) — confirmed a DOCUMENTED limit, no fix  [VERIFIED via A: reproducing test]
- **Investigated:** SELECT/rebind path (router.go:263-337) + select.go + the existing co-located test.
- **Found (skeptic a):** the non-SELECTing game socket rides a single per-IP `ipBind` slot (router.go:295, overwritten per SELECT) → two same-IP players in different lobbies hijack the first's game socket. `TestCoLocatedBoundFlowSurvivesOtherSelect` misses it (there both clients SELECT, so both epBind'd).
- **Skeptic b — checked notes/ (HARD RULE) BEFORE changing:** it's the ALREADY-DOCUMENTED accepted limit (notes/ROUTER.md:93-96, ROUTER_LIVE_TEST.md:60, MULTI_LOBBY_LIVE.md:134). Client (`SendSelectLobbyPacket`, SFClientRecon.cs:1871) SELECTs only from the recon socket, exactly as documented. NOT a new bug.
- **Verified:** wrote `colocated_gamesocket_test.go`; router log shows the game flow rebuilt A→B on the co-located SELECT. Then `t.Skip`'d it (asserts post-fix behavior; un-skip when a client-side fix lands) so the suite stays green.
- **Skeptic c — DOWNS of "fixing" in the router:** not cleanly possible — the router can't disambiguate two non-SELECTing game sockets on one IP from (IP,port) alone. A speculative router change would risk the working single-player + co-located-same-lobby paths. Real fix is client-side/protocol.
- **Confidence:** ~95% this is the per-IP limit; whether it's *kit's* symptom is unknown → BLOCKED on his repro (punch list).
- **Net:** +1 pending regression test, no behavior change; backend switching otherwise sound.

### Iteration 4 — BOX-1 / P0-23 — code-complete + instrumented, runtime-gated  [no change; needs live BOX-DIAG]
- **Picked because:** top UNBLOCKED item; backlog calls it the real cause of box problems.
- **Skeptic b (checked code before changing):** backlog's "diagnostic staged / likely SyncTransforms" is STALE. Already committed: PostMapLoad collider-refresh (SfMapTerrainHost.cs:668), opt-in safety floor (695), NSO fall-guard (SFHeadlessHost.cs:4736), and a comprehensive `[BOX-DIAG]` (6747) that raycasts the floor @center/@crate (obj+layer+trigger) and dumps crate physics.
- **Skeptic c — DOWNS of each candidate change, all rejected:** (1) default the safety floor ON → flat 2000×2000 Y=0 collider breaks pit/multi-level maps; (2) speculative static-collider re-registration → unverifiable without runtime, risks side effects; (3) extend BOX-DIAG → already reports floor layer + crate physics, redundant.
- **Verdict:** no safe high-confidence code change. Runtime-gated — one live `[BOX-DIAG]` line resolves it. Confidence code is complete ~85%.
- **META (3rd in a row):** P0-24, LOBBY-1, P0-23 are all code-complete/stale + runtime-gated; the autonomous code-only surface (router) is exhausted (LOBBY-1 blocked). To keep finding *fixable* bugs the loop needs (A) kit's live logs or (B) go-ahead to run the headless oracle. Code-only damage-path investigation (OPEN-1..6) is still progressable → next iteration.

### Iteration 5 — Option B AUTHORIZED + safety-verified  [smoke test passed; self-match footgun fixed]
- kit chose **B** (loop may run the local headless oracle) and is away → safety for unattended runs is the priority.
- **Verified live:** launched an isolated oracle (bridge 1441 / port 1437 / own prefix+logfile); it booted under Proton (Unity log active), then torn down cleanly — `loop144[1]`=0, game-exe=0, proton=0, log stale. No collateral (only ever killed by the 1441 marker / explicit PIDs; the unrelated SSH job ended on its own).
- **Footgun caught + fixed:** a plain `pkill -f 'sf-oracle-unity-1441'` matched the iteration's OWN shell → killed it mid-cleanup (exit 144) → orphan risk. Procedure now mandates `setsid`+PGID kill, bracket-trick patterns, and a `timeout 200` backstop (see the Option-B section).
- No queue bug this iteration; hardened the Option-B procedure so unattended cron runs cannot accumulate processes or touch kit's game. Next code-progressable item: OPEN-1..6 damage paths.

### Iteration 6 — AC-1 — VERIFIED live via Option B + first HUNT finding  [VERIFIED via C: live oracle + stress test]
- **Picked because:** cleanest Option-B target (purpose-built stress test, no 2nd client needed); first real use of the now-authorized headless-oracle capability.
- **Ran:** launched isolated oracle (bridge 1441/port 1437, hardened procedure), booted clean (14+ heartbeats), fired `stress-test-anticheat.py --pps 500 --duration 6 --port 1437`, grepped the plugin log, then **cleaned up (0 procs after)**.
- **Result:** rate-guard fired at threshold — `[anticheat] exceeded total rate (241/s)` then `(497/s) ... Observation only; not dropping`. Calibrated + observe-only as designed. AC-1 = VERIFIED, no change. Evidence in `loop-evidence/AC-1/`.
- **Skeptic self-check (DISPROVEN a false find):** an early heartbeat showed `rx=141/s` for a 500pps send → looked like ~70% packet loss. The fuller log (`497/s`, violation #2497) shows the oracle received the full rate; the 141 was a mid-ramp sample. NOT a bug — did not ship it.
- **HUNT (per the new standing directive):** logged **DISC-1** — AC rate-guard keys per-`IP:port`, so a source-port-randomizing flood evades per-source limits + can fail-closed-drop legit new clients. Low confidence / partly by-design; flagged for evaluation, not "fixed."
- **Process safety:** Option-B procedure exercised end-to-end (orphan-guard → setsid/PGID launch → drive → PGID cleanup → verify 0). Clean.

### Iteration 7 — OPEN-1/2 (void/lava death) — code-correct, no bug; DISC-2 hunt DISPROVEN  [code trace; live confirm pending]
- **Picked:** top code-investigable item; core mechanic ("can't die to void/lava").
- **Traced the full damage path:** `ValidateDamagePacket` (:2905) allows env `attackerIdx==255` (:2920) + skips distance for it (:2950); self-attacker passes too (dist 0). Dispatch echoes killing-blow `PktPlayerTookDamage` to the victim **incl. sender** (:2615-2617 → `Die()`); `PktPlayerFallOut` → `ScheduleRoundAdvanceOnDeath` (:2649). **No code bug** blocks void/lava death.
- **HUNT → DISC-2 → DISPROVEN:** hypothesized the server-auth-rig snapshot snaps the local player out of the void. Checked SFClientRecon `ApplySnapshot` — local shift-correction is DISABLED (:2215-2225); only remote slots lerped (opt-in). No void-rescue; hypothesis killed from code, not chased into runtime.
- **Bonus (same dispatch read):** OPEN-3 force-relay looks correct too — `PktPlayerForceAddedAndBlock` relays to others incl. the victim (:2634-2647), so a punched gun should drop. Noted, not fully traced.
- **Verdict:** OPEN-1/2 = no code bug; likely stale (pre-revert / stale-deploy reports). Live confirm is the only step left → punch list. Confidence no-code-bug ~80%; residual = patched DLL not emitting in the routed setup (runtime-only, not in repo).
- No code change this iteration.

