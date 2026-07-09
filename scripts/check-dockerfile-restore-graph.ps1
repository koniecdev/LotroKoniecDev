#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Docker restore-graph guard (PowerShell twin of check-dockerfile-restore-graph.sh).

.DESCRIPTION
    Every image Dockerfile copies the .csproj files first, runs `dotnet restore`, and only then
    copies the sources — so NuGet restore lands in a layer keyed on the project graph alone and a
    C# edit never re-downloads packages. That optimisation has a silent failure mode:

        dotnet restore -> "Skipping project '.../Foo.csproj' because it was not found." -> exit 0

    A ProjectReference whose .csproj was never COPY'd does NOT fail the build. Restore quietly
    skips it, the cached layer is incomplete, and the later `dotnet build` re-restores the gap on
    every image build — reaching for the network in a step that is supposed to be offline. It stays
    green forever, so nobody notices; it is how Projections, Hateoas and Logging fell out of three
    Dockerfiles at once.

    For each Dockerfile that copies .csproj files explicitly, this derives the restore roots from
    the Dockerfile's own `dotnet restore` commands, walks the real ProjectReference graph on disk,
    and demands the COPY list cover the full transitive closure. Dockerfiles that copy whole source
    trees (Dockerfile.migrator.prod, Dockerfile.tests) have no list to go stale and are skipped.

    CI runs the .sh; this .ps1 is the local twin for Windows devs — keep the two in sync.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-NormalizedPath {
    # Collapse "a/b/../c" to "a/c" without touching the filesystem — the referenced project may
    # legitimately not exist (that is what we are hunting).
    param([Parameter(Mandatory)][string] $Path)

    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($segment in ($Path -replace '\\', '/') -split '/') {
        if ($segment -eq '' -or $segment -eq '.') { continue }
        if ($segment -eq '..') {
            # RemoveAt, never $parts[0..($parts.Count - 2)]: on a one-element list that slice is
            # 0..-1, which PowerShell reads as the index pair {0, -1} and DUPLICATES the element
            # instead of emptying the list — "App/../Lib" would normalise to "App/App/Lib".
            if ($parts.Count -gt 0) { $parts.RemoveAt($parts.Count - 1) }
            continue
        }
        $parts.Add($segment)
    }
    return ($parts -join '/')
}

function Get-ProjectReference {
    param([Parameter(Mandatory)][string] $Project)

    $full = Join-Path $repoRoot $Project
    if (-not (Test-Path $full)) { return @() }

    $dir = Split-Path -Parent $Project
    $refs = @()
    foreach ($match in (Select-String -Path $full -Pattern 'ProjectReference\s+Include="([^"]+)"' -AllMatches)) {
        foreach ($m in $match.Matches) {
            $refs += Get-NormalizedPath "$dir/$($m.Groups[1].Value)"
        }
    }
    return $refs
}

function Get-RestoreClosure {
    param([Parameter(Mandatory)][string[]] $Roots)

    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $missingOnDisk = [System.Collections.Generic.List[string]]::new()
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($root in $Roots) { $queue.Enqueue((Get-NormalizedPath $root)) }

    while ($queue.Count -gt 0) {
        $project = $queue.Dequeue()
        if (-not $seen.Add($project)) { continue }
        if (-not (Test-Path (Join-Path $repoRoot $project))) {
            $missingOnDisk.Add($project)
            continue
        }
        foreach ($ref in (Get-ProjectReference $project)) { $queue.Enqueue($ref) }
    }

    return [pscustomobject]@{ Needed = $seen; MissingOnDisk = $missingOnDisk }
}

function Join-Continuation {
    # Strip comments, then join `RUN a && \` / `    b` pairs into one command — the order Docker
    # itself uses. Comments must go first or the guard fails OPEN: a commented-out
    # `# dotnet restore Foo.csproj` would inject a phantom restore root, satisfying the "this
    # Dockerfile restores something" check for a Dockerfile that restores nothing. The COPY
    # extraction already ignores comments (it anchors on ^COPY), so the two must agree.
    #
    # The continuation join is a no-op on today's Dockerfiles, kept because a restore root wrapped
    # onto a bare continuation line would otherwise drop out of the closure and the guard would
    # pass vacuously.
    # AllowEmptyString: a Mandatory string[] otherwise rejects the blank lines every Dockerfile has.
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        [string[]] $Lines
    )

    $joined = @()
    $buffer = ''
    foreach ($line in $Lines) {
        if ($line -match '^\s*#') { continue }
        if ($line -match '\\$') { $buffer += $line -replace '\\$', ''; continue }
        $joined += ($buffer + $line)
        $buffer = ''
    }
    if ($buffer -ne '') { $joined += $buffer }
    return $joined
}

