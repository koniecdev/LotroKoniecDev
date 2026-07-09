#!/usr/bin/env bash
# Post-deploy smoke test (M6-13). One command that gives a green/red signal that a deployed
# environment came up correctly, without manual clicking — run it after every deploy (staging or
# production), or against the local prod-parity / dev stack.
#
# It exercises the five legs that actually break on a deploy:
#   1. Health      — auth-api + tms-api /health/ready return 200; the frontend root responds (it has
#                    no /health endpoint — it is a Static-SSR app, so "the page serves" is liveness).
#   2. FE assets   — the frontend image actually shipped its static web assets. "The page serves" is
#                    NOT enough: a [StreamRendering] page returns 200 with its spinner frame before it
#                    fetches anything, so an image whose static-web-assets manifest lost `_framework/*`
#                    passes leg 1 while every asset 404s and the browser spins forever (#414). The
#                    fingerprint in `blazor.web.<hash>.js` is the tell — `@Assets[]` emits it only when
#                    it resolved the manifest, so an unfingerprinted src means the manifest is empty.
#   3. Auth token  — a client-credentials token round-trip against auth-api's /connect/token. This is
#                    the only non-interactive OIDC grant available in staging/production (the web
#                    client needs a browser; the password-flow client is seeded only in Testing).
#   4. Token accept — tms-api ACCEPTS that token: an anonymous call to a protected read is 401, and
#                    the same call WITH the bearer token is NOT 401 (it is 403 — the service account
#                    has no role; every TMS endpoint is role-gated). 401-with-a-valid-token is the
#                    classic "works locally, breaks on staging" issuer/audience/JWKS mismatch
#                    (runbook → "Consistency rules that bite", rule #1), so distinguishing 403 from
#                    401 is the high-value check, not reading any particular row.
#   5. Distribution — the public translation-file endpoint serves the artifact with an ETag and
#                    honours If-None-Match with a 304 (the CLI/player relies on this; spec 0001).
#
# Clear pass/fail per check; NON-ZERO exit on any failure (exit 1). Usage / configuration problems
# exit 2. A not-yet-seeded environment (no translation artifact built) WARNS on leg 5 rather than
# failing — a correctly deployed but empty environment is still "up". Keep in sync with smoke.ps1.

set -euo pipefail

usage() {
    cat <<'EOF'
Post-deploy smoke test for a LotroKoniecDev environment.

Usage:
  scripts/smoke.sh --auth-url URL --tms-url URL --frontend-url URL [--client-secret SECRET] [options]

Required (flag or env var):
  --auth-url      URL    auth-api base URL          (env SMOKE_AUTH_URL)
  --tms-url       URL    tms-api base URL           (env SMOKE_TMS_URL)
  --frontend-url  URL    frontend base URL          (env SMOKE_FRONTEND_URL)
  --client-secret SECRET OpenIddict API client secret (env SMOKE_CLIENT_SECRET)

Options:
  --client-id     ID     OIDC client id             (env SMOKE_CLIENT_ID,  default lotrokoniecdev-api)
  --scope         SCOPE  token scope                (env SMOKE_SCOPE,      default service)
  --lang          LANG   translation-file language  (env SMOKE_LANG,       default pl)
  --timeout       SECS   per-request timeout        (env SMOKE_TIMEOUT,    default 15)
  --insecure, -k         skip TLS verification (local CA / dev cert stacks; env SMOKE_INSECURE=1)
  -h, --help             show this help

Examples:
  # A real environment (publicly-trusted ingress cert — no --insecure):
  SMOKE_CLIENT_SECRET=… scripts/smoke.sh \
    --auth-url https://auth.lotro-translator.pl \
    --tms-url  https://tms.lotro-translator.pl \
    --frontend-url https://lotro-translator.pl

  # The local dev stack (host Kestrels + untrusted dev cert):
  scripts/smoke.sh \
    --auth-url https://localhost:5003 --tms-url https://localhost:5002 \
    --frontend-url https://localhost:7017 \
    --client-secret dev-api-secret-min-32-characters-long --insecure
EOF
}

AUTH_URL="${SMOKE_AUTH_URL:-}"
TMS_URL="${SMOKE_TMS_URL:-}"
FRONTEND_URL="${SMOKE_FRONTEND_URL:-}"
CLIENT_ID="${SMOKE_CLIENT_ID:-lotrokoniecdev-api}"
CLIENT_SECRET="${SMOKE_CLIENT_SECRET:-}"
SCOPE="${SMOKE_SCOPE:-service}"
LANG_CODE="${SMOKE_LANG:-pl}"
TIMEOUT="${SMOKE_TIMEOUT:-15}"
INSECURE="${SMOKE_INSECURE:-0}"

