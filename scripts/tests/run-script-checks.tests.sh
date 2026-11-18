#!/usr/bin/env bash
# Test suite for scripts/run-script-checks.sh (#1473), the runner that sends the bash checks to a
# Linux container on Windows and runs them directly everywhere else.
#
# Every case runs a copy of the runner inside a scratch git repo. STUB `uname`, `docker` and
# `cygpath` come first on PATH, so the Windows leg runs on Linux too, and no case starts a real
# container. CI runs this in the `guards` job, next to the other bash gates.

set -euo pipefail
export LC_ALL=C

SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
RUNNER="$SCRIPTS_DIR/run-script-checks.sh"
REAL_PIN_FILE="$SCRIPTS_DIR/../src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Dockerfile"
PIN_PATH='src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Dockerfile'
FAKE_PIN="mcr.microsoft.com/dotnet/sdk:10.0@sha256:$(printf '%064d' 7)"
WINDOWS='MINGW64_NT-10.0-26300'

# Two steps on purpose: `cd ""` succeeds, so a failed mktemp inside one $(cd …) would point the
# cleanup below at the current directory.
TMP_ROOT="$(mktemp -d)" || exit 1
TMP_ROOT="$(cd -- "$TMP_ROOT" && pwd -P)" || exit 1
[ -d "$TMP_ROOT" ] || exit 1
trap 'rm -rf "$TMP_ROOT"' EXIT

# Git exports GIT_DIR and the like to a hook. Left in place, they would point every scratch repo
# below at the real one.
for var in $(git rev-parse --local-env-vars); do
    unset "$var"
done
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=run-script-checks-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=run-script-checks-tests GIT_COMMITTER_EMAIL=tests@localhost

cases=0
CASE=""
LAST_STATUS=0
OUT="$TMP_ROOT/stdout"
ERR="$TMP_ROOT/stderr"
CALLS="$TMP_ROOT/calls"
BUILD_STDIN="$TMP_ROOT/build-stdin"
GITFILE_COPY="$TMP_ROOT/gitfile-copy"
GITFILE_SOURCE="$TMP_ROOT/gitfile-source"

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    printf '  --- stdout ---\n%s\n  --- stderr ---\n%s\n  --- calls ---\n%s\n' \
        "$(cat "$OUT")" "$(cat "$ERR")" "$(cat "$CALLS" 2> /dev/null)" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Stubs --------------------------------------------------------------------------------------
# STUB_UNAME          — what `uname -s` prints (default: Linux)
# STUB_INFO_STATUS    — `docker info` exits with this (default: 0, and prints STUB_OS_TYPE)
# STUB_OS_TYPE        — the OSType `docker info` prints (default: linux)
# STUB_IMAGE_EXISTS   — 1: `docker image inspect` finds the image
# STUB_BUILD_FAIL     — 1: `docker build` fails
# STUB_PRUNE_FAIL     — 1: `docker image prune` fails
# STUB_RUN_STATUS     — `docker run` exits with this (default: 0)
# Each docker call is one line in STUB_CALLS: the value of MSYS_NO_PATHCONV, then every argument
# in brackets. cygpath marks what it converted with `M:`, as a drive letter would.
BIN="$TMP_ROOT/bin"
mkdir -p "$BIN"
cat > "$BIN/uname" << 'STUB'
#!/usr/bin/env bash
printf '%s\n' "${STUB_UNAME:-Linux}"
STUB
cat > "$BIN/cygpath" << 'STUB'
#!/usr/bin/env bash
[ "$#" -eq 2 ] && [ "$1" = -m ] || { echo "stub cygpath: unexpected call '$*'" >&2; exit 1; }
printf 'M:%s\n' "$2"
STUB
cat > "$BIN/docker" << 'STUB'
#!/usr/bin/env bash
{
    printf 'docker(MSYS_NO_PATHCONV=%s)' "${MSYS_NO_PATHCONV-unset}"
    printf ' [%s]' "$@"
    printf '\n'
} >> "$STUB_CALLS"
case "$1" in
    info)
        status="${STUB_INFO_STATUS:-0}"
        [ "$status" -eq 0 ] || exit "$status"
        printf '%s\n' "${STUB_OS_TYPE:-linux}"
        ;;
    image)
        case "$2" in
            inspect) [ "${STUB_IMAGE_EXISTS:-0}" = 1 ] ;;
            prune) [ "${STUB_PRUNE_FAIL:-0}" != 1 ] ;;
            *) echo "stub docker: unexpected call '$*'" >&2; exit 1 ;;
        esac
        ;;
    build)
        cat > "$STUB_BUILD_STDIN"
        echo 'stub build log line'
        [ "${STUB_BUILD_FAIL:-0}" != 1 ]
        ;;
    run)
        for arg in "$@"; do
            case "$arg" in
                *:/repo/.git:ro)
                    source="${arg%:/repo/.git:ro}"
                    source="${source#M:}"
                    printf '%s\n' "$source" > "$STUB_GITFILE_SOURCE"
                    cp "$source" "$STUB_GITFILE_COPY"
                    ;;
            esac
        done
        exit "${STUB_RUN_STATUS:-0}"
        ;;
    *)
        echo "stub docker: unexpected call '$*'" >&2
        exit 1
        ;;
