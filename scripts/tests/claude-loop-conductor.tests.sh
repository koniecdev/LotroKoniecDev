#!/usr/bin/env bash
# Test suite for the backlog loop's conductor, scripts/claude/backlog-loop.sh (ADR-0060).
#
# The conductor runs several tickets at once, each in its own worktree, and stops at the PR. Its
# scheduling is plain bash with no LLM in it, so it is the part a wrong edit breaks silently: a
# queue lost in a subshell runs nothing, a missed slot count runs too much, a missed trap leaves
# sessions running after the conductor is gone. This suite pins that behavior:
#   * never more than -j tickets at once, and really -j when there is enough work,
#   * every ticket runs exactly once, in order when -j is 1,
#   * a usage limit waits for the running tickets, naps, then runs the same ticket again (the
#     worker resumes its session then — #934), and a limit that outlasts every nap names the
#     worktree kept for that resume,
#   * a worktree that cannot be made, or two failures in a row, stop new starts,
#   * a skip, a refusal or a blocked ticket is not a failure,
#   * drain mode and -n still work, a number given twice runs once, and nothing calls `gh pr merge`,
#   * the table names a ticket whose worker had to be resumed,
#   * the loop refuses to run loop scripts that differ from origin/main,
#   * stopping the conductor stops its workers and its nap, also when a second stop comes during
#     the cleanup,
#   * a worker that has already ended gets no signal and frees its slot, even when its number now
#     answers for another program, and an ended nap timer gets no signal either (#992).
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
git init -q --bare "$TMP_ROOT/origin.git"
git -C "$FAKE_REPO" remote add origin "$TMP_ROOT/origin.git"
git -C "$FAKE_REPO" push -q origin main
# The fakes below differ from origin/main by design; the one case that tests that guard turns
# this back off.
export LOOP_ALLOW_LOCAL_SCRIPTS=1

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
# and writes the same .meta file the real one does. $STATE/work-sec-<ticket> sets how long one
# ticket works.
cat > "$FAKE_REPO/scripts/claude/work-ticket.sh" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
ticket="$1" run_dir="$2"
sleeper=""
trap '[ -n "$sleeper" ] && kill "$sleeper" 2>/dev/null; exit 143' TERM
echo "$$" >> "$STATE/pids"
echo "$$" > "$STATE/pid-$ticket"
echo "$ticket" >> "$STATE/started"
mkdir -p "$STATE/running"
touch "$STATE/running/$ticket"
ls "$STATE/running" | wc -l | tr -d ' ' >> "$STATE/concurrency"
"$REAL_SLEEP" "$(cat "$STATE/work-sec-$ticket" 2>/dev/null || echo "${FAKE_WORK_SEC:-0.6}")" &
sleeper=$!
wait "$sleeper"
sleeper=""
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
# The real worker keeps the worktree of a limited session for the next run's resume.
[ "$rc" -eq 6 ] && echo "worktree=kept" >> "$run_dir/ticket-$ticket.meta"
# The real worker writes resumes= for every ticket, 0 when none was needed.
echo "resumes=$(cat "$STATE/resumes-$ticket" 2>/dev/null || echo 0)" >> "$run_dir/ticket-$ticket.meta"
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
# Every wait in the conductor is a `sleep`: shrink them so a 60-minute nap takes a moment. With
# $STATE/slow-naps present, a nap of a minute or more really waits, so a case can stop the
# conductor in the middle of one. A number in that file lowers the bar to that many seconds. Each
# nap timer notes its number, which `exec` keeps.
cat > "$TMP_ROOT/bin/sleep" <<'STUB'
#!/usr/bin/env bash
echo "$$" >> "$STATE/nap-pids"
if [ -f "$STATE/slow-naps" ]; then
    slow_from="$(cat "$STATE/slow-naps")"
    if [ "${1%%.*}" -ge "${slow_from:-60}" ] 2>/dev/null; then exec "$REAL_SLEEP" 30; fi
