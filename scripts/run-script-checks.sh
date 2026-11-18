#!/usr/bin/env bash
# Runs the repo's bash checks: the scripts/check-*.sh guards, scripts/claude/verify-copy.sh and the
# scripts/tests/*.tests.sh self-tests (#1473).
#
# On Linux and macOS it runs each script directly, in the caller's shell. On Windows (Git Bash) it
# runs them all in one Linux container set up like CI's runner. Git Bash is many times slower, and
# it copies a file when a script asks for a symbolic link, so some self-tests fail although nothing
# is wrong. The container runs as a non-root user, because CI does, and some self-tests run other
# cases as root.
#
#   scripts/run-script-checks.sh <script path from the repo root>...
#
# Exit code: 0 when every script passed, otherwise the highest exit code of any script. 2 when it
# cannot run them (a bad argument, no Docker on Windows). Never a silent fallback to Git Bash.
set -euo pipefail

PIN_FILE='src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Dockerfile'
IMAGE_REPOSITORY='lotrokoniecdev-script-checks'
IMAGE_LABEL='lotrokoniecdev.script-checks'
# The SDK image's own non-root user (`app`).
CONTAINER_USER='1654:1654'
DOCKER_INFO_TIMEOUT_SECONDS=30
OLD_IMAGE_AGE='168h'

PIN=''
IMAGE_TAG=''
GITFILE=''
REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)"

die() {
    printf 'run-script-checks: %s\n' "$1" >&2
    exit 2
}

usage() {
    printf 'usage: %s <script path from the repo root>...\n' "$(basename -- "$0")" >&2
    exit 2
}

