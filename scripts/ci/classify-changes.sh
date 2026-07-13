#!/usr/bin/env bash
#
# Decide WHICH gates a change must face. One classifier, three independent verdicts:
#
#   code=…    the .NET gate — restore + Release build (the repo-wide zero-warning gate)
#             + unit tests + integration tests (real PostgreSQL). Minutes.
#   guards=…  the cheap bash gates CI EXECUTES — SSR purity, Dockerfile restore graph,
#             migration safety (+ its self-test), backlog-loop provenance self-test. Seconds.
#   images=…  the four shipped OCI images — build + Trivy scan.
#
# Why three and not one: a change to a script that CI executes (say scripts/claude/work-ticket.sh)
# must re-run the guard that executes it — but it cannot break `dotnet build`, the test suites or
# the SSR guard. Collapsing the two into a single "run everything" verdict is what made a PR that
# only touched .claude/ + docs + the loop scripts pay for a full .NET build + both test suites.
#
# Verdict rules:
#   * A file is INERT unless it can affect one of the three gates. Everything under docs/, .claude/,
#     .github/, .idea/, .run/, .vscode/, .docker/, every *.md / *.sh / *.ps1 / compose file,
#     the Dockerfiles and .dockerignore.
#   * code    = any NON-inert file (fail-safe default: an unrecognized path IS a build input), plus
#               pr-verify.yml / ci.yml — they define the .NET gate, so a change to one must run the
#               gate it just redefined.
#   * guards  = anything the guard steps execute or read, plus everything `code` implies (a .NET PR
#               faces the guards too).
#   * images  = any Dockerfile flavor, .dockerignore, and pr-verify.yml (it defines the image job).
#
# NEVER add a build input (.editorconfig, Directory.*.props/.targets, global.json, nuget.config,
# a test fixture) to the inert list — that is a false green, the one failure mode that matters here.
# The reverse mistake (a redundant build) only costs minutes. `scripts/tests/classify-changes.tests.sh`
# pins both directions and runs UNCONDITIONALLY in the job that calls this script, so a classifier
# that starts skipping real builds goes red before it is ever trusted.
#
# Usage:
#   classify-changes.sh <base-ref>   # classify `git diff --name-only <base-ref> HEAD`
#   classify-changes.sh --files      # classify the newline-separated paths on stdin (test seam)
#
# Prints `key=value` lines to stdout (and appends them to $GITHUB_OUTPUT when set); the human
# breakdown goes to stderr. Fails OPEN — every verdict true — when the diff cannot be computed.
#
# CI-only (Linux runners), so unlike the check-*.sh guards this one has no .ps1 twin.
set -euo pipefail

INERT='\.md$|^docs/|^LICENSE$|^\.gitignore$|^\.gitattributes$|\.sh$|\.ps1$|(^|/)Dockerfile[^/]*$|(^|/)\.dockerignore$|^compose[^/]*\.ya?ml$|^\.docker/|^\.github/|^\.claude/|^\.run/|^\.vscode/|^\.idea/|(^|/)\.env[^/]*\.example$'

# Executed or read by the guard steps of pr-verify/ci. The .ps1 twins are here because the
# migration-safety self-test runs its pwsh leg; the Dockerfiles because the restore-graph guard
# parses their COPY lists — a Dockerfile-only PR that drops a COPY ["…csproj"] line must still
# face the guard that catches it (and the image job, which actually builds it). scripts/hetzner/
# deploy.sh is here because it is the script CD runs on a PROD box and its self-test is the only
# thing that checks it before it gets there (HETZ-04).
GUARD_INPUTS='^scripts/check-ssr-purity\.(sh|ps1)$|^scripts/check-migration-safety\.(sh|ps1)$|^scripts/tests/check-migration-safety\.tests\.sh$|^scripts/check-dockerfile-restore-graph\.(sh|ps1)$|^scripts/claude/(issue-trust|next-ticket|work-ticket)\.sh$|^scripts/tests/claude-loop-provenance\.tests\.sh$|^scripts/hetzner/deploy\.sh$|^scripts/tests/hetzner-deploy\.tests\.sh$|(^|/)Dockerfile[^/]*$|^\.github/workflows/(pr-verify|ci)\.yml$'

# Build-relevant despite matching INERT: the two workflows that DEFINE the .NET gate.
CODE_INPUTS='^\.github/workflows/(pr-verify|ci)\.yml$'

# Image-build inputs: ANY Dockerfile flavor (a dev-only flavor costs a redundant image build, never
# a false skip), the build-context filter, and pr-verify.yml — the image job must validate its own
# new definition.
IMAGE_INPUTS='(^|/)Dockerfile[^/]*$|(^|/)\.dockerignore$|^\.github/workflows/pr-verify\.yml$'

files=''
diffable=1

case "${1-}" in
    --files)
        files="$(cat)"
        ;;
    '' | -*)
        echo "usage: $(basename "$0") <base-ref> | --files < paths" >&2
        exit 2
        ;;
    *)
        if ! files="$(git diff --name-only "$1" HEAD)"; then
            diffable=0
        fi
        ;;
esac

code=true
guards=true
images=true

if [ "$diffable" -eq 1 ] && [ -n "$files" ]; then
    build_relevant="$(printf '%s\n' "$files" | grep -vE "$INERT" || true)"
    forced_code="$(printf '%s\n' "$files" | grep -E "$CODE_INPUTS" || true)"
    guard_relevant="$(printf '%s\n' "$files" | grep -E "$GUARD_INPUTS" || true)"
    image_relevant="$(printf '%s\n' "$files" | grep -E "$IMAGE_INPUTS" || true)"

    if [ -z "$build_relevant" ] && [ -z "$forced_code" ]; then
        code=false
    fi
    if [ "$code" = false ] && [ -z "$guard_relevant" ]; then
        guards=false
    fi
    if [ -z "$image_relevant" ]; then
        images=false
    fi

    {
        echo 'Changed files:'
        printf '%s\n' "$files" | sed 's/^/  /'
        echo "Build-relevant files: ${build_relevant:-<none>}"
        echo "Forced-code files:    ${forced_code:-<none>}"
        echo "Guard-relevant files: ${guard_relevant:-<none>}"
        echo "Image-relevant files: ${image_relevant:-<none>}"
    } >&2
elif [ "$diffable" -eq 0 ]; then
    echo '::warning::Could not compute the diff — running every gate.' >&2
else
    echo '::warning::Empty diff — running every gate.' >&2
fi

verdicts="$(printf 'code=%s\nguards=%s\nimages=%s\n' "$code" "$guards" "$images")"
printf '%s\n' "$verdicts"
if [ -n "${GITHUB_OUTPUT-}" ]; then
    printf '%s\n' "$verdicts" >> "$GITHUB_OUTPUT"
fi
