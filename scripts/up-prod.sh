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

echo ""
echo "Reminder: map the vhosts to loopback once (REQUIRED — *.test is not auto-resolved by browsers):"
echo "  127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test"
echo ""

exec docker compose -f compose.prod.yaml --env-file "$ENV_FILE" up "$@"
