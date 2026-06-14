#!/usr/bin/env bash
# "docker compose up" with a one-time .env bootstrap.
# The stack also boots fine without a .env (compose has sane defaults); this is
# purely a convenience so you can edit secrets in one place. Extra args pass through.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$REPO_ROOT/.env"

if [ ! -f "$ENV_FILE" ]; then
    cp "$REPO_ROOT/.env.example" "$ENV_FILE"
    echo ".env created from .env.example. Edit secrets if needed."
fi

cd "$REPO_ROOT"
exec docker compose up "$@"
