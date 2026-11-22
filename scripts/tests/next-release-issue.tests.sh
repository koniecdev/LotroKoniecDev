#!/usr/bin/env bash
# Test suite for scripts/ci/next-release-issue.sh (#1440): the "Next release" issue that CD keeps
# up to date after every prod deploy, and its close after a release.
#
# Each case builds a fixture repo with a tag and a few merged PRs. A stub `gh` serves the open issues,
# the pull requests and their closing issues from JSON files, runs the script's --jq expressions
# through the real jq, and logs every write (open, edit, comment, close) to calls.log.
#
# CI runs this in pr-verify (guards) and in ci.yml.
set -euo pipefail

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
ISSUE_SH="$SCRIPTS_DIR/ci/next-release-issue.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

for var in $(git rev-parse --local-env-vars); do
    unset "$var"
done
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=test GIT_AUTHOR_EMAIL=test@test.invalid
export GIT_COMMITTER_NAME=test GIT_COMMITTER_EMAIL=test@test.invalid

command -v jq >/dev/null || { echo 'next-release-issue.tests.sh needs jq' >&2; exit 2; }

REPO='koniecdev/LotroKoniecDev'
MARKER='<!-- lotro-next-release -->'
cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- next-release-issue.sh output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    printf '  --- calls.log ---\n' && sed 's/^/    /' "$STUB/calls.log" 2>/dev/null || true
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Stub gh ------------------------------------------------------------------------------------
#   $STUB/issues.json, or issues.page1.json + issues.page2.json   the open issues (--paginate)
#   $STUB/pulls/<sha>.json, $STUB/graphql/<pr>.json               as in next-release.tests.sh
#   $STUB/calls.log                                               one line per write
#   $STUB/body-<n>.md, $STUB/comments-<n>.md                      what was written to issue <n>
#   $STUB/list-fail, $STUB/write-fail, $STUB/pulls-fail           those calls fail
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
[ "${1-}" = api ] || { echo "stub gh: unexpected '$*'" >&2; exit 1; }
shift
method=GET
path=""
jq_expr=""
paginate=0
title=""
body_file=""
state=""
number=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        -X) method="$2"; shift 2 ;;
        --jq) jq_expr="$2"; shift 2 ;;
        --paginate) paginate=1; shift ;;
        -f | -F)
            case "$2" in
                title=*) title="${2#title=}" ;;
                body=@*) body_file="${2#body=@}" ;;
                state=*) state="${2#state=}" ;;
                number=*) number="${2#number=}" ;;
            esac
            shift 2
            ;;
        -*) echo "stub gh: unexpected flag '$1'" >&2; exit 1 ;;
        *) path="$1"; shift ;;
    esac
