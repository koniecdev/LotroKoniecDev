#!/usr/bin/env bash
# Test suite for the prod promotion's UI change notice (scripts/ci/promotion-ui-notice.sh, #1377).
#
# The script's real input, the GitHub deployments API, exists nowhere in CI, so each case runs it
# inside a throwaway fixture repo with a STUB `gh` first on PATH. The properties that matter:
#
#   * Which files count as UI: a hit must warn, and a near miss (an API file, a test file with a
#     UI-like path, src/FrontendX/) must stay silent.
#   * The baseline is the newest record that EVER reached `success`, found across pages.
#   * When it cannot tell, it says so and asks for a walk; it never reports "no UI change".
#   * It always exits 0: the notice must never fail the prod leg.
#   * The annotation is one workflow command: `%` escaped first, newlines escaped, list capped.
#
# CI runs this in the `guards` job, right next to the other bash gates.

set -euo pipefail

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
NOTICE_SH="$SCRIPTS_DIR/ci/promotion-ui-notice.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

# Git exports GIT_DIR and the like to a hook or a `git rebase -x` command; left in place, they
# would point the fixture repo at the real one.
for var in $(git rev-parse --local-env-vars); do
    unset "$var"
done
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null

cases=0
CASE=""
LAST_STDOUT=""
LAST_STDERR=""
LAST_STATUS=0
SUMMARY="$TMP_ROOT/step_summary"
GH_OUT="$TMP_ROOT/github_output"

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- stdout ---\n%s\n  --- stderr ---\n%s\n' "$LAST_STDOUT" "$LAST_STDERR" | sed 's/^/    /'
    if [ -s "$SUMMARY" ]; then
        printf '  --- step summary ---\n%s\n' "$(cat "$SUMMARY")" | sed 's/^/    /'
    fi
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Fixture repo: a linear history, one kind of change per commit ------------------------------
FIXTURE="$TMP_ROOT/repo"
mkdir -p "$FIXTURE"
git -C "$FIXTURE" init --quiet --initial-branch=main
commit_file() {
    mkdir -p "$FIXTURE/$(dirname "$1")"
    printf '%s\n' "${2:-$1}" > "$FIXTURE/$1"
    git -C "$FIXTURE" add -A
    git -C "$FIXTURE" -c user.name=test -c user.email=test@test.invalid commit --quiet -m "change $1"
    git -C "$FIXTURE" rev-parse HEAD
}
AUTH='src/AuthSystem/LotroKoniecDev.AuthSystem.API'
BASE="$(commit_file README.md)"
FRONTEND="$(commit_file src/Frontend/LotroKoniecDev.Frontend/Components/Pages/Home/Home.razor)"
API_ONLY="$(commit_file src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Features/Translations/Upsert.cs)"
AUTH_FEATURE="$(commit_file "$AUTH/Features/Login.cs")"
AUTH_PAGE="$(commit_file "$AUTH/Pages/Account/Login.cshtml")"
AUTH_STATIC="$(commit_file "$AUTH/wwwroot/account.css")"
AUTH_TEMPLATE="$(commit_file "$AUTH/Services/Emails/Templates/EmailTemplateRenderer.cs")"
AUTH_SENDER="$(commit_file "$AUTH/Services/Emails/AccountConfirmationEmailSender.cs")"
AUTH_PROCESSOR="$(commit_file "$AUTH/Services/Emails/EmailDeliveryProcessor.cs")"
AUTH_ERROR_PAGE="$(commit_file "$AUTH/Middleware/TooManyRequestsPage.cs")"
AUTH_MIDDLEWARE="$(commit_file "$AUTH/Middleware/RequestContextLoggingMiddleware.cs")"
AUTH_CSP="$(commit_file "$AUTH/Middleware/SecurityHeadersMiddleware.cs")"
UI_LIKE_TEST="$(commit_file tests/LotroKoniecDev.Frontend.Tests.Unit/src/Frontend/Pages/HomeTests.cs)"
FRONTEND_X="$(commit_file src/FrontendX/Thing.cs)"
mkdir -p "$FIXTURE/docs"
git -C "$FIXTURE" mv src/Frontend/LotroKoniecDev.Frontend/Components/Pages/Home/Home.razor docs/Home.razor
git -C "$FIXTURE" -c user.name=test -c user.email=test@test.invalid commit --quiet -m 'move Home out'
MOVED_OUT="$(git -C "$FIXTURE" rev-parse HEAD)"
mkdir -p "$FIXTURE/src/Frontend/LotroKoniecDev.Frontend/wwwroot"
for i in $(seq -w 1 24); do
    printf 'x\n' > "$FIXTURE/src/Frontend/LotroKoniecDev.Frontend/wwwroot/file-$i.css"
