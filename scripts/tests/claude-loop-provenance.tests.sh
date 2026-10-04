#!/usr/bin/env bash
# Test suite for the backlog loop's issue-provenance gate (AUDIT-SEC-08, ADR-0026).
#
# The gate is what stops attacker-authored text on this PUBLIC repo from becoming the task of an
# agent that pushes branches and opens PRs. It is enforced in two places and both are covered here:
#   * issue-trust.sh  — the policy (author + every commenter must carry write access)
#   * next-ticket.sh  — the picker never returns an untrusted ticket
#   * work-ticket.sh  — an explicitly-named untrusted ticket never spawns a claude session
# It also covers the rule that keeps the loop from working a ticket twice now that it stops at the
# PR (ADR-0060): a ticket with an open PR, or with a worktree already on disk, is never started.
# And it covers the worker's worktree lifecycle: the session runs in its own worktree cut from
# origin/main, the main checkout is never touched, a finished worktree is removed or salvaged, a
# stop signal ends the session and its children, and only a real open PR for the ticket counts.
# A session that ends normally without a STATUS line is resumed a capped number of times (#925).
# A usage limit keeps the worktree, and the next run resumes the session in it; a DONE with no
# open PR is resumed once to open it; the picker does not hide a worktree kept that way (#934).
#
# `gh` is stubbed from fixtures, so the suite is offline and hermetic. The stub applies the
# caller's own `--jq` filter with real jq, which keeps the scripts' jq filters under test too.
# CI runs this before anything else that could rot the gate silently.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
TRUST="$SCRIPTS_DIR/claude/issue-trust.sh"

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

# The worker's own defaults are under test, not the caller's environment. That includes the
# maintainer's ~/.claude/model-policy.env, which the worker reads before its defaults.
unset BASH_MAX_TIMEOUT_MS BASH_DEFAULT_TIMEOUT_MS CLAUDE_CODE_DISABLE_BACKGROUND_TASKS LOOP_MAX_RESUMES \
    LOOP_KEEP_WORKTREE LOOP_TICKET_TIMEOUT_MIN
export HOME="$TMP_ROOT/home"
mkdir -p "$HOME"
# The ticket clock when LOOP_TICKET_TIMEOUT_MIN is unset (#953).
DEFAULT_TIMEOUT_MIN=240
# A resume after a usage limit looks for the session's transcript in the worker's config dir; the
# developer's real one must never be read.
export LOOP_CONFIG_DIR="$TMP_ROOT/config"

export GH_FIXTURES="$TMP_ROOT/fixtures"
CLAUDE_MARKER="$TMP_ROOT/claude-was-spawned"
mkdir -p "$GH_FIXTURES" "$TMP_ROOT/bin"

# work-ticket.sh creates worktrees and branches, and next-ticket.sh looks at the worktrees on disk;
# exercise both against a throwaway repo, never the developer's own working copy — a regression
# must not be able to touch someone's checkout just because they ran the tests.
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=provenance-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=provenance-tests GIT_COMMITTER_EMAIL=tests@localhost
FAKE_REPO="$TMP_ROOT/fake-repo"
WORK="$FAKE_REPO/scripts/claude/work-ticket.sh"
mkdir -p "$FAKE_REPO/scripts/claude"
NEXT="$FAKE_REPO/scripts/claude/next-ticket.sh"
cp "$SCRIPTS_DIR/claude/work-ticket.sh" "$SCRIPTS_DIR/claude/issue-trust.sh" "$SCRIPTS_DIR/claude/next-ticket.sh" \
    "$FAKE_REPO/scripts/claude/"
chmod +x "$FAKE_REPO/scripts/claude/"*.sh
git -C "$FAKE_REPO" init -q -b main
git -C "$FAKE_REPO" add -A
git -C "$FAKE_REPO" commit -qm "provenance-gate fixture repo"
# A local bare "origin", so work-ticket.sh can fetch origin/main and cut a worktree from it.
git init -q --bare "$TMP_ROOT/origin.git"
git -C "$FAKE_REPO" remote add origin "$TMP_ROOT/origin.git"
git -C "$FAKE_REPO" push -q origin main

LAST_OUTPUT=""
LAST_STDOUT=""
cases=0

fail() {
    printf '✗ %s\n' "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    exit 1
}

# ── Offline `gh` + `claude` stubs ──────────────────────────────────────────────────────────────
cat > "$TMP_ROOT/bin/gh" <<'STUB'
#!/usr/bin/env bash
# Minimal offline `gh`: serves $GH_FIXTURES and applies the caller's --jq filter with real jq.
#
# It models `--paginate` faithfully — comments live in `comments-<n>.json` (page 1) plus optional
# `comments-<n>-p2.json` … , and only a caller that passes --paginate sees past page 1, exactly as
# real `gh` only follows Link headers when asked. That is what makes "a hostile comment on page 2"
# a test the gate can fail: drop --paginate from issue-trust.sh and the case goes red.
set -o pipefail

filter=""
paginate=0
state="open"
args=()
while [ $# -gt 0 ]; do
    case "$1" in
        --jq) filter="${2:-}"; shift 2 ;;
        --paginate) paginate=1; shift ;;
        --state) state="${2:-}"; shift 2 ;;
        *) args+=("$1"); shift ;;
    esac
done

emit_file() {
    if [ -n "$filter" ]; then jq -r "$filter" < "$1"; else cat "$1"; fi
}

emit() {
    if [ ! -f "$1" ]; then
        echo "gh: Not Found (HTTP 404)" >&2
        exit 1
    fi
    emit_file "$1"
}

case "${args[0]:-}" in
    api)
        path="${args[1]:-}"
        case "$path" in
            */comments)
                number="${path%/comments}"
                number="${number##*/}"
                emit "$GH_FIXTURES/comments-$number.json"
                if [ "$paginate" = "1" ]; then
                    for page in "$GH_FIXTURES/comments-$number-p"*.json; do
                        if [ -f "$page" ]; then emit_file "$page"; fi
                    done
                fi
                ;;
            *)
                emit "$GH_FIXTURES/issue-${path##*/}.json"
                ;;
        esac
        ;;
    issue)
        case "${args[1]:-}" in
            list) emit "$GH_FIXTURES/issue-list.json" ;;
            view) emit "$GH_FIXTURES/issue-${args[2]:-}.json" ;;
            *) echo "gh stub: unsupported issue subcommand" >&2; exit 1 ;;
        esac
        ;;
    pr)
        case "${args[1]:-}" in
            list)
                if [ -f "$GH_FIXTURES/pr-list-fail.json" ]; then
                    echo "gh: HTTP 502" >&2
                    exit 1
                fi
                # Like real `gh`, `pr list` returns open PRs unless asked for `--state all`; a fixture
                # without a state is an open PR.
                payload='[]'
                [ ! -f "$GH_FIXTURES/pr-list.json" ] || payload="$(cat "$GH_FIXTURES/pr-list.json")"
                if [ "$state" != "all" ]; then
                    payload="$(jq --arg state "$(printf '%s' "$state" | tr '[:lower:]' '[:upper:]')" \
                        'map(select((.state // "OPEN") == $state))' <<< "$payload")"
                fi
                if [ -n "$filter" ]; then jq -r "$filter" <<< "$payload"; else printf '%s' "$payload"; fi
                ;;
            view) emit "$GH_FIXTURES/pr-view-${args[2]:-}.json" ;;
            *) echo "gh stub: unsupported pr subcommand" >&2; exit 1 ;;
        esac
        ;;
    *) echo "gh stub: unsupported command" >&2; exit 1 ;;
esac
exit 0
STUB

cat > "$TMP_ROOT/bin/claude" <<STUB
#!/usr/bin/env bash
# The worker session must never start for an untrusted ticket — leave proof if it does.
touch "$CLAUDE_MARKER"
# Each run leaves its arguments, its background switches and its folder, so a case can compare a
# resume with the first run.
run=\$(( \$(cat "$TMP_ROOT/claude-runs" 2>/dev/null || echo 0) + 1 ))
echo "\$run" > "$TMP_ROOT/claude-runs"
printf '%s\n' "\$@" > "$TMP_ROOT/claude-args-\$run"
echo "\${CLAUDE_CODE_DISABLE_BACKGROUND_TASKS:-unset} \${BASH_MAX_TIMEOUT_MS:-unset} \${BASH_DEFAULT_TIMEOUT_MS:-unset}" \
    > "$TMP_ROOT/claude-env-\$run"
pwd -P > "$TMP_ROOT/claude-cwd-\$run"
# The lifecycle cases script what the session does in its worktree.
if [ -n "\${CLAUDE_BEHAVIOR:-}" ]; then exec "\$CLAUDE_BEHAVIOR" "\$@"; fi
echo '{"result":"STATUS: DONE","is_error":false}'
STUB

# Never touch the real Docker daemon: the worker removes a finished worktree's E2E images.
cat > "$TMP_ROOT/bin/docker" <<STUB
#!/usr/bin/env bash
echo "docker \$*" >> "$TMP_ROOT/docker-calls"
if [ "\$1 \$2" = "image ls" ] && [ -f "$TMP_ROOT/docker-images" ]; then cat "$TMP_ROOT/docker-images"; fi
exit 0
STUB

# work-ticket.sh polls its session every 30 seconds; a test has no reason to wait that long.
REAL_SLEEP="$(command -v sleep)"
printf '#!/usr/bin/env bash\nexec "%s" 0.05\n' "$REAL_SLEEP" > "$TMP_ROOT/bin/sleep"

chmod +x "$TMP_ROOT/bin/gh" "$TMP_ROOT/bin/claude" "$TMP_ROOT/bin/docker" "$TMP_ROOT/bin/sleep"
export PATH="$TMP_ROOT/bin:$PATH"

command -v jq >/dev/null 2>&1 || fail "this suite needs jq on PATH"

# ── Fixture helpers ────────────────────────────────────────────────────────────────────────────
# fixture_issue <number> <login> <association> [state] [body]
# `association` may be the literal `null` to model a missing association; `login` may be the
# literal `null` to model a deleted (ghost) account.
fixture_issue() {
    local number="$1" login="$2" association="$3" state="${4:-OPEN}" body="${5:-}"
    local association_json="\"$association\"" user_json="{ \"login\": \"$login\" }"
    [ "$association" = "null" ] && association_json="null"
    [ "$login" = "null" ] && user_json="null"
    cat > "$GH_FIXTURES/issue-$number.json" <<EOF
{
  "number": $number,
  "state": "$state",
  "user": $user_json,
  "author_association": $association_json,
  "body": "$body"
}
EOF
    printf '[]' > "$GH_FIXTURES/comments-$number.json"
}

# write_comments <file> <login:association> ...
write_comments() {
    local file="$1"
    shift
    local json="[" separator=""
    for writer in "$@"; do
        json="$json$separator{\"user\":{\"login\":\"${writer%%:*}\"},\"author_association\":\"${writer##*:}\"}"
        separator=","
    done
    printf '%s]' "$json" > "$file"
}

# fixture_comments <number> <login:association> ...              — page 1
fixture_comments() {
    local number="$1"
    shift
    write_comments "$GH_FIXTURES/comments-$number.json" "$@"
}

# fixture_comments_page2 <number> <login:association> ...        — only a --paginate caller sees it
fixture_comments_page2() {
    local number="$1"
    shift
    write_comments "$GH_FIXTURES/comments-$number-p2.json" "$@"
}

# fixture_list <number:label,label> ... — builds the `gh issue list` payload, lowest number first.
fixture_list() {
    local json="[" separator=""
    for entry in "$@"; do
        local number="${entry%%:*}" labels="${entry#*:}" labels_json="" label_separator=""
        [ "$labels" = "$number" ] && labels=""
        local IFS=,
        for label in $labels; do
            labels_json="$labels_json$label_separator{\"name\":\"$label\"}"
            label_separator=","
        done
        unset IFS
        json="$json$separator{\"number\":$number,\"title\":\"T$number: fixture\",\"labels\":[$labels_json]}"
        separator=","
    done
    printf '%s]' "$json" > "$GH_FIXTURES/issue-list.json"
}

# fixture_prs <number:headRefName[:fork]> ... — builds the `gh pr list` payload of open PRs.
fixture_prs() {
    local json="[" separator="" rest head fork
    for entry in "$@"; do
        rest="${entry#*:}"; head="${rest%%:*}"; fork=false
        [ "$rest" != "$head" ] && fork=true
        json="$json$separator{\"number\":${entry%%:*},\"headRefName\":\"$head\",\"isCrossRepository\":$fork}"
        separator=","
    done
    printf '%s]' "$json" > "$GH_FIXTURES/pr-list.json"
}

# fixture_prs_state <number,headRefName,state[,headRefOid[,ended]]> ... — PRs in every state, as a
# resume reads them. A merged or closed PR ends now unless `ended` (ISO 8601) says otherwise; an
# open PR's head is a commit nobody has unless `headRefOid` names one.
fixture_prs_state() {
    local json="[" separator="" number head state oid ended closed merged
    for entry in "$@"; do
        IFS=, read -r number head state oid ended <<< "$entry"
        oid="${oid:-0000000000000000000000000000000000000000}"
        closed=null
        merged=null
        if [ "$state" != "OPEN" ]; then
            closed="\"${ended:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}\""
            [ "$state" != "MERGED" ] || merged="$closed"
        fi
        json="$json$separator{\"number\":$number,\"headRefName\":\"$head\",\"headRefOid\":\"$oid\",\"isCrossRepository\":false,\"state\":\"$state\",\"closedAt\":$closed,\"mergedAt\":$merged}"
        separator=","
    done
    printf '%s]' "$json" > "$GH_FIXTURES/pr-list.json"
}

# fixture_transcript <session id> — the transcript `claude --resume` needs in the worker's config dir.
fixture_transcript() {
    mkdir -p "$LOOP_CONFIG_DIR/projects/fixture"
    : > "$LOOP_CONFIG_DIR/projects/fixture/$1.jsonl"
}

# fixture_pr_view <number> <state> <headRefName> [fork] — what `gh pr view <number>` returns.
fixture_pr_view() {
    local fork=false
    [ "${4:-}" = "fork" ] && fork=true
    printf '{"number":%s,"state":"%s","headRefName":"%s","isCrossRepository":%s}' "$1" "$2" "$3" "$fork" \
        > "$GH_FIXTURES/pr-view-$1.json"
}