# A value-taking flag given as the last token (no value follows) must be a clean usage error (exit 2),
# not a raw `set -u` "unbound variable" abort. $1 = flag, $2 = remaining arg count ($#).
require_value() {
    if [ "$2" -lt 2 ]; then
        echo "smoke: option $1 requires a value" >&2
        echo >&2
        usage >&2
        exit 2
    fi
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --auth-url)      require_value "$1" "$#"; AUTH_URL="$2"; shift 2 ;;
        --tms-url)       require_value "$1" "$#"; TMS_URL="$2"; shift 2 ;;
        --frontend-url)  require_value "$1" "$#"; FRONTEND_URL="$2"; shift 2 ;;
        --client-id)     require_value "$1" "$#"; CLIENT_ID="$2"; shift 2 ;;
        --client-secret) require_value "$1" "$#"; CLIENT_SECRET="$2"; shift 2 ;;
        --scope)         require_value "$1" "$#"; SCOPE="$2"; shift 2 ;;
        --lang)          require_value "$1" "$#"; LANG_CODE="$2"; shift 2 ;;
        --timeout)       require_value "$1" "$#"; TIMEOUT="$2"; shift 2 ;;
        --insecure|-k)   INSECURE=1; shift ;;
        -h|--help)       usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; echo >&2; usage >&2; exit 2 ;;
    esac
done

if ! command -v curl >/dev/null 2>&1; then
    echo "smoke: 'curl' is required but not on PATH." >&2
    exit 2
fi

missing=""
[ -z "$AUTH_URL" ]      && missing="$missing --auth-url"
[ -z "$TMS_URL" ]       && missing="$missing --tms-url"
[ -z "$FRONTEND_URL" ]  && missing="$missing --frontend-url"
[ -z "$CLIENT_SECRET" ] && missing="$missing --client-secret"
if [ -n "$missing" ]; then
    echo "smoke: missing required value(s):$missing" >&2
    echo >&2
    usage >&2
    exit 2
fi

# Strip a single trailing slash so "$URL/path" never doubles up.
AUTH_URL="${AUTH_URL%/}"
TMS_URL="${TMS_URL%/}"
FRONTEND_URL="${FRONTEND_URL%/}"

# Empty by default; "-k" when --insecure. Used UNQUOTED in curl calls so the empty case vanishes
# (a single token never word-splits) — keeps this bash-3.2 safe (macOS system bash) without arrays.
TLS_FLAG=""
[ "$INSECURE" = "1" ] && TLS_FLAG="-k"

PASS=0
FAIL=0
WARN=0
HDR_FILE="$(mktemp)"
trap 'rm -f "$HDR_FILE"' EXIT

pass() { PASS=$((PASS + 1)); printf '  \xe2\x9c\x93 %s\n' "$1"; }
fail() { FAIL=$((FAIL + 1)); printf '  \xe2\x9c\x97 %s\n' "$1"; }
warn() { WARN=$((WARN + 1)); printf '  \xe2\x9a\xa0 %s\n' "$1"; }

# Echoes the HTTP status code of a GET (000 on a connection/TLS failure). No -f, so a 4xx/5xx is a
# normal 0-exit response we inspect — only a transport failure yields 000.
get_status() {
    local code
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time "$TIMEOUT" $TLS_FLAG "$@" 2>/dev/null)" || code="000"
    echo "$code"
}

echo "== LotroKoniecDev post-deploy smoke test =="
echo "Targets:"
echo "  auth     = $AUTH_URL"
echo "  tms      = $TMS_URL"
echo "  frontend = $FRONTEND_URL"
[ "$INSECURE" = "1" ] && echo "  (TLS verification disabled: --insecure)"
echo

echo "[1/5] Health"
code="$(get_status "$AUTH_URL/health/ready")"
[ "$code" = "200" ] && pass "auth /health/ready -> 200" || fail "auth /health/ready -> $code (expected 200)"
code="$(get_status "$TMS_URL/health/ready")"
[ "$code" = "200" ] && pass "tms /health/ready -> 200" || fail "tms /health/ready -> $code (expected 200)"
# Static-SSR app: no /health endpoint. "The page serves" is the liveness signal — 2xx (home) or
# 3xx (redirect to login) both mean up; 4xx/5xx/000 mean down.
code="$(get_status "$FRONTEND_URL/")"
if [ "$code" -ge 200 ] 2>/dev/null && [ "$code" -lt 400 ] 2>/dev/null; then
    pass "frontend / -> $code (serving)"
else
    fail "frontend / -> $code (expected 2xx/3xx)"
fi
echo

echo "[2/5] Frontend static web assets (#414)"
home_html="$(curl -s --max-time "$TIMEOUT" $TLS_FLAG "$FRONTEND_URL/" 2>/dev/null || true)"
# `@Assets["_framework/blazor.web.js"]` renders a fingerprinted src ONLY when MapStaticAssets
# resolved the publish manifest. A bare `blazor.web.js` means the manifest shipped empty.
asset_path="$(printf '%s' "$home_html" | grep -oE '_framework/blazor\.web\.[A-Za-z0-9]+\.js' | head -n 1 || true)"
if [ -z "$asset_path" ]; then
    fail "frontend / has no fingerprinted _framework/blazor.web.<hash>.js (image built without its static web assets)"
