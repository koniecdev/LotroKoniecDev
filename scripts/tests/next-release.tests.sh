#!/usr/bin/env bash
# Test suite for scripts/ci/next-release.sh (#1440): the change list and the proposed number that
# both the "Next release" issue and scripts/release.sh show.
#
# Each case builds a small fixture repo with tags, and a stub `gh` serves the pull request of each
# commit and the closing issues of each pull request from JSON files. The stub runs the script's own
# --jq expressions through the real jq, so a wrong expression fails here, not on GitHub.
#
# CI runs this in pr-verify (guards) and in ci.yml.
set -euo pipefail

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
NEXT_RELEASE_SH="$SCRIPTS_DIR/ci/next-release.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

for var in $(git rev-parse --local-env-vars); do
    unset "$var"
done
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=test GIT_AUTHOR_EMAIL=test@test.invalid
export GIT_COMMITTER_NAME=test GIT_COMMITTER_EMAIL=test@test.invalid

command -v jq >/dev/null || { echo 'next-release.tests.sh needs jq' >&2; exit 2; }

REPO='koniecdev/LotroKoniecDev'
cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- next-release.sh output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    if [ -f "$OUT/changes.md" ]; then
        printf '  --- changes.md ---\n' && sed 's/^/    /' "$OUT/changes.md"
    fi
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Stub gh ------------------------------------------------------------------------------------
#   $STUB/pulls/<sha>.json       the commits/<sha>/pulls answer (default: no pull request)
#   $STUB/graphql/<pr>.json      the closing issues of pull request <pr>
#   $STUB/graphql/<pr>.fail      graphql for <pr> exits 1 with a raw error body: "always", or the
#                                number of calls that fail before it works
#   $STUB/rest.fail              every REST call fails
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
[ "${1-}" = api ] || { echo "stub gh: unexpected '$*'" >&2; exit 1; }
shift
path="$1"
shift
jq_expr=""
number=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --jq) jq_expr="$2"; shift 2 ;;
        -F) case "$2" in number=*) number="${2#number=}" ;; esac; shift 2 ;;
        -f) shift 2 ;;
        *) echo "stub gh: unexpected argument '$1'" >&2; exit 1 ;;
    esac
done
case "$path" in
    graphql)
        fail_file="$STUB/graphql/${number}.fail"
        if [ -f "$fail_file" ]; then
            left="$(cat "$fail_file")"
            if [ "$left" = always ] || [ "$left" -gt 0 ]; then
                [ "$left" = always ] || echo $((left - 1)) > "$fail_file"
                echo '{"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"nodes":[null]}}}},"errors":[{"type":"FORBIDDEN","message":"Resource not accessible by integration"}]}'
                echo 'gh: Resource not accessible by integration' >&2
                exit 1
            fi
        fi
        json="$(cat "$STUB/graphql/${number}.json" 2>/dev/null || echo '{"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"nodes":[]}}}}}')"
        ;;
    repos/*/commits/*/pulls)
        [ ! -f "$STUB/rest.fail" ] || { echo 'gh: HTTP 502' >&2; exit 1; }
        sha="${path#*/commits/}"
        sha="${sha%/pulls}"
        json="$(cat "$STUB/pulls/${sha}.json" 2>/dev/null || echo '[]')"
        ;;
    *) echo "stub gh: unexpected path '$path'" >&2; exit 1 ;;
esac
if [ -n "$jq_expr" ]; then
    jq -r "$jq_expr" <<< "$json"
else
    printf '%s\n' "$json"
fi
STUB
chmod +x "$BIN/gh"
export PATH="$BIN:$PATH"

# --- Fixture helpers ----------------------------------------------------------------------------
new_case() {
    CASE="$1"
    FIXTURE="$TMP_ROOT/repo-$cases"
    STUB="$TMP_ROOT/stub-$cases"
    OUT="$TMP_ROOT/out-$cases"
    rm -rf "$FIXTURE" "$STUB" "$OUT"
    mkdir -p "$FIXTURE" "$STUB/pulls" "$STUB/graphql"
    git -C "$FIXTURE" init --quiet --initial-branch=main
    commit_file 'C0: init' >/dev/null
    export STUB
}

commit_file() {
    printf '%s\n' "$1" >> "$FIXTURE/log.txt"
    git -C "$FIXTURE" add -A
    git -C "$FIXTURE" commit --quiet -m "$1"
    git -C "$FIXTURE" rev-parse HEAD
}

tag() {
    git -C "$FIXTURE" tag -a "$1" -m "$1" "${2:-HEAD}"
}

