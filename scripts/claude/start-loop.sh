#!/usr/bin/env bash
# Starts the backlog loop in THIS terminal — the one way to start it.
#
# Never start the loop from a Claude Code session. Since Claude Code 2.1.285, a command that the
# Bash tool runs in the background is killed after two hours at most, and the kill is a SIGKILL to
# the whole process tree. No trap runs then: the lock stays, no worker salvages its work, no resume
# marker is written, and the next run skips those tickets. A normal batch runs five hours or more
# (#969). A plain terminal has no such limit, so this script refuses to run inside a session.
#
# The conductor runs from its own detached checkout next to the main one (`<main checkout>-loop`),
# moved to origin/main first, so the main checkout may sit on any branch. Whichever copy of this
# script you start, it then hands over to the copy in that checkout, so the run itself uses the
# reviewed code on origin/main.
#
# Ctrl-C, or closing the terminal, stops the loop, and each worker salvages its work first. The
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
    echo "start-loop: run this in a plain terminal, never from a Claude Code session (#969)" >&2
    echo "start-loop: only if this terminal inherited CLAUDECODE by mistake: env -u CLAUDECODE $0 …" >&2
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

real_dir() { (cd "$1" && pwd -P); }

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
COMMON_DIR="$(real_dir "$common_dir")"
MAIN_ROOT="$(dirname "$COMMON_DIR")"
LOOP_CHECKOUT="$MAIN_ROOT-loop"

# The same test the conductor uses for its lock. It comes first, because moving the loop checkout
# would change the files under a conductor that is running from there.
lock_pid="$(cat "$MAIN_ROOT/.claude/backlog-loop.lock/pid" 2>/dev/null || true)"
if [ -n "$lock_pid" ] && kill -0 "$lock_pid" 2>/dev/null \
    && ps -p "$lock_pid" -o command= 2>/dev/null | grep -q 'backlog-loop.sh'; then
    echo "start-loop: a loop is already running (pid $lock_pid) — one at a time" >&2
    exit 1
fi

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

cd "$LOOP_CHECKOUT"
# caffeinate keeps macOS awake for the whole run; other systems run the loop as it is.
if command -v caffeinate >/dev/null 2>&1; then
    set -- caffeinate -is scripts/claude/backlog-loop.sh "$@"
else
    set -- scripts/claude/backlog-loop.sh "$@"
fi
# The copy ignores Ctrl-C, a closed terminal and TERM (a logout sends it to every process), and
# ends only when every writer has closed the pipe. If it ended first, a worker that writes its
# cleanup lines into the closed pipe would be killed by SIGPIPE before it salvaged anything.
"$@" 2>&1 | (trap '' INT HUP TERM; exec tee -a "$log")
