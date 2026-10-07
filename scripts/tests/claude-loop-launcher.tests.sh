#!/usr/bin/env bash
# Test suite for the backlog loop's terminal launcher, scripts/claude/start-loop.sh (#969).
#
# The loop runs for hours, so it must run in a plain terminal, never from a Claude Code session,
# whose background commands are killed after two hours at most. The launcher is what makes that
# one command. This suite pins its contract:
#   * no arguments start nothing, and neither does a loop that is already running,
#   * the loop runs from its own checkout next to the main one, moved to origin/main first, so it
#     runs from any folder while the main checkout is on any branch, with any loop code there,
#   * whichever copy of the launcher you start, the copy on origin/main does the run,
#   * a loop checkout with local changes, or a folder there that is not this repository's checkout,
#     is refused and left as it is,
#   * the console is copied to logs/claude-loop/console-<timestamp>.log in the main checkout,
#   * Ctrl-C, or a closed terminal, stops the run, and each worker salvages its work first: the
#     copy of the console outlives the stop, so a worker that writes its cleanup lines is not
#     killed by SIGPIPE.
#
# The real launcher drives the real conductor. `work-ticket.sh` is a fake, and `gh`, `sleep`,
# `osascript` and `caffeinate` are stubbed, so the suite is offline and runs in seconds.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
REAL_SLEEP="$(command -v sleep)"
export REAL_SLEEP

TMP_ROOT="$(cd "$(mktemp -d)" && pwd -P)"
cleanup() {
    # A failed case can leave a fake worker or a fake loop running; never leave one behind.
    local leftover
    while read -r leftover; do
        kill "$leftover" 2>/dev/null || true
    done < <(cat "$TMP_ROOT/state/pids" "$TMP_ROOT/fake-loop-pid" 2>/dev/null || true)
    rm -rf "$TMP_ROOT"
}
trap cleanup EXIT

export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=launcher-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=launcher-tests GIT_COMMITTER_EMAIL=tests@localhost
# The launcher's own run is under test: its guard must pass because the loop checkout is on
# origin/main, not because the caller switched it off.
unset LOOP_ALLOW_LOCAL_SCRIPTS

FAKE_REPO="$TMP_ROOT/fake-repo"
LOOP_CHECKOUT="$TMP_ROOT/fake-repo-loop"
LAUNCHER="$FAKE_REPO/scripts/claude/start-loop.sh"
export STATE="$TMP_ROOT/state"
mkdir -p "$FAKE_REPO/scripts/claude" "$TMP_ROOT/bin" "$TMP_ROOT/elsewhere"
cp "$SCRIPTS_DIR/claude/start-loop.sh" "$SCRIPTS_DIR/claude/backlog-loop.sh" "$FAKE_REPO/scripts/claude/"

# ── Fakes ──────────────────────────────────────────────────────────────────────────────────────
# The fake worker notes which copy of it runs, works $FAKE_WORK_SEC seconds and writes the .meta
# file the real one does. A stop makes it write its cleanup lines one by one, as the real worker
# does while it salvages, and only then note that it salvaged. A worker killed by SIGPIPE half way
# never writes that note. Like the real one, it acts on the first stop signal only: a closed
# terminal sends HUP to every process, and the conductor then sends TERM.
cat > "$FAKE_REPO/scripts/claude/work-ticket.sh" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
ticket="$1" run_dir="$2"
sleeper=""
ending=0
on_stop() {
    [ "$ending" -eq 0 ] || return 0
    ending=1
    if [ -n "$sleeper" ]; then kill "$sleeper" 2>/dev/null || true; fi
    for step in 1 2 3 4 5 6 7 8; do
        echo "[loop] #$ticket salvaging, step $step"
        "$REAL_SLEEP" 0.1
    done
    echo "[loop] #$ticket STOPPED — session killed, changes salvaged"
    touch "$STATE/salvaged-$ticket"
    printf 'issue=%s\noutcome=stopped\n' "$ticket" > "$run_dir/ticket-$ticket.meta"
    exit 143
}
trap on_stop INT TERM HUP
echo "$$" >> "$STATE/pids"
echo "$ticket" >> "$STATE/started"
echo "$0" > "$STATE/copy-$ticket"
"$REAL_SLEEP" "${FAKE_WORK_SEC:-0.3}" &
sleeper=$!
wait "$sleeper"
sleeper=""
printf 'issue=%s\noutcome=pr-opened\npr=%s\n' "$ticket" "$((ticket + 1000))" > "$run_dir/ticket-$ticket.meta"
FAKE
chmod +x "$FAKE_REPO/scripts/claude/"*.sh

