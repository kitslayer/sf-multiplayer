#!/usr/bin/env bash
# One-command client installer for sf-multiplayer.
#
# What it does:
#   1. Detects the user's Steam SF install (Linux + macOS); prompts if it can't.
#   2. Downloads + installs BepInEx 5.4.x to the SF directory (idempotent).
#   3. Downloads the latest sf-multiplayer plugin DLLs from GitHub releases.
#   4. Copies them to <SF>/BepInEx/plugins/.
#   5. Prints the Steam launch options the user needs to set.
#
# Run as the user who owns the SF install (NOT root). Idempotent — safe to
# re-run to update plugins.
#
# Usage:
#   curl -sSL https://raw.githubusercontent.com/kitslayer/sf-multiplayer/main/deploy/install-sf-client.sh | bash
#   OR
#   bash install-sf-client.sh [--sf-path /custom/path/to/StickFightTheGame]

set -eu

REPO="kitslayer/sf-multiplayer"
BRANCH="main"

# ---- Config knobs ----
SF_PATH=""
BEPINEX_VERSION="5.4.23.5"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_x86_${BEPINEX_VERSION}.zip"

# ---- Arg parse ----
while [ $# -gt 0 ]; do
  case "$1" in
    --sf-path) SF_PATH="$2"; shift 2 ;;
    --help|-h)
      grep '^#' "$0" | head -25
      exit 0
      ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

# ---- Detect SF install ----
detect_sf() {
  local candidates=(
    "$HOME/.local/share/Steam/steamapps/common/StickFightTheGame"
    "$HOME/.steam/steam/steamapps/common/StickFightTheGame"
    "$HOME/Library/Application Support/Steam/steamapps/common/StickFightTheGame"
    "/mnt/games/SteamLibrary/steamapps/common/StickFightTheGame"
    "/mnt/Games/SteamLibrary/steamapps/common/StickFightTheGame"
  )
  for c in "${candidates[@]}"; do
    if [ -d "$c" ] && [ -f "$c/StickFight.exe" ]; then
      echo "$c"
      return 0
    fi
  done
  return 1
}

if [ -z "$SF_PATH" ]; then
  if SF_PATH=$(detect_sf); then
    echo "[*] Detected SF install: $SF_PATH"
  else
    echo "[!] Could not auto-detect SF install."
    echo "    Re-run with --sf-path /path/to/StickFightTheGame"
    exit 1
  fi
fi

if [ ! -f "$SF_PATH/StickFight.exe" ]; then
  echo "[!] $SF_PATH/StickFight.exe not found — wrong directory?"
  exit 1
fi

echo
echo "==[ sf-multiplayer client installer ]=="
echo "  SF install:  $SF_PATH"
echo "  BepInEx ver: $BEPINEX_VERSION"
echo

# ---- Install BepInEx if missing ----
if [ ! -d "$SF_PATH/BepInEx" ] || [ ! -f "$SF_PATH/winhttp.dll" ]; then
  echo "[1/3] Installing BepInEx $BEPINEX_VERSION..."
  TMPDIR=$(mktemp -d)
  trap "rm -rf $TMPDIR" EXIT

  if command -v curl >/dev/null 2>&1; then
    curl -sSL "$BEPINEX_URL" -o "$TMPDIR/bep.zip"
  elif command -v wget >/dev/null 2>&1; then
    wget -q "$BEPINEX_URL" -O "$TMPDIR/bep.zip"
  else
    echo "[!] Need curl or wget to download BepInEx."
    exit 1
  fi

  if ! command -v unzip >/dev/null 2>&1; then
    echo "[!] Need 'unzip' to extract BepInEx (apt install unzip / brew install unzip)."
    exit 1
  fi

  unzip -q -o "$TMPDIR/bep.zip" -d "$SF_PATH/"
  echo "    BepInEx installed."
else
  echo "[1/3] BepInEx already present at $SF_PATH/BepInEx — keeping it."
fi

# ---- Download plugin DLLs ----
echo "[2/3] Downloading latest sf-multiplayer plugins..."

PLUGINS_URL_BASE="https://github.com/$REPO/raw/$BRANCH/dist"
TMPDIR=${TMPDIR:-$(mktemp -d)}

# We currently DON'T host pre-built DLLs in the repo (they need user's local
# Assembly-CSharp.dll for some refs). For now, the installer assumes the user
# has built the plugins locally and we copy from their working directory.
# Future v0.2: hosted release artifacts.
if [ -f "./sf-headless-host/bin/Release/SFHeadlessHost.dll" ] && [ -f "./sf-client-recon/bin/Release/SFClientRecon.dll" ]; then
  echo "    Using locally-built plugins (./sf-headless-host/bin/Release/, ./sf-client-recon/bin/Release/)"
  cp ./sf-headless-host/bin/Release/SFHeadlessHost.dll "$TMPDIR/"
  cp ./sf-client-recon/bin/Release/SFClientRecon.dll "$TMPDIR/"
else
  echo "[!] No local plugin builds found and we don't yet host pre-built DLLs."
  echo "    Build them first: bash setup-all.sh (in the sf-multiplayer repo root)"
  echo "    OR if you ran this via curl, clone the repo and run from there:"
  echo "      git clone https://github.com/$REPO.git && cd sf-multiplayer && bash deploy/install-sf-client.sh"
  exit 1
fi

# ---- Copy plugins ----
echo "[3/3] Installing plugins into $SF_PATH/BepInEx/plugins/"
mkdir -p "$SF_PATH/BepInEx/plugins"
cp "$TMPDIR/SFHeadlessHost.dll" "$SF_PATH/BepInEx/plugins/"
cp "$TMPDIR/SFClientRecon.dll"  "$SF_PATH/BepInEx/plugins/"
echo "    Installed:"
md5sum "$SF_PATH/BepInEx/plugins/SFHeadlessHost.dll" "$SF_PATH/BepInEx/plugins/SFClientRecon.dll"

echo
echo "==[ done ]=="
echo
echo "Next step — set Steam Launch Options for Stick Fight:"
echo
echo "  Linux/Proton:   WINEDLLOVERRIDES=\"winhttp=n,b\" %command% -address SERVER_IP -port 1337"
echo "  Windows:        %command% -address SERVER_IP -port 1337"
echo
echo "Right-click Stick Fight in Steam → Properties → Launch Options, paste, save."
echo "Then click Play — you'll connect directly to SERVER_IP:1337 (Phase 6 oracle)."
echo
echo "Server browser:  https://github.com/$REPO/blob/$BRANCH/deploy/server-browser.html"