# pr <commit sha> <pr number> <pr title>
pr() {
    jq -n --arg sha "$1" --argjson number "$2" --arg title "$3" \
        '[{number: $number, title: $title, merged_at: "2026-11-20T10:00:00Z", merge_commit_sha: $sha}]' \
        > "$STUB/pulls/$1.json"
}

# issue <number> <title> <labels, comma-separated> <body> [repository]
issue() {
    jq -n --argjson number "$1" --arg title "$2" --arg labels "$3" --arg body "$4" \
        --arg repo "${5:-$REPO}" \
        '{number: $number, title: $title, body: $body, repository: {nameWithOwner: $repo},
          labels: {nodes: ($labels | split(",") | map(select(. != "")) | map({name: .}))}}'
}

# closes <pr number> <issue json>...
closes() {
    local pr_number="$1"
    shift
    printf '%s\n' "$@" | jq -s '{data: {repository: {pullRequest: {closingIssuesReferences: {nodes: .}}}}}' \
        > "$STUB/graphql/${pr_number}.json"
}

run_next() {
    LAST_STATUS=0
    LAST_OUTPUT="$( (cd "$FIXTURE" && env GITHUB_REPOSITORY="$REPO" "$NEXT_RELEASE_SH" "$@") 2>&1 )" \
        || LAST_STATUS=$?
}

has() { grep -qxF -- "$1" <<< "$LAST_OUTPUT"; }
changes_has() { grep -qF -- "$1" "$OUT/changes.md"; }
input_has() { grep -qF -- "$1" "$OUT/notes-input.md"; }

BODY_FEATURE=$'## Problem\n\nTranslators cannot see who changed a row.\n\n## Why\n\nNothing stores it.\n\n## Fix\n\nThe editor shows the last author.\n\n## Tasks\n\n- [ ] do it'

# --- Cases --------------------------------------------------------------------------------------

new_case 'features only'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 101 'Translators cannot see who changed a row'
closes 101 "$(issue 11 'Translators cannot see who changed a row' 'type-feature,priority-medium' "$BODY_FEATURE")"
run_next --out-dir "$OUT" HEAD
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
has 'bump=minor' || fail 'a server feature must propose the next minor'
has 'version=v0.3.0' || fail 'v0.2.1 plus a feature must be v0.3.0'
has 'base_tag=v0.2.1' || fail 'the base must be the newest tag'
has 'features=1' || fail 'one feature PR expected'
grep -qxF 'bump=minor' "$OUT/proposal" || fail 'the proposal file must hold the same lines'
changes_has '### Features' || fail 'the Features group is missing'
changes_has '- #101 Translators cannot see who changed a row (closes #11)' || fail 'the PR line is missing'
input_has 'Closes #11 (type-feature): Translators cannot see who changed a row' || fail 'the notes input must name the issue and its type'
input_has 'Translators cannot see who changed a row.' || fail 'the notes input must carry the Problem section'
input_has 'The editor shows the last author.' || fail 'the notes input must carry the Fix section'
if input_has 'Nothing stores it.' || input_has '- [ ] do it'; then
    fail 'only Problem and Fix go into the notes input'
fi
pass 'a server feature proposes the next minor and lists the PR with its Problem and Fix'

new_case 'fixes only'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 102 'The export drops the last row'
closes 102 "$(issue 12 'The export drops the last row' 'type-bug' '')"
run_next --out-dir "$OUT" HEAD
has 'bump=patch' || fail 'a server bug must propose the next patch'
has 'version=v0.2.2' || fail 'v0.2.1 plus a fix must be v0.2.2'
changes_has '### Fixes' || fail 'the Fixes group is missing'
pass 'a server fix proposes the next patch'

new_case 'features and fixes'
tag v0.2.1
c1="$(commit_file 'C1')"
c2="$(commit_file 'C2')"
pr "$c1" 103 'A bug'
closes 103 "$(issue 13 'A bug' 'type-bug' '')"
pr "$c2" 104 'A feature'
closes 104 "$(issue 14 'A feature' 'type-feature' '')"
run_next --out-dir "$OUT" HEAD
has 'bump=minor' || fail 'a feature next to a fix must propose the minor'
has 'version=v0.3.0' || fail 'expected v0.3.0'
has 'features=1' || fail 'the feature PR must be counted'
has 'fixes=1' || fail 'the fix PR must be counted'
pass 'a feature next to a fix proposes the minor'

