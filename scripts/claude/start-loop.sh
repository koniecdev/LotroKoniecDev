#!/usr/bin/env bash
# Starts the backlog loop in THIS terminal — the one way to start it.
#
# Never start the loop from a Claude Code session. Since Claude Code 2.1.285, a command that the
# Bash tool runs in the background is killed after two hours at most, and the kill is a SIGKILL to
# the whole process tree. No trap runs then: the lock stays, no worker salvages its work, no resume
# marker is written, and the next run skips those tickets. A normal batch runs five hours or more.
# A plain terminal has no such limit, so this script refuses to run inside a session.
#
# The conductor runs from its own detached checkout next to the main one (`<main checkout>-loop`),
# moved to origin/main first, so the main checkout may sit on any branch. Whichever copy of this
# script you start, it then hands over to the copy in that checkout, so the run itself uses the
# reviewed code on origin/main.
#
# One Ctrl-C only prints a warning: the run is in a process group of its own, and this script
# counts the presses. A second Ctrl-C within 5 seconds, closing the terminal, or `kill` of this
# script stops the loop, and each worker salvages its work first. A `kill -9` of this script leaves
# the run going: stop it with `kill <pid in .claude/backlog-loop.lock/pid>`. To let the running
# tickets finish and start nothing new, run
# `touch .claude/backlog-loop.lock/stop-after-running` in the main checkout. The run cannot answer
# a prompt on the terminal, so git and ssh must get their credentials without one: a question there
# pauses the whole run, and this script then says so. The
# console is copied to logs/claude-loop/console-<timestamp>.log in the main checkout; /backlog with
# no arguments builds the roll-up from that file.
#
# Usage (from any folder; a symlink to this file works too):
#   scripts/claude/start-loop.sh 937 933 931        # exactly these tickets, up to 3 at once
#   scripts/claude/start-loop.sh -j 1 937 933       # one at a time, in this order
#   scripts/claude/start-loop.sh -n 3               # the next 3 ready tickets from the picker
#
# Every argument goes to backlog-loop.sh, and its env vars work here too: backlog-loop.sh --help.
set -euo pipefail

usage() {
    echo "usage: start-loop.sh <ticket numbers…> [-j N]   e.g. start-loop.sh 937 933 931" >&2
    echo "       start-loop.sh -n N                        the next N ready tickets" >&2
}

case "${1:-}" in
    '') usage; exit 1 ;;
    -h|--help)
        sed -n '2,/^set /p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
        exit 0 ;;
esac

# Claude Code sets CLAUDECODE in every command its Bash tool runs.
if [ -n "${CLAUDECODE:-}" ]; then
    echo "start-loop: run this in a plain terminal, never from a Claude Code session (it kills a background command after two hours)" >&2
    exit 1
fi

# The conductor treats a run with no ticket numbers and no count above zero as "every ready
# ticket", so `-j 2` alone would start the whole backlog.
tickets=0
count=0
set_work() {
    while [ $# -gt 0 ]; do
        case "$1" in
            -n) count="${2:-0}"; [ $# -lt 2 ] || shift ;;
            -j) [ $# -lt 2 ] || shift ;;
            [0-9]*) tickets=$((tickets + 1)) ;;
        esac
        shift
    done
}
set_work "$@"
case "$count" in ''|*[!0-9]*) count=0 ;; esac
if [ "$tickets" -eq 0 ] && [ "$((10#$count))" -eq 0 ]; then
    echo "start-loop: name the tickets, or -n N with N above zero — without either the loop would take every ready ticket" >&2
    usage
    exit 1
fi

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here.
real_dir() { (CDPATH='' cd -- "$1" && pwd -P); }