fi
exec "$REAL_SLEEP" 0.05
STUB
# Never pop real macOS notifications from a test run.
printf '#!/usr/bin/env bash\nexit 0\n' > "$TMP_ROOT/bin/osascript"
chmod +x "$TMP_ROOT/bin/"*
export PATH="$TMP_ROOT/bin:$PATH"

# Bash reaps a finished worker at once, and from then on the system may give its number to another
# program (#992). No test can make the system do that, so a case that sets BASH_ENV to this file
# runs the conductor with a `kill` that acts as if it had: the number of a worker that is gone
# still answers a probe, and a signal to it reaches nobody. Every call is logged first. BASH_ENV
# loads the function into every bash the case starts, and a function wins over the builtin.
REUSED_NUMBERS="$TMP_ROOT/reused-numbers.bash"
cat > "$REUSED_NUMBERS" <<'FAKE'
kill() {
    local arg
    printf '%s\n' "$*" >> "$STATE/kills"
    for arg in "$@"; do
        if grep -qx -- "$arg" "$STATE/pids" 2>/dev/null && ! builtin kill -0 "$arg" 2>/dev/null; then
            return 0
        fi
    done
    builtin kill "$@"
}
FAKE

# Bash runs a trap only after a foreground command ends. So this `wait` pauses right after the first
# nap timer is reaped, and a stop sent in that pause finds the conductor still holding the number.
HOLD_AFTER_NAP="$TMP_ROOT/hold-after-nap.bash"
printf '. "%s"\n' "$REUSED_NUMBERS" > "$HOLD_AFTER_NAP"
cat >> "$HOLD_AFTER_NAP" <<'FAKE'
wait() {
    local rc=0
    builtin wait "$@" || rc=$?
    if [ ! -e "$STATE/nap-held" ] && grep -qx -- "${1:-}" "$STATE/nap-pids" 2>/dev/null; then
        echo "$1" > "$STATE/nap-held"
        "$REAL_SLEEP" 5
        touch "$STATE/nap-released"
    fi
    return "$rc"
}
FAKE

# The first signal the conductor sends to a worker waits 3 seconds, so a case can send a second
# stop in the middle of the cleanup.
PAUSE_FIRST_STOP="$TMP_ROOT/pause-first-stop.bash"
cat > "$PAUSE_FIRST_STOP" <<'FAKE'
kill() {
    if [ ! -e "$STATE/stop-paused" ] && [ "$#" -eq 1 ] && grep -qx -- "$1" "$STATE/pids" 2>/dev/null; then
        touch "$STATE/stop-paused"
        "$REAL_SLEEP" 3
    fi
    builtin kill "$@"
}
FAKE

# A process that has exited but was never reaped still answers `kill -0`; in a container whose PID 1
# reaps nothing, an orphan stays that way. Such a zombie is dead for these tests.
alive() {
    kill -0 "$1" 2>/dev/null || return 1
    case "$(ps -o stat= -p "$1" 2>/dev/null)" in Z*|"") return 1 ;; esac
    return 0
}

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
echo 1 > "$STATE/resumes-8"
run_conductor 0 "conductor: the table names a ticket whose worker had to be resumed" -j 1 8 9
printf '%s\n' "$LAST_OUTPUT" | grep -E '^\[conductor\] #8 .*resumed 1x$' >/dev/null \
    || fail "the row of #8 should say it was resumed once" "$LAST_OUTPUT"
printf '%s\n' "$LAST_OUTPUT" | grep -E '^\[conductor\] #9 .*resumed' >/dev/null \
    && fail "the row of #9 must not mention a resume" "$LAST_OUTPUT"

reset_state
run_conductor 0 "conductor: -j 1 runs one at a time, in the given order" -j 1 7 3 9
[ "$(max_concurrency)" = "1" ] || fail "expected 1 at once, saw $(max_concurrency)" "$LAST_OUTPUT"
expect_started "7 3 9"

