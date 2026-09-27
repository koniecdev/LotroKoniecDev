#!/usr/bin/env bash
# Test suite for the backlog loop's issue-provenance gate (AUDIT-SEC-08, ADR-0026).
#
# The gate is what stops attacker-authored text on this PUBLIC repo from becoming the task of an
# agent that pushes and auto-merges. It is enforced in two places and both are covered here:
#   * issue-trust.sh  — the policy (author + every commenter must carry write access)
#   * next-ticket.sh  — the picker never returns an untrusted ticket
#   * work-ticket.sh  — an explicitly-named untrusted ticket never spawns a claude session
# It also covers the rule that keeps the loop from working a ticket twice now that it stops at the
# PR (ADR-0060): a ticket with an open PR, or with a worktree already on disk, is never started.
# And it covers the worker's worktree lifecycle: the session runs in its own worktree cut from
# origin/main, the main checkout is never touched, and a finished worktree is removed or salvaged.
#
# `gh` is stubbed from fixtures, so the suite is offline and hermetic. The stub applies the
# caller's own `--jq` filter with real jq, which keeps the scripts' jq filters under test too.
# CI runs this before anything else that could rot the gate silently.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
TRUST="$SCRIPTS_DIR/claude/issue-trust.sh"
NEXT="$SCRIPTS_DIR/claude/next-ticket.sh"

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

export GH_FIXTURES="$TMP_ROOT/fixtures"
CLAUDE_MARKER="$TMP_ROOT/claude-was-spawned"
mkdir -p "$GH_FIXTURES" "$TMP_ROOT/bin"

# work-ticket.sh checks out branches and stashes; exercise it against a throwaway clone of the
# two scripts, never the developer's own working copy — a regression in the gate must not be able
# to `git checkout main` under someone's feet just because they ran the tests.
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=provenance-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=provenance-tests GIT_COMMITTER_EMAIL=tests@localhost
FAKE_REPO="$TMP_ROOT/fake-repo"
WORK="$FAKE_REPO/scripts/claude/work-ticket.sh"
mkdir -p "$FAKE_REPO/scripts/claude"
cp "$SCRIPTS_DIR/claude/work-ticket.sh" "$SCRIPTS_DIR/claude/issue-trust.sh" "$FAKE_REPO/scripts/claude/"
chmod +x "$FAKE_REPO/scripts/claude/work-ticket.sh" "$FAKE_REPO/scripts/claude/issue-trust.sh"
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