done
reply() {
    if [ -n "$jq_expr" ]; then jq -r "$jq_expr" <<< "$1"; else printf '%s\n' "$1"; fi
}
case "$method $path" in
    "GET repos/"*"/issues?"*)
        case "$path" in
            *"creator=github-actions%5Bbot%5D"*) ;;
            *) echo "stub gh: issues listed without the bot creator filter: $path" >&2; exit 1 ;;
        esac
        [ "$paginate" -eq 1 ] || { echo 'stub gh: the issue list must be paginated' >&2; exit 1; }
        [ ! -f "$STUB/list-fail" ] || { echo 'gh: HTTP 502' >&2; exit 1; }
        if [ -f "$STUB/issues.page1.json" ]; then
            reply "$(cat "$STUB/issues.page1.json")"
            reply "$(cat "$STUB/issues.page2.json")"
        else
            reply "$(cat "$STUB/issues.json" 2>/dev/null || echo '[]')"
        fi
        ;;
    "POST repos/"*"/issues")
        [ ! -f "$STUB/write-fail" ] || { echo 'gh: HTTP 502' >&2; exit 1; }
        echo "open ${title}" >> "$STUB/calls.log"
        cp "$body_file" "$STUB/body-500.md"
        reply '{"number": 500}'
        ;;
    "PATCH repos/"*"/issues/"*)
        [ ! -f "$STUB/write-fail" ] || { echo 'gh: HTTP 502' >&2; exit 1; }
        n="${path##*/}"
        if [ -n "$state" ]; then
            echo "close ${n} ${state}" >> "$STUB/calls.log"
        else
            echo "edit ${n} ${title}" >> "$STUB/calls.log"
            cp "$body_file" "$STUB/body-${n}.md"
        fi
        ;;
    "POST repos/"*"/comments")
        n="${path%/comments}"
        n="${n##*/}"
        echo "comment ${n}" >> "$STUB/calls.log"
        cat "$body_file" >> "$STUB/comments-${n}.md"
        ;;
    "GET graphql")
        if [ -f "$STUB/graphql/${number}.fail" ]; then
            echo '{"errors":[{"message":"Resource not accessible by integration"}]}'
            exit 1
        fi
        reply "$(cat "$STUB/graphql/${number}.json" 2>/dev/null || echo '{"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"nodes":[]}}}}}')"
        ;;
    "GET repos/"*"/commits/"*"/pulls")
        [ ! -f "$STUB/pulls-fail" ] || { echo 'gh: HTTP 502' >&2; exit 1; }
        sha="${path#*/commits/}"
        sha="${sha%/pulls}"
        reply "$(cat "$STUB/pulls/${sha}.json" 2>/dev/null || echo '[]')"
        ;;
    *) echo "stub gh: unexpected call '$method $path'" >&2; exit 1 ;;
esac
STUB
chmod +x "$BIN/gh"
export PATH="$BIN:$PATH"

# --- Fixture helpers ----------------------------------------------------------------------------
new_case() {
    CASE="$1"
    FIXTURE="$TMP_ROOT/repo-$cases"
    STUB="$TMP_ROOT/stub-$cases"
    rm -rf "$FIXTURE" "$STUB"
    mkdir -p "$FIXTURE" "$STUB/pulls" "$STUB/graphql"
    : > "$STUB/calls.log"
    git -C "$FIXTURE" init --quiet --initial-branch=main
    commit_file 'C0: init' >/dev/null
    git -C "$FIXTURE" tag -a v0.2.1 -m v0.2.1
    export STUB
}

commit_file() {
    printf '%s\n' "$1" >> "$FIXTURE/log.txt"
    git -C "$FIXTURE" add -A
    git -C "$FIXTURE" commit --quiet -m "$1"
    git -C "$FIXTURE" rev-parse HEAD
}

# merged <pr number> <issue number> <issue title> <labels> — a new commit, its PR and its issue.
merged() {
    local sha
    sha="$(commit_file "PR $1")"
    jq -n --arg sha "$sha" --argjson number "$1" --arg title "PR $1: $3" \
        '[{number: $number, title: $title, merged_at: "2026-11-20T10:00:00Z", merge_commit_sha: $sha}]' \
        > "$STUB/pulls/${sha}.json"
    jq -n --argjson number "$2" --arg title "$3" --arg labels "$4" --arg repo "$REPO" \
        '{data: {repository: {pullRequest: {closingIssuesReferences: {nodes: [{number: $number, title: $title,
          body: "", repository: {nameWithOwner: $repo},
          labels: {nodes: ($labels | split(",") | map({name: .}))}}]}}}}}' \
        > "$STUB/graphql/$1.json"
}

# open_issue <number> <bump> [extra issue json objects...] — the open issues the bot list returns.
open_issue() {
    local number="$1" bump="$2"
    shift 2
    {
        jq -n --argjson number "$number" --arg body "${MARKER}"$'\n'"<!-- next-release-bump: ${bump} -->"$'\nold body' \
            '{number: $number, body: $body}'
        printf '%s\n' "$@"
    } | jq -s '.' > "$STUB/issues.json"
}

short_head() { git -C "$FIXTURE" rev-parse --short=7 HEAD; }

run_issue() {
    LAST_STATUS=0
    LAST_OUTPUT="$( (cd "$FIXTURE" && env GITHUB_REPOSITORY="$REPO" REPO_OWNER=owner-login \
        RUN_URL='https://github.com/koniecdev/LotroKoniecDev/actions/runs/1' "$ISSUE_SH" "$@") 2>&1 )" \
        || LAST_STATUS=$?
}