# Match on the repo-RELATIVE path: the repo itself may sit under a directory named like one of the
# excluded ones (agent worktrees live in .claude/worktrees/), which an absolute match would swallow.
# @(...) keeps this an array even for 0 or 1 hits — `foreach` over a bare $null iterates ONCE with
# a null item, which would blow up on .FullName below.
$dockerfiles = @(
    Get-ChildItem -Path $repoRoot -Recurse -File -Filter 'Dockerfile*' -ErrorAction SilentlyContinue |
        Where-Object {
            $rel = $_.FullName.Substring($repoRoot.Length + 1) -replace '\\', '/'
            $rel -notmatch '^(\.git|\.claude)/' -and $rel -notmatch '(^|/)node_modules/'
        } |
        Sort-Object FullName
)

if ($dockerfiles.Count -eq 0) {
    Write-Error "Docker restore-graph guard: no Dockerfile found under $repoRoot"
    exit 2
}

$script:fail = $false
$checked = 0

foreach ($dockerfile in $dockerfiles) {
    $relative = $dockerfile.FullName.Substring($repoRoot.Length + 1) -replace '\\', '/'
    $lines = Get-Content $dockerfile.FullName

    $copied = @()
    foreach ($line in $lines) {
        if ($line -match '^\s*COPY \["([^"]+\.csproj)"') { $copied += Get-NormalizedPath $Matches[1] }
    }
    if ($copied.Count -eq 0) { continue }

    $roots = @()
    foreach ($line in (Join-Continuation $lines)) {
        if ($line -notmatch 'dotnet restore') { continue }
        foreach ($m in [regex]::Matches($line, '[A-Za-z0-9_./-]+\.csproj')) { $roots += $m.Value }
    }
    $roots = $roots | Sort-Object -Unique

    if ($roots.Count -eq 0) {
        $script:fail = $true
        Write-Host "X $relative copies .csproj files but never runs ``dotnet restore`` on one.`n"
        continue
    }

    $checked++
    $closure = Get-RestoreClosure -Roots $roots

    if ($closure.MissingOnDisk.Count -gt 0) {
        $script:fail = $true
        Write-Host "X $relative restores a project graph that references files which do not exist:"
        foreach ($project in $closure.MissingOnDisk) { Write-Host "    $project" }
        Write-Host ""
    }

    # Ordinal, because the Linux image build is case-sensitive: a COPY ["lib/lib.csproj"] that
    # should read "Lib/Lib.csproj" lands no file there and restore skips it. PowerShell's
    # -notcontains would compare case-insensitively and wave that Dockerfile through, while the
    # .sh twin (byte-exact `comm`) fails it. The twins must return the same verdict.
    $copiedSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $copied, [System.StringComparer]::Ordinal)
    $missing = @($closure.Needed | Where-Object { -not $copiedSet.Contains($_) } | Sort-Object)
    if ($missing) {
        $script:fail = $true
        Write-Host "X $relative does not COPY every .csproj its restore graph needs:"
        foreach ($project in $missing) { Write-Host "    missing: $project" }
        Write-Host "    (dotnet restore SKIPS these silently - the restore layer is cached incomplete)"
        Write-Host ""
    }
}

if ($script:fail) {
    Write-Host "----------------------------------------------------------------------"
    Write-Host "Docker restore-graph guard FAILED."
    Write-Host 'Add the missing COPY ["...csproj", ".../"] lines, mirroring the sibling entries.'
    Write-Host "See CLAUDE.md -> 'Docker restore layers are gated': a new project must join"
    Write-Host "every Dockerfile whose restore graph reaches it."
    exit 1
}

Write-Host "OK Docker restore-graph guard passed - $checked Dockerfile(s) copy their full .csproj closure."
