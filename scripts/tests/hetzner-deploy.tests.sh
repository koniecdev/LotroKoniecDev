#!/usr/bin/env bash
# Test suite for the Hetzner rollout script (scripts/hetzner/deploy.sh — HETZ-04 / #490).
#
# deploy.sh is the one piece of CD that runs ON a production box, and its real inputs (a live Docker
# daemon, GHCR, Neon) are exactly what a test cannot have. So each case builds a throwaway stack dir
# (.env + a compose file) and puts a STUB `docker` first on PATH which records every invocation in
# order and fakes the outcomes the script branches on.
#
# The property that matters most here — and the one the first cut of this script got WRONG — is the
# ORDER: the migrator must run as its own gate BEFORE `up -d` recreates any app container. Compose's
# `depends_on: service_completed_successfully` gates the START of the apps, not their CREATION, so
# leaving the gate to compose destroys the old containers first and a failed migration takes the site
# DOWN instead of leaving the previous release serving (verified against Docker 29.6.1, #490 review).
# Several cases below assert exactly that ordering and that a failed gate never reaches `up -d`.
#
# CI runs this in the `guards` job, right next to the other bash gates.

set -euo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
REPO_ROOT="$(cd "$SCRIPTS_DIR/.." && pwd)"
DEPLOY_SH="$SCRIPTS_DIR/hetzner/deploy.sh"
TMP_ROOT="$(mktemp -d)"
# The chmod first: a case stopped while its stack dir is at mode 555 leaves a folder `rm -rf` cannot empty.
trap 'chmod -R u+rwX "$TMP_ROOT" 2>/dev/null || true; rm -rf "$TMP_ROOT"' EXIT

REPO='ghcr.io/koniecdev'
IMAGES='lotrokoniecdev-migrator lotrokoniecdev-auth-api lotrokoniecdev-tms-api lotrokoniecdev-frontend'
GOOD_DIGEST='sha256:1111111111111111111111111111111111111111111111111111111111111111'
OTHER_DIGEST='sha256:2222222222222222222222222222222222222222222222222222222222222222'
STDIN_MARKER='stdin-of-deploy.sh'
export STDIN_MARKER

cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    printf '  --- docker calls ---\n'
    sed 's/^/    /' "$TMP_ROOT/calls.log" 2>/dev/null || true
    printf '  --- deploy.sh output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# A stack dir as scripts/hetzner/bootstrap.sh + the runbook leave one: compose file, Caddyfile,
# `.env` at 0600 already naming the currently-deployed tag.
new_stack() {
    local dir="$TMP_ROOT/stack-$1"
    mkdir -p "$dir/.docker/hetzner"
    # SC2016: ${IMAGE_TAG} is compose's own interpolation syntax — it must land in the file verbatim.
    # shellcheck disable=SC2016
    printf 'services:\n  migrator:\n    image: %s/lotrokoniecdev-migrator:${IMAGE_TAG}\n' "$REPO" \
        > "$dir/compose.hetzner.yaml"
    # SC2016: {$DOMAIN_APP} is Caddy's env placeholder — it must land in the file verbatim.
    # shellcheck disable=SC2016
    printf '{$DOMAIN_APP} {\n\treverse_proxy frontend:8080\n}\n' > "$dir/.docker/hetzner/Caddyfile"
    {
        echo 'COMPOSE_PROJECT_NAME=lotro-prod'
        echo 'IMAGE_TAG=sha-0000000'
        echo 'ConnectionStrings__AuthDatabase=Host=neon;Password=supersecret'
    } > "$dir/.env"
    chmod 600 "$dir/.env"
    printf '%s' "$dir"
}

