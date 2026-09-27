#!/usr/bin/env bash
# Works ONE GitHub ticket in a FRESH headless Claude session inside its own git worktree, then
# judges the outcome. It stops at the pull request — nothing here merges (ADR-0060):
#   worktree from origin/main  →  claude -p "/work-ticket <n>"  →  STATUS: DONE (PR opened, waits for the owner's review)
#                                                             →  STATUS: BLOCKED (label `loop-blocked` + questions as issue comment)
#
# The worktree is `.claude/worktrees/ticket-<n>` under the main checkout — the same name a manual
# `/ticket` session uses — so the main checkout is never touched and several tickets can run at
# once. The session dies with the ticket, so nothing accumulates anywhere. The conductor
# (backlog-loop.sh) calls this once per ticket, up to `-j` at a time.
#
# Usage: work-ticket.sh <issue-number> [run-dir]
# Env:
#   LOOP_EFFORT             claude effort level (default: the worker effort from
#                           ~/.claude/model-policy.env when present, else high — reviews inside
#                           the session run at high via the code-reviewer agent definition)
#   LOOP_MODEL              model (default: the worker model from ~/.claude/model-policy.env
#                           when present, else opus — Opus 5.5 in the policy since
#                           2026-09-22)
#   LOOP_CONFIG_DIR         Claude config dir = which account runs the loop
#                           (default: ~/.claude-account1)
#   LOOP_GH_USER            gh account whose token backs the loop's gh write calls — labels and
#                           issue comments (default: koniecdev, the repo owner); an existing
#                           GH_TOKEN in the environment wins
#   LOOP_PERMISSION_MODE    default: auto, plus a loop-scoped --allowedTools Bash allowlist
#                           (git/gh/dotnet/scripts — see LOOP_ALLOWED_TOOLS below); this does NOT
#                           widen permissions of your interactive sessions
#   LOOP_ALLOWED_TOOLS      override the loop's Bash allowlist (space-separated rule list)
#   LOOP_UNSAFE=1           use --dangerously-skip-permissions instead (full overnight autonomy)
#   LOOP_MAX_BUDGET_USD     optional per-ticket API budget cap
#   LOOP_TICKET_TIMEOUT_MIN wall-clock kill switch per ticket (default: 90)
#   LOOP_KEEP_WORKTREE=1    keep the worktree after the run (default: remove it when it is clean;
#                           the branch always stays)
#   LOOP_TRUSTED_ASSOCIATIONS / LOOP_TRUSTED_LOGINS / LOOP_TRUST_GATE — see issue-trust.sh
#
# Exit codes: 0 PR opened · 2 blocked · 3 error (incl. a provenance gate that could not reach the API)
#             4 timeout · 6 usage limit hit · 10 could not prepare the worktree
#             11 issue refused by the provenance gate (untrusted author or commenter)
#             12 skipped: the ticket already has an open PR, or its worktree already exists
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$REPO_ROOT"

ISSUE="${1:?usage: work-ticket.sh <issue-number> [run-dir]}"
case "$ISSUE" in
    ''|*[!0-9]*) echo "work-ticket: not an issue number: '$ISSUE'" >&2; exit 3 ;;
esac

# Worktrees, logs and the lock live under the MAIN checkout even when this script runs from a
# worktree, so every run on the machine sees the same `.claude/worktrees/ticket-<n>` names.
MAIN_ROOT="$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")"
RUN_DIR="${2:-$MAIN_ROOT/logs/claude-loop/adhoc-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$RUN_DIR"

# Central per-role model/effort policy (maintainer's machine); explicit LOOP_* env still wins,
# and the hardcoded fallbacks keep a fresh clone self-contained.
if [ -f "$HOME/.claude/model-policy.env" ]; then
    # shellcheck disable=SC1091
    . "$HOME/.claude/model-policy.env"
