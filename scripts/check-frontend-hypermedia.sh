#!/usr/bin/env bash
# Hypermedia guard for the Frontend.
#
# There is no API gateway (ADR-0041): each API's discovery document IS the client
# contract surface, so the Frontend resolves every entry point by REL NAME through
# IDiscoveryCache and follows the href the server hands back. It must never carry an
# API path of its own — a hardcoded "/api/v1/..." silently reintroduces the coupling
# #610 removed, and it fails at runtime (404, or worse a call the caller was never
# authorized to make) instead of at build time.
#
# CLAUDE.md and ADR-0041 state this rule in prose; THIS script is the machine that
# enforces it, so the rule can't rot. Run it before pushing; CI (pr-verify + ci) runs
# it on every PR. Keep this in sync with its check-frontend-hypermedia.ps1 twin.
#
# What it flags: an API path inside a STRING LITERAL (the shape a hardcoded route
# actually takes — a const, an interpolation base, a razor href). Prose mentions in
# comments and XML docs are deliberately allowed: documenting which route a rel points
# at is useful, and banning that only pushes people to weaken the guard.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FRONTEND_DIR="$REPO_ROOT/src/Frontend"

if [ ! -d "$FRONTEND_DIR" ]; then
    echo "Hypermedia guard: Frontend directory not found at $FRONTEND_DIR" >&2
    exit 2
fi

fail=0

check() {
    # $1 = grep -E pattern to flag, $2 = plain explanation of what to do instead.
    # obj/ and bin/ are build output, not source — never scan them (in CI this runs before
    # restore/build, but a local run after a build would otherwise hit generated *.g.cs).
    local matches
    matches="$(grep -rnE "$1" --include='*.razor' --include='*.cs' --exclude-dir=obj --exclude-dir=bin "$FRONTEND_DIR" 2>/dev/null || true)"
    if [ -n "$matches" ]; then
        fail=1
        printf '✗ %s\n' "$2"
        echo "$matches" | sed 's/^/    /'
        echo
    fi
}

# A double-quoted literal containing a versioned API path. C# strings and razor attributes
# both use double quotes, so one pattern covers every real occurrence; the version is matched
# loosely so a future /api/v2 cannot slip through.
check '"[^"]*api/v[0-9]' \
    'A TMS API path is hardcoded in a string literal. Resolve the entry point by rel instead: IDiscoveryCache.ResolveTranslationSystemHrefAsync(Rels.<Name>), or follow the href a loaded resource already advertises.'

if [ "$fail" -ne 0 ]; then
    echo "──────────────────────────────────────────────────────────────────────"
    echo "Hypermedia guard FAILED — the Frontend must not hardcode API paths."
    echo "See ADR-0041 and CLAUDE.md → 'No API gateway'. A client takes one root URL"
    echo "per service as config and resolves everything else by rel name."
    exit 1
fi

echo "✓ Hypermedia guard passed — Frontend resolves its entry points by rel."