# The stub `docker`. Behavior is steered by env vars the cases set:
#   STUB_CADDY_INVALID / STUB_PULL_FAIL / STUB_MIGRATE_FAIL / STUB_UP_FAIL — make that step exit 1
#   STUB_DIGEST — the digest `docker image inspect` reports for every image
#   STUB_BROKER_BOOTING_TRIES=<n> — the first n `rabbitmqctl set_operator_policy` calls fail, as on a
#     broker that is still starting; STUB_BROKER_DOWN — every one of them fails;
#     STUB_BROKER_RESTARTED — the first one returns 137, as a try does when the container restarts
#   STUB_BROKER_HANGS — the stub `timeout` lets the call start, then stops it with 124, as it does on
#     a broker that never answers
# Every call is appended to $STUB_LOG *in order*, so a case can assert what ran, what did NOT, and
# crucially in which sequence.
install_stub_docker() {
    local bin="$TMP_ROOT/bin"
    mkdir -p "$bin"
    cat > "$bin/docker" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$STUB_LOG"

if [ "$1" = compose ]; then
    # docker compose [global flags] <verb> … — walk past `compose` and any global flag (`-f <file>`,
    # `--profile <name>`, …) to find the verb. Flag-tolerant on purpose: the Caddyfile gate passes
    # `--profile validate`, and a parser that mistook a flag for the verb would silently stop faking
    # that step's outcome, which would make the failure cases below assert nothing.
    verb=""
    skip_next=0
    for arg in "$@"; do
        if [ "$skip_next" = 1 ]; then skip_next=0; continue; fi
        case "$arg" in
            compose) continue ;;
            -f | --file | -p | --project-name | --profile | --env-file) skip_next=1; continue ;;
            -*) continue ;;
            *) verb="$arg"; break ;;
        esac
    done
    case "$verb" in
        pull) [ -z "${STUB_PULL_FAIL:-}" ] || { echo 'stub: pull failed' >&2; exit 1; } ;;
        run)
            # Two distinct `compose run` calls — tell them apart by what they run:
            #   `--profile validate run --rm --no-deps caddy-validate`  → the Caddyfile check
            #   `run --rm --no-deps migrator`                           → the migration gate
            case "$*" in
                *validate*) [ -z "${STUB_CADDY_INVALID:-}" ] || { echo 'stub: Caddyfile invalid' >&2; exit 1; } ;;
                *migrator*) [ -z "${STUB_MIGRATE_FAIL:-}" ] || { echo 'stub: migration failed' >&2; exit 1; } ;;
            esac
            ;;
        up)   [ -z "${STUB_UP_FAIL:-}" ] || { echo 'stub: up failed' >&2; exit 1; } ;;
        ps)   echo 'NAME   STATUS' ;;
        exec)
            case "$*" in
                *set_operator_policy*)
                    # run_deploy feeds a marker into deploy.sh's stdin. The policy call must never
                    # read it (see deploy.sh: SIGTTIN under `timeout` in a hand-run deploy).
                    if IFS= read -r line && [ "$line" = "$STDIN_MARKER" ]; then
                        echo 'read-stdin' >> "$STUB_LOG"
                        echo 'stub: the policy call read the stdin of deploy.sh' >&2
                        exit 70
                    fi
                    # This call is already in the log, so the count includes it.
                    tries="$(grep -c 'set_operator_policy' "$STUB_LOG")"
                    # A broken wait bound would retry forever and hang the suite. Let a runaway loop
                    # land instead, so the case that counts the tries fails fast.
                    if [ "$tries" -gt 50 ]; then
                        echo 'stub: runaway retry loop, letting it land'
                        exit 0
                    fi
                    if [ -n "${STUB_BROKER_RESTARTED:-}" ] && [ "$tries" -eq 1 ]; then
                        echo 'stub: the exec was killed with the container' >&2
                        exit 137
                    fi
                    if [ -n "${STUB_BROKER_DOWN:-}" ] || [ "$tries" -le "${STUB_BROKER_BOOTING_TRIES:-0}" ]; then
                        echo "stub: unable to perform an operation on node 'rabbit@lotro-rabbitmq'" >&2
                        exit 69
                    fi
                    echo 'stub: policy set'
                    ;;
            esac
            ;;
    esac
    exit 0
fi

case "$1 $2" in
    'image inspect')
        ref="$3"
        printf '%s@%s\n' "${ref%:*}" "${STUB_DIGEST}"
        ;;
    'image prune') ;;
esac
exit 0
STUB
    chmod +x "$bin/docker"
    # A stub `timeout` too: macOS has none, and the real one would make the hang case wait for real.
    cat > "$bin/timeout" <<'STUB'
#!/usr/bin/env bash
kill_after=''
if [ "$1" = -k ]; then
    kill_after="-k $2 "
    shift 2
fi
printf 'timeout %s%s\n' "$kill_after" "$1" >> "$STUB_LOG"
shift
if [ -n "${STUB_BROKER_HANGS:-}" ]; then
    "$@" > /dev/null 2>&1
    exit 124
fi
exec "$@"
STUB
    chmod +x "$bin/timeout"
    PATH="$bin:$PATH"
    export PATH
}

run_deploy() {
    local dir="$1"
    shift
    STUB_LOG="$TMP_ROOT/calls.log"
    : > "$STUB_LOG"
    export STUB_LOG
    LAST_STATUS=0
    # No pause between broker tries, so a broker that never answers costs no wall-clock time here. The
    # marker on stdin stands in for an operator's terminal (see the stub's policy branch).
    LAST_OUTPUT="$(printf '%s\n' "$STDIN_MARKER" \
        | env STACK_DIR="$dir" BROKER_POLICY_RETRY_SECONDS=0 "$@" bash "$DEPLOY_SH" 2>&1)" || LAST_STATUS=$?
}

expected_digests() {
    local digest="$1" out="" image
    for image in $IMAGES; do
        out="${out}${out:+ }${REPO}/${image}@${digest}"
    done
    printf '%s' "$out"
}

