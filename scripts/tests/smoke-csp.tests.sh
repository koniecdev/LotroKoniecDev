#!/usr/bin/env bash
# Test suite for smoke leg 2's CSP consistency ladder (#670).
#
# Leg 2 decides whether a deploy is rolled back, and scripts/smoke.sh is otherwise INERT to
# CI (no build, no unit test touches it), so without this file nothing checks the ladder
# before it runs against live production.
#
# A stub `curl` on PATH serves canned headers and a canned body, so every branch is pinned
# without a server. The stub only answers what leg 2 needs; the later legs fail against the
# unreachable dummy URLs, so the assertions are on leg 2's own output lines rather than on
# the overall exit code — which is 1 in every case here for unrelated reasons.
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
while [ "$#" -gt 0 ]; do
    case "$1" in
        -o) body_target="$2"; shift 2 ;;
        -D) header_target="$2"; shift 2 ;;
        -w) want_code=1; shift 2 ;;
        --max-time|--data-urlencode|-H|-X) shift 2 ;;
        *) shift ;;
    esac
done
[ -n "$header_target" ] && cat "$STUB_HEADERS" > "$header_target"
if [ -n "$body_target" ]; then
    cat "$STUB_BODY" > "$body_target"
elif [ "$want_code" -eq 0 ]; then
    cat "$STUB_BODY"
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
    LAST_OUTPUT="$(PATH="$STUB_DIR:$PATH" STUB_HEADERS="$TMP_ROOT/headers" STUB_BODY="$TMP_ROOT/body" \
        bash "$SCRIPTS_DIR/smoke.sh" \
            --auth-url https://auth.invalid --tms-url https://tms.invalid \
            --frontend-url https://fe.invalid --client-secret stub-secret \
            --timeout 1 "$@" 2>&1 | sed -n '/\[2\/5\]/,/\[3\/5\]/p' || true)"
    cases=$((cases + 1))
    printf '✓ %s\n' "$desc"
}

expect_line() {
    printf '%s' "$LAST_OUTPUT" | grep -qF "$1" \
        || fail "leg 2 should report '$1'" "$LAST_OUTPUT"
}

reject_line() {
    if printf '%s' "$LAST_OUTPUT" | grep -qF "$1"; then
        fail "leg 2 should NOT report '$1'" "$LAST_OUTPUT"
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

printf 'All %d smoke CSP case(s) passed.\n' "$cases"