logged() { grep -qxF -- "$1" "$STUB/calls.log"; }
log_lines() { grep -c . "$STUB/calls.log" || true; }

# --- Cases --------------------------------------------------------------------------------------

new_case 'opens the issue'
merged 101 11 'Show who changed a row' type-feature
run_issue update "sha-$(short_head)"
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
logged 'open Next release: v0.3.0' || fail 'the issue must open with the proposed version in its title'
logged 'comment 500' || fail 'opening the issue must add a comment, so the owner is told'
grep -qF "$MARKER" "$STUB/body-500.md" || fail 'the body must carry the hidden marker'
grep -qF '<!-- next-release-bump: minor -->' "$STUB/body-500.md" || fail 'the body must record the bump'
grep -qF '@owner-login' "$STUB/body-500.md" || fail 'the body must mention the owner from REPO_OWNER'
grep -qF '@owner-login' "$STUB/comments-500.md" || fail 'the comment must mention the owner'
grep -qF -- "- **Prod image:** \`sha-$(short_head)\`" "$STUB/body-500.md" || fail 'the body must name the prod image'
grep -qF -- '- #101 PR 101: Show who changed a row (closes #11)' "$STUB/body-500.md" || fail 'the body must list the change'
grep -qF 'actions/runs/1' "$STUB/body-500.md" || fail 'the body must link the CD run'
pass 'a first feature opens the issue with the marker, the list, the owner and a comment'

new_case 'second deploy, same patch'
merged 102 12 'A fix' type-bug
open_issue 77 patch
run_issue update "sha-$(short_head)"
logged 'edit 77 Next release: v0.2.2' || fail 'the open issue must be edited'
[ "$(log_lines)" -eq 1 ] || fail 'an edit with an unchanged bump must not comment or open another issue'
grep -qF -- '- #102 PR 102: A fix (closes #12)' "$STUB/body-77.md" || fail 'the body must be rebuilt'
pass 'a second deploy edits the same issue and adds no comment'

new_case 'patch to minor'
merged 103 13 'A fix' type-bug
merged 104 14 'A feature' type-feature
open_issue 78 patch
run_issue update "sha-$(short_head)"
logged 'edit 78 Next release: v0.3.0' || fail 'the title must follow the new version'
logged 'comment 78' || fail 'a patch that became a minor must comment'
grep -qF 'went up to v0.3.0' "$STUB/comments-78.md" || fail 'the comment must name the new version'
pass 'the proposed number going from patch to minor adds a comment'

new_case 'minor stays minor'
merged 105 15 'A feature' type-feature
merged 106 16 'Another feature' type-feature
open_issue 79 minor
run_issue update "sha-$(short_head)"
logged 'edit 79 Next release: v0.3.0' || fail 'the issue must be edited'
[ "$(log_lines)" -eq 1 ] || fail 'a minor that stays a minor must not comment'
pass 'a minor that stays a minor adds no comment'

new_case 'desktop changes'
merged 107 43 'M4-03: The app finds the game' type-feature
run_issue update "sha-$(short_head)"
[ "$(log_lines)" -eq 0 ] || fail 'a desktop-only change must open no issue'
merged 108 17 'A feature' type-feature
merged 109 44 'M4-04: Settings' type-feature
open_issue 80 minor
run_issue update "sha-$(short_head)"
logged 'edit 80 Next release: v0.3.0' || fail 'the issue must be edited'
[ "$(log_lines)" -eq 1 ] || fail 'a desktop change must never comment'
grep -qF '### Desktop app (released on its own as desktop-vX.Y.Z)' "$STUB/body-80.md" || fail 'the body must list the desktop group'
pass 'desktop changes open no issue and never comment, but are listed'