esac
STUB
chmod +x "$BIN/uname" "$BIN/cygpath" "$BIN/docker"

# A PATH with only what the runner needs before its Docker checks, so a real docker or cygpath on
# the machine cannot answer for a missing one.
link_tools() {
    local dir="$1" tool
    shift
    mkdir -p "$dir"
    for tool in bash dirname basename git "$@"; do
        ln -s "$(command -v "$tool")" "$dir/$tool"
    done
    cp "$BIN/uname" "$dir/uname"
}
NO_DOCKER_BIN="$TMP_ROOT/no-docker-bin"
link_tools "$NO_DOCKER_BIN"
NO_CYGPATH_BIN="$TMP_ROOT/no-cygpath-bin"
link_tools "$NO_CYGPATH_BIN"
cp "$BIN/docker" "$NO_CYGPATH_BIN/docker"

# --- Scratch repos ------------------------------------------------------------------------------
# make_repo <name> [pin file content] — prints the path of a new committed repo with the runner, a
# pin file and these scripts: ok.sh (exit 0), red.sh (exit 1), three.sh (exit 3) and stdin.sh
# (exit 9 when it can read a line).
make_repo() {
    local repo="$TMP_ROOT/$1"
    [ ! -e "$repo" ] || { echo "make_repo: $repo exists" >&2; exit 1; }
    mkdir -p "$repo/scripts" "$(dirname "$repo/$PIN_PATH")"
    cp "$RUNNER" "$repo/scripts/run-script-checks.sh"
    printf '%s\n' "${2-FROM $FAKE_PIN AS build}" > "$repo/$PIN_PATH"
    printf '#!/usr/bin/env bash\necho ok ran\n' > "$repo/scripts/ok.sh"
    printf '#!/usr/bin/env bash\necho red ran\nexit 1\n' > "$repo/scripts/red.sh"
    printf '#!/usr/bin/env bash\necho three ran\nexit 3\n' > "$repo/scripts/three.sh"
    printf '#!/usr/bin/env bash\nif read -r line; then exit 9; fi\n' > "$repo/scripts/stdin.sh"
    chmod +x "$repo/scripts/"*.sh
    # It runs inside $(…), where `set -e` does not reach, so every step checks itself.
    git -C "$repo" init -q \
        && git -C "$repo" add . \
        && git -C "$repo" update-index --chmod=+x -- scripts/run-script-checks.sh scripts/ok.sh \
            scripts/red.sh scripts/three.sh scripts/stdin.sh \
        && git -C "$repo" commit -q -m init > /dev/null \
        || { echo "make_repo: git could not create $repo" >&2; exit 1; }
    printf '%s\n' "$repo"
}

# run_runner <repo> [VAR=value ...] -- <runner arguments ...>
# Runs the repo's copy of the runner by its path, from outside the repo, with no input.
run_runner() {
    local repo="$1" envs=()
    shift
    while [ "$#" -gt 0 ] && [ "$1" != -- ]; do
        envs+=("$1")
        shift
    done
    shift
    : > "$CALLS"
    rm -f "$BUILD_STDIN" "$GITFILE_COPY" "$GITFILE_SOURCE"
    LAST_STATUS=0
    (
        cd "$TMP_ROOT"
        env PATH="$BIN:$PATH" STUB_CALLS="$CALLS" STUB_BUILD_STDIN="$BUILD_STDIN" \
            STUB_GITFILE_COPY="$GITFILE_COPY" STUB_GITFILE_SOURCE="$GITFILE_SOURCE" \
            ${envs[@]+"${envs[@]}"} \
            "$repo/scripts/run-script-checks.sh" "$@" < /dev/null
    ) > "$OUT" 2> "$ERR" || LAST_STATUS=$?
}

expect_status() {
    [ "$LAST_STATUS" -eq "$1" ] || fail "expected exit $1, got $LAST_STATUS"
}

expect_out() {
    grep -qF -- "$1" "$OUT" || fail "stdout lacks: $1"
}

