#!/usr/bin/env bash
# Test suite for the smoke test's own checks: the CSP checks of leg 2 on the frontend (#670) and
# leg 6 on the auth pages (#693, #833), and the discovery links of leg 7 (#898).
#
# These legs decide whether a deploy is rolled back, and scripts/smoke.sh is otherwise INERT to
# CI (no build, no unit test touches it), so without this file nothing checks them before
# they run against live production.
#
# A stub `curl` on PATH serves canned headers and a canned body, so every branch is pinned
# without a server: one pair for the auth origin, another for everything else, and one body per
# discovery root. The other legs fail against the dummy URLs, so the assertions are on each leg's
# own output lines rather than on the overall exit code — which is 1 in every case here for
# unrelated reasons.
# When pwsh is available (it is on the ubuntu runners), every case runs again against smoke.ps1,
# with Invoke-WebRequest replaced by a function that serves the same canned files.
# CI runs this right before pr-verify's other guard tests.

set -euo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

STUB_DIR="$TMP_ROOT/bin"
mkdir -p "$STUB_DIR"
cat > "$STUB_DIR/curl" <<'STUB'
#!/usr/bin/env bash
# Minimal curl stand-in: -w prints a status code, -D writes the canned headers, the body
# goes to -o when given and to stdout otherwise. -L follows a redirecting root, as curl would.
# Everything else is accepted and ignored.
body_target=""
header_target=""
want_code=0
url=""
sent_headers=""
follow=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        -o) body_target="$2"; shift 2 ;;
        -D) header_target="$2"; shift 2 ;;
        -w) want_code=1; shift 2 ;;
        -H) sent_headers="$sent_headers|$2|"; shift 2 ;;
        -L|--location) follow=1; shift ;;
        --max-time|--data-urlencode|-X) shift 2 ;;
        https://*) url="$1"; shift ;;
        *) shift ;;
    esac
done
headers="$STUB_HEADERS"
body="$STUB_BODY"
code=200
case "$url" in
    https://tms.invalid/|https://auth.invalid/)
        # Like the real roots, links come only to a client that asks for the HATEOAS media type.
        code="$STUB_ROOT_CODE"
        case "$code" in
            3??) [ "$follow" -eq 1 ] && code=200 ;;
        esac
        case "$sent_headers" in
            *"|Accept: application/vnd.dev-lotrokoniecdev.hateoas.json|"*)
                body="$STUB_TMS_ROOT"
                [ "$url" = "https://auth.invalid/" ] && body="$STUB_AUTH_ROOT"
                ;;
            *) body="$STUB_PLAIN_ROOT" ;;
        esac
        ;;
    https://auth.invalid/*.css) headers="$STUB_AUTH_CSS_HEADERS"; body="$STUB_AUTH_CSS_BODY"; code="$STUB_AUTH_CSS_CODE" ;;
    https://auth.invalid/*) headers="$STUB_AUTH_HEADERS"; body="$STUB_AUTH_BODY" ;;
esac
[ -n "$header_target" ] && cat "$headers" > "$header_target"
if [ -n "$body_target" ]; then
    cat "$body" > "$body_target"
elif [ "$want_code" -eq 0 ]; then
    cat "$body"
fi
[ "$want_code" -eq 1 ] && printf '%s' "$code"
# Like curl: an unreachable host prints 000 and exits non-zero.
[ "$code" = "000" ] && exit 7
exit 0
STUB
chmod +x "$STUB_DIR/curl"

