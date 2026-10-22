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

    A run that checks no Dockerfile at all is not a pass (#1079). A wrong folder, or a Dockerfile
    renamed out of the `Dockerfile*` pattern, would otherwise switch the guard off.

    Exit codes: 0 = every checked COPY list is complete, 1 = a COPY list is incomplete,
    2 = cannot run, or nothing was checked.

    CI runs the .sh; this .ps1 is the local twin for Windows devs — keep the two in sync.
    Tests for both: scripts/tests/check-dockerfile-restore-graph.tests.sh.
#>

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# A COPY or ADD instruction and its first source, in every shape Docker accepts: the keyword in any
# case (Docker lowers it), then any flag words such as `--link` or `--chown=app`, then a JSON list or
# the shell form. Only the first source is read, because this repo copies one project file per line.
# Used with -cmatch, so `.csproj` keeps its case, as in the .sh twin.
$copyPattern = '^\s*(?i:COPY|ADD)(\s+--\S*)*\s+(\[\s*"(?<source>[^"]+\.csproj)"|(?<source>[^\s\["]\S*\.csproj)\s)'
# `dotnet restore` as the shell runs it, with any spaces or tabs between the two words, or as an
# exec-form RUN ["dotnet", "restore", ...] list.
$restorePattern = 'dotnet(\s+|"\s*,\s*")restore'

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
    if (-not (Test-Path -LiteralPath $full)) { return @() }

    # A project the guard cannot read must not pass as a leaf: it would hide its whole subtree (#1079).
    try {
        $referenceLines = @(Select-String -LiteralPath $full -Pattern 'ProjectReference\s+Include="([^"]+)"' -AllMatches -ErrorAction Stop)
    }
    catch {
        Write-Host "Docker restore-graph guard: could not read ${Project}: $($_.Exception.Message)"
        exit 2
    }

    $dir = Split-Path -Parent $Project
    $refs = @()
    foreach ($match in $referenceLines) {
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
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $project))) {
            $missingOnDisk.Add($project)
            continue
        }
        foreach ($ref in (Get-ProjectReference $project)) { $queue.Enqueue($ref) }
    }

    return [pscustomobject]@{ Needed = $seen; MissingOnDisk = $missingOnDisk }
}

function Join-Continuation {
    # Returns the Dockerfile one instruction per line, read by the rules of Docker's own parser
    # (BuildKit, frontend/dockerfile/parser/parser.go). The COPY list and the restore roots are both
    # read from this output, so they cannot disagree on what a comment or a continuation is.
    # Get-Content already drops the CR of a CRLF line end.
    #   - A line whose first visible character is `#` is a comment. Comments go first: a
    #     commented-out `# dotnet restore Foo.csproj` must not add a restore root to a Dockerfile
    #     that restores nothing.
    #   - A `\` at the end of a line, with any spaces or tabs after it, joins the next line on.
    #   - Inside such a join, an empty line is skipped and the join goes on.
    # AllowEmptyString: a Mandatory string[] otherwise rejects the blank lines every Dockerfile has.
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [AllowEmptyCollection()]
        [string[]] $Lines
    )

    $joined = @()
    $buffer = ''
    $joining = $false
    foreach ($line in $Lines) {
        if ($line -match '^\s*#') { continue }
        if ($joining -and $line -match '^\s*$') { continue }
        if ($line -match '\\[ \t]*$') { $buffer += $line -replace '\\[ \t]*$', ''; $joining = $true; continue }
        $joined += ($buffer + $line)
        $buffer = ''
        $joining = $false
    }
    if ($joining) { $joined += $buffer }
    return $joined
}

function Find-Dockerfile {
    # Walks the tree the way the .sh twin's find does, so both twins check the same files.
    # -Force: without it, PowerShell on Linux and macOS skips every dot-folder (.docker, .github),
    # which find enters. .git and .claude at the root, and every node_modules, bin and obj, are never
    # entered, so a loop worktree or a build folder that changes during the walk cannot fail it.
    # Symbolic links and junctions are not followed, as with find's default -P. Other reparse points,
    # like OneDrive placeholders, are ordinary files and folders here. -clike matches the name with
    # case, as find -name does.
    param(
        [Parameter(Mandatory)][string] $Directory,
        [switch] $IsRoot
    )

    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force -ErrorAction Stop) {
        if ($item.LinkType -eq 'SymbolicLink' -or $item.LinkType -eq 'Junction') { continue }
        if ($item.PSIsContainer) {
            if ($item.Name -ceq 'node_modules' -or $item.Name -ceq 'bin' -or $item.Name -ceq 'obj') { continue }
            if ($IsRoot -and ($item.Name -ceq '.git' -or $item.Name -ceq '.claude')) { continue }
            Find-Dockerfile -Directory $item.FullName
        }
        elseif ($item.Name -clike 'Dockerfile*') {
            $item
        }
    }
}

# @(...) keeps this an array even for 0 or 1 hits — `foreach` over a bare $null iterates ONCE with
# a null item, which would blow up on .FullName below.
# A listing error stops the guard with exit 2, like a failed find in the .sh twin: a folder the
# guard cannot read may hold the Dockerfile it should check.
try {
    $dockerfiles = @(Find-Dockerfile -Directory $repoRoot -IsRoot | Sort-Object FullName)
}
catch {
    Write-Host "Docker restore-graph guard: could not list the Dockerfiles under ${repoRoot}: $($_.Exception.Message)"
    exit 2
}

$script:fail = $false
$checked = 0

foreach ($dockerfile in $dockerfiles) {
    $relative = $dockerfile.FullName.Substring($repoRoot.Length + 1) -replace '\\', '/'
    try {
        # @(...): an empty file gives $null, which the Mandatory parameter of Join-Continuation refuses.
        $lines = @(Get-Content -LiteralPath $dockerfile.FullName -ErrorAction Stop)
    }
    catch {
        Write-Host "Docker restore-graph guard: could not read ${relative}: $($_.Exception.Message)"
        exit 2
    }
    $instructions = @(Join-Continuation $lines)

    $copied = @()
    foreach ($line in $instructions) {
        if ($line -cmatch $copyPattern) { $copied += Get-NormalizedPath $Matches['source'] }
    }
    if ($copied.Count -eq 0) { continue }

    # -cnotmatch: the shell runs `dotnet`, never `DOTNET`, and the .sh twin's grep keeps the case too.
    $roots = @()
    foreach ($line in $instructions) {
        if ($line -cnotmatch $restorePattern) { continue }
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

if ($checked -eq 0) {
    Write-Host "Docker restore-graph guard: no Dockerfile under $repoRoot copies a .csproj list, so nothing was checked."
    Write-Host "Check the path, and that the image Dockerfiles still match 'Dockerfile*'."
    exit 2
}

Write-Host "OK Docker restore-graph guard passed - $checked Dockerfile(s) copy their full .csproj closure."