done
printf 'x\n' > "$FIXTURE/src/Frontend/LotroKoniecDev.Frontend/wwwroot/100%.css"
git -C "$FIXTURE" add -A
git -C "$FIXTURE" -c user.name=test -c user.email=test@test.invalid commit --quiet -m 'many UI files'
MANY="$(git -C "$FIXTURE" rev-parse HEAD)"
mkdir -p "$FIXTURE/src/Frontend/LotroKoniecDev.Frontend/Components/Twenty"
for i in $(seq -w 1 20); do
    printf 'x\n' > "$FIXTURE/src/Frontend/LotroKoniecDev.Frontend/Components/Twenty/part-$i.razor"
done
git -C "$FIXTURE" add -A
git -C "$FIXTURE" -c user.name=test -c user.email=test@test.invalid commit --quiet -m 'exactly twenty UI files'
TWENTY="$(git -C "$FIXTURE" rev-parse HEAD)"
UNRELATED="$(git -C "$FIXTURE" -c user.name=test -c user.email=test@test.invalid \
    commit-tree "$(git -C "$FIXTURE" rev-parse 'HEAD^{tree}')" -m 'unrelated root')"
short_frontend="$(git -C "$FIXTURE" rev-parse --short=7 "$FRONTEND")"

# --- Stub `gh`: serves JSON fixtures, steered by env vars ----------------------------------------
# STUB_GH_FAIL      — every `gh` call dies
# STUB_REPO         — what `gh repo view` answers; STUB_REPO_FAIL makes it die instead
# STUB_CALLS        — every `gh api` URL is appended here
# A --jq filter is applied with jq, as gh applies it, and a filter error exits 1, as gh does.
# STUB_PAGE1/PAGE2  — the deployments list; PAGE2 is served only with --paginate, after PAGE1,
#                     as two concatenated arrays (a resolver that forgot --paginate misses it)
# STUB_STATUSES_DIR — <deployment-id>.json for each statuses call
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
[ -z "${STUB_GH_FAIL:-}" ] || { echo 'stub: gh failed' >&2; exit 1; }
if [ "${1:-}" = repo ] && [ "${2:-}" = view ]; then
    [ -z "${STUB_REPO_FAIL:-}" ] || { echo 'stub: no GitHub remote' >&2; exit 1; }
    printf '%s\n' "${STUB_REPO:-}"
    exit 0
fi
url=""
paginate=""
filter=""
previous=""
for arg in "$@"; do
    [ "$previous" != '--jq' ] || filter="$arg"
    case "$arg" in
        repos/*) url="$arg" ;;
        --paginate) paginate=1 ;;
    esac
    previous="$arg"
done
printf '%s\n' "$url" >> "$STUB_CALLS"
body() {
    case "$url" in
        */deployments/*/statuses*)
            id="${url#*/deployments/}"
            id="${id%%/*}"
            cat "$STUB_STATUSES_DIR/$id.json"
            ;;
        */deployments\?environment=production*)
            cat "$STUB_PAGE1"
            if [ -n "$paginate" ] && [ -s "${STUB_PAGE2:-/nonexistent}" ]; then
                cat "$STUB_PAGE2"
            fi
            ;;
        *)
            echo "stub gh: unexpected call: $*" >&2
            return 1
            ;;
    esac
}
if [ -z "$filter" ]; then
    body
    exit
fi
answer="$(body)" || exit 1
jq -r "$filter" <<< "$answer" || exit 1
STUB
chmod +x "$BIN/gh"
PATH="$BIN:$PATH"
export PATH

pages() {
    printf '%s' "$1" > "$TMP_ROOT/page1.json"
    printf '%s' "${2:-}" > "$TMP_ROOT/page2.json"
}
statuses() {
    mkdir -p "$TMP_ROOT/statuses"
    printf '%s' "$2" > "$TMP_ROOT/statuses/$1.json"
}
# The common shape: the in-flight candidate (no success) on top, the release prod runs below it.
prod_runs() {
    pages "[{\"id\": 2, \"sha\": \"$2\"}, {\"id\": 1, \"sha\": \"$1\"}]"
    statuses 2 '[{"state": "waiting"}]'
    statuses 1 '[{"state": "success"}, {"state": "in_progress"}]'
}

