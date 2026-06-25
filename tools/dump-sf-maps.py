#!/usr/bin/env python3
"""
Offline Stick Fight map dumper using UnityPy.

Extracts per Landfall scene 1..124 (skipping 102=stats):
  - Static colliders (BoxCollider, SphereCollider, CapsuleCollider) via TypeTree
  - Player spawn points via MapInfo.spawnPoints[] (raw byte parse — SF's custom
    MonoBehaviours don't have embedded TypeTrees so UnityPy's high-level read
    fails; we parse the binary header + first array manually)
  - Weapon spawn points: GameObjects with WeaponPickUp components
  - Killbox volumes: GameObjects with KillingFloor / KIllAllOutOfRange components
  - Syncable objects (NetworkSyncableObject / DestructiblePiece): same approach

Output: one landfall-N.json per scene at $SF_LEVELDUMPER_OUT (default ./maps/).

Usage:
    /tmp/sf-unity-venv/bin/python tools/dump-sf-maps.py [--data DIR] [--out DIR] [-v]
"""

import argparse
import json
import os
import re
import struct
import sys
from collections import defaultdict

import UnityPy

# SF MonoScript path_ids in globalgamemanagers.assets (verified by inspection).
# These can change if the game is patched, but for v25 of SF they're stable.
SF_SCRIPT_NAMES = {
    "DestructiblePiece": 131,
    "WeaponPickUp": 366,
    "KIllAllOutOfRange": 394,
    "MapInfoSyncableBase": 622,
    "NetworkSyncableObject": 769,
    "KillingFloor": 788,
    "MapInfo": 944,
}
SF_SCRIPT_BY_PID = {pid: name for name, pid in SF_SCRIPT_NAMES.items()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--data",
        default="$HOME/.local/share/Steam/steamapps/common/StickFightTheGame/StickFight_Data",
    )
    ap.add_argument("--out", default="./maps")
    ap.add_argument("--only", type=int, default=None)
    ap.add_argument("--verbose", "-v", action="store_true")
    args = ap.parse_args()
    # The default --data embeds $HOME (and a user may pass ~); expand both so
    # UnityPy.load / os.listdir get a real path, not the literal "$HOME/...".
    args.data = os.path.expanduser(os.path.expandvars(args.data))

    os.makedirs(args.out, exist_ok=True)

    print(f"Loading {args.data}...", file=sys.stderr)
    env = UnityPy.load(args.data)
    print(f"  {len(env.objects)} objects, {len(env.files)} files", file=sys.stderr)

    # Group objects by source filename, build per-file path-id lookups.
    objects_by_file = defaultdict(list)
    transforms_by_pid_global = {}
    for obj in env.objects:
        af = getattr(obj, "assets_file", None)
        if af is None:
            continue
        fname = (getattr(af, "path", None) or getattr(af, "name", "") or "").split("/")[-1]
        objects_by_file[fname].append(obj)
        if obj.type.name in ("Transform", "RectTransform"):
            transforms_by_pid_global[obj.path_id] = obj

    # Iterate level files.
    level_files = []
    for entry in sorted(os.listdir(args.data)):
        m = re.match(r"^level(\d+)$", entry)
        if not m:
            continue
        idx = int(m.group(1))
        if idx == 0 or idx == 102:
            continue
        if args.only is not None and idx != args.only:
            continue
        level_files.append(idx)
    print(f"Processing {len(level_files)} levels", file=sys.stderr)

    for idx in level_files:
        try:
            data = extract_scene(idx, objects_by_file[f"level{idx}"], transforms_by_pid_global)
        except Exception as e:
            print(f"  scene {idx}: FAILED — {e}", file=sys.stderr)
            continue
        out_path = os.path.join(args.out, f"landfall-{idx}.json")
        with open(out_path, "w") as f:
            json.dump(data, f, indent=2, separators=(",", ": "))
        if args.verbose:
            print(
                f"  scene {idx}: "
                f"static={len(data['staticColliders'])} "
                f"player={len(data['playerSpawns'])} "
                f"weapons={len(data['weaponSpawns'])} "
                f"killbox={len(data['killboxes'])} "
                f"sync={len(data['syncableObjects'])}",
                file=sys.stderr,
            )

    print(f"Done. Output at {args.out}", file=sys.stderr)


