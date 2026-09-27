#!/usr/bin/env bash
# Test suite for the backlog loop's conductor, scripts/claude/backlog-loop.sh (ADR-0060).
#
# The conductor runs several tickets at once, each in its own worktree, and stops at the PR. Its
# scheduling is plain bash with no LLM in it, so it is the part a wrong edit breaks silently: a
# queue lost in a subshell runs nothing, a missed slot count runs too much, a missed trap leaves
# sessions running after the conductor is gone. This suite pins that behavior:
#   * never more than -j tickets at once, and really -j when there is enough work,
#   * every ticket runs exactly once, in order when -j is 1,
#   * a usage limit waits for the running tickets, naps, then runs the same ticket again,
#   * a worktree that cannot be made, or two failures in a row, stop new starts,
#   * a skip, a refusal or a blocked ticket is not a failure,
#   * drain mode and -n still work, and nothing ever calls `gh pr merge`,
#   * stopping the conductor stops its workers.
#
# `work-ticket.sh` and `next-ticket.sh` are replaced by fakes, and `gh`, `sleep` and `osascript`
# are stubbed, so the suite is offline and runs in seconds.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
REAL_SLEEP="$(command -v sleep)"
export REAL_SLEEP

TMP_ROOT="$(mktemp -d)"
cleanup() {
    # A failed case can leave a fake worker running; never leave one behind.
    if [ -f "$TMP_ROOT/state/pids" ]; then
        while read -r leftover; do kill "$leftover" 2>/dev/null || true; done < "$TMP_ROOT/state/pids"
    fi
    rm -rf "$TMP_ROOT"
}
trap cleanup EXIT

export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=conductor-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=conductor-tests GIT_COMMITTER_EMAIL=tests@localhost

FAKE_REPO="$TMP_ROOT/fake-repo"
CONDUCTOR="$FAKE_REPO/scripts/claude/backlog-loop.sh"
export STATE="$TMP_ROOT/state"
mkdir -p "$FAKE_REPO/scripts/claude" "$TMP_ROOT/bin"
cp "$SCRIPTS_DIR/claude/backlog-loop.sh" "$CONDUCTOR"
git -C "$FAKE_REPO" init -q -b main
git -C "$FAKE_REPO" add -A
git -C "$FAKE_REPO" commit -qm "conductor fixture repo"

cases=0
LAST_OUTPUT=""

fail() {
    printf '✗ %s\n' "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    exit 1
}

# ── Fakes ──────────────────────────────────────────────────────────────────────────────────────
# The fake worker counts how many workers run at once, takes its exit code from
# $STATE/rc-<ticket> (a list: each run takes the first word, so "6 0" is "limit, then success"),
# and writes the same .meta file the real one does.
cat > "$FAKE_REPO/scripts/claude/work-ticket.sh" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
ticket="$1" run_dir="$2"
echo "$$" >> "$STATE/pids"
echo "$ticket" >> "$STATE/started"
mkdir -p "$STATE/running"
touch "$STATE/running/$ticket"
ls "$STATE/running" | wc -l | tr -d ' ' >> "$STATE/concurrency"
"$REAL_SLEEP" "${FAKE_WORK_SEC:-0.6}"
rm -f "$STATE/running/$ticket"
rc=0
if [ -f "$STATE/rc-$ticket" ]; then
    set -- $(cat "$STATE/rc-$ticket")
    rc="${1:-0}"
    [ $# -gt 0 ] && shift
    echo "$*" > "$STATE/rc-$ticket"
fi
outcome=pr-opened
case "$rc" in 2) outcome=blocked ;; 3) outcome=error ;; 6) outcome=limit ;; 11) outcome=untrusted ;; 12) outcome=skipped ;; esac
printf 'issue=%s\noutcome=%s\n' "$ticket" "$outcome" > "$run_dir/ticket-$ticket.meta"
[ "$rc" -eq 0 ] && echo "pr=$((ticket + 1000))" >> "$run_dir/ticket-$ticket.meta"
exit "$rc"
FAKE