expect_err() {
    grep -qF -- "$1" "$ERR" || fail "stderr lacks: $1"
}

expect_call() {
    grep -qF -- "$1" "$CALLS" || fail "no docker call contains: $1"
}

expect_no_call() {
    ! grep -qF -- "$1" "$CALLS" || fail "a docker call contains: $1"
}

run_call() {
    grep -F -- '[run]' "$CALLS" || true
}

# --- Direct leg ---------------------------------------------------------------------------------
REPO="$(make_repo main)"

CASE='direct: one green script'
run_runner "$REPO" -- scripts/ok.sh
expect_status 0
expect_out 'ok ran'
expect_out '== scripts/ok.sh: exit 0 in '
expect_out '== 1 script(s) in '
[ ! -s "$CALLS" ] || fail 'Linux called docker'
pass 'exit 0, the script ran, no docker call'

CASE='direct: a red script stays red'
run_runner "$REPO" -- scripts/red.sh
expect_status 1
expect_out '== scripts/red.sh: exit 1 in '
pass 'exit 1'

CASE='direct: every script runs, the highest exit wins'
run_runner "$REPO" -- scripts/red.sh scripts/three.sh scripts/ok.sh
expect_status 3
for name in red three ok; do
    expect_out "$name ran"
done
expect_out '== scripts/three.sh: exit 3 in '
expect_out 'highest exit 3'
pass 'all three ran in order, exit 3'

CASE='direct: a script gets no input, as in CI'
LAST_STATUS=0
(
    cd "$TMP_ROOT"
    echo 'a line that must not reach the script' \
        | env PATH="$BIN:$PATH" "$REPO/scripts/run-script-checks.sh" scripts/stdin.sh
) > "$OUT" 2> "$ERR" || LAST_STATUS=$?
expect_status 0
pass 'the script read nothing'

CASE='direct: macOS runs directly too'
run_runner "$REPO" STUB_UNAME=Darwin -- scripts/ok.sh
expect_status 0
expect_out 'ok ran'
[ ! -s "$CALLS" ] || fail 'macOS called docker'
pass 'no docker call'

CASE='direct: a script that is not executable fails as in CI'
NOT_EXECUTABLE_REPO="$(make_repo not-executable)"
chmod -x "$NOT_EXECUTABLE_REPO/scripts/ok.sh"
run_runner "$NOT_EXECUTABLE_REPO" -- scripts/ok.sh
expect_status 126
pass 'exit 126'

# --- Arguments ----------------------------------------------------------------------------------
for bad in '' '/etc/passwd' 'C:/repo/scripts/ok.sh' 'scripts\ok.sh' '../outside.sh' \
    'scripts/../scripts/ok.sh' 'scripts/missing.sh' 'scripts'; do
    CASE="arguments: '$bad' is refused"
    run_runner "$REPO" STUB_UNAME="$WINDOWS" -- scripts/ok.sh "$bad"
    expect_status 2
    [ ! -s "$CALLS" ] || fail 'docker was called for a bad argument'
    ! grep -qF 'ok ran' "$OUT" || fail 'a script ran although an argument was bad'
    pass 'exit 2 before anything runs'
done

CASE='arguments: none'
run_runner "$REPO" --
expect_status 2
expect_err 'usage:'
pass 'exit 2 with usage'

# --- Windows leg: Docker must be there ----------------------------------------------------------
CASE='windows: no docker'
run_runner "$REPO" PATH="$NO_DOCKER_BIN" STUB_UNAME="$WINDOWS" -- scripts/ok.sh
expect_status 2
expect_err 'Docker is needed on Windows'
pass 'exit 2, never Git Bash'

CASE='windows: no cygpath'
run_runner "$REPO" PATH="$NO_CYGPATH_BIN" STUB_UNAME="$WINDOWS" -- scripts/ok.sh
expect_status 2
expect_err 'cygpath is missing'
pass 'exit 2'

CASE='windows: Docker is not running'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_INFO_STATUS=1 -- scripts/ok.sh
expect_status 2
expect_err 'Docker is not running'
expect_no_call '[run]'
pass 'exit 2, no container'

CASE='windows: Docker does not answer in time'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_INFO_STATUS=124 -- scripts/ok.sh
expect_status 2
expect_err 'did not answer within 30s'
pass 'exit 2 with the time-out message'

CASE='windows: Docker runs Windows containers'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_OS_TYPE=windows -- scripts/ok.sh
expect_status 2
expect_err "Docker runs 'windows' containers"
expect_no_call '[run]'
pass 'exit 2, no container'

