#!/usr/bin/env bash
# Launch sf-mirror-local in GRAPHICAL mode for hands-on testing against the
# local oracle. Plugin is batchmode-gated, so SFHeadlessHost won't activate in
# this instance — the user just sees normal SF and connects to the host on
# 127.0.0.1:1340 (the oracle).
set -u

PROTON="${SFHEADLESS_PROTON:-$HOME/.local/share/Steam/steamapps/common/Proton - Experimental/proton}"
PREFIX="${SF_PLAYER_PREFIX:-$HOME/sf-player-prefix}"
HOST="${SF_HOST:-127.0.0.1}"
PORT="${SF_PORT:-1337}"

mkdir -p "$PREFIX"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$HOME/.local/share/Steam"
export STEAM_COMPAT_DATA_PATH="$PREFIX"
export WINEDLLOVERRIDES="winhttp=n,b"
export WINEDEBUG=-all
export PROTON_USE_XALIA=0

cd "$HOME/sf-mirror-local"
echo "Launching SF (player) targeting $HOST:$PORT (prefix=$PREFIX)"
exec "$PROTON" run "$HOME/sf-mirror-local/StickFight.exe" \
  -address "$HOST" -port "$PORT" \
  -screen-width 1280 -screen-height 720 -screen-fullscreen 0