new_case 'nothing raises the number'
merged 110 18 'Refactor' type-refactor
run_issue update "sha-$(short_head)"
[ "$(log_lines)" -eq 0 ] || fail 'with no issue open, nothing must be written'
open_issue 81 patch
run_issue update "sha-$(short_head)"
logged 'comment 81' || fail 'the open issue must get a short comment'
logged 'close 81 closed' || fail 'the open issue must be closed'
grep -qF 'since v0.2.1' "$STUB/comments-81.md" || fail 'the comment must name the newest release'
pass 'nothing raising the number closes an open issue with a comment, and does nothing otherwise'

new_case 'ignores issues it does not own'
merged 111 19 'A fix' type-bug
jq -n --arg marker "$MARKER" '[
    {number: 60, body: "a normal issue"},
    {number: 61, body: null},
    {number: 62, body: $marker, pull_request: {url: "x"}}
]' > "$STUB/issues.json"
run_issue update "sha-$(short_head)"
logged 'open Next release: v0.2.2' || fail 'with no marked issue a new one must open'
if grep -qE '^(edit|close|comment) 6[0-2]' "$STUB/calls.log"; then
    fail 'an issue without the marker, or a PR, must never be touched'
fi
pass 'issues without the marker and pull requests are ignored'

new_case 'two marked issues'
merged 112 20 'A fix' type-bug
open_issue 90 patch "$(jq -n --arg body "$MARKER" '{number: 85, body: $body}')"
run_issue update "sha-$(short_head)"
logged 'edit 85 Next release: v0.2.2' || fail 'the oldest marked issue must be edited'
grep -qF '::warning::' <<< "$LAST_OUTPUT" || fail 'two marked issues must warn'
if grep -qE '^edit 90' "$STUB/calls.log"; then
    fail 'only one issue must be edited'
fi
pass 'two marked issues: the oldest is used, with a warning'

new_case 'hand run with another tag'
merged 113 21 'A feature' type-feature
run_issue update 'v0.2.1-hotfix'
[ "$LAST_STATUS" -eq 0 ] || fail "a hand-run tag must not fail the job (got $LAST_STATUS)"
grep -qF '::warning::' <<< "$LAST_OUTPUT" || fail 'a hand-run tag must warn'
[ "$(log_lines)" -eq 0 ] || fail 'a hand-run tag must leave the issue as it is'
pass 'a tag that is not sha-<7 hex> warns and writes nothing'

new_case 'tag of a commit not on main'
merged 114 22 'A feature' type-feature
git -C "$FIXTURE" switch --quiet -c side
side="$(commit_file 'side branch')"
git -C "$FIXTURE" switch --quiet main
run_issue update "sha-${side:0:7}"
[ "$LAST_STATUS" -eq 0 ] || fail "a commit off main must not fail the job (got $LAST_STATUS)"
grep -qF '::warning::' <<< "$LAST_OUTPUT" || fail 'a commit off main must warn'
run_issue update 'sha-0000000'
[ "$LAST_STATUS" -eq 0 ] || fail "an unknown short sha must not fail the job (got $LAST_STATUS)"
[ "$(log_lines)" -eq 0 ] || fail 'nothing must be written for a commit off main'
pass 'a sha-<7 hex> that is not one commit on main warns and writes nothing'

new_case 'close after a release'
open_issue 82 minor
run_issue close v0.3.0 'https://github.com/koniecdev/LotroKoniecDev/releases/tag/v0.3.0'
logged 'comment 82' || fail 'the issue must get a comment'
logged 'close 82 closed' || fail 'the issue must be closed'
grep -qF '[v0.3.0](https://github.com/koniecdev/LotroKoniecDev/releases/tag/v0.3.0)' "$STUB/comments-82.md" \
    || fail 'the comment must link the release'
rm "$STUB/issues.json"
: > "$STUB/calls.log"
run_issue close v0.3.0 'https://example.invalid'
[ "$LAST_STATUS" -eq 0 ] || fail "closing with no open issue must not fail (got $LAST_STATUS)"
[ "$(log_lines)" -eq 0 ] || fail 'with no open issue nothing must be written'
pass 'close mode links the release and closes the issue, and does nothing when none is open'