# The same stub for smoke.ps1: a global function wins over the cmdlet of the same name.
cat > "$STUB_DIR/smoke-stub.ps1" <<'STUB'
function global:Invoke-WebRequest {
    param([string]$Uri, $Method, $Headers, $Body, $ContentType, $TimeoutSec, $MaximumRedirection,
          [switch]$SkipHttpErrorCheck, [switch]$SkipCertificateCheck)
    if ($Uri -eq 'https://tms.invalid/' -or $Uri -eq 'https://auth.invalid/') {
        $code = [int]$env:STUB_ROOT_CODE
        # Without MaximumRedirection 0 the real cmdlet follows a redirect.
        if ($code -ge 300 -and $code -lt 400 -and $MaximumRedirection -ne 0) { $code = 200 }
        if ($Headers -and $Headers['Accept'] -ceq 'application/vnd.dev-lotrokoniecdev.hateoas.json') {
            $root = if ($Uri -eq 'https://auth.invalid/') { $env:STUB_AUTH_ROOT } else { $env:STUB_TMS_ROOT }
            # The real cmdlet hands a vendor media type over as bytes, not as text.
            return [pscustomobject]@{ StatusCode = $code; Content = [System.IO.File]::ReadAllBytes($root); Headers = @{} }
        }
        return [pscustomobject]@{ StatusCode = $code; Content = [System.IO.File]::ReadAllText($env:STUB_PLAIN_ROOT); Headers = @{} }
    }
    $prefix = if ($Uri -like 'https://auth.invalid/*.css') { 'STUB_AUTH_CSS_' }
              elseif ($Uri -like 'https://auth.invalid/*') { 'STUB_AUTH_' }
              else { 'STUB_' }
    $statusCode = if ($prefix -eq 'STUB_AUTH_CSS_') { [int]$env:STUB_AUTH_CSS_CODE } else { 200 }
    if ($statusCode -eq 0) { throw 'No such host is known.' }
    $responseHeaders = [System.Collections.Generic.Dictionary[string, string[]]]::new()
    foreach ($line in [System.IO.File]::ReadAllLines([Environment]::GetEnvironmentVariable($prefix + 'HEADERS'))) {
        if ($line -match '^([^:]+):\s*(.*)$') {
            $name = $Matches[1]
            $value = $Matches[2]
            if ($responseHeaders.ContainsKey($name)) { $responseHeaders[$name] = $responseHeaders[$name] + $value }
            else { $responseHeaders[$name] = @($value) }
        }
    }
    $content = [System.IO.File]::ReadAllText([Environment]::GetEnvironmentVariable($prefix + 'BODY'))
    return [pscustomobject]@{ StatusCode = $statusCode; Content = $content; Headers = $responseHeaders }
}
& $env:SMOKE_SCRIPT
exit $LASTEXITCODE
STUB

# The discovery roots answer this status, and the smoke is given these two API URLs. A leg-7 case
# may change them for one run.
ROOT_CODE=200
CONFIGURED_TMS_URL=https://tms.invalid
CONFIGURED_AUTH_URL=https://auth.invalid
: > "$TMP_ROOT/tms-root"
: > "$TMP_ROOT/auth-root"
printf '{"name":"plain"}' > "$TMP_ROOT/plain-root"

LAST_OUTPUT=""
cases=0
TWIN=""
FAIL_MARK=""
REQUIRE_LABEL=""
# What the stub answers for /account.css. A case that needs another answer sets these and resets them.
AUTH_CSS_CODE=200
AUTH_CSS_TYPE="text/css"

fail() {
    printf '✗ [%s] %s\n' "$TWIN" "$1"
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
    write_auth_css
    LAST_OUTPUT="$(run_smoke "$@" | sed -n '/\[2\/7\]/,/\[3\/7\]/p' || true)"
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$TWIN" "$desc"
}

write_auth_css() {
    printf 'HTTP/2 %s\r\ncontent-type: %s\r\n' "$AUTH_CSS_CODE" "$AUTH_CSS_TYPE" > "$TMP_ROOT/auth-css-headers"
    printf '.auth-panel{}' > "$TMP_ROOT/auth-css-body"
}