# --- Windows leg: the image ---------------------------------------------------------------------
CASE='windows: the image is built from the pinned SDK image'
run_runner "$REPO" STUB_UNAME="$WINDOWS" -- scripts/ok.sh
expect_status 0
expect_call 'docker(MSYS_NO_PATHCONV=1) [info] [--format] [{{.OSType}}]'
expect_call "docker(MSYS_NO_PATHCONV=1) [build] [--load] [--tag]"
[ "$(head -1 "$BUILD_STDIN")" = "FROM $FAKE_PIN" ] || fail "the recipe does not start with FROM $FAKE_PIN"
for needed in 'LABEL lotrokoniecdev.script-checks=true' 'install -y --no-install-recommends jq locales' \
    'en_US.UTF-8 UTF-8' 'locale-gen' "grep -qx 'en_US.utf8'" 'ENV LANG=C.UTF-8'; do
    grep -qF -- "$needed" "$BUILD_STDIN" || fail "the recipe lacks: $needed"
done
! grep -qF 'stub build log line' "$OUT" "$ERR" || fail 'a good build printed its log'
pass 'FROM the pin, plus jq and the en_US.UTF-8 locale, quiet when it works'

CASE='windows: the tag comes from the recipe'
expected_tag="lotrokoniecdev-script-checks:$(git hash-object --stdin < "$BUILD_STDIN" | cut -c1-16)"
expect_call "[--tag] [$expected_tag] [-]"
expect_call "[$expected_tag] [bash] [/repo/scripts/run-script-checks.sh] [scripts/ok.sh]"
pass "$expected_tag"

CASE='windows: a new image removes only old labelled ones'
expect_call 'docker(MSYS_NO_PATHCONV=1) [image] [prune] [--all] [--force] [--filter] [label=lotrokoniecdev.script-checks] [--filter] [until=168h]'
[ "$(grep -cF '[prune]' "$CALLS")" -eq 1 ] || fail 'prune ran more than once'
pass 'label and age filters'

CASE='windows: a failed prune changes nothing'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_PRUNE_FAIL=1 -- scripts/ok.sh
expect_status 0
expect_call '[run]'
pass 'exit 0, the scripts still ran'

CASE='windows: an existing image is used as it is'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_IMAGE_EXISTS=1 -- scripts/ok.sh
expect_status 0
expect_call "[image] [inspect] [$expected_tag]"
expect_no_call '[build]'
expect_no_call '[prune]'
expect_call '[run]'
pass 'no build, no prune'

CASE='windows: a failed build'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_BUILD_FAIL=1 -- scripts/ok.sh
expect_status 2
expect_err 'stub build log line'
expect_err 'could not build the check image'
expect_no_call '[run]'
expect_no_call '[prune]'
pass 'exit 2 with the build log, no container'

CASE='windows: no pin file'
NO_PIN_REPO="$(make_repo no-pin)"
rm "$NO_PIN_REPO/$PIN_PATH"
run_runner "$NO_PIN_REPO" STUB_UNAME="$WINDOWS" -- scripts/ok.sh
expect_status 2
expect_err "$PIN_PATH"
expect_no_call '[build]'
pass 'exit 2, names the file'

CASE='windows: a pin without a digest'
NO_DIGEST_REPO="$(make_repo no-digest 'FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build')"
run_runner "$NO_DIGEST_REPO" STUB_UNAME="$WINDOWS" -- scripts/ok.sh
expect_status 2
expect_err 'found no digest-pinned SDK image'
expect_no_call '[build]'
pass 'exit 2'

CASE="windows: the real $PIN_PATH still has a digest pin"
REAL_PIN_REPO="$(make_repo real-pin "$(cat "$REAL_PIN_FILE")")"
run_runner "$REAL_PIN_REPO" STUB_UNAME="$WINDOWS" -- scripts/ok.sh
expect_status 0
head -1 "$BUILD_STDIN" | grep -qE '^FROM mcr\.microsoft\.com/dotnet/sdk:[^@ ]+@sha256:[0-9a-f]{64}$' \
    || fail "the recipe starts with '$(head -1 "$BUILD_STDIN")'"
pass "$(head -1 "$BUILD_STDIN" | cut -c1-60)…"

