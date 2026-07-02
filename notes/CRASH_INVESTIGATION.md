# Crash investigation — oracle native access violation

Started 2026-05-23 night. Hard fault inside `StickFight.exe` while running under Proton/Wine. **7 distinct crash dumps in `/home/miles/sf-mirror-local/2026-05-23_*/` from this single day's testing.**

## ⚠️ Update 2026-06-11 — it's PERIODIC, and there's a new clue + a mitigation

Two findings from the 2026-06-10 review change the picture:

1. **The crash is time-periodic, not gameplay-triggered.** On the live `.115` oracle it has fired **once per day at ~24h02m of process uptime** (22 occurrences logged; failure clock drifts +2m33s/day — Jun 1 17:58 → Jun 10 18:21, exactly tracking the restart→restart interval). Same `0x0057ed26` signature every time. A bug that fires on a fixed *uptime* clock regardless of player activity points at an **accumulating timer/counter**, not an event race.
2. **`0x7f800004` is the float +Infinity bit pattern.** The faulting write is to `0x7f800004`; `0x7f800000` is exactly the IEEE-754 single-precision `+Inf` encoding. The instruction is `lock xadd [eax], ecx` with `EAX=0x7f800004` — i.e. a value that *should* be an object pointer is instead `(+Inf bits) + 4`. Working hypothesis: a `float` accumulator (a timer, an interpolation `t`, a counter) reaches `+Inf` after ~24h, gets reinterpreted/truncated into a pointer, and the atomic refcount increment on that garbage address faults. Next dump analysis should hunt for an un-reset `float` that grows monotonically with wall-clock uptime.
3. **New failure MODE observed 2026-06-10:** after one daily crash the process **wedged instead of exiting** — alive 7h with CPU frozen, UDP port bound but deaf — so `Restart=on-failure` never fired and every monitor (systemd, `/healthz`, sf-monitor) reported it healthy while nobody could join.

**Mitigations now deployed (`deploy/`):**
- `sf-oracle.service.d/restart-daily.conf` — `RuntimeMaxSec=82800` (+10min jitter) + `Restart=always`: a clean restart ~1h inside the crash-free window turns the daily hard-crash into a ~25s blip.
- `sf-oracle-watchdog.{sh,service,timer}` — every 2 min, UDP-Pings the oracle (via `healthcheck.py`) and restarts the unit if it's `active` but deaf past a 120s warm-up. This is what catches the wedge that `Restart=`/`RuntimeMaxSec` cannot (they need the process to exit).
- `serve-lobbies.py` `/healthz` now does a real UDP probe and reports `lobbiesResponsive` + `status:"degraded"` (503) when a lobby is alive-but-deaf.

These contain the symptom; the root cause (the +Inf accumulator) is still open. Fresh `crash.dmp` + `error.log` land daily in `~/sf-oracle/install/2026-*/` on `.115` — analyze one with the float-timer hypothesis.

## Update 2026-07-01 — a confirmed +Inf source in this codebase, and a correction to the "accumulator" framing