new_case 'unreadable closing issues in the body'
merged 115 23 'A fix' type-bug
sha="$(commit_file 'PR 116')"
jq -n --arg sha "$sha" '[{number: 116, title: "Bump Stryker", merged_at: "2026-11-20T10:00:00Z", merge_commit_sha: $sha}]' \
    > "$STUB/pulls/${sha}.json"
touch "$STUB/graphql/116.fail"
run_issue update "sha-$(short_head)"
logged 'open Next release: v0.2.2' || fail 'the issue must open from the readable PR'
grep -qF '### Closing issues could not be read' "$STUB/body-500.md" || fail 'the body must show the unreadable group'
grep -qF -- '- #116 Bump Stryker' "$STUB/body-500.md" || fail 'the unreadable PR must be listed'
pass 'a PR whose closing issues cannot be read is shown in its own group'

new_case 'issue list on two pages'
merged 117 24 'A fix' type-bug
jq -n '[{number: 70, body: "unrelated"}]' > "$STUB/issues.page1.json"
jq -n --arg body "${MARKER}"$'\n<!-- next-release-bump: patch -->' '[{number: 71, body: $body}]' > "$STUB/issues.page2.json"
run_issue update "sha-$(short_head)"
logged 'edit 71 Next release: v0.2.2' || fail 'the marked issue on the second page must be found'
pass 'the marked issue is found on the second page of the list'

new_case 'GitHub cannot be read or written'
merged 118 25 'A feature' type-feature
touch "$STUB/list-fail"
run_issue update "sha-$(short_head)"
[ "$LAST_STATUS" -eq 3 ] || fail "an unreadable issue list must exit 3, got $LAST_STATUS"
[ "$(log_lines)" -eq 0 ] || fail 'nothing must be written when the list cannot be read'
run_issue close v0.3.0 'https://example.invalid'
[ "$LAST_STATUS" -eq 3 ] || fail "close with an unreadable list must exit 3 (release.sh warns on it), got $LAST_STATUS"
rm "$STUB/list-fail"
touch "$STUB/write-fail"
run_issue update "sha-$(short_head)"
[ "$LAST_STATUS" -eq 3 ] || fail "a failed write must exit 3, got $LAST_STATUS"
open_issue 83 minor
run_issue close v0.3.0 'https://example.invalid'
[ "$LAST_STATUS" -eq 3 ] || fail "a failed close must exit 3, got $LAST_STATUS"
rm "$STUB/write-fail" "$STUB/issues.json"
: > "$STUB/calls.log"
touch "$STUB/pulls-fail"
run_issue update "sha-$(short_head)"
[ "$LAST_STATUS" -eq 3 ] || fail "a change list that cannot be built must exit 3, got $LAST_STATUS"
[ "$(log_lines)" -eq 0 ] || fail 'nothing must be written without a change list'
pass 'an unreadable list, a failed write or a failed change list exits 3 and writes nothing more'

new_case 'bad arguments'
merged 119 26 'A feature' type-feature
LAST_STATUS=0
LAST_OUTPUT="$( (cd "$FIXTURE" && env GITHUB_REPOSITORY="$REPO" REPO_OWNER='not a login' "$ISSUE_SH" update "sha-$(short_head)") 2>&1 )" \
    || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 2 ] || fail "a REPO_OWNER that is not a login must exit 2, got $LAST_STATUS"
run_issue update
[ "$LAST_STATUS" -eq 2 ] || fail "update without a tag must exit 2, got $LAST_STATUS"
run_issue close v0.3.0
[ "$LAST_STATUS" -eq 2 ] || fail "close without a URL must exit 2, got $LAST_STATUS"
LAST_STATUS=0
LAST_OUTPUT="$( (cd "$FIXTURE" && env -u GITHUB_REPOSITORY "$ISSUE_SH" close v0.3.0 x) 2>&1 )" || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 2 ] || fail "a missing GITHUB_REPOSITORY must exit 2, got $LAST_STATUS"
[ "$(log_lines)" -eq 0 ] || fail 'nothing must be written'
pass 'a bad REPO_OWNER, missing arguments or a missing GITHUB_REPOSITORY exits 2'

printf '\nnext-release-issue.tests.sh: %d cases passed\n' "$cases"
