# 2026-05-31 — Oracle crash-loop: `MissingMethodException: Monitor.Enter` every frame

## Symptom
The live oracle (.115, ALKA's box-fix build) appeared "down" — clients couldn't
join. Plugin log flooded with, every frame:
```
[Error :Unity Log] MissingMethodException: Method not found: 'System.Threading.Monitor.Enter'.
Stack trace:            <-- empty
```
Heartbeat stalled (~tick 11 then the fault flood took over). A `systemctl
restart` did NOT fix it — so it's a code/binary issue, not transient state.

## Root cause
A **stale `SFClientRecon.dll` (a CLIENT plugin) was deployed in the ORACLE's**
`BepInEx/plugins/` dir (md5 `94cb62a2…`, mtime 05-29). That build was compiled
with C# `lock (_snapLock) { … }`, which Roslyn lowers to the **2-arg**
`System.Threading.Monitor.Enter(object, ref bool)` — an overload that **does not
exist in Unity 5.6's Mono 2.0.50727** (only the 1-arg `Monitor.Enter(object)`
does). When that method JITs at runtime → `MissingMethodException`. The empty
stack is the tell-tale of a faulting JIT'd/Harmony-DMD method.

Confirmed by IL inspection (`ilspycmd -il`):
- `SFHeadlessHost.dll` (d0955185): **no** `Monitor::Enter` — clean.
- `SFBoxFix.dll` (c3e569f7): **no** `Monitor::Enter` — clean.
- `SFClientRecon.dll` (94cb62a2): `call void Monitor::Enter(object, bool&)` at
  two sites (`lock (_snapLock)`, decompiled L1027/L1050) — **the culprit.**

Two compounding mistakes:
1. A **client** plugin (`SFClientRecon`) has no role on the headless server — it
   even logs "does nothing on oracle. Bye" — yet was present and still loaded +
   ticked enough to JIT its lock path and fault per-frame.
2. That deployed DLL was a **divergent/older build**: the committed `main`
   source uses the *explicit 1-arg* `Monitor.Enter(obj)` + try/finally (the
   documented Mono-2.0 fix, commit 4ba94ed), so it was never rebuilt/redeployed
   from current source.

Why it looked healthy earlier in the day then broke: the lock path only JITs
once its method first runs; once it does, it faults every subsequent frame.

## Fix (applied)
Moved the stray `SFClientRecon.dll` out of the oracle's plugins dir to
`/home/miles/sf-oracle/stray-plugins.off/` and restarted. Result: **0
Monitor.Enter faults, heartbeat advancing.** The oracle only needs
`SFHeadlessHost.dll` (+ `SFBoxFix.dll`).

Verified our shipping client DLLs are clean (IL: 0 two-arg `Monitor.Enter`;
they use the explicit 1-arg form): `dist/SFClientRecon.dll` (66bda748),
`dist/SFServerBrowser.dll`.

## Recurrence risk / follow-up
- A deploy script (`setup-all.sh` / ALKA's `deploy-physics-fix.ps1`) likely
  copies *all* plugin DLLs — including `SFClientRecon` — into the oracle
  install. **The oracle deploy must exclude client plugins** (`SFClientRecon`,
  `SFServerBrowser`). Worth a guard in the deploy tooling.
- Project rule reaffirmed: **never C# `lock{}`** in any plugin (client or host)
  on this Mono 2.0 target — always `Monitor.Enter(obj)` + try/finally/`Exit`.
  ALKA's client build violated this; flag to ALKA to rebuild from `main` source.
