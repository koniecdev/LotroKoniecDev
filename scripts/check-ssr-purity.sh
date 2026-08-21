#!/usr/bin/env bash
# Frontend markup guard: Static SSR purity, plus no inline <script> (CSP).
#
# The second rule is newer (#670): our CSP sends `script-src 'self'`, so an inline <script>
# in a component is dead on arrival and only the browser console reports it. It lives here
# rather than in a fifth guard script because this is already the frontend-markup gate CI
# runs in both workflows; the trade-off is that the file name now says less than it checks.
#
# The Frontend is Static SSR on purpose: no WebAssembly download, no SignalR circuit,
# no per-user server state. A single stray @rendermode, @onclick or StateHasChanged
# silently flips a page into interactive mode and quietly breaks that guarantee.
# CLAUDE.md states this rule in prose; THIS script is the machine that enforces it,
# so the rule can't rot. Run it before pushing; CI (pr-verify + ci) runs it on every PR.
#
# Lifted from TheKittySaver with ONE deliberate deviation: @onsubmit is allowed.
# It is the documented Static-SSR special exception — "always functional, regardless of
# render mode" (MS docs: aspnet/core/blazor/components/class-libraries-and-static-server-side-rendering)
# — and is how Editor.razor posts forms (<form method="post" @formname @onsubmit>).
# KittySaver uses <EditForm> so its broad @on* regex never sees this; we use plain forms.
# Every other @on* handler (onclick/onchange/oninput/…) is dead in SSR and stays forbidden.
# Keep this in sync with its check-ssr-purity.ps1 twin (run locally on Windows).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FRONTEND_DIR="$REPO_ROOT/src/Frontend"

if [ ! -d "$FRONTEND_DIR" ]; then
    echo "Pure-SSR guard: Frontend directory not found at $FRONTEND_DIR" >&2
    exit 2
fi

fail=0

check() {
    # $1 = grep -E pattern to flag, $2 = plain explanation of what to do instead,
    # $3 = optional grep -E allow-pattern: a token that IS valid in SSR (e.g. @onsubmit).
    #      It is stripped from each candidate line and $1 re-tested — so a line matching
    #      ONLY via the allowed token drops out, while a line that also carries a real
    #      violation (e.g. @onclick beside @onsubmit on one element) is still flagged.
    # obj/ and bin/ are build output, not source — never scan them (in CI this runs before
    # restore/build, but a local run after a build would otherwise hit generated *.g.cs).
    local matches
    matches="$(grep -rnE "$1" --include='*.razor' --include='*.cs' --exclude-dir=obj --exclude-dir=bin "$FRONTEND_DIR" 2>/dev/null || true)"
    if [ -n "$matches" ] && [ -n "${3:-}" ]; then
        matches="$(printf '%s\n' "$matches" | sed -E "s/$3//g" | grep -E "$1" || true)"
    fi
    if [ -n "$matches" ]; then
        fail=1
        printf '✗ %s\n' "$2"
        echo "$matches" | sed 's/^/    /'
        echo
    fi
}

check '@rendermode' \
    'Render-mode directive forces a component into interactive mode. Remove it — SSR renders on the server.'
check '@on[a-z]+[[:space:]]*[=:]' \
    'Blazor event handler (@onclick / @onchange / @oninput …) does nothing in SSR. Use <form method="post" @onsubmit> or <EditForm OnValidSubmit>.' \
    '@onsubmit[[:space:]]*[=:]'
check 'StateHasChanged' \
    'StateHasChanged only works in interactive mode. In SSR the next request re-renders the page — delete the call.'
check 'AddInteractiveServerComponents|AddInteractiveWebAssemblyComponents|AddInteractiveServerRenderMode|AddInteractiveWebAssemblyRenderMode' \
    'Interactive Blazor registered in Program.cs. Keep only AddRazorComponents() and MapRazorComponents<App>().'
# HTML tag and attribute names are case-insensitive, so spell both out. The other rules stay
# case-sensitive on purpose: they match C#/Razor identifiers, where case is part of the name.
# Known gaps, accepted: the scan is line-based, so an opening tag split over two lines slips
# through (the smoke test folds newlines and still catches it), and a literal "<script>" inside
# a C# string would be flagged with no way to opt out.
check '<[sS][cC][rR][iI][pP][tT][^>]*>' \
    "Inline <script> in the Frontend. Our CSP sends script-src 'self', so the browser blocks it and only the console says so (#670). Move the code to a file under wwwroot and load it with src=, or drop it." \
    '<[sS][cC][rR][iI][pP][tT][^>]*[sS][rR][cC]=[^>]*>'
# The trailing class keeps List<ImportMapDefinition> and friends out of the match.
check '<ImportMap[[:space:]/>]' \
    "Blazor's import-map component renders an inline <script type=\"importmap\">, which script-src 'self' blocks on every page (#670). A Static SSR app never resolves a module specifier, so it has no job here."

if [ "$fail" -ne 0 ]; then
    echo "──────────────────────────────────────────────────────────────────────"
    echo "Frontend markup guard FAILED."
    echo "See CLAUDE.md → 'Frontend is Static SSR'. Genuinely need interactivity, or an"
    echo "inline script? Both are architecture changes — write an ADR first (docs/adr/, /adr)."
    exit 1
fi

echo "✓ Frontend markup guard passed — Static SSR, no inline script."
