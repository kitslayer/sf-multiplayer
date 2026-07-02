#!/usr/bin/env bash
# sflauncher.sh — one-click Linux/macOS client launcher for sf-multiplayer.
#
# What it does (idempotent):
#   1. Find your Stick Fight Steam install
#   2. Install BepInEx 5.4.23.5 if missing
#   3. Download latest plugin DLLs from this repo's main branch
#   4. Install them into <SF>/BepInEx/plugins/
#   5. Open the lobby browser in your default browser pointing at the
#      LOBBY_URL you choose (set via $LOBBY_URL env var or first-run prompt)
#
# Make this script executable + run it:
#   chmod +x sflauncher.sh
#   ./sflauncher.sh
#
# Or set the server explicitly:
#   LOBBY_URL=http://69.53.117.43:8080/lobbies ./sflauncher.sh

set -eu

REPO="kitslayer/sf-multiplayer"
BRANCH="main"
BEPINEX_VERSION="5.4.23.5"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x86_${BEPINEX_VERSION}.zip"
PLUGIN_HOST_URL="https://github.com/$REPO/raw/$BRANCH/dist/SFHeadlessHost.dll"
PLUGIN_RECON_URL="https://github.com/$REPO/raw/$BRANCH/dist/SFClientRecon.dll"
BROWSER_URL_BASE="https://raw.githubusercontent.com/$REPO/$BRANCH/deploy/server-browser.html"

CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/sf-multiplayer-launcher"
LAST_URL_FILE="$CONFIG_DIR/last_url"
mkdir -p "$CONFIG_DIR"

# ---- styling ----
if [ -t 1 ]; then
  c_g='\033[0;32m'; c_y='\033[0;33m'; c_r='\033[0;31m'; c_b='\033[0;34m'; c_n='\033[0m'
else
  c_g=''; c_y=''; c_r=''; c_b=''; c_n=''
fi
say() { printf "${c_b}[*]${c_n} %s\n" "$*"; }
ok()  { printf "${c_g}[✓]${c_n} %s\n" "$*"; }
warn(){ printf "${c_y}[!]${c_n} %s\n" "$*"; }
err() { printf "${c_r}[!]${c_n} %s\n" "$*" >&2; }

# ---- header ----
echo
echo "==[ sf-multiplayer launcher ]=="
echo

# ---- find SF install ----
find_sf() {
  local candidates=(
    "$HOME/.local/share/Steam/steamapps/common/StickFightTheGame"
    "$HOME/.steam/steam/steamapps/common/StickFightTheGame"
    "$HOME/Library/Application Support/Steam/steamapps/common/StickFightTheGame"
    "/mnt/games/SteamLibrary/steamapps/common/StickFightTheGame"
    "/mnt/Games/SteamLibrary/steamapps/common/StickFightTheGame"
  )
  # Also scan plugged-in mounts under /run/media/$USER
  if [ -d "/run/media/$USER" ]; then
    for m in /run/media/$USER/*/SteamLibrary/steamapps/common/StickFightTheGame; do
      [ -d "$m" ] && candidates+=("$m")
    done
  fi
  for c in "${candidates[@]}"; do
    if [ -d "$c" ] && [ -f "$c/StickFight.exe" ]; then
      echo "$c"
      return 0
    fi
  done
  return 1
}

if SF=$(find_sf); then
  ok "Found Stick Fight: $SF"
else
  warn "Couldn't auto-find Stick Fight."
  printf "Drag the StickFightTheGame folder here OR type the path, then Enter:\n> "
  read -r SF
  SF="${SF%\'}"; SF="${SF#\'}"    # strip enclosing quotes (drag-drop sometimes adds)
  SF="${SF%\"}"; SF="${SF#\"}"
  if [ ! -f "$SF/StickFight.exe" ]; then
    err "$SF doesn't contain StickFight.exe. Aborting."
    exit 1
  fi
fi

# ---- tooling check ----
if command -v curl >/dev/null 2>&1; then
  DL() { curl -fsSL -o "$2" "$1"; }
elif command -v wget >/dev/null 2>&1; then
  DL() { wget -q -O "$2" "$1"; }
else
  err "Need 'curl' or 'wget' (apt install curl  /  brew install curl)."
  exit 1
fi
if ! command -v unzip >/dev/null 2>&1; then
  err "Need 'unzip' (apt install unzip  /  brew install unzip)."
  exit 1
fi

# ---- install BepInEx if missing ----
if [ ! -d "$SF/BepInEx" ] || [ ! -f "$SF/winhttp.dll" ]; then
  say "Installing BepInEx $BEPINEX_VERSION..."
  TMP=$(mktemp -d); trap "rm -rf $TMP" EXIT
  DL "$BEPINEX_URL" "$TMP/bep.zip"
  unzip -q -o "$TMP/bep.zip" -d "$SF/"
  ok "BepInEx installed."
else
  ok "BepInEx already present — keeping it."
fi

# ---- download + install plugins ----
say "Downloading latest plugin DLLs from $REPO@$BRANCH..."
mkdir -p "$SF/BepInEx/plugins"
DL "$PLUGIN_HOST_URL"  "$SF/BepInEx/plugins/SFHeadlessHost.dll"
DL "$PLUGIN_RECON_URL" "$SF/BepInEx/plugins/SFClientRecon.dll"
ok "Plugins installed."
md5sum "$SF/BepInEx/plugins/SFHeadlessHost.dll" "$SF/BepInEx/plugins/SFClientRecon.dll" 2>/dev/null || true

# ---- pick lobby URL ----
if [ -z "${LOBBY_URL:-}" ]; then
  default_url=$(cat "$LAST_URL_FILE" 2>/dev/null || echo "http://69.53.117.43:8080/lobbies")
  echo
  printf "Lobby endpoint URL [%s]: " "$default_url"
  read -r LOBBY_URL
  LOBBY_URL="${LOBBY_URL:-$default_url}"
fi
echo "$LOBBY_URL" > "$LAST_URL_FILE"

# ---- print Steam launch options ----
SERVER_HOST=$(echo "$LOBBY_URL" | sed -E 's|^[a-z]+://([^:/]+).*|\1|')
SERVER_PORT="${LOBBY_PORT:-1338}"
echo
ok "Set Steam Launch Options for Stick Fight: The Game:"
echo
echo "    WINEDLLOVERRIDES=\"winhttp=n,b\" %command% -address $SERVER_HOST -port $SERVER_PORT"
echo
echo "(Right-click Stick Fight in Steam → Properties → Launch Options → paste → close)"
echo

# ---- open server browser ----
say "Opening lobby browser in your default browser..."
BROWSER_LOCAL="$CONFIG_DIR/server-browser.html"
DL "$BROWSER_URL_BASE" "$BROWSER_LOCAL"
# Append URL anchor so the page pre-fills (the browser reads localStorage too,
# so this is a hint for first-time users)
echo "<!-- prefilled URL: $LOBBY_URL -->" >> "$BROWSER_LOCAL"

if command -v xdg-open >/dev/null 2>&1; then
  xdg-open "$BROWSER_LOCAL" >/dev/null 2>&1 &
elif command -v open >/dev/null 2>&1; then
  open "$BROWSER_LOCAL"
else
  warn "No xdg-open/open found. Open this URL manually:"
  echo "    file://$BROWSER_LOCAL"
fi

echo
ok "All set. Click Play on Stick Fight in Steam (after setting launch options above)."
echo