def extract_scene(idx, scene_objects, transforms_by_pid_global):
    """Parse one scene's objects and produce a JSON-ready dict."""
    # Per-file path_id lookups.
    by_pid = {obj.path_id: obj for obj in scene_objects}

    static_colliders = []
    player_spawns = []
    weapon_spawns = []
    killboxes = []
    syncable_objects = []

    # Indexing pass — find every Transform's world position (we'll need this
    # for spawn-point PPtr resolution).
    # Also pre-cache GameObject -> Transform association.
    transforms = {}  # path_id -> transform obj
    for obj in scene_objects:
        if obj.type.name in ("Transform", "RectTransform"):
            transforms[obj.path_id] = obj

    # Walk GameObjects and their components.
    for obj in scene_objects:
        if obj.type.name != "GameObject":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue

        # Find this GameObject's Transform and any colliders / MonoBehaviours.
        transform = None
        components = []
        for c in tree.get("m_Component", []):
            ref = c.get("component", c)
            pid = ref.get("m_PathID", 0)
            if pid == 0:
                continue
            child = by_pid.get(pid)
            if child is None:
                continue
            components.append(child)
            if transform is None and child.type.name in ("Transform", "RectTransform"):
                transform = child
        if transform is None:
            continue

        try:
            t_tree = transform.read_typetree()
        except Exception:
            continue
        local_scale = t_tree.get("m_LocalScale", {})
        scale = (
            float(local_scale.get("x", 1)),
            float(local_scale.get("y", 1)),
            float(local_scale.get("z", 1)),
        )
        world_pos = world_position(transform, by_pid, transforms_by_pid_global)
        go_name = tree.get("m_Name", "")

        for child in components:
            tn = child.type.name
            if tn == "BoxCollider":
                _try_collider_box(child, world_pos, scale, static_colliders)
            elif tn == "SphereCollider":
                _try_collider_sphere(child, world_pos, scale, static_colliders)
            elif tn == "CapsuleCollider":
                _try_collider_capsule(child, world_pos, scale, static_colliders)
            elif tn == "MonoBehaviour":
                cls = identify_script(child)
                if cls == "WeaponPickUp":
                    weapon_spawns.append({"pos": list(world_pos)})
                elif cls == "KillingFloor":
                    # Wide thin slab at the GO's Y position.
                    killboxes.append({
                        "pos": list(world_pos),
                        "size": [200.0, 1.0, 200.0],
                    })
                elif cls == "KIllAllOutOfRange":
                    killboxes.append({
                        "pos": list(world_pos),
                        "size": [4.4, 4.4, 4.4],
                    })
                elif cls in ("NetworkSyncableObject", "DestructiblePiece"):
                    syncable_objects.append({
                        "pos": list(world_pos),
                        "type": f"{cls}:{go_name}",
                    })
                elif cls == "MapInfo":
                    # Raw-byte parse of spawnPoints[] (UnityPy can't read SF's
                    # custom MonoBehaviour TypeTree without Cecil/pythonnet).
                    sp_pids = parse_mapinfo_spawnpoints(child)
                    for fid, pid in sp_pids:
                        sp_tr = by_pid.get(pid) or transforms_by_pid_global.get(pid)
                        if sp_tr is None:
                            continue
                        sp_world = world_position(sp_tr, by_pid, transforms_by_pid_global)
                        player_spawns.append({"pos": list(sp_world)})

    return {
        "sceneIndex": idx,
        "name": "",
        "staticColliders": static_colliders,
        "playerSpawns": player_spawns,
        "weaponSpawns": weapon_spawns,
        "killboxes": killboxes,
        "syncableObjects": syncable_objects,
    }


def _try_collider_box(child, world_pos, scale, out):
    try:
        bc = child.read_typetree()
    except Exception:
        return
    if bc.get("m_IsTrigger", False):
        return
    center = bc.get("m_Center", {})
    size = bc.get("m_Size", {})
    wp = (
        world_pos[0] + float(center.get("x", 0)) * scale[0],
        world_pos[1] + float(center.get("y", 0)) * scale[1],
        world_pos[2] + float(center.get("z", 0)) * scale[2],
    )
    wsize = (
        abs(float(size.get("x", 0)) * scale[0]),
        abs(float(size.get("y", 0)) * scale[1]),
        abs(float(size.get("z", 0)) * scale[2]),
    )
    out.append({"pos": list(wp), "rot": [0, 0, 0, 1], "size": list(wsize), "kind": "Box"})


