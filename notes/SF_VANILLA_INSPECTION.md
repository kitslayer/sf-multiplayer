# SF vanilla inspection setup (Unity Explorer)

Two parallel vanilla SF instances with Unity Explorer for runtime introspection. Use this to establish "what does stock SF actually do" ground truth when our oracle code's behavior is suspect.

## Install location

| Component | Path |
|---|---|
| Instance 1 (host) | `~/sf-mirror-local/` |
| Instance 2 (joiner) | `~/sf-mirror-local-p2/` (hard-linked clone of p1, distinct Goldberg account) |
| Wineprefix p1 | `~/sf-vanilla-p1/` (Proton-managed) |
| Wineprefix p2 | `~/sf-vanilla-p2/` |
| Unity Explorer | `<install>/BepInEx/plugins/sinai-dev-UnityExplorer/` (4.9.0 BIE5.Mono) |
| Goldberg config | `<install>/StickFight_Data/Plugins/steam_settings/configs.user.ini` |

Both installs use stock `Assembly-CSharp.dll` (md5 `b215e152afd2c4f3fa4271d780834e9a`). All SF mod plugins are parked (`*.dll.parked` or `.disabled`). Only `sinai-dev-UnityExplorer/` is active.

Goldberg accounts:
- p1: `sf_test_local2` / steamid `76561199999999992`
- p2: `sf_test_local2_p2` / steamid `76561199999999993`

These are distinct so the two instances can see each other in a LAN match without identity collision.

## Launch

```bash
PROTON="$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export WINEDLLOVERRIDES="winhttp=n,b"
export WINEDEBUG=-all

# Instance 1
STEAM_COMPAT_DATA_PATH=$HOME/sf-vanilla-p1 \
  "$PROTON" run /home/miles/sf-mirror-local/StickFight.exe \
  -screen-width 1280 -screen-height 720 -screen-fullscreen 0 &

# Instance 2 (after p1 init settles)
sleep 2
STEAM_COMPAT_DATA_PATH=$HOME/sf-vanilla-p2 \
  "$PROTON" run /home/miles/sf-mirror-local-p2/StickFight.exe \
  -screen-width 1280 -screen-height 720 -screen-fullscreen 0 &
```

Both windows pop in ~15-30s. Different SF avatars per instance since Goldberg gives them distinct identities.

## Unity Explorer basics

**Hotkey:** F7 (default). If conflicts, rebind via `~/sf-vanilla-p1/pfx/drive_c/users/steamuser/AppData/Roaming/UnityExplorer/`.

UE main toolbar tabs (top of overlay):
- **Object Explorer** — find any GameObject by name / hierarchy
- **Inspector** — read/write component fields on selected GameObject
- **C# Console** — REPL for arbitrary code
- **Mouse Inspect** — click on any visible game object to jump to it
- **Hook Manager** — log every call to a method (binary-instrument any function)
- **Clipboard** / **Options** / etc.

### Finding a specific object

Object Explorer → search box (top right) → type partial name like "Crate" or "Destructible" or "Controller" → hit Enter. Results filter live.

Single-click a row to open it in Inspector.

### Inspecting a component

In the Inspector panel, the Components list (right side) shows every MonoBehaviour / Unity built-in attached. Click any to open that component in its own tab.

Field list shows ALL accessible fields (Properties + Fields + Methods, color-coded). Filter via the "Filter names" box. Toggle scope between All / Instance / Static — **static fields** like `NetworkSyncableObject.mHasControl` only show under `Static`.

For **private fields** (Mono 2.0): they appear too, marked with subtle color. UE uses reflection internally so `private` doesn't hide things.

### C# Console (REPL)

Multi-line C# 7-ish. Common Unity namespaces auto-imported. Use for bulk field dumps:

```csharp
var sb = new System.Text.StringBuilder();
var nsos = UnityEngine.Object.FindObjectsOfType<NetworkSyncableObject>();
foreach (var nso in nsos) {
    var rb = nso.GetComponent<UnityEngine.Rigidbody>();
    if (rb != null)
        sb.AppendLine(nso.name + " isKin=" + rb.isKinematic + " mass=" + rb.mass);
}
System.IO.File.WriteAllText(@"Z:\tmp\dump.txt", sb.ToString());
return "wrote " + nsos.Length + " entries to /tmp/dump.txt";
```

