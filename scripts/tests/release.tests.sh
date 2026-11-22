#!/usr/bin/env bash
# Test suite for scripts/release.sh (#1440): the release the owner runs in a terminal.
#
# Each case builds a fixture repo with a local bare origin and a stub `gh` that plays GitHub: the
# runs of each commit, the CD runs with their prod job, the production gate, the deployments, the
# pull requests with their closing issues, the issues and the release. A CD run moves to its next
# state each time the script reads it, and an approval, a rejection or a re-run gives it a new list
# of states, so the script's whole wait-and-review loop runs here in a second. The stub runs the
# script's --jq expressions through the real jq. A stub `claude` writes the notes, fails, or hangs.
#
# The real gate cannot be tried in CI: the first real run of the script is its live proof (#1440).
#
# The last case reads cd.yml, deploy.yml and gitleaks.yml, so pr-verify runs this suite on every PR.
set -euo pipefail

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
REPO_ROOT="$(CDPATH='' cd -- "$SCRIPTS_DIR/.." && pwd)"
RELEASE_SH="$SCRIPTS_DIR/release.sh"
TMP_ROOT="$(mktemp -d)"

PTY_PID=""
stop_leftovers() {
    local pid_file pid
    [ -z "$PTY_PID" ] || kill "$PTY_PID" 2>/dev/null || true
    for pid_file in "$TMP_ROOT"/case-*/stub/claude.pid; do
        [ -f "$pid_file" ] || continue
        pid="$(cat "$pid_file")"
        kill "$pid" 2>/dev/null || true
    done
    rm -rf "$TMP_ROOT"
}
trap stop_leftovers EXIT

for var in $(git rev-parse --local-env-vars); do
    unset "$var"
done
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=test GIT_AUTHOR_EMAIL=test@test.invalid
export GIT_COMMITTER_NAME=test GIT_COMMITTER_EMAIL=test@test.invalid
unset GIT_EDITOR VISUAL EDITOR

command -v jq >/dev/null || { echo 'release.tests.sh needs jq' >&2; exit 2; }
script_version="$(script --version 2>&1 || true)"
grep -qF util-linux <<< "$script_version" || { echo 'release.tests.sh needs script from util-linux (the terminal cases)' >&2; exit 2; }
TIMEOUT=()
if command -v timeout >/dev/null; then
    TIMEOUT=(timeout 60)
fi

ADR_PATH='docs/adr/0070-a-release-is-a-git-tag-and-cd-builds-each-commit-once.md'
CI='.github/workflows/ci.yml'
GITLEAKS='.github/workflows/gitleaks.yml'
CI_CREATED='2026-11-22T10:00:00Z'
CD_CREATED='2026-11-22T10:05:00Z'
DEPLOYING='status=in_progress prod=in_progress steps=4'
DEPLOYED='status=in_progress prod=completed pconc=success steps=12 nr=in_progress deploy=1'
DONE='status=completed conclusion=success prod=completed pconc=success steps=12 nr=success'
REJECTED='status=completed conclusion=failure prod=completed pconc=failure steps=0 nr=none'
CANCELLED='status=completed conclusion=cancelled prod=completed pconc=cancelled steps=0 nr=none'

cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- release.sh output (exit %s) ---\n%s\n' "$LAST_STATUS" "$LAST_OUTPUT" | sed 's/^/    /'
    printf '  --- calls.log ---\n' && sed 's/^/    /' "$STUB/calls.log" 2>/dev/null || true
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Stub gh: GitHub as files ---------------------------------------------------------------------
#   $STUB/commit-runs/<sha>[.1 .2 …]  the push runs of a commit, one per line:
#                                     id, workflow path, status, conclusion, attempt, created, url
#                                     (tab-separated); numbered files are served in turn, the last
#                                     one stays
#   $STUB/runs/<id>/{title,event,created}   a CD run
#   $STUB/runs/<id>/state             its current state: status= conclusion= prod= (the prod job's
#                                     status, or none or skipped) pconc= steps= nr= (the issue job's
#                                     conclusion, or none) deploy=1 (adds a prod deployment record)
#   $STUB/runs/<id>/queue             its next states, one per line; a read of the run takes one
#   $STUB/runs/<id>/on-approve|on-reject|on-rerun   the states that follow that event
#   $STUB/runs/<id>/hidden            the number of run listings that do not show it yet
#   $STUB/runs/<id>/tick-on-list      a listing of runs also moves it to its next state
#   $STUB/runs/<id>/approve-fail      "once" or "always"; approve-fail-then: the state it leaves
#   $STUB/runs/<id>/rerun-fail, $STUB/can-approve (true | false)
#   $STUB/deployments                 production deployments, newest first: id, run id
#                                     (deploy_run=<id> adds one for another run, such as a later hand run)
#   $STUB/release-fail, $STUB/crlf    `gh release create` fails; every answer ends lines with CRLF
#   $STUB/not-signed-in, $STUB/pulls-fail, $STUB/issue-close-fail   those calls fail
#   $STUB/release-slow                `gh release create` takes 2 s, then fails
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/gh" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
S="$STUB"
REPO_URL='https://github.com/koniecdev/LotroKoniecDev'
jq_expr=""
emit() {
    local text="$1"
    if [ -n "$jq_expr" ]; then
        text="$(jq -r "$jq_expr" <<< "$text")"
    fi
    if [ -f "$S/crlf" ]; then
        printf '%s\n' "$text" | sed 's/$/\r/'
    else
        printf '%s\n' "$text"
    fi
}
log() { printf '%s\n' "$*" >> "$S/calls.log"; }

read_state() {
    s_status=queued s_conclusion='' s_prod=none s_pconc='' s_steps=0 s_nr=none
    local kv
    for kv in $(cat "$1/state"); do
        case "$kv" in
            status=*) s_status="${kv#*=}" ;;
            conclusion=*) s_conclusion="${kv#*=}" ;;
            prod=*) s_prod="${kv#*=}" ;;
            pconc=*) s_pconc="${kv#*=}" ;;
            steps=*) s_steps="${kv#*=}" ;;
            nr=*) s_nr="${kv#*=}" ;;
        esac
    done
}
add_deployment() {
    { printf '%s\t%s\n' "$((9000 + $(grep -c . "$S/deployments" || true)))" "$1"; cat "$S/deployments"; } > "$S/deployments.new"
    mv "$S/deployments.new" "$S/deployments"
}
apply() {
    local kv
    printf '%s\n' "$2" > "$1/state"
    for kv in $2; do
        case "$kv" in
            deploy=1) add_deployment "$(basename "$1")" ;;
            deploy_run=*) add_deployment "${kv#deploy_run=}" ;;
        esac
    done
}
advance() {
    local line
    if [ -s "$1/queue" ]; then
        line="$(head -n 1 "$1/queue")"
        tail -n +2 "$1/queue" > "$1/queue.new"
        mv "$1/queue.new" "$1/queue"
        apply "$1" "$line"
    fi
}
install() {
    apply "$1" "$(head -n 1 "$2")"
    tail -n +2 "$2" > "$1/queue"
}
run_json() {
    local rd="$S/runs/$1"
    read_state "$rd"
    jq -n --argjson id "$1" --arg status "$s_status" --arg conclusion "$s_conclusion" \
        --arg title "$(cat "$rd/title")" --arg event "$(cat "$rd/event")" --arg created "$(cat "$rd/created")" \
        --arg url "$REPO_URL/actions/runs/$1" \
        '{id: $id, status: $status, conclusion: (if $conclusion == "" then null else $conclusion end),
          display_title: $title, event: $event, created_at: $created, html_url: $url}'
}
jobs_json() {
    local rd="$S/runs/$1"
    read_state "$rd"
    jq -n --arg prod "$s_prod" --arg pconc "$s_pconc" --argjson steps "$s_steps" --arg nr "$s_nr" '
        [ (if $prod == "none" then empty
           elif $prod == "skipped" then {name: "Deploy to prod", status: "completed", conclusion: "skipped", steps: []}
           else {name: "Deploy to prod / Deploy to production (Hetzner)", status: $prod,
                 conclusion: (if $pconc == "" then null else $pconc end), steps: [range($steps) | {number: .}]} end),
          (if $nr == "none" then empty
           elif $nr == "success" or $nr == "failure" then {name: "Next release issue", status: "completed", conclusion: $nr}
           else {name: "Next release issue", status: $nr, conclusion: null} end),
          {name: "CI gate (deploy candidate)", status: "completed", conclusion: "success"} ]
        | map(. + {html_url: "x"}) | {jobs: .}'
}