# The fake picker serves $STATE/backlog in order and honors --exclude, like the real one.
cat > "$FAKE_REPO/scripts/claude/next-ticket.sh" <<'FAKE'
#!/usr/bin/env bash
exclude=" ${2:-} "
for n in $(cat "$STATE/backlog" 2>/dev/null); do
    case "$exclude" in *" $n "*) continue ;; esac
    echo "$n"
    exit 0
done
exit 1
FAKE
chmod +x "$FAKE_REPO/scripts/claude/"*.sh

cat > "$TMP_ROOT/bin/gh" <<'STUB'
#!/usr/bin/env bash
echo "gh $*" >> "$STATE/gh-calls"
case "$1 ${2:-}" in
    "pr checks") echo green ;;
    "api "*) echo 0 ;;
esac
exit 0
STUB
# Every wait in the conductor is a `sleep`: shrink them so a 60-minute nap takes a moment.
cat > "$TMP_ROOT/bin/sleep" <<'STUB'
#!/usr/bin/env bash
exec "$REAL_SLEEP" 0.05
STUB
# Never pop real macOS notifications from a test run.
printf '#!/usr/bin/env bash\nexit 0\n' > "$TMP_ROOT/bin/osascript"
chmod +x "$TMP_ROOT/bin/"*
export PATH="$TMP_ROOT/bin:$PATH"

reset_state() {
    rm -rf "$STATE" "$FAKE_REPO/.claude" "$FAKE_REPO/logs"
    mkdir -p "$STATE"
    : > "$STATE/started"
    : > "$STATE/concurrency"
    : > "$STATE/gh-calls"
}

# run_conductor <expected-exit> <description> <args...>
# A broken queue does not fail, it runs the same ticket forever. So each run gets 60 seconds, and
# a run that outlives them is stopped and reported instead of hanging the suite.
run_conductor() {
    local expected="$1" description="$2" rc=0 pid tenths=0
    shift 2
    "$CONDUCTOR" "$@" > "$TMP_ROOT/conductor.out" 2>&1 &
    pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        if [ "$tenths" -ge 600 ]; then
            kill -TERM "$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
            fail "$description — the conductor was still running after 60s" "$(tail -20 "$TMP_ROOT/conductor.out")"
        fi
        "$REAL_SLEEP" 0.1
        tenths=$((tenths + 1))
    done
    wait "$pid" || rc=$?
    LAST_OUTPUT="$(cat "$TMP_ROOT/conductor.out")"
    if [ "$rc" -ne "$expected" ]; then
        fail "$description — expected exit $expected, got $rc" "$LAST_OUTPUT"
    fi
    cases=$((cases + 1))
    printf '✓ %s\n' "$description"
}

started() { tr '\n' ' ' < "$STATE/started" | sed 's/ $//'; }
max_concurrency() { sort -n "$STATE/concurrency" | tail -1; }

expect_started() {
    [ "$(started)" = "$1" ] || fail "tickets started should be '$1' but were '$(started)'" "$LAST_OUTPUT"
}

expect_in_output() {
    printf '%s' "$LAST_OUTPUT" | grep -qF -- "$1" || fail "output should contain '$1'" "$LAST_OUTPUT"
}

expect_no_merge() {
    if grep -q "pr merge" "$STATE/gh-calls"; then
        fail "the conductor called gh pr merge — it must stop at the PR" "$(cat "$STATE/gh-calls")"
    fi
}

# ── Slots ──────────────────────────────────────────────────────────────────────────────────────
reset_state
run_conductor 0 "conductor: -j 2 runs five tickets two at a time" -j 2 1 2 3 4 5
[ "$(max_concurrency)" = "2" ] || fail "expected exactly 2 at once, saw $(max_concurrency)" "$LAST_OUTPUT"
[ "$(tr '\n' ' ' < "$STATE/started" | wc -w | tr -d ' ')" = "5" ] || fail "every ticket must run once" "$(started)"
for ticket in 1 2 3 4 5; do
    [ "$(grep -cx "$ticket" "$STATE/started")" = "1" ] || fail "#$ticket ran more than once" "$(started)"
done
expect_in_output "done: 5 PR opened"
expect_in_output "#1001"
expect_in_output "assign yourself to approve it, then run /merge-train"
expect_no_merge