fi
EFFORT="${LOOP_EFFORT:-${MODEL_POLICY_WORKER_EFFORT:-high}}"
MODEL="${LOOP_MODEL:-${MODEL_POLICY_WORKER_MODEL:-opus}}"
export CLAUDE_CONFIG_DIR="${LOOP_CONFIG_DIR:-$HOME/.claude-account1}"
PERMISSION_MODE="${LOOP_PERMISSION_MODE:-auto}"
TIMEOUT_MIN="${LOOP_TICKET_TIMEOUT_MIN:-90}"
KEEP_WORKTREE="${LOOP_KEEP_WORKTREE:-0}"

OUT="$RUN_DIR/ticket-$ISSUE.json"
ERR="$RUN_DIR/ticket-$ISSUE.stderr"
META="$RUN_DIR/ticket-$ISSUE.meta"
WT="$MAIN_ROOT/.claude/worktrees/ticket-$ISSUE"

for tool in claude gh jq git; do
    command -v "$tool" >/dev/null 2>&1 || { echo "work-ticket: missing dependency: $tool" >&2; exit 3; }
done

log() { echo "[loop] #$ISSUE $(date +%H:%M:%S) $*"; }

meta() { echo "$1=$2" >> "$META"; }

# Every exit path writes its outcome here, so the conductor's roll-up lists skipped and refused
# tickets too, not only the ones that got a session.
: > "$META"
meta issue "$ISSUE"

# ── gh identity: write calls must not depend on the machine's ACTIVE gh account ────────────────
# Labels and issue comments need write access, but the active gh account here is often the EMU
# work account, which GitHub bars from writing outside its enterprise ("Enterprise Managed User
# cannot access this content"). Mint the owner's token unless the caller set one.
if [ -z "${GH_TOKEN:-}" ]; then
    owner_token="$(gh auth token --user "${LOOP_GH_USER:-koniecdev}" 2>/dev/null || true)"
    if [ -n "$owner_token" ]; then
        export GH_TOKEN="$owner_token"
    else
        log "no gh token for '${LOOP_GH_USER:-koniecdev}' — gh write calls will use the active gh account"
    fi
fi

# ── Provenance gate: only maintainer-written issue text may become the worker's task ───────────
# This is the enforcement point, not next-ticket.sh: the picker merely *selects*, whereas the
# untrusted text reaches the LLM here. Explicit ticket numbers (backlog-loop.sh 123) and direct
# invocations skip the picker entirely, so the gate has to live in front of the session (ADR-0026).
trust_rc=0
"$REPO_ROOT/scripts/claude/issue-trust.sh" "$ISSUE" || trust_rc=$?
if [ "$trust_rc" -eq 1 ]; then
    meta outcome untrusted
    log "REFUSED by the provenance gate — untrusted writer (see above); no session spawned"
    exit 11
fi
if [ "$trust_rc" -ne 0 ]; then
    # An unreadable API is systemic, not a property of this ticket: report it as a session error
    # so the conductor's circuit breaker stops the run instead of "skipping" the whole backlog.
    meta outcome error
    log "provenance gate could not verify #$ISSUE (rc=$trust_rc) — treating as an error"
    exit 3
fi