def _try_collider_sphere(child, world_pos, scale, out):
    try:
        sc = child.read_typetree()
    except Exception:
        return
    if sc.get("m_IsTrigger", False):
        return
    center = sc.get("m_Center", {})
    radius = float(sc.get("m_Radius", 0))
    wp = (
        world_pos[0] + float(center.get("x", 0)) * scale[0],
        world_pos[1] + float(center.get("y", 0)) * scale[1],
        world_pos[2] + float(center.get("z", 0)) * scale[2],
    )
    d = radius * 2 * max(abs(scale[0]), abs(scale[1]), abs(scale[2]))
    out.append({"pos": list(wp), "rot": [0, 0, 0, 1], "size": [d, d, d], "kind": "Sphere"})


def _try_collider_capsule(child, world_pos, scale, out):
    try:
        cc = child.read_typetree()
    except Exception:
        return
    if cc.get("m_IsTrigger", False):
        return
    center = cc.get("m_Center", {})
    radius = float(cc.get("m_Radius", 0))
    height = float(cc.get("m_Height", 0))
    wp = (
        world_pos[0] + float(center.get("x", 0)) * scale[0],
        world_pos[1] + float(center.get("y", 0)) * scale[1],
        world_pos[2] + float(center.get("z", 0)) * scale[2],
    )
    d = max(radius * 2, height) * max(abs(scale[0]), abs(scale[1]), abs(scale[2]))
    out.append({"pos": list(wp), "rot": [0, 0, 0, 1], "size": [d, d, d], "kind": "Capsule"})


def identify_script(mb_obj):
    """Return the SF MonoScript class name for a MonoBehaviour, or None."""
    try:
        head = mb_obj.parse_monobehaviour_head()
    except Exception:
        return None
    pid = getattr(head.m_Script, "path_id", 0)
    return SF_SCRIPT_BY_PID.get(pid)


def parse_mapinfo_spawnpoints(mb_obj):
    """Raw-byte parse of MapInfo.spawnPoints (Transform[]).
    Layout (after MonoBehaviour header):
      header = 32 bytes (PPtr GO + Enabled + PPtr Script + m_Name length)
      m_Name = N bytes + 4-byte alignment
      spawnPoints array:
        int32 count
        N × PPtr<Transform> (each: int32 file_id + int64 path_id = 12 bytes)
    Returns list of (file_id, path_id) tuples; caller resolves to world pos."""
    try:
        raw = mb_obj.get_raw_data()
    except Exception:
        return []
    if len(raw) < 36:
        return []
    name_len = struct.unpack_from("<i", raw, 28)[0]
    cursor = 32 + name_len
    cursor = (cursor + 3) & ~3
    if cursor + 4 > len(raw):
        return []
    sp_count = struct.unpack_from("<i", raw, cursor)[0]
    cursor += 4
    if sp_count < 0 or sp_count > 32:
        return []
    out = []
    for _ in range(sp_count):
        if cursor + 12 > len(raw):
            break
        fid = struct.unpack_from("<i", raw, cursor)[0]
        pid = struct.unpack_from("<q", raw, cursor + 4)[0]
        out.append((fid, pid))
        cursor += 12
    return out


def world_position(transform_obj, by_pid, global_by_pid=None):
    """Walk parent chain to get a Transform's world position."""
    pos = [0.0, 0.0, 0.0]
    current = transform_obj
    visited = set()
    while current is not None and current.path_id not in visited:
        visited.add(current.path_id)
        try:
            t = current.read_typetree()
        except Exception:
            break
        lp = t.get("m_LocalPosition", {})
        pos[0] += float(lp.get("x", 0))
        pos[1] += float(lp.get("y", 0))
        pos[2] += float(lp.get("z", 0))
        father = t.get("m_Father", {})
        pid = father.get("m_PathID", 0)
        if pid == 0:
            break
        current = by_pid.get(pid)
        if current is None and global_by_pid is not None:
            current = global_by_pid.get(pid)
    return tuple(pos)


if __name__ == "__main__":
    main()