case "${1-}" in
    repo)
        jq_expr='.nameWithOwner'
        emit '{"nameWithOwner": "koniecdev/LotroKoniecDev"}'
        exit 0
        ;;
    release)
        tag="$3"
        notes=""
        while [ "$#" -gt 0 ]; do
            [ "$1" = --notes-file ] && notes="$2"
            shift
        done
        if [ -f "$S/release-slow" ]; then
            touch "$S/release-started"
            sleep 2
            echo 'HTTP 502' >&2
            exit 1
        fi
        if [ -f "$S/release-fail" ]; then
            echo 'HTTP 422: Validation Failed' >&2
            exit 1
        fi
        log "release ${tag}"
        cp "$notes" "$S/release-notes.md"
        emit "$REPO_URL/releases/tag/${tag}"
        exit 0
        ;;
    api) shift ;;
    *) echo "stub gh: unexpected '$*'" >&2; exit 1 ;;
esac

method=GET path="" paginate=0 state="" note="" number="" body_file=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        -X) method="$2"; shift 2 ;;
        --jq) jq_expr="$2"; shift 2 ;;
        --paginate) paginate=1; shift ;;
        -f | -F)
            case "$2" in
                state=*) state="${2#state=}" ;;
                comment=*) note="${2#comment=}" ;;
                number=*) number="${2#number=}" ;;
                body=@*) body_file="${2#body=@}" ;;
            esac
            shift 2
            ;;
        -*) echo "stub gh: unexpected flag '$1'" >&2; exit 1 ;;
        *) path="$1"; shift ;;
    esac
done

case "$method $path" in
    "GET user")
        if [ -f "$S/not-signed-in" ]; then
            echo 'gh: To get started with GitHub CLI, please run:  gh auth login' >&2
            exit 4
        fi
        emit '{"login": "koniecdev"}'
        ;;
    "GET repos/"*"/actions/runs?head_sha="*)
        sha="${path#*head_sha=}"
        sha="${sha%%&*}"
        base="$S/commit-runs/$sha"
        file="$base"
        numbered="$(find "$S/commit-runs" -name "${sha}.[0-9]*" | sort -t . -k 2,2n)"
        if [ -n "$numbered" ]; then
            file="$(head -n 1 <<< "$numbered")"
            if [ "$(grep -c . <<< "$numbered")" -gt 1 ]; then
                mv "$file" "$S/served-${sha}"
                file="$S/served-${sha}"
            fi
        fi
        rows="$(cat "$file" 2>/dev/null || true)"
        emit "$(jq -R -s 'split("\n") | map(select(. != "") | split("\t")
            | {id: (.[0] | tonumber), path: .[1], status: .[2], conclusion: (if .[3] == "" then null else .[3] end),
               run_attempt: (.[4] | tonumber), created_at: .[5], html_url: .[6]}) | {workflow_runs: .}' <<< "$rows")"
        ;;
    "GET repos/"*"/actions/workflows/cd.yml/runs?"*)
        query="${path#*\?}"
        want_status="" created=""
        IFS='&' read -r -a params <<< "$query"
        for p in "${params[@]}"; do
            case "$p" in
                status=*) want_status="${p#status=}" ;;
                created=\>=*) created="${p#created=>=}" ;;
            esac
        done
        all="[]"
        for rd in "$S"/runs/*/; do
            rd="${rd%/}"
            [ -d "$rd" ] || continue
            if [ -f "$rd/hidden" ]; then
                left="$(cat "$rd/hidden")"
                if [ "$left" -gt 0 ]; then
                    echo $((left - 1)) > "$rd/hidden"
                    continue
                fi
            fi
            [ ! -f "$rd/tick-on-list" ] || advance "$rd"
            all="$(jq --argjson run "$(run_json "$(basename "$rd")")" '. + [$run]' <<< "$all")"
        done
        emit "$(jq --arg status "$want_status" --arg created "$created" '
            map(select(($status == "" or .status == $status) and ($created == "" or .created_at >= $created)))
            | sort_by(-.id) | {workflow_runs: .}' <<< "$all")"
        ;;
    "GET repos/"*"/actions/runs/"*"/jobs"*)
        id="${path#*/actions/runs/}"
        emit "$(jobs_json "${id%%/*}")"
        ;;
    "GET repos/"*"/actions/runs/"*"/pending_deployments")
        id="${path#*/actions/runs/}"
        id="${id%%/*}"
        read_state "$S/runs/$id"
        can="$(cat "$S/can-approve" 2>/dev/null || echo true)"
        if [ "$s_status" = waiting ] && [ "$s_prod" = waiting ]; then
            emit "[{\"environment\": {\"id\": 17348997657, \"name\": \"production\"}, \"current_user_can_approve\": ${can}}]"
        else
            emit '[]'
        fi
        ;;
    "GET repos/"*"/actions/runs/"*)
        id="${path##*/}"
        advance "$S/runs/$id"
        emit "$(run_json "$id")"
        ;;
    "POST repos/"*"/actions/runs/"*"/pending_deployments")
        id="${path#*/actions/runs/}"
        id="${id%%/*}"
        rd="$S/runs/$id"
        if [ "$state" = approved ] && [ -f "$rd/approve-fail" ]; then
            log "approve-failed ${id}"
            [ "$(cat "$rd/approve-fail")" = always ] || rm "$rd/approve-fail"
            [ ! -f "$rd/approve-fail-then" ] || apply "$rd" "$(cat "$rd/approve-fail-then")"
            echo 'HTTP 422: No pending deployment requests to approve or reject' >&2
            exit 1
        fi
        read_state "$rd"
        if [ "$s_status" != waiting ]; then
            echo 'HTTP 422: No pending deployment requests to approve or reject' >&2
            exit 1
        fi
        if [ "$state" = approved ]; then
            log "approve ${id} ${note}"
            install "$rd" "$rd/on-approve"
        else
            log "reject ${id} ${note}"
            if [ -f "$rd/on-reject" ]; then install "$rd" "$rd/on-reject"; else apply "$rd" 'status=completed conclusion=failure prod=completed pconc=failure steps=0'; fi
        fi
        emit '[]'
        ;;
    "POST repos/"*"/actions/runs/"*"/rerun")
        id="${path#*/actions/runs/}"
        id="${id%%/*}"
        rd="$S/runs/$id"
        read_state "$rd"
        if [ "$s_status" != completed ]; then
            echo 'HTTP 403: This workflow is already running' >&2
            exit 1
        fi
        if [ -f "$rd/rerun-fail" ]; then
            echo 'HTTP 403: Unable to re-run this workflow run because it was created over a month ago' >&2
            exit 1
        fi
        log "rerun ${id}"
        install "$rd" "$rd/on-rerun"
        ;;
    "GET repos/"*"/deployments?"*)
        emit "$(jq -R -s 'split("\n") | map(select(. != "") | split("\t") | {id: (.[0] | tonumber), sha: "run-sha"})' < "$S/deployments")"
        ;;
    "GET repos/"*"/deployments/"*"/statuses"*)
        dep="${path#*/deployments/}"
        dep="${dep%%/*}"
        run="$(awk -F '\t' -v d="$dep" '$1 == d { print $2 }' "$S/deployments")"
        emit "[{\"state\": \"inactive\", \"target_url\": \"$REPO_URL/actions/runs/${run}/job/1\"}, {\"state\": \"success\", \"target_url\": \"$REPO_URL/actions/runs/${run}/job/1\"}]"
        ;;
    "GET repos/"*"/commits/"*"/pulls")
        [ ! -f "$S/pulls-fail" ] || { echo 'gh: HTTP 502' >&2; exit 1; }
        sha="${path#*/commits/}"
        emit "$(cat "$S/pulls/${sha%/pulls}.json" 2>/dev/null || echo '[]')"
        ;;
    "GET graphql")
        emit "$(cat "$S/graphql/${number}.json" 2>/dev/null || echo '{"data":{"repository":{"pullRequest":{"closingIssuesReferences":{"nodes":[]}}}}}')"
        ;;
    "GET repos/"*"/issues?"*)
        emit "$(cat "$S/issues.json" 2>/dev/null || echo '[]')"
        ;;
    "POST repos/"*"/issues/"*"/comments")
        n="${path%/comments}"
        log "comment ${n##*/} $(cat "$body_file")"
        ;;
    "PATCH repos/"*"/issues/"*)
        [ ! -f "$S/issue-close-fail" ] || { echo 'gh: HTTP 502' >&2; exit 1; }
        log "close ${path##*/} ${state}"
        ;;
    *)
        echo "stub gh: unexpected call '$method $path'" >&2
        exit 1
        ;;
