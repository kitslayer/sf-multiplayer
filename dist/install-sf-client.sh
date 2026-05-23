#!/usr/bin/env bash
# sf-multiplayer Linux/macOS client installer.
#
# Place this script in the same folder as SFHeadlessHost.dll +
# SFClientRecon.dll. Run it. It will:
#
#   1. Find your Stick Fight install (Linux + macOS Steam paths)
#   2. Download + install BepInEx 5.4.x if not present
#   3. Copy the two plugin DLLs into <SF>/BepInEx/plugins/
#   4. Print the Steam Launch Options you need to set
#
# Re-run anytime to update plugins.

set -eu

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BEPINEX_VERSION="5.4.23.5"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_x86_${BEPINEX_VERSION}.zip"

PLUGIN1="$HERE/SFHeadlessHost.dll"
PLUGIN2="$HERE/SFClientRecon.dll"
if [ ! -f "$PLUGIN1" ] || [ ! -f "$PLUGIN2" ]; then
  echo "[!] Missing plugin DLLs in $HERE"
  echo "    Expected: SFHeadlessHost.dll and SFClientRecon.dll next to this script."
  exit 1
fi

# ---- Detect SF install ----
SF_PATH=""
for c in \
  "$HOME/.local/share/Steam/steamapps/common/StickFightTheGame" \
  "$HOME/.steam/steam/steamapps/common/StickFightTheGame" \
  "$HOME/Library/Application Support/Steam/steamapps/common/StickFightTheGame" \
  "/mnt/games/SteamLibrary/steamapps/common/StickFightTheGame" \
  "/mnt/Games/SteamLibrary/steamapps/common/StickFightTheGame" \
  "/run/media/$USER"/*/SteamLibrary/steamapps/common/StickFightTheGame ; do
  if [ -d "$c" ] && [ -f "$c/StickFight.exe" ]; then SF_PATH="$c"; break; fi
done

if [ -z "$SF_PATH" ]; then
  echo "Couldn't auto-find Stick Fight install."
  read -r -p "Path to your StickFightTheGame folder: " SF_PATH
fi
if [ ! -f "$SF_PATH/StickFight.exe" ]; then
  echo "[!] $SF_PATH/StickFight.exe not found."
  exit 1
fi

echo
echo "==[ sf-multiplayer client installer ]=="
echo "  SF install:  $SF_PATH"
echo

# ---- Install BepInEx ----
if [ ! -d "$SF_PATH/BepInEx" ] || [ ! -f "$SF_PATH/winhttp.dll" ]; then
  echo "[1/3] Downloading BepInEx $BEPINEX_VERSION..."
  TMP=$(mktemp -d)
  trap "rm -rf $TMP" EXIT
  if command -v curl >/dev/null 2>&1; then
    curl -sSL "$BEPINEX_URL" -o "$TMP/bep.zip"
  elif command -v wget >/dev/null 2>&1; then
    wget -q "$BEPINEX_URL" -O "$TMP/bep.zip"
  else
    echo "[!] Need curl or wget to download BepInEx."
    exit 1
  fi
  if ! command -v unzip >/dev/null 2>&1; then
    echo "[!] Need 'unzip' to extract BepInEx (apt install unzip / brew install unzip)."
    exit 1
  fi
  unzip -q -o "$TMP/bep.zip" -d "$SF_PATH/"
  echo "    BepInEx installed."
else
  echo "[1/3] BepInEx already present — keeping it."
fi

# ---- Copy plugins ----
echo "[2/3] Installing plugins to $SF_PATH/BepInEx/plugins/"
mkdir -p "$SF_PATH/BepInEx/plugins"
cp "$PLUGIN1" "$SF_PATH/BepInEx/plugins/"
cp "$PLUGIN2" "$SF_PATH/BepInEx/plugins/"
md5sum "$SF_PATH/BepInEx/plugins/SFHeadlessHost.dll" "$SF_PATH/BepInEx/plugins/SFClientRecon.dll" 2>/dev/null \
  || md5 "$SF_PATH/BepInEx/plugins/SFHeadlessHost.dll" "$SF_PATH/BepInEx/plugins/SFClientRecon.dll"

echo
echo "[3/3] Set Steam Launch Options for Stick Fight: The Game:"
echo
echo "    WINEDLLOVERRIDES=\"winhttp=n,b\" %command% -address SERVER_IP -port 1337"
echo
echo "(Replace SERVER_IP with your server's address, e.g. 192.168.1.115)"
echo
echo "Steam → Stick Fight → Properties → Launch Options → paste → close → click Play."
echo
echo "==[ done ]=="
