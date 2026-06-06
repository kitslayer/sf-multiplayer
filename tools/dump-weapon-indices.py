#!/usr/bin/env python3
"""Walk the SF asset bundles, find the Player prefab, and extract the
weapon name → prefab-index mapping by reading the Transform tree under
Weapons/. The decompile of WeaponSelectionHandler tells us the byte
m_WeaponObjects index comes from the first space-delimited token of
each child's name (e.g., "6 Sniper" → index 6).

Output: stdout — two columns: index name.
"""
import os
import sys

import UnityPy

DATA_DIR = os.path.expanduser(
    os.environ.get(
        "SF_DATA_DIR",
        "~/.local/share/Steam/steamapps/common/StickFightTheGame/StickFight_Data",
    )
)

def main():
    found = {}
    # Walk all asset bundles looking for GameObjects named "Player".
    for fn in sorted(os.listdir(DATA_DIR)):
        path = os.path.join(DATA_DIR, fn)
        if not os.path.isfile(path):
            continue
        # Only care about .assets / level* files.
        if not (fn.endswith(".assets") or fn.startswith("level") or fn == "globalgamemanagers"):
            continue
        try:
            env = UnityPy.load(path)
        except Exception:
            continue
        for obj in env.objects:
            if obj.type.name != "GameObject":
                continue
            try:
                data = obj.read()
                if data.name != "Player":
                    continue
                # Walk children of Transform to find a child named "Weapons".
                root_tr = None
                for cref in data.m_Components:
                    comp = cref.read()
                    if comp.type.name == "Transform":
                        root_tr = comp
                        break
                if root_tr is None:
                    continue
                weapons_tr = None
                for child_ref in root_tr.m_Children:
                    child_tr = child_ref.read()
                    child_go = child_tr.m_GameObject.read()
                    if child_go.name == "Weapons":
                        weapons_tr = child_tr
                        break
                if weapons_tr is None:
                    continue
                for w_ref in weapons_tr.m_Children:
                    w_tr = w_ref.read()
                    w_go = w_tr.m_GameObject.read()
                    name = w_go.name
                    # First space-delimited token is the index.
                    parts = name.split(" ", 1)
                    if len(parts) != 2:
                        continue
                    try:
                        idx = int(parts[0])
                    except ValueError:
                        continue
                    found.setdefault(idx, parts[1])
            except Exception:
                continue
    if not found:
        print("No Player prefab found", file=sys.stderr)
        sys.exit(1)
    for idx in sorted(found):
        print(idx, found[idx])

if __name__ == "__main__":
    main()