git -C "$FAKE_REPO" init -q -b main
git -C "$FAKE_REPO" add -A
git -C "$FAKE_REPO" commit -qm "launcher fixture repo"
git init -q --bare -b main "$TMP_ROOT/origin.git"
git -C "$FAKE_REPO" remote add origin "$TMP_ROOT/origin.git"
git -C "$FAKE_REPO" push -q origin main
# A second clone moves origin/main the way a merged PR does.
git clone -q -b main "$TMP_ROOT/origin.git" "$TMP_ROOT/pusher"

cat > "$TMP_ROOT/bin/gh" <<'STUB'
#!/usr/bin/env bash
case "$1 ${2:-}" in
    "pr checks") echo green ;;
    "api "*) echo 0 ;;
esac
exit 0
STUB
# The conductor's naps would make each case last tens of seconds.
cat > "$TMP_ROOT/bin/sleep" <<'STUB'
#!/usr/bin/env bash
exec "$REAL_SLEEP" 0.05
STUB
# Never pop real macOS notifications from a test run.
printf '#!/usr/bin/env bash\nexit 0\n' > "$TMP_ROOT/bin/osascript"
# Runs the command it is given, as the real one does, and notes that it was used.
cat > "$TMP_ROOT/bin/caffeinate" <<'STUB'
#!/usr/bin/env bash
echo "$*" >> "$STATE/caffeinate"
shift
exec "$@"
STUB
chmod +x "$TMP_ROOT/bin/"*
export PATH="$TMP_ROOT/bin:$PATH"

cases=0
LAST_OUTPUT=""

fail() {
    printf '✗ %s\n' "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ %s\n' "$1"
}

reset_state() {
    rm -rf "$STATE" "$FAKE_REPO/.claude" "$FAKE_REPO/logs"
    mkdir -p "$STATE"
    : > "$STATE/started"
}

# run_launcher <expected-exit> <description> <launcher> <args...> — runs from a folder outside
# the repository, and gives up after 60 seconds instead of hanging the suite.
run_launcher() {
    local expected="$1" description="$2" launcher="$3" rc=0 pid tenths=0
    shift 3
    (cd "$TMP_ROOT/elsewhere" && exec "$launcher" "$@") > "$TMP_ROOT/launcher.out" 2>&1 &
    pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        if [ "$tenths" -ge 600 ]; then
            kill -TERM "$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
            fail "$description — the launcher was still running after 60s" "$(tail -20 "$TMP_ROOT/launcher.out")"
        fi
        "$REAL_SLEEP" 0.1
        tenths=$((tenths + 1))
    done
    wait "$pid" || rc=$?
    LAST_OUTPUT="$(cat "$TMP_ROOT/launcher.out")"
    if [ "$rc" -ne "$expected" ]; then
        fail "$description — expected exit $expected, got $rc" "$LAST_OUTPUT"
    fi
}

started() { tr '\n' ' ' < "$STATE/started" | sed 's/ $//'; }

expect_started() {
    [ "$(started)" = "$1" ] || fail "tickets started should be '$1' but were '$(started)'" "$LAST_OUTPUT"
}

expect_in_output() {
    printf '%s' "$LAST_OUTPUT" | grep -qF -- "$1" || fail "output should contain '$1'" "$LAST_OUTPUT"
}

