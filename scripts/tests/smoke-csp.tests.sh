#!/usr/bin/env bash
# Test suite for the CSP checks of the smoke test: leg 2 on the frontend (#670) and leg 6 on
# the auth pages (#693).
#
# Both legs decide whether a deploy is rolled back, and scripts/smoke.sh is otherwise INERT to
# CI (no build, no unit test touches it), so without this file nothing checks them before
# they run against live production.
#
# A stub `curl` on PATH serves canned headers and a canned body, so every branch is pinned
# without a server: one pair for the auth origin, another for everything else. The other legs
# fail against the dummy URLs, so the assertions are on each leg's own output lines rather than
# on the overall exit code — which is 1 in every case here for unrelated reasons.
# CI runs this right before pr-verify's other guard tests.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

STUB_DIR="$TMP_ROOT/bin"
mkdir -p "$STUB_DIR"
cat > "$STUB_DIR/curl" <<'STUB'
#!/usr/bin/env bash
# Minimal curl stand-in: -w prints a status code, -D writes the canned headers, the body
# goes to -o when given and to stdout otherwise. Everything else is accepted and ignored.
body_target=""
header_target=""
want_code=0
url=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        -o) body_target="$2"; shift 2 ;;
        -D) header_target="$2"; shift 2 ;;
        -w) want_code=1; shift 2 ;;
        --max-time|--data-urlencode|-H|-X) shift 2 ;;
        https://*) url="$1"; shift ;;
        *) shift ;;
    esac
done
headers="$STUB_HEADERS"
body="$STUB_BODY"
case "$url" in
    https://auth.invalid/*) headers="$STUB_AUTH_HEADERS"; body="$STUB_AUTH_BODY" ;;
esac
[ -n "$header_target" ] && cat "$headers" > "$header_target"
if [ -n "$body_target" ]; then
    cat "$body" > "$body_target"
elif [ "$want_code" -eq 0 ]; then
    cat "$body"
fi
[ "$want_code" -eq 1 ] && printf '200'
exit 0
STUB
chmod +x "$STUB_DIR/curl"

LAST_OUTPUT=""
cases=0

fail() {
    printf '✗ %s\n' "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    exit 1
}

# $1 description, $2 CSP header line ('' = no header at all), $3 body, then extra smoke flags.
run_case() {
    local desc="$1" csp="$2" body="$3"
    shift 3
    printf 'HTTP/2 200\r\ncontent-type: text/html\r\n' > "$TMP_ROOT/headers"
    if [ -n "$csp" ]; then
        printf 'content-security-policy: %s\r\n' "$csp" >> "$TMP_ROOT/headers"
    fi
    printf '%s' "$body" > "$TMP_ROOT/body"
    : > "$TMP_ROOT/auth-headers"
    : > "$TMP_ROOT/auth-body"
    LAST_OUTPUT="$(run_smoke "$@" | sed -n '/\[2\/6\]/,/\[3\/6\]/p' || true)"
    cases=$((cases + 1))
    printf '✓ %s\n' "$desc"
}

run_smoke() {
    PATH="$STUB_DIR:$PATH" STUB_HEADERS="$TMP_ROOT/headers" STUB_BODY="$TMP_ROOT/body" \
        STUB_AUTH_HEADERS="$TMP_ROOT/auth-headers" STUB_AUTH_BODY="$TMP_ROOT/auth-body" \
        bash "$SCRIPTS_DIR/smoke.sh" \
            --auth-url https://auth.invalid --tms-url https://tms.invalid \
            --frontend-url https://fe.invalid --client-secret stub-secret \
            --timeout 1 "$@" 2>&1
}

# $1 description, then header lines ('' = none) up to a literal '--', then the body, then extra
# smoke flags. The frontend gets an empty answer, so only leg 6 is read.
run_auth_case() {
    local desc="$1"
    shift
    printf 'HTTP/2 200\r\ncontent-type: text/html\r\n' > "$TMP_ROOT/auth-headers"
    while [ "$1" != "--" ]; do
        [ -n "$1" ] && printf '%s\r\n' "$1" >> "$TMP_ROOT/auth-headers"
        shift
    done
    shift
    printf '%s' "$1" > "$TMP_ROOT/auth-body"
    shift
    : > "$TMP_ROOT/headers"
    : > "$TMP_ROOT/body"
    LAST_OUTPUT="$(run_smoke "$@" | sed -n '/\[6\/6\]/,/=====/p' || true)"
    cases=$((cases + 1))
    printf '✓ %s\n' "$desc"
}