esac
STUB

cat > "$BIN/claude" <<'STUB'
#!/usr/bin/env bash
cat > "$STUB/claude-stdin.txt"
printf '[%s]' "$@" > "$STUB/claude-args.txt"
pwd > "$STUB/claude-cwd.txt"
case "$(cat "$STUB/claude-mode" 2>/dev/null || echo ok)" in
    ok) printf '%s\n' '{"type":"result","is_error":false,"result":"A release.\r\n\r\n## For translators\r\n\r\n- **AI NOTES** (#11)"}' ;;
    error) printf '%s\n' '{"type":"result","is_error":true,"result":"Claude AI usage limit reached"}' ;;
    empty) printf '%s\n' '{"type":"result","is_error":false,"result":""}' ;;
    noheading) printf '%s\n' '{"type":"result","is_error":false,"result":"Jestem Arwena, nie Rin."}' ;;
    exit1) echo 'claude: not signed in' >&2; exit 1 ;;
    sleep) echo "$$" > "$STUB/claude.pid"; exec sleep 30 ;;
    sleep3) sleep 3; printf '%s\n' '{"type":"result","is_error":false,"result":"A release.\n\n## For translators\n\n- **AI NOTES** (#11)"}' ;;
esac
STUB

cat > "$BIN/editor" <<'STUB'
#!/usr/bin/env bash
echo 'EDITED BY THE TEST' >> "$1"
[ ! -f "$STUB/editor-fail" ] || exit 1
STUB
chmod +x "$BIN/gh" "$BIN/claude" "$BIN/editor"
export PATH="$BIN:$PATH"

# --- Fixture helpers ------------------------------------------------------------------------------
new_case() {
    CASE="$1"
    T="$TMP_ROOT/case-$cases"
    rm -rf "$T"
    ORIGIN="$T/origin.git"
    CLONE="$T/clone"
    STUB="$T/stub"
    mkdir -p "$STUB/runs" "$STUB/commit-runs" "$STUB/pulls" "$STUB/graphql"
    : > "$STUB/calls.log"
    : > "$STUB/deployments"
    git init --quiet --bare --initial-branch=main "$ORIGIN"
    git clone --quiet "$ORIGIN" "$CLONE" 2>/dev/null
    export STUB
}

# commit <message> [file] — prints the new commit.
commit() {
    local file="${2:-log.txt}"
    mkdir -p "$(dirname "$CLONE/$file")"
    printf '%s\n' "$1" >> "$CLONE/$file"
    git -C "$CLONE" add -A
    git -C "$CLONE" commit --quiet -m "$1"
    git -C "$CLONE" rev-parse HEAD
}

push() { git -C "$CLONE" push --quiet origin main --tags 2>/dev/null; }

# pr <commit> <pr number> <title> <issue number> <labels>
pr() {
    jq -n --arg sha "$1" --argjson number "$2" --arg title "$3" \
        '[{number: $number, title: $title, merged_at: "2026-11-22T09:00:00Z", merge_commit_sha: $sha}]' > "$STUB/pulls/$1.json"
    jq -n --argjson number "$4" --arg title "$3" --arg labels "$5" \
        '{data: {repository: {pullRequest: {closingIssuesReferences: {nodes: [{number: $number, title: $title,
          body: "## Problem\n\nRows show no author.\n\n## Fix\n\nThe editor shows the last author.",
          repository: {nameWithOwner: "koniecdev/LotroKoniecDev"},
          labels: {nodes: ($labels | split(",") | map({name: .}))}}]}}}}}' > "$STUB/graphql/$2.json"
}

# runs_of <commit> <rows file suffix or ''> <row>... — the push runs of a commit.
runs_of() {
    local sha="$1" suffix="$2"
    shift 2
    printf '%s\n' "$@" > "$STUB/commit-runs/${sha}${suffix}"
}
ci_row() { printf '%s\t%s\t%s\t%s\t%s\t%s\thttps://ci/%s' "$1" "$CI" "$2" "$3" "${4:-1}" "$CI_CREATED" "$1"; }
gitleaks_row() { printf '%s\t%s\tcompleted\tsuccess\t1\t%s\thttps://gl/%s' "$1" "$GITLEAKS" "$CI_CREATED" "$1"; }

# cd_run <id> <title> <event> <state>
cd_run() {
    mkdir -p "$STUB/runs/$1"
    printf '%s' "$2" > "$STUB/runs/$1/title"
    printf '%s' "$3" > "$STUB/runs/$1/event"
    printf '%s' "$CD_CREATED" > "$STUB/runs/$1/created"
    printf '%s\n' "$4" > "$STUB/runs/$1/state"
}
# release_run <id> <state> [ci id] [attempt] — a CD run of the release commit $R.
release_run() { cd_run "$1" "CD for ${R} (CI run ${3:-7001}, attempt ${4:-1})" workflow_run "$2"; }
# states <id> <file> <state>... — the states of a run that follow an event, or its queue.
states() {
    local id="$1" file="$2"
    shift 2
    printf '%s\n' "$@" > "$STUB/runs/$id/$file"
}
approve_ok() { states "$1" on-approve "$DEPLOYING" "$DEPLOYED" "$DONE"; }
on_prod() { printf '%s\t%s\n' "$((8000 + $(grep -c . "$STUB/deployments" || true)))" "$1" >> "$STUB/deployments"; }

# The common start: C0 (with ADR-0070) tagged v0.2.1 and on prod through run 400; C1, a feature PR
# with a green CI run, is the release commit R. Issue #77 is the open "Next release" issue.
base() {
    C0="$(commit 'C0: init' "$ADR_PATH")"
    git -C "$CLONE" tag -a v0.2.1 -m v0.2.1
    C1="$(commit 'C1: feature')"
    pr "$C1" 101 'Show who changed a row' 11 "${1:-type-feature}"
    R="$C1"
    runs_of "$C1" '' "$(ci_row 7001 completed success)" "$(gitleaks_row 7002)"
    cd_run 400 "CD for ${C0} (CI run 6000, attempt 1)" workflow_run "$DONE"
    on_prod 400
    jq -n '[{number: 77, body: "<!-- lotro-next-release -->\n<!-- next-release-bump: minor -->"}]' > "$STUB/issues.json"
}

# base with prod already on R (run 500), so no deploy runs.
base_on_prod() {
    base "$@"
    release_run 500 "$DONE"
    : > "$STUB/deployments"
    on_prod 500
}

run_release() {
    local answers="$1"
    shift
    LAST_STATUS=0
    LAST_OUTPUT="$(cd "$CLONE" && printf '%b' "$answers" | "${TIMEOUT[@]}" env RELEASE_POLL_SECONDS=0 \
        RELEASE_NOTES_GRACE_SECONDS=5 "$@" "$RELEASE_SH" 2>&1)" || LAST_STATUS=$?
}

# A run on a terminal (util-linux script), driven by what it prints, never by the clock:
# pty_start <command for /bin/sh>, then pty_wait_for <text> and pty_send <input>, then pty_end.
pty_start() {
    PTY_OUT="$T/pty.out"
    mkfifo "$T/pty.in"
    (cd "$CLONE" && SHELL=/bin/sh exec script -qfec "$1" "$PTY_OUT" < "$T/pty.in" > /dev/null 2>&1) &
    PTY_PID=$!
    exec 7> "$T/pty.in"
}
pty_wait_for() {
    local _
    for _ in $(seq 1 600); do
        if [ -f "$PTY_OUT" ] && grep -qF -- "$1" "$PTY_OUT"; then
            return 0
        fi
        sleep 0.1
    done
    LAST_OUTPUT="$(cat "$PTY_OUT" 2>/dev/null || true)"
    return 1
}
pty_send() { printf '%b' "$1" >&7; }
pty_end() {
    exec 7>&-
    LAST_STATUS=0
    wait "$PTY_PID" || LAST_STATUS=$?
    PTY_PID=""
    LAST_OUTPUT="$(cat "$PTY_OUT")"
}
PTY_ENV="env PATH='$PATH' RELEASE_POLL_SECONDS=1 RELEASE_NOTES_GRACE_SECONDS=5"

wait_for_file() {
    local _
    for _ in $(seq 1 300); do
        [ ! -f "$1" ] || return 0
        sleep 0.1
    done
    return 1
}

