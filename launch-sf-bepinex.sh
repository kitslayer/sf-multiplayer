#!/usr/bin/env bash
# Launches the real Stick Fight install with BepInEx forced on. Use this
# instead of "Play" in Steam when you want SFNetcodeV2 to actually load —
# Steam-clicked launches don't pass WINEDLLOVERRIDES so the doorstop
# (winhttp.dll) never gets injected.
#
# Pre-reqs:
#   - SteamLinuxRuntime + Proton Experimental installed via Steam
#   - SF is at the standard install path under common/StickFightTheGame
#   - SFNetcodeV2.dll is in BepInEx/plugins/
#
# After launching, tail BepInEx/LogOutput.log to confirm the plugin loaded;
# you should see "Loading [SFNetcodeV2 0.1.0]" and "advertising protocol v26".
set -u
SF_DIR="$HOME/.local/share/Steam/steamapps/common/StickFightTheGame"
PROTON="$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export STEAM_COMPAT_DATA_PATH="$HOME/.local/share/Steam/steamapps/compatdata/674940"
export WINEDLLOVERRIDES="winhttp=n,b"
export PROTON_USE_XALIA=0
cd "$SF_DIR"
exec "$PROTON" run "$SF_DIR/StickFight.exe" "$@"