# --- Windows leg: the container -----------------------------------------------------------------
CASE='windows: the container call'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_IMAGE_EXISTS=1 -- scripts/red.sh scripts/ok.sh
expect_status 0
call="$(run_call)"
[ "$(grep -c . <<< "$call")" -eq 1 ] || fail 'expected exactly one docker run'
for part in 'docker(MSYS_NO_PATHCONV=1) [run] [--rm] [--init] [--pull] [never] [--user] [1654:1654] [-w] [/repo]' \
    "[-v] [M:$REPO:/repo:ro]" \
    '[-e] [GIT_CONFIG_COUNT=1] [-e] [GIT_CONFIG_KEY_0=safe.directory] [-e] [GIT_CONFIG_VALUE_0=*]' \
    '[bash] [/repo/scripts/run-script-checks.sh] [scripts/red.sh] [scripts/ok.sh]'; do
    grep -qF -- "$part" <<< "$call" || fail "the run call lacks: $part"
done
! grep -qF 'GIT_DIR' <<< "$call" || fail 'the run call sets GIT_DIR'
! grep -qF 'GIT_WORK_TREE' <<< "$call" || fail 'the run call sets GIT_WORK_TREE'
! grep -qF '/git:' <<< "$call" || fail 'a main checkout got a /git mount'
mounts="$(grep -oE '\[-v\] \[[^]]*\]' <<< "$call")"
! grep -vqE ':ro\]$' <<< "$mounts" || fail "a mount is not read-only: $mounts"
pass 'one run, non-root, read-only, converted paths, no GIT_DIR, scripts in order'

CASE='windows: the container exit is the result'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_IMAGE_EXISTS=1 STUB_RUN_STATUS=3 -- scripts/ok.sh
expect_status 3
pass 'exit 3 passes through'

CASE='windows: docker cannot start the container'
run_runner "$REPO" STUB_UNAME="$WINDOWS" STUB_IMAGE_EXISTS=1 STUB_RUN_STATUS=125 -- scripts/ok.sh
expect_status 2
expect_err 'docker could not start the container (exit 125)'
pass 'exit 125 becomes 2'

CASE='windows: a script git does not mark executable fails as in CI'
MODE_REPO="$(make_repo git-mode)"
git -C "$MODE_REPO" update-index --chmod=-x -- scripts/red.sh
run_runner "$MODE_REPO" STUB_UNAME="$WINDOWS" STUB_IMAGE_EXISTS=1 -- scripts/red.sh scripts/ok.sh
expect_status 126
expect_out '== scripts/red.sh: exit 126 (git mode 100644; fix: git update-index --chmod=+x scripts/red.sh)'
call="$(run_call)"
grep -qF '[/repo/scripts/run-script-checks.sh] [scripts/ok.sh]' <<< "$call" || fail 'ok.sh did not run'
! grep -qF '[scripts/red.sh]' <<< "$call" || fail 'red.sh was sent to the container'
pass 'exit 126, the other script still ran'

CASE='windows: only scripts git does not mark executable'
run_runner "$MODE_REPO" STUB_UNAME="$WINDOWS" STUB_IMAGE_EXISTS=1 -- scripts/red.sh
expect_status 126
[ ! -s "$CALLS" ] || fail 'docker was called with nothing to run'
pass 'exit 126, no docker call'

CASE='windows: a script that is not in git'
UNTRACKED_REPO="$(make_repo untracked)"
printf '#!/usr/bin/env bash\nexit 0\n' > "$UNTRACKED_REPO/scripts/new.sh"
run_runner "$UNTRACKED_REPO" STUB_UNAME="$WINDOWS" STUB_IMAGE_EXISTS=1 -- scripts/new.sh
expect_status 0
expect_err 'note: scripts/new.sh is not in git'
expect_call '[scripts/new.sh]'
pass 'a note, and it runs'

CASE='windows: a worktree gets the shared .git and a file that points into it'
WORKTREE="$TMP_ROOT/worktree-a"
git -C "$REPO" worktree add -q --detach "$WORKTREE"
run_runner "$WORKTREE" STUB_UNAME="$WINDOWS" STUB_IMAGE_EXISTS=1 -- scripts/ok.sh
expect_status 0
call="$(run_call)"
grep -qF -- "[-v] [M:$REPO/.git:/git:ro]" <<< "$call" || fail 'no read-only /git mount of the shared .git'
grep -qF -- ':/repo/.git:ro]' <<< "$call" || fail 'no read-only file over /repo/.git'
[ "$(cat "$GITFILE_COPY")" = 'gitdir: /git/worktrees/worktree-a' ] \
    || fail "the file over /repo/.git says '$(cat "$GITFILE_COPY")'"
[ ! -e "$(cat "$GITFILE_SOURCE")" ] || fail 'the file over /repo/.git was left behind'
! grep -qF 'GIT_DIR' <<< "$call" || fail 'the run call sets GIT_DIR'
pass 'gitdir: /git/worktrees/worktree-a, removed after the run'

printf '\nAll %d run-script-checks case(s) passed.\n' "$cases"