expect_line() {
    printf '%s' "$LAST_OUTPUT" | grep -qF "$1" \
        || fail "the leg should report '$1'" "$LAST_OUTPUT"
}

reject_line() {
    if printf '%s' "$LAST_OUTPUT" | grep -qF "$1"; then
        fail "the leg should NOT report '$1'" "$LAST_OUTPUT"
    fi
}

FINGERPRINTED='<script src="_framework/blazor.web.abc123.js"></script>'
LOCKED="default-src 'self'; script-src 'self'"

# 1. The fixed frontend: a locked CSP and only external script.
run_case "locked CSP + no inline script passes" "$LOCKED" "$FINGERPRINTED"
expect_line "serves no inline <script>"

# 2. The #670 bug itself: the import map beside the external script.
run_case "locked CSP + the import map fails" "$LOCKED" \
    "$FINGERPRINTED<script type=\"importmap\">{}</script>"
expect_line "serves 1 inline <script> element(s)"

# 3/4. HTML tag and attribute names are case-insensitive.
run_case "an UPPERCASE inline script is still caught" "$LOCKED" \
    "$FINGERPRINTED<SCRIPT>alert(1)</SCRIPT>"
expect_line "serves 1 inline <script> element(s)"
run_case "an UPPERCASE SRC is not mistaken for an inline script" "$LOCKED" \
    '<script SRC="_framework/blazor.web.abc123.js"></script>'
expect_line "serves no inline <script>"

# 5. A tag split over two lines must not slip through.
run_case "a script tag split over two lines is still counted" "$LOCKED" \
    "$(printf '%s<script\n type="importmap">{}</script>' "$FINGERPRINTED")"
expect_line "serves 1 inline <script> element(s)"

# 6. No CSP at all: a Development stack warns, and --require-csp turns that into a failure.
run_case "a missing CSP header only warns by default" "" "$FINGERPRINTED"
expect_line "sends no Content-Security-Policy header"
reject_line "(--require-csp)"
run_case "a missing CSP header fails under --require-csp" "" "$FINGERPRINTED" --require-csp
expect_line "sends no Content-Security-Policy header (--require-csp)"

# 7. A weakened script-src is itself the defect — the inline count would prove nothing.
run_case "script-src with 'unsafe-inline' fails" \
    "default-src 'self'; script-src 'self' 'unsafe-inline'" "$FINGERPRINTED"
expect_line "script-src allows 'unsafe-inline'"

# 8. A nonce or hash legitimately admits one inline script, so the count is skipped.
run_case "script-src with a nonce is accepted" \
    "script-src 'self' 'nonce-r4nd0m'" "$FINGERPRINTED<script>ok()</script>"
expect_line "admits inline script only by nonce or hash"
run_case "script-src with a sha384 hash is accepted" \
    "script-src 'self' 'sha384-abc'" "$FINGERPRINTED<script>ok()</script>"
expect_line "admits inline script only by nonce or hash"

# 9. A CSP without any script-src falls back to default-src, which blocks inline too.
run_case "a CSP with no script-src still requires no inline script" \
    "default-src 'self'" "$FINGERPRINTED<script>alert(1)</script>"
expect_line "serves 1 inline <script> element(s)"

# 10. The header value must survive the colons inside its own URLs.
run_case "an https: source in the policy does not truncate the value" \
    "script-src 'self' https://cdn.example.com:8443" "$FINGERPRINTED"
expect_line "serves no inline <script>"

# --- Leg 6: the auth pages (#693) ---

AUTH_CSP="content-security-policy: default-src 'self'; frame-ancestors 'none'; script-src 'self'; style-src 'self' 'nonce-Ab_9-z'"
AUTH_OK_HEADERS_FRAME="x-frame-options: DENY"
AUTH_OK_HEADERS_SNIFF="x-content-type-options: nosniff"
AUTH_OK_HEADERS_REFERRER="referrer-policy: no-referrer"
AUTH_PAGE='<html><head><style nonce="Ab_9-z">p{}</style></head><body><script src="/login.js"></script></body></html>'

