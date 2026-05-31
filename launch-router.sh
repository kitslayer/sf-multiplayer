#!/usr/bin/env bash
# Launch the sf-router single-port UDP front-door in routing mode.
#
# Clients connect to ONE public port (default 1337). The router reads the
# lobby registry that launch-lobby.sh writes (/tmp/sf-lobbies) and forwards
# each client to the backend SF.exe for the lobby code it SELECTed. Backends
# stay on loopback ports; only the router port needs to be open at the firewall.
#
# Usage:
#   ./launch-router.sh                 # listen 0.0.0.0:1337, registry /tmp/sf-lobbies, stats 127.0.0.1:8081
#   SF_ROUTER_LISTEN=0.0.0.0:1337 SF_LOBBIES_DIR=/tmp/sf-lobbies ./launch-router.sh
#
# Env:
#   SF_ROUTER_LISTEN   public UDP bind (default 0.0.0.0:1337)
#   SF_LOBBIES_DIR     lobby registry dir (default /tmp/sf-lobbies)
#   SF_ROUTER_STATS    HTTP addr for GET /router/stats (default 127.0.0.1:8081; empty to disable)
set -eu

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LISTEN="${SF_ROUTER_LISTEN:-0.0.0.0:1337}"
REGISTRY="${SF_LOBBIES_DIR:-/tmp/sf-lobbies}"
STATS="${SF_ROUTER_STATS:-127.0.0.1:8081}"

# Build the binary fresh (cheap; ~1s) so the running router always matches source.
BIN="/tmp/sf-router"
( cd "$REPO_DIR/sf-router" && go build -o "$BIN" ./cmd/sf-router )

mkdir -p "$REGISTRY"

ARGS=(-listen "$LISTEN" -registry "$REGISTRY")
[ -n "$STATS" ] && ARGS+=(-stats "$STATS")

echo "Starting sf-router: listen=$LISTEN registry=$REGISTRY stats=${STATS:-<off>}"
exec "$BIN" "${ARGS[@]}"