reset_state
run_conductor 0 "conductor: -j 1 runs one at a time, in the given order" -j 1 7 3 9
[ "$(max_concurrency)" = "1" ] || fail "expected 1 at once, saw $(max_concurrency)" "$LAST_OUTPUT"
expect_started "7 3 9"

reset_state
run_conductor 1 "conductor: -j 0 is refused" -j 0 1
run_conductor 1 "conductor: -j with a word is refused" -j many 1
[ "$(started)" = "" ] || fail "a refused run must start nothing" "$(started)"

# ── Usage limit ────────────────────────────────────────────────────────────────────────────────
reset_state
echo "6 0" > "$STATE/rc-11"
run_conductor 0 "conductor: a usage limit naps, then runs the same ticket again" -j 2 11 12
[ "$(grep -cx 11 "$STATE/started")" = "2" ] || fail "#11 should run twice (limit, then retry)" "$(started)"
[ "$(grep -cx 12 "$STATE/started")" = "1" ] || fail "#12 should run once" "$(started)"
expect_in_output "usage limit — sleeping"
expect_in_output "done: 2 PR opened"

reset_state
echo "6 6 6" > "$STATE/rc-13"
export LOOP_LIMIT_RETRIES=1
run_conductor 0 "conductor: a limit that outlasts every nap gives up" -j 1 13
unset LOOP_LIMIT_RETRIES
expect_in_output "usage limit persisted after 1 naps"
[ "$(grep -cx 13 "$STATE/started")" = "2" ] || fail "#13 should run once plus one retry" "$(started)"

# ── Stop conditions ────────────────────────────────────────────────────────────────────────────
reset_state
echo 10 > "$STATE/rc-21"
run_conductor 10 "conductor: a worktree that cannot be made stops new starts" -j 1 21 22 23
expect_started "21"
expect_in_output "could not prepare a worktree"

reset_state
echo 3 > "$STATE/rc-31"
echo 4 > "$STATE/rc-32"
run_conductor 0 "conductor: two failures in a row stop new starts" -j 1 31 32 33 34
expect_started "31 32"
expect_in_output "2 consecutive failures"

reset_state
echo 12 > "$STATE/rc-41"
echo 11 > "$STATE/rc-42"
echo 2 > "$STATE/rc-43"
echo 3 > "$STATE/rc-44"
run_conductor 0 "conductor: a skip, a refusal or a block is not a failure" -j 1 41 42 43 44 45
expect_started "41 42 43 44 45"
expect_in_output "done: 1 PR opened · 1 blocked · 1 failed · 1 skipped · 1 untrusted"

# ── Drain mode ─────────────────────────────────────────────────────────────────────────────────
reset_state
echo "51 52 53" > "$STATE/backlog"
run_conductor 0 "conductor: drain mode runs every ticket the picker offers, once" -j 2
[ "$(tr ' ' '\n' <<<"$(started)" | sort -n | tr '\n' ' ' | sed 's/ $//')" = "51 52 53" ] \
    || fail "drain should run 51 52 53 once each" "$(started)"

reset_state
echo "51 52 53" > "$STATE/backlog"
run_conductor 0 "conductor: -n 2 stops after two tickets" -j 1 -n 2
expect_started "51 52"
expect_no_merge

# ── Stopping the conductor stops its workers ───────────────────────────────────────────────────
reset_state
FAKE_WORK_SEC=20 "$CONDUCTOR" -j 2 61 62 >/dev/null 2>&1 &
conductor_pid=$!
for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    [ "$(wc -l < "$STATE/started" | tr -d ' ')" -ge 2 ] && break
    "$REAL_SLEEP" 0.2
done
[ "$(wc -l < "$STATE/started" | tr -d ' ')" -ge 2 ] || fail "both workers should have started"
kill -TERM "$conductor_pid"
wait "$conductor_pid" 2>/dev/null || true
"$REAL_SLEEP" 0.3
while read -r worker; do
    if kill -0 "$worker" 2>/dev/null; then
        fail "worker $worker outlived the conductor"
    fi
done < "$STATE/pids"
[ ! -d "$FAKE_REPO/.claude/backlog-loop.lock" ] || fail "the lock outlived the conductor"
cases=$((cases + 1)); printf '✓ conductor: TERM stops every worker and frees the lock\n'

printf 'All %d conductor case(s) passed.\n' "$cases"