# run_in_background <answers> — release.sh with $! as its own process id, for a signal.
run_in_background() {
    printf '%b' "$1" > "$T/answers"
    (cd "$CLONE" && exec env RELEASE_POLL_SECONDS=0 RELEASE_NOTES_GRACE_SECONDS=5 "$RELEASE_SH" < "$T/answers" > "$T/out" 2>&1) &
    BG_PID=$!
}

out_has() { grep -qF -- "$1" <<< "$LAST_OUTPUT"; }
logged() { grep -qF -- "$1" "$STUB/calls.log"; }
log_count() { grep -c -- "$1" "$STUB/calls.log" || true; }
line_of() { grep -n -m 1 -- "$1" "$STUB/calls.log" | cut -d : -f 1; }
tag_of() { git -C "$ORIGIN" rev-parse --verify --quiet "$1^{commit}" || true; }
no_tag() { [ -z "$(git -C "$ORIGIN" tag --list 'v0.3*' 'v1*')" ] || fail "no tag must be pushed"; }
released() {
    [ "$LAST_STATUS" -eq 0 ] || fail "expected a release (exit 0), got exit $LAST_STATUS"
    [ "$(tag_of "$1")" = "$2" ] || fail "the tag $1 must point at $2"
    logged "release $1" || fail "the GitHub Release $1 must be created"
}

# --- Cases ----------------------------------------------------------------------------------------

new_case 'happy path behind a docs-only tip'
base
C2="$(commit 'C2: docs' docs/readme.md)"
runs_of "$C2" '' "$(gitleaks_row 7003)"
push
release_run 500 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
out_has 'no CI run (it changed only files CI ignores)' || fail 'the docs-only tip must be explained'
out_has "Release commit: $(git -C "$CLONE" rev-parse --short "$C1")" || fail 'the commit before the docs tip is the release commit'
logged "approve 500 Approved by scripts/release.sh to release ${C1}." || fail 'the release run must be approved'
out_has 'From now on Ctrl-C stops only this script, not the deploy on GitHub.' || fail 'the Ctrl-C note must follow the approval'
[ "$(git -C "$ORIGIN" cat-file -t v0.3.0)" = tag ] || fail 'the tag must be annotated'
[ "$(git -C "$ORIGIN" tag -l --format='%(contents:subject)' v0.3.0)" = v0.3.0 ] || fail 'the tag message must be v0.3.0'
grep -qF '**AI NOTES**' "$STUB/release-notes.md" || fail 'the AI notes must be published'
logged 'close 77 closed' || fail 'the Next release issue must be closed'
logged 'comment 77 Released as [v0.3.0](https://github.com/koniecdev/LotroKoniecDev/releases/tag/v0.3.0).' \
    || fail 'the issue must link the release'
grep -qF 'You write the release notes' "$STUB/claude-stdin.txt" || fail 'the prompt file must reach claude on stdin'
grep -qF "Server images: sha-$(git -C "$CLONE" rev-parse --short=7 "$C1")" "$STUB/claude-stdin.txt" || fail 'the input must name the images'
grep -qF 'Closes #11 (type-feature): Show who changed a row' "$STUB/claude-stdin.txt" || fail 'the input must hold the change list'
grep -qF 'The editor shows the last author.' "$STUB/claude-stdin.txt" || fail 'the input must hold the Fix section'
[ "$(cat "$STUB/claude-args.txt")" = '[-p][--tools][][--strict-mcp-config][--no-session-persistence][--output-format][json]' ] \
    || fail "claude must run with exactly the planned flags, got $(cat "$STUB/claude-args.txt")"
[[ "$(cat "$STUB/claude-cwd.txt")" == */notes-cwd ]] || fail 'claude must start in its own empty folder'
pass 'a docs-only tip is skipped, the release run approved, the tag annotated, the release and the issue done'

new_case 'red CI'
base
runs_of "$C1" '' "$(ci_row 7001 completed failure)" "$(gitleaks_row 7002)"
push
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "red CI must stop with exit 1, got $LAST_STATUS"
out_has "CI of ${C1} ended 'failure': https://ci/7001" || fail 'the CI run URL must be printed'
logged approve && fail 'nothing must be approved'
no_tag
pass 'red CI stops before any deploy, with the run URL'

new_case 'CI cancelled by a newer push'
base
runs_of "$C1" '' "$(ci_row 7001 completed cancelled)" "$(gitleaks_row 7002)"
push
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "cancelled CI must stop with exit 1, got $LAST_STATUS"
out_has "ended 'cancelled'" || fail 'the cancelled CI must be named'
no_tag
pass 'a cancelled CI stops the release'

new_case 'CI still running'
base
runs_of "$C1" .1 "$(ci_row 7001 in_progress '')" "$(gitleaks_row 7002)"
runs_of "$C1" .2 "$(ci_row 7001 in_progress '')" "$(gitleaks_row 7002)"
runs_of "$C1" .3 "$(ci_row 7001 completed success)" "$(gitleaks_row 7002)"
push
release_run 500 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
out_has ': in_progress (https://ci/7001).' || fail 'the wait for CI must be shown'
pass 'the script waits for the release commit CI to finish'

new_case 'other candidates waiting at the gate'
base
push
cd_run 450 "CD for 1111111111111111111111111111111111111111 (CI run 6500, attempt 1)" workflow_run 'status=waiting prod=waiting'
cd_run 460 "CD for 2222222222222222222222222222222222222222 (CI run 7100, attempt 1)" workflow_run 'status=waiting prod=waiting'
release_run 500 'status=in_progress prod=pending'
states 500 queue 'status=in_progress prod=pending' 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
logged "reject 450 Rejected by scripts/release.sh: the release of ${C1} is being deployed." || fail 'the older candidate must be rejected, naming R'
logged 'reject 460' || fail 'a newer candidate must be rejected too'
[ "$(line_of 'reject 450')" -lt "$(line_of 'approve 500')" ] || fail 'the others must be rejected before the approval'
pass 'other runs waiting at the gate are rejected, then the release run is approved'

new_case 'prod already runs the release commit'
base_on_prod
push
run_release '\n'
released v0.3.0 "$C1"
out_has "Prod already runs ${C1}: no deploy needed." || fail 'the skip must be said'
logged approve && fail 'nothing must be approved'
logged reject && fail 'nothing must be rejected'
pass 'prod already on the release commit: no approval, straight to the release'

new_case 'the newest tag is already on the tip'
base
git -C "$CLONE" tag -a v0.2.2 -m v0.2.2 "$C1"
C2="$(commit 'C2: docs' docs/readme.md)"
runs_of "$C2" '' "$(gitleaks_row 7003)"
push
run_release '\n'
[ "$LAST_STATUS" -eq 0 ] || fail "nothing new must exit 0, got $LAST_STATUS"
out_has 'Nothing new to release' || fail 'it must say there is nothing new'
[ ! -f "$STUB/claude-stdin.txt" ] || fail 'no notes call when there is nothing to release'
[ "$(log_count .)" -eq 0 ] || fail 'nothing must be written'
pass 'with the newest tag on the last deployable commit, nothing is done'

new_case 'red rollout'
base
push
release_run 500 'status=waiting prod=waiting'
states 500 on-approve "$DEPLOYING" 'status=completed conclusion=failure prod=completed pconc=failure steps=14'
echo sleep > "$STUB/claude-mode"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a red rollout must stop with exit 1, got $LAST_STATUS"
out_has "ended 'failure' (a red rollout or a rollback). Nothing is tagged." || fail 'the failed deploy must be named'
no_tag
for _ in 1 2 3 4 5 6 7 8 9 10; do
    [ -f "$STUB/claude.pid" ] && break
    sleep 0.2
done
pid="$(cat "$STUB/claude.pid")"
sleep 0.5
if kill -0 "$pid" 2>/dev/null; then
    fail 'the background notes call must be stopped when the script exits'
fi
pass 'a red rollout (or a rollback) stops with no tag, and the background notes call is stopped'

new_case 'rollout cancelled half-way'
base
push
release_run 500 'status=waiting prod=waiting'
states 500 on-approve "$DEPLOYING" 'status=completed conclusion=cancelled prod=completed pconc=cancelled steps=6'
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a rollout cancelled with steps must stop, got $LAST_STATUS"
logged rerun && fail 'a rollout that started is never re-run'
no_tag
pass 'a prod job that ran steps and was cancelled stops, never re-runs'

