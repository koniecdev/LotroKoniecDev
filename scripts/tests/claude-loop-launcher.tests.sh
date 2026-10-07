#!/usr/bin/env bash
# Test suite for the backlog loop's terminal launcher, scripts/claude/start-loop.sh (#969).
#
# The loop runs for hours, so it must run in a plain terminal, never from a Claude Code session,
# whose background commands are killed after two hours at most. The launcher is what makes that
# one command. This suite pins its contract:
#   * nothing starts without arguments, inside a Claude Code session, while a loop runs, or when
#     the arguments would take every ready ticket (`-j N` alone, `-n 0`),
#   * the loop runs from its own checkout next to the main one, moved to origin/main first, so it
#     runs from any folder (here: from inside another repository), through a symlink, while the
#     main checkout is on any branch, with any loop code there,
#   * whichever copy of the launcher you start, the copy on origin/main does the run,
#   * a loop checkout deleted by hand is made again; one with local changes, or a folder there that
#     is not this repository's checkout, is refused and left as it is,
#   * a lock whose process number now belongs to another program does not stop a run, and a worker
#     of the last run that still salvages keeps the loop checkout where it is,
#   * without caffeinate (Linux), and with CDPATH exported, the run still works,
#   * the console is copied to logs/claude-loop/console-<timestamp>.log in the main checkout,
#   * Ctrl-C, a closed terminal, or TERM to the whole group stops the run, and each worker salvages
#     its work first: the copy of the console outlives the stop, so a worker that writes its
#     cleanup lines is not killed by SIGPIPE.
#
# The real launcher drives the real conductor. `work-ticket.sh` and `next-ticket.sh` are fakes, and
# `gh`, `sleep`, `osascript` and `caffeinate` are stubbed, so the suite is offline and runs in
# seconds. It needs perl, which macOS and Ubuntu both ship.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
REAL_SLEEP="$(command -v sleep)"
export REAL_SLEEP

