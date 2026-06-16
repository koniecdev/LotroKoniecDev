#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Pure-SSR guard for the Frontend (PowerShell twin of check-ssr-purity.sh).

.DESCRIPTION
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

if ($script:fail) {
    Write-Host "----------------------------------------------------------------------"
    Write-Host "Pure-SSR guard FAILED - the Frontend must stay Static SSR."
    Write-Host "See CLAUDE.md -> 'Frontend is Static SSR'. Genuinely need interactivity?"
    Write-Host "That is an architecture change - write an ADR first (docs/adr/, /adr)."
    exit 1
}

Write-Host "OK Pure-SSR guard passed - Frontend is clean."