new_case 'nothing raises the number'
base type-refactor
push
release_run 500 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
[ "$LAST_STATUS" -eq 0 ] || fail "Enter at the version question must stop with exit 0, got $LAST_STATUS"
out_has 'No change raises the version number, so no release is proposed.' || fail 'the script must say no release is proposed'
logged 'approve 500' || fail 'the deploy still happens'
[ -z "$(git -C "$ORIGIN" tag --list 'v0.2.2')" ] || fail 'no tag without a typed version'
: > "$STUB/calls.log"
run_release '0.2.2\n\n'
released v0.2.2 "$C1"
logged approve && fail 'the second run must find prod on the commit already'
pass 'nothing raising the number tags nothing, unless the owner types a version'

new_case 'a typed version that is not higher'
base_on_prod type-refactor
push
run_release '0.2.1\nabc\n0.1.9\n\n'
[ "$LAST_STATUS" -eq 0 ] || fail "expected exit 0, got $LAST_STATUS"
out_has '0.2.1 is not above the newest release, v0.2.1.' || fail 'the same version must be refused'
out_has "'abc' is not a version like 0.3.0." || fail 'a non-version must be refused'
out_has '0.1.9 is not above the newest release' || fail 'a lower version must be refused'
[ "$(git -C "$ORIGIN" tag | grep -c .)" -eq 1 ] || fail 'no tag must be made'
pass 'a typed version that is not above the newest tag is refused'

new_case 'the claude call fails'
base_on_prod
push
echo exit1 > "$STUB/claude-mode"
run_release '\n'
released v0.3.0 "$C1"
grep -qF '> The AI release notes failed (the claude call exited with 1)' "$STUB/release-notes.md" || fail 'the raw notes must say why'
grep -qF -- '- #101 Show who changed a row (closes #11)' "$STUB/release-notes.md" || fail 'the raw list must be the notes'
pass 'a failed claude call gives the raw list and the release goes on'

new_case 'q stops'
base_on_prod
push
run_release 'q\n'
[ "$LAST_STATUS" -eq 0 ] || fail "q must exit 0, got $LAST_STATUS"
out_has 'Stopped. Nothing was tagged or published.' || fail 'q must say nothing was done'
no_tag
logged 'release v' && fail 'no release must be created'
pass 'q stops with no tag'

new_case 'no answer at the prompt'
base_on_prod
push
run_release ''
[ "$LAST_STATUS" -eq 1 ] || fail "a closed input must stop with exit 1, got $LAST_STATUS"
no_tag
pass 'no answer at all publishes nothing'

new_case 'the release cannot be created'
base_on_prod
push
touch "$STUB/release-fail"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a failed release create must exit 1, got $LAST_STATUS"
[ "$(tag_of v0.3.0)" = "$C1" ] || fail 'the pushed tag must stay'
out_has 'gh release create v0.3.0 --verify-tag --latest --title "v0.3.0" --notes-file' || fail 'the command that finishes the release must be printed'
out_has 'scripts/ci/next-release-issue.sh close v0.3.0' || fail 'closing the issue by hand must be printed too'
[ -f "$CLONE/.git/release-notes-v0.3.0.md" ] || fail 'the notes must be kept for that command'
logged 'close 77' && fail 'the issue must stay open'
pass 'a failed release create keeps the tag and prints the command that finishes it'

new_case 'a code tip whose CI run appears on the second read'
base
C2="$(commit 'C2: code')"
pr "$C2" 102 'Another feature' 12 type-feature
runs_of "$C2" .1 "$(gitleaks_row 7004)"
runs_of "$C2" .2 "$(gitleaks_row 7004)" "$(ci_row 7005 completed success)"
push
R="$C2"
cd_run 520 "CD for ${C2} (CI run 7005, attempt 1)" workflow_run "$DONE"
: > "$STUB/deployments"
on_prod 520
run_release '\n'
released v0.3.0 "$C2"
pass 'a commit with only a gitleaks run is read twice before it counts as docs-only'

new_case 'no runs yet'
base
C2="$(commit 'C2: code')"
pr "$C2" 102 'Another feature' 12 type-feature
runs_of "$C2" .1
runs_of "$C2" .2
runs_of "$C2" .3 "$(gitleaks_row 7004)" "$(ci_row 7005 completed success)"
push
R="$C2"
cd_run 520 "CD for ${C2} (CI run 7005, attempt 1)" workflow_run "$DONE"
: > "$STUB/deployments"
on_prod 520
run_release '\n'
released v0.3.0 "$C2"
out_has 'no CI or gitleaks run yet, waiting for GitHub.' || fail 'the wait must be shown'
pass 'a commit with no runs yet is waited for'

new_case 'a commit that skips Actions'
base_on_prod
C2="$(commit 'C2: notes [skip ci]')"
runs_of "$C2" ''
push
run_release '\n'
released v0.3.0 "$C1"
out_has 'its message skips GitHub Actions' || fail 'the skip marker must be named'
pass 'a commit whose message skips Actions is stepped over'

new_case 'the candidate is the run of the latest CI attempt'
base
runs_of "$C1" '' "$(ci_row 7001 completed success 2)" "$(gitleaks_row 7002)"
push
release_run 501 'status=completed conclusion=success prod=skipped' 7001 1
release_run 502 'status=waiting prod=waiting' 7001 2
approve_ok 502
run_release '\n'
released v0.3.0 "$C1"
logged 'approve 502' || fail 'the run of attempt 2 must be approved'
logged 'rerun 501' && fail 'the refused run of attempt 1 must be left alone'
pass 'the candidate is matched by the CI run id and its latest attempt'

new_case 'the CD run starts late'
base
push
release_run 500 'status=waiting prod=waiting'
echo 3 > "$STUB/runs/500/hidden"
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
out_has "The CD run of ${C1} has not started yet." || fail 'the wait for the CD run must be shown'
pass 'the script waits for the CD run to appear'

new_case 'an older run of the release commit waits at the gate'
base
runs_of "$C1" '' "$(ci_row 7001 completed success 2)" "$(gitleaks_row 7002)"
push
release_run 501 'status=waiting prod=waiting' 7001 1
approve_ok 501
release_run 502 'status=in_progress prod=none' 7001 2
touch "$STUB/runs/502/tick-on-list"
states 502 queue 'status=in_progress prod=none' 'status=in_progress prod=none' 'status=in_progress prod=pending' 'status=waiting prod=waiting'
run_release '\n'
released v0.3.0 "$C1"
logged 'approve 501' || fail 'a waiting run of the release commit must be approved, whatever its attempt'
logged "reject 502 Rejected by scripts/release.sh: ${C1} is already deployed by run 501." \
    || fail 'another run of the release commit that reaches the gate later must be rejected'
pass 'the approved run is followed, and a second run of the commit is rejected at the gate'

new_case 'the candidate was rejected earlier'
base
push
release_run 500 "$REJECTED"
states 500 on-rerun 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
[ "$(line_of 'rerun 500')" -lt "$(line_of 'approve 500')" ] || fail 'the run must be re-run, then approved'
out_has 'ended without starting (it was rejected)' || fail 'the cause must be printed'
out_has 'staging goes back to' || fail 'the staging step back must be said'
pass 'a candidate rejected before the script started is re-run, then approved'

new_case 'the candidate was cancelled'
base
push
release_run 500 "$CANCELLED"
states 500 on-rerun 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
out_has 'it was cancelled, by the CD janitor or by a newer candidate' || fail 'the cancel must be named'
pass 'a candidate whose prod job was cancelled before it started is re-run'

new_case 'the candidate keeps ending without a deploy'
base
push
release_run 500 "$CANCELLED"
states 500 on-rerun "$CANCELLED"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a third end without a deploy must stop, got $LAST_STATUS"
[ "$(log_count 'rerun 500')" -eq 2 ] || fail 'at most two re-runs'
no_tag
pass 'after two re-runs a third end without a deploy stops the script'

new_case 'the run is still finishing before a re-run'
base
push
release_run 500 'status=in_progress prod=completed pconc=failure steps=0'
states 500 queue 'status=in_progress prod=completed pconc=failure steps=0' "$REJECTED"
states 500 on-rerun 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
out_has 'waiting for the run to end' || fail 'a re-run needs the run to end first'
pass 'a run is re-run only after it ended'

new_case 'a prod deploy someone else approved'
base
push
release_run 500 "$DEPLOYING"
states 500 queue "$DEPLOYED" "$DONE"
run_release '\n'
released v0.3.0 "$C1"
logged approve && fail 'a deploy in progress needs no approval'
pass 'a prod job in progress is followed, not stopped'