TMP_ROOT="$(cd "$(mktemp -d)" && pwd -P)"
cleanup() {
    # A failed case can leave a launcher, a fake worker or a fake loop running; never leave one
    # behind. Each launcher leads a process group of its own, and its console copy ignores TERM.
    local leftover
    while read -r leftover; do
        kill -KILL -- -"$leftover" 2>/dev/null || true
    done < <(cat "$TMP_ROOT/groups" 2>/dev/null || true)
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
# origin/main, not because the caller switched it off. A developer may run this suite from a
# Claude Code session, whose CLAUDECODE the launcher refuses; one case sets it on purpose.
unset LOOP_ALLOW_LOCAL_SCRIPTS CLAUDECODE

FAKE_REPO="$TMP_ROOT/fake-repo"
LOOP_CHECKOUT="$TMP_ROOT/fake-repo-loop"
LAUNCHER="$FAKE_REPO/scripts/claude/start-loop.sh"
export STATE="$TMP_ROOT/state"
mkdir -p "$FAKE_REPO/scripts/claude" "$TMP_ROOT/bin" "$TMP_ROOT/links"
# Every case starts the launcher from inside another repository, so a launcher that falls back to
# the folder it is started in would work on the wrong one.
ELSEWHERE="$TMP_ROOT/elsewhere"
git init -q -b main "$ELSEWHERE"
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
# A signal to the whole group ends this sleep as well. Like the real worker, which waits with errexit
# off, the fake must not exit on that before its stop handler runs.
wait "$sleeper" || true
sleeper=""
printf 'issue=%s\noutcome=pr-opened\npr=%s\n' "$ticket" "$((ticket + 1000))" > "$run_dir/ticket-$ticket.meta"
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

# run_launcher <expected-exit> <description> <launcher> <args...> — runs from $RUN_FROM, by default
# a folder outside the repository, in a process group of its own, and gives up after 60 seconds
# instead of hanging the suite.
run_launcher() {
    local expected="$1" description="$2" launcher="$3" rc=0 pid tenths=0
    shift 3
    set -m
    (cd "${RUN_FROM:-$ELSEWHERE}" && exec "$launcher" "$@") < /dev/null > "$TMP_ROOT/launcher.out" 2>&1 &
    pid=$!
    set +m
    echo "$pid" >> "$TMP_ROOT/groups"
    while kill -0 "$pid" 2>/dev/null; do
        if [ "$tenths" -ge 600 ]; then
            # The console copy ignores TERM, so the whole group gets KILL after a short grace.
            kill -TERM -- -"$pid" 2>/dev/null || true
            "$REAL_SLEEP" 2
            kill -KILL -- -"$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
            fail "$description — the launcher was still running after 60s" "$(tail -20 "$TMP_ROOT/launcher.out")"
        fi
        "$REAL_SLEEP" 0.1
        tenths=$((tenths + 1))
    done
    { wait "$pid" || rc=$?; } 2>/dev/null
    LAST_OUTPUT="$(cat "$TMP_ROOT/launcher.out")"
    if [ "$rc" -ne "$expected" ]; then
        fail "$description — expected exit $expected, got $rc" "$LAST_OUTPUT"
    fi
    [ ! -e "$ELSEWHERE-loop" ] || fail "$description — a loop checkout was made for the folder the launcher started in" "$LAST_OUTPUT"
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

group_empty() {
    ps -A -o pgid= -o stat= | awk -v group="$1" '$1 == group && $2 !~ /^Z/ { left = 1 } END { exit left }'
}

# ── What starts nothing ────────────────────────────────────────────────────────────────────────
reset_state
run_launcher 1 "launcher: no arguments" "$LAUNCHER"
expect_in_output "usage: start-loop.sh"
[ ! -e "$LOOP_CHECKOUT" ] || fail "a launcher without arguments must not make the loop checkout"
pass "launcher: no arguments print the usage and start nothing"

reset_state
run_launcher 1 "launcher: -j alone" "$LAUNCHER" -j 2
expect_in_output "without either the loop would take every ready ticket"
run_launcher 1 "launcher: -n 0" "$LAUNCHER" -n 0
expect_in_output "without either the loop would take every ready ticket"
[ ! -e "$LOOP_CHECKOUT" ] || fail "a refused launch must not make the loop checkout"
expect_started ""
pass "launcher: arguments that would take every ready ticket (-j alone, -n 0) are refused"

# A Bash tool command of a Claude Code session dies at the tool's time limit (#969).
reset_state
CLAUDECODE=1 run_launcher 1 "launcher: inside a Claude Code session" "$LAUNCHER" 5
expect_in_output "never from a Claude Code session"
[ ! -e "$LOOP_CHECKOUT" ] || fail "a refused launch must not make the loop checkout"
expect_started ""
pass "launcher: a start from inside a Claude Code session is refused"

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
grep -qF "done: 2 PR opened" "$console" || fail "the console copy should hold the roll-up" "$(cat "$console")"
grep -qF "start-loop: loop code" "$console" || fail "the console copy should name the loop code" "$(cat "$console")"
expect_in_output "done: 2 PR opened"
[ ! -d "$FAKE_REPO/.claude/backlog-loop.lock" ] || fail "the lock outlived the run"
pass "launcher: the first run makes the loop checkout on origin/main, runs from there and copies the console"

# The lock's process number now belongs to a program that is not a conductor.
reset_state
mkdir -p "$FAKE_REPO/.claude/backlog-loop.lock"
"$REAL_SLEEP" 30 &
stranger=$!
echo "$stranger" > "$TMP_ROOT/fake-loop-pid"
echo "$stranger" > "$FAKE_REPO/.claude/backlog-loop.lock/pid"
run_launcher 0 "launcher: a lock with a reused process number" "$LAUNCHER" 15
kill "$stranger" 2>/dev/null || true
wait "$stranger" 2>/dev/null || true
rm -f "$TMP_ROOT/fake-loop-pid"
expect_started "15"
expect_in_output "stale lock (owner gone)"
pass "launcher: a lock whose process number now belongs to another program does not stop a run"

reset_state
echo "16 17" > "$STATE/backlog"
run_launcher 0 "launcher: -n 1" "$LAUNCHER" -n 1
expect_started "16"
pass "launcher: -n N runs the next N ready tickets"

# A wrapper in ~/.local/bin is often a symlink.
reset_state
ln -s "$LAUNCHER" "$TMP_ROOT/links/backlog"
run_launcher 0 "launcher: through a symlink" "$TMP_ROOT/links/backlog" 18
expect_started "18"
pass "launcher: a symlink to the launcher runs the loop of the repository it points into"

# A copy instead of a symlink, as the first wrapper in ~/.local/bin was: outside any repository, and
# inside another one.
reset_state
mkdir -p "$TMP_ROOT/loose" "$ELSEWHERE/tools"
cp "$LAUNCHER" "$TMP_ROOT/loose/start-loop.sh"
cp "$LAUNCHER" "$ELSEWHERE/tools/start-loop.sh"
run_launcher 1 "launcher: a copy outside any repository" "$TMP_ROOT/loose/start-loop.sh" 22
expect_in_output "is not inside a checkout of the repository"
run_launcher 1 "launcher: a copy inside another repository" "$ELSEWHERE/tools/start-loop.sh" 22
expect_in_output "has no backlog-loop.sh"
rm -rf "$TMP_ROOT/loose" "$ELSEWHERE/tools"
expect_started ""
pass "launcher: a copy of the launcher outside the repository is refused before it touches anything"

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
# Without the line, the check below would pass whether or not the copy hands over.
grep -qF "the stale launcher did the run" "$LAUNCHER" || fail "the stale copy got no marker line: the awk anchor no longer matches"
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

# Deleted by hand, while git still lists it as a worktree.
reset_state
rm -rf "$LOOP_CHECKOUT"
run_launcher 0 "launcher: a deleted loop checkout" "$LAUNCHER" 20
expect_started "20"
[ "$(loop_head)" = "$(origin_main)" ] || fail "the loop checkout should be made again on origin/main" "$LAST_OUTPUT"
pass "launcher: a loop checkout deleted by hand is made again"

# A worker of the last run still salvages from the loop checkout after its conductor dropped the
# lock. The checkout must not move under it, even though origin/main has moved.
reset_state
mkdir -p "$TMP_ROOT/other-state" "$TMP_ROOT/other-run"
STATE="$TMP_ROOT/other-state" FAKE_WORK_SEC=30 "$LOOP_CHECKOUT/scripts/claude/work-ticket.sh" 98 "$TMP_ROOT/other-run" \
    > /dev/null 2>&1 &
ending_worker=$!
echo "$ending_worker" > "$TMP_ROOT/fake-loop-pid"
for _ in $(seq 1 50); do [ -s "$TMP_ROOT/other-state/started" ] && break; "$REAL_SLEEP" 0.1; done
head_before="$(loop_head)"
move_origin_main merged-3.txt
run_launcher 1 "launcher: workers still ending" "$LAUNCHER" 23
kill "$ending_worker" 2>/dev/null || true
wait "$ending_worker" 2>/dev/null || true
rm -f "$TMP_ROOT/fake-loop-pid"
expect_in_output "workers of the last run are still ending"
expect_started ""
[ "$(loop_head)" = "$head_before" ] || fail "the loop checkout moved under a worker that still ran from it"
pass "launcher: a worker of the last run that still salvages keeps the loop checkout where it is"

# Linux has no caffeinate, so that path runs the conductor by itself. Every caffeinate on PATH is
# hidden: a folder that holds one is replaced by a folder of links to everything else in it.
reset_state
no_caffeinate_path=""
farm=0
IFS=: read -r -a path_dirs <<< "$PATH"
for dir in "${path_dirs[@]}"; do
    if [ -e "$dir/caffeinate" ]; then
        farm=$((farm + 1))
        mkdir -p "$TMP_ROOT/path-$farm"
        find "$dir" -mindepth 1 -maxdepth 1 ! -name caffeinate \
            -exec sh -c 'ln -s "$@" "$0"' "$TMP_ROOT/path-$farm/" {} + 2>/dev/null || true
        dir="$TMP_ROOT/path-$farm"
    fi
    no_caffeinate_path="$no_caffeinate_path${no_caffeinate_path:+:}$dir"
done
PATH="$no_caffeinate_path" command -v caffeinate >/dev/null && fail "caffeinate should be hidden for this case"
PATH="$no_caffeinate_path" run_launcher 0 "launcher: no caffeinate" "$LAUNCHER" 24
expect_started "24"
[ ! -e "$STATE/caffeinate" ] || fail "no caffeinate should have run" "$(cat "$STATE/caffeinate")"
pass "launcher: without caffeinate the conductor runs by itself"

# With CDPATH exported, `cd` to a relative folder can print it, and the launcher started by a
# relative path from the main checkout must not mistake that output for its own folder. Started
# through `bash`, it keeps the relative path as $0, as it does when typed in zsh; bash's own exec
# would hand it the full path.
reset_state
RUN_FROM="$FAKE_REPO" CDPATH=".:$TMP_ROOT" run_launcher 0 "launcher: CDPATH" bash scripts/claude/start-loop.sh 25
expect_started "25"
pass "launcher: an exported CDPATH does not confuse a launcher started by a relative path"

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

# origin/main from before the launcher existed.
reset_state
git -C "$TMP_ROOT/pusher" rm -q scripts/claude/start-loop.sh
git -C "$TMP_ROOT/pusher" commit -qm "no launcher yet"
git -C "$TMP_ROOT/pusher" push -q origin HEAD:main
run_launcher 1 "launcher: origin/main without a launcher" "$LAUNCHER" 21
expect_in_output "origin/main has no scripts/claude/start-loop.sh to hand over to"
expect_started ""
git -C "$TMP_ROOT/pusher" revert --no-edit HEAD >/dev/null
git -C "$TMP_ROOT/pusher" push -q origin HEAD:main
pass "launcher: an origin/main without a launcher to hand over to is refused"

# ── Stopping a run ─────────────────────────────────────────────────────────────────────────────
# stop_case <signal> <description> — starts a two-ticket run in a process group of its own, as a
# terminal does, waits until both workers work, and sends the signal to the whole group, as Ctrl-C
# (INT), a closed terminal (HUP) or `kill -- -<group>` (TERM) does.
stop_case() {
    local signal="$1" description="$2" launcher_pid console
    reset_state
    # A shell starts its background jobs with INT ignored, and bash can never trap a signal that was
    # ignored when it started. So when this suite itself runs in the background, a launcher started
    # from here would never see the Ctrl-C. A terminal starts it with every signal at its default,
    # and perl puts them back to that.
    set -m
    (cd "$ELSEWHERE" && FAKE_WORK_SEC=60 exec perl -e '$SIG{$_} = "DEFAULT" for qw(INT HUP TERM); exec @ARGV or die "exec: $!\n"' \
        "$LAUNCHER" -j 2 11 12) < /dev/null > "$TMP_ROOT/stop.out" 2>&1 &
    launcher_pid=$!
    set +m
    echo "$launcher_pid" >> "$TMP_ROOT/groups"
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
stop_case TERM "launcher: TERM to the whole group stops the run, every worker salvages, and the console copy keeps it all"

printf 'All %d launcher case(s) passed.\n' "$cases"
