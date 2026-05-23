#!/usr/bin/env bash
# One-command build + deploy for Path A. Mirrors ALKA's deploy/setup-all.bat
# in spirit but for our single-Unity-process architecture.
#
# What it does:
#   1. Build sf-headless-host/SFHeadlessHost.dll (server-side, into oracle install)
#   2. Build sf-client-recon/SFClientRecon.dll (client-side, into Steam install)
#   3. Verify md5s + permissions
#
# What it doesn't do:
#   - Download Goldberg or Proton (assumed already installed)
#   - Provide ref DLLs (sf-headless-host/refs/ must exist with your Stick Fight
#     install's BepInEx.dll, 0Harmony.dll, Assembly-CSharp.dll, UnityEngine.dll)
#   - Patch Assembly-CSharp.dll (the v25 raw-UDP patched DLL is a separate dep)
#
# Env vars:
#   SF_ORACLE_INSTALL  — path to oracle SF install (default: $HOME/sf-mirror-local)
#   SF_STEAM_INSTALL   — path to player Steam SF install (default: autodetect)
#   DOTNET             — path to dotnet binary (default: $HOME/.dotnet/dotnet)
set -eu

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
ORACLE="${SF_ORACLE_INSTALL:-$HOME/sf-mirror-local}"
STEAM_DEFAULT="$HOME/.local/share/Steam/steamapps/common/StickFightTheGame"
STEAM="${SF_STEAM_INSTALL:-$STEAM_DEFAULT}"

if [ ! -x "$DOTNET" ] && ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found at $DOTNET and not on PATH." >&2
  echo "Install: https://learn.microsoft.com/dotnet/core/install/linux" >&2
  exit 1
fi
if ! command -v "$DOTNET" >/dev/null 2>&1; then
  DOTNET=dotnet
fi

echo "=== Build sf-headless-host (server-side oracle plugin) ==="
( cd "$REPO_DIR/sf-headless-host" && "$DOTNET" build -c Release ) | tail -6
SERVER_DLL="$REPO_DIR/sf-headless-host/bin/Release/SFHeadlessHost.dll"
[ -f "$SERVER_DLL" ] || { echo "Build did not produce $SERVER_DLL" >&2; exit 1; }

echo
echo "=== Build sf-client-recon (client-side reconciliation plugin) ==="
( cd "$REPO_DIR/sf-client-recon" && "$DOTNET" build -c Release ) | tail -6
CLIENT_DLL="$REPO_DIR/sf-client-recon/bin/Release/SFClientRecon.dll"
[ -f "$CLIENT_DLL" ] || { echo "Build did not produce $CLIENT_DLL" >&2; exit 1; }

echo
echo "=== Deploy server plugin → $ORACLE ==="
if [ -d "$ORACLE/BepInEx/plugins" ]; then
  cp "$SERVER_DLL" "$ORACLE/BepInEx/plugins/SFHeadlessHost.dll"
  echo "  Installed: $ORACLE/BepInEx/plugins/SFHeadlessHost.dll"
  md5sum "$ORACLE/BepInEx/plugins/SFHeadlessHost.dll"
else
  echo "  SKIP — oracle install $ORACLE not found or missing BepInEx/plugins/."
  echo "  Manually copy $SERVER_DLL → <oracle>/BepInEx/plugins/"
fi

echo
echo "=== Deploy client plugin → $STEAM ==="
if [ -d "$STEAM/BepInEx/plugins" ]; then
  cp "$CLIENT_DLL" "$STEAM/BepInEx/plugins/SFClientRecon.dll"
  echo "  Installed: $STEAM/BepInEx/plugins/SFClientRecon.dll"
  md5sum "$STEAM/BepInEx/plugins/SFClientRecon.dll"
else
  echo "  SKIP — Steam install $STEAM not found or missing BepInEx/plugins/."
  echo "  Manually copy $CLIENT_DLL → <SF install>/BepInEx/plugins/"
fi

echo
echo "=== Done ==="
echo "Start an oracle:    $REPO_DIR/launch-lobby.sh"
echo "List running:       $REPO_DIR/list-lobbies.sh"
echo "Stop all:           $REPO_DIR/stop-all-lobbies.sh"
echo "Steam SF launch options: WINEDLLOVERRIDES=\"winhttp=n,b\" %command% -address 127.0.0.1 -port 1337"