# A symlink, such as a wrapper in ~/.local/bin, must lead back to the repository it points into.
self="$0"
while [ -L "$self" ]; do
    link="$(readlink "$self")"
    case "$link" in
        /*) self="$link" ;;
        *) self="$(dirname "$self")/$link" ;;
    esac
done
SELF_DIR="$(real_dir "$(dirname "$self")")"
SELF="$SELF_DIR/$(basename "$self")"
if ! common_dir="$(git -C "$SELF_DIR" rev-parse --path-format=absolute --git-common-dir 2>/dev/null)"; then
    echo "start-loop: $SELF_DIR is not inside a checkout of the repository" >&2
    exit 1
fi
# A copy that sits in some other repository would fetch there and make a loop checkout for it.
if [ ! -x "$SELF_DIR/backlog-loop.sh" ]; then
    echo "start-loop: $SELF_DIR has no backlog-loop.sh — run the launcher inside the repository, or a symlink to it" >&2
    exit 1
fi
COMMON_DIR="$(real_dir "$common_dir")"
MAIN_ROOT="$(dirname "$COMMON_DIR")"
LOOP_CHECKOUT="$MAIN_ROOT-loop"

# The same test the conductor uses for its lock. It comes first, because moving the loop checkout
# would change the files under a conductor that is running from there.
lock_pid="$(cat "$MAIN_ROOT/.claude/backlog-loop.lock/pid" 2>/dev/null || true)"
if [ -n "$lock_pid" ] && kill -0 "$lock_pid" 2>/dev/null \
    && grep -q 'backlog-loop.sh' <<< "$(ps -p "$lock_pid" -o command= 2>/dev/null || true)"; then
    echo "start-loop: a loop is already running (pid $lock_pid) — one at a time" >&2
    exit 1
fi
# The conductor drops its lock as soon as it has told its workers to stop, and each worker may take
# some seconds more to salvage. They run from the loop checkout, so it must not move yet. Only a
# bash that runs the script counts, not an editor or a search that names the file.
processes="$(ps -A -o pid=,command= 2>/dev/null || true)"
case "$processes" in
    *"bash $LOOP_CHECKOUT/scripts/claude/work-ticket.sh "*)
        echo "start-loop: workers from $LOOP_CHECKOUT are still running:" >&2
        grep -F "bash $LOOP_CHECKOUT/scripts/claude/work-ticket.sh " <<< "$processes" >&2 || true
        echo "start-loop: wait until they end, or stop each one with kill <pid> (it salvages its work first)" >&2
        exit 1 ;;
esac

git -C "$MAIN_ROOT" fetch --quiet origin main
if [ ! -e "$LOOP_CHECKOUT" ]; then
    # --force re-creates a loop checkout whose folder was deleted by hand while git still lists it.
    # The path is free, so nothing else is overwritten.
    git -C "$MAIN_ROOT" worktree add --quiet --force --detach "$LOOP_CHECKOUT" origin/main
fi
loop_common_dir="$(git -C "$LOOP_CHECKOUT" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
if [ -z "$loop_common_dir" ] || [ "$(real_dir "$loop_common_dir")" != "$COMMON_DIR" ]; then
    echo "start-loop: $LOOP_CHECKOUT is not a checkout of this repository — move it away first" >&2
    exit 1
fi
if [ -n "$(git -C "$LOOP_CHECKOUT" status --porcelain)" ]; then
    echo "start-loop: $LOOP_CHECKOUT has local changes, and the loop runs from there:" >&2
    git -C "$LOOP_CHECKOUT" status --short >&2
    exit 1
fi
head_before="$(git -C "$LOOP_CHECKOUT" rev-parse HEAD)"
git -C "$LOOP_CHECKOUT" checkout --quiet --detach origin/main

# Git writes a changed file as a new file, so a copy of this script that bash is still reading from
# the loop checkout stays whole until the exec. The handed-over copy runs all of this again; it
# fetches once more, so it hands over again only when main moved in between.
target="$(real_dir "$LOOP_CHECKOUT")/scripts/claude/start-loop.sh"
if [ "$SELF" != "$target" ] || [ "$head_before" != "$(git -C "$LOOP_CHECKOUT" rev-parse HEAD)" ]; then
    if [ ! -x "$target" ]; then
        echo "start-loop: origin/main has no scripts/claude/start-loop.sh to hand over to" >&2
        exit 1
    fi
    exec "$target" "$@"
fi

log="$MAIN_ROOT/logs/claude-loop/console-$(date +%Y%m%d-%H%M%S).log"
mkdir -p "$(dirname "$log")"
printf 'start-loop: loop code %s\nstart-loop: console copy in %s\n' \
    "$(git -C "$LOOP_CHECKOUT" log --oneline -1 | cut -c1-80)" "$log" | tee -a "$log"

# A relative path the caller gave means a folder seen from where they started this script, so it
# becomes a full path before the cd below. pwd -P gives the real folder, so every program
# reads a ".." in the path the same way. The value came from the environment, so the
# conductor gets the new one.
case "${LOOP_CONFIG_DIR:-}" in ''|/*) ;; *) LOOP_CONFIG_DIR="$(pwd -P)/$LOOP_CONFIG_DIR" ;; esac
cd "$LOOP_CHECKOUT"
# The full path makes every script under it, which works out its own folder from that path, safe
# from CDPATH.
conductor="$LOOP_CHECKOUT/scripts/claude/backlog-loop.sh"

# What keeps the computer awake for the run. caffeinate keeps an idle Mac awake, but not a laptop
# with a closed lid. systemd-inhibit holds an idle and sleep lock on Linux while logind grants one,
# so a short lock is asked for first: a machine without logind, such as a container, refuses it,
# and the loop then runs as it is. Other systems run the loop as it is.
keep_awake=""
if command -v caffeinate >/dev/null 2>&1; then
    keep_awake=caffeinate
elif command -v systemd-inhibit >/dev/null 2>&1 \
    && systemd-inhibit --what=idle:sleep --who=claude-loop --why="backlog loop check" true >/dev/null 2>&1; then
    keep_awake=systemd-inhibit
fi

# The run gets a process group of its own. A terminal sends Ctrl-C to its foreground group only, so
# a press reaches this script and nothing of the run: not the conductor, the picker, the workers,
# or the git they run, which catches Ctrl-C while it holds a lock file. This script counts the
# presses. In a terminal Ctrl-C is also a copy reflex, so the first press only warns, and a second
# one within PRESS_WINDOW_SEC stops the run. SECONDS counts whole seconds, so the window is 5 to
# 6 seconds. A closed terminal and TERM to this script stop it at once.
# A stop sends TERM, then CONT, to the run's whole group, once: the reach a closed terminal had
# while the run shared the terminal's group. CONT wakes a member that the terminal stopped
# because it read from it. Later presses and signals do nothing, so they cannot cut a salvage
# short.
# Every PAUSE_CHECK_SEC this script looks at the run (watch_run), for two cases:
# - The run cannot answer a question on the terminal: when one of its processes reads the terminal,
#   the kernel stops the run's whole group. Before a stop the script says so, once per pause,
#   because only the owner can end it. During a stop it sends TERM and CONT again.
# - A stop that went out before the conductor started never reached it: systemd-inhibit, which
#   ignores TERM, starts the conductor some milliseconds after the run, and a stop noted before the
#   run's group was known went nowhere. During a stop the script sends TERM to the conductor once,
#   when it shows up in the run's group. The conductor ignores every TERM after its first.
# Either TERM goes out only when SECONDS has moved PAUSE_CHECK_SEC on since the last one, which is
# more than 1 real second: a second TERM within some milliseconds of the first cuts the conductor's
# or a worker's own stop short.
PRESS_WINDOW_SEC=5
PAUSE_CHECK_SEC=2
run_pid=""
stopping=0
last_press=""
paused=0
# SECONDS when the launcher last sent TERM to the run, and the conductor it sent TERM to on its own.
last_term=0
conductor_termed=""

# say <line> — to the terminal and to the console copy. It runs inside a trap, so it uses builtins
# only and never fails: the terminal may already be gone.
say() {
    printf '%s\n' "$1" 2>/dev/null || true
    printf '%s\n' "$1" >> "$log" 2>/dev/null || true
}

send_stop() {
    stopping=1
    [ -n "$run_pid" ] || return 0
    last_term=$SECONDS
    kill -TERM -- "-$run_pid" 2>/dev/null || true
    kill -CONT -- "-$run_pid" 2>/dev/null || true
}

on_ctrl_c() {
    [ "$stopping" -eq 0 ] || return 0
    if [ -n "$last_press" ] && [ $(( SECONDS - last_press )) -le "$PRESS_WINDOW_SEC" ]; then
        send_stop
        say "start-loop: Ctrl-C again — stopping the run; each running ticket salvages its work first"
        return 0
    fi
    last_press=$SECONDS
    say "start-loop: Ctrl-C — the run goes on. Press it again within ${PRESS_WINDOW_SEC}s to stop every running ticket; to let them finish and start nothing new: touch $MAIN_ROOT/.claude/backlog-loop.lock/stop-after-running"
}

on_stop_signal() {
    [ "$stopping" -eq 0 ] || return 0
    send_stop
    say "start-loop: $1 — stopping the run; each running ticket salvages its work first"
}

# One look at the run (see PAUSE_CHECK_SEC above). A press also reaches the `ps`, so a read that
# fails is read again, as in job_running; one that keeps failing (a `ps` that knows no `-o`, as in
# Git Bash) changes nothing. The list is taken apart by word splitting, which needs no temporary
# file. The conductor's number comes from its lock, and counts only while that process is
# in the run's group: a lock left by an old run names another process.
watch_run() {
    local listing field pid="" pgid="" n=0 tries=1 stopped=0 conductor_pid="" conductor_in_run=0
    # shellcheck disable=SC2006  # backquotes on purpose, see job_running
    until listing=`ps -A -o pid= -o pgid= -o stat= 2>/dev/null`; do
        [ "$tries" -lt 5 ] || return 0
        tries=$((tries + 1))
    done
    if [ "$stopping" -eq 1 ] && [ -z "$conductor_termed" ]; then
        read -r conductor_pid 2>/dev/null < "$MAIN_ROOT/.claude/backlog-loop.lock/pid" || conductor_pid=""
    fi
    # One pass over the fields, three per process: the number, its group, its state. None of them
    # holds a character a glob would expand.
    # shellcheck disable=SC2086  # split the list into its fields
    for field in $listing; do
        n=$((n + 1))
        case "$n" in
            1) pid="$field" ;;
            2) pgid="$field" ;;
            *)
                n=0
                [ "$pgid" = "$run_pid" ] || continue
                case "$field" in T*) stopped=1 ;; esac
                if [ -n "$conductor_pid" ] && [ "$pid" = "$conductor_pid" ]; then conductor_in_run=1; fi ;;
        esac
    done
    if [ "$stopping" -eq 1 ]; then
        [ $(( SECONDS - last_term )) -ge "$PAUSE_CHECK_SEC" ] || return 0
        if [ "$stopped" -eq 1 ]; then
            last_term=$SECONDS
            kill -TERM -- "-$run_pid" 2>/dev/null || true
            kill -CONT -- "-$run_pid" 2>/dev/null || true
            say "start-loop: the run was paused by a question on the terminal during the stop; sent it TERM again"
        elif [ "$conductor_in_run" -eq 1 ]; then
            last_term=$SECONDS
            conductor_termed="$conductor_pid"
            kill -TERM "$conductor_pid" 2>/dev/null || true
        fi
        return 0
    fi
    if [ "$stopped" -eq 0 ]; then
        paused=0
        return 0
    fi
    [ "$paused" -eq 0 ] || return 0
    paused=1
    say "start-loop: the run is paused — one of its processes asked a question on the terminal, which the run cannot answer. Press Ctrl-C twice to stop the run; each running ticket salvages its work first"
}

# The same read of the job list as the conductor's job_running, for the same reason: a press also
# reaches the child that reads it, and a read it killed lists nothing.
job_running() {
    local running tries=1
    # shellcheck disable=SC2006  # backquotes on purpose, see backlog-loop.sh
    until running=`jobs -rp`; do
        [ "$tries" -lt 5 ] || break
        tries=$((tries + 1))
    done
    case $'\n'"$running"$'\n' in *$'\n'"$1"$'\n'*) return 0 ;; esac
    return 1
}

# The traps come before the run starts. A subshell puts every trapped signal back at its default, so
# the run starts with nothing ignored; an ignored one would be passed on to all of it.
trap on_ctrl_c INT
trap 'on_stop_signal HUP' HUP
trap 'on_stop_signal TERM' TERM

# `set -m` gives the run its own group. Its leader is the subshell, because `$!` of a pipeline is
# its last process. The subshell turns job control off again so the pipeline stays in its group, as
# work-ticket.sh does for a session. Its no-op trap keeps it alive through the stop: it waits for
# the pipeline and then ends with the pipeline's status, so this script waits until every worker
# has salvaged. Under `set -m` bash does not point a background job's standard input at /dev/null,
# and a job outside the terminal's foreground group that reads the terminal is stopped.
# The copy ignores Ctrl-C, a closed terminal and TERM, and ends only when every writer has closed
# the pipe. If it ended first, a worker that writes its cleanup lines into the closed pipe would be
# killed by SIGPIPE before it salvaged anything. It also ignores SIGTTOU, which a terminal set to
# `stty tostop` sends to a writer outside its foreground group.
set -m
(
    set +m
    trap : TERM HUP
    case "$keep_awake" in
        caffeinate)
            caffeinate -is "$conductor" "$@" 2>&1 | (trap '' INT HUP TERM TTOU; exec tee -a "$log") ;;
        systemd-inhibit)
            # systemd-inhibit ignores Ctrl-C, a closed terminal and TERM: when it dies, it ends its
            # command with TERM, which would cut the conductor's salvage short. It starts the
            # conductor with every signal back at its default.
            (trap '' INT HUP TERM; exec systemd-inhibit --what=idle:sleep --who=claude-loop \
                --why="backlog loop running" "$conductor" "$@") 2>&1 | (trap '' INT HUP TERM TTOU; exec tee -a "$log") ;;
        *)
            "$conductor" "$@" 2>&1 | (trap '' INT HUP TERM TTOU; exec tee -a "$log") ;;
    esac
) < /dev/null &
run_pid=$!
set +m

# Each wait is a background sleep + `wait`, so a press runs its trap at once. A background job
# starts with INT ignored, so a press never ends the sleep. This script ends with the run's status,
# which is the conductor's, at most PAUSE_CHECK_SEC after the run ends.
while job_running "$run_pid"; do
    sleep "$PAUSE_CHECK_SEC" > /dev/null 2>&1 &
    wait "$!" 2>/dev/null || true
    watch_run
done
rc=0
wait "$run_pid" 2>/dev/null || rc=$?
exit "$rc"