new_case 'neither'
tag v0.2.1
c1="$(commit_file 'C1')"
c2="$(commit_file 'C2')"
c3="$(commit_file 'C3')"
c4="$(commit_file 'C4')"
pr "$c1" 105 'Refactor'
closes 105 "$(issue 15 'Refactor' 'type-refactor' '')"
pr "$c2" 106 'Tests'
closes 106 "$(issue 16 'Tests' 'type-test' '')"
pr "$c3" 107 'CI'
closes 107 "$(issue 17 'CI' 'type-infra' '')"
pr "$c4" 108 'Docs'
closes 108 "$(issue 18 'Docs' 'type-docs' '')"
run_next --out-dir "$OUT" HEAD
has 'bump=none' || fail 'refactor, test, infra and docs must never raise the number'
has 'version=' || fail 'no version is proposed'
has 'other=4' || fail 'all four belong to Other'
changes_has '### Other' || fail 'the Other group is missing'
pass 'refactor, test, infra and docs changes propose nothing'

new_case 'PR without a closing issue'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 109 'Part of #50: first half'
run_next --out-dir "$OUT" HEAD
has 'bump=none' || fail 'a PR with no closing issue must never raise the number'
has 'no_issue=1' || fail 'it belongs to the no-closing-issue group'
changes_has '### No closing issue' || fail 'the group heading is missing'
changes_has '- #109 Part of #50: first half' || fail 'the PR line is missing'
pass 'a PR with no closing issue is listed on its own and raises nothing'

new_case 'commit without a PR'
tag v0.2.1
commit_file 'C1: pushed straight to main' >/dev/null
run_next --out-dir "$OUT" HEAD
has 'no_issue=1' || fail 'a commit with no PR belongs to the no-closing-issue group'
changes_has 'C1: pushed straight to main (no pull request)' || fail 'the commit must be listed by its subject'
pass 'a commit with no pull request is listed by its subject'

new_case 'no tag yet'
c1="$(commit_file 'C1')"
pr "$c1" 110 'A feature'
closes 110 "$(issue 20 'A feature' 'type-feature' '')"
run_next --out-dir "$OUT" HEAD
has 'base_tag=' || fail 'there is no base tag'
has 'version=v0.1.0' || fail 'with no tag, a feature must propose v0.1.0'
has 'changes=2' || fail 'with no tag, every commit counts (C0 and C1)'
pass 'with no v* tag the whole history counts'

new_case 'rollback inside the newest tag'
c1="$(commit_file 'C1')"
pr "$c1" 111 'A feature'
closes 111 "$(issue 21 'A feature' 'type-feature' '')"
commit_file 'C2' >/dev/null
tag v0.2.1
run_next --out-dir "$OUT" "$c1"
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
has 'changes=0' || fail 'a commit inside the newest tag has nothing unreleased'
has 'bump=none' || fail 'a rollback proposes nothing'
changes_has 'No changes since v0.2.1.' || fail 'the list must say there is nothing new'
pass 'a commit already inside the newest tag proposes nothing'

new_case 'closing issue in another repository'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 112 'Bump Stryker from 4.0 to 4.1'
closes 112 "$(issue 900 'Mutants survive' 'type-feature' '' 'stryker-mutator/stryker-net')"
run_next --out-dir "$OUT" HEAD
has 'bump=none' || fail 'an issue of another repository must never raise the number'
has 'no_issue=1' || fail 'the PR has no closing issue of this repository'
pass 'a closing issue in another repository is ignored'

new_case 'closing issues cannot be read'
tag v0.2.1
c1="$(commit_file 'C1')"
c2="$(commit_file 'C2')"
pr "$c1" 113 'Bump Stryker'
echo always > "$STUB/graphql/113.fail"
pr "$c2" 114 'A fix'
closes 114 "$(issue 24 'A fix' 'type-bug' '')"
run_next --out-dir "$OUT" HEAD
[ "$LAST_STATUS" -eq 0 ] || fail "an unreadable PR must not stop the script (got $LAST_STATUS)"
has 'unreadable=1' || fail 'the PR belongs to the unreadable group'
has 'bump=patch' || fail 'the number must come from the other PRs'
grep -qF '::warning::' <<< "$LAST_OUTPUT" || fail 'a warning must be printed'
changes_has '### Closing issues could not be read' || fail 'the group heading is missing'
changes_has '- #113 Bump Stryker' || fail 'the PR line is missing'
pass 'a PR whose closing issues cannot be read gets its own group and a warning'

new_case 'closing issues readable on the second try'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 115 'A feature'
closes 115 "$(issue 25 'A feature' 'type-feature' '')"
echo 1 > "$STUB/graphql/115.fail"
run_next --out-dir "$OUT" HEAD
has 'unreadable=0' || fail 'one failure must be retried'
has 'bump=minor' || fail 'the retry must classify the PR normally'
pass 'one failed read is retried'