new_case 'a hand run on prod and at the gate'
base
push
cd_run 300 "CD by hand: sha-$(git -C "$CLONE" rev-parse --short=7 "$C1")" workflow_dispatch "$DONE"
: > "$STUB/deployments"
on_prod 300
cd_run 301 'CD by hand: sha-abc1234' workflow_dispatch 'status=waiting prod=waiting'
release_run 500 'status=in_progress prod=pending'
states 500 queue 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
out_has 'WARNING: prod runs a hand run of CD' || fail 'a hand run on prod must be named loudly'
logged 'reject 301' || fail 'a hand run at the gate must be rejected'
logged 'approve 500' || fail 'a hand-run record never counts as the release commit on prod'
pass 'a hand run never counts as the release on prod and is rejected at the gate'

new_case 'the approval fails because the run was cancelled'
base
push
release_run 500 'status=waiting prod=waiting'
echo once > "$STUB/runs/500/approve-fail"
printf '%s\n' "$CANCELLED" > "$STUB/runs/500/approve-fail-then"
states 500 on-rerun 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
logged 'approve-failed 500' || fail 'the first approval must fail'
logged 'rerun 500' || fail 'the cancelled run must be re-run'
pass 'a failed approval of a run that is no longer waiting goes on with the table'

new_case 'the approval fails and the run still waits'
base
push
release_run 500 'status=waiting prod=waiting'
echo always > "$STUB/runs/500/approve-fail"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a refused approval must stop, got $LAST_STATUS"
out_has 'it still waits at the gate' || fail 'the stop must say why'
no_tag
pass 'a refused approval of a run that still waits stops the script'

new_case 'the login may not approve'
base
push
release_run 500 'status=waiting prod=waiting'
echo false > "$STUB/can-approve"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a login that may not approve must stop, got $LAST_STATUS"
out_has 'may not review the production deploy' || fail 'the stop must say why'
no_tag
pass 'a gh login that may not approve stops the script'

new_case 'a re-run is refused'
base
push
release_run 500 "$REJECTED"
touch "$STUB/runs/500/rerun-fail"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a refused re-run must stop, got $LAST_STATUS"
out_has 'Use the manual steps in the runbook' || fail 'the runbook fallback must be named'
no_tag
pass 'a refused re-run stops with the runbook fallback'

new_case 'the issue job failed'
base
push
release_run 500 'status=waiting prod=waiting'
states 500 on-approve "$DEPLOYING" "$DEPLOYED" 'status=completed conclusion=success prod=completed pconc=success steps=12 nr=failure'
run_release '\n'
released v0.3.0 "$C1"
out_has 'WARNING: the "Next release issue" job of' || fail 'a failed issue job must be reported'
pass 'a failed issue job warns and the release goes on'

new_case 'an answer with no heading'
base_on_prod
push
echo noheading > "$STUB/claude-mode"
run_release '\n'
released v0.3.0 "$C1"
grep -qF 'the answer has no ## heading' "$STUB/release-notes.md" || fail 'an off-topic answer must give the raw list'
pass 'an AI answer with no heading gives the raw list'

new_case 'an error answer'
base_on_prod
push
echo error > "$STUB/claude-mode"
run_release '\n'
released v0.3.0 "$C1"
grep -qF 'returned no notes: an error or a usage limit' "$STUB/release-notes.md" || fail 'is_error must give the raw list'
pass 'an is_error answer gives the raw list'

new_case 'an empty answer'
base_on_prod
push
echo empty > "$STUB/claude-mode"
run_release '\n'
released v0.3.0 "$C1"
grep -qF 'returned no notes' "$STUB/release-notes.md" || fail 'an empty answer must give the raw list'
pass 'an empty answer gives the raw list'

new_case 'no jq'
base_on_prod
push
run_release '\n' RELEASE_JQ=jq-is-not-installed-here
released v0.3.0 "$C1"
out_has 'AI notes are off (jq is not installed)' || fail 'the start must say the AI notes are off'
[ ! -f "$STUB/claude-stdin.txt" ] || fail 'claude must never be called without jq'
grep -qF 'The AI release notes are off (jq is not installed)' "$STUB/release-notes.md" || fail 'the raw list must say why'
pass 'without jq the AI notes are off and the raw list is used'

new_case 'no claude'
base_on_prod
push
run_release '\n' RELEASE_CLAUDE=claude-is-not-installed-here
released v0.3.0 "$C1"
grep -qF '(the claude CLI is not installed)' "$STUB/release-notes.md" || fail 'the raw list must say why'
pass 'without the claude CLI the raw list is used'

new_case 'the notes are still being written'
base_on_prod
push
echo sleep > "$STUB/claude-mode"
run_release 'r\n\n' RELEASE_NOTES_GRACE_SECONDS=1
released v0.3.0 "$C1"
out_has 'The AI notes are still being written.' || fail 'the wait must be offered'
grep -qF 'These notes are the raw change list: the AI notes were not ready.' "$STUB/release-notes.md" || fail 'r must give the raw list'
pid="$(cat "$STUB/claude.pid")"
if kill -0 "$pid" 2>/dev/null; then
    fail 'the notes call must be stopped after r'
fi
pass 'r at the wait gives the raw list and stops the notes call'

new_case 'edit the notes with GIT_EDITOR'
base_on_prod
push
run_release 'e\n\n' GIT_EDITOR="$BIN/editor"
released v0.3.0 "$C1"
grep -qF 'EDITED BY THE TEST' "$STUB/release-notes.md" || fail 'the edited notes must be published'
pass 'e opens the notes in GIT_EDITOR'

new_case 'edit the notes with EDITOR'
base_on_prod
push
run_release 'e\n\n' EDITOR="$BIN/editor"
released v0.3.0 "$C1"
grep -qF 'EDITED BY THE TEST' "$STUB/release-notes.md" || fail 'EDITOR must be used when GIT_EDITOR is not set'
pass 'e falls back to EDITOR'

new_case 'the editor fails'
base_on_prod
push
touch "$STUB/editor-fail"
run_release 'e\n\n' GIT_EDITOR="$BIN/editor"
released v0.3.0 "$C1"
out_has 'The editor failed; the notes stay as they were.' || fail 'a failed editor must be said'
if grep -qF 'EDITED BY THE TEST' "$STUB/release-notes.md"; then
    fail 'a failed edit must not change the notes'
fi
pass 'a failing editor leaves the notes as they were and asks again'

new_case 'change the version'
base_on_prod
push
run_release 'v\n0.2.0\nv\n1.0.0\n\n'
released v1.0.0 "$C1"
out_has '0.2.0 is not above the newest release' || fail 'a lower version must be refused'
pass 'v refuses a lower version and accepts a typed 1.0.0'

new_case 'CRLF from the tools'
base
push
touch "$STUB/crlf"
release_run 500 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
grep -qF '**AI NOTES**' "$STUB/release-notes.md" || fail 'the notes must survive CRLF'
if grep -q $'\r' "$STUB/release-notes.md"; then
    fail 'no CR may reach the notes'
fi
pass 'answers that end lines with CRLF work'

new_case 'a release commit without ADR-0070'
commit 'C0: init' >/dev/null
git -C "$CLONE" tag -a v0.0.9 -m v0.0.9
C1="$(commit 'C1: old')"
R="$C1"
runs_of "$C1" '' "$(ci_row 7001 completed success)" "$(gitleaks_row 7002)"
push
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a commit without ADR-0070 must stop, got $LAST_STATUS"
out_has 'does not contain ADR-0070' || fail 'the stop must name ADR-0070'
[ "$(log_count .)" -eq 0 ] || fail 'nothing must be written'
pass 'a release commit without ADR-0070 is never tagged'

new_case 'a key pressed during the wait'
base
push
release_run 500 'status=in_progress prod=pending'
states 500 queue 'status=in_progress prod=pending' 'status=in_progress prod=pending' 'status=in_progress prod=pending' 'status=waiting prod=waiting'
approve_ok 500
pty_start "$PTY_ENV '$RELEASE_SH'"
pty_wait_for 'Waiting for the CD run of' || fail 'the deploy wait must start'
pty_send '\n'
pty_wait_for 'Enter = publish' || fail 'the last prompt must be reached'
pty_send 'q\n'
pty_end
out_has '**AI NOTES**' || fail 'the stub claude must write the notes: a test must never reach the real one'
logged 'release v' && fail 'an Enter typed while the script waited must never publish'
no_tag
out_has 'Stopped. Nothing was tagged or published.' || fail 'the q typed after the prompt must stop it'
pass 'a line typed while the script waits is thrown away, so only an answer to the prompt publishes'