origin_main() { git -C "$FAKE_REPO" rev-parse refs/remotes/origin/main; }
loop_head() { git -C "$LOOP_CHECKOUT" rev-parse HEAD; }

console_logs() { ls "$FAKE_REPO/logs/claude-loop/"console-*.log 2>/dev/null || true; }

# move_origin_main <file> — a merged PR lands on origin/main.
move_origin_main() {
    echo "$1" > "$TMP_ROOT/pusher/$1"
    git -C "$TMP_ROOT/pusher" add "$1"
    git -C "$TMP_ROOT/pusher" commit -qm "merged: $1"
    git -C "$TMP_ROOT/pusher" push -q origin HEAD:main
}

# A process that has exited but was never reaped still answers `kill -0`; such a zombie is dead
# for these tests.
alive() {
    kill -0 "$1" 2>/dev/null || return 1
    case "$(ps -o stat= -p "$1" 2>/dev/null)" in Z*|"") return 1 ;; esac
    return 0
}

group_empty() {
    ps -A -o pgid= -o stat= | awk -v group="$1" '$1 == group && $2 !~ /^Z/ { left = 1 } END { exit left }'
}

# ── What starts nothing ────────────────────────────────────────────────────────────────────────
reset_state
run_launcher 1 "launcher: no arguments" "$LAUNCHER"
expect_in_output "usage: start-loop.sh"
[ ! -e "$LOOP_CHECKOUT" ] || fail "a launcher without arguments must not make the loop checkout"
pass "launcher: no arguments print the usage and start nothing"

# A live conductor runs from the loop checkout; moving it would change the files under it.
reset_state
mkdir -p "$TMP_ROOT/running-loop" "$FAKE_REPO/.claude/backlog-loop.lock"
# It stays a bash process, so its command line keeps the script's name, as a real conductor's does.
cat > "$TMP_ROOT/running-loop/backlog-loop.sh" <<'FAKE'
#!/usr/bin/env bash
"$REAL_SLEEP" 60 &
timer=$!
trap 'kill "$timer"; exit 143' TERM
wait "$timer"
FAKE
chmod +x "$TMP_ROOT/running-loop/backlog-loop.sh"
bash "$TMP_ROOT/running-loop/backlog-loop.sh" &
fake_loop=$!
echo "$fake_loop" > "$TMP_ROOT/fake-loop-pid"
echo "$fake_loop" > "$FAKE_REPO/.claude/backlog-loop.lock/pid"
run_launcher 1 "launcher: a running loop" "$LAUNCHER" 5
kill "$fake_loop" 2>/dev/null || true
wait "$fake_loop" 2>/dev/null || true
rm -f "$TMP_ROOT/fake-loop-pid"
expect_in_output "a loop is already running (pid $fake_loop)"
[ ! -e "$LOOP_CHECKOUT" ] || fail "a refused launch must not make or move the loop checkout"
expect_started ""
pass "launcher: a running loop is refused, and nothing is moved or started"

# ── A run ──────────────────────────────────────────────────────────────────────────────────────
# A lock left by a loop that is gone does not stop the launcher; the conductor reclaims it.
reset_state
mkdir -p "$FAKE_REPO/.claude/backlog-loop.lock"
bash -c 'exit 0' &
gone=$!
wait "$gone"
echo "$gone" > "$FAKE_REPO/.claude/backlog-loop.lock/pid"
run_launcher 0 "launcher: the first run" "$LAUNCHER" -j 1 5 6
expect_started "5 6"
[ "$(loop_head)" = "$(origin_main)" ] || fail "the loop checkout should sit on origin/main" "$LAST_OUTPUT"
git -C "$LOOP_CHECKOUT" symbolic-ref -q HEAD >/dev/null && fail "the loop checkout should be detached"
for ticket in 5 6; do
    [ "$(cat "$STATE/copy-$ticket")" = "$LOOP_CHECKOUT/scripts/claude/work-ticket.sh" ] \
        || fail "#$ticket should run from the loop checkout, ran from $(cat "$STATE/copy-$ticket")"