new_case 'desktop feature only'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 116 'M4-03: The app finds the game'
closes 116 "$(issue 43 'M4-03: The app finds the game' 'type-feature' '')"
run_next --out-dir "$OUT" HEAD
has 'bump=none' || fail 'a desktop feature must never raise the server number'
has 'desktop=1' || fail 'it belongs to the desktop group'
changes_has '### Desktop app (released on its own as desktop-vX.Y.Z)' || fail 'the desktop group heading is missing'
changes_has '- #116 M4-03: The app finds the game (closes #43)' || fail 'the PR line is missing'
pass 'a desktop feature is listed in its own group and raises nothing'

new_case 'server bug and desktop feature in one PR'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 117 'Two fixes in one'
closes 117 "$(issue 30 'The export drops a row' 'type-bug' '')" "$(issue 31 'M4-15: The window keeps its height' 'type-feature' '')"
run_next --out-dir "$OUT" HEAD
has 'bump=patch' || fail 'the number must follow the server issue only'
has 'fixes=1' || fail 'the PR belongs to Fixes'
has 'desktop=1' || fail 'its desktop issue belongs to Desktop'
changes_has '- #117 Two fixes in one (closes #30)' || fail 'the Fixes line must name only the server issue'
changes_has '- #117 Two fixes in one (closes #31)' || fail 'the Desktop line must name the desktop issue'
pass 'a mixed PR is a fix, and its desktop issue is still listed under Desktop'

new_case 'desktop tags are never the base'
tag v0.2.1
commit_file 'C1' >/dev/null
tag desktop-v0.9.0
c2="$(commit_file 'C2')"
pr "$c2" 118 'A fix'
closes 118 "$(issue 32 'A fix' 'type-bug' '')"
run_next --out-dir "$OUT" HEAD
has 'base_tag=v0.2.1' || fail 'a desktop-v tag must never be the base'
has 'changes=2' || fail 'both commits after v0.2.1 count'
has 'version=v0.2.2' || fail 'expected v0.2.2'
pass 'a desktop-v tag is never the base'

new_case 'minor after nine'
tag v0.9.3
commit_file 'C1' >/dev/null
tag v0.10.0-rc1
c2="$(commit_file 'C2')"
pr "$c2" 119 'A feature'
closes 119 "$(issue 33 'A feature' 'type-feature' '')"
run_next --out-dir "$OUT" HEAD
has 'base_tag=v0.9.3' || fail 'a tag with a suffix is not a release'
has 'version=v0.10.0' || fail 'v0.9.3 plus a feature must be v0.10.0, never v1.0.0'
pass 'the minor after v0.9.x is v0.10.0, and a suffixed tag is ignored'

new_case 'issue without Problem or Fix'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 120 'Old ticket'
closes 120 "$(issue 34 'Old ticket' 'type-bug' 'Just a line of text.')"
run_next --out-dir "$OUT" HEAD
input_has 'Closes #34 (type-bug): Old ticket' || fail 'the title must still be there'
if input_has 'Problem:' || input_has 'Just a line of text.'; then
    fail 'an issue with no Problem or Fix section gives its title only'
fi
pass 'an issue with no Problem or Fix section gives its title only'

new_case 'issue without labels'
tag v0.2.1
c1="$(commit_file 'C1')"
pr "$c1" 121 'Unlabelled ticket'
closes 121 "$(issue 35 'Unlabelled ticket' '' "$BODY_FEATURE")"
run_next --out-dir "$OUT" HEAD
has 'other=1' || fail 'an issue with no labels belongs to Other'
input_has 'Closes #35 (no type label): Unlabelled ticket' || fail 'the notes input must say the issue has no type label'
input_has 'The editor shows the last author.' || fail 'an empty label field must not shift the body away'
pass 'an issue with no labels keeps its body (no field shift)'

new_case 'bad input'
run_next HEAD
[ "$LAST_STATUS" -eq 2 ] || fail "a missing --out-dir must exit 2, got $LAST_STATUS"
run_next --out-dir "$OUT" 0000000000000000000000000000000000000000
[ "$LAST_STATUS" -eq 2 ] || fail "an unknown commit must exit 2, got $LAST_STATUS"
LAST_STATUS=0
LAST_OUTPUT="$( (cd "$FIXTURE" && env -u GITHUB_REPOSITORY "$NEXT_RELEASE_SH" --out-dir "$OUT" HEAD) 2>&1 )" || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 2 ] || fail "a missing GITHUB_REPOSITORY must exit 2, got $LAST_STATUS"
pass 'bad input exits 2'

new_case 'GitHub cannot be read'
tag v0.2.1
commit_file 'C1' >/dev/null
touch "$STUB/rest.fail"
run_next --out-dir "$OUT" HEAD
[ "$LAST_STATUS" -eq 3 ] || fail "a REST failure must exit 3, got $LAST_STATUS"
pass 'a pull request that cannot be read exits 3'

printf '\nnext-release.tests.sh: %d cases passed\n' "$cases"
