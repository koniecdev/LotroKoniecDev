#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Hypermedia guard for every API client (PowerShell twin of check-client-hypermedia.sh).

.DESCRIPTION
    There is no API gateway (ADR-0041): each API's discovery document IS the client contract
    surface, so a client - the Frontend (#610) and the patcher CLI (#611) - resolves every entry
    point by REL NAME and follows the href the server hands back. It must never carry an API path
    of its own - a hardcoded "/api/v1/..." silently reintroduces the coupling those tickets
    removed, and it fails at runtime (404, or worse a call the caller was never authorized to
    make) instead of at build time. The CLI is the sharper case: it ships to players' machines and
    cannot be updated remotely, so a path baked into it is a permanent commitment.

    CLAUDE.md and ADR-0041 state the rule in prose; this script enforces it. CI runs the .sh;
    this .ps1 is the local twin for Windows devs - keep the two in sync.
    Tests for both: scripts/tests/check-client-hypermedia.tests.sh.

    A run that scans no file is not a pass (#1079), so a missing client folder, a folder with no
    .razor or .cs file, and a file that cannot be read all stop the guard with exit 2.

    What it flags: an API path inside a STRING LITERAL (the shape a hardcoded route actually
    takes - a const, an interpolation base, a razor href). Prose mentions in comments and XML
    docs are deliberately allowed: documenting which route a rel points at is useful, and
    banning that only pushes people to weaken the guard.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# A double-quoted literal containing a versioned API path. C# strings and razor attributes both
# use double quotes, so one pattern covers every real occurrence; the version is matched loosely
# so a future /api/v2 cannot slip through.
$apiPathPattern = '"[^"]*api/v[0-9]'

$script:fail = $false

function Find-SourceFile {
    # Lists exactly the files the .sh twin's grep -r reads. It enters dot-folders (-Force; without it,
    # PowerShell on Linux and macOS skips them), never enters obj/ or bin/ (matched with case, like
    # --exclude-dir), skips symbolic links like grep -r does, and matches the name with case.
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][string] $NamePattern
    )

    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force -ErrorAction Stop) {
        if ($item.LinkType -eq 'SymbolicLink' -or $item.LinkType -eq 'Junction') { continue }
        if ($item.PSIsContainer) {
            if ($item.Name -ceq 'obj' -or $item.Name -ceq 'bin') { continue }
            Find-SourceFile -Directory $item.FullName -NamePattern $NamePattern
        }
        elseif ($item.Name -cmatch $NamePattern) {
            $item
        }
    }
}

function Test-Hypermedia {
    param(
        [Parameter(Mandatory)][string] $Client,
        [Parameter(Mandatory)][string] $Message
    )

    # Write-Host, not Write-Error: under ErrorActionPreference Stop, Write-Error throws, and the
    # script would end with exit 1 before it reached `exit 2`.
    $dir = Join-Path $repoRoot "src/$Client"
    if (-not (Test-Path $dir)) {
        Write-Host "Hypermedia guard: client directory not found at $dir"
        exit 2
    }

    # obj/ and bin/ are build output, not source - never scan them (mirrors the .sh --exclude-dir).
    try {
        $files = @(Find-SourceFile -Directory $dir -NamePattern '\.(razor|cs)$')
    }
    catch {
        Write-Host "Hypermedia guard: could not list the files under ${dir}: $($_.Exception.Message)"
        exit 2
    }
    if ($files.Count -eq 0) {
        Write-Host "Hypermedia guard: no .razor or .cs file under $dir, so nothing was checked."
        exit 2
    }

    try {
        $hits = $files | Select-String -Pattern $apiPathPattern -CaseSensitive -ErrorAction Stop
    }
    catch {
        Write-Host "Hypermedia guard: could not read the files under ${dir}: $($_.Exception.Message)"
        exit 2
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

Test-Hypermedia -Client 'Frontend' `
    -Message 'A TMS API path is hardcoded in a string literal under src/Frontend. Resolve the entry point by rel instead: IDiscoveryCache.ResolveTranslationSystemHrefAsync(Rels.<Name>), or follow the href a loaded resource already advertises.'

Test-Hypermedia -Client 'Patcher' `
    -Message 'A TMS API path is hardcoded in a string literal under src/Patcher. The CLI ships to players and cannot be updated remotely, so a baked-in path is permanent - resolve the endpoint by rel through ITranslationFileEndpointResolver instead.'

if ($script:fail) {
    Write-Host "----------------------------------------------------------------------"
    Write-Host "Hypermedia guard FAILED - API clients must not hardcode API paths."
    Write-Host "See ADR-0041 and CLAUDE.md -> 'No API gateway'. A client takes one root URL"
    Write-Host "per service as config and resolves everything else by rel name."
    exit 1
}

Write-Host "OK Hypermedia guard passed - the Frontend and the CLI resolve their entry points by rel."