new_case 'a version typed during the wait'
base type-refactor
push
release_run 500 'status=in_progress prod=pending'
states 500 queue 'status=in_progress prod=pending' 'status=in_progress prod=pending' 'status=waiting prod=waiting'
approve_ok 500
pty_start "$PTY_ENV '$RELEASE_SH'"
pty_wait_for 'Waiting for the CD run of' || fail 'the deploy wait must start'
pty_send '0.2.2\n'
pty_wait_for 'Type a version' || fail 'the version question must be reached'
pty_send '\n'
pty_end
[ "$LAST_STATUS" -eq 0 ] || fail "Enter at the version question must exit 0, got $LAST_STATUS"
out_has 'Stopped. Nothing was tagged.' || fail 'the Enter typed after the question must stop it'
[ -z "$(git -C "$ORIGIN" tag --list v0.2.2)" ] || fail 'a version typed during the wait must never be used'
pass 'a version typed while the script waits is thrown away'

new_case 'an r typed before the notes wait'
base_on_prod
push
echo sleep3 > "$STUB/claude-mode"
pty_start "env PATH='$PATH' RELEASE_POLL_SECONDS=1 RELEASE_NOTES_GRACE_SECONDS=1 '$RELEASE_SH'"
pty_wait_for 'Writing the release notes in the background.' || fail 'the notes call must start'
pty_send 'r\n'
pty_wait_for 'Enter = publish' || fail 'the last prompt must be reached'
pty_send '\n'
pty_end
released v0.3.0 "$C1"
grep -qF '**AI NOTES**' "$STUB/release-notes.md" || fail 'an r typed before the wait was offered must not choose the raw list'
pass 'an r typed before the notes wait is thrown away'

new_case 'the tag push is refused'
base_on_prod
push
printf '#!/bin/sh\necho "refused by the test" >&2\nexit 1\n' > "$ORIGIN/hooks/pre-receive"
chmod +x "$ORIGIN/hooks/pre-receive"
run_release 'e\n\n' GIT_EDITOR="$BIN/editor"
[ "$LAST_STATUS" -eq 1 ] || fail "a refused push must exit 1, got $LAST_STATUS"
[ -z "$(git -C "$CLONE" tag --list v0.3.0)" ] || fail 'the local tag must be removed, or the next run sees the release as done'
no_tag
logged 'release v' && fail 'no release without a pushed tag'
grep -qF 'EDITED BY THE TEST' "$CLONE/.git/release-notes-v0.3.0.md" || fail 'the edited notes must be kept'
out_has "git tag -a v0.3.0 ${C1}" || fail 'the tag command must be printed'
out_has 'git push origin v0.3.0' || fail 'the push command must be printed'
out_has 'gh release create v0.3.0' || fail 'the release command must be printed'
rm "$ORIGIN/hooks/pre-receive"
: > "$STUB/calls.log"
run_release '\n'
released v0.3.0 "$C1"
pass 'a refused tag push removes the local tag, keeps the notes, and a second run releases'

new_case 'the tag cannot be made'
base_on_prod
push
git -C "$CLONE" tag v0.3.0/blocker "$C0"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a tag that cannot be made must exit 1, got $LAST_STATUS"
no_tag
[ -f "$CLONE/.git/release-notes-v0.3.0.md" ] || fail 'the notes must be kept'
out_has 'the tag v0.3.0 could not be made, so nothing is published' || fail 'the stop must say why'
pass 'a tag that cannot be made publishes nothing and keeps the notes'

new_case 'a newer candidate reaches the gate after the approval'
base
push
release_run 500 'status=waiting prod=waiting'
states 500 on-approve "$DEPLOYING" "$DEPLOYING" "$DEPLOYING" "$DEPLOYED" "$DONE"
cd_run 460 "CD for 2222222222222222222222222222222222222222 (CI run 7100, attempt 1)" workflow_run 'status=in_progress prod=pending'
touch "$STUB/runs/460/tick-on-list"
states 460 queue 'status=in_progress prod=pending' 'status=waiting prod=waiting'
run_release '\n'
released v0.3.0 "$C1"
logged 'reject 460' && fail 'a candidate that reaches the gate after the approval must be left for the owner'
pass 'after the approval, other candidates at the gate are left alone'

new_case 'the prod job is skipped'
base
push
release_run 500 'status=completed conclusion=success prod=skipped'
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a skipped prod job must stop, got $LAST_STATUS"
out_has 'skipped its prod job' || fail 'the stop must say the prod job was skipped'
logged rerun && fail 'a skipped prod job is never re-run'
no_tag
pass 'a skipped prod job (red staging, refused gate) stops with no re-run'

new_case 'the run ended before its prod job'
base
push
release_run 500 'status=completed conclusion=failure prod=none'
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a run that ended before its prod job must stop, got $LAST_STATUS"
out_has 'ended before its prod job' || fail 'the stop must say why'
no_tag
pass 'a run that ended before its prod job stops'

new_case 'a later deploy replaced the release'
base
push
cd_run 300 'CD by hand: sha-abc1234' workflow_dispatch "$DONE"
release_run 500 'status=waiting prod=waiting'
states 500 on-approve "$DEPLOYING" "$DEPLOYED" "status=completed conclusion=success prod=completed pconc=success steps=12 nr=success deploy_run=300"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "prod not on the release commit after the deploy must stop, got $LAST_STATUS"
out_has 'but prod no longer runs it: a later deploy replaced it with CD by hand: sha-abc1234' || fail 'the stop must name what prod runs'
no_tag
approve_ok 500
states 500 on-rerun 'status=waiting prod=waiting'
: > "$STUB/calls.log"
run_release '\n'
released v0.3.0 "$C1"
out_has "it deployed ${C1} earlier, but a later deploy replaced it" || fail 'the new start must say why it deploys again'
logged 'rerun 500' || fail 'the new start must run the release run again'
pass 'the release is not tagged when prod no longer runs it, and a new start deploys it again'

new_case 'only an empty line publishes'
base_on_prod
push
run_release 'n\ny\nyes\nq\n'
[ "$LAST_STATUS" -eq 0 ] || fail "q must exit 0, got $LAST_STATUS"
out_has "Unknown answer 'y'." || fail 'y must be an unknown answer'
logged 'release v' && fail 'n, y or yes must never publish'
no_tag
pass 'n, y and yes are unknown answers; only Enter publishes'

new_case 'the change list cannot be built'
base
push
release_run 500 'status=waiting prod=waiting'
approve_ok 500
touch "$STUB/pulls-fail"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a change list that cannot be built must stop, got $LAST_STATUS"
out_has 'the change list could not be built' || fail 'the stop must say why'
logged approve && fail 'nothing must be approved before the change list exists'
no_tag
pass 'a change list that cannot be built stops before any deploy'

new_case 'bad start'
base
push
LAST_STATUS=0
LAST_OUTPUT="$(cd "$CLONE" && "${TIMEOUT[@]}" "$RELEASE_SH" --help 2>&1 < /dev/null)" || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 2 ] || fail "an argument must exit 2, got $LAST_STATUS"
touch "$STUB/not-signed-in"
run_release ''
[ "$LAST_STATUS" -eq 2 ] || fail "gh signed out must exit 2, got $LAST_STATUS"
out_has 'gh is not signed in' || fail 'a signed-out gh must be named'
rm "$STUB/not-signed-in"
LAST_STATUS=0
LAST_OUTPUT="$(cd "$T" && printf '' | env RELEASE_POLL_SECONDS=0 "$RELEASE_SH" 2>&1)" || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 2 ] || fail "outside a clone it must exit 2, got $LAST_STATUS"
[ "$(log_count .)" -eq 0 ] || fail 'nothing must be written'
pass 'an argument, a signed-out gh or a folder outside a clone exits 2'

new_case 'the issue cannot be closed'
base_on_prod
push
touch "$STUB/issue-close-fail"
run_release '\n'
released v0.3.0 "$C1"
out_has 'WARNING: the "Next release" issue could not be closed. Close it by hand.' || fail 'the failed close must warn'
pass 'an issue that cannot be closed only warns after the release'

