#!/usr/bin/env bash
# Checks that scripts/claude/ is an untouched copy of one engine version, and that the repository's
# loop settings fit it (CONTRACT.md, section 1.1). A repository's CI runs it on every change; the
# conductor runs only code and settings equal to origin/main, which that CI checked.
# verify-copy.ps1 is its twin for a shell without bash: change both.
#
#   scripts/claude/verify-copy.sh [--ref <commit>] [--engine <engine checkout>]
#
# It compares the git tree id of scripts/claude at <ref> (default HEAD) with the tree= line of the
# stamp scripts/claude-loop.engine. A tree id covers every file name, every byte and every exec bit,
# so an edited, added or removed file, or a lost exec bit, fails it. It reads committed files only:
# a change that is not committed yet is not checked. It then reads scripts/claude-loop.conf at that
# commit as the engine does, except for the check of `repo` against the checkout's origin, which a
# fork's clone would fail.
#
# With --engine it also checks, in a checkout of the engine repository, that the stamp's commit
# exists and has that tree in engine/, and prints how many engine commits the copy is behind the
# engine's origin/main. A file edited together with its stamp passes the check without --engine.
#
# Exit codes: 0 the copy and the settings fit · 1 they do not · 2 usage error
set -euo pipefail

usage() {
    echo "usage: verify-copy.sh [--ref <commit>] [--engine <engine checkout>]" >&2
    exit 2
}

REF=HEAD
ENGINE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --ref) [ $# -ge 2 ] || usage; REF="$2"; shift 2 ;;
        --engine) [ $# -ge 2 ] || usage; ENGINE="$2"; shift 2 ;;
        -h|--help) sed -n '2,/^set /p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) usage ;;
    esac
done

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here.
SCRIPT_DIR="$(CDPATH='' cd -- "$(dirname "$0")" && pwd)"
CHECKOUT="$(CDPATH='' cd -- "$SCRIPT_DIR/../.." && pwd)"
# shellcheck source=loop-config.sh
. "$SCRIPT_DIR/loop-config.sh"

failed=0
problem() {
    echo "verify-copy: $*" >&2
    failed=1
}

commit="$(git -C "$CHECKOUT" rev-parse -q --verify "$REF^{commit}" 2>/dev/null)" \
    || { echo "verify-copy: $REF is not a commit of $CHECKOUT" >&2; exit 2; }

# The stamp and the settings as committed, so a check in CI sees what will merge.
stamp="$(git -C "$CHECKOUT" show "$commit:scripts/claude-loop.engine" 2>/dev/null || true)"
stamp_commit="$(sed -n 's/^commit=//p' <<< "$stamp" | tail -1)"
stamp_tree="$(sed -n 's/^tree=//p' <<< "$stamp" | tail -1)"
if [ -z "$stamp" ]; then
    problem "scripts/claude-loop.engine is missing at $REF: copy the engine with its tools/sync.sh"
elif ! [[ "$stamp_commit" =~ ^[0-9a-f]{40}$ ]] || ! [[ "$stamp_tree" =~ ^[0-9a-f]{40}$ ]]; then
    problem "scripts/claude-loop.engine needs a commit= and a tree= line, each a full object id"
fi

copy_tree="$(git -C "$CHECKOUT" rev-parse -q --verify "$commit:scripts/claude" 2>/dev/null || true)"
if [ -z "$copy_tree" ]; then
    problem "scripts/claude/ is missing at $REF"
elif [ -n "$stamp_tree" ] && [ "$copy_tree" != "$stamp_tree" ]; then
    problem "scripts/claude/ at $REF is tree $copy_tree, but the stamp says the engine's tree $stamp_tree."
    problem "The copy was changed by hand. Make the change in the engine and copy it again with its tools/sync.sh."
fi

# The settings are read from the commit, through a folder of their own.
settings_dir="$(mktemp -d "${TMPDIR:-/tmp}/verify-copy.XXXXXX")" || { echo "verify-copy: no temporary folder" >&2; exit 2; }
trap 'rm -rf "$settings_dir"' EXIT
mkdir -p "$settings_dir/scripts"
if git -C "$CHECKOUT" show "$commit:scripts/claude-loop.conf" > "$settings_dir/scripts/claude-loop.conf" 2>/dev/null; then
    load_loop_config "$settings_dir" --no-origin || failed=1
else
    problem "scripts/claude-loop.conf is missing at $REF (CONTRACT.md, section 2)"
fi

if [ -n "$ENGINE" ] && [ -n "$stamp_commit" ]; then
    engine_tree="$(git -C "$ENGINE" rev-parse -q --verify "$stamp_commit:engine" 2>/dev/null || true)"
    if [ -z "$engine_tree" ]; then
        problem "the engine checkout $ENGINE has no commit $stamp_commit with an engine/ folder: fetch it, or the stamp is wrong"
    elif [ "$engine_tree" != "$stamp_tree" ]; then
        problem "the engine's commit $stamp_commit has tree $engine_tree, but the stamp says $stamp_tree: the stamp was changed by hand"
    else
        behind="$(git -C "$ENGINE" rev-list --count "$stamp_commit..origin/main" -- engine 2>/dev/null || echo "?")"
        echo "verify-copy: the copy is engine commit ${stamp_commit:0:12}; origin/main of the engine has $behind newer commit(s) that change engine/"
    fi
fi

[ "$failed" -eq 0 ] || exit 1
echo "verify-copy: scripts/claude/ at $REF is the engine's tree ${stamp_tree:0:12}, and the settings fit contract $LOOP_CONTRACT"
