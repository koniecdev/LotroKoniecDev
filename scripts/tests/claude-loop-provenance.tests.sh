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
#
# `gh` is stubbed from fixtures, so the suite is offline and hermetic. The stub applies the
# caller's own `--jq` filter with real jq, which keeps the scripts' jq filters under test too.
# CI runs this before anything else that could rot the gate silently.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
TRUST="$SCRIPTS_DIR/claude/issue-trust.sh"

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

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
args=()
while [ $# -gt 0 ]; do
    case "$1" in
        --jq) filter="${2:-}"; shift 2 ;;
        --paginate) paginate=1; shift ;;
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
                if [ -f "$GH_FIXTURES/pr-list.json" ]; then
                    emit_file "$GH_FIXTURES/pr-list.json"
                elif [ -n "$filter" ]; then
                    printf '[]' | jq -r "$filter"
                else
                    printf '[]'
                fi
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
# The lifecycle cases script what the session does in its worktree.
if [ -n "\${CLAUDE_BEHAVIOR:-}" ]; then exec "\$CLAUDE_BEHAVIOR"; fi
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

# fixture_pr_view <number> <state> <headRefName> [fork] — what `gh pr view <number>` returns.
fixture_pr_view() {
    local fork=false
    [ "${4:-}" = "fork" ] && fork=true
    printf '{"number":%s,"state":"%s","headRefName":"%s","isCrossRepository":%s}' "$1" "$2" "$3" "$fork" \
        > "$GH_FIXTURES/pr-view-$1.json"
}

reset_fixtures() {
    rm -f "$GH_FIXTURES"/*.json "$CLAUDE_MARKER"
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

reset_fixtures
fixture_issue 61 maintainer OWNER
mkdir -p "$FAKE_REPO/.claude/worktrees/ticket-61"
run_case 12 "work-ticket: a ticket whose worktree exists exits 12" "$WORK" 61 "$TMP_ROOT/run"
expect_in_output "already exists"
[ ! -f "$CLAUDE_MARKER" ] || fail "work-ticket spawned a claude session next to an existing worktree"
rm -rf "$FAKE_REPO/.claude/worktrees/ticket-61"

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

# The main checkout is dirty and sits on another branch, and its local `main` carries a commit
# origin does not have: the loop must cut every worktree from origin/main and touch nothing here.
git -C "$FAKE_REPO" update-ref refs/heads/main \
    "$(git -C "$FAKE_REPO" commit-tree -p main -m "local-only commit" "main^{tree}")"
git -C "$FAKE_REPO" checkout -q -b someone-elses-work
echo "work in progress" > "$FAKE_REPO/dirty.txt"

# The fake session names its branch after its ticket and reports PR 7<ticket>.
behavior "$TMP_ROOT/done.sh" 'ticket="${PWD##*ticket-}"
git checkout -q -b "$ticket-fixture"
git commit -q --allow-empty -m "fixture work"
echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/7$ticket\\nSUMMARY: the rate limiter now sends 429 with Retry-After; quota and usage limits unchanged\",\"is_error\":false}"'

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
echo "{\"result\":\"STATUS: BLOCKED\\nCATEGORY: business-questions\",\"is_error\":false}"'
run_case 2 "work-ticket: BLOCKED exits 2" env CLAUDE_BEHAVIOR="$TMP_ROOT/blocked.sh" "$WORK" 71 "$TMP_ROOT/run"
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

reset_fixtures
fixture_issue 75 maintainer OWNER
behavior "$TMP_ROOT/limit.sh" 'echo "{\"result\":\"\",\"is_error\":true,\"api_error_status\":429}"'
run_case 6 "work-ticket: an API 429 is a usage limit" env CLAUDE_BEHAVIOR="$TMP_ROOT/limit.sh" "$WORK" 75 "$TMP_ROOT/run"
grep -qx "outcome=limit" "$TMP_ROOT/run/ticket-75.meta" || fail "meta should say limit"
[ ! -e "$WT_ROOT/ticket-75" ] || fail "the worktree should be removed, so the retry can make it again"

# Only a real open PR for this ticket counts as DONE.
reset_fixtures
fixture_issue 76 maintainer OWNER
behavior "$TMP_ROOT/done-no-pr.sh" 'echo "{\"result\":\"STATUS: DONE\",\"is_error\":false}"'
run_case 3 "work-ticket: DONE without a PR anywhere is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done-no-pr.sh" "$WORK" 76 "$TMP_ROOT/run"
expect_in_output "no PR found"

reset_fixtures
fixture_issue 77 maintainer OWNER
fixture_pr_view 777 OPEN 12-another-ticket
run_case 3 "work-ticket: DONE naming another ticket's PR is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 77 "$TMP_ROOT/run"
expect_in_output "not an open PR for this ticket"

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

# A stop signal ends the session and everything it started, then salvages and cleans up.
# stop_case <ticket> <signal...> — sends the signals in order to a worker whose session is busy.
stop_case() {
    local ticket="$1" worker term_rc=0 session_child
    shift
    reset_fixtures
    fixture_issue "$ticket" maintainer OWNER
    rm -f "$TMP_ROOT/session-child"
    behavior "$TMP_ROOT/long.sh" 'git checkout -q -b "${PWD##*ticket-}-fixture"
echo "partial" > partial.txt
"$REAL_SLEEP" 60 &
echo $! > "'"$TMP_ROOT"'/session-child"
wait'
    env CLAUDE_BEHAVIOR="$TMP_ROOT/long.sh" "$WORK" "$ticket" "$TMP_ROOT/run" > "$TMP_ROOT/term.out" 2>&1 &
    worker=$!
    for _ in $(seq 1 100); do [ -s "$TMP_ROOT/session-child" ] && break; "$REAL_SLEEP" 0.1; done
    [ -s "$TMP_ROOT/session-child" ] || { kill "$worker" 2>/dev/null; fail "the fake session never started" "$(cat "$TMP_ROOT/term.out")"; }
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
        fail "a process the session started outlived the stop"
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

# A session that ends normally may leave something running in its group; it ends with the session.
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

printf 'All %d provenance-gate case(s) passed.\n' "$cases"