env_tag() { sed -n 's/^IMAGE_TAG=//p' "$1/.env" | head -1; }
policy_tries() { grep -c 'set_operator_policy' "$TMP_ROOT/calls.log" || true; }
called() { grep -qF -- "$1" "$TMP_ROOT/calls.log"; }
# Line number of the first docker call containing $1 (0 = never called).
call_line() { grep -nF -- "$1" "$TMP_ROOT/calls.log" | head -1 | cut -d: -f1; }

# Service keys that pin a static ipv4_address in the REAL compose.hetzner.yaml (service keys are the
# only two-space-indented bare keys under `services:`).
pinned_ip_services() {
    awk '
        /^services:/ { in_services = 1; next }
        /^[^[:space:]#]/ { in_services = 0 }
        in_services && /^  [A-Za-z0-9_.-]+:[[:space:]]*$/ { svc = $1; sub(/:$/, "", svc); next }
        in_services && /ipv4_address:/ && svc != "" { print svc }
    ' "$REPO_ROOT/compose.hetzner.yaml" | sort -u
}

# The service every recorded `compose … run …` call targets (the first non-flag word after `run`).
run_targets() {
    awk '{
        seen = 0
        for (i = 1; i <= NF; i++) {
            if ($i == "run") { seen = 1; continue }
            if (!seen) continue
            if ($i ~ /^-/) continue
            print $i
            break
        }
    }' "$TMP_ROOT/calls.log"
}

install_stub_docker

# --- Input validation: a bad tag must die before it can reach compose or a remote shell -----------

CASE='no IMAGE_TAG'
stack="$(new_stack novar)"
run_deploy "$stack"
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 on a missing IMAGE_TAG' "got $LAST_STATUS"
pass 'a missing IMAGE_TAG is a usage error (exit 2)'