# Before #992 the conductor kept a worker on its list while its number answered a probe, so a
# number reused at once held the slot for good and hid that worker's result.
reset_state
export BASH_ENV="$REUSED_NUMBERS"
run_conductor 0 "conductor: a worker whose number now answers for another program frees its slot" -j 1 73 74
unset BASH_ENV
expect_started "73 74"
expect_in_output "done: 2 PR opened"

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
printf '%s\n' "$LAST_OUTPUT" | grep -q "worktree kept" \
    && fail "a ticket whose retry ran must not be listed as waiting for a resume" "$LAST_OUTPUT"

reset_state
echo "6 6 6" > "$STATE/rc-13"
export LOOP_LIMIT_RETRIES=1
run_conductor 0 "conductor: a limit that outlasts every nap gives up" -j 1 13
unset LOOP_LIMIT_RETRIES
expect_in_output "usage limit persisted after 1 naps"
[ "$(grep -cx 13 "$STATE/started")" = "2" ] || fail "#13 should run once plus one retry" "$(started)"
printf '%s\n' "$LAST_OUTPUT" | grep -E '^\[conductor\] #13 +limit .*worktree kept: run #13 again to resume its session$' >/dev/null \
    || fail "the row of #13 should say its worktree waits for a resume" "$LAST_OUTPUT"

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

reset_state
run_conductor 0 "conductor: a ticket number given twice runs once" -j 2 5 5
expect_started "5"

# ── Old or unreviewed loop code does not run ───────────────────────────────────────────────────
reset_state
echo "# a local change" >> "$CONDUCTOR"
export LOOP_ALLOW_LOCAL_SCRIPTS=0
run_conductor 1 "conductor: scripts/claude/ that differs from origin/main is refused" -j 1 91
expect_in_output "differs from origin/main"
[ "$(started)" = "" ] || fail "a refused run must start nothing" "$(started)"
git -C "$FAKE_REPO" checkout -q -- scripts/claude/backlog-loop.sh
run_conductor 0 "conductor: scripts/claude/ that matches origin/main runs" -j 1 92
expect_started "92"
export LOOP_ALLOW_LOCAL_SCRIPTS=1

# ── Stopping the conductor stops its workers ───────────────────────────────────────────────────
reset_state
FAKE_WORK_SEC=20 "$CONDUCTOR" -j 2 61 62 >/dev/null 2>&1 &
conductor_pid=$!
for _ in $(seq 1 100); do
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

# A usage-limit nap is an hour; a stop signal must not wait for it to end.
reset_state
touch "$STATE/slow-naps"
echo 6 > "$STATE/rc-96"
"$CONDUCTOR" -j 1 96 > "$TMP_ROOT/nap.out" 2>&1 &
conductor_pid=$!
for _ in $(seq 1 100); do
    grep -q "usage limit — sleeping" "$TMP_ROOT/nap.out" && break
    "$REAL_SLEEP" 0.1
done
grep -q "usage limit — sleeping" "$TMP_ROOT/nap.out" || { kill "$conductor_pid" 2>/dev/null; fail "the conductor never started its nap" "$(cat "$TMP_ROOT/nap.out")"; }
# The last nap timer runs once its number answers; the earlier ones have ended.
for _ in $(seq 1 100); do alive "$(tail -1 "$STATE/nap-pids")" && break; "$REAL_SLEEP" 0.1; done
nap_timer="$(tail -1 "$STATE/nap-pids")"
alive "$nap_timer" || { kill "$conductor_pid" 2>/dev/null; fail "the usage-limit nap timer never ran"; }
kill -TERM "$conductor_pid"
for _ in $(seq 1 50); do kill -0 "$conductor_pid" 2>/dev/null || break; "$REAL_SLEEP" 0.1; done
if kill -0 "$conductor_pid" 2>/dev/null; then
    kill -KILL "$conductor_pid"
    fail "the conductor was still napping 5 seconds after TERM"