reset_fixtures() {
    rm -f "$GH_FIXTURES"/*.json "$CLAUDE_MARKER" "$TMP_ROOT"/claude-runs "$TMP_ROOT"/claude-args-* \
        "$TMP_ROOT"/claude-env-* "$TMP_ROOT"/claude-cwd-*
    rm -rf "$LOOP_CONFIG_DIR"
}

# ── Assertions ─────────────────────────────────────────────────────────────────────────────────
# run_case <expected-exit> <description> <command...>
run_case() {
    local expected="$1" description="$2"
    shift 2
    local rc=0
    LAST_STDOUT="$("$@" 2>"$TMP_ROOT/stderr.txt")" || rc=$?
    LAST_OUTPUT="$LAST_STDOUT$(printf '\n')$(cat "$TMP_ROOT/stderr.txt")"
    if [ "$rc" -ne "$expected" ]; then
        fail "$description — expected exit $expected, got $rc" "$LAST_OUTPUT"
    fi
    cases=$((cases + 1))
    printf '✓ %s\n' "$description"
}

expect_in_output() {
    printf '%s' "$LAST_OUTPUT" | grep -qF "$1" \
        || fail "output should contain '$1'" "$LAST_OUTPUT"
}

# expect_meta <ticket> <key=value>... — the last line of a key wins, as in the conductor's table.
expect_meta() {
    local meta="$TMP_ROOT/run/ticket-$1.meta" pair
    shift
    for pair in "$@"; do
        [ "$(sed -n "s/^${pair%%=*}=//p" "$meta" | tail -1)" = "${pair#*=}" ] \
            || fail "meta should end with $pair" "$(cat "$meta")"
    done
}

expect_stdout() {
    [ "$LAST_STDOUT" = "$1" ] \
        || fail "stdout should be '$1' but was '$LAST_STDOUT'" "$LAST_OUTPUT"
}

# ── issue-trust.sh: the policy ─────────────────────────────────────────────────────────────────
for association in OWNER MEMBER COLLABORATOR; do
    reset_fixtures
    fixture_issue 10 maintainer "$association"
    run_case 0 "issue-trust: $association author is trusted" "$TRUST" 10
    expect_in_output "trusted"
done

for association in CONTRIBUTOR FIRST_TIME_CONTRIBUTOR MANNEQUIN NONE; do
    reset_fixtures
    fixture_issue 11 outsider "$association"
    run_case 1 "issue-trust: $association author is refused" "$TRUST" 11
    expect_in_output "REFUSED #11"
    expect_in_output "$association"
done

reset_fixtures
fixture_issue 12 ghost null
run_case 1 "issue-trust: a missing author_association is refused (fail-closed)" "$TRUST" 12
expect_in_output "<none>"

# The comment channel: `/work-ticket` treats later comments as overriding the body, and on a
# public repo anyone may comment on a maintainer's issue.
reset_fixtures
fixture_issue 13 maintainer OWNER
fixture_comments 13 maintainer:OWNER outsider:NONE
run_case 1 "issue-trust: an outsider comment on a maintainer issue is refused" "$TRUST" 13
expect_in_output "comment #2"
expect_in_output "outsider"

reset_fixtures
fixture_issue 14 maintainer OWNER
fixture_comments 14 maintainer:OWNER teammate:COLLABORATOR
run_case 0 "issue-trust: comments by trusted writers keep the ticket trusted" "$TRUST" 14
expect_in_output "2 comment(s)"

# The attack the finding named: bury the hostile comment past the first page. The gate must
# paginate; drop `--paginate` from issue-trust.sh and this case goes red (that is its whole job).
reset_fixtures
fixture_issue 22 maintainer OWNER
fixture_comments 22 maintainer:OWNER teammate:COLLABORATOR
fixture_comments_page2 22 outsider:NONE
run_case 1 "issue-trust: a hostile comment on page 2 is still caught (--paginate is pinned)" "$TRUST" 22
expect_in_output "comment #3"
expect_in_output "outsider"

reset_fixtures
fixture_issue 23 maintainer OWNER
fixture_comments 23 maintainer:OWNER
fixture_comments_page2 23 teammate:MEMBER
run_case 0 "issue-trust: trusted writers across both comment pages stay trusted" "$TRUST" 23
expect_in_output "2 comment(s)"

# A deleted account leaves `user: null`. Its association must never be read as the login.
reset_fixtures
fixture_issue 24 null MEMBER
run_case 1 "issue-trust: an issue by a ghost account is refused" "$TRUST" 24
expect_in_output "no identifiable author"

# Fail-closed on every API failure — a rate-limited or offline `gh` must never mean "trusted".
reset_fixtures
run_case 2 "issue-trust: an unreadable issue is refused (fail-closed)" "$TRUST" 15
expect_in_output "fail-closed"

reset_fixtures
fixture_issue 16 maintainer OWNER
rm -f "$GH_FIXTURES/comments-16.json"
run_case 2 "issue-trust: unreadable comments are refused (fail-closed)" "$TRUST" 16
expect_in_output "comments of issue #16"

# Knobs.
reset_fixtures
fixture_issue 17 outsider NONE
run_case 0 "issue-trust: LOOP_TRUST_GATE=0 is the human escape hatch" \
    env LOOP_TRUST_GATE=0 "$TRUST" 17
expect_in_output "GATE DISABLED"

reset_fixtures
fixture_issue 18 teammate MEMBER
run_case 1 "issue-trust: LOOP_TRUSTED_ASSOCIATIONS can narrow the allowlist" \
    env LOOP_TRUSTED_ASSOCIATIONS=OWNER "$TRUST" 18

reset_fixtures
fixture_issue 19 maintainer OWNER
run_case 0 "issue-trust: a lower-case allowlist is normalized" \
    env LOOP_TRUSTED_ASSOCIATIONS=owner,member "$TRUST" 19

reset_fixtures
fixture_issue 20 release-bot NONE
run_case 0 "issue-trust: LOOP_TRUSTED_LOGINS admits a named bot/second account" \
    env LOOP_TRUSTED_LOGINS=release-bot "$TRUST" 20

# A trailing comma must not turn the empty association into a trusted one.
reset_fixtures
fixture_issue 21 ghost null
run_case 1 "issue-trust: a trailing comma in the allowlist admits no empty association" \
    env LOOP_TRUSTED_ASSOCIATIONS=OWNER, "$TRUST" 21

run_case 2 "issue-trust: a non-numeric issue argument is a usage error" "$TRUST" "42; rm -rf /"

# The shape guard must sit above the escape hatch — an env var must not be able to switch it off.
run_case 2 "issue-trust: LOOP_TRUST_GATE=0 does not disable the argument guard" \
    env LOOP_TRUST_GATE=0 "$TRUST" "42; rm -rf /"

run_case 3 "work-ticket: a non-numeric issue argument is rejected" "$WORK" "1 2" "$TMP_ROOT/run"

# ── next-ticket.sh: the picker never selects untrusted work ────────────────────────────────────
picker() { env LOOP_SKIP_ISSUES= LOOP_SKIP_TITLES= "$NEXT" "$@"; }

reset_fixtures
fixture_list 30:priority-high
fixture_issue 30 outsider NONE
run_case 1 "next-ticket: an externally-authored issue is never returned" picker
expect_stdout ""

reset_fixtures
fixture_list 31:priority-high
fixture_issue 31 maintainer OWNER
run_case 0 "next-ticket: a maintainer-authored issue is still returned" picker
expect_stdout "31"

# Priority must not outrank provenance: the attacker's `priority-critical` ticket sorts first and loses.
reset_fixtures
fixture_list 32:priority-critical 33:priority-low
fixture_issue 32 outsider NONE
fixture_issue 33 maintainer OWNER
run_case 0 "next-ticket: a critical untrusted issue never outranks a low trusted one" picker
expect_stdout "33"

reset_fixtures
fixture_list 34:priority-high
fixture_issue 34 maintainer OWNER
fixture_comments 34 outsider:NONE
run_case 1 "next-ticket: an outsider comment disqualifies a maintainer ticket" picker
expect_stdout ""

# `audit` findings are triaged by a human before the loop may touch them.
reset_fixtures
fixture_list 35:priority-medium,audit
fixture_issue 35 maintainer OWNER
run_case 1 "next-ticket: audit findings are skipped by default" picker
expect_stdout ""

run_case 0 "next-ticket: the audit skip is a default, not a hard block" \
    env LOOP_SKIP_ISSUES= LOOP_SKIP_TITLES= LOOP_SKIP_LABELS=loop-blocked "$NEXT"
expect_stdout "35"

# The dependency gate still works behind the provenance gate.
reset_fixtures
fixture_list 36:priority-high
fixture_issue 36 maintainer OWNER OPEN "Depends on #37"
fixture_issue 37 maintainer OWNER OPEN
run_case 1 "next-ticket: an open dependency still blocks a trusted ticket" picker
expect_stdout ""

reset_fixtures
fixture_list 38:priority-high
fixture_issue 38 maintainer OWNER OPEN "Depends on #39"
fixture_issue 39 maintainer OWNER CLOSED
run_case 0 "next-ticket: a closed dependency releases a trusted ticket" picker
expect_stdout "38"

# The loop stops at the PR (ADR-0060), so a worked ticket stays open until its PR merges.
reset_fixtures
fixture_list 50:priority-high 51:priority-low
fixture_issue 50 maintainer OWNER
fixture_issue 51 maintainer OWNER
fixture_prs 900:50-already-worked
run_case 0 "next-ticket: a ticket with an open PR is skipped" picker
expect_stdout "51"

# The branch prefix is matched whole: a PR for #50 must not hide #5, and a PR for #150 must not
# hide #50.
reset_fixtures
fixture_list 5:priority-high
fixture_issue 5 maintainer OWNER
fixture_prs 900:50-other-ticket
run_case 0 "next-ticket: a PR for #50 does not hide #5" picker
expect_stdout "5"

reset_fixtures
fixture_list 50:priority-high
fixture_issue 50 maintainer OWNER
fixture_prs 900:150-other-ticket
run_case 0 "next-ticket: a PR for #150 does not hide #50" picker
expect_stdout "50"

# A fork may name its branch after our ticket; that PR is not ours and hides nothing.
reset_fixtures
fixture_list 53:priority-high
fixture_issue 53 maintainer OWNER
fixture_prs 900:53-from-a-fork:fork
run_case 0 "next-ticket: a fork's PR does not hide a ticket" picker
expect_stdout "53"

reset_fixtures
fixture_list 54:priority-high 55:priority-low
fixture_issue 54 maintainer OWNER
fixture_issue 55 maintainer OWNER
mkdir -p "$FAKE_REPO/.claude/worktrees/ticket-54"
run_case 0 "next-ticket: a ticket whose worktree exists is skipped" picker
expect_stdout "55"
rm -rf "$FAKE_REPO/.claude/worktrees/ticket-54"

# A worktree kept after a usage limit waits for a resume, even when its session opened the PR first
# (#934). A plain folder is not a worktree of its own: git resolves it to the main .git, and a
# marker there must not count.
reset_fixtures
fixture_list 56:priority-high 57:priority-low
fixture_issue 56 maintainer OWNER
fixture_issue 57 maintainer OWNER
fixture_prs 900:56-limit-hit-after-the-pr
git -C "$FAKE_REPO" worktree add -q --detach "$FAKE_REPO/.claude/worktrees/ticket-56" origin/main
marker_56="$(git -C "$FAKE_REPO/.claude/worktrees/ticket-56" rev-parse --path-format=absolute --git-dir)/loop-resume"
echo "session=s-56" > "$marker_56"
run_case 0 "next-ticket: a worktree kept for a resume does not hide its ticket" picker
expect_stdout "56"
# An empty marker names no session: the worker would skip the ticket, so the picker does too.
: > "$marker_56"
run_case 0 "next-ticket: an empty marker does not count as a worktree kept for a resume" picker
expect_stdout "57"
git -C "$FAKE_REPO" worktree remove --force "$FAKE_REPO/.claude/worktrees/ticket-56"
rm -f "$GH_FIXTURES/pr-list.json"
mkdir -p "$FAKE_REPO/.claude/worktrees/ticket-56"
: > "$FAKE_REPO/.git/loop-resume"
run_case 0 "next-ticket: a plain folder never counts as a worktree kept for a resume" picker
expect_stdout "57"
rm -rf "$FAKE_REPO/.claude/worktrees/ticket-56" "$FAKE_REPO/.git/loop-resume"

reset_fixtures
fixture_list 52:priority-high
fixture_issue 52 maintainer OWNER
printf 'x' > "$GH_FIXTURES/pr-list-fail.json"
run_case 1 "next-ticket: an unreadable PR list picks nothing (fail-closed)" picker
expect_stdout ""
expect_in_output "cannot list open pull requests"

# ── work-ticket.sh: naming a ticket explicitly cannot bypass the gate ──────────────────────────
reset_fixtures
fixture_issue 40 outsider NONE
run_case 11 "work-ticket: an explicitly-named untrusted ticket exits 11" "$WORK" 40 "$TMP_ROOT/run"
expect_in_output "REFUSED"
[ ! -f "$CLAUDE_MARKER" ] || fail "work-ticket spawned a claude session for an untrusted ticket"
cases=$((cases + 1))
printf '✓ work-ticket: no claude session is spawned for an untrusted ticket\n'

# A ticket already in flight is skipped before any worktree or session exists.
reset_fixtures
fixture_issue 60 maintainer OWNER
fixture_prs 901:60-already-worked
run_case 12 "work-ticket: a ticket with an open PR exits 12" "$WORK" 60 "$TMP_ROOT/run"
expect_in_output "PR #901 is already open"
[ ! -f "$CLAUDE_MARKER" ] || fail "work-ticket spawned a claude session for a ticket that already has a PR"

# The folder is not a worktree of its own, so the resume marker git resolves for it is the main
# .git's: it must not make the run resume anything (#934).
reset_fixtures
fixture_issue 61 maintainer OWNER
mkdir -p "$FAKE_REPO/.claude/worktrees/ticket-61"
printf 'session=s-61\nhead=%s\n' "$(git -C "$FAKE_REPO" rev-parse HEAD)" > "$FAKE_REPO/.git/loop-resume"
fixture_transcript s-61
run_case 12 "work-ticket: a ticket whose worktree exists exits 12" "$WORK" 61 "$TMP_ROOT/run"
expect_in_output "already exists"
[ ! -f "$CLAUDE_MARKER" ] || fail "work-ticket spawned a claude session next to an existing worktree"
rm -rf "$FAKE_REPO/.claude/worktrees/ticket-61" "$FAKE_REPO/.git/loop-resume"

# An unreadable API is systemic, not a property of the ticket: exit 3 so the conductor's
# circuit breaker stops the run rather than "skipping" every remaining ticket as untrusted.
reset_fixtures
run_case 3 "work-ticket: an unverifiable ticket is an error, not a skip" "$WORK" 41 "$TMP_ROOT/run"
expect_in_output "could not verify"
[ ! -f "$CLAUDE_MARKER" ] || fail "work-ticket spawned a claude session for an unverifiable ticket"

# ── work-ticket.sh: the worktree lifecycle ─────────────────────────────────────────────────────
# behavior <file> <shell body> — a fake session. It notes where it runs and which commit it starts
# from, then runs the body, which prints the result JSON.
behavior() {
    printf '#!/usr/bin/env bash\nset -e\npwd -P > "%s/session-cwd"\ngit rev-parse HEAD > "%s/session-start"\n%s\n' \
        "$TMP_ROOT" "$TMP_ROOT" "$2" > "$1"
    chmod +x "$1"
}
WT_ROOT="$FAKE_REPO/.claude/worktrees"
export REAL_SLEEP

# A process that has exited but was never reaped still answers `kill -0`; in a container whose PID 1
# reaps nothing, an orphan stays that way. Such a zombie is dead for these tests.
alive() {
    kill -0 "$1" 2>/dev/null || return 1
    case "$(ps -o stat= -p "$1" 2>/dev/null)" in Z*|"") return 1 ;; esac
    return 0
}

# watchdogs <session leader> <session child> — the members of an ended session's group other than
# its child, leaving out any process whose parent is in the group too, like a short command the
# watchdog runs. After the session has ended, a case expects exactly one: the watchdog.
watchdogs() {
    ps -A -o pid= -o ppid= -o pgid= | awk -v g="$1" -v c="$2" \
        '$3 == g { member[$1] = 1; parent[$1] = $2 } END { for (p in member) if (p != c && !(parent[p] in member)) print p }'
}

# The main checkout is dirty and sits on another branch, and its local `main` carries a commit
# origin does not have: the loop must cut every worktree from origin/main and touch nothing here.
git -C "$FAKE_REPO" update-ref refs/heads/main \
    "$(git -C "$FAKE_REPO" commit-tree -p main -m "local-only commit" "main^{tree}")"
git -C "$FAKE_REPO" checkout -q -b someone-elses-work
echo "work in progress" > "$FAKE_REPO/dirty.txt"

# The fake session names its branch after its ticket and reports PR 7<ticket>.
behavior "$TMP_ROOT/done.sh" 'ticket="${PWD##*ticket-}"
git checkout -q -B "$ticket-fixture"
git commit -q --allow-empty -m "fixture work"
echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7$ticket\\nSUMMARY: the rate limiter now sends 429 with Retry-After; quota and usage limits unchanged\",\"is_error\":false,\"session_id\":\"s-done\"}"'

reset_fixtures
fixture_issue 70 maintainer OWNER
fixture_pr_view 770 OPEN 70-fixture
printf '%s\n' "lotrokoniecdev-auth:fe-e2e-ticket-70-hash" "lotrokoniecdev-auth:fe-e2e-ticket-700-hash" \
    > "$TMP_ROOT/docker-images"
: > "$TMP_ROOT/docker-calls"
run_case 0 "work-ticket: DONE opens the PR from its own worktree" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 70 "$TMP_ROOT/run"
expect_in_output "PR #770 opened"
[ "$(cat "$TMP_ROOT/session-cwd")" = "$(cd "$FAKE_REPO" && pwd -P)/.claude/worktrees/ticket-70" ] \
    || fail "the session should run in .claude/worktrees/ticket-70" "$(cat "$TMP_ROOT/session-cwd")"
[ "$(cat "$TMP_ROOT/session-start")" = "$(git -C "$FAKE_REPO" rev-parse origin/main)" ] \
    || fail "the worktree should start at origin/main, not at the local main"
grep -qx "outcome=pr-opened" "$TMP_ROOT/run/ticket-70.meta" || fail "meta should say pr-opened" "$(cat "$TMP_ROOT/run/ticket-70.meta")"
grep -qx "pr=770" "$TMP_ROOT/run/ticket-70.meta" || fail "meta should carry the PR" "$(cat "$TMP_ROOT/run/ticket-70.meta")"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "a DONE session must not be resumed" "$LAST_OUTPUT"
expect_meta 70 resumes=0
[ ! -e "$WT_ROOT/ticket-70" ] || fail "a clean worktree should be removed"
git -C "$FAKE_REPO" rev-parse -q --verify 70-fixture >/dev/null || fail "the ticket branch must stay"
grep -qx "docker image rm lotrokoniecdev-auth:fe-e2e-ticket-70-hash" "$TMP_ROOT/docker-calls" \
    || fail "the worktree's E2E images should be removed" "$(cat "$TMP_ROOT/docker-calls")"
! grep -q "ticket-700" "$TMP_ROOT/docker-calls" || fail "#700's images must stay" "$(cat "$TMP_ROOT/docker-calls")"
[ "$(git -C "$FAKE_REPO" branch --show-current)" = "someone-elses-work" ] || fail "the main checkout's branch changed"
[ -f "$FAKE_REPO/dirty.txt" ] || fail "the main checkout's work in progress is gone"
cases=$((cases + 1)); printf '✓ work-ticket: the main checkout is untouched and the finished worktree is removed\n'
cases=$((cases + 1)); printf '✓ work-ticket: a summary that talks about rate limits is not a usage limit\n'

reset_fixtures
fixture_issue 71 maintainer OWNER
behavior "$TMP_ROOT/blocked.sh" 'git checkout -q -b 71-fixture
echo "half done" > leftover.txt
echo "{\"result\":\"STATUS: BLOCKED\\nCATEGORY: business-questions\",\"is_error\":false,\"session_id\":\"s-71\"}"'
run_case 2 "work-ticket: BLOCKED exits 2" env CLAUDE_BEHAVIOR="$TMP_ROOT/blocked.sh" "$WORK" 71 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "a BLOCKED session asks questions and must not be resumed" "$LAST_OUTPUT"
salvage_branch="$(git -C "$FAKE_REPO" for-each-ref --format='%(refname:short)' 'refs/heads/loop-salvage/71-*')"
[ -n "$salvage_branch" ] || fail "leftovers should be committed on a loop-salvage branch" "$LAST_OUTPUT"
git -C "$FAKE_REPO" show "$salvage_branch:leftover.txt" >/dev/null 2>&1 || fail "the salvage branch should hold the leftover file"
git -C "$FAKE_REPO" show 71-fixture:leftover.txt >/dev/null 2>&1 && fail "the salvage commit must not land on the ticket branch"
[ ! -e "$WT_ROOT/ticket-71" ] || fail "a salvaged worktree is clean, so it should be removed"
cases=$((cases + 1)); printf '✓ work-ticket: leftovers are salvaged on their own branch before the worktree goes\n'

# A commit made while still detached is on no branch; removing the worktree would drop it.
reset_fixtures
fixture_issue 72 maintainer OWNER
behavior "$TMP_ROOT/detached.sh" 'echo "work" > detached.txt
git add detached.txt
git commit -q -m "made while detached"
echo "{\"result\":\"STATUS: BLOCKED\\nCATEGORY: red-build\",\"is_error\":false}"'
run_case 2 "work-ticket: a session that committed while detached" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/detached.sh" "$WORK" 72 "$TMP_ROOT/run"
salvage_branch="$(git -C "$FAKE_REPO" for-each-ref --format='%(refname:short)' 'refs/heads/loop-salvage/72-*')"
[ -n "$salvage_branch" ] && git -C "$FAKE_REPO" show "$salvage_branch:detached.txt" >/dev/null 2>&1 \
    || fail "a commit on no branch should get a salvage branch" "$LAST_OUTPUT"
cases=$((cases + 1)); printf '✓ work-ticket: commits made on no branch survive the worktree removal\n'

# A rebase stopped on a conflict must be left exactly as it is: a commit on top would bury it.
reset_fixtures
fixture_issue 73 maintainer OWNER
behavior "$TMP_ROOT/rebase.sh" 'git checkout -q -b 73-one
echo one > conflict.txt; git add conflict.txt; git commit -q -m one
git checkout -q -b 73-two HEAD~1
echo two > conflict.txt; git add conflict.txt; git commit -q -m two
git rebase 73-one >/dev/null 2>&1 || true
echo "{\"result\":\"crashed mid-rebase\",\"is_error\":true}"'
run_case 3 "work-ticket: a session that died mid-rebase is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/rebase.sh" "$WORK" 73 "$TMP_ROOT/run"
expect_in_output "half done"
[ -d "$WT_ROOT/ticket-73" ] || fail "a worktree with a rebase in progress must be kept"
[ -z "$(git -C "$FAKE_REPO" for-each-ref 'refs/heads/loop-salvage/73-*')" ] || fail "nothing may be committed on top of a stopped rebase"
git -C "$WT_ROOT/ticket-73" rebase --abort
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-73"
cases=$((cases + 1)); printf '✓ work-ticket: a stopped rebase is left for a human\n'

reset_fixtures
fixture_issue 74 maintainer OWNER
behavior "$TMP_ROOT/garbage.sh" 'echo "{\"result\":\"Review done: the rate limit and quota checks look right.\",\"is_error\":false}"'
run_case 3 "work-ticket: a final message without a STATUS block is an error, even one about rate limits" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/garbage.sh" "$WORK" 74 "$TMP_ROOT/run"
[ ! -e "$WT_ROOT/ticket-74" ] || fail "the worktree should be removed after an error too"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "a result with no session id cannot be resumed"
expect_meta 74 resumes=0

reset_fixtures
fixture_issue 75 maintainer OWNER
behavior "$TMP_ROOT/limit.sh" 'echo "{\"result\":\"\",\"is_error\":true,\"api_error_status\":429}"'
run_case 6 "work-ticket: an API 429 is a usage limit" env CLAUDE_BEHAVIOR="$TMP_ROOT/limit.sh" "$WORK" 75 "$TMP_ROOT/run"
grep -qx "outcome=limit" "$TMP_ROOT/run/ticket-75.meta" || fail "meta should say limit"
[ ! -e "$WT_ROOT/ticket-75" ] || fail "the worktree should be removed, so the retry can make it again"

# ── work-ticket.sh: a session that stops without a verdict is resumed (#925) ──────────────────
# The failure these cases pin: the worker starts the test suite in the background and ends its
# turn to wait for it. `claude -p` then exits, so the final message has no STATUS line although
# the work is committed in the worktree.
behavior "$TMP_ROOT/waits.sh" 'case " $* " in
*" --resume "*)
    echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/788\\nSUMMARY: suite green\",\"is_error\":false,\"session_id\":\"s-88\",\"num_turns\":3,\"total_cost_usd\":12.5}" ;;
*)
    echo "first-run stderr" >&2
    git checkout -q -b 88-fixture
    git commit -q --allow-empty -m "reviewed work"
    echo "{\"result\":\"The suite is still running; the task notification will wake me.\",\"is_error\":false,\"session_id\":\"s-88\",\"num_turns\":35,\"total_cost_usd\":11.05}" ;;
esac'

reset_fixtures
fixture_issue 88 maintainer OWNER
fixture_pr_view 788 OPEN 88-fixture
# A usage-limit retry reuses the run folder; an earlier attempt's resume results must not stay.
mkdir -p "$TMP_ROOT/run"
echo stale > "$TMP_ROOT/run/ticket-88.json.before-resume-2"
echo stale > "$TMP_ROOT/run/ticket-88.json.resume-2"
run_case 0 "work-ticket: a session that stopped to wait is resumed and opens its PR" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/waits.sh" "$WORK" 88 "$TMP_ROOT/run"
[ ! -e "$TMP_ROOT/run/ticket-88.json.before-resume-2" ] && [ ! -e "$TMP_ROOT/run/ticket-88.json.resume-2" ] \
    || fail "an earlier attempt's resume results should be cleared"
expect_in_output "timeout=${DEFAULT_TIMEOUT_MIN}m)"
expect_in_output "resuming it (1 of 2)"
expect_in_output "PR #788 opened"
[ "$(cat "$TMP_ROOT/claude-runs")" = "2" ] || fail "expected the first run and one resume" "$LAST_OUTPUT"
expect_meta 88 outcome=pr-opened pr=788 resumes=1 turns=38 cost=12.5
grep -q "first-run stderr" "$TMP_ROOT/run/ticket-88.stderr" || fail "the resume must not wipe the first run's stderr"
[ "$(sed -n 2p "$TMP_ROOT/claude-args-1")" = "/work-ticket 88" ] || fail "the first run should get /work-ticket 88"
sed -n 2p "$TMP_ROOT/claude-args-2" | grep -q "end with the STATUS: DONE or STATUS: BLOCKED block" \
    || fail "the resume should ask for the STATUS block" "$(sed -n 2p "$TMP_ROOT/claude-args-2")"
sed -n 2p "$TMP_ROOT/claude-args-2" | grep -qE "stops this session in about ($((DEFAULT_TIMEOUT_MIN - 1))|$DEFAULT_TIMEOUT_MIN) minutes" \
    || fail "the resume should say how much of the clock is left" "$(sed -n 2p "$TMP_ROOT/claude-args-2")"
[ "$(tail -2 "$TMP_ROOT/claude-args-2" | tr '\n' ' ')" = "--resume s-88 " ] \
    || fail "the resume should name the session of the first run" "$(cat "$TMP_ROOT/claude-args-2")"
[ "$(sed '1,2d' "$TMP_ROOT/claude-args-1")" = "$(sed '1,2d' "$TMP_ROOT/claude-args-2" | sed '$d' | sed '$d')" ] \
    || fail "the resume should keep every flag of the first run" "$(cat "$TMP_ROOT/claude-args-1" "$TMP_ROOT/claude-args-2")"
[ "$(cat "$TMP_ROOT/claude-cwd-1")" = "$(cd "$FAKE_REPO" && pwd -P)/.claude/worktrees/ticket-88" ] \
    && [ "$(cat "$TMP_ROOT/claude-cwd-2")" = "$(cat "$TMP_ROOT/claude-cwd-1")" ] \
    || fail "both runs should work in .claude/worktrees/ticket-88" "$(cat "$TMP_ROOT"/claude-cwd-*)"
[ "$(cat "$TMP_ROOT/claude-env-1")" = "1 3600000 600000" ] && [ "$(cat "$TMP_ROOT/claude-env-2")" = "1 3600000 600000" ] \
    || fail "every run should get background tasks off and the raised Bash timeouts" "$(cat "$TMP_ROOT"/claude-env-*)"
grep -q "still running" "$TMP_ROOT/run/ticket-88.json.before-resume-1" || fail "the first run's result should be kept"
[ "$(find "$TMP_ROOT/run" -name 'ticket-88*.json' | wc -l | tr -d ' ')" = "1" ] \
    || fail "the conductor adds up ticket-*.json, and a resumed run already reports the whole session's cost"
cases=$((cases + 1)); printf '✓ work-ticket: the resume keeps the flags, the worktree and the session, and the first result\n'

reset_fixtures
fixture_issue 89 maintainer OWNER
behavior "$TMP_ROOT/never-status.sh" 'echo "{\"result\":\"Waiting for the E2E suite.\",\"is_error\":false,\"session_id\":\"s-89\",\"num_turns\":2}"'
run_case 3 "work-ticket: a session that never prints a STATUS line is an error after the resume cap" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/never-status.sh" "$WORK" 89 "$TMP_ROOT/run"
expect_in_output "after 2 resume(s)"
[ "$(cat "$TMP_ROOT/claude-runs")" = "3" ] || fail "expected the first run and two resumes" "$LAST_OUTPUT"
expect_meta 89 outcome=error resumes=2 turns=6

reset_fixtures
fixture_issue 92 maintainer OWNER
run_case 3 "work-ticket: LOOP_MAX_RESUMES=0 turns the resume off" \
    env LOOP_MAX_RESUMES=0 CLAUDE_BEHAVIOR="$TMP_ROOT/never-status.sh" "$WORK" 92 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "no resume was allowed" "$LAST_OUTPUT"
expect_meta 92 resumes=0

reset_fixtures
fixture_issue 93 maintainer OWNER
run_case 3 "work-ticket: a LOOP_MAX_RESUMES that is not a number is refused" \
    env LOOP_MAX_RESUMES=two "$WORK" 93 "$TMP_ROOT/run"
[ ! -f "$CLAUDE_MARKER" ] || fail "no session may start with a broken setting"
expect_meta 93 outcome=error

# A bad clock used to pass the start and break the script's arithmetic 30 seconds into the session.
for bad_clock in 4h 0 090 abc; do
    reset_fixtures
    fixture_issue 124 maintainer OWNER
    run_case 3 "work-ticket: a LOOP_TICKET_TIMEOUT_MIN of '$bad_clock' is refused" \
        env LOOP_TICKET_TIMEOUT_MIN="$bad_clock" "$WORK" 124 "$TMP_ROOT/run"
    expect_in_output "LOOP_TICKET_TIMEOUT_MIN is not a whole number of minutes above zero, without a leading zero: '$bad_clock'"
    [ ! -f "$CLAUDE_MARKER" ] || fail "no session may start with a broken clock ($bad_clock)"
    [ ! -e "$WT_ROOT/ticket-124" ] || fail "a broken clock must not leave a worktree ($bad_clock)"
    expect_meta 124 outcome=error
done

# A resume is a new process that can read the issue again, so the gate runs before it too.
reset_fixtures
fixture_issue 98 maintainer OWNER
behavior "$TMP_ROOT/stranger-comments.sh" 'printf "[{\"user\":{\"login\":\"stranger\"},\"author_association\":\"NONE\"}]" \
    > "$GH_FIXTURES/comments-98.json"
echo "{\"result\":\"The suite is still running.\",\"is_error\":false,\"session_id\":\"s-98\"}"'
run_case 11 "work-ticket: a stranger's comment added during the run blocks the resume" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/stranger-comments.sh" "$WORK" 98 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "an untrusted ticket must not be resumed" "$LAST_OUTPUT"
expect_meta 98 outcome=untrusted resumes=0
[ ! -e "$WT_ROOT/ticket-98" ] || fail "the worktree should be removed"

# The gate fails closed before a resume too: an issue it cannot read is never resumed.
reset_fixtures
fixture_issue 100 maintainer OWNER
behavior "$TMP_ROOT/issue-unreadable.sh" 'rm -f "$GH_FIXTURES/issue-100.json"
echo "{\"result\":\"The suite is still running.\",\"is_error\":false,\"session_id\":\"s-100\"}"'
run_case 3 "work-ticket: an issue the gate cannot read before a resume is not resumed" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/issue-unreadable.sh" "$WORK" 100 "$TMP_ROOT/run"
expect_in_output "could not verify #100 before a resume"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "an unverifiable ticket must not be resumed" "$LAST_OUTPUT"
expect_meta 100 outcome=error resumes=0

reset_fixtures
fixture_issue 90 maintainer OWNER
behavior "$TMP_ROOT/limit-on-resume.sh" 'case " $* " in
*" --resume "*) echo "{\"result\":\"\",\"is_error\":true,\"api_error_status\":429,\"session_id\":\"s-90\"}" ;;
*) echo "{\"result\":\"The suite is still running.\",\"is_error\":false,\"session_id\":\"s-90\"}" ;;
esac'
run_case 6 "work-ticket: a usage limit during a resume is still a usage limit" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-on-resume.sh" "$WORK" 90 "$TMP_ROOT/run"
expect_meta 90 outcome=limit resumes=1 worktree=kept session=s-90
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-90"

# The loop trusts api_error_status 429 even without is_error, so such a stop is never resumed.
reset_fixtures
fixture_issue 94 maintainer OWNER
behavior "$TMP_ROOT/limit-quiet.sh" 'echo "{\"result\":\"\",\"is_error\":false,\"api_error_status\":429,\"session_id\":\"s-94\"}"'
run_case 6 "work-ticket: a 429 without is_error is a usage limit, not a resume" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-quiet.sh" "$WORK" 94 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "a usage limit is resumed by the next run, not by this one" "$LAST_OUTPUT"
expect_meta 94 outcome=limit worktree=kept
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-94"

# ── work-ticket.sh: a usage limit keeps the session for the next run (#934) ────────────────────
# The session commits, leaves a file it has not committed yet, and hits the limit. The worktree
# stays as it is, and the next run of the ticket resumes that session in it.
behavior "$TMP_ROOT/limit-keep.sh" 'ticket="${PWD##*ticket-}"
case " $* " in
*" --resume "*)
    [ -f uncommitted.txt ] || { echo "the resumed session lost its uncommitted file" >&2; exit 1; }
    git add uncommitted.txt
    git commit -q -m "finished after the limit"
    echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7$ticket\",\"is_error\":false,\"session_id\":\"s-$ticket\",\"num_turns\":4,\"total_cost_usd\":9.5}" ;;
*)
    git checkout -q -b "$ticket-fixture"
    git commit -q --allow-empty -m "reviewed work"
    echo "not committed yet" > uncommitted.txt
    echo "You have hit your limit" >&2
    echo "{\"result\":\"You have hit your session limit\",\"is_error\":true,\"session_id\":\"s-$ticket\",\"num_turns\":40,\"total_cost_usd\":7.25}" ;;
esac'

# kept_marker <ticket> — where the worker keeps the session of a worktree kept for a resume.
kept_marker() {
    echo "$(git -C "$WT_ROOT/ticket-$1" rev-parse --path-format=absolute --git-dir)/loop-resume"
}

reset_fixtures
fixture_issue 101 maintainer OWNER
run_case 6 "work-ticket: a usage limit keeps the worktree for a resume" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 101 "$TMP_ROOT/run"
expect_in_output "worktree kept"
expect_meta 101 outcome=limit worktree=kept session=s-101
[ -f "$WT_ROOT/ticket-101/uncommitted.txt" ] || fail "the kept worktree must keep the session's uncommitted file"
[ -z "$(git -C "$FAKE_REPO" for-each-ref 'refs/heads/loop-salvage/101-*')" ] \
    || fail "nothing may be salvaged from a worktree kept for a resume"
grep -qx "session=s-101" "$(kept_marker 101)" || fail "the marker should name the session" "$(cat "$(kept_marker 101)")"
marker_101="$(kept_marker 101)"
# The limit hit after the PR was opened: an open PR from the kept branch is the session's own. A PR
# of the same branch that was closed before the session started is history, not a stop sign.
fixture_prs_state "7101,101-fixture,OPEN,$(git -C "$WT_ROOT/ticket-101" rev-parse 101-fixture)" \
    "7001,101-fixture,CLOSED,,2020-01-01T00:00:00Z" "7002,101-older-attempt,MERGED,,2020-01-01T00:00:00Z"
fixture_pr_view 7101 OPEN 101-fixture
fixture_transcript s-101
run_case 0 "work-ticket: the next run resumes the kept session, which opens its PR" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 101 "$TMP_ROOT/run"
expect_in_output "resuming session s-101 after a usage limit"
expect_in_output "PR #7101 opened"
[ "$(cat "$TMP_ROOT/claude-runs")" = "2" ] || fail "expected the limited run and one resume" "$LAST_OUTPUT"
sed -n 2p "$TMP_ROOT/claude-args-2" | grep -q "The usage limit that stopped this session is over" \
    || fail "the resume should say the limit is over" "$(sed -n 2p "$TMP_ROOT/claude-args-2")"
sed -n 2p "$TMP_ROOT/claude-args-2" | grep -q "end with the STATUS: DONE or STATUS: BLOCKED block" \
    || fail "the resume should ask for the STATUS block" "$(sed -n 2p "$TMP_ROOT/claude-args-2")"
[ "$(tail -2 "$TMP_ROOT/claude-args-2" | tr '\n' ' ')" = "--resume s-101 " ] \
    || fail "the resume should name the kept session" "$(cat "$TMP_ROOT/claude-args-2")"
[ "$(sed '1,2d' "$TMP_ROOT/claude-args-1")" = "$(sed '1,2d' "$TMP_ROOT/claude-args-2" | sed '$d' | sed '$d')" ] \
    || fail "the resume should keep every flag of the first run" "$(cat "$TMP_ROOT/claude-args-1" "$TMP_ROOT/claude-args-2")"
[ "$(cat "$TMP_ROOT/claude-cwd-2")" = "$(cat "$TMP_ROOT/claude-cwd-1")" ] \
    || fail "the resume should work in the kept worktree" "$(cat "$TMP_ROOT"/claude-cwd-*)"
[ "$(cat "$TMP_ROOT/claude-env-2")" = "1 3600000 600000" ] \
    || fail "the resume should get background tasks off and the raised Bash timeouts" "$(cat "$TMP_ROOT/claude-env-2")"
expect_meta 101 outcome=pr-opened pr=7101 resumes=1 turns=44 cost=9.5 session=s-101
grep -q "session limit" "$TMP_ROOT/run/ticket-101.json.before-resume-1" || fail "the limited result should be kept"
[ "$(find "$TMP_ROOT/run" -name 'ticket-101*.json' | wc -l | tr -d ' ')" = "1" ] \
    || fail "the resume reports the whole session's cost, so only one result may count"
grep -q "hit your limit" "$TMP_ROOT/run"/ticket-101.stderr.limit-* 2>/dev/null \
    || fail "the limited run's stderr should be kept aside"
! grep -q "hit your limit" "$TMP_ROOT/run/ticket-101.stderr" \
    || fail "the old limit message must not stay where a later crash would be read as a limit"
[ ! -e "$WT_ROOT/ticket-101" ] || fail "the worktree should be removed once the PR is open"
[ ! -e "$marker_101" ] || fail "the marker should go with the worktree"
cases=$((cases + 1)); printf '✓ work-ticket: the resume keeps the tree, the flags and the session, and one cost\n'

# The session's transcript is gone (another config dir, or the CLI cleaned it up): start over.
reset_fixtures
fixture_issue 102 maintainer OWNER
fixture_pr_view 7102 OPEN 102-fixture
run_case 6 "work-ticket: a usage limit, before a resume that cannot work" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 102 "$TMP_ROOT/run"
run_case 0 "work-ticket: a kept session with no transcript starts fresh" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 102 "$TMP_ROOT/run"
expect_in_output "has no transcript"
[ "$(sed -n 2p "$TMP_ROOT/claude-args-2")" = "/work-ticket 102" ] || fail "the fallback should be a fresh start" "$(cat "$TMP_ROOT/claude-args-2")"
[ "$(cat "$TMP_ROOT/session-start")" = "$(git -C "$FAKE_REPO" rev-parse origin/main)" ] \
    || fail "the fresh start should cut a new worktree from origin/main"
salvage_branch="$(git -C "$FAKE_REPO" for-each-ref --format='%(refname:short)' 'refs/heads/loop-salvage/102-*')"
[ -n "$salvage_branch" ] && git -C "$FAKE_REPO" show "$salvage_branch:uncommitted.txt" >/dev/null 2>&1 \
    || fail "the kept worktree's uncommitted file should be salvaged before the fresh start" "$LAST_OUTPUT"

# LOOP_KEEP_WORKTREE=1 keeps finished worktrees, but a kept one that cannot be resumed must still
# make room for the fresh start.
reset_fixtures
fixture_issue 121 maintainer OWNER
fixture_pr_view 7121 OPEN 121-fixture
run_case 6 "work-ticket: a usage limit on #121" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 121 "$TMP_ROOT/run"
run_case 0 "work-ticket: LOOP_KEEP_WORKTREE=1 does not block the fresh start after a lost session" \
    env LOOP_KEEP_WORKTREE=1 CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 121 "$TMP_ROOT/run"
[ "$(sed -n 2p "$TMP_ROOT/claude-args-2")" = "/work-ticket 121" ] || fail "the fallback should be a fresh start" "$(cat "$TMP_ROOT/claude-args-2")"
[ -d "$WT_ROOT/ticket-121" ] || fail "the fresh run's own worktree should still be kept"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-121"

# Work that went on without the session: never resume it.
for case_spec in "103:7103:103-fixture:MERGED:was merged" "104:7104:104-by-hand:OPEN:is open from another branch" \
    "109:7109:109-fixture:CLOSED:from this branch was closed" \
    "116:7116:116-fixture:OPEN:has commits the kept branch does not"; do
    IFS=: read -r ticket pr head state message <<< "$case_spec"
    reset_fixtures
    fixture_issue "$ticket" maintainer OWNER
    run_case 6 "work-ticket: a usage limit on #$ticket" \
        env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" "$ticket" "$TMP_ROOT/run"
    fixture_transcript "s-$ticket"
    # For #116 someone pushed to the session's own PR during the nap: its head is a commit the kept
    # branch does not have.
    fixture_prs_state "$pr,$head,$state"
    run_case 12 "work-ticket: a kept session is not resumed when a PR of its ticket $message" \
        env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" "$ticket" "$TMP_ROOT/run"
    expect_in_output "$message"
    [ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "nothing may be resumed" "$LAST_OUTPUT"
    [ -f "$WT_ROOT/ticket-$ticket/uncommitted.txt" ] || fail "the kept worktree must stay for a human"
    [ ! -e "$(kept_marker "$ticket")" ] || fail "the marker should be dropped, or the picker keeps offering the ticket"
    git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-$ticket"
done

# Someone changed a file in the kept worktree since the limit, without a commit: the session no
# longer knows that tree.
reset_fixtures
fixture_issue 117 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #117" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 117 "$TMP_ROOT/run"
fixture_transcript s-117
echo "a human edit" >> "$WT_ROOT/ticket-117/uncommitted.txt"
run_case 12 "work-ticket: a kept worktree whose files changed is not resumed" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 117 "$TMP_ROOT/run"
expect_in_output "HEAD or its files changed"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "nothing may be resumed" "$LAST_OUTPUT"
grep -q "a human edit" "$WT_ROOT/ticket-117/uncommitted.txt" || fail "the human's edit must stay"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-117"

# Someone committed in the kept worktree since the limit: the session no longer knows that tree.
reset_fixtures
fixture_issue 105 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #105" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 105 "$TMP_ROOT/run"
fixture_transcript s-105
git -C "$WT_ROOT/ticket-105" commit -q --allow-empty -m "someone works here by hand"
run_case 12 "work-ticket: a kept worktree whose HEAD moved is not resumed" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 105 "$TMP_ROOT/run"
expect_in_output "HEAD or its files changed"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "nothing may be resumed" "$LAST_OUTPUT"
[ ! -e "$(kept_marker 105)" ] || fail "the stale marker should be dropped, so the worktree reads as someone's work"
run_case 12 "work-ticket: after that it is a worktree someone works in" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 105 "$TMP_ROOT/run"
expect_in_output "already exists"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-105"

# The limit hit before the session cut its branch, and no PR exists yet: the resume cuts the branch
# and opens the PR. A closed PR from an older attempt's branch does not stop it.
behavior "$TMP_ROOT/limit-detached.sh" 'ticket="${PWD##*ticket-}"
case " $* " in
*" --resume "*)
    git checkout -q -b "$ticket-fixture"
    git add -A
    git commit -q -m "finished after the limit"
    echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7$ticket\",\"is_error\":false,\"session_id\":\"s-$ticket\",\"num_turns\":1}" ;;
*)
    echo "work in progress" > wip.txt
    echo "{\"result\":\"You have hit your session limit\",\"is_error\":true,\"session_id\":\"s-$ticket\",\"num_turns\":3}" ;;
esac'
reset_fixtures
fixture_issue 110 maintainer OWNER
run_case 6 "work-ticket: a usage limit before the branch was cut" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-detached.sh" "$WORK" 110 "$TMP_ROOT/run"
fixture_transcript s-110
fixture_prs_state 7009,110-older-attempt,CLOSED
fixture_pr_view 7110 OPEN 110-fixture
run_case 0 "work-ticket: the resume opens the PR that did not exist before the limit" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-detached.sh" "$WORK" 110 "$TMP_ROOT/run"
expect_in_output "PR #7110 opened"
git -C "$FAKE_REPO" show 110-fixture:wip.txt >/dev/null 2>&1 || fail "the resumed session should find its file and commit it"
expect_meta 110 outcome=pr-opened pr=7110 resumes=1 turns=4

# A rebase stopped half way when the limit hit: HEAD is detached, but the session's own open PR is
# still its own, because git records which branch the rebase is on.
behavior "$TMP_ROOT/limit-mid-rebase.sh" 'ticket="${PWD##*ticket-}"
case " $* " in
*" --resume "*)
    git rebase --abort
    echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7$ticket\",\"is_error\":false,\"session_id\":\"s-$ticket\"}" ;;
*)
    git checkout -q -b "$ticket-base"
    echo one > conflict.txt; git add conflict.txt; git commit -q -m one
    git checkout -q -b "$ticket-fixture" HEAD~1
    echo two > conflict.txt; git add conflict.txt; git commit -q -m two
    git rebase "$ticket-base" >/dev/null 2>&1 || true
    echo "{\"result\":\"You have hit your session limit\",\"is_error\":true,\"session_id\":\"s-$ticket\"}" ;;
esac'
reset_fixtures
fixture_issue 111 maintainer OWNER
run_case 6 "work-ticket: a usage limit in the middle of a rebase" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-mid-rebase.sh" "$WORK" 111 "$TMP_ROOT/run"
[ -z "$(git -C "$WT_ROOT/ticket-111" branch --show-current)" ] || fail "the fixture should leave HEAD detached by the rebase"
fixture_transcript s-111
fixture_prs_state "7111,111-fixture,OPEN,$(git -C "$WT_ROOT/ticket-111" rev-parse refs/heads/111-fixture)"
fixture_pr_view 7111 OPEN 111-fixture
run_case 0 "work-ticket: a kept rebase still counts its own open PR as its own" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-mid-rebase.sh" "$WORK" 111 "$TMP_ROOT/run"
expect_in_output "PR #7111 opened"

# The limit is back during the resume: the worktree is kept again, the marker counts every turn so
# far, and the attempt before keeps its files under the .limit- names.
behavior "$TMP_ROOT/limit-twice.sh" 'ticket="${PWD##*ticket-}"
case " $* " in
*" --resume "*)
    echo more >> wip.txt
    echo "second limit" >&2
    echo "{\"result\":\"You have hit your session limit\",\"is_error\":true,\"session_id\":\"s-$ticket\",\"num_turns\":5,\"total_cost_usd\":3}" ;;
*)
    git checkout -q -b "$ticket-fixture"
    echo wip > wip.txt
    echo "first limit" >&2
    echo "{\"result\":\"You have hit your session limit\",\"is_error\":true,\"session_id\":\"s-$ticket\",\"num_turns\":7,\"total_cost_usd\":1}" ;;
esac'
reset_fixtures
fixture_issue 112 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #112" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-twice.sh" "$WORK" 112 "$TMP_ROOT/run"
fixture_transcript s-112
run_case 6 "work-ticket: a second usage limit during the resume keeps the worktree again" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-twice.sh" "$WORK" 112 "$TMP_ROOT/run"
expect_meta 112 outcome=limit worktree=kept resumes=1 turns=12
grep -qx "turns=12" "$(kept_marker 112)" || fail "the marker should count every turn so far" "$(cat "$(kept_marker 112)")"
[ "$(jq -r '.total_cost_usd' "$TMP_ROOT/run/ticket-112.json")" = "3" ] || fail "ticket-112.json should hold the resume's result"
grep -q "first limit" "$TMP_ROOT/run"/ticket-112.stderr.limit-* 2>/dev/null || fail "the first attempt's stderr should be kept aside"
grep -q "second limit" "$TMP_ROOT/run/ticket-112.stderr" || fail "the resume should write a stderr of its own"
[ "$(find "$TMP_ROOT/run" -name 'ticket-112*.json' | wc -l | tr -d ' ')" = "1" ] \
    || fail "only one result may count toward the conductor's cost total"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-112"

# GitHub cannot list the ticket's PRs before a resume: an error, and the kept session waits for the
# next run.
reset_fixtures
fixture_issue 113 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #113" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 113 "$TMP_ROOT/run"
fixture_transcript s-113
printf x > "$GH_FIXTURES/pr-list-fail.json"
run_case 3 "work-ticket: a PR list that fails before a resume is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 113 "$TMP_ROOT/run"
expect_meta 113 outcome=error
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "nothing may be resumed" "$LAST_OUTPUT"
[ -f "$(kept_marker 113)" ] || fail "the kept session should still wait for the next run"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-113"

# Another run claims the marker between the checks and the claim: only one of them may resume.
reset_fixtures
fixture_issue 114 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #114" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 114 "$TMP_ROOT/run"
fixture_transcript s-114
mv "$TMP_ROOT/bin/gh" "$TMP_ROOT/bin/gh.real"
printf '#!/usr/bin/env bash\ncase "$*" in *"--state all"*) rm -f "%s" ;; esac\nexec "%s" "$@"\n' \
    "$(kept_marker 114)" "$TMP_ROOT/bin/gh.real" > "$TMP_ROOT/bin/gh"
chmod +x "$TMP_ROOT/bin/gh"
run_case 12 "work-ticket: a marker another run claimed first is not resumed twice" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 114 "$TMP_ROOT/run"
mv "$TMP_ROOT/bin/gh.real" "$TMP_ROOT/bin/gh"
expect_in_output "another run has just claimed"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "nothing may be resumed" "$LAST_OUTPUT"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-114"

# The resume dies on a rate limit before it writes any JSON: the session id the loop already knows
# still keeps the worktree for the next run.
behavior "$TMP_ROOT/limit-then-crash.sh" 'ticket="${PWD##*ticket-}"
case " $* " in
*" --resume "*)
    echo "API Error: rate limit exceeded" >&2
    exit 1 ;;
*)
    git checkout -q -b "$ticket-fixture"
    echo wip > wip.txt
    echo "{\"result\":\"You have hit your session limit\",\"is_error\":true,\"session_id\":\"s-$ticket\"}" ;;
esac'
reset_fixtures
fixture_issue 118 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #118" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-then-crash.sh" "$WORK" 118 "$TMP_ROOT/run"
fixture_transcript s-118
run_case 6 "work-ticket: a resume that dies without JSON still keeps its session" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-then-crash.sh" "$WORK" 118 "$TMP_ROOT/run"
expect_meta 118 outcome=limit worktree=kept session=s-118
grep -qx "session=s-118" "$(kept_marker 118)" || fail "the marker should still name the session" "$(cat "$(kept_marker 118)" 2>&1)"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-118"

# No transcript, and GitHub cannot list the PRs: the marker must stay, so the next run still sees a
# kept worktree rather than someone's work.
reset_fixtures
fixture_issue 122 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #122" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 122 "$TMP_ROOT/run"
printf x > "$GH_FIXTURES/pr-list-fail.json"
run_case 3 "work-ticket: a PR list that fails after a lost transcript is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 122 "$TMP_ROOT/run"
[ -s "$(kept_marker 122)" ] || fail "the marker should stay until GitHub has answered"
[ -f "$WT_ROOT/ticket-122/uncommitted.txt" ] || fail "nothing may be salvaged before GitHub has answered"

# The files of the kept worktree cannot be read: an error, and the marker stays for the next run.
rm -f "$GH_FIXTURES/pr-list-fail.json"
fixture_transcript s-122
run_case 3 "work-ticket: a kept worktree whose files cannot be read is an error, not a skip" \
    env TMPDIR="$TMP_ROOT/no-such-dir" CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 122 "$TMP_ROOT/run"
expect_in_output "could not read the files"
[ -s "$(kept_marker 122)" ] || fail "the marker should stay for the next run"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-122"

# The session pushed, opened its PR, then rewrote its branch (a rebase before the force push) and
# hit the limit: the PR's head is in the branch's own history, so the PR is still its own.
behavior "$TMP_ROOT/limit-after-rewrite.sh" 'ticket="${PWD##*ticket-}"
case " $* " in
*" --resume "*) echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7$ticket\",\"is_error\":false,\"session_id\":\"s-$ticket\"}" ;;
*)
    git checkout -q -b "$ticket-fixture"
    git commit -q --allow-empty -m "pushed work"
    git rev-parse HEAD > "'"$TMP_ROOT"'/pushed-head"
    git commit -q --amend --allow-empty -m "pushed work, rewritten"
    echo "{\"result\":\"You have hit your session limit\",\"is_error\":true,\"session_id\":\"s-$ticket\"}" ;;
esac'
reset_fixtures
fixture_issue 123 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #123" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-after-rewrite.sh" "$WORK" 123 "$TMP_ROOT/run"
fixture_transcript s-123
fixture_prs_state "7123,123-fixture,OPEN,$(cat "$TMP_ROOT/pushed-head")"
fixture_pr_view 7123 OPEN 123-fixture
run_case 0 "work-ticket: a session that rewrote its own pushed branch is still resumed" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-after-rewrite.sh" "$WORK" 123 "$TMP_ROOT/run"
expect_in_output "PR #7123 opened"

# A stranger commented during the nap: the kept session is never resumed, so its work is salvaged
# and the worktree removed, as before #934, instead of waiting where nobody looks.
reset_fixtures
fixture_issue 119 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #119" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 119 "$TMP_ROOT/run"
fixture_transcript s-119
fixture_comments 119 stranger:NONE
run_case 11 "work-ticket: a kept ticket refused by the provenance gate is salvaged" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 119 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "an untrusted ticket must not be resumed" "$LAST_OUTPUT"
[ ! -e "$WT_ROOT/ticket-119" ] || fail "the kept worktree should be removed"
salvage_branch="$(git -C "$FAKE_REPO" for-each-ref --format='%(refname:short)' 'refs/heads/loop-salvage/119-*')"
[ -n "$salvage_branch" ] && git -C "$FAKE_REPO" show "$salvage_branch:uncommitted.txt" >/dev/null 2>&1 \
    || fail "the kept session's uncommitted file should be salvaged" "$LAST_OUTPUT"

# No transcript, and the session had opened its PR: that PR waits for review, and the session's
# unfinished work stays in its worktree for the owner instead of going to a salvage branch.
reset_fixtures
fixture_issue 120 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #120" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 120 "$TMP_ROOT/run"
fixture_prs 7120:120-fixture
run_case 12 "work-ticket: a kept session with no transcript and an open PR is left for the owner" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-keep.sh" "$WORK" 120 "$TMP_ROOT/run"
expect_in_output "stays in"
[ -f "$WT_ROOT/ticket-120/uncommitted.txt" ] || fail "the unfinished work should stay in the worktree"
[ -z "$(git -C "$FAKE_REPO" for-each-ref 'refs/heads/loop-salvage/120-*')" ] || fail "nothing should be salvaged"
[ ! -e "$(kept_marker 120)" ] || fail "a session that cannot be resumed should lose its marker"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-120"

# A stop during the resumed session ends it like any other: salvaged, and the worktree removed.
behavior "$TMP_ROOT/limit-then-busy.sh" 'ticket="${PWD##*ticket-}"
case " $* " in
*" --resume "*)
    : > "'"$TMP_ROOT"'/resumed"
    "$REAL_SLEEP" 60 ;;
*)
    git checkout -q -b "$ticket-fixture"
    echo wip > wip.txt
    echo "{\"result\":\"You have hit your session limit\",\"is_error\":true,\"session_id\":\"s-$ticket\"}" ;;
esac'
reset_fixtures
rm -f "$TMP_ROOT/resumed"
fixture_issue 115 maintainer OWNER
run_case 6 "work-ticket: a usage limit on #115" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-then-busy.sh" "$WORK" 115 "$TMP_ROOT/run"
fixture_transcript s-115
env CLAUDE_BEHAVIOR="$TMP_ROOT/limit-then-busy.sh" "$WORK" 115 "$TMP_ROOT/run" > "$TMP_ROOT/term.out" 2>&1 &
worker=$!
for _ in $(seq 1 100); do [ -f "$TMP_ROOT/resumed" ] && break; "$REAL_SLEEP" 0.1; done
[ -f "$TMP_ROOT/resumed" ] || { kill "$worker" 2>/dev/null; fail "the resume never started" "$(cat "$TMP_ROOT/term.out")"; }
kill -TERM "$worker"
term_rc=0
for _ in $(seq 1 100); do alive "$worker" || break; "$REAL_SLEEP" 0.1; done
if alive "$worker"; then
    kill -KILL "$worker"
    fail "work-ticket did not stop within 10 seconds of TERM during a resume" "$(cat "$TMP_ROOT/term.out")"
fi
wait "$worker" || term_rc=$?
[ "$term_rc" -eq 143 ] || fail "a stopped worker should exit 143, got $term_rc" "$(cat "$TMP_ROOT/term.out")"
expect_meta 115 outcome=stopped
[ -n "$(git -C "$FAKE_REPO" for-each-ref 'refs/heads/loop-salvage/115-*')" ] || fail "the stopped resume's work should be salvaged"
[ ! -e "$WT_ROOT/ticket-115" ] || fail "a stopped resume should not leave its worktree behind"
cases=$((cases + 1)); printf '✓ work-ticket: TERM during a resumed session stops, salvages and cleans up\n'

reset_fixtures
fixture_issue 91 maintainer OWNER
behavior "$TMP_ROOT/crash.sh" 'echo "{\"result\":\"API Error: 500\",\"is_error\":true,\"session_id\":\"s-91\"}"'
run_case 3 "work-ticket: a session that failed is not resumed" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/crash.sh" "$WORK" 91 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "a failed session must not be resumed" "$LAST_OUTPUT"
expect_meta 91 resumes=0

# F11 of the #925 review: a session that exits non-zero crashed, whatever its JSON says.
reset_fixtures
fixture_issue 97 maintainer OWNER
behavior "$TMP_ROOT/exit-1.sh" 'echo "{\"result\":\"still running\",\"is_error\":false,\"session_id\":\"s-97\"}"
exit 1'
run_case 3 "work-ticket: a session that exits non-zero is not resumed" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/exit-1.sh" "$WORK" 97 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "a crashed session must not be resumed" "$LAST_OUTPUT"

# The wall clock. A fake `date` adds $TMP_ROOT/clock-skew seconds to `date +%s`, so a case can
# jump the clock forward without waiting.
REAL_DATE="$(command -v date)"
cat > "$TMP_ROOT/bin/date" <<STUB
#!/usr/bin/env bash
if [ "\${1:-}" = "+%s" ]; then
    echo \$(( \$("$REAL_DATE" +%s) + \$(cat "$TMP_ROOT/clock-skew" 2>/dev/null || echo 0) ))
else
    exec "$REAL_DATE" "\$@"
fi
STUB
chmod +x "$TMP_ROOT/bin/date"

# The skew is set only after the child has started, so the stop finds it. It jumps ten minutes past
# the default clock.
reset_fixtures
rm -f "$TMP_ROOT/session-child" "$TMP_ROOT/clock-skew"
fixture_issue 95 maintainer OWNER
behavior "$TMP_ROOT/long-skew.sh" 'git checkout -q -b 95-fixture
echo "partial" > partial.txt
set -m
"$REAL_SLEEP" 60 &
echo $! > "'"$TMP_ROOT"'/session-child"
set +m
echo '"$(( (DEFAULT_TIMEOUT_MIN + 10) * 60 ))"' > "'"$TMP_ROOT"'/clock-skew"
wait'
run_case 4 "work-ticket: the wall clock ends a session" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/long-skew.sh" "$WORK" 95 "$TMP_ROOT/run"
expect_meta 95 outcome=timeout resumes=0
[ -n "$(git -C "$FAKE_REPO" for-each-ref 'refs/heads/loop-salvage/95-*')" ] || fail "a timed-out session's work should be salvaged" "$LAST_OUTPUT"
[ ! -e "$WT_ROOT/ticket-95" ] || fail "a timed-out run should not leave its worktree behind"
session_child="$(cat "$TMP_ROOT/session-child")"
for _ in $(seq 1 50); do alive "$session_child" || break; "$REAL_SLEEP" 0.1; done
if alive "$session_child"; then
    kill -KILL "$session_child"
    fail "a process the timed-out session started outlived it"
fi

# A stop that comes while the worker ends a timed-out session must not cut that short. The session
# here takes a moment to end on TERM, so the worker's grace loop naps once. That nap is held until
# the session has ended, and the stop comes then. Its trap would skip the command group the worker
# has already found, and with the session gone, nothing else can find it.
reset_fixtures
rm -f "$TMP_ROOT/session-pid" "$TMP_ROOT/session-child" "$TMP_ROOT/clock-skew" \
    "$TMP_ROOT/in-grace" "$TMP_ROOT/grace-released"
fixture_issue 136 maintainer OWNER
mkdir -p "$TMP_ROOT/hold-grace-bin"
printf '#!/usr/bin/env bash\nif [ "$1" = 0.5 ] && [ ! -e "%s" ]; then\n    touch "%s"\n    for _ in $(seq 1 200); do [ -e "%s" ] && break; "%s" 0.05; done\nfi\nexec "%s" 0.05\n' \
    "$TMP_ROOT/in-grace" "$TMP_ROOT/in-grace" "$TMP_ROOT/grace-released" "$REAL_SLEEP" "$REAL_SLEEP" \
    > "$TMP_ROOT/hold-grace-bin/sleep"
chmod +x "$TMP_ROOT/hold-grace-bin/sleep"
behavior "$TMP_ROOT/slow-to-end.sh" 'echo $$ > "'"$TMP_ROOT"'/session-pid"
git checkout -q -b 136-fixture
trap "$REAL_SLEEP 0.3; exit 143" TERM
set -m
"$REAL_SLEEP" 60 &
echo $! > "'"$TMP_ROOT"'/session-child"
set +m
echo '"$(( (DEFAULT_TIMEOUT_MIN + 10) * 60 ))"' > "'"$TMP_ROOT"'/clock-skew"
wait'
env PATH="$TMP_ROOT/hold-grace-bin:$PATH" CLAUDE_BEHAVIOR="$TMP_ROOT/slow-to-end.sh" "$WORK" 136 "$TMP_ROOT/run" \
    > "$TMP_ROOT/held-grace.out" 2>&1 &
worker=$!
for _ in $(seq 1 100); do [ -e "$TMP_ROOT/in-grace" ] && break; "$REAL_SLEEP" 0.1; done
[ -e "$TMP_ROOT/in-grace" ] || { kill -KILL "$worker" 2>/dev/null || true
    fail "the timed-out session never made the worker nap in its grace loop" "$(cat "$TMP_ROOT/held-grace.out")"; }
session_pid="$(cat "$TMP_ROOT/session-pid")"
session_child="$(cat "$TMP_ROOT/session-child")"
for _ in $(seq 1 50); do kill -0 "$session_pid" 2>/dev/null || break; "$REAL_SLEEP" 0.1; done
kill -0 "$session_pid" 2>/dev/null && { touch "$TMP_ROOT/grace-released"; kill -KILL "$worker" 2>/dev/null || true
    kill -KILL "$session_child" 2>/dev/null || true; fail "the timed-out session never ended" "$(cat "$TMP_ROOT/held-grace.out")"; }
kill -TERM "$worker"
touch "$TMP_ROOT/grace-released"
for _ in $(seq 1 300); do alive "$worker" || break; "$REAL_SLEEP" 0.1; done
if alive "$worker"; then
    kill -KILL "$worker" "$session_child" 2>/dev/null || true
    fail "work-ticket did not end within 30 seconds" "$(cat "$TMP_ROOT/held-grace.out")"
fi
held_rc=0
wait "$worker" || held_rc=$?
for _ in $(seq 1 50); do alive "$session_child" || break; "$REAL_SLEEP" 0.1; done
if alive "$session_child"; then
    kill -KILL "$session_child"
    fail "a stop during a timeout cut its cleanup short, and a command group of the session outlived it" "$(cat "$TMP_ROOT/held-grace.out")"
fi
[ "$held_rc" -eq 4 ] || fail "a worker that times out should still exit 4 after a stop, got $held_rc" "$(cat "$TMP_ROOT/held-grace.out")"
expect_meta 136 outcome=timeout
[ ! -e "$WT_ROOT/ticket-136" ] || fail "a timed-out run should not leave its worktree behind"
rm -f "$TMP_ROOT/clock-skew"
cases=$((cases + 1)); printf '✓ work-ticket: a stop while a timed-out session is being ended does not cut that short\n'

# One clock for the whole ticket (20 minutes): the first run uses 5, and the resume reaches 20:50.
# A resume with a clock of its own would count only 15:50 and run on.
reset_fixtures
rm -f "$TMP_ROOT/clock-skew"
fixture_issue 96 maintainer OWNER
behavior "$TMP_ROOT/waits-long.sh" 'case " $* " in
*" --resume "*) echo 1250 > "'"$TMP_ROOT"'/clock-skew"; "$REAL_SLEEP" 15; echo "{\"result\":\"STATUS: DONE\",\"is_error\":false,\"session_id\":\"s-96\"}" ;;
*) echo 300 > "'"$TMP_ROOT"'/clock-skew"; echo "{\"result\":\"still running\",\"is_error\":false,\"session_id\":\"s-96\",\"total_cost_usd\":3.5}" ;;
esac'
run_case 4 "work-ticket: a resume gets only what is left of the ticket's clock" \
    env LOOP_TICKET_TIMEOUT_MIN=20 CLAUDE_BEHAVIOR="$TMP_ROOT/waits-long.sh" "$WORK" 96 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/claude-runs")" = "2" ] || fail "expected a resume that then timed out" "$LAST_OUTPUT"
expect_meta 96 outcome=timeout resumes=1
[ "$(jq -r '.total_cost_usd' "$TMP_ROOT/run/ticket-96.json")" = "3.5" ] \
    || fail "a killed resume must leave the last result, and its cost, in ticket-96.json"

# With less than ten minutes of the clock left, a resume could not even run the suite.
reset_fixtures
echo 0 > "$TMP_ROOT/clock-skew"
fixture_issue 99 maintainer OWNER
behavior "$TMP_ROOT/waits-late.sh" 'echo 1000 > "'"$TMP_ROOT"'/clock-skew"
echo "{\"result\":\"still running\",\"is_error\":false,\"session_id\":\"s-99\"}"'
run_case 3 "work-ticket: no resume when too little of the clock is left" \
    env LOOP_TICKET_TIMEOUT_MIN=20 CLAUDE_BEHAVIOR="$TMP_ROOT/waits-late.sh" "$WORK" 99 "$TMP_ROOT/run"
expect_in_output "not resuming"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "no time for a resume" "$LAST_OUTPUT"
expect_meta 99 outcome=error resumes=0

# A session that ends during the last nap before the limit is judged from its result, not stopped
# as a timeout (#991). Only the poll nap is slow here. It ends once the worker has reaped the
# session, and only then moves the clock past the limit, by $NAP_SKEW seconds. A session that still
# runs after 10 seconds leaves the clock alone, so the worker naps again instead of timing it out.
mkdir -p "$TMP_ROOT/last-nap-bin"
cat > "$TMP_ROOT/last-nap-bin/sleep" <<STUB
#!/usr/bin/env bash
[ "\$1" = 30 ] || exec "$REAL_SLEEP" 0.05
touch "$TMP_ROOT/in-poll-nap"
for _ in \$(seq 1 200); do
    session="\$(cat "$TMP_ROOT/session-pid" 2>/dev/null || true)"
    if [ -n "\$session" ] && ! kill -0 "\$session" 2>/dev/null; then
        echo "\$NAP_SKEW" > "$TMP_ROOT/clock-skew"
        exit 0
    fi
    "$REAL_SLEEP" 0.05
done
STUB
chmod +x "$TMP_ROOT/last-nap-bin/sleep"
wait_for_poll_nap='echo $$ > "'"$TMP_ROOT"'/session-pid"
for _ in $(seq 1 100); do [ -e "'"$TMP_ROOT"'/in-poll-nap" ] && break; "$REAL_SLEEP" 0.05; done'

reset_fixtures
rm -f "$TMP_ROOT/clock-skew" "$TMP_ROOT/session-pid" "$TMP_ROOT/in-poll-nap"
fixture_issue 131 maintainer OWNER
fixture_pr_view 7131 OPEN 131-fixture
behavior "$TMP_ROOT/done-in-last-nap.sh" 'git checkout -q -b 131-fixture
git commit -q --allow-empty -m "reviewed work"
'"$wait_for_poll_nap"'
echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7131\",\"is_error\":false,\"session_id\":\"s-131\"}"'
run_case 0 "work-ticket: a session that ends in the last nap before the limit is judged, not timed out" \
    env PATH="$TMP_ROOT/last-nap-bin:$PATH" NAP_SKEW=1250 LOOP_TICKET_TIMEOUT_MIN=20 \
    CLAUDE_BEHAVIOR="$TMP_ROOT/done-in-last-nap.sh" "$WORK" 131 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/clock-skew" 2>/dev/null || true)" = "1250" ] \
    || fail "the session did not end during the poll nap, so the case proves nothing" "$LAST_OUTPUT"
expect_in_output "PR #7131 opened"
expect_meta 131 outcome=pr-opened resumes=0
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "a DONE session must not be resumed" "$LAST_OUTPUT"
[ ! -e "$WT_ROOT/ticket-131" ] || fail "a judged run should not leave its worktree behind"

# Judged past the limit, a session that needs a resume never gets one: the clock is the ticket's.
# Here the clock reads 21:30 of 20 minutes, and the log must not count negative minutes.
reset_fixtures
rm -f "$TMP_ROOT/clock-skew" "$TMP_ROOT/session-pid" "$TMP_ROOT/in-poll-nap"
fixture_issue 132 maintainer OWNER
behavior "$TMP_ROOT/no-status-in-last-nap.sh" "$wait_for_poll_nap"'
echo "{\"result\":\"still working\",\"is_error\":false,\"session_id\":\"s-132\"}"'
run_case 3 "work-ticket: a session judged past the limit is not resumed" \
    env PATH="$TMP_ROOT/last-nap-bin:$PATH" NAP_SKEW=1290 LOOP_TICKET_TIMEOUT_MIN=20 \
    CLAUDE_BEHAVIOR="$TMP_ROOT/no-status-in-last-nap.sh" "$WORK" 132 "$TMP_ROOT/run"
[ "$(cat "$TMP_ROOT/clock-skew" 2>/dev/null || true)" = "1290" ] \
    || fail "the session did not end during the poll nap, so the case proves nothing" "$LAST_OUTPUT"
expect_in_output "only 0m of the ticket's clock is left — not resuming"
expect_meta 132 outcome=error resumes=0
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "no time for a resume" "$LAST_OUTPUT"
rm -rf "$TMP_ROOT/clock-skew" "$TMP_ROOT/bin/date" "$TMP_ROOT/session-pid" "$TMP_ROOT/in-poll-nap" \
    "$TMP_ROOT/last-nap-bin"

# Only a real open PR for this ticket counts as DONE. With no session id there is nothing to resume.
reset_fixtures
fixture_issue 76 maintainer OWNER
behavior "$TMP_ROOT/done-no-pr.sh" 'echo "{\"result\":\"STATUS: DONE\",\"is_error\":false}"'
run_case 3 "work-ticket: DONE without a PR anywhere is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done-no-pr.sh" "$WORK" 76 "$TMP_ROOT/run"
expect_in_output "no PR found"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "a result with no session id cannot be resumed" "$LAST_OUTPUT"

# A DONE whose push or `gh pr create` failed is resumed once to open the PR (#934). The resume does
# not count toward LOOP_MAX_RESUMES, so it happens even with that cap at 0.
behavior "$TMP_ROOT/done-then-pr.sh" 'ticket="${PWD##*ticket-}"
case " $* " in
*" --resume "*) echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7$ticket\",\"is_error\":false,\"session_id\":\"s-$ticket\"}" ;;
*)
    git checkout -q -b "$ticket-fixture"
    git commit -q --allow-empty -m "reviewed work"
    echo "{\"result\":\"STATUS: DONE\\nSUMMARY: gh pr create failed on a network error\",\"is_error\":false,\"session_id\":\"s-$ticket\"}" ;;
esac'
reset_fixtures
fixture_issue 106 maintainer OWNER
fixture_pr_view 7106 OPEN 106-fixture
run_case 0 "work-ticket: a DONE with no open PR is resumed once and opens it" \
    env LOOP_MAX_RESUMES=0 CLAUDE_BEHAVIOR="$TMP_ROOT/done-then-pr.sh" "$WORK" 106 "$TMP_ROOT/run"
expect_in_output "resuming it once to open the PR"
expect_in_output "PR #7106 opened"
[ "$(cat "$TMP_ROOT/claude-runs")" = "2" ] || fail "expected the first run and one resume" "$LAST_OUTPUT"
sed -n 2p "$TMP_ROOT/claude-args-2" | grep -qF 'no open pull request for ticket #106. ' \
    || fail "the resume should say what the loop found" "$(sed -n 2p "$TMP_ROOT/claude-args-2")"
sed -n 2p "$TMP_ROOT/claude-args-2" | grep -qF 'branch name starts with "106-"' \
    || fail "the resume should name the branch rule the loop checks" "$(sed -n 2p "$TMP_ROOT/claude-args-2")"
[ "$(tail -2 "$TMP_ROOT/claude-args-2" | tr '\n' ' ')" = "--resume s-106 " ] \
    || fail "the resume should name the session" "$(cat "$TMP_ROOT/claude-args-2")"
expect_meta 106 outcome=pr-opened pr=7106 resumes=1

# The resume answers the same: once is enough, then it is an error.
reset_fixtures
fixture_issue 77 maintainer OWNER
fixture_pr_view 777 OPEN 12-another-ticket
run_case 3 "work-ticket: DONE naming another ticket's PR is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 77 "$TMP_ROOT/run"
expect_in_output "not an open PR for this ticket"
expect_in_output "resuming it once to open the PR"
[ "$(cat "$TMP_ROOT/claude-runs")" = "2" ] || fail "a DONE with no PR is resumed exactly once" "$LAST_OUTPUT"
expect_meta 77 outcome=error resumes=1
sed -n 2p "$TMP_ROOT/claude-args-2" | grep -qF '(PR #777, which your message links, is not one)' \
    || fail "the resume should name the PR the message linked" "$(sed -n 2p "$TMP_ROOT/claude-args-2")"
# Anyone who opens a PR names its branch, so that text must never reach the session (ADR-0026).
! sed -n 2p "$TMP_ROOT/claude-args-2" | grep -q "12-another-ticket" \
    || fail "the linked PR's branch name must stay out of the prompt" "$(sed -n 2p "$TMP_ROOT/claude-args-2")"

# A wrong link in the summary does not hide the ticket's own PR, found by its branch.
reset_fixtures
fixture_issue 107 maintainer OWNER
fixture_pr_view 7107 OPEN 12-another-ticket
behavior "$TMP_ROOT/done-wrong-link.sh" 'git checkout -q -b 107-fixture
printf "[{\"number\":8107,\"headRefName\":\"107-fixture\",\"isCrossRepository\":false}]" > "$GH_FIXTURES/pr-list.json"
echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7107\",\"is_error\":false,\"session_id\":\"s-107\"}"'
run_case 0 "work-ticket: a DONE that links the wrong PR still finds the ticket's own" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done-wrong-link.sh" "$WORK" 107 "$TMP_ROOT/run"
expect_in_output "PR #8107 opened"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "no resume is needed when the PR exists" "$LAST_OUTPUT"

# GitHub cannot say whether a PR exists: a resume would start from a guess, so there is none.
reset_fixtures
fixture_issue 108 maintainer OWNER
behavior "$TMP_ROOT/done-api-down.sh" 'git checkout -q -b 108-fixture
printf x > "$GH_FIXTURES/pr-list-fail.json"
echo "{\"result\":\"STATUS: DONE\",\"is_error\":false,\"session_id\":\"s-108\"}"'
run_case 3 "work-ticket: a DONE the loop cannot check against GitHub is not resumed" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done-api-down.sh" "$WORK" 108 "$TMP_ROOT/run"
expect_in_output "could not be listed"
[ "$(cat "$TMP_ROOT/claude-runs")" = "1" ] || fail "no resume on an unreadable PR list" "$LAST_OUTPUT"
expect_meta 108 outcome=error resumes=0

reset_fixtures
fixture_issue 84 maintainer OWNER
fixture_pr_view 784 OPEN 84-fixture fork
run_case 3 "work-ticket: DONE naming a fork's PR is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 84 "$TMP_ROOT/run"
expect_in_output "not an open PR for this ticket"

reset_fixtures
fixture_issue 78 maintainer OWNER
run_case 3 "work-ticket: DONE naming a PR that does not exist is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 78 "$TMP_ROOT/run"
expect_in_output "unreadable"

# A worktree folder deleted by hand is still registered; the run must not trip over it.
reset_fixtures
fixture_issue 79 maintainer OWNER
fixture_pr_view 779 OPEN 79-fixture
git -C "$FAKE_REPO" worktree add -q --detach "$WT_ROOT/ticket-79" origin/main
rm -rf "$WT_ROOT/ticket-79"
run_case 0 "work-ticket: a worktree folder deleted by hand does not block the ticket" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 79 "$TMP_ROOT/run"

reset_fixtures
fixture_issue 80 maintainer OWNER
fixture_pr_view 780 OPEN 80-fixture
run_case 0 "work-ticket: LOOP_KEEP_WORKTREE=1 keeps the worktree" \
    env LOOP_KEEP_WORKTREE=1 CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 80 "$TMP_ROOT/run"
[ -d "$WT_ROOT/ticket-80" ] || fail "the worktree should be kept"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-80"

reset_fixtures
fixture_issue 81 maintainer OWNER
printf 'x' > "$GH_FIXTURES/pr-list-fail.json"
run_case 3 "work-ticket: an unreadable PR list is an error, not a start" "$WORK" 81 "$TMP_ROOT/run"
[ ! -f "$CLAUDE_MARKER" ] || fail "no session may start when the in-flight check cannot run"

reset_fixtures
fixture_issue 82 maintainer OWNER
git -C "$FAKE_REPO" remote set-url origin "$TMP_ROOT/no-such-origin.git"
run_case 10 "work-ticket: a failed fetch exits 10" "$WORK" 82 "$TMP_ROOT/run"
git -C "$FAKE_REPO" remote set-url origin "$TMP_ROOT/origin.git"
grep -qx "outcome=no-worktree" "$TMP_ROOT/run/ticket-82.meta" || fail "meta should say no-worktree"
[ ! -f "$CLAUDE_MARKER" ] || fail "no session may start without a worktree"

# The lifeline pipe is made in TMPDIR. Without it the watchdog could not tell when the worker is
# gone, so no session starts.
reset_fixtures
fixture_issue 137 maintainer OWNER
run_case 3 "work-ticket: no session starts when the watchdog's pipe cannot be made" \
    env TMPDIR="$TMP_ROOT/no-such-dir" "$WORK" 137 "$TMP_ROOT/run"
expect_in_output "could not open the lifeline pipe"
[ ! -f "$CLAUDE_MARKER" ] || fail "no session may start without its lifeline"
expect_meta 137 outcome=error
[ ! -e "$WT_ROOT/ticket-137" ] || fail "the worktree should be removed when no session could start"

# A stop signal ends the session and everything it started, then salvages and cleans up.
# Claude Code runs each Bash command in a process group of its own, so the fake session starts its
# child the same way (`set -m`): killing the session's group alone would miss it. Unlike claude,
# the fake does not end that child on TERM, so the loop's own tree kill has to.
# stop_case <ticket> <signal...> — sends the signals in order to a worker whose session is busy.
stop_case() {
    local ticket="$1" worker term_rc=0 session_child
    shift
    reset_fixtures
    fixture_issue "$ticket" maintainer OWNER
    rm -f "$TMP_ROOT/session-child"
    behavior "$TMP_ROOT/long.sh" 'git checkout -q -b "${PWD##*ticket-}-fixture"
echo "partial" > partial.txt
set -m
"$REAL_SLEEP" 60 &
echo $! > "'"$TMP_ROOT"'/session-child"
set +m
wait'
    env CLAUDE_BEHAVIOR="$TMP_ROOT/long.sh" "$WORK" "$ticket" "$TMP_ROOT/run" > "$TMP_ROOT/term.out" 2>&1 &
    worker=$!
    for _ in $(seq 1 100); do [ -s "$TMP_ROOT/session-child" ] && break; "$REAL_SLEEP" 0.1; done
    [ -s "$TMP_ROOT/session-child" ] || { kill "$worker" 2>/dev/null; fail "the fake session never started" "$(cat "$TMP_ROOT/term.out")"; }
    session_child="$(cat "$TMP_ROOT/session-child")"
    [ "$(ps -o pgid= -p "$session_child" | tr -d ' ')" = "$session_child" ] \
        || { kill "$worker" 2>/dev/null; fail "the fake session's child should lead a process group of its own"; }
    for signal in "$@"; do kill -"$signal" "$worker"; done
    for _ in $(seq 1 100); do alive "$worker" || break; "$REAL_SLEEP" 0.1; done
    if alive "$worker"; then
        kill -KILL "$worker"
        fail "work-ticket did not stop within 10 seconds of $*" "$(cat "$TMP_ROOT/term.out")"
    fi
    wait "$worker" || term_rc=$?
    [ "$term_rc" -eq 143 ] || fail "a stopped worker should exit 143, got $term_rc" "$(cat "$TMP_ROOT/term.out")"
    session_child="$(cat "$TMP_ROOT/session-child")"
    if alive "$session_child"; then
        kill "$session_child"
        fail "a process the session started in its own group outlived the stop"
    fi
    grep -qx "outcome=stopped" "$TMP_ROOT/run/ticket-$ticket.meta" || fail "meta should say stopped" "$(cat "$TMP_ROOT/run/ticket-$ticket.meta")"
    [ -n "$(git -C "$FAKE_REPO" for-each-ref "refs/heads/loop-salvage/$ticket-*")" ] \
        || fail "a stopped session's work should be salvaged" "$(cat "$TMP_ROOT/term.out")"
    [ ! -e "$WT_ROOT/ticket-$ticket" ] || fail "a stopped run should not leave its worktree behind"
    cases=$((cases + 1)); printf '✓ work-ticket: %s stops the session and its children, salvages, and cleans up\n' "$*"
}
stop_case 83 TERM
# Closing the terminal: HUP to the job, then TERM from the conductor's trap.
stop_case 85 HUP TERM

# A SIGKILL runs no trap. The watchdog inside the session's group must end the session anyway.
reset_fixtures
fixture_issue 86 maintainer OWNER
rm -f "$TMP_ROOT/session-child"
env CLAUDE_BEHAVIOR="$TMP_ROOT/long.sh" "$WORK" 86 "$TMP_ROOT/run" > "$TMP_ROOT/kill.out" 2>&1 &
worker=$!
for _ in $(seq 1 100); do [ -s "$TMP_ROOT/session-child" ] && break; "$REAL_SLEEP" 0.1; done
[ -s "$TMP_ROOT/session-child" ] || { kill "$worker" 2>/dev/null; fail "the fake session never started" "$(cat "$TMP_ROOT/kill.out")"; }
kill -KILL "$worker"
wait "$worker" 2>/dev/null || true
session_child="$(cat "$TMP_ROOT/session-child")"
for _ in $(seq 1 100); do alive "$session_child" || break; "$REAL_SLEEP" 0.1; done
if alive "$session_child"; then
    kill "$session_child"
    fail "the session outlived a SIGKILL of its worker"
fi
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-86"
cases=$((cases + 1)); printf '✓ work-ticket: a SIGKILLed worker does not leave its session running\n'

# The system may give an ended worker's number to another program at once, and a check by number
# cannot tell the two apart (#995, #997). No test can make the system reuse a number, so the worker
# runs under a parent that never reaps it: once the worker has ended, its number still answers
# `kill -0` and `ps`, as a zombie, until that parent is gone. The parent is a subshell that has
# turned into `sleep` with `exec`, so nothing in it ever waits for a child. Its output goes nowhere:
# after a failed case it still runs for a while, and it must not hold the test run's output open.
# start_unreaped <ticket> <behavior> [VAR=value...] — sets worker and holder.
start_unreaped() {
    local ticket="$1" session_behavior="$2"
    shift 2
    rm -f "$TMP_ROOT/worker-pid"
    (
        env "$@" CLAUDE_BEHAVIOR="$session_behavior" "$WORK" "$ticket" "$TMP_ROOT/run" > "$TMP_ROOT/unreaped.out" 2>&1 &
        echo $! > "$TMP_ROOT/worker-pid"
        exec "$REAL_SLEEP" 120
    ) > /dev/null 2>&1 &
    holder=$!
    for _ in $(seq 1 50); do [ -s "$TMP_ROOT/worker-pid" ] && break; "$REAL_SLEEP" 0.1; done
    [ -s "$TMP_ROOT/worker-pid" ] || { kill "$holder" 2>/dev/null || true; fail "the worker never started"; }
    worker="$(cat "$TMP_ROOT/worker-pid")"
}

# Ends the parent, so the system reaps the worker's zombie. A worker that ended without its stop
# trap, or a failed case, can leave the worker's poll nap running: in the cases that use it
# (poll-nap-bin below), that is the one real 30-second sleep. It goes too, if it is still that sleep.
stop_unreaped() {
    local nap
    kill "$holder" 2>/dev/null || true
    wait "$holder" 2>/dev/null || true
    nap="$(cat "$TMP_ROOT/in-poll-nap" 2>/dev/null || true)"
    [ -n "$nap" ] || return 0
    case "$(ps -o command= -p "$nap" 2>/dev/null)" in *"sleep 30") kill "$nap" 2>/dev/null || true ;; esac
}

# wait_until_unreaped — the worker has ended, and its number still answers. Without the second part
# the case would test a free number, which any check by number handles.
wait_until_unreaped() {
    for _ in $(seq 1 100); do alive "$worker" || break; "$REAL_SLEEP" 0.1; done
    if alive "$worker"; then
        kill -KILL "$worker" 2>/dev/null || true
        stop_unreaped
        fail "work-ticket did not end within 10 seconds" "$(cat "$TMP_ROOT/unreaped.out")"
    fi
    kill -0 "$worker" 2>/dev/null || { stop_unreaped; fail "the ended worker's number no longer answers, so the case tested nothing"; }
}

reset_fixtures
fixture_issue 133 maintainer OWNER
rm -f "$TMP_ROOT/session-child" "$TMP_ROOT/in-poll-nap"
start_unreaped 133 "$TMP_ROOT/long.sh"
for _ in $(seq 1 100); do [ -s "$TMP_ROOT/session-child" ] && break; "$REAL_SLEEP" 0.1; done
[ -s "$TMP_ROOT/session-child" ] \
    || { kill -KILL "$worker" 2>/dev/null || true; stop_unreaped; fail "the fake session never started" "$(cat "$TMP_ROOT/unreaped.out")"; }
session_child="$(cat "$TMP_ROOT/session-child")"
session_pid="$(ps -o ppid= -p "$session_child" 2>/dev/null | tr -d ' ' || true)"
[ -n "$session_pid" ] || { kill -KILL "$worker" 2>/dev/null || true; stop_unreaped
    fail "the fake session's child ended before the SIGKILL" "$(cat "$TMP_ROOT/unreaped.out")"; }
kill -KILL "$worker"
wait_until_unreaped
for _ in $(seq 1 100); do alive "$session_child" || break; "$REAL_SLEEP" 0.1; done
if alive "$session_child"; then
    kill -KILL "$session_child" 2>/dev/null || true
    kill -KILL -- "-$session_pid" 2>/dev/null || true
    stop_unreaped
    fail "the session outlived a SIGKILL of its worker while the worker's number still answered"
fi
stop_unreaped
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-133"
cases=$((cases + 1)); printf '✓ work-ticket: a SIGKILLed worker whose number still answers does not leave its session running\n'

# claude ends its own commands when it exits normally (checked by hand against a real session).
# What is left in the session's own group, like a plain child or the watchdog, ends here.
reset_fixtures
fixture_issue 87 maintainer OWNER
fixture_pr_view 787 OPEN 87-fixture
rm -f "$TMP_ROOT/session-child"
behavior "$TMP_ROOT/done-leaves-child.sh" 'git checkout -q -b 87-fixture
"$REAL_SLEEP" 60 > /dev/null 2>&1 &
echo $! > "'"$TMP_ROOT"'/session-child"
echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/787\",\"is_error\":false}"'
run_case 0 "work-ticket: a session that ends normally" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done-leaves-child.sh" "$WORK" 87 "$TMP_ROOT/run"
session_child="$(cat "$TMP_ROOT/session-child")"
for _ in $(seq 1 50); do alive "$session_child" || break; "$REAL_SLEEP" 0.1; done
if alive "$session_child"; then
    kill "$session_child"
    fail "a process a finished session left behind is still running"
fi
cases=$((cases + 1)); printf '✓ work-ticket: what a finished session left running ends with it\n'

# A session that has ended gets no more signals (#983). Bash reaps a finished background job at
# once, not at `wait`, and from then on the system may give its number to another program. The
# worker looks at its session only every 30 seconds, so a stop in that nap must not use the number.
# No test can make the system reuse a number, so every `kill` is logged instead: an exported
# function wins over the builtin in each bash below it. It notes the real PID of the shell that
# called it, because `$$` is the worker's PID in the watchdog subshell too.
reset_fixtures
fixture_issue 130 maintainer OWNER
rm -f "$TMP_ROOT/session-pid" "$TMP_ROOT/session-child" "$TMP_ROOT/kills" "$TMP_ROOT/in-poll-nap"
# Only the poll nap is a real 30 seconds here, so the worker cannot notice the end before the stop.
mkdir -p "$TMP_ROOT/poll-nap-bin"
printf '#!/usr/bin/env bash\n[ "$1" != 30 ] || { echo "$$" > "%s"; exec "%s" 30; }\nexec "%s" 0.05\n' \
    "$TMP_ROOT/in-poll-nap" "$REAL_SLEEP" "$REAL_SLEEP" > "$TMP_ROOT/poll-nap-bin/sleep"
chmod +x "$TMP_ROOT/poll-nap-bin/sleep"
# The session leaves a child in its own group and ends while the worker naps.
behavior "$TMP_ROOT/ends-leaving-child.sh" 'echo $$ > "'"$TMP_ROOT"'/session-pid"
"$REAL_SLEEP" 60 > /dev/null 2>&1 &
echo $! > "'"$TMP_ROOT"'/session-child"
for _ in $(seq 1 100); do [ -e "'"$TMP_ROOT"'/in-poll-nap" ] && break; "$REAL_SLEEP" 0.05; done
echo "{\"result\":\"STATUS: DONE\",\"is_error\":false}"'
(
    export KILL_LOG="$TMP_ROOT/kills"
    kill() { printf '%s %s\n' "$(exec sh -c 'echo "$PPID"')" "$*" >> "$KILL_LOG"; builtin kill "$@"; }
    export -f kill
    exec env PATH="$TMP_ROOT/poll-nap-bin:$PATH" CLAUDE_BEHAVIOR="$TMP_ROOT/ends-leaving-child.sh" \
        "$WORK" 130 "$TMP_ROOT/run" > "$TMP_ROOT/ended.out" 2>&1
) &
worker=$!
for _ in $(seq 1 100); do [ -s "$TMP_ROOT/session-child" ] && break; "$REAL_SLEEP" 0.1; done
[ -s "$TMP_ROOT/session-child" ] \
    || { kill -KILL "$worker" 2>/dev/null || true; fail "the fake session never started" "$(cat "$TMP_ROOT/ended.out")"; }
session_pid="$(cat "$TMP_ROOT/session-pid")"
session_child="$(cat "$TMP_ROOT/session-child")"
# The worker has reaped the session once no process answers to its number.
for _ in $(seq 1 100); do kill -0 "$session_pid" 2>/dev/null || break; "$REAL_SLEEP" 0.1; done
kill -0 "$session_pid" 2>/dev/null \
    && { kill -KILL "$worker" 2>/dev/null || true; fail "the fake session never ended" "$(cat "$TMP_ROOT/ended.out")"; }
# The worker naps and still holds the lifeline, so it may still signal the group. The watchdog must
# leave the group alone: once the group is empty, its number is free.
"$REAL_SLEEP" 1
alive "$session_child" || { kill -KILL "$worker" 2>/dev/null || true
    fail "the watchdog ended the group of an ended session while its worker could still signal it"; }
watchdog="$(watchdogs "$session_pid" "$session_child")"
[ "$(printf '%s\n' "$watchdog" | grep -c .)" -eq 1 ] || { kill -KILL "$worker" 2>/dev/null || true
    fail "expected exactly one watchdog in the session's group" \
        "$(ps -A -o pid= -o pgid= -o command= | awk -v g="$session_pid" '$2 == g')"; }
kill -TERM "$worker"
for _ in $(seq 1 100); do alive "$worker" || break; "$REAL_SLEEP" 0.1; done
if alive "$worker"; then
    kill -KILL "$worker"
    fail "work-ticket did not stop within 10 seconds of TERM" "$(cat "$TMP_ROOT/ended.out")"
fi
term_rc=0
wait "$worker" || term_rc=$?
[ "$term_rc" -eq 143 ] || fail "a stopped worker should exit 143, got $term_rc" "$(cat "$TMP_ROOT/ended.out")"
expect_meta 130 outcome=stopped
# The watchdog is in the session's group until it ends that group, so it may signal that number;
# nothing else may. The worker's own kills must be in the log (its poll nap ends with one), or the
# check is blind. A probe with signal 0 sends nothing, so it does not count.
grep -q "^$worker " "$TMP_ROOT/kills" || fail "no kill run by the worker was logged" "$(cat "$TMP_ROOT/kills")"
if awk -v d="$watchdog" -v s="$session_pid" \
    '$1 != d && $2 != "-0" { for (i = 2; i <= NF; i++) if ($i == s || $i == "-" s) hit = 1 } END { exit !hit }' \
    "$TMP_ROOT/kills"; then
    fail "the worker signalled the number of a session that had already ended" "$(grep -v "^$watchdog " "$TMP_ROOT/kills")"
fi
# Once the worker has let go of the session, the watchdog ends what is left in the group, and
# itself last.
for _ in $(seq 1 100); do { alive "$session_child" || alive "$watchdog"; } || break; "$REAL_SLEEP" 0.1; done
if alive "$session_child" || alive "$watchdog"; then
    kill -KILL "$session_child" "$watchdog" 2>/dev/null || true
    fail "what an ended session left in its group outlived the stopped worker"
fi
cases=$((cases + 1)); printf '✓ work-ticket: a stop just after the session ended sends nothing to its number\n'

# The two ways in of #997: the session ends while the worker naps and leaves a child in its group.
# Then the worker is stopped, which leaves the ended session's group to the watchdog (#983), or it
# is SIGKILLed. Either way the worker's number still answers once the worker has ended (see
# start_unreaped), and the watchdog must end the group anyway.
# A stopped worker lets go of the session before its cleanup, so the group ends at once and not
# only when the worker exits: a `git gc` that the salvage commit starts in the background would
# otherwise hold the pipe open for minutes. In the stop case a slow `docker`, the last step of the
# cleanup, holds the worker there, and the group must end while the worker waits.
mkdir -p "$TMP_ROOT/held-finish-bin"
printf '#!/usr/bin/env bash\ntouch "%s"\nfor _ in $(seq 1 200); do [ -e "%s" ] && break; "%s" 0.05; done\n' \
    "$TMP_ROOT/in-finish" "$TMP_ROOT/finish-released" "$REAL_SLEEP" > "$TMP_ROOT/held-finish-bin/docker"
chmod +x "$TMP_ROOT/held-finish-bin/docker"
# ended_session_case <ticket> <signal> <description>
ended_session_case() {
    local ticket="$1" signal="$2" description="$3" watchdog path="$TMP_ROOT/poll-nap-bin:$PATH"
    reset_fixtures
    fixture_issue "$ticket" maintainer OWNER
    rm -f "$TMP_ROOT/session-pid" "$TMP_ROOT/session-child" "$TMP_ROOT/in-poll-nap" \
        "$TMP_ROOT/in-finish" "$TMP_ROOT/finish-released"
    [ "$signal" != TERM ] || path="$TMP_ROOT/held-finish-bin:$path"
    start_unreaped "$ticket" "$TMP_ROOT/ends-leaving-child.sh" PATH="$path"
    for _ in $(seq 1 100); do [ -s "$TMP_ROOT/session-child" ] && break; "$REAL_SLEEP" 0.1; done
    [ -s "$TMP_ROOT/session-child" ] \
        || { kill -KILL "$worker" 2>/dev/null || true; stop_unreaped; fail "the fake session never started" "$(cat "$TMP_ROOT/unreaped.out")"; }
    session_pid="$(cat "$TMP_ROOT/session-pid")"
    session_child="$(cat "$TMP_ROOT/session-child")"
    for _ in $(seq 1 100); do kill -0 "$session_pid" 2>/dev/null || break; "$REAL_SLEEP" 0.1; done
    kill -0 "$session_pid" 2>/dev/null \
        && { kill -KILL "$worker" 2>/dev/null || true; stop_unreaped; fail "the fake session never ended" "$(cat "$TMP_ROOT/unreaped.out")"; }
    watchdog="$(watchdogs "$session_pid" "$session_child")"
    [ "$(printf '%s\n' "$watchdog" | grep -c .)" -eq 1 ] || { kill -KILL "$worker" 2>/dev/null || true; stop_unreaped
        fail "expected exactly one watchdog in the session's group" \
            "$(ps -A -o pid= -o pgid= -o command= | awk -v g="$session_pid" '$2 == g')"; }
    kill -"$signal" "$worker"
    if [ "$signal" = TERM ]; then
        for _ in $(seq 1 100); do [ -e "$TMP_ROOT/in-finish" ] && break; "$REAL_SLEEP" 0.1; done
        [ -e "$TMP_ROOT/in-finish" ] || { kill -KILL "$worker" 2>/dev/null || true; stop_unreaped
            fail "the stopped worker never reached the end of its cleanup" "$(cat "$TMP_ROOT/unreaped.out")"; }
        for _ in $(seq 1 50); do { alive "$session_child" || alive "$watchdog"; } || break; "$REAL_SLEEP" 0.1; done
        if alive "$session_child" || alive "$watchdog"; then
            kill -KILL "$session_child" "$watchdog" 2>/dev/null || true
            touch "$TMP_ROOT/finish-released"
            stop_unreaped
            fail "the watchdog waited for the stopped worker to exit before it ended the session's group"
        fi
        alive "$worker" || { stop_unreaped; fail "the worker left its cleanup early, so the case tested nothing"; }
        touch "$TMP_ROOT/finish-released"
    fi
    wait_until_unreaped
    for _ in $(seq 1 100); do { alive "$session_child" || alive "$watchdog"; } || break; "$REAL_SLEEP" 0.1; done
    if alive "$session_child" || alive "$watchdog"; then
        kill -KILL "$session_child" "$watchdog" 2>/dev/null || true
        stop_unreaped
        fail "what an ended session left in its group outlived its worker while the worker's number still answered"
    fi
    stop_unreaped
    if [ "$signal" = TERM ]; then
        expect_meta "$ticket" outcome=stopped
        [ ! -e "$WT_ROOT/ticket-$ticket" ] || fail "a stopped run should not leave its worktree behind"
    else
        git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-$ticket"
    fi
    cases=$((cases + 1)); printf '✓ work-ticket: %s\n' "$description"
}
ended_session_case 134 TERM "a stop after the session ended cleans up what it left, though the worker's number still answers"
ended_session_case 135 KILL "a SIGKILL after the session ended cleans up what it left, though the worker's number still answers"

# A poll timer that bash has reaped gets no signal either (#992). It leads no process group, so
# nothing keeps its number reserved. Bash runs a trap only after a foreground command ends, so a
# `wait` that pauses right after the poll timer is reaped keeps the worker in the moment before it
# forgets that number. The stop comes in that pause.
reset_fixtures
fixture_issue 131 maintainer OWNER
rm -f "$TMP_ROOT/session-pid" "$TMP_ROOT/kills" "$TMP_ROOT/poll-pid" "$TMP_ROOT/poll-held" "$TMP_ROOT/poll-held.released"
mkdir -p "$TMP_ROOT/short-poll-bin"
printf '#!/usr/bin/env bash\n[ "$1" != 30 ] || echo "$$" > "%s"\nexec "%s" 0.05\n' \
    "$TMP_ROOT/poll-pid" "$REAL_SLEEP" > "$TMP_ROOT/short-poll-bin/sleep"
chmod +x "$TMP_ROOT/short-poll-bin/sleep"
# The session outlives the pause, so the stop finds it running.
behavior "$TMP_ROOT/outlives-pause.sh" 'echo $$ > "'"$TMP_ROOT"'/session-pid"
"$REAL_SLEEP" 60'
(
    export KILL_LOG="$TMP_ROOT/kills" POLL_PID="$TMP_ROOT/poll-pid" POLL_HELD="$TMP_ROOT/poll-held"
    kill() { printf '%s %s\n' "$(exec sh -c 'echo "$PPID"')" "$*" >> "$KILL_LOG"; builtin kill "$@"; }
    wait() {
        local rc=0
        builtin wait "$@" || rc=$?
        if [ ! -e "$POLL_HELD" ] && [ "${1:-}" = "$(cat "$POLL_PID" 2>/dev/null)" ]; then
            echo "$1" > "$POLL_HELD"
            "$REAL_SLEEP" 5
            touch "$POLL_HELD.released"
        fi
        return "$rc"
    }
    export -f kill wait
    exec env PATH="$TMP_ROOT/short-poll-bin:$PATH" CLAUDE_BEHAVIOR="$TMP_ROOT/outlives-pause.sh" \
        "$WORK" 131 "$TMP_ROOT/run" > "$TMP_ROOT/poll-held.out" 2>&1
) &
worker=$!
for _ in $(seq 1 100); do [ -s "$TMP_ROOT/poll-held" ] && [ -s "$TMP_ROOT/session-pid" ] && break; "$REAL_SLEEP" 0.1; done
if [ ! -s "$TMP_ROOT/poll-held" ] || [ ! -s "$TMP_ROOT/session-pid" ]; then
    kill -KILL "$worker" 2>/dev/null || true
    fail "the worker never ended a poll nap while its session ran" "$(cat "$TMP_ROOT/poll-held.out")"
fi
poll_timer="$(cat "$TMP_ROOT/poll-held")"
session_pid="$(cat "$TMP_ROOT/session-pid")"
kill -TERM "$worker"
for _ in $(seq 1 150); do alive "$worker" || break; "$REAL_SLEEP" 0.1; done
if alive "$worker"; then
    kill -KILL "$worker"
    fail "work-ticket did not stop within 15 seconds of TERM" "$(cat "$TMP_ROOT/poll-held.out")"
fi
term_rc=0
wait "$worker" || term_rc=$?
[ "$term_rc" -eq 143 ] || fail "a stopped worker should exit 143, got $term_rc" "$(cat "$TMP_ROOT/poll-held.out")"
expect_meta 131 outcome=stopped
# A stop that comes in the pause ends the worker before the pause is released.
[ ! -e "$TMP_ROOT/poll-held.released" ] || fail "the stop came after the pause, so the case did not test the race"
# The stop of the running session must be in the log, or the check below is blind.
grep -qF -- "-TERM -- -$session_pid" "$TMP_ROOT/kills" \
    || fail "the stop of the running session was not logged" "$(cat "$TMP_ROOT/kills" 2>/dev/null || true)"
if awk -v p="$poll_timer" '$2 != "-0" { for (i = 2; i <= NF; i++) if ($i == p) hit = 1 } END { exit !hit }' \
    "$TMP_ROOT/kills"; then
    fail "the worker signalled the number of a poll timer that bash had already reaped" "$(cat "$TMP_ROOT/kills")"
fi
for _ in $(seq 1 50); do alive "$session_pid" || break; "$REAL_SLEEP" 0.1; done
if alive "$session_pid"; then
    kill -KILL "$session_pid" 2>/dev/null || true
    fail "the session outlived the stopped worker"
fi
cases=$((cases + 1)); printf '✓ work-ticket: a stop just after the poll timer ended sends nothing to its number\n'

printf 'All %d provenance-gate case(s) passed.\n' "$cases"