The Wine path `Z:\tmp\` maps to `/tmp/` on Linux. You can dump from UE and read from the laptop terminal in parallel.

A ready-made crate-state dump snippet lives at `/tmp/ue-dump-snippet.cs` after sessions. Re-runnable any time mid-match — overwrites the dump file.

## Common inspection targets

### Crate (pushable physics)

1. Object Explorer → search "Crate"
2. Components to inspect:
   - **Rigidbody** — `isKinematic`, `interpolation`, `mass`, `useGravity`, `collisionDetectionMode`
   - **NetworkSyncableObject** — `m_Index` (ushort), `mIsListening` (bool, private), `mHasControl` (static!)
   - **RigidBodyIndexHolder** — `Index` (byte), `mInited` (bool, private)
   - **BoxCollider.sharedMaterial** — friction + bounciness PhysicMaterial

Two crate variants exist:
- **Simple** (`Crate2 (6)` in Desert5): Transform, MeshFilter, BoxCollider, MeshRenderer, Rigidbody, NetworkSyncableObject, RigidBodyIndexHolder.
- **Effect** (`Crate (3)`): adds AudioSource, RandomPitch, SoundPan, ShakeOnImpact, IgnorePlayerWhenOffScreen, LevelEditor.NetworkComponentTAG.

### Chain destructible (ice block, hanging chain)

1. Object Explorer → search "Chain" or "Ice" or "Destructible"
2. `DestructiblePiece.simpleDestruction` (should be False) + `eventDestruction` (should be True) — that pattern identifies chain-style as the oracle's `IsChainStyleDestructibleRoot` filter expects.

### Moving platform

1. Object Explorer → search "Ghost" / "Platform" / "Pillar"
2. Component is a subclass of `MapInfoSyncableBase` (one of `GhostPlatform`, `MoveAlongPathUsingForce`, `PillarHandler`).
3. `m_StartPos` (Vector2) — quantized key the oracle uses for mapSync.
4. `m_NetworkControl` (bool) — vanilla=true on host only, oracle forces true.
5. `GetData()` / `SetData(byte[])` methods — the mapState payload our v26.6 snapshot relays.

### Static game state

Open Inspector on the type (not an instance):
- `MultiplayerManager.IsServer` (static field `mIsServer`)
- `MatchmakingHandler.IsNetworkMatch` (static field `mIsNetworkMatch`)
- `NetworkSyncableObject.mHasControl` (static)
- `GameManager.Instance` (property → singleton instance with `inFight`, `currentMapInfo`, `randomWeaponCounter` etc.)

These let you snapshot the global match state in one go.

## Important vanilla-SF behaviors gotchas

- **`ChatManager.Awake` disables itself if `!MatchmakingHandler.IsNetworkMatch`.** Solo vanilla = no chat UI. /start, /next, /map and other commands can't be typed in solo. Use UE's C# Console REPL to call game methods directly.
- **Many fields are private.** `NetworkSyncableObject.mIsListening`, `mHasControl`, `m_Index` are all private. UE shows them by default; AccessTools/reflection-based plugin code must use `BindingFlags.NonPublic | BindingFlags.Instance` (or `Static`).
- **The patched and stock Assembly-CSharp.dll differ in `-address`/`-port` CLI parsing only** (per [`reference_patched_dll`](../../.claude/projects/-home-miles-sf-multiplayer/memory/reference_patched_dll.md)). The patched DLL falls through to vanilla behavior when no `-address` is passed. Either is valid for vanilla testing as long as no `-address` flag is set.

## What this setup is for

- Establishing ground-truth values for any field/component before claiming the oracle's behavior is "the same as vanilla."
- Cross-checking what Assembly-CSharp components exist that the oracle code never references.
- Debugging visual symptoms by attaching to runtime state vs. inferring from code.

Bug investigations that have used this setup: [`bug-investigations/2026-05-24_vanilla-ground-truth.md`](bug-investigations/2026-05-24_vanilla-ground-truth.md), [`bug-investigations/2026-05-24_missing-vanilla-mechanisms.md`](bug-investigations/2026-05-24_missing-vanilla-mechanisms.md).