fi
wait "$conductor_pid" 2>/dev/null || true
for _ in $(seq 1 30); do alive "$nap_timer" || break; "$REAL_SLEEP" 0.1; done
if alive "$nap_timer"; then
    kill "$nap_timer"
    fail "the nap timer outlived the conductor"
fi
cases=$((cases + 1)); printf '✓ conductor: TERM ends a usage-limit nap at once\n'

# A worker that ended on its own gets no signal from a stop that comes before the conductor's next
# check (#992). The check nap really waits here, so #71 is still on the conductor's list when the
# stop comes.
reset_state
echo 10 > "$STATE/slow-naps"
echo 0.3 > "$STATE/work-sec-71"
BASH_ENV="$REUSED_NUMBERS" FAKE_WORK_SEC=20 "$CONDUCTOR" -j 2 71 72 > "$TMP_ROOT/reaped.out" 2>&1 &
conductor_pid=$!
for _ in $(seq 1 100); do
    [ -s "$STATE/pid-71" ] && [ -s "$STATE/pid-72" ] && break
    "$REAL_SLEEP" 0.1
done
if [ ! -s "$STATE/pid-71" ] || [ ! -s "$STATE/pid-72" ]; then
    kill -TERM "$conductor_pid" 2>/dev/null || true
    fail "both workers should have started" "$(cat "$TMP_ROOT/reaped.out")"
fi
worker_71="$(cat "$STATE/pid-71")"
worker_72="$(cat "$STATE/pid-72")"
# The conductor has reaped #71 once no process answers to its number.
for _ in $(seq 1 100); do kill -0 "$worker_71" 2>/dev/null || break; "$REAL_SLEEP" 0.1; done
if kill -0 "$worker_71" 2>/dev/null; then
    kill -TERM "$conductor_pid" 2>/dev/null || true
    fail "#71 should have ended on its own" "$(cat "$TMP_ROOT/reaped.out")"
fi
kill -TERM "$conductor_pid"
for _ in $(seq 1 50); do kill -0 "$conductor_pid" 2>/dev/null || break; "$REAL_SLEEP" 0.1; done
if kill -0 "$conductor_pid" 2>/dev/null; then
    kill -KILL "$conductor_pid"
    fail "the conductor did not stop within 5 seconds of TERM" "$(cat "$TMP_ROOT/reaped.out")"
fi
wait "$conductor_pid" 2>/dev/null || true
# A probe sends nothing, so only a call with a real signal counts.
if awk -v w="$worker_71" '$1 != "-0" { for (i = 1; i <= NF; i++) if ($i == w) hit = 1 } END { exit !hit }' \
    "$STATE/kills"; then
    fail "the conductor signalled the number of a worker that bash had already reaped" "$(cat "$STATE/kills")"
fi
# The stop of #72 must be in the log, or the check above is blind.
grep -qx "$worker_72" "$STATE/kills" || fail "the stop of the running worker #72 was not logged" "$(cat "$STATE/kills")"
for _ in $(seq 1 30); do alive "$worker_72" || break; "$REAL_SLEEP" 0.1; done
if alive "$worker_72"; then
    fail "the running worker #72 outlived the conductor"
fi
cases=$((cases + 1)); printf '✓ conductor: a stop sends nothing to a worker that has already ended\n'

# The same holds for a nap timer that bash has reaped while the conductor's nap still holds its
# number.
reset_state
BASH_ENV="$HOLD_AFTER_NAP" FAKE_WORK_SEC=20 "$CONDUCTOR" -j 1 81 > "$TMP_ROOT/held.out" 2>&1 &
conductor_pid=$!
for _ in $(seq 1 100); do [ -s "$STATE/nap-held" ] && [ -s "$STATE/pid-81" ] && break; "$REAL_SLEEP" 0.1; done
if [ ! -s "$STATE/nap-held" ] || [ ! -s "$STATE/pid-81" ]; then
    kill -TERM "$conductor_pid" 2>/dev/null || true
    fail "the conductor never ended a nap while #81 ran" "$(cat "$TMP_ROOT/held.out")"
