#!/usr/bin/env bash
# Launches one headless Stick Fight instance under Proton+Goldberg with
# SFHeadlessHost loaded. Honors SFHEADLESS_PORT, SFHEADLESS_BRIDGEPORT,
# SFHEADLESS_SCENE, SFHEADLESS_DEBUG from the caller's env.
#
# The caller (the Go server's oracle package) is expected to redirect
# stdout/stderr; we don't manage that here.
set -u

# Use the sf-mirror-local install (Goldberg-shimmed) — see
# notes/recon/SERVER_ARCHITECTURE.md for why we don't use the real Steam install
# for headless. Override with SFHEADLESS_INSTALL=/path/to/StickFightTheGame.
SF_DIR="${SFHEADLESS_INSTALL:-$HOME/sf-mirror-local}"
PROTON="${SFHEADLESS_PROTON:-$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton}"

# Each oracle gets its own wineprefix so concurrent instances don't fight over
# user-state files. Default location: /tmp/sf-oracle-prefix-<bridgeport>.
PREFIX="${SFHEADLESS_PREFIX:-/tmp/sf-oracle-prefix-${SFHEADLESS_BRIDGEPORT:-1341}}"
mkdir -p "$PREFIX"

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export STEAM_COMPAT_DATA_PATH="$PREFIX"
# winhttp=n,b loads the BepInEx doorstop. The *.drv= (empty = disabled) entries
# turn OFF Wine's audio drivers so a HEADLESS instance is silent — it has no
# reason to open the audio device, and on a dev laptop with speakers the menu
# music/SFX is just noise. Wine falls back to a null audio device cleanly.
export WINEDLLOVERRIDES="winhttp=n,b;winepulse.drv=;winealsa.drv=;wineoss.drv="
export WINEDEBUG=-all
export PROTON_USE_XALIA=0

cd "$SF_DIR"
exec "$PROTON" run "$SF_DIR/StickFight.exe" -batchmode -nographics \
  -logFile "/tmp/sf-oracle-unity-${SFHEADLESS_BRIDGEPORT:-1341}.log"