done
grep -q "scripts/claude/backlog-loop.sh -j 1 5 6" "$STATE/caffeinate" \
    || fail "the conductor should run under caffeinate with every argument" "$(cat "$STATE/caffeinate" 2>/dev/null)"
[ "$(console_logs | wc -l | tr -d ' ')" = "1" ] || fail "one console copy expected" "$(console_logs)"
console="$(console_logs)"
grep -qF "[conductor] run $FAKE_REPO/logs/claude-loop/" "$console" || fail "the console copy should name the run folder" "$(cat "$console")"
grep -qF "done: 2 PR opened" "$console" || fail "the console copy should end with the roll-up" "$(cat "$console")"
grep -qF "start-loop: loop code" "$console" || fail "the console copy should name the loop code" "$(cat "$console")"
expect_in_output "done: 2 PR opened"
[ ! -d "$FAKE_REPO/.claude/backlog-loop.lock" ] || fail "the lock outlived the run"
pass "launcher: the first run makes the loop checkout on origin/main, runs from there and copies the console"

# The main checkout sits on a branch with other loop code, and origin/main has moved since the last
# run. The conductor refuses loop code that differs from origin/main, so this run works only from a
# loop checkout that was moved.
reset_state
git -C "$FAKE_REPO" checkout -q -b old-branch
echo "# an older conductor" >> "$FAKE_REPO/scripts/claude/backlog-loop.sh"
git -C "$FAKE_REPO" commit -qam "older loop code"
move_origin_main merged-1.txt
run_launcher 0 "launcher: another branch in the main checkout" "$LAUNCHER" -j 1 7
expect_started "7"
[ "$(loop_head)" = "$(origin_main)" ] || fail "the loop checkout should have moved to the new origin/main" "$LAST_OUTPUT"
[ -f "$LOOP_CHECKOUT/merged-1.txt" ] || fail "the loop checkout should hold what was merged"
git -C "$FAKE_REPO" checkout -q main
pass "launcher: runs from any folder while the main checkout is on another branch with other loop code"

# A stale copy of the launcher, here with a local edit in the main checkout, must not do the run.
reset_state
awk '/^log="/ { print "echo \"the stale launcher did the run\"" } { print }' "$LAUNCHER" > "$TMP_ROOT/stale"
cat "$TMP_ROOT/stale" > "$LAUNCHER"
run_launcher 0 "launcher: a stale copy" "$LAUNCHER" 8
git -C "$FAKE_REPO" checkout -q -- scripts/claude/start-loop.sh
expect_started "8"
printf '%s' "$LAST_OUTPUT" | grep -qF "the stale launcher did the run" \
    && fail "the stale copy did the run instead of handing over" "$LAST_OUTPUT"
pass "launcher: a stale copy hands the run over to the copy on origin/main"

# The copy in the loop checkout itself moves its own checkout to a newer launcher, then hands over
# to the new file.
reset_state
awk '/^log="/ { print "echo \"the launcher from the new main did the run\"" } { print }' \
    "$TMP_ROOT/pusher/scripts/claude/start-loop.sh" > "$TMP_ROOT/newer"
cat "$TMP_ROOT/newer" > "$TMP_ROOT/pusher/scripts/claude/start-loop.sh"
git -C "$TMP_ROOT/pusher" commit -qam "a newer launcher"
git -C "$TMP_ROOT/pusher" push -q origin HEAD:main
run_launcher 0 "launcher: the loop checkout's own copy" "$LOOP_CHECKOUT/scripts/claude/start-loop.sh" 9
expect_started "9"
expect_in_output "the launcher from the new main did the run"
[ "$(loop_head)" = "$(origin_main)" ] || fail "the loop checkout should sit on the newer origin/main" "$LAST_OUTPUT"
pass "launcher: the loop checkout's own copy moves its checkout and hands over to the newer launcher"

