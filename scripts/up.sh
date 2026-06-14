#!/usr/bin/env bash
# "docker compose up" with a one-time .env bootstrap.
# compose has no secret defaults, so a .env is required; this creates it from .env.example
# on first run (edit secrets there afterwards). Extra args pass through.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$REPO_ROOT/.env"

if [ ! -f "$ENV_FILE" ]; then
    cp "$REPO_ROOT/.env.example" "$ENV_FILE"
    echo ".env created from .env.example. Edit secrets if needed."
fi

cd "$REPO_ROOT"
exec docker compose up "$@"
