# zip-src — source of the scripts inside the installer zip

These five files are the script/text members of the root
`sf-multiplayer-StickFight-Installer.zip` (the offline 1-click installer the
README advertises). They were previously **only** inside the zip — i.e. the
installer players actually run had no reviewable source in git. Extracted
2026-06-11 during the security/packaging pass so changes to them get diffed
like any other code.

Two installer variants exist on purpose:

| Variant | Scripts | Payload | Used by |
|---|---|---|---|
| **Offline zip** (this dir) | `install.ps1` / `uninstall.ps1` (+ INSTALAR/DESINSTALAR wrappers, README.txt) | `StickFight-DropIn/` inside the zip: full BepInEx core + plugins + patched assembly, no downloads | the README hero download |
| **Online** (`../`) | `install-sf-multiplayer.ps1` / `uninstall-sf-multiplayer.ps1` | `../files/` DLLs + BepInEx downloaded at install time | operators / manual installs |

## Updating the zip

There is no builder script yet. To refresh payload DLLs in place (what the
2026-06-11 pass did):

```bash
S=$(mktemp -d) && mkdir -p "$S/sf-multiplayer-StickFight/StickFight-DropIn/BepInEx/plugins"
cp dist/SFClientRecon.dll dist/SFServerBrowser.dll "$S/sf-multiplayer-StickFight/StickFight-DropIn/BepInEx/plugins/"
(cd "$S" && zip ../../sf-multiplayer-StickFight-Installer.zip \
  'sf-multiplayer-StickFight/StickFight-DropIn/BepInEx/plugins/SFClientRecon.dll' \
  'sf-multiplayer-StickFight/StickFight-DropIn/BepInEx/plugins/SFServerBrowser.dll')
```

Same pattern for script members (paths relative to the zip root,
`sf-multiplayer-StickFight/<name>`). If you edit a script HERE, push it into
the zip the same way — the zip is the artifact, this dir is the source.
**Keep the two in sync; the zip wins for what players actually got.**

TODO (flagged in notes/REVIEW_2026-06-10.md): make the zip a CI/Release
artifact built from this dir + `files/` + a pinned BepInEx, instead of a
tracked binary that drifts.
