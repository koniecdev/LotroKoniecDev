#!/usr/bin/env bash
# One-time bootstrap of the ASP.NET Core dev HTTPS certificate for Docker.
# Trusts the dev cert on the host, then exports it to .docker/https/aspnetapp.pfx
# so containers can mount it and serve HTTPS through Kestrel.
#
# Cert password is read from .env (key: ASPNETCORE_KESTREL_CERT_PASSWORD).
# Run once on a fresh clone, then `docker compose up`.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CERT_DIR="$REPO_ROOT/.docker/https"
CERT_PATH="$CERT_DIR/aspnetapp.pfx"
ENV_FILE="$REPO_ROOT/.env"

if [ ! -f "$ENV_FILE" ]; then
    echo ".env not found. Copy .env.example to .env first." >&2
    exit 1
fi

PASSWORD="$(grep -E '^[[:space:]]*ASPNETCORE_KESTREL_CERT_PASSWORD[[:space:]]*=' "$ENV_FILE" | head -n1 | cut -d= -f2- | xargs || true)"
if [ -z "$PASSWORD" ]; then
    echo "ASPNETCORE_KESTREL_CERT_PASSWORD not set in .env" >&2
    exit 1
fi

mkdir -p "$CERT_DIR"

echo "Trusting ASP.NET Core dev certificate (you may be prompted)..."
dotnet dev-certs https --trust

[ -f "$CERT_PATH" ] && rm -f "$CERT_PATH"

echo "Exporting PFX to $CERT_PATH ..."
dotnet dev-certs https -ep "$CERT_PATH" -p "$PASSWORD" --format Pfx >/dev/null

echo ""
echo "Done. Cert at: $CERT_PATH"
echo "Next: docker compose up"