CASE='broker wait not a number'
stack="$(new_stack badwait)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 BROKER_POLICY_WAIT_SECONDS=3m
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 on a broker wait that is not a whole number' "got $LAST_STATUS"
called 'pull' && fail 'a bad setting still reached docker'
[ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env was rewritten although nothing rolled'
pass 'a broker wait, retry or time limit that is not a whole number is a usage error before anything runs'

CASE='hostile IMAGE_TAG'
stack="$(new_stack hostile)"
run_deploy "$stack" IMAGE_TAG='sha-abc; rm -rf /'
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 on a tag carrying shell metacharacters' "got $LAST_STATUS"
[ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env was rewritten with a rejected tag'
called 'pull' && fail 'a rejected tag still reached docker'
pass 'a tag outside [A-Za-z0-9._-] is rejected and nothing runs'

CASE='no .env'
stack="$(new_stack noenv)"
rm "$stack/.env"
run_deploy "$stack" IMAGE_TAG=sha-abc1234
[ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 when the box .env is missing' "got $LAST_STATUS"
pass 'a stack dir without .env is a usage error (exit 2)'

# A root session left .env as root:root 0600 (#806). `chmod 000` gives the same result for this
# user: a file it cannot read. Root reads a file at any mode, and so does a user on a file system that
# ignores the mode bits. There the case would prove nothing, so it skips.
CASE='.env not readable'
stack="$(new_stack unreadable)"
chmod 000 "$stack/.env"
if [ -r "$stack/.env" ]; then
    chmod 600 "$stack/.env"
    if [ "$(id -u)" -eq 0 ]; then
        printf -- '- [%s] SKIPPED: this suite runs as root, and root can read a file at mode 000\n' "$CASE"
    else
        printf -- '- [%s] SKIPPED: a file at mode 000 stays readable here (the file system ignores the mode bits, or this user can read any file)\n' "$CASE"
    fi
else
    # A user with no name: `ls` prints the uid, and deploy.sh falls back to it too.
    reader="$(id -un 2>/dev/null || id -u)"
    run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST"
    chmod 600 "$stack/.env"
    [ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 when this user cannot read the box .env' "got $LAST_STATUS"
    grep -qF "::error::deploy.sh: $stack/.env exists, but ${reader} cannot read it (owner ${reader}, mode ----------" \
        <<<"$LAST_OUTPUT" || fail 'the message must be an error line that names the reader, the owner and the mode'
    grep -qF "Fix it as root: chown deploy:deploy $stack/.env && chmod 600 $stack/.env" <<<"$LAST_OUTPUT" \
        || fail 'the message must name the fix'
    # The file may already belong to deploy, and then only a run as deploy fixes it.
    if [ "$reader" != deploy ]; then
        grep -qF 'Then run this script as deploy: sudo -u deploy env IMAGE_TAG=sha-abc1234 bash' <<<"$LAST_OUTPUT" \
            || fail 'a user other than deploy must be told to run the script as deploy'
    fi
    grep -qF 'Nothing was pulled' <<<"$LAST_OUTPUT" || fail 'the message must say that nothing was pulled'
    [ -s "$TMP_ROOT/calls.log" ] && fail 'docker ran although .env could not be read (the trap ran, or worse)'
    grep -q 'PREVIOUS_IMAGE_TAG=' <<<"$LAST_OUTPUT" && fail 'CD must get no rollback target when nothing rolled'
    [ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env was rewritten although nothing rolled'
    pass 'a .env this user cannot read is refused with its owner, its mode and the fix, before any docker call'

    CASE='.env readable again'
    run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST"
    [ "$LAST_STATUS" -eq 0 ] || fail 'the same stack must roll once .env is readable again' "status $LAST_STATUS"
    grep -q 'PREVIOUS_IMAGE_TAG=sha-0000000' <<<"$LAST_OUTPUT" || fail 'the previous tag was not printed for the rollback path'
    [ "$(env_tag "$stack")" = 'sha-abc1234' ] || fail 'IMAGE_TAG was not pinned in .env' "$(cat "$stack/.env")"
    pass 'the same stack rolls once its .env is readable again'

    CASE='.env is a link'
    # Without `ls -L` the message would show the link's own mode (lrwxr-xr-x), not the file's.
    stack="$(new_stack linked)"
    mv "$stack/.env" "$stack/env.target"
    ln -s env.target "$stack/.env"
    chmod 000 "$stack/env.target"
    run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST"
    chmod 600 "$stack/env.target"
    [ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 when .env links to a file this user cannot read' "got $LAST_STATUS"
    grep -qF "(owner ${reader}, mode ----------" <<<"$LAST_OUTPUT" \
        || fail 'the message must report the file the link points to, not the link'
    [ -s "$TMP_ROOT/calls.log" ] && fail 'docker ran although .env could not be read'
    pass 'a .env that links to an unreadable file is refused with the mode of that file'

    # In the cases above the owner, the group and the reader are one user, so a message that printed
    # the group or the reader as the owner would pass. A stub `ls` names other ones.
    mkdir -p "$TMP_ROOT/lsbin"
    cat > "$TMP_ROOT/lsbin/ls" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$STUB_LS_LINE"
STUB
    chmod +x "$TMP_ROOT/lsbin/ls"

    CASE='.env owned by root'
    stack="$(new_stack rootowned)"
    chmod 000 "$stack/.env"
    run_deploy "$stack" IMAGE_TAG=sha-abc1234 PATH="$TMP_ROOT/lsbin:$PATH" \
        STUB_LS_LINE='-rw------- 1 root lotrogroup 120 Oct 19 10:00 .env'
    chmod 600 "$stack/.env"
    [ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 on a root-owned .env' "got $LAST_STATUS"
    grep -qF '(owner root, mode -rw-------)' <<<"$LAST_OUTPUT" || fail 'the owner must be the third field of ls, not the group'
    pass 'the message names the owner from ls, not the group or the reader'

    # CD runs as deploy, and no test runner does, so a stub `id` stands in for both readers that a
    # real run of this suite never is: deploy, and a uid with no name.
    mkdir -p "$TMP_ROOT/idbin"
    cat > "$TMP_ROOT/idbin/id" <<'STUB'
#!/usr/bin/env bash
if [ "$1" = -un ]; then
    if [ -n "${STUB_ID_NAME:-}" ]; then
        printf '%s\n' "$STUB_ID_NAME"
        exit 0
    fi
    echo "id: cannot find name for user ID $("$STUB_REAL_ID" -u)" >&2
    exit 1
fi
exec "$STUB_REAL_ID" "$@"
STUB
    chmod +x "$TMP_ROOT/idbin/id"
    real_id="$(command -v id)"

    CASE='deploy cannot read .env'
    stack="$(new_stack asdeploy)"
    chmod 000 "$stack/.env"
    run_deploy "$stack" IMAGE_TAG=sha-abc1234 PATH="$TMP_ROOT/idbin:$PATH" STUB_REAL_ID="$real_id" STUB_ID_NAME=deploy
    chmod 600 "$stack/.env"
    [ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 when deploy cannot read .env' "got $LAST_STATUS"
    grep -qF "$stack/.env exists, but deploy cannot read it" <<<"$LAST_OUTPUT" || fail 'the message must name deploy as the reader'
    grep -qF "Fix it as root: chown deploy:deploy $stack/.env && chmod 600 $stack/.env" <<<"$LAST_OUTPUT" \
        || fail 'the message must name the fix'
    grep -qF 'run this script as deploy' <<<"$LAST_OUTPUT" && fail 'a script that already runs as deploy must not be told to run as deploy'
    pass 'a run as deploy (the CD run) gets the fix, and no line telling it to run as deploy'

    CASE='reader with no name'
    stack="$(new_stack noname)"
    chmod 000 "$stack/.env"
    run_deploy "$stack" IMAGE_TAG=sha-abc1234 PATH="$TMP_ROOT/idbin:$PATH" STUB_REAL_ID="$real_id"
    chmod 600 "$stack/.env"
    [ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 when a uid with no name cannot read .env' "got $LAST_STATUS"
    grep -qF "$stack/.env exists, but $("$real_id" -u) cannot read it" <<<"$LAST_OUTPUT" \
        || fail 'a uid with no name must be shown as the number'
    grep -qF 'cannot find name' <<<"$LAST_OUTPUT" && fail 'the error that id -un prints must not reach the message'
    pass 'a reader with no user name is shown by its uid'

    CASE='compose file not readable'
    stack="$(new_stack composeunreadable)"
    chmod 000 "$stack/compose.hetzner.yaml"
    run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST"
    chmod 644 "$stack/compose.hetzner.yaml"
    [ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 when this user cannot read the compose file' "got $LAST_STATUS"
    grep -qF "$stack/compose.hetzner.yaml exists, but ${reader} cannot read it (owner ${reader}, mode ----------" \
        <<<"$LAST_OUTPUT" || fail 'the message must name the compose file, its owner and its mode'
    grep -qF "Fix it as root: chown deploy:deploy $stack/compose.hetzner.yaml && chmod 644 $stack/compose.hetzner.yaml" \
        <<<"$LAST_OUTPUT" || fail 'the message must name the fix, with the mode a compose file has'
    [ -s "$TMP_ROOT/calls.log" ] && fail 'docker ran although the compose file could not be read'
    pass 'a compose file this user cannot read is refused the same way, before any docker call'

    CASE='stack dir not writable'
    # Without this check the roll would fail on the last step, after `up -d` replaced the containers.
    stack="$(new_stack readonlydir)"
    chmod 555 "$stack"
    run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST"
    chmod 755 "$stack"
    [ "$LAST_STATUS" -eq 2 ] || fail 'expected exit 2 when this user cannot write to the stack dir' "got $LAST_STATUS"
    grep -qF "${reader} cannot write to $stack, and the last step writes .env there (owner ${reader}, mode dr-xr-xr-x" \
        <<<"$LAST_OUTPUT" || fail 'the message must name the stack dir, its owner and its mode'
    grep -qF "Fix it as root: chown deploy:deploy $stack && chmod 755 $stack" <<<"$LAST_OUTPUT" \
        || fail 'the message must name the fix, with the mode bootstrap.sh gives the stack dir'
    [ -s "$TMP_ROOT/calls.log" ] && fail 'docker ran although the last step could never write .env'
    [ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env changed although nothing rolled'
    pass 'a stack dir this user cannot write to is refused before any docker call, not after up -d'
fi

# --- Happy path + THE ordering property -----------------------------------------------------------

CASE='happy path'
stack="$(new_stack happy)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 EXPECTED_DIGESTS="$(expected_digests "$GOOD_DIGEST")" \
    STUB_DIGEST="$GOOD_DIGEST"
[ "$LAST_STATUS" -eq 0 ] || fail 'a clean roll should exit 0' "status $LAST_STATUS"
grep -q 'PREVIOUS_IMAGE_TAG=sha-0000000' <<<"$LAST_OUTPUT" || fail 'the previous tag was not printed for the rollback path'
[ "$(env_tag "$stack")" = 'sha-abc1234' ] || fail 'IMAGE_TAG was not pinned in .env' "$(cat "$stack/.env")"
grep -q 'supersecret' "$stack/.env" || fail 'rewriting .env dropped the other variables'
called 'run --rm --no-deps caddy-validate' || fail 'the Caddyfile was never validated'
called 'pull' || fail 'images were never pulled'
called 'run --rm --no-deps migrator' || fail 'the migration gate never ran'
called 'up -d --remove-orphans' || fail 'containers were never rolled'
called 'caddy reload' || fail 'Caddy was never reloaded — a changed Caddyfile would sit unapplied'
called 'image prune --all --force --filter until=' || fail 'the age-based image prune never ran'
pass 'validates, pulls, migrates, rolls, reloads Caddy, prunes, and pins the tag'

CASE='migration gate ordering'
# THE regression test for the #490 review finding: compose recreates app containers BEFORE it honours
# `depends_on: service_completed_successfully`, so the migrator MUST run as its own gate first.
gate_at="$(call_line 'run --rm --no-deps migrator')"
up_at="$(call_line 'up -d --remove-orphans')"
[ -n "$gate_at" ] && [ -n "$up_at" ] || fail 'expected both the migration gate and up -d to run'
[ "$gate_at" -lt "$up_at" ] \
    || fail 'the migrator must run BEFORE up -d recreates the apps' "gate at call #$gate_at, up -d at call #$up_at"
pass 'the migrator gate runs strictly BEFORE up -d recreates any app container'

CASE='one-off gates vs static IPs'
# THE regression test for the #506 review finding. `compose run <svc>` builds the one-off container
# from the FULL service definition — static `ipv4_address` included — so targeting a service whose IP
# the LIVE container already holds dies with "failed to set up container networking: Address already
# in use" (reproduced on Docker 29.6.1, the engine the boxes run). It bites on the SECOND deploy, not
# the first, which is precisely the failure a green first roll cannot reveal. Invariant: no service
# that pins an IP in the real compose.hetzner.yaml may ever be a `compose run` target.
pinned="$(pinned_ip_services)"
[ -n "$pinned" ] || fail 'expected compose.hetzner.yaml to pin at least one static ipv4_address (Caddy)'
for svc in $pinned; do
    run_targets | grep -qxF "$svc" && fail \
        "deploy.sh builds a one-off container from '$svc', which pins a static ipv4_address" \
        "the running container already holds that address — every deploy after the first would die with 'Address already in use'"
done
pass "no one-off container is built from a static-IP service ($(echo "$pinned" | tr '\n' ' '))"

CASE='.env mode'
# SC2012: the path is ours and has no exotic characters; `ls -l` is the one mode probe whose output
# is identical on the GNU (CI) and BSD (macOS dev) coreutils — `stat` needs a different flag on each.
# shellcheck disable=SC2012
[ "$(ls -l "$stack/.env" | cut -c1-10)" = '-rw-------' ] \
    || fail '.env must stay 0600 after the rewrite' "$(ls -l "$stack/.env")"
pass 'the rewritten .env is still 0600 (it holds every secret on the box)'

CASE='IMAGE_TAG absent from .env'
stack="$(new_stack notag)"
grep -v '^IMAGE_TAG=' "$stack/.env" > "$stack/.env.new" && mv "$stack/.env.new" "$stack/.env"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST"
[ "$LAST_STATUS" -eq 0 ] || fail 'expected a clean roll' "status $LAST_STATUS"
[ "$(env_tag "$stack")" = 'sha-abc1234' ] || fail 'IMAGE_TAG was not appended to a .env that lacked it'
pass 'a .env without IMAGE_TAG gets the line appended, not lost'

# --- A failed migration must be a NO-OP for the running site --------------------------------------

CASE='migration fails'
stack="$(new_stack migfail)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_MIGRATE_FAIL=1
[ "$LAST_STATUS" -ne 0 ] || fail 'a failed migration must fail the roll'
called 'up -d --remove-orphans' \
    && fail 'up -d ran after a FAILED migration — the old app containers would have been destroyed'
[ "$(env_tag "$stack")" = 'sha-0000000' ] \
    || fail '.env was moved to the new tag although the roll failed' "$(env_tag "$stack")"
grep -q 'PREVIOUS_IMAGE_TAG=sha-0000000' <<<"$LAST_OUTPUT" || fail 'CD was not told what to roll back to'
pass 'a failed migration never reaches up -d: the previous release keeps serving and .env still names it'

# --- The supply-chain assertion: attested digests are the ones that actually run -------------------

CASE='digest mismatch'
stack="$(new_stack mismatch)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 EXPECTED_DIGESTS="$(expected_digests "$GOOD_DIGEST")" \
    STUB_DIGEST="$OTHER_DIGEST"
[ "$LAST_STATUS" -ne 0 ] || fail 'a pulled image that is NOT the attested digest must fail the roll'
called 'run --rm --no-deps migrator' && fail 'the migration ran on an unattested image'
called 'up -d --remove-orphans' && fail 'the roll continued after a digest mismatch'
grep -q 'Digest mismatch' <<<"$LAST_OUTPUT" || fail 'the mismatch was not reported'
pass 'a tag re-pointed after CD attested it aborts the roll before migrating or starting anything'

CASE='no digests supplied'
stack="$(new_stack nodigest)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$OTHER_DIGEST"
[ "$LAST_STATUS" -eq 0 ] || fail 'a hand-run deploy (no EXPECTED_DIGESTS) should skip the assertion' "status $LAST_STATUS"
called 'up -d --remove-orphans' || fail 'a hand-run deploy should still roll'
pass 'a hand-run deploy without EXPECTED_DIGESTS skips the assertion and still rolls'

# --- Every other abort must also leave the running site alone --------------------------------------

CASE='broken Caddyfile'
stack="$(new_stack caddybad)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_CADDY_INVALID=1
[ "$LAST_STATUS" -ne 0 ] || fail 'an invalid Caddyfile must fail the roll'
called 'pull' && fail 'a broken Caddyfile should abort before anything is pulled'
called 'up -d --remove-orphans' && fail 'containers were rolled with a broken Caddyfile'
[ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env moved although the roll failed'
pass 'an invalid Caddyfile aborts up front — the running Caddy keeps serving its current config'

CASE='pull fails'
stack="$(new_stack pullfail)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_PULL_FAIL=1
[ "$LAST_STATUS" -ne 0 ] || fail 'a failed pull must fail the script'
called 'run --rm --no-deps migrator' && fail 'the migration ran although the pull failed'
called 'up -d --remove-orphans' && fail 'containers were rolled although the pull failed'
[ "$(env_tag "$stack")" = 'sha-0000000' ] || fail '.env moved although the roll failed'
pass 'a failed pull aborts before any container is touched and .env still names the live tag'

CASE='up fails'
stack="$(new_stack upfail)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_UP_FAIL=1
[ "$LAST_STATUS" -ne 0 ] || fail 'a failed up -d must fail the script'
[ "$(env_tag "$stack")" = 'sha-0000000' ] \
    || fail '.env must still name the previous tag when the roll failed' "$(env_tag "$stack")"
grep -q 'PREVIOUS_IMAGE_TAG=sha-0000000' <<<"$LAST_OUTPUT" || fail 'CD was not told what to roll back to'
grep -q 'Deploy FAILED' <<<"$LAST_OUTPUT" || fail 'the failure banner (with container logs) was not printed'
called 'logs' || fail 'container logs were not dumped on failure'
pass 'a failed up -d exits non-zero, dumps logs, leaves .env on the live tag, and names the rollback target'

# --- The parked e-mail expiry policy (ADR-0036 §6, #1085) ------------------------------------------

AUTH_TOPOLOGY="$REPO_ROOT/src/AuthSystem/LotroKoniecDev.AuthSystem.Infrastructure/Messaging/RabbitMqTopology.cs"
TMS_TOPOLOGY="$REPO_ROOT/src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Messaging/AccountEventsTopology.cs"
# The owner's decision on #1085, word for word: only emails.send.dlq, 604800000 ms, as a policy. The CLI
# runs as the `rabbitmq` user (see deploy.sh for the Erlang cookie trap a root CLI sets off).
POLICY_CALL='exec -T --user rabbitmq rabbitmq rabbitmqctl set_operator_policy --apply-to queues emails-send-dlq-ttl ^emails\.send\.dlq$ {"message-ttl":604800000}'

# Every queue name the apps declare: the `const string …Queue = "…"` lines of both topology files.
declared_queues() {
    sed -n 's/.*const string [A-Za-z]*Queue = "\([^"]*\)";.*/\1/p' "$AUTH_TOPOLOGY" "$TMS_TOPOLOGY"
}
queue_constant() {
    sed -n "s/.*const string $1 = \"\([^\"]*\)\";.*/\1/p" "$AUTH_TOPOLOGY" "$TMS_TOPOLOGY"
}

CASE='parked e-mail expiry'
stack="$(new_stack policy)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST"
[ "$LAST_STATUS" -eq 0 ] || fail 'a clean roll should exit 0' "status $LAST_STATUS"
called "$POLICY_CALL" || fail 'the 7-day expiry policy was not applied to emails.send.dlq as the owner decided'
[ "$(policy_tries)" -eq 1 ] || fail 'a broker that answers needs exactly one try' "tries: $(policy_tries)"
# The try runs under `timeout -k 5 20`: the line right before the policy call.
[ "$(call_line 'timeout -k 5 20')" -eq $(($(call_line 'set_operator_policy') - 1)) ] \
    || fail 'the policy try must run under a 20 s time limit with a KILL 5 s later'
called 'read-stdin' && fail 'the policy call read the stdin of deploy.sh (SIGTTIN in a hand-run deploy)'
grep -q '::warning::' <<<"$LAST_OUTPUT" && fail 'a policy that landed must not print a warning'
pass 'every deploy sets the 7-day expiry policy on emails.send.dlq, as the rabbitmq user'

CASE='policy after up -d'
# `up -d` is what creates a new broker, so a policy set before it would miss a fresh volume.
policy_at="$(call_line 'set_operator_policy')"
up_at="$(call_line 'up -d --remove-orphans')"
[ -n "$policy_at" ] && [ -n "$up_at" ] || fail 'expected both up -d and the policy step to run'
[ "$policy_at" -gt "$up_at" ] \
    || fail 'the policy must be set AFTER up -d, which creates a new broker' "policy at call #$policy_at, up -d at call #$up_at"
pass 'the policy is set after up -d, so a broker that up -d just created gets it too'

CASE='policy reach'
# The policy must reach the e-mail parking lot and nothing else the apps declare: never the work queue
# emails.send (a waiting e-mail would be dropped), and never tms.account-erased.dlq (erasure work waits
# for a person, #1085). The names come from the C# constants, so a renamed queue fails here instead of
# leaving the policy on nothing. RabbitMQ reads the pattern as a regular expression; this one uses only
# syntax that grep -E reads the same way.
pattern="$(sed -n "s/^EMAIL_DLQ_EXPIRY_PATTERN='\(.*\)'$/\1/p" "$DEPLOY_SH")"
[ -n "$pattern" ] || fail 'could not read EMAIL_DLQ_EXPIRY_PATTERN from deploy.sh'
queues="$(declared_queues)"
email_dlq="$(queue_constant EmailDeadLetterQueue)"
erased_dlq="$(queue_constant ErasedDeadLetterQueue)"
[ -n "$email_dlq" ] && [ -n "$erased_dlq" ] \
    || fail 'could not read EmailDeadLetterQueue and ErasedDeadLetterQueue from the topology files' "$queues"
[ "$(printf '%s\n' "$queues" | grep -c .)" -ge 4 ] \
    || fail 'expected at least the four queues of both topology files' "$queues"
matched="$(printf '%s\n' "$queues" | grep -E -- "$pattern" || true)"
[ "$matched" = "$email_dlq" ] \
    || fail "the policy must match ${email_dlq} and nothing else" "matched: ${matched:-<nothing>}; declared: $(printf '%s\n' "$queues" | tr '\n' ' ')"
pass "the policy matches ${email_dlq} only, not ${erased_dlq} or any other declared queue"

CASE='broker still starting'
stack="$(new_stack booting)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_BROKER_BOOTING_TRIES=2
[ "$LAST_STATUS" -eq 0 ] || fail 'a broker that answers on a later try is a clean roll' "status $LAST_STATUS"
[ "$(policy_tries)" -eq 3 ] || fail 'expected two failed tries and a third that lands' "tries: $(policy_tries)"
grep -q '::warning::' <<<"$LAST_OUTPUT" && fail 'a policy that landed on a later try must not print a warning'
pass 'a broker that is still starting is tried again until it answers'

CASE='broker restarted'
# 137 is also what a try returns when the broker container restarts under it. That is not a stuck
# broker, so the step must try again rather than give up.
stack="$(new_stack restarted)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_BROKER_RESTARTED=1
[ "$LAST_STATUS" -eq 0 ] || fail 'a broker that restarted once is a clean roll' "status $LAST_STATUS"
[ "$(policy_tries)" -eq 2 ] || fail 'expected the killed try and one more that lands' "tries: $(policy_tries)"
grep -q '::warning::' <<<"$LAST_OUTPUT" && fail 'a policy that landed on the next try must not print a warning'
pass 'a try killed with a restarting broker (137) is tried again, not taken for a time-out'

CASE='broker never answers'
stack="$(new_stack brokerdown)"
# A wait of 0 s ends the step after the first refused try. A real wait would make this case slow.
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_BROKER_DOWN=1 BROKER_POLICY_WAIT_SECONDS=0
[ "$LAST_STATUS" -eq 0 ] || fail 'a broker outage must never block an app deploy' "status $LAST_STATUS"
[ "$(policy_tries)" -eq 1 ] || fail 'a wait that has run out must end the tries' "tries: $(policy_tries)"
grep -q '::warning::.*emails-send-dlq-ttl' <<<"$LAST_OUTPUT" || fail 'the missing policy was not reported as a warning'
grep -q 'unable to perform an operation' <<<"$LAST_OUTPUT" || fail "the broker's own error was not shown"
[ "$(env_tag "$stack")" = 'sha-abc1234' ] || fail 'the apps rolled, so the new tag must still be pinned' "$(env_tag "$stack")"
grep -q 'Deploy FAILED' <<<"$LAST_OUTPUT" && fail 'the deploy did not fail, so no failure banner'
pass 'a broker that never answers prints a warning, and the deploy still pins the new tag and exits 0'

CASE='broker stuck'
# A try that runs out of time ends the step at once: 36 stuck tries would hold the deploy for minutes,
# and a CD job that hits its own time limit is cancelled with no rollback.
stack="$(new_stack brokerstuck)"
run_deploy "$stack" IMAGE_TAG=sha-abc1234 STUB_DIGEST="$GOOD_DIGEST" STUB_BROKER_HANGS=1
[ "$LAST_STATUS" -eq 0 ] || fail 'a stuck broker must never block an app deploy' "status $LAST_STATUS"
[ "$(policy_tries)" -eq 1 ] || fail 'a try that ran out of time must be the last one' "tries: $(policy_tries)"
grep -q 'did not answer within 20 s' <<<"$LAST_OUTPUT" || fail 'the time-out was not named'
grep -q '::warning::.*emails-send-dlq-ttl' <<<"$LAST_OUTPUT" || fail 'the missing policy was not reported as a warning'
[ "$(env_tag "$stack")" = 'sha-abc1234' ] || fail 'the apps rolled, so the new tag must still be pinned' "$(env_tag "$stack")"
pass 'a broker that holds the call past the time limit stops the step after one try, with a warning'

printf '\nAll %d hetzner deploy.sh case(s) passed.\n' "$cases"