# The only extra flag any case passes is --require-csp; smoke.ps1 gets it as SMOKE_REQUIRE_CSP.
run_smoke() {
    if [ "$TWIN" = "ps1" ]; then
        local require_csp=0
        [ "${1:-}" = "--require-csp" ] && require_csp=1
        STUB_HEADERS="$TMP_ROOT/headers" STUB_BODY="$TMP_ROOT/body" \
            STUB_AUTH_HEADERS="$TMP_ROOT/auth-headers" STUB_AUTH_BODY="$TMP_ROOT/auth-body" \
            STUB_TMS_ROOT="$TMP_ROOT/tms-root" STUB_AUTH_ROOT="$TMP_ROOT/auth-root" \
            STUB_PLAIN_ROOT="$TMP_ROOT/plain-root" STUB_ROOT_CODE="$ROOT_CODE" \
            STUB_AUTH_CSS_HEADERS="$TMP_ROOT/auth-css-headers" STUB_AUTH_CSS_BODY="$TMP_ROOT/auth-css-body" \
            STUB_AUTH_CSS_CODE="$AUTH_CSS_CODE" \
            SMOKE_SCRIPT="$SCRIPTS_DIR/smoke.ps1" SMOKE_REQUIRE_CSP="$require_csp" \
            SMOKE_AUTH_URL="$CONFIGURED_AUTH_URL" SMOKE_TMS_URL="$CONFIGURED_TMS_URL" \
            SMOKE_FRONTEND_URL=https://fe.invalid SMOKE_CLIENT_SECRET=stub-secret SMOKE_TIMEOUT=1 \
            pwsh -NoProfile -File "$STUB_DIR/smoke-stub.ps1" 2>&1
        return
    fi
    PATH="$STUB_DIR:$PATH" STUB_HEADERS="$TMP_ROOT/headers" STUB_BODY="$TMP_ROOT/body" \
        STUB_AUTH_HEADERS="$TMP_ROOT/auth-headers" STUB_AUTH_BODY="$TMP_ROOT/auth-body" \
        STUB_TMS_ROOT="$TMP_ROOT/tms-root" STUB_AUTH_ROOT="$TMP_ROOT/auth-root" \
        STUB_PLAIN_ROOT="$TMP_ROOT/plain-root" STUB_ROOT_CODE="$ROOT_CODE" \
        STUB_AUTH_CSS_HEADERS="$TMP_ROOT/auth-css-headers" STUB_AUTH_CSS_BODY="$TMP_ROOT/auth-css-body" \
        STUB_AUTH_CSS_CODE="$AUTH_CSS_CODE" \
        bash "$SCRIPTS_DIR/smoke.sh" \
            --auth-url "$CONFIGURED_AUTH_URL" --tms-url "$CONFIGURED_TMS_URL" \
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
    write_auth_css
    LAST_OUTPUT="$(run_smoke "$@" | sed -n '/\[6\/7\]/,/\[7\/7\]/p' || true)"
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$TWIN" "$desc"
}

# $1 description, $2 the TMS root body, $3 the auth root body, $4 the status both roots answer with
# (default 200). The frontend and the auth pages get an empty answer, so only leg 7 is read.
run_discovery_case() {
    local desc="$1"
    printf '%s' "$2" > "$TMP_ROOT/tms-root"
    printf '%s' "$3" > "$TMP_ROOT/auth-root"
    ROOT_CODE="${4:-200}"
    : > "$TMP_ROOT/headers"
    : > "$TMP_ROOT/body"
    : > "$TMP_ROOT/auth-headers"
    : > "$TMP_ROOT/auth-body"
    LAST_OUTPUT="$(run_smoke | sed -n '/\[7\/7\]/,/=====/p' || true)"
    ROOT_CODE=200
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$TWIN" "$desc"
}

# $1 description, $2 the TMS URL and $3 the auth URL the smoke is given. A URL leg 7 cannot read is a
# usage problem, so the whole output is read and the exit status must be 2.
run_usage_case() {
    local desc="$1" status=0
    CONFIGURED_TMS_URL="$2"
    CONFIGURED_AUTH_URL="$3"
    LAST_OUTPUT="$(run_smoke)" || status=$?
    CONFIGURED_TMS_URL=https://tms.invalid
    CONFIGURED_AUTH_URL=https://auth.invalid
    if [ "$status" != "2" ]; then
        fail "the smoke should exit 2 (usage problem), not $status" "$LAST_OUTPUT"
    fi
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$TWIN" "$desc"
}