new_case 'stopped while the tag is pushed'
base_on_prod
push
printf '#!/bin/sh\ntouch "%s"\nsleep 2\nexit 1\n' "$T/hook-started" > "$ORIGIN/hooks/pre-receive"
chmod +x "$ORIGIN/hooks/pre-receive"
run_in_background '\n'
wait_for_file "$T/hook-started" || fail 'the push must start'
kill -TERM "$BG_PID"
LAST_STATUS=0
wait "$BG_PID" || LAST_STATUS=$?
LAST_OUTPUT="$(cat "$T/out")"
[ "$LAST_STATUS" -eq 143 ] || fail "a stop during the push must exit 143, got $LAST_STATUS"
[ -z "$(git -C "$CLONE" tag --list v0.3.0)" ] || fail 'the local tag must be removed, or the next run sees the release as done'
no_tag
out_has 'did not reach GitHub, so nothing is published and the local tag is removed' || fail 'the stop must say what happened'
[ -f "$CLONE/.git/release-notes-v0.3.0.md" ] || fail 'the notes must be kept'
pass 'a stop (Ctrl-C or a kill) during the push removes the local tag'

new_case 'stopped while the release is created'
base_on_prod
push
touch "$STUB/release-slow"
run_in_background '\n'
wait_for_file "$STUB/release-started" || fail 'the release create must start'
kill -TERM "$BG_PID"
LAST_STATUS=0
wait "$BG_PID" || LAST_STATUS=$?
LAST_OUTPUT="$(cat "$T/out")"
[ "$LAST_STATUS" -eq 143 ] || fail "a stop during the release create must exit 143, got $LAST_STATUS"
[ "$(tag_of v0.3.0)" = "$C1" ] || fail 'the pushed tag stays'
out_has 'the tag v0.3.0 is on GitHub, but its release is not published. Finish with:' || fail 'the stop must say how to finish'
out_has 'gh release create v0.3.0 --verify-tag --latest --title "v0.3.0" --notes-file' || fail 'the finish command must be printed'
pass 'a stop during the release create prints the commands that finish it'

new_case 'a newest tag that is not on GitHub'
base_on_prod
push
git -C "$CLONE" tag -a v0.2.2 -m v0.2.2 "$C1"
run_release '\n'
[ "$LAST_STATUS" -eq 1 ] || fail "a local-only newest tag must stop, got $LAST_STATUS"
out_has 'the newest tag here, v0.2.2, is not on GitHub' || fail 'the stop must name the tag'
logged 'release v' && fail 'nothing must be published'
pass 'a newest tag only this clone has stops the run before anything else'

new_case 'the release run deployed earlier, and a later deploy replaced it'
base
push
cd_run 300 'CD by hand: sha-abc1234' workflow_dispatch "$DONE"
release_run 500 "$DONE"
: > "$STUB/deployments"
on_prod 300
on_prod 500
states 500 on-rerun 'status=waiting prod=waiting'
approve_ok 500
run_release '\n'
released v0.3.0 "$C1"
logged 'rerun 500' || fail 'an old deploy that was replaced must be run again'
[ "$(line_of 'rerun 500')" -lt "$(line_of 'approve 500')" ] || fail 're-run first, then approve'
pass 'a release run whose old deploy was replaced is run again, not taken as done'

new_case 'no answer at the version question'
base_on_prod type-refactor
push
run_release ''
[ "$LAST_STATUS" -eq 0 ] || fail "no answer at the version question must exit 0, got $LAST_STATUS"
out_has 'Stopped. Nothing was tagged.' || fail 'it must stop'
[ "$(git -C "$ORIGIN" tag | grep -c .)" -eq 1 ] || fail 'no tag must be made'
pass 'a closed input at the version question stops with no tag'

new_case 'git or gh not installed'
base
push
mkdir -p "$T/min"
ln -s "$(command -v bash)" "$T/min/bash"
ln -s "$(command -v dirname)" "$T/min/dirname"
LAST_STATUS=0
LAST_OUTPUT="$(cd "$CLONE" && PATH="$T/min" "$RELEASE_SH" 2>&1 < /dev/null)" || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 2 ] || fail "a missing git must exit 2, got $LAST_STATUS"
out_has 'git is not installed.' || fail 'the missing git must be named'
ln -s "$(command -v git)" "$T/min/git"
LAST_STATUS=0
LAST_OUTPUT="$(cd "$CLONE" && PATH="$T/min" "$RELEASE_SH" 2>&1 < /dev/null)" || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 2 ] || fail "a missing gh must exit 2, got $LAST_STATUS"
out_has 'gh is not installed' || fail 'the missing gh must be named'
pass 'a missing git or gh exits 2'

new_case 'the workflows still match what the script reads'
CD_YML="$REPO_ROOT/.github/workflows/cd.yml"
DEPLOY_YML="$REPO_ROOT/.github/workflows/deploy.yml"
GITLEAKS_YML="$REPO_ROOT/.github/workflows/gitleaks.yml"
run_name="$(awk '/^run-name:/ { inside = 1; next } inside && /^[^ ]/ { exit } inside { print }' "$CD_YML")"
for needed in "format('CD for {0} (CI run {1}, attempt {2})', github.event.workflow_run.head_sha, github.event.workflow_run.id, github.event.workflow_run.run_attempt)" \
    "github.event_name == 'workflow_run'" "format('CD by hand: {0}', inputs.image_tag || github.sha)"; do
    grep -qF -- "$needed" <<< "$run_name" || fail "cd.yml run-name must contain: $needed"
done
job_name() { grep -A 1 -E "^  $2:\$" "$1" | tail -n 1; }
[ "$(job_name "$CD_YML" deploy-prod)" = '    name: Deploy to prod' ] \
    || fail 'cd.yml deploy-prod must be named "Deploy to prod" (release.sh reads that name)'
# shellcheck disable=SC2016 # a workflow expression, not a shell one
[ "$(job_name "$DEPLOY_YML" deploy)" = '    name: Deploy to ${{ inputs.environment }} (Hetzner)' ] \
    || fail 'deploy.yml deploy must be named "Deploy to <environment> (Hetzner)"'
[ "$(job_name "$CD_YML" next-release)" = '    name: Next release issue' ] \
    || fail 'cd.yml next-release must be named "Next release issue"'
for needed in "PROD_JOB='Deploy to prod / Deploy to production (Hetzner)'" "PROD_JOB_SKIPPED='Deploy to prod'" \
    "NEXT_RELEASE_JOB='Next release issue'"; do
    grep -qxF -- "$needed" "$RELEASE_SH" || fail "release.sh must look for the job names the workflows use: $needed"
done
# shellcheck disable=SC2016 # workflow expressions, not shell ones
grep -qF 'image_tag: ${{ steps.tag.outputs.tag }}' "$DEPLOY_YML" || fail 'deploy.yml must output the resolved tag'
next_release_job="$(awk '/^  next-release:$/ { inside = 1; print; next } inside && /^  [^ ]/ { exit } inside { print }' "$CD_YML")"
grep -qxF '    continue-on-error: true' <<< "$next_release_job" || fail 'the issue job must never turn a good deploy red'
# shellcheck disable=SC2016
grep -qxF "    if: \${{ !cancelled() && needs.deploy-prod.result == 'success' }}" <<< "$next_release_job" \
    || fail 'the issue job must run only after a successful prod deploy, also when staging was skipped'
# shellcheck disable=SC2016
grep -qF 'value: ${{ jobs.deploy.outputs.image_tag }}' "$DEPLOY_YML" || fail 'deploy.yml must pass the tag to its caller'
# shellcheck disable=SC2016
grep -qF 'IMAGE_TAG: ${{ needs.deploy-prod.outputs.image_tag }}' "$CD_YML" || fail 'the issue job must take the tag the rollout resolved'
gitleaks_on="$(awk '/^on:/ { inside = 1; next } inside && /^[^ ]/ { exit } inside { print }' "$GITLEAKS_YML")"
grep -qxF '  push:' <<< "$gitleaks_on" || fail 'gitleaks.yml must run on every push (release.sh reads its run as "pushed, not CI")'
grep -qxF '    branches: ["main"]' <<< "$gitleaks_on" || fail 'gitleaks.yml must run on pushes to main'
if grep -qE 'paths(-ignore)?:' <<< "$gitleaks_on"; then
    fail 'gitleaks.yml must have no path filter, or a docs-only commit would look unprocessed'
fi
pass 'cd.yml, deploy.yml and gitleaks.yml still give release.sh the names and runs it reads'

printf '\nrelease.tests.sh: %d cases passed\n' "$cases"
