# Live test checklist — post SFBoxFix v0.2.4 + SFHeadlessHost 0.3.9

Two players, oracle VPS `69.53.117.43:1337`. After deploy, confirm log:

```bash
grep -E 'SFBoxFix v0.2.4|SFHeadlessHost 0.3.9|MONO-FIX|BOX-DIAG' /tmp/sf-oracle-plugin-11337.log | tail -30
```

No sustained spam of `MethodInfo.op_Inequality` in `WriteInputsToRigs`.

## Session checks

| # | Test | Pass criteria |
|---|------|----------------|
| 1 | Death → next map | Feels &lt; ~3s from kill to playable next map (after delays tuned) |
| 2 | Round 2+ rigs | Heartbeat shows `rigs=1` (or N players) after second death/map |
| 3 | Preset ground weapons | Pickups visible on preset weapon maps after load / pre-combat |
| 4 | Boxes | No vanish within 2s; push feels better than pre-fix; crates do not explode each other |
| 5 | Stability | Server accepts new join after 10+ minutes; healthcheck OK |

## Log markers

- `[Open-B] Scheduled NSO inventory + auth rig respawn`
- `[P6.8] CheckForGroundWeapons` / `pre-combat`
- `[BOX-DIAG] nsos=… void=…`
- `[DEATH] Round advance gate cleared` / `Round advance scheduled`

## Deploy

```powershell
.\deploy-physics-fix.ps1 -DeployVps
# On VPS (interactive sudo):
sudo systemctl restart sf-oracle.service
```