# fixture_prs <number:headRefName> ... — builds the `gh pr list` payload of open PRs.
fixture_prs() {
    local json="[" separator=""
    for entry in "$@"; do
        json="$json$separator{\"number\":${entry%%:*},\"headRefName\":\"${entry#*:}\"}"
        separator=","
    done
    printf '%s]' "$json" > "$GH_FIXTURES/pr-list.json"
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
# behavior <file> <shell body> — a fake session: it runs in its cwd, then prints the result JSON.
behavior() {
    printf '#!/usr/bin/env bash\nset -e\npwd -P > "%s/session-cwd"\n%s\n' "$TMP_ROOT" "$2" > "$1"
    chmod +x "$1"
}
WT_ROOT="$FAKE_REPO/.claude/worktrees"

# The main checkout is dirty and sits on another branch: the loop must neither care nor touch it.
git -C "$FAKE_REPO" checkout -q -b someone-elses-work
echo "work in progress" > "$FAKE_REPO/dirty.txt"

reset_fixtures
fixture_issue 70 maintainer OWNER
behavior "$TMP_ROOT/done.sh" 'git checkout -q -b "fixture-$(basename "$PWD")"
git commit -q --allow-empty -m "fixture work"
echo "{\"result\":\"STATUS: DONE\\nPR: https://github.com/koniecdev/LotroKoniecDev/pull/777\",\"is_error\":false}"'
: > "$TMP_ROOT/docker-calls"
run_case 0 "work-ticket: DONE opens the PR from its own worktree" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 70 "$TMP_ROOT/run"
expect_in_output "PR #777 opened"
[ "$(cat "$TMP_ROOT/session-cwd")" = "$(cd "$FAKE_REPO" && pwd -P)/.claude/worktrees/ticket-70" ] \
    || fail "the session should run in .claude/worktrees/ticket-70" "$(cat "$TMP_ROOT/session-cwd")"
grep -qx "outcome=pr-opened" "$TMP_ROOT/run/ticket-70.meta" || fail "meta should say pr-opened" "$(cat "$TMP_ROOT/run/ticket-70.meta")"
grep -qx "pr=777" "$TMP_ROOT/run/ticket-70.meta" || fail "meta should carry the PR" "$(cat "$TMP_ROOT/run/ticket-70.meta")"
[ ! -e "$WT_ROOT/ticket-70" ] || fail "a clean worktree should be removed"
git -C "$FAKE_REPO" rev-parse -q --verify fixture-ticket-70 >/dev/null || fail "the ticket branch must stay"
grep -q "image ls" "$TMP_ROOT/docker-calls" || fail "the worktree's E2E images should be looked up for removal"
[ "$(git -C "$FAKE_REPO" branch --show-current)" = "someone-elses-work" ] || fail "the main checkout's branch changed"
[ -f "$FAKE_REPO/dirty.txt" ] || fail "the main checkout's work in progress is gone"
cases=$((cases + 1)); printf '✓ work-ticket: the main checkout is untouched and the finished worktree is removed\n'

reset_fixtures
fixture_issue 71 maintainer OWNER
behavior "$TMP_ROOT/blocked.sh" 'echo "half done" > leftover.txt
echo "{\"result\":\"STATUS: BLOCKED\\nCATEGORY: business-questions\",\"is_error\":false}"'
run_case 2 "work-ticket: BLOCKED exits 2" env CLAUDE_BEHAVIOR="$TMP_ROOT/blocked.sh" "$WORK" 71 "$TMP_ROOT/run"
salvage_branch="$(git -C "$FAKE_REPO" for-each-ref --format='%(refname:short)' 'refs/heads/loop-salvage/71-*')"
[ -n "$salvage_branch" ] || fail "leftovers should be committed on a loop-salvage branch" "$LAST_OUTPUT"
git -C "$FAKE_REPO" show "$salvage_branch:leftover.txt" >/dev/null 2>&1 || fail "the salvage branch should hold the leftover file"
[ ! -e "$WT_ROOT/ticket-71" ] || fail "a salvaged worktree is clean, so it should be removed"
cases=$((cases + 1)); printf '✓ work-ticket: leftovers are salvaged on a branch before the worktree goes\n'

reset_fixtures
fixture_issue 72 maintainer OWNER
behavior "$TMP_ROOT/garbage.sh" 'echo "{\"result\":\"I think I am done\",\"is_error\":false}"'
run_case 3 "work-ticket: a final message without a STATUS block is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/garbage.sh" "$WORK" 72 "$TMP_ROOT/run"
[ ! -e "$WT_ROOT/ticket-72" ] || fail "the worktree should be removed after an error too"

reset_fixtures
fixture_issue 73 maintainer OWNER
behavior "$TMP_ROOT/done-no-pr.sh" 'echo "{\"result\":\"STATUS: DONE\",\"is_error\":false}"'
run_case 3 "work-ticket: DONE without a PR anywhere is an error" \
    env CLAUDE_BEHAVIOR="$TMP_ROOT/done-no-pr.sh" "$WORK" 73 "$TMP_ROOT/run"
expect_in_output "no PR found"

reset_fixtures
fixture_issue 74 maintainer OWNER
run_case 0 "work-ticket: LOOP_KEEP_WORKTREE=1 keeps the worktree" \
    env LOOP_KEEP_WORKTREE=1 CLAUDE_BEHAVIOR="$TMP_ROOT/done.sh" "$WORK" 74 "$TMP_ROOT/run"
[ -d "$WT_ROOT/ticket-74" ] || fail "the worktree should be kept"
git -C "$FAKE_REPO" worktree remove --force "$WT_ROOT/ticket-74"

printf 'All %d provenance-gate case(s) passed.\n' "$cases"
