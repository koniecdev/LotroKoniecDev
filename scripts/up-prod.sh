#!/usr/bin/env bash
# "docker compose up" for the production-parity stack, with one-time .env.prod + TLS bootstrap.
# Mirrors scripts/up.sh but targets compose.prod.yaml. On first run it creates .env.prod from the
# example WITH freshly generated OpenIddict secrets, and mints the local CA / proxy / Postgres certs.
# Review the SMTP / admin / database values in .env.prod before a real run. Extra args pass through.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$REPO_ROOT/.env.prod"
CERT_PATH="$REPO_ROOT/.docker/prod-https/proxy.crt"

cd "$REPO_ROOT"

if [ ! -f "$ENV_FILE" ]; then
    # Drop the three empty OpenIddict placeholders, then append freshly generated secrets.
    grep -vE '^OpenIddict__(EncryptionKey__Key|SigningKey__RsaPrivateKeyXml|ApiClientSecret)=' \
        .env.prod.example > "$ENV_FILE"
    scripts/gen-openiddict-keys.sh >> "$ENV_FILE"
    echo ".env.prod created with generated OpenIddict secrets. Review SMTP/admin/DB values before prod use."
fi

if [ ! -f "$CERT_PATH" ]; then
    echo "Prod-parity TLS material missing — running one-time bootstrap..."
    scripts/init-prod-https.sh
fi

# Map the *.lotro.test vhosts to loopback (REQUIRED — *.test is never resolved by public DNS, by
# design, so only a local hosts entry works). Idempotent: a no-op once present, so no repeated
# prompts — admin is needed only the first time on a fresh machine. The in-stack containers don't
# need this (they reach Caddy via Docker network aliases); it's purely for the browser + host curl.
HOSTS_FILE="/etc/hosts"
VHOSTS="app.lotro.test auth.lotro.test tms.lotro.test"
HOSTS_MISSING=0
for name in $VHOSTS; do
    grep -qiF "$name" "$HOSTS_FILE" 2>/dev/null || HOSTS_MISSING=1
done
if [ "$HOSTS_MISSING" -eq 1 ]; then
    echo "Mapping *.lotro.test -> 127.0.0.1 in $HOSTS_FILE (one-time per machine; may prompt for admin)..."
    HOSTS_MARKER="# LotroKoniecDev prod-parity (compose.prod.yaml) — auto-added by up-prod"
    HOSTS_LINE="127.0.0.1 $VHOSTS"
    if [ -w "$HOSTS_FILE" ]; then
        printf '\n%s\n%s\n' "$HOSTS_MARKER" "$HOSTS_LINE" >> "$HOSTS_FILE"
    else
        printf '\n%s\n%s\n' "$HOSTS_MARKER" "$HOSTS_LINE" | sudo tee -a "$HOSTS_FILE" >/dev/null
    fi
    # macOS keeps a resolver cache; flush it so the new mapping resolves immediately (sudo creds are
    # still cached from the tee above, so this won't prompt again).
    if [ "$(uname)" = "Darwin" ]; then
        sudo dscacheutil -flushcache 2>/dev/null || true
        sudo killall -HUP mDNSResponder 2>/dev/null || true
    fi
fi

exec docker compose -f compose.prod.yaml --env-file "$ENV_FILE" up "$@"