run_notice() {
    : > "$SUMMARY"
    : > "$GH_OUT"
    : > "$TMP_ROOT/calls"
    LAST_STATUS=0
    local err="$TMP_ROOT/stderr"
    LAST_STDOUT="$( (cd "$FIXTURE" && env \
        GITHUB_ACTIONS=true \
        GITHUB_REPOSITORY='koniecdev/LotroKoniecDev' \
        GITHUB_OUTPUT="$GH_OUT" \
        GITHUB_STEP_SUMMARY="$SUMMARY" \
        STUB_PAGE1="$TMP_ROOT/page1.json" \
        STUB_PAGE2="$TMP_ROOT/page2.json" \
        STUB_STATUSES_DIR="$TMP_ROOT/statuses" \
        STUB_CALLS="$TMP_ROOT/calls" \
        "$@" "$NOTICE_SH") 2>"$err" )" || LAST_STATUS=$?
    LAST_STDERR="$(cat "$err")"
}

has_line() { grep -qxF -- "$1" <<< "$LAST_STDOUT"; }
warnings() { grep -c '^::warning' <<< "$LAST_STDOUT" || true; }

expect_ui() {
    CASE="$1"
    prod_runs "$2" "$3"
    run_notice CANDIDATE_SHA="$3"
    [ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
    has_line 'ui_change=yes' || fail 'expected ui_change=yes'
    has_line "baseline=$2" || fail "expected baseline=$2"
    [ "$(warnings)" -eq 1 ] || fail 'expected exactly one ::warning annotation'
    grep -q '^::warning title=This promotion changes the UI::' <<< "$LAST_STDOUT" || fail 'the annotation has the wrong title'
    grep -qF -- "- \`$4\`" "$SUMMARY" || fail "the step summary does not list $4"
    pass "warns for $4"
}

expect_no_ui() {
    CASE="$1"
    prod_runs "$2" "$3"
    run_notice CANDIDATE_SHA="$3"
    [ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
    has_line 'ui_change=no' || fail 'expected ui_change=no'
    has_line "baseline=$2" || fail "expected baseline=$2"
    [ "$(warnings)" -eq 0 ] || fail 'a batch with no UI change must not annotate'
    grep -q 'changes no UI file' "$SUMMARY" || fail 'the step summary does not say "no UI file"'
    pass "$4"
}

expect_unknown() {
    [ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
    has_line 'ui_change=unknown' || fail 'expected ui_change=unknown'
    [ "$(warnings)" -eq 1 ] || fail 'expected exactly one ::warning annotation'
    grep -q '^::warning title=Cannot tell whether this promotion changes the UI::' <<< "$LAST_STDOUT" \
        || fail 'the annotation has the wrong title'
    grep -q 'ask a session for /staging-walk' <<< "$LAST_STDOUT" || fail 'the annotation must still ask for a walk'
    grep -q 'Cannot tell' "$SUMMARY" || fail 'the step summary does not say "Cannot tell"'
    grep -q "$1" "$SUMMARY" || fail "the step summary does not give the reason ($1)"
    pass "$2"
}

# --- Which files count as UI ---------------------------------------------------------------------

expect_ui 'Frontend' "$BASE" "$FRONTEND" 'src/Frontend/LotroKoniecDev.Frontend/Components/Pages/Home/Home.razor'
expect_ui 'auth Razor Page' "$AUTH_FEATURE" "$AUTH_PAGE" "$AUTH/Pages/Account/Login.cshtml"
expect_ui 'auth static file' "$AUTH_PAGE" "$AUTH_STATIC" "$AUTH/wwwroot/account.css"
expect_ui 'auth e-mail template' "$AUTH_STATIC" "$AUTH_TEMPLATE" "$AUTH/Services/Emails/Templates/EmailTemplateRenderer.cs"
expect_ui 'auth e-mail sender' "$AUTH_TEMPLATE" "$AUTH_SENDER" "$AUTH/Services/Emails/AccountConfirmationEmailSender.cs"
expect_ui 'auth error page' "$AUTH_PROCESSOR" "$AUTH_ERROR_PAGE" "$AUTH/Middleware/TooManyRequestsPage.cs"
expect_ui 'auth CSP' "$AUTH_MIDDLEWARE" "$AUTH_CSP" "$AUTH/Middleware/SecurityHeadersMiddleware.cs"
expect_ui 'moved out of a UI folder' "$FRONTEND_X" "$MOVED_OUT" 'src/Frontend/LotroKoniecDev.Frontend/Components/Pages/Home/Home.razor'

expect_no_ui 'API only' "$FRONTEND" "$API_ONLY" 'stays silent for a TMS API change'
expect_no_ui 'auth feature' "$API_ONLY" "$AUTH_FEATURE" 'stays silent for an auth API feature'
expect_no_ui 'auth mail processor' "$AUTH_SENDER" "$AUTH_PROCESSOR" 'stays silent for an e-mail processor that writes no text'
expect_no_ui 'auth middleware' "$AUTH_ERROR_PAGE" "$AUTH_MIDDLEWARE" 'stays silent for middleware that is not a page'
expect_no_ui 'UI-like test path' "$AUTH_CSP" "$UI_LIKE_TEST" 'stays silent for a test file whose path contains src/Frontend/'
expect_no_ui 'src/FrontendX' "$UI_LIKE_TEST" "$FRONTEND_X" 'stays silent for a folder that only starts with "Frontend"'
expect_no_ui 'empty span' "$FRONTEND" "$FRONTEND" 'stays silent when prod already runs the candidate'

CASE='the whole span counts'
prod_runs "$BASE" "$API_ONLY"
run_notice CANDIDATE_SHA="$API_ONLY"
has_line 'ui_change=yes' || fail 'a UI change earlier in the batch must count, not only the last commit'
pass 'diffs the whole batch since the release prod runs, not the last commit'

# --- Finding the release prod runs -------------------------------------------------------------

CASE='the inactive trap'
pages "[{\"id\": 2, \"sha\": \"$FRONTEND\"}, {\"id\": 1, \"sha\": \"$BASE\"}]"
statuses 2 '[{"state": "error"}, {"state": "waiting"}]'
statuses 1 '[{"state": "inactive"}, {"state": "success"}, {"state": "in_progress"}]'
run_notice CANDIDATE_SHA="$FRONTEND"
has_line "baseline=$BASE" || fail 'a success that is now "inactive" is still the release prod ran last'
has_line 'ui_change=yes' || fail 'expected ui_change=yes'
pass 'reads the whole status history: a success later marked inactive still counts'

CASE='newest success wins'
pages "[{\"id\": 3, \"sha\": \"$API_ONLY\"}, {\"id\": 2, \"sha\": \"$FRONTEND\"}, {\"id\": 1, \"sha\": \"$BASE\"}]"
statuses 3 '[{"state": "waiting"}]'
statuses 2 '[{"state": "success"}]'
statuses 1 '[{"state": "success"}]'
run_notice CANDIDATE_SHA="$API_ONLY"
has_line "baseline=$FRONTEND" || fail 'expected the NEWEST successful record as the baseline'
has_line 'ui_change=no' || fail 'expected ui_change=no for the span FRONTEND..API_ONLY'
pass 'takes the newest record that reached success'

CASE='success on page 2'
pages "[{\"id\": 3, \"sha\": \"$API_ONLY\"}, {\"id\": 2, \"sha\": \"$FRONTEND\"}]" "[{\"id\": 1, \"sha\": \"$BASE\"}]"
statuses 3 '[{"state": "waiting"}]'
statuses 2 '[{"state": "error"}]'
statuses 1 '[{"state": "success"}]'
run_notice CANDIDATE_SHA="$API_ONLY"
has_line "baseline=$BASE" || fail 'the last success sat on page 2: the deployments list must be read with --paginate'
has_line 'ui_change=yes' || fail 'expected ui_change=yes'
pass 'pages through the deployments list'

# --- When it cannot tell, it says so -------------------------------------------------------------

CASE='no success ever'
pages "[{\"id\": 2, \"sha\": \"$FRONTEND\"}, {\"id\": 1, \"sha\": \"$BASE\"}]"
statuses 2 '[{"state": "waiting"}]'
statuses 1 '[{"state": "error"}]'
run_notice CANDIDATE_SHA="$FRONTEND"
expect_unknown 'ever reached success' 'no record ever reached success: cannot tell, asks for a walk'

CASE='no records at all'
pages '[]'
run_notice CANDIDATE_SHA="$FRONTEND"
expect_unknown 'ever reached success' 'an empty deployment history: cannot tell, asks for a walk'

CASE='API down'
prod_runs "$BASE" "$FRONTEND"
run_notice STUB_GH_FAIL=1 CANDIDATE_SHA="$FRONTEND"
expect_unknown 'did not answer' 'a dead API: cannot tell, asks for a walk, still exits 0'

CASE='garbled API answer'
pages '{"message": "Bad credentials"}'
run_notice CANDIDATE_SHA="$FRONTEND"
expect_unknown 'did not answer' 'an answer that is not a list: cannot tell'

CASE='statuses call fails'
# The list reads fine, but the newest record's statuses cannot be read. Skipping that record would
# take an older success as prod's release, so this must be "cannot tell", not a baseline.
pages "[{\"id\": 7, \"sha\": \"$API_ONLY\"}, {\"id\": 1, \"sha\": \"$BASE\"}]"
rm -f "$TMP_ROOT/statuses/7.json"
statuses 1 '[{"state": "success"}]'
run_notice CANDIDATE_SHA="$API_ONLY"
expect_unknown 'did not answer' 'a statuses call that fails: cannot tell, never an older success'
has_line 'baseline=' || fail 'expected an empty baseline'

CASE='garbled statuses answer'
pages "[{\"id\": 8, \"sha\": \"$API_ONLY\"}, {\"id\": 1, \"sha\": \"$BASE\"}]"
statuses 8 '{"message": "Not Found"}'
statuses 1 '[{"state": "success"}]'
run_notice CANDIDATE_SHA="$API_ONLY"
expect_unknown 'did not answer' 'a statuses answer that is not a list: cannot tell'
has_line 'baseline=' || fail 'expected an empty baseline'

CASE='baseline outside history'
prod_runs 1111111111111111111111111111111111111111 "$FRONTEND"
run_notice CANDIDATE_SHA="$FRONTEND"
expect_unknown 'is not a commit in this checkout' 'a recorded release that is not in the checkout: cannot tell'
has_line 'baseline=1111111111111111111111111111111111111111' || fail 'the verdict must still name the recorded release'

CASE='unrelated baseline'
prod_runs "$UNRELATED" "$API_ONLY"
run_notice CANDIDATE_SHA="$API_ONLY"
has_line 'ui_change=yes' || fail 'a baseline with no shared history still diffs as two trees'
pass 'compares trees, so a baseline with no shared history still works'

CASE='candidate outside history'
prod_runs "$BASE" "$FRONTEND"
run_notice CANDIDATE_SHA=2222222222222222222222222222222222222222
expect_unknown 'is not a commit in this checkout' 'a candidate that is not in the checkout: cannot tell'

CASE='explicit image tag'
prod_runs "$BASE" "$FRONTEND"
run_notice CANDIDATE_SHA="$API_ONLY" SHORT_SHA="$short_frontend" IMAGE_TAG='1.2.3'
expect_unknown 'explicit image tag' 'a dispatch that rolls another image tag: cannot tell'

CASE='image tag of this commit'
prod_runs "$BASE" "$FRONTEND"
run_notice CANDIDATE_SHA="$FRONTEND" SHORT_SHA="$short_frontend" IMAGE_TAG="sha-$short_frontend"
has_line 'ui_change=yes' || fail 'the image tag of the candidate itself is the normal case'
pass 'an image_tag equal to sha-<short> is the normal case'

# --- The annotation and the outputs --------------------------------------------------------------

CASE='escape and cap'
prod_runs "$MOVED_OUT" "$MANY"
run_notice CANDIDATE_SHA="$MANY"
has_line 'ui_change=yes' || fail 'expected ui_change=yes'
[ "$(warnings)" -eq 1 ] || fail 'a long file list must stay ONE workflow command'
annotation="$(grep '^::warning' <<< "$LAST_STDOUT")"
grep -qF 'wwwroot/100%25.css' <<< "$annotation" || fail 'a "%" in a file name must be escaped as %25'
grep -qF '%250A' <<< "$annotation" && fail '"%" must be escaped BEFORE the newlines, or %0A turns into %250A'
listed="$(grep -o '%0Asrc/' <<< "$annotation" | grep -c '' || true)"
[ "$listed" -eq 20 ] || fail "expected 20 files in the annotation, got $listed"
grep -qF '…and 5 more: see the job summary.' <<< "$annotation" || fail 'expected the "and 5 more" line'
[ "$(grep -c '^- `src/' "$SUMMARY")" -eq 25 ] || fail 'the step summary must list all 25 files'
grep -q 'Not detected:' "$SUMMARY" || fail 'the step summary must name what the notice cannot see'
pass 'one annotation, % escaped first, 20 files listed, the full list in the summary'

CASE='exactly the cap'
prod_runs "$MANY" "$TWENTY"
run_notice CANDIDATE_SHA="$TWENTY"
annotation="$(grep '^::warning' <<< "$LAST_STDOUT")"
listed="$(grep -o '%0Asrc/' <<< "$annotation" | grep -c '' || true)"
[ "$listed" -eq 20 ] || fail "expected all 20 files in the annotation, got $listed"
grep -qF 'more: see the job summary' <<< "$annotation" && fail 'twenty files fit the cap: no "and N more" line'
pass 'twenty files fit in the annotation with no "more" line'

CASE='GITHUB_OUTPUT'
prod_runs "$BASE" "$FRONTEND"
run_notice CANDIDATE_SHA="$FRONTEND"
grep -qx 'ui_change=yes' "$GH_OUT" || fail 'ui_change was not written to GITHUB_OUTPUT'
grep -qx "baseline=$BASE" "$GH_OUT" || fail 'baseline was not written to GITHUB_OUTPUT'
pass 'writes the verdict to GITHUB_OUTPUT'

CASE='local run'
prod_runs "$BASE" "$FRONTEND"
: > "$SUMMARY"
LAST_STATUS=0
LAST_STDOUT="$( (cd "$FIXTURE" && env -u GITHUB_ACTIONS -u GITHUB_STEP_SUMMARY -u GITHUB_OUTPUT \
    GITHUB_REPOSITORY='koniecdev/LotroKoniecDev' \
    STUB_PAGE1="$TMP_ROOT/page1.json" STUB_PAGE2="$TMP_ROOT/page2.json" \
    STUB_STATUSES_DIR="$TMP_ROOT/statuses" STUB_CALLS="$TMP_ROOT/calls" \
    CANDIDATE_SHA="$FRONTEND" "$NOTICE_SH") 2>"$TMP_ROOT/stderr" )" || LAST_STATUS=$?
LAST_STDERR="$(cat "$TMP_ROOT/stderr")"
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
has_line 'ui_change=yes' || fail 'expected ui_change=yes on stdout'
[ "$(warnings)" -eq 0 ] || fail 'a local run must not print workflow commands'
grep -qF 'Home.razor' <<< "$LAST_STDERR" || fail 'a local run must print the file list on stderr'
pass 'a local run prints the verdict on stdout and the details on stderr, no workflow commands'

CASE='repository from gh'
prod_runs "$BASE" "$FRONTEND"
run_notice GITHUB_REPOSITORY= STUB_REPO='someone/fork' CANDIDATE_SHA="$FRONTEND"
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
has_line 'ui_change=yes' || fail 'expected ui_change=yes with the repository from gh repo view'
grep -q '^repos/someone/fork/deployments?' "$TMP_ROOT/calls" || fail 'the deployments API must be asked for the repository gh repo view named'
pass 'without GITHUB_REPOSITORY it asks gh repo view which repository this is'

CASE='repository lookup fails'
prod_runs "$BASE" "$FRONTEND"
run_notice GITHUB_REPOSITORY= STUB_REPO_FAIL=1 CANDIDATE_SHA="$FRONTEND"
expect_unknown 'repository could not be found' 'no repository to ask (no GitHub remote, gh not signed in): cannot tell'

CASE='default candidate'
prod_runs "$BASE" "$FRONTEND"
git -C "$FIXTURE" checkout --quiet "$FRONTEND"
run_notice
has_line 'ui_change=yes' || fail 'with no CANDIDATE_SHA the candidate is HEAD'
git -C "$FIXTURE" checkout --quiet main
pass 'the candidate defaults to HEAD'

printf '\nAll %d promotion-ui-notice cases passed.\n' "$cases"