# Echoes a discovery document with one link per href, in the shape both APIs send.
discovery_doc() {
    local links="" href
    for href in "$@"; do
        links="$links${links:+,}{\"href\":\"$href\",\"rel\":\"self\",\"method\":\"GET\"}"
    done
    printf '{"name":"stub","links":[%s]}' "$links"
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

run_suite() {
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
    reject_line "$REQUIRE_LABEL"
    run_case "a missing CSP header fails under --require-csp" "" "$FINGERPRINTED" --require-csp
    expect_line "sends no Content-Security-Policy header $REQUIRE_LABEL"

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

    AUTH_CSP="content-security-policy: default-src 'self'; frame-ancestors 'none'; script-src 'self'; style-src 'self'"
    AUTH_OK_HEADERS_FRAME="x-frame-options: DENY"
    AUTH_OK_HEADERS_SNIFF="x-content-type-options: nosniff"
    AUTH_OK_HEADERS_REFERRER="referrer-policy: no-referrer"
    AUTH_PAGE='<html><head><link rel="stylesheet" href="/account.css" /></head><body><script src="/login.js"></script></body></html>'

    # 11. The fixed auth origin passes every check on both pages.
    run_auth_case "the auth pages with their headers and a linked stylesheet pass" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login forbids framing"
    expect_line "auth /Account/ConfirmEmail forbids framing"
    expect_line "auth /Account/Login sends nosniff and Referrer-Policy 'no-referrer'"
    expect_line "auth /Account/Login serves no inline <script>"
    expect_line "auth /Account/Login serves no inline <style>"
    expect_line "auth /Account/ConfirmEmail serves no inline <style>"
    expect_line "auth /account.css -> 200 text/css"
    expect_line "auth /error-page.css -> 200 text/css"
    reject_line "$FAIL_MARK"

    # 12. The state before #693: HSTS only. A Development stack warns, --require-csp fails.
    run_auth_case "an auth page with no CSP only warns by default" \
        "strict-transport-security: max-age=2592000" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login sends no Content-Security-Policy header (expected only on a Development stack)"
    run_auth_case "an auth page with no CSP fails under --require-csp" \
        "" -- "$AUTH_PAGE" --require-csp
    expect_line "auth /Account/ConfirmEmail sends no Content-Security-Policy header $REQUIRE_LABEL"

    # 13. The accident this ticket replaced: antiforgery's SAMEORIGIN, or no frame header at all.
    run_auth_case "SAMEORIGIN is not enough" \
        "$AUTH_CSP" "x-frame-options: SAMEORIGIN" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login can be framed: X-Frame-Options 'SAMEORIGIN'"
    run_auth_case "SAMEORIGIN next to DENY is still a failure" \
        "$AUTH_CSP" "x-frame-options: SAMEORIGIN" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" \
        "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "can be framed: X-Frame-Options 'SAMEORIGIN, DENY'"
    run_auth_case "a CSP without frame-ancestors fails even with DENY" \
        "content-security-policy: default-src 'self'; style-src 'self'" "$AUTH_OK_HEADERS_FRAME" \
        "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login can be framed"

    # 14. nosniff and the referrer policy are part of the set.
    run_auth_case "a missing nosniff fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login misses nosniff or a Referrer-Policy"
    run_auth_case "a missing Referrer-Policy fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" -- "$AUTH_PAGE"
    expect_line "auth /Account/ConfirmEmail misses nosniff or a Referrer-Policy"
    run_auth_case "a Referrer-Policy value that is not a policy fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "referrer-policy: ok" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login misses nosniff or a Referrer-Policy that keeps the URL private"
    run_auth_case "a Referrer-Policy that sends the full URL everywhere fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "referrer-policy: unsafe-url" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login misses nosniff or a Referrer-Policy that keeps the URL private"
    run_auth_case "the last token of a Referrer-Policy list is the one checked" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "referrer-policy: unsafe-url, strict-origin-when-cross-origin" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login sends nosniff and Referrer-Policy"

    # 15. The styles live in /account.css (#833): any style block is a regression, also one whose
    # nonce the policy would admit.
    run_auth_case "an inline <style> fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- \
        '<html><style>p{}</style><STYLE media="all">a{}</STYLE></html>'
    expect_line "auth /Account/Login serves 2 inline <style> element(s); the styles belong in /account.css"
    run_auth_case "a <style> the policy admits by nonce still fails" \
        "content-security-policy: frame-ancestors 'none'; script-src 'self'; style-src 'self' 'nonce-Ab_9-z'" \
        "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- \
        '<html><style nonce="Ab_9-z">p{}</style></html>'
    expect_line "auth /Account/Login serves 1 inline <style> element(s)"

    # 16. 'unsafe-inline' is the defect itself, in either directive.
    run_auth_case "style-src 'unsafe-inline' fails" \
        "content-security-policy: frame-ancestors 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'" \
        "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login style-src allows 'unsafe-inline'"
    run_auth_case "script-src 'unsafe-inline' fails" \
        "content-security-policy: frame-ancestors 'none'; script-src 'self' 'unsafe-inline'; style-src 'self'" \
        "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login script-src allows 'unsafe-inline'"

    # 17. The old Login page: the password toggle as an inline script.
    run_auth_case "an inline script on an auth page fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- \
        "$(printf '<html><link rel="stylesheet" href="/account.css" /><script>\n(function(){})();</script></html>')"
    expect_line "auth /Account/Login serves 1 inline <script> element(s)"

    # 18. A style tag split over two lines must not slip through, under a policy with no style-src.
    run_auth_case "a style tag split over two lines is still counted" \
        "content-security-policy: default-src 'self'; frame-ancestors 'none'" \
        "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- \
        "$(printf '<html><style\n media="all">p{}</style></html>')"
    expect_line "auth /Account/Login serves 1 inline <style> element(s)"

    # 19. A page whose stylesheet is missing from the image answers 200 and loads unstyled (#833).
    AUTH_CSS_CODE=404
    AUTH_CSS_TYPE="text/html; charset=utf-8"
    run_auth_case "a missing /account.css fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /account.css -> 404 'text/html; charset=utf-8' (expected 200 text/css)"
    AUTH_CSS_CODE=200
    run_auth_case "an /account.css served as HTML fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /account.css -> 200 'text/html; charset=utf-8' (expected 200 text/css)"
    expect_line "auth /error-page.css -> 200 'text/html; charset=utf-8' (expected 200 text/css)"
    AUTH_CSS_TYPE="text/css"
    AUTH_CSS_CODE=000
    run_auth_case "an unreachable stylesheet fails and the leg still ends" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /account.css -> 000"
    expect_line "auth /error-page.css -> 000"
    AUTH_CSS_CODE=200

    # 20. A style-src without 'self' blocks the shared stylesheet, even with no inline style at all.
    run_auth_case "a style-src that does not admit 'self' fails" \
        "content-security-policy: default-src 'self'; frame-ancestors 'none'; script-src 'self'; style-src 'none'" \
        "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login style-src 'none' does not admit 'self', so /account.css is blocked"
    run_auth_case "a CSP with no style-src and no default-src restricts no style" \
        "content-security-policy: frame-ancestors 'none'; script-src 'self'" \
        "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- "$AUTH_PAGE"
    expect_line "auth /Account/Login serves no inline <style>"

    # 21. An empty body proves nothing, so it is a failure, not a pass.
    run_auth_case "an auth page with no HTML fails" \
        "$AUTH_CSP" "$AUTH_OK_HEADERS_FRAME" "$AUTH_OK_HEADERS_SNIFF" "$AUTH_OK_HEADERS_REFERRER" -- ""
    expect_line "auth /Account/Login served no HTML page"
    reject_line "serves no inline <style>"

    # --- Leg 7: discovery links on the configured origin (#898) ---

    TMS_OK="$(discovery_doc https://tms.invalid/ https://tms.invalid/api/v1/progress)"
    AUTH_OK="$(discovery_doc https://auth.invalid/ https://auth.invalid/auth/register)"

    # 20. Both roots link only to their own origin. The stub sends links only for the HATEOAS
    # Accept header, so this also proves the leg asks for them.
    run_discovery_case "discovery links on the configured origins pass" "$TMS_OK" "$AUTH_OK"
    expect_line "tms / sends 2 link(s), all on https://tms.invalid:443"
    expect_line "auth / sends 2 link(s), all on https://auth.invalid:443"
    reject_line "$FAIL_MARK"

    # 21. The drift this leg exists for: http instead of https, on one API only.
    run_discovery_case "an http link fails" \
        "$(discovery_doc https://tms.invalid/ http://tms.invalid/api/v1/progress)" "$AUTH_OK"
    expect_line "tms / sends 1 of 2 link(s) outside https://tms.invalid:443, first http://tms.invalid:80"
    expect_line "auth / sends 2 link(s), all on https://auth.invalid:443"

    # 22. An internal host name, on the other API.
    run_discovery_case "an internal host fails" "$TMS_OK" \
        "$(discovery_doc http://auth-api:8080/ http://auth-api:8080/auth/register)"
    expect_line "tms / sends 2 link(s), all on https://tms.invalid:443"
    expect_line "auth / sends 2 of 2 link(s) outside https://auth.invalid:443, first http://auth-api:8080"

    # 23. The right scheme and host on another port.
    run_discovery_case "another port fails" "$(discovery_doc https://tms.invalid:8443/)" "$AUTH_OK"
    expect_line "tms / sends 1 of 1 link(s) outside https://tms.invalid:443, first https://tms.invalid:8443"

    # 24. A host that only starts like the configured one, and a host hidden behind user info.
    run_discovery_case "a look-alike host fails" "$(discovery_doc https://tms.invalid.example/)" "$AUTH_OK"
    expect_line "tms / sends 1 of 1 link(s) outside https://tms.invalid:443, first https://tms.invalid.example:443"
    run_discovery_case "the host after user info is the one compared" \
        "$(discovery_doc https://tms.invalid@example.invalid/)" "$AUTH_OK"
    expect_line "tms / sends 1 of 1 link(s) outside https://tms.invalid:443, first https://example.invalid:443"

    # 25. Another spelling of the same origin passes, because the frontend compares it that way.
    run_discovery_case "upper case and the default port still pass" \
        "$(discovery_doc https://TMS.invalid:443/ HTTPS://tms.INVALID/api)" "$AUTH_OK"
    expect_line "tms / sends 2 link(s), all on https://tms.invalid:443"
    reject_line "$FAIL_MARK"

    # 26. JSON may escape a slash, and may be spread over lines with spaces around the colon.
    run_discovery_case "an escaped slash is read as a slash" \
        "$(discovery_doc 'https:\/\/tms.invalid\/')" "$AUTH_OK"
    expect_line "tms / sends 1 link(s), all on https://tms.invalid:443"
    run_discovery_case "pretty-printed JSON is read too" \
        "$(printf '{\n  "links": [\n    { "href" : "https://tms.invalid/" },\n    { "href" :\n      "http://tms.invalid/" }\n  ]\n}')" \
        "$AUTH_OK"
    expect_line "tms / sends 1 of 2 link(s) outside https://tms.invalid:443, first http://tms.invalid:80"

    # 27. A relative link, or an empty one, is on no origin.
    run_discovery_case "a relative link fails" "$(discovery_doc https://tms.invalid/ /api/v1/progress)" "$AUTH_OK"
    expect_line "tms / sends 1 of 2 link(s) outside https://tms.invalid:443, first (not an absolute address)"
    run_discovery_case "an empty href fails" "$(discovery_doc '')" "$AUTH_OK"
    expect_line "tms / sends 1 of 1 link(s) outside https://tms.invalid:443, first (not an absolute address)"

    # 28. A root without links proves nothing, so it is a failure, not a pass.
    run_discovery_case "a root with no links fails" '{"name":"stub","links":[]}' "$AUTH_OK"
    expect_line "tms / sends no links with the HATEOAS Accept header"
    expect_line "auth / sends 2 link(s), all on https://auth.invalid:443"

    # 29. A root that does not answer 200 fails before any link is read.
    run_discovery_case "a root that answers 503 fails" "$TMS_OK" "$AUTH_OK" 503
    expect_line "tms / (discovery) -> 503 (expected 200)"
    expect_line "auth / (discovery) -> 503 (expected 200)"
    reject_line "link(s)"

    # 30. A root that redirects fails, because the clients follow no redirect. Both stubs follow it
    # if the leg lets them, so this also proves the leg does not.
    run_discovery_case "a redirecting root fails" "$TMS_OK" "$AUTH_OK" 308
    expect_line "tms / (discovery) -> 308 (expected 200)"
    expect_line "auth / (discovery) -> 308 (expected 200)"

    # 31. Links the strict pattern cannot read are on no origin: a second @, an empty host label, a
    # backslash.
    run_discovery_case "a link with a second @ fails" "$(discovery_doc 'https://evil.invalid@x@tms.invalid/')" "$AUTH_OK"
    expect_line "tms / sends 1 of 1 link(s) outside https://tms.invalid:443, first (not an absolute address)"
    run_discovery_case "a link with an empty host label fails" "$(discovery_doc 'https://tms..invalid/')" "$AUTH_OK"
    expect_line "tms / sends 1 of 1 link(s) outside https://tms.invalid:443, first (not an absolute address)"
    run_discovery_case "a link with a backslash in the host part fails" \
        "$(discovery_doc 'https://evil.invalid\\@tms.invalid/')" "$AUTH_OK"
    expect_line "tms / sends 1 of 1 link(s) outside https://tms.invalid:443, first (not an absolute address)"

    # 32. A configured URL leg 7 cannot read, such as a typo in the on-demand workflow, is a usage
    # problem: exit 2 before any leg runs, so the leg can never skip itself.
    run_usage_case "a TMS URL with no scheme exits 2" tms.invalid https://auth.invalid
    expect_line "'tms.invalid' is not an absolute URL with a plain host name"
    reject_line "[1/7]"
    run_usage_case "an auth URL with a second @ exits 2" https://tms.invalid 'https://a@b@auth.invalid'
    expect_line "'https://a@b@auth.invalid' is not an absolute URL with a plain host name"
}

TWIN="sh"
FAIL_MARK="✗"
REQUIRE_LABEL="(--require-csp)"
run_suite

if command -v pwsh >/dev/null 2>&1; then
    TWIN="ps1"
    FAIL_MARK="[FAIL]"
    REQUIRE_LABEL="(-RequireCsp)"
    run_suite
else
    printf 'i pwsh not found — skipped the smoke.ps1 twin cases.\n'
fi

printf 'All %d smoke case(s) passed.\n' "$cases"