# ── What is refused ────────────────────────────────────────────────────────────────────────────
reset_state
echo "half done" > "$LOOP_CHECKOUT/notes.txt"
head_before="$(loop_head)"
move_origin_main merged-2.txt
run_launcher 1 "launcher: a dirty loop checkout" "$LAUNCHER" 19
expect_in_output "has local changes"
expect_in_output "notes.txt"
expect_started ""
[ -f "$LOOP_CHECKOUT/notes.txt" ] || fail "the local change must stay"
[ "$(loop_head)" = "$head_before" ] || fail "a dirty loop checkout must not be moved"
rm "$LOOP_CHECKOUT/notes.txt"
pass "launcher: a loop checkout with local changes is refused and left as it is"

reset_state
git -C "$FAKE_REPO" worktree remove "$LOOP_CHECKOUT"
mkdir -p "$LOOP_CHECKOUT"
echo "someone else's files" > "$LOOP_CHECKOUT/readme.txt"
run_launcher 1 "launcher: a foreign folder" "$LAUNCHER" 10
expect_in_output "is not a checkout of this repository"
expect_started ""
[ "$(cat "$LOOP_CHECKOUT/readme.txt")" = "someone else's files" ] || fail "a foreign folder must be left as it is"
rm -rf "$LOOP_CHECKOUT"
pass "launcher: a folder at the loop checkout's path that is not this repository's checkout is refused"

# ── Stopping a run ─────────────────────────────────────────────────────────────────────────────
# stop_case <signal> <description> — starts a two-ticket run in a process group of its own, as a
# terminal does, waits until both workers work, and sends the signal to the whole group, as Ctrl-C
# (INT) or a closed terminal (HUP) does.
stop_case() {
    local signal="$1" description="$2" launcher_pid console
    reset_state
    set -m
    (cd "$TMP_ROOT/elsewhere" && FAKE_WORK_SEC=60 exec "$LAUNCHER" -j 2 11 12) > "$TMP_ROOT/stop.out" 2>&1 &
    launcher_pid=$!
    set +m
    for _ in $(seq 1 100); do [ "$(wc -l < "$STATE/started" | tr -d ' ')" -ge 2 ] && break; "$REAL_SLEEP" 0.1; done
    if [ "$(wc -l < "$STATE/started" | tr -d ' ')" -lt 2 ]; then
        kill -TERM -- -"$launcher_pid" 2>/dev/null || true
        fail "$description — both workers should have started" "$(cat "$TMP_ROOT/stop.out")"
    fi
    kill -"$signal" -- -"$launcher_pid"
    # Bash reports a job that a signal ended on stderr; that line is expected here.
    {
        for _ in $(seq 1 300); do group_empty "$launcher_pid" && break; "$REAL_SLEEP" 0.1; done
        if ! group_empty "$launcher_pid"; then
            kill -KILL -- -"$launcher_pid" 2>/dev/null || true
            fail "$description — the run did not end within 30 seconds of $signal" "$(cat "$TMP_ROOT/stop.out")"
        fi
        wait "$launcher_pid" || true
    } 2>/dev/null
    console="$(console_logs)"
    for ticket in 11 12; do
        [ -e "$STATE/salvaged-$ticket" ] \
            || fail "$description — worker #$ticket was killed before it salvaged" "$(cat "$console")"
        grep -qF "#$ticket STOPPED — session killed, changes salvaged" "$console" \
            || fail "$description — the console copy lost the cleanup of #$ticket" "$(cat "$console")"
    done
    [ ! -d "$FAKE_REPO/.claude/backlog-loop.lock" ] || fail "$description — the lock outlived the run"
    pass "$description"
}

stop_case INT "launcher: Ctrl-C stops the run, every worker salvages, and the console copy keeps it all"
stop_case HUP "launcher: a closed terminal stops the run, every worker salvages, and the console copy keeps it all"

printf 'All %d launcher case(s) passed.\n' "$cases"
