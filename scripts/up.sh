#!/usr/bin/env bash
# "docker compose up" with one-time .env + dev-cert bootstrap baked in.
# compose has no secret defaults, so a .env is required; this creates it from .env.example
# on first run (edit secrets there afterwards). Runs init-dev-https.sh if the HTTPS dev-cert
# PFX is missing, then `docker compose up`. Extra args pass through.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$REPO_ROOT/.env"
CERT_PATH="$REPO_ROOT/.docker/https/aspnetapp.pfx"

if [ ! -f "$ENV_FILE" ]; then
    cp "$REPO_ROOT/.env.example" "$ENV_FILE"
    echo ".env created from .env.example. Edit secrets if needed."
fi

if [ ! -f "$CERT_PATH" ]; then
    echo "Dev HTTPS cert missing — running one-time bootstrap..."
    "$(dirname "$0")/init-dev-https.sh"
fi

cd "$REPO_ROOT"
exec docker compose up "$@"