# 11. The fixed auth origin passes every check on both pages.
run_auth_case "the auth pages with their headers and a nonced style pass" \
    "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
expect_line "auth /Account/Login forbids framing"
expect_line "auth /Account/ConfirmEmail forbids framing"
expect_line "auth /Account/Login sends nosniff and Referrer-Policy 'no-referrer'"
expect_line "auth /Account/Login serves no inline <script>"
expect_line "auth /Account/Login puts the header's nonce on every inline <style>"
reject_line "✗"

# 12. The state before #693: HSTS only. A Development stack warns, --require-csp fails.
run_auth_case "an auth page with no CSP only warns by default" \
    "strict-transport-security: max-age=2592000" -- "$AUTH_PAGE"
expect_line "auth /Account/Login sends no Content-Security-Policy header (expected only on a Development stack)"
run_auth_case "an auth page with no CSP fails under --require-csp" \
    "" -- "$AUTH_PAGE" --require-csp
expect_line "auth /Account/ConfirmEmail sends no Content-Security-Policy header (--require-csp)"

# 13. The accident this ticket replaced: antiforgery's SAMEORIGIN, or no frame header at all.
run_auth_case "SAMEORIGIN is not enough" \
    "$AUTH_CSP" "x-frame-options: SAMEORIGIN" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
expect_line "auth /Account/Login can be framed: X-Frame-Options 'SAMEORIGIN'"
run_auth_case "SAMEORIGIN next to DENY is still a failure" \
    "$AUTH_CSP" "x-frame-options: SAMEORIGIN" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" \
    "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
expect_line "can be framed: X-Frame-Options 'SAMEORIGIN, DENY'"
run_auth_case "a CSP without frame-ancestors fails even with DENY" \
    "content-security-policy: default-src 'self'; style-src 'self' 'nonce-Ab_9-z'" "$AUTH_OK_HEADERS_FRAME" \
    "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
expect_line "auth /Account/Login can be framed"

# 14. nosniff and the referrer policy are part of the set.
run_auth_case "a missing nosniff fails" \
    "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
expect_line "auth /Account/Login misses nosniff or a Referrer-Policy"
run_auth_case "a missing Referrer-Policy fails" \
    "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" -- "$AUTH_PAGE"
expect_line "auth /Account/ConfirmEmail misses nosniff or a Referrer-Policy"

# 15. A style block without the nonce, or with another request's nonce, is blocked.
run_auth_case "a <style> without the nonce fails" \
    "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- \
    '<html><style>p{}</style><STYLE nonce="Ab_9-z">a{}</STYLE></html>'
expect_line "auth /Account/Login serves 1 <style> element(s) without the header's nonce"
run_auth_case "a stale nonce fails" \
    "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- \
    '<html><style nonce="0ld">p{}</style></html>'
expect_line "serves 1 <style> element(s) without the header's nonce"

# 16. 'unsafe-inline' is the defect itself, in either directive.
run_auth_case "style-src 'unsafe-inline' fails" \
    "content-security-policy: frame-ancestors 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'" \
    "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
expect_line "auth /Account/Login style-src allows 'unsafe-inline'"
run_auth_case "script-src 'unsafe-inline' fails" \
    "content-security-policy: frame-ancestors 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'nonce-Ab_9-z'" \
    "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
expect_line "auth /Account/Login script-src allows 'unsafe-inline'"

# 17. The old Login page: the password toggle as an inline script.
run_auth_case "an inline script on an auth page fails" \
    "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- \
    "$(printf '<html><style nonce="Ab_9-z">p{}</style><script>\n(function(){})();</script></html>')"
expect_line "auth /Account/Login serves 1 inline <script> element(s)"

# 18. No style-src and no default-src nonce: any inline style is blocked by default-src.
run_auth_case "an inline style under a nonce-less policy fails" \
    "content-security-policy: default-src 'self'; frame-ancestors 'none'" \
    "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
expect_line "auth /Account/Login serves 1 inline <style> element(s), which its own style-src blocks"

# 19. An empty body proves nothing, so it is a failure, not a pass.
run_auth_case "an auth page with no HTML fails" \
    "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- ""
expect_line "auth /Account/Login served no HTML page"
reject_line "puts the header's nonce"

printf 'All %d smoke CSP case(s) passed.\n' "$cases"
