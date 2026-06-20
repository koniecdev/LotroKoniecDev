#!/usr/bin/env bash
# One-time bootstrap of the TLS material for the production-parity stack (compose.prod.yaml).
#
# Unlike the dev stack (scripts/init-dev-https.sh), the prod-parity stack terminates TLS at a
# reverse proxy (Caddy) on three host-based virtual hosts, so it needs a cert whose SANs cover
# them — the ASP.NET `localhost` dev cert does not. This script mints a tiny local CA and:
#   * proxy.{crt,key}  — leaf for app/auth/tms.lotro.test (mounted into Caddy)
#   * rootCA.crt       — the CA the Frontend + tms-api containers trust for their OIDC back-channel
#                        to https://auth.lotro.test; .docker/trust-ca-entrypoint.sh installs it into
#                        their OS trust store (.NET ignores SSL_CERT_FILE)
#   * postgres.{crt,key} — self-signed server cert so Postgres can run with ssl=on (the app
#                        connects with `Trust Server Certificate=true`, so the name is irrelevant)
#
# Everything lands in .docker/prod-https/ (git-ignored). Re-running is a no-op unless --force.
# Add the three hostnames to your hosts file once (REQUIRED — *.test is not auto-resolved by browsers):
#   127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CERT_DIR="$REPO_ROOT/.docker/prod-https"

FORCE=0
if [ "${1:-}" = "--force" ]; then
    FORCE=1
fi

if [ -f "$CERT_DIR/proxy.crt" ] && [ "$FORCE" -eq 0 ]; then
    echo "Prod-parity TLS material already present in $CERT_DIR (use --force to regenerate)."
    exit 0
fi

mkdir -p "$CERT_DIR"
cd "$CERT_DIR"

echo "Generating local CA..."
openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout rootCA.key -out rootCA.crt -days 3650 \
    -subj "/CN=LotroKoniecDev Local Prod-Parity CA" >/dev/null 2>&1

echo "Generating reverse-proxy leaf cert (app/auth/tms.lotro.test)..."
openssl req -newkey rsa:2048 -nodes \
    -keyout proxy.key -out proxy.csr \
    -subj "/CN=app.lotro.test" >/dev/null 2>&1
openssl x509 -req -in proxy.csr \
    -CA rootCA.crt -CAkey rootCA.key -CAcreateserial \
    -out proxy.crt -days 825 \
    -extfile <(printf "subjectAltName=DNS:app.lotro.test,DNS:auth.lotro.test,DNS:tms.lotro.test\nextendedKeyUsage=serverAuth\nbasicConstraints=CA:FALSE\n") \
    >/dev/null 2>&1
rm -f proxy.csr

echo "Generating self-signed Postgres server cert..."
openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout postgres.key -out postgres.crt -days 825 \
    -subj "/CN=lotro-postgres" >/dev/null 2>&1

chmod 600 rootCA.key proxy.key postgres.key

echo ""
echo "Done. TLS material at: $CERT_DIR"
echo "  - proxy.{crt,key}    -> mounted into the Caddy reverse proxy"
echo "  - rootCA.crt         -> installed into the Frontend + tms-api OS trust store by trust-ca-entrypoint.sh"
echo "  - postgres.{crt,key} -> Postgres ssl=on server cert"
echo ""
echo "Ensure your hosts file maps the vhosts to loopback (one-time):"
echo "  127.0.0.1 app.lotro.test auth.lotro.test tms.lotro.test"
