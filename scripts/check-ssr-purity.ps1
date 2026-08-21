#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Frontend markup guard (PowerShell twin of check-ssr-purity.sh): Static SSR purity, plus
    no inline <script> (CSP).

.DESCRIPTION
    The second rule is newer (#670): our CSP sends script-src 'self', so an inline <script> in
    a component is dead on arrival and only the browser console reports it. It lives here rather
    than in a fifth guard script because this is already the frontend-markup gate CI runs in both
    workflows; the trade-off is that the file name now says less than it checks.

    The Frontend is Static SSR on purpose: no WebAssembly download, no SignalR circuit,
    no per-user server state. A single stray @rendermode, @onclick or StateHasChanged
    silently flips a page into interactive mode and quietly breaks that guarantee.
    CLAUDE.md states the rule in prose; this script enforces it. CI runs the .sh; this
    .ps1 is the local twin for Windows devs — keep the two in sync.

    Lifted from TheKittySaver with ONE deliberate deviation: @onsubmit is allowed. It is
    the documented Static-SSR special exception — "always functional, regardless of render
    mode" — and is how Editor.razor posts forms (<form method="post" @formname @onsubmit>).
    Every other @on* handler (onclick/onchange/oninput/...) is dead in SSR and stays forbidden.
#>

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot
$frontendDir = Join-Path $repoRoot 'src/Frontend'

if (-not (Test-Path $frontendDir)) {
    Write-Error "Pure-SSR guard: Frontend directory not found at $frontendDir"
    exit 2
}

# obj/ and bin/ are build output, not source — never scan them (mirrors the .sh --exclude-dir).
$files = Get-ChildItem -Path $frontendDir -Recurse -File -Include '*.razor', '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
$script:fail = $false

function Test-SsrPurity {
    # -Pattern flags a violation; -AllowPattern (optional) is an SSR-valid token (e.g. @onsubmit)
    # stripped before re-testing, so it can't mask a real handler sharing the line.
    param(
        [Parameter(Mandatory)][string] $Pattern,
        [Parameter(Mandatory)][string] $Message,
        [string] $AllowPattern
    )

    $hits = $files | Select-String -Pattern $Pattern -CaseSensitive
    if ($AllowPattern) {
        # Strip the SSR-valid token, then re-test: a line matching ONLY via the allowed
        # token drops out; a line also carrying a real handler is still flagged.
        $hits = $hits | Where-Object { ($_.Line -creplace $AllowPattern, '') -cmatch $Pattern }
    }

    if ($hits) {
        $script:fail = $true
        Write-Host "X $Message"
        foreach ($hit in $hits) {
            Write-Host ("    {0}:{1}:{2}" -f $hit.Path, $hit.LineNumber, $hit.Line.Trim())
        }
        Write-Host ""
    }
}

Test-SsrPurity -Pattern '@rendermode' `
    -Message 'Render-mode directive forces a component into interactive mode. Remove it - SSR renders on the server.'
Test-SsrPurity -Pattern '@on[a-z]+\s*[=:]' `
    -Message 'Blazor event handler (@onclick / @onchange / @oninput ...) does nothing in SSR. Use <form method="post" @onsubmit> or <EditForm OnValidSubmit>.' `
    -AllowPattern '@onsubmit\s*[=:]'
Test-SsrPurity -Pattern 'StateHasChanged' `
    -Message 'StateHasChanged only works in interactive mode. In SSR the next request re-renders the page - delete the call.'
Test-SsrPurity -Pattern 'AddInteractiveServerComponents|AddInteractiveWebAssemblyComponents|AddInteractiveServerRenderMode|AddInteractiveWebAssemblyRenderMode' `
    -Message 'Interactive Blazor registered in Program.cs. Keep only AddRazorComponents() and MapRazorComponents<App>().'
# HTML tag and attribute names are case-insensitive, so spell both out. The other rules stay
# case-sensitive on purpose: they match C#/Razor identifiers, where case is part of the name.
Test-SsrPurity -Pattern '<[sS][cC][rR][iI][pP][tT][^>]*>' `
    -Message "Inline <script> in the Frontend. Our CSP sends script-src 'self', so the browser blocks it and only the console says so (#670). Move the code to a file under wwwroot and load it with src=, or drop it." `
    -AllowPattern '<[sS][cC][rR][iI][pP][tT][^>]*[sS][rR][cC]=[^>]*>'
# The trailing class keeps List<ImportMapDefinition> and friends out of the match.
Test-SsrPurity -Pattern '<ImportMap[\s/>]' `
    -Message "Blazor's import-map component renders an inline <script type=""importmap"">, which script-src 'self' blocks on every page (#670). A Static SSR app never resolves a module specifier, so it has no job here."

if ($script:fail) {
    Write-Host "----------------------------------------------------------------------"
    Write-Host "Frontend markup guard FAILED."
    Write-Host "See CLAUDE.md -> 'Frontend is Static SSR'. Genuinely need interactivity, or an"
    Write-Host "inline script? Both are architecture changes - write an ADR first (docs/adr/, /adr)."
    exit 1
}

Write-Host "OK Frontend markup guard passed - Static SSR, no inline script."
