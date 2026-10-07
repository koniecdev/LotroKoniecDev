#!/usr/bin/env bash
# Starts the backlog loop in THIS terminal — the one way to start it.
#
# Never start the loop from a Claude Code session. Since Claude Code 2.1.285, a command that the
# Bash tool runs in the background is killed after two hours at most, and the kill is a SIGKILL to
# the whole process tree. No trap runs then: the lock stays, no worker salvages its work, no resume
# marker is written, and the next run skips those tickets. A normal batch runs five hours or more
# (#969). A plain terminal has no such limit.
#
# The conductor runs from its own detached checkout next to the main one (`<main checkout>-loop`),
# moved to origin/main first, so the main checkout may sit on any branch. Whichever copy of this
# script you start, it then hands over to the copy in that checkout, so all the loop code that runs
# is the reviewed code on origin/main.
#
# Ctrl-C, or closing the terminal, stops the loop, and each worker salvages its work first. The
# console is copied to logs/claude-loop/console-<timestamp>.log in the main checkout; /backlog with
# no arguments builds the roll-up from that file.
#
# Usage (from any folder):
#   scripts/claude/start-loop.sh 937 933 931        # exactly these tickets, up to 3 at once
#   scripts/claude/start-loop.sh -j 1 937 933       # one at a time, in this order
#   scripts/claude/start-loop.sh -n 3               # the next 3 ready tickets from the picker
#
# Every argument goes to backlog-loop.sh, and its env vars work here too: backlog-loop.sh --help.
set -euo pipefail

case "${1:-}" in
    '')
        echo "usage: start-loop.sh <ticket numbers…> [-j N]   e.g. start-loop.sh 937 933 931" >&2
        echo "       start-loop.sh -n N                        the next N ready tickets" >&2
        exit 1 ;;
    -h|--help)
        sed -n '2,/^set /p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'
        exit 0 ;;
esac

real_dir() { (cd "$1" && pwd -P); }

SELF_DIR="$(real_dir "$(dirname "$0")")"
COMMON_DIR="$(real_dir "$(git -C "$SELF_DIR" rev-parse --path-format=absolute --git-common-dir)")"
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
    git -C "$MAIN_ROOT" worktree add --quiet --detach "$LOOP_CHECKOUT" origin/main
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
if [ "$SELF_DIR/$(basename "$0")" != "$target" ] \
    || [ "$head_before" != "$(git -C "$LOOP_CHECKOUT" rev-parse HEAD)" ]; then
    if [ ! -x "$target" ]; then
        echo "start-loop: origin/main has no scripts/claude/start-loop.sh to hand over to" >&2
        exit 1
    fi
    exec "$target" "$@"
fi

log="$MAIN_ROOT/logs/claude-loop/console-$(date +%Y%m%d-%H%M%S).log"
mkdir -p "$(dirname "$log")"
printf 'start-loop: loop code %s\nstart-loop: console copy in %s\n' \
    "$(git -C "$LOOP_CHECKOUT" log --oneline -1 | cut -c1-80)" "$log" | tee "$log"

cd "$LOOP_CHECKOUT"
# caffeinate keeps macOS awake for the whole run; other systems run the loop as it is.
if command -v caffeinate >/dev/null 2>&1; then
    set -- caffeinate -is scripts/claude/backlog-loop.sh "$@"
else
    set -- scripts/claude/backlog-loop.sh "$@"
fi
# The copy ignores Ctrl-C and a closed terminal. If it ended first, a worker that writes its
# cleanup lines into the closed pipe would be killed by SIGPIPE before it salvaged anything.
"$@" 2>&1 | (trap '' INT HUP; exec tee -a "$log")