# ── Never start a ticket that is already in flight ─────────────────────────────────────────────
# A ticket with an open PR is waiting for the owner's review, and an existing worktree means a
# manual `/ticket` session or an earlier run is still on it. Working it again would open a second
# PR for the same ticket, or fight over the same branch.
open_pr="$(gh pr list --state open --limit 200 --json number,headRefName \
    --jq "[.[] | select(.headRefName | startswith(\"$ISSUE-\")) | .number] | first // empty")" || {
    meta outcome error
    log "could not list open pull requests — treating as an error"
    exit 3
}
if [ -n "$open_pr" ]; then
    meta outcome skipped
    meta pr "$open_pr"
    log "SKIPPED — PR #$open_pr is already open for this ticket and waits for your review"
    exit 12
fi
if [ -e "$WT" ]; then
    meta outcome skipped
    log "SKIPPED — $WT already exists (another session may be on this ticket)"
    exit 12
fi

# ── A fresh worktree from origin/main ──────────────────────────────────────────────────────────
# Detached on purpose: the worker creates the ticket branch itself (`gh issue develop`). Several
# runs fetch at once, and a fetch that loses the ref lock to another one succeeds on a retry.
if ! git fetch --quiet origin main 2>/dev/null; then
    sleep 5
    git fetch --quiet origin main || { meta outcome no-worktree; log "could not fetch origin/main"; exit 10; }
fi
if ! git worktree add --quiet --detach "$WT" origin/main; then
    meta outcome no-worktree
    log "could not create the worktree $WT"
    exit 10
fi

# Commit (never delete, never stash) anything a session left behind: leftovers become ordinary
# named git history on a dedicated `loop-salvage/<issue>-<timestamp>` branch, cut from wherever
# the session got to (so partial commits on the ticket branch stay reachable from it too).
salvage() {
    [ -d "$WT" ] || return 0
    if [ -n "$(git -C "$WT" status --porcelain)" ]; then
        salvage_branch="loop-salvage/$ISSUE-$(date +%Y%m%d-%H%M%S)"
        git -C "$WT" checkout -b "$salvage_branch" --quiet 2>/dev/null || true
        git -C "$WT" add -A >/dev/null 2>&1 || true
        git -C "$WT" commit --quiet --no-verify \
            -m "claude-loop: salvage uncommitted work for #$ISSUE" \
            -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" >/dev/null 2>&1 || true
        log "leftover changes committed on branch $salvage_branch"
    fi
}

# Each E2E suite tags its images per worktree as `<repository>:<suite>-<folder>-<hash>` (#884), and
# nothing removes them with the worktree. A loop run removes its worktree, so it removes them too.
remove_e2e_images() {
    command -v docker >/dev/null 2>&1 || return 0
    local images image
    images="$(docker image ls --filter label=lotrokoniecdev.e2e --format '{{.Repository}}:{{.Tag}}' 2>/dev/null \
        | grep -F -- "-ticket-$ISSUE-" || true)"
    for image in $images; do
        docker image rm "$image" >/dev/null 2>&1 || true
    done
}

# Everything is committed by now, so removing a clean worktree loses nothing: the branch stays,
# and `git worktree add` brings the folder back in seconds for a follow-up fix.
finish() {
    salvage
    if [ "$KEEP_WORKTREE" = "1" ]; then
        log "worktree kept: $WT"
        return 0
    fi
    if git worktree remove "$WT" 2>/dev/null; then
        remove_e2e_images
    else
        log "worktree left in place (not clean): $WT"
    fi
}

# ── One fresh headless session for the whole ticket ────────────────────────────────────────────
ALLOWED_TOOLS="${LOOP_ALLOWED_TOOLS:-Bash(git:*) Bash(gh:*) Bash(dotnet:*) Bash(scripts/:*) Bash(./scripts/:*)}"

cmd=(claude -p "/work-ticket $ISSUE" --output-format json --model "$MODEL" --effort "$EFFORT")
if [ "${LOOP_UNSAFE:-0}" = "1" ]; then
    cmd+=(--dangerously-skip-permissions)
else
    # shellcheck disable=SC2086,SC2206
    cmd+=(--permission-mode "$PERMISSION_MODE" --allowedTools $ALLOWED_TOOLS)
fi
[ -n "${LOOP_MAX_BUDGET_USD:-}" ] && cmd+=(--max-budget-usd "$LOOP_MAX_BUDGET_USD")

log "fresh headless session starting in $WT (model=$MODEL, effort=$EFFORT, timeout=${TIMEOUT_MIN}m)"
start_epoch="$(date +%s)"

# The conductor stops a run by sending TERM to this script. A background child here ignores
# SIGINT and outlives its parent, so the session is killed explicitly on every way out. `pid` is
# cleared once the session is reaped, so the EXIT trap can never hit a recycled PID.
pid=""
trap 'if [ -n "$pid" ]; then kill "$pid" 2>/dev/null || true; fi' EXIT
trap 'exit 143' INT TERM HUP

set +e
( cd "$WT" && exec "${cmd[@]}" ) > "$OUT" 2> "$ERR" &
pid=$!
while kill -0 "$pid" 2>/dev/null; do
    sleep 30
    if [ $(( $(date +%s) - start_epoch )) -ge $(( TIMEOUT_MIN * 60 )) ]; then
        kill "$pid" 2>/dev/null
        sleep 5
        kill -9 "$pid" 2>/dev/null
        wait "$pid" 2>/dev/null
        pid=""
        set -e
        meta outcome timeout
        finish
        log "TIMEOUT after ${TIMEOUT_MIN}m — session killed, changes salvaged"
        exit 4
    fi
done
wait "$pid"
claude_rc=$?
pid=""
set -e

elapsed_min=$(( ( $(date +%s) - start_epoch ) / 60 ))

result="$(jq -r '.result // ""' "$OUT" 2>/dev/null || echo "")"
is_error="$(jq -r '.is_error // false' "$OUT" 2>/dev/null || echo "true")"
cost="$(jq -r '.total_cost_usd // 0' "$OUT" 2>/dev/null || echo 0)"
turns="$(jq -r '.num_turns // 0' "$OUT" 2>/dev/null || echo 0)"
meta cost "$cost"
meta turns "$turns"
meta minutes "$elapsed_min"

# ── Usage-limit / hard-error detection ─────────────────────────────────────────────────────────
# The CLI reports plan/rate limits as api_error_status 429 in the result JSON regardless of the
# message wording ("usage limit", "session limit", …) — trust that first; the wording grep stays
# as the fallback for stderr-only failures where no result JSON was written.
api_error_status="$(jq -r '.api_error_status // 0' "$OUT" 2>/dev/null || echo 0)"
combined="$result $(tail -c 2000 "$ERR" 2>/dev/null || true)"
if [ "$api_error_status" = "429" ] || echo "$combined" | grep -qiE 'usage limit|session limit|rate.?limit|overloaded|quota'; then
    meta outcome limit
    finish
    log "USAGE LIMIT hit — the conductor will sleep and retry"
    exit 6
fi
if [ "$claude_rc" -ne 0 ] || [ "$is_error" = "true" ]; then
    meta outcome error
    finish
    log "session ERROR (rc=$claude_rc, is_error=$is_error) — see $ERR"
    exit 3
fi

# ── Judge the worker's final message (STATUS contract from /work-ticket) ───────────────────────
if echo "$result" | grep -qE '^STATUS:[[:space:]]*BLOCKED'; then
    meta outcome blocked
    gh label create loop-blocked --color e36209 \
        --description "claude-loop: needs human input" --force >/dev/null 2>&1 || true
    gh issue edit "$ISSUE" --add-label loop-blocked >/dev/null 2>&1 || true
    comment="$(printf '🤖 **claude-loop: BLOCKED** — needs your input before the loop retries this ticket.\n\n%s' \
        "$(echo "$result" | head -c 4000)")"
    gh issue comment "$ISSUE" --body "$comment" >/dev/null 2>&1 || true
    finish
    log "BLOCKED — questions posted on the issue, labeled loop-blocked"
    exit 2
fi

if ! echo "$result" | grep -qE '^STATUS:[[:space:]]*DONE'; then
    meta outcome error
    finish
    log "no STATUS: DONE/BLOCKED contract in the final message — treating as error (see $OUT)"
    exit 3
fi

# ── DONE: verify the PR really exists (never trust a summary alone) ────────────────────────────
pr_url="$(echo "$result" | grep -oE 'https://github\.com/[^ )>,]+/pull/[0-9]+' | head -1 || true)"
pr_num=""
if [ -n "$pr_url" ]; then
    pr_num="${pr_url##*/}"
else
    pr_num="$(gh pr list --state open --json number,headRefName \
        --jq ".[] | select(.headRefName | startswith(\"$ISSUE-\")) | .number" | head -1 || true)"
fi
if [ -z "$pr_num" ]; then
    meta outcome error
    finish
    log "worker reported DONE but no PR found — treating as error"
    exit 3
fi
meta pr "$pr_num"
meta outcome pr-opened
finish
log "PR #$pr_num opened — waiting for your review (cost \$$cost, $turns turns, ${elapsed_min}m)"
exit 0
