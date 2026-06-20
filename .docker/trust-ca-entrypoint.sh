#!/bin/sh
# compose.prod.yaml entrypoint for the two services that make a TLS back-channel to the in-stack
# reverse proxy: the Frontend (OIDC discovery/token/userinfo) and tms-api (OIDC metadata + JWKS).
# In Production OpenIddict rejects plain-HTTP requests, so both reach auth THROUGH the proxy over
# https://auth.lotro.test — which serves the local-CA leaf cert. .NET validates that cert against the
# OS trust store (it does NOT honor SSL_CERT_FILE the way curl/OpenSSL does), so install the local CA
# into the store, then drop back to the non-root app user (the image's default) and run the app.
#
# This trust install lives only in the local parity stack: real production uses a publicly-trusted
# ingress cert, so the app images need no CA injection and the Dockerfiles stay untouched.
set -e

if [ -f /certs/rootCA.crt ]; then
    cp /certs/rootCA.crt /usr/local/share/ca-certificates/lotro-prod-ca.crt
    update-ca-certificates >/dev/null 2>&1
fi

# Drop to the image's non-root app user (the MS .NET base image's `app` is uid=gid=1654; APP_GID
# defaults to APP_UID only as a convenience, not an assumption that the two always coincide).
exec setpriv --reuid="${APP_UID:-1654}" --regid="${APP_GID:-${APP_UID:-1654}}" --init-groups "$@"