check_arguments() {
    [ "$#" -gt 0 ] || usage
    local path
    for path in "$@"; do
        case "$path" in
            '' | /* | [A-Za-z]:*) die "'$path' is not a path from the repo root" ;;
            *\\*) die "'$path' has a backslash; write paths with /" ;;
            .. | ../* | */.. | */../*) die "'$path' goes outside the repo" ;;
        esac
        [ -f "$REPO_ROOT/$path" ] || die "'$path' is not a file in the repo"
    done
}

# Git Bash and MSYS2 name the shell in front: MINGW64_NT-…, MSYS_NT-…, UCRT64_NT-…, CLANG64_NT-….
is_windows() {
    case "$(uname -s)" in
        MINGW* | MSYS* | CYGWIN* | *_NT-*) return 0 ;;
        *) return 1 ;;
    esac
}

run_directly() {
    cd "$REPO_ROOT"
    local path code started highest=0 summary='' line
    local run_started=$SECONDS
    for path in "$@"; do
        started=$SECONDS
        code=0
        # CI gives a step no input; a script that waits for some must not hang here.
        "./$path" < /dev/null || code=$?
        line="== $path: exit $code in $((SECONDS - started))s"
        printf '%s\n' "$line"
        summary="$summary$line"$'\n'
        if [ "$code" -gt "$highest" ]; then
            highest=$code
        fi
    done
    printf '\n%s== %d script(s) in %ds, highest exit %d\n' \
        "$summary" "$#" "$((SECONDS - run_started))" "$highest"
    return "$highest"
}

# Runs a command with MSYS path conversion off, so docker.exe gets /repo and the other container
# paths as written. Git for Windows reads the first variable, MSYS2 only the second.
without_path_conversion() {
    MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' "$@"
}

check_docker() {
    command -v docker > /dev/null 2>&1 \
        || die 'Docker is needed on Windows: install Docker Desktop and start it. The scripts never fall back to Git Bash.'
    command -v cygpath > /dev/null 2>&1 \
        || die 'cygpath is missing: run this from Git Bash.'

    # Only GNU timeout: Windows' own timeout.exe can come first on PATH, and it takes other arguments.
    local os_type status=0 limit=()
    if command -v timeout > /dev/null 2>&1 && timeout --version < /dev/null > /dev/null 2>&1; then
        limit=(timeout "$DOCKER_INFO_TIMEOUT_SECONDS")
    fi
    os_type="$(without_path_conversion ${limit[@]+"${limit[@]}"} \
        docker info --format '{{.OSType}}' 2> /dev/null)" || status=$?
    if [ "$status" -eq 124 ]; then
        die "Docker did not answer within ${DOCKER_INFO_TIMEOUT_SECONDS}s. Is Docker Desktop still starting?"
    fi
    [ "$status" -eq 0 ] || die 'Docker is not running: start Docker Desktop.'
    [ "$os_type" = linux ] \
        || die "Docker runs '$os_type' containers: switch Docker Desktop to Linux containers."
}

# Sets PIN to the SDK image reference, digest included.
read_pin() {
    local line
    line="$(LC_ALL=C grep -m1 -oE \
        '^FROM mcr\.microsoft\.com/dotnet/sdk:[^@ ]+@sha256:[0-9a-f]{64}' \
        "$REPO_ROOT/$PIN_FILE" 2> /dev/null)" \
        || die "found no digest-pinned SDK image (FROM mcr.microsoft.com/dotnet/sdk:…@sha256:…) in $PIN_FILE"
    PIN="${line#FROM }"
}

# CI's runner has jq and an en_US.UTF-8 locale; the SDK image has neither. Without them one
# self-test goes red and another skips a case. The last two commands fail the build instead.
# CI also runs in a UTF-8 locale, and a guard that forgets `export LC_ALL=C` fails only there
# (#1162); without LANG the container would run in the POSIX locale and pass it.
print_recipe() {
    printf 'FROM %s\nLABEL %s=true\n' "$1" "$IMAGE_LABEL"
    cat << 'RECIPE'
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends jq locales \
    && sed -i 's/^# *en_US.UTF-8 UTF-8/en_US.UTF-8 UTF-8/' /etc/locale.gen \
    && locale-gen \
    && rm -rf /var/lib/apt/lists/* \
    && jq --version \
    && locale -a | grep -qx 'en_US.utf8'
ENV LANG=C.UTF-8
RECIPE
}

# Sets IMAGE_TAG, and builds the check image first when it is not there. The tag comes from the
# recipe, so a new SDK pin (Dependabot moves it in PIN_FILE) builds a new image.
ensure_image() {
    local recipe hash build_log
    read_pin
    recipe="$(print_recipe "$PIN")"
    hash="$(git hash-object --stdin <<< "$recipe")" || die 'git could not hash the image recipe'
    IMAGE_TAG="$IMAGE_REPOSITORY:${hash:0:16}"

    if ! without_path_conversion docker image inspect "$IMAGE_TAG" > /dev/null 2>&1; then
        # Old check images go by age, not as "every other tag": a second checkout on an older pin
        # may still use its image, and Docker never removes an image a container uses. This runs
        # before the build, because a build from cached layers can keep an old creation time.
        without_path_conversion docker image prune --all --force \
            --filter "label=$IMAGE_LABEL" --filter "until=$OLD_IMAGE_AGE" > /dev/null 2>&1 || true
        printf 'Building the check image %s. Only the first run does this, and it needs network.\n' \
            "$IMAGE_TAG" >&2
        if ! build_log="$(without_path_conversion docker build --load --tag "$IMAGE_TAG" - 2>&1 <<< "$recipe")"; then
            printf '%s\n' "$build_log" >&2
            die "could not build the check image $IMAGE_TAG (the build log is above)"
        fi
    fi
}

# Prints the path in the C:/… form docker.exe reads. A /tmp/… path would mount an empty folder.
windows_path() {
    cygpath -m "$1" || die "cygpath could not convert $1"
}

run_in_container() {
    cd "$REPO_ROOT"

    # A mounted file always looks executable in the container, and Git for Windows adds a new file
    # without the bit. CI then fails with 126, so give the same result here. Windows finds a file
    # whatever the letter case, so git must look the path up the same way.
    local path mode highest=0 to_run=() not_executable=''
    for path in "$@"; do
        mode="$(git ls-files -s -- ":(icase,literal)$path")" \
            || die "git could not read the index of $REPO_ROOT"
        mode="${mode%% *}"
        if [ -z "$mode" ]; then
            printf 'note: %s is not in git, so its executable bit is not checked\n' "$path" >&2
            to_run+=("$path")
        elif [ "$mode" != 100755 ]; then
            not_executable="$not_executable== $path: exit 126 (git mode $mode; fix: git update-index --chmod=+x $path)"$'\n'
            highest=126
        else
            to_run+=("$path")
        fi
    done
    printf '%s' "$not_executable"

    local status=0
    if [ "${#to_run[@]}" -gt 0 ]; then
        local mounts root_mount git_dir common_dir git_mount gitfile_mount
        check_docker
        ensure_image
        root_mount="$(windows_path "$REPO_ROOT")"
        mounts=(-v "$root_mount:/repo:ro")

        # A worktree's .git is a file that names a Windows path, which git in the container cannot
        # read. Mount the shared .git and put a file that points into it over /repo/.git. Exported
        # GIT_DIR would reach the self-tests' own scratch repos instead (#1160).
        if [ -f "$REPO_ROOT/.git" ]; then
            local inside
            git_dir="$(git rev-parse --path-format=absolute --git-dir)" \
                || die "git could not find the git folder of $REPO_ROOT"
            common_dir="$(git rev-parse --path-format=absolute --git-common-dir)" \
                || die "git could not find the shared git folder of $REPO_ROOT"
            case "$git_dir" in
                "$common_dir") inside='' ;;
                "$common_dir"/*) inside="/${git_dir#"$common_dir"/}" ;;
                *) die "the git folder $git_dir is not inside $common_dir" ;;
            esac
            GITFILE="$(mktemp)"
            trap 'rm -f "$GITFILE"' EXIT
            printf 'gitdir: /git%s\n' "$inside" > "$GITFILE"
            git_mount="$(windows_path "$common_dir")"
            gitfile_mount="$(windows_path "$GITFILE")"
            mounts+=(-v "$git_mount:/git:ro" -v "$gitfile_mount:/repo/.git:ro")
        fi

        without_path_conversion docker run --rm --init --pull never --user "$CONTAINER_USER" -w /repo \
            "${mounts[@]}" \
            -e GIT_CONFIG_COUNT=1 -e GIT_CONFIG_KEY_0=safe.directory -e GIT_CONFIG_VALUE_0='*' \
            "$IMAGE_TAG" bash /repo/scripts/run-script-checks.sh "${to_run[@]}" || status=$?
        if [ "$status" -eq 125 ]; then
            printf 'run-script-checks: docker could not start the container (exit 125)\n' >&2
            status=2
        fi
    fi

    if [ "$status" -gt "$highest" ]; then
        highest=$status
    fi
    # The container's own summary does not know about the scripts it never got.
    if [ -n "$not_executable" ] && [ "${#to_run[@]}" -gt 0 ]; then
        printf '%s== highest exit %d, with the scripts git does not mark executable\n' \
            "$not_executable" "$highest"
    fi
    return "$highest"
}

check_arguments "$@"
if is_windows; then
    run_in_container "$@"
else
    run_directly "$@"
fi