else
    pass "frontend / references $asset_path (manifest resolved)"
    code="$(get_status "$FRONTEND_URL/$asset_path")"
    [ "$code" = "200" ] && pass "frontend /$asset_path -> 200" || fail "frontend /$asset_path -> $code (expected 200)"
fi
echo

echo "[3/5] OIDC token round-trip (client_credentials)"
TOKEN=""
token_out="$(curl -s --max-time "$TIMEOUT" $TLS_FLAG \
    -X POST "$AUTH_URL/connect/token" \
    -H "Content-Type: application/x-www-form-urlencoded" \
    --data-urlencode "grant_type=client_credentials" \
    --data-urlencode "client_id=$CLIENT_ID" \
    --data-urlencode "client_secret=$CLIENT_SECRET" \
    --data-urlencode "scope=$SCOPE" \
    -w $'\n%{http_code}' 2>/dev/null)" || token_out=$'\n000'
token_code="${token_out##*$'\n'}"
token_body="${token_out%$'\n'*}"
# JWT chars are base64url + '.', all safe inside "[^"]*".
TOKEN="$(printf '%s' "$token_body" | tr -d '\r\n' | grep -o '"access_token"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed -E 's/.*"access_token"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/' || true)"
if [ "$token_code" = "200" ] && [ -n "$TOKEN" ]; then
    pass "POST auth/connect/token -> 200, access_token received (client '$CLIENT_ID', scope '$SCOPE')"
else
    fail "POST auth/connect/token -> $token_code, no access_token (check client id/secret + scope grant)"
fi
echo

echo "[4/5] Token accepted by tms-api (authenticated read)"
# Reads are role-gated; a client-credentials token has no role, so the value here is proving the
# token is VALIDATED (403), not that it is rejected (401). Pair with an anonymous call (must 401)
# so the check also proves the endpoint is genuinely protected.
anon_code="$(get_status "$TMS_URL/api/v1/game-versions")"
[ "$anon_code" = "401" ] && pass "GET tms/api/v1/game-versions (no token) -> 401 (protected)" \
    || fail "GET tms/api/v1/game-versions (no token) -> $anon_code (expected 401)"
if [ -n "$TOKEN" ]; then
    auth_code="$(get_status "$TMS_URL/api/v1/game-versions" -H "Authorization: Bearer $TOKEN")"
    case "$auth_code" in
        200|403) pass "GET tms/api/v1/game-versions (bearer) -> $auth_code (token validated by tms)" ;;
        401)     fail "GET tms/api/v1/game-versions (bearer) -> 401 TOKEN REJECTED — issuer/audience/JWKS mismatch (runbook: Consistency rules #1/#2)" ;;
        *)       fail "GET tms/api/v1/game-versions (bearer) -> $auth_code (expected 200/403)" ;;
    esac
else
    fail "GET tms/api/v1/game-versions (bearer) -> skipped, no access token from step 2"
fi
echo

echo "[5/5] Translation-file distribution (ETag / 304)"
: > "$HDR_FILE"
file_code="$(curl -s -o /dev/null -D "$HDR_FILE" -w '%{http_code}' --max-time "$TIMEOUT" $TLS_FLAG "$TMS_URL/api/v1/translation-files/$LANG_CODE" 2>/dev/null)" || file_code="000"
if [ "$file_code" = "200" ]; then
    etag="$(grep -i '^etag:' "$HDR_FILE" | head -1 | sed -E 's/^[Ee][Tt][Aa][Gg]:[[:space:]]*//' | tr -d '\r' || true)"
    if [ -n "$etag" ]; then
        pass "GET tms/api/v1/translation-files/$LANG_CODE -> 200, ETag $etag"
        revalidate_code="$(get_status "$TMS_URL/api/v1/translation-files/$LANG_CODE" -H "If-None-Match: $etag")"
        [ "$revalidate_code" = "304" ] && pass "revalidate with If-None-Match -> 304 (not modified)" \
            || fail "revalidate with If-None-Match -> $revalidate_code (expected 304)"
    else
        fail "GET tms/api/v1/translation-files/$LANG_CODE -> 200 but no ETag header"
    fi
elif [ "$file_code" = "404" ]; then
    warn "GET tms/api/v1/translation-files/$LANG_CODE -> 404 (endpoint up, but no '$LANG_CODE' artifact built yet — import/seed has not run)"
else
    fail "GET tms/api/v1/translation-files/$LANG_CODE -> $file_code (expected 200, or 404 if unseeded)"
fi
echo

echo "=================================================="
if [ "$FAIL" -eq 0 ]; then
    echo "Result: PASSED — $PASS check(s) ok, $WARN warning(s)."
    exit 0
fi
echo "Result: FAILED — $FAIL failure(s), $PASS ok, $WARN warning(s)."
exit 1