fi
held_nap="$(cat "$STATE/nap-held")"
kill -TERM "$conductor_pid"
for _ in $(seq 1 100); do kill -0 "$conductor_pid" 2>/dev/null || break; "$REAL_SLEEP" 0.1; done
if kill -0 "$conductor_pid" 2>/dev/null; then
    kill -KILL "$conductor_pid"
    fail "the conductor did not stop within 10 seconds of TERM" "$(cat "$TMP_ROOT/held.out")"
fi
wait "$conductor_pid" 2>/dev/null || true
if awk -v n="$held_nap" '$1 != "-0" { for (i = 1; i <= NF; i++) if ($i == n) hit = 1 } END { exit !hit }' \
    "$STATE/kills"; then
    fail "the conductor signalled the number of a nap timer that bash had already reaped" "$(cat "$STATE/kills")"
fi
# A stop that comes in the pause ends the conductor before the pause is released.
[ ! -e "$STATE/nap-released" ] || fail "the stop came after the pause, so the case did not test the race"
# The stop of #81 must be in the log, or the check above is blind.
grep -qx "$(cat "$STATE/pid-81")" "$STATE/kills" || fail "the stop of the running worker #81 was not logged" "$(cat "$STATE/kills")"
[ ! -d "$FAKE_REPO/.claude/backlog-loop.lock" ] || fail "the lock outlived the conductor"
cases=$((cases + 1)); printf '✓ conductor: a stop sends nothing to a nap timer that has already ended\n'

# A second stop in the middle of the cleanup, such as a second Ctrl-C, is ignored. Its `exit 130`
# would otherwise end the cleanup before every worker got its TERM.
reset_state
BASH_ENV="$PAUSE_FIRST_STOP" FAKE_WORK_SEC=20 "$CONDUCTOR" -j 2 93 94 > "$TMP_ROOT/twice.out" 2>&1 &
conductor_pid=$!
for _ in $(seq 1 100); do [ -s "$STATE/pid-93" ] && [ -s "$STATE/pid-94" ] && break; "$REAL_SLEEP" 0.1; done
if [ ! -s "$STATE/pid-93" ] || [ ! -s "$STATE/pid-94" ]; then
    kill -TERM "$conductor_pid" 2>/dev/null || true
    fail "both workers should have started" "$(cat "$TMP_ROOT/twice.out")"
fi
kill -TERM "$conductor_pid"
for _ in $(seq 1 100); do [ -e "$STATE/stop-paused" ] && break; "$REAL_SLEEP" 0.1; done
[ -e "$STATE/stop-paused" ] || fail "the conductor never started to stop its workers" "$(cat "$TMP_ROOT/twice.out")"
kill -HUP "$conductor_pid"
for _ in $(seq 1 100); do kill -0 "$conductor_pid" 2>/dev/null || break; "$REAL_SLEEP" 0.1; done
if kill -0 "$conductor_pid" 2>/dev/null; then
    kill -KILL "$conductor_pid"
    fail "the conductor did not stop within 10 seconds of the second stop" "$(cat "$TMP_ROOT/twice.out")"
fi
wait "$conductor_pid" 2>/dev/null || true
for ticket in 93 94; do
    worker="$(cat "$STATE/pid-$ticket")"
    for _ in $(seq 1 30); do alive "$worker" || break; "$REAL_SLEEP" 0.1; done
    if alive "$worker"; then fail "worker #$ticket outlived a conductor that was stopped twice"; fi
done
[ ! -d "$FAKE_REPO/.claude/backlog-loop.lock" ] || fail "the lock outlived a conductor that was stopped twice"
cases=$((cases + 1)); printf '✓ conductor: a second stop during the cleanup still stops every worker\n'

printf 'All %d conductor case(s) passed.\n' "$cases"