Prompted by the SF mod [z7572/NaNFixer](https://github.com/z7572/NaNFixer), two refinements to hypothesis (2) above:

1. **There is a *verified* +Inf-manufacturing site in this exact codebase.** `NetworkSyncableObject.SyncObjectState` computes `m_PositionSpeed = distance / m_TimeBetweenPackages` (`:431`) and `m_RotationSpeed = angle / m_TimeBetweenPackages` (`:433`), where `m_TimeBetweenPackages = Time.time - m_TimeOfLastPackage` (`:427`). When the denominator → 0 (two updates for one object in the same frame), both fields become **float +Inf**. NaNFixer independently reverse-engineered this and it matches our decompile exactly. So the +Inf is no longer purely hypothetical — we have a concrete mechanism that writes +Inf into persistent per-object float fields.

2. **Correction: the +Inf cannot come from *linear* accumulation.** float max ≈ 3.4e38; adding a ~16 ms frame-time per frame reaches only ~86,400 after 24 h (~5.2 M frames) — you'd need ~1e31 years to sum to +Inf. So "a float accumulator reaches +Inf after ~24 h" is not reachable by *addition*. A float +Inf must come from a **division by (near-)zero** or geometric growth — exactly the `x / Δt` shape above. This redirects the hunt: stop looking for a slowly-growing timer; look for a division whose denominator is a `Time.time` difference or a vector magnitude/normalize that can hit ~0.

**Aggravating factor (not a clean 24 h explainer):** `Time.time` is float32, so its ULP grows with magnitude (≈7.8 ms at 24 h; a full 60 fps frame-gap only collapses to exactly 0 around ~3 days of uptime). Same-frame bursts are the uptime-*independent* trigger; precision decay just makes near-coincident updates round to 0 more readily as uptime climbs. It plausibly *worsens* the odds over a long-running process but does not by itself pin the ~24 h period.

**Caveat — this specific method is listener-side.** `SyncObjectState` runs only when `!mHasControl`. Our host forces static `mHasControl = true` (so `LerpLocalDummy` is gated off at `:239`), which means the host likely does **not** run `SyncObjectState` for its own objects — so this exact method is probably not the host's crash site. The transferable lead is the *class* of bug: audit **host-side** float divisions whose denominator is (a) a `Time.time` delta or (b) a vector magnitude/normalize that can be ~0 — candidates: host send-side timing (`SendNewObjectStatePackage`/`TickSyncPos`), `SFBoxFix`'s server-auth Rigidbody forces, and the snapshot serializer.

**Next-dump actions:** (a) enumerate those host-side divisions and add a `Finite()`/back-date guard at each (cheap; mirrors the client guard just shipped in `sf-client-recon/SfNsoNaNGuard.cs`); (b) in the next `crash.dmp`, look for float fields holding `0x7f800000` (+Inf) in live NSO/box instances, and check whether an uptime-derived counter is being used to index a structure near the faulting `[eax]`.

(Client side, the fix is already in — `sf-client-recon/SfNsoNaNGuard.cs`. It does **not** touch the host; the host track stays open.)

## TL;DR

The oracle is crashing **deterministically at the same x86 instruction address** during gameplay. Same bytes, same access violation, 5 separate crash dirs match perfectly. It's not a race; it's a code path. **Cause unknown without symbols.** Until we know more, do NOT speculate-patch — every revert and partial-fix this session has caused other regressions.

## The crashes

```
Today's oracle crashes (StickFight.exe access violation 0xc0000005):
  13:14:40  → EIP 0x0057ed26  bytes: f0 0f c1 08 8b 4e 34 89 4d f8 85 c9 74 2a 8b 51
  14:36:22  → epilogue bytes (different — likely same fault, caught mid-unwind)
  15:24:06  → EIP 0x0057ed26  bytes: f0 0f c1 08 8b 4e 34 89 4d f8 85 c9 74 2a 8b 51
  15:30:48  → epilogue bytes (different)
  15:59:02  → EIP 0x0057ed26  bytes: f0 0f c1 08 8b 4e 34 89 4d f8 85 c9 74 2a 8b 51
  17:41:42  → EIP 0x0057ed26  bytes: f0 0f c1 08 8b 4e 34 89 4d f8 85 c9 74 2a 8b 51
  20:09:28  → EIP 0x0057ed26  bytes: f0 0f c1 08 8b 4e 34 89 4d f8 85 c9 74 2a 8b 51
```

Five out of seven crashes hit the exact same instruction with identical surrounding bytes. The other two have function-epilogue bytes (`83 ec 04 8d 65 f4 59 ...`) — those look like the same fault propagating up through stack unwind.

## Decoded faulting instruction

```
f0 0f c1 08              lock xadd dword ptr [eax], ecx
8b 4e 34                 mov  ecx, [esi+0x34]
89 4d f8                 mov  [ebp-8], ecx
85 c9                    test ecx, ecx
74 2a                    jz   +0x2a              (skip if null)
8b 51 ??                 mov  edx, [ecx+??]      (deref next-pointer)
```

That's a "atomic-increment-refcount-then-traverse-next-pointer" pattern. Classic for **linked-list / sync-collection iteration with refcount safety**. Mono uses this for ThreadLocalStorage, sync block tables, weak references, GC handles. Native unmanaged C++ uses it for `std::shared_ptr` style refcount inc.

## Faulting memory location

```
Write to location 7f800004 caused an access violation.
EAX: 0x7f800004
ESI: 0x20ab7160  (probably the actual object)
```

`0x7f800000` is the **last MB of 32-bit user space** in Wine — right at the user/kernel boundary. Writing there always faults. This is a sentinel-pointer pattern: when something is unmapped, freed, or never-initialized, Wine sometimes leaves a high address as a "do not touch" marker rather than NULL.

So EAX is bogus: the code computed a refcount address via `ESI + 0x34` (`mov ecx, [esi+0x34]`) but EAX was loaded from somewhere else BEFORE this instruction — possibly the previous instruction loaded EAX from a different field of ESI that's been corrupted/freed.

## What I know it's NOT

- Not BepInEx itself (BepInEx 5.4 is widely deployed; our 5.4.23.5 is the same as everyone uses)
- Not the patched Assembly-CSharp.dll (md5 stable across runs; same as user's working install on .115)
- Not my new code's exception handlers (the addresses are in stickfight.exe NATIVE, not Mono-managed Harmony stubs)
- Not memory exhaustion (43% memory in use at crash time, 0 paging file pressure)
- Not GC stress (no GC log entries, fault is in non-GC code)

## What I suspect (top 3, no proof)

### Hypothesis A: NSO event-channel iteration

`f0 0f c1 08` matches the pattern Unity uses for NSO `ListenForEventPackages` / `ListenForPackages` iteration internals. With our heavy NSO traffic + the `m_DontSyncForSeconds` interaction we patched, a syncable object could be freed (via `OnDestroy` setting `mIsListening=false`) while another thread is mid-iteration on its event-channel. The refcount inc to keep it alive races with the destroy.

**Verifiable**: would correlate with high destruction-event traffic just before each crash. We saw the live oracle log show a stress-break path for chains (P0-11 era) — the destruction filter is now drop-all but the SERVER's own DestructiblePiece collision still fires server-side. That fires SF's internal `OnDestructibleDestroyed` → object pool returns → race window.

### Hypothesis B: InvokeMultiplayerManagerInitChain side-effect

We log NREs in `[P6.9] ReadyUp threw NRE` + `[P6.9] InitSyncedObjects threw NRE` after every match start. These exceptions are caught but leave SF's `mNetworkManager`/`mConnectedClients`/`mSpawnedWeapons` partially initialized. Later, SF's own native code (driven by Lidgren network thread or Unity main loop) dereferences a field expected to be a valid object but it's the sentinel pointer 0x7f800000.

**Verifiable**: crashes only happen AFTER `/start` was issued. If we never start a match, no crashes. (Not yet validated.)

### Hypothesis C: Concurrent ObjectUpdate write into a removed NSO

Our `SendBroadcastPrefix` runs on Unity main thread. SF's Lidgren receive thread processes inbound `ObjectUpdate`. If a client's ObjectUpdate arrives for an NSO that was just destroyed (via the destruction filter race scenario), the inbound handler tries to apply a position to a freed object. Most cases throw NRE; one specific case writes to a refcount field at 0x7f800004 because the NSO header has been replaced with the sentinel.

**Verifiable**: crashes coincide with ObjectUpdate-for-recently-destroyed-NSO. Need an NSO-destroy-timing trace to confirm.

## What's blocking diagnosis

1. **No symbols for StickFight.exe** — `SymGetSymFromAddr64, GetLastError: 'Success.'` means we can't resolve `0x0057ed26` to a function name. Would need a debug build of SF (Landfall doesn't ship one) or symbol-extract from a reverse-engineering toolchain.
2. **No Mono-managed callstack** — the crash is in native code, so the .NET-side stack that LED to that native call isn't in the dump.
3. **Crash dumps are minidumps, not full core** — Wine's `crash.dmp` is a Windows-style minidump. Can be loaded in WinDbg / dotPeek / Ghidra to disassemble around 0x0057ed26 + understand calling context.

## Next moves (in order of value vs effort)

1. **Disassemble the crash region around 0x0057ed26 in StickFight.exe using Ghidra/IDA** — would tell us what function this is. Cheap if you have the tools; expensive setup-time otherwise.
2. **Test Hypothesis B (no-/start = no crash)** — run the oracle for 30 min with NO clients ever connecting. If no crash, the trigger is match-time activity.
3. **Test Hypothesis A (destruction race)** — disable client-initiated destruction relay entirely for one run. If no crash, destruction events are the trigger.
4. **Patch a fault handler in our plugin** to log "we crashed in 0x0057ed26 at time T, recent activity was X" before the process dies. Use `AppDomain.UnhandledException` + a watchdog thread that captures the last 100 LogInfo lines.

## DO NOT do yet

- Don't add a `try-catch` to suppress this — it's native, not managed. Try-catch in C# won't catch it.
- Don't add a Harmony patch to the suspected SF function — we don't know which function it is.
- Don't revert recent changes hoping it fixes itself — the crash pattern was present BEFORE the recent feature work (the 13:14:40 crash predates Phase 6.19 work).

## Update 2026-05-23 night — disassembly identifies the function

`objdump -d -M intel` on StickFight.exe pinned the crash function:

```
Function starts at 0x0057ec60:
  57ec60: push ebp                          ; function prologue
  57ec61: mov  ebp,esp
  57ec63: sub  esp,0xb0                     ; 176 bytes of locals
  ...
  57ec92: call 0xc841d0                     ; some helper
  57ece1: call 0xaebb20                     ; ← bump allocator (~5KB slab)
  57ecef: call 0x57be40                     ; ← constructor (zeroes 3 fields, sets self-ptr)
  ...
  57ed14: mov  eax,[ebp+0x14+0x7c]          ; ← load arg4 -> [0x7c]   (pointer to sub-object)
  57ed1a: test eax,eax                      ;   null check
  57ed1c: je   0x57ed2a                     ;   skip if null
  57ed1e: add  eax,0x4                      ;   eax = sub-object + 0x4 (sync block / refcount slot)
  57ed21: mov  ecx,0x1                      ;   ecx = 1
  57ed26: lock xadd [eax],ecx               ; *** CRASH: atomic_inc(sync_block)
```

`add esi,0x94 ; push esi ; call 0x57ec60` from caller `0x55b8e2` shows arg4 is
`this + 0x94`, so the crash chain dereferences `this->[0x94 + 0x7c] = this->[0x110]`
then `+0x4`.

**Key context clue — the string at `0x12427a8` (referenced repeatedly near the
crash region) is `"// Dump is not supported for this joint"`.** This is
Unity/PhysX joint-debug code. The 6 callers of `0x57ec60` are all in the
0x55b8xx - 0x55fxxx range, consistent with a tight cluster of joint event
handlers (joint break, joint anchor sync, joint dump, etc.).

## What this strongly suggests

The crash is in **PhysX's joint-debug "dump" code path**, fired when a joint
is in an unrecoverable state (PhysX runs an assert → calls the dump → dump
chases a pointer chain → one of those pointers is `0x7f800000` → crash on the
sync-block refcount increment).

`0x7f800000` is the IEEE-754 bit pattern for **positive infinity** as float32.
That's not a coincidence — it strongly suggests a `Vector3` field's component
got reinterpreted as a pointer somewhere. Specifically: a joint's anchor
position or relative-orientation field went to `Inf`, was later interpreted
by another code path as a `void*` (perhaps via a union or unsafe cast in
PhysX's internal joint data layout), then dereferenced.

## Probable trigger (still uncertain)

The chain leading to `Inf` in a joint anchor is most likely:

1. Our auth rig (one per slot, multi-bone with ConfigurableJoints) is
   driven by `UpdateGhostRigPosition` — each PlayerUpdate teleports all
   the rig's rigidbodies by the same delta.
2. Across rounds (`BroadcastStartMatch` resets state but auth rigs persist
   via `_authSpawnDone` gate), the joint constraint solver may accumulate
   drift in joint anchors.
3. Eventually a joint anchor reaches `Inf` (constraint violation under
   continuous teleport-then-physics-tick).
4. PhysX detects → calls dump → dump's stale ref → crash.

Alternative: ragdoll spawn/teardown when a player dies. Stock SF creates +
destroys many joints during the death-ragdoll-respawn cycle. If our code
keeps a reference to a joint after SF destroyed it (via `_authSpawnDone =
true` blocking re-spawn), the next physics tick hits the stale joint.

## Less-certain but interesting

The 5 callers besides the one we traced are likely:
- joint break event handler
- joint anchor update
- joint motor target update
- joint validation pass
- joint serialization

All routes through `0x57ec60` which constructs a "joint snapshot for logging"
(matching the 5KB slab alloc + the dump string nearby). So crashes ALWAYS
happen during JOINT LOGGING — meaning PhysX's internal assert path. Whatever
caused the original assert is the upstream root cause.

## What I would test next (not patching yet — needs your call)

1. **Disable auth-rig spawn entirely** — set `_authSpawnDone = true` at boot
   so `SpawnAuthoritativePlayersForAllClients` never runs. If no crash for
   30 min, auth rigs are the trigger.
2. **Make auth rigs kinematic in ALL their rigidbodies** (currently mixed
   per `MakeRigKinematicMirror`). Kinematic joints don't run the constraint
   solver → no anchor drift → no PhysX assert → no dump crash.
3. **Add a per-frame NaN/Inf check on every rig's transforms** (in our
   Update hook). If any becomes Inf, log + reset to last known good
   position. Tells us EXACTLY when the corruption starts.

Option 3 is the most diagnostic. Options 1 and 2 are workarounds that
might mask the underlying issue but would let comp matches run without
crashes while we fix the root cause.

## Confidence

| Claim | Confidence |
|---|---|
| Crash is in PhysX joint-debug "dump" code | **High** (string reference + call pattern) |
| Root cause is a `void*` field reinterpreted from a `Vector3.x = Inf` | High |
| Our auth-rig multi-rigidbody MovePosition is involved | Medium |
| Specific fix without testing | **NOT confident yet** |

Per the goal directive, **no patches.** Documenting for tomorrow's session
to pick a test approach.

## Side note — log flood bug from commit `bce8bcc` (FIXED)

The per-lobby log listener used `lock (_lock)` which the C# compiler emits as `Monitor.Enter(obj, ref bool)` (2-arg overload added in .NET 4.0). SF's Mono 2.0 runtime doesn't have that overload. Every log event threw `MissingMethodException`, which BepInEx logged, which fired the listener again, which threw → infinite recursion → 400MB/oracle log floods in ~10 min.

**Fixed** by replacing `lock(_lock)` with a `[ThreadStatic] _reentryGuard` boolean. The guard prevents recursion even if `WriteLine` itself throws something else later.

This was MY bug, NOT the underlying native crash. The two crashes labeled "kernelbase.dll AccessViolation" (14:36:22 and 15:30:48) might be Wine cascading off the log flood saturating something, OR they might be the same StickFight.exe crash with the stack unwinding past the SF boundary. Inconclusive without symbols.
