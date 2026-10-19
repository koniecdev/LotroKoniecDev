#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Migration-safety guard (PowerShell twin of check-migration-safety.sh).

.DESCRIPTION
    ADR-0023: migrations are forward-only and N-1 backward-compatible - the previous app
    revision keeps serving on the new schema during every deploy's smoke window and after
    any failed rollout (rollback moves traffic, never schema). A destructive migration
    turns that window into an outage, so it must be a deliberate expand -> backfill ->
    contract step, never an accident that passes a green gate.

    Scans migration files NEWLY ADDED relative to a base ref (plus staged and untracked
    files, so it works pre-commit). Only the Up() body is checked - every Down() contains
    drops by construction and never runs in shared environments (ADR-0023 section 1).
    Designer files and model snapshots are excluded; already-shipped migrations are never
    re-flagged. A flagged file passes when it carries a comment line with the token
    `MIGRATION-SAFETY: acknowledged` followed by the reason.

    Exit codes: 0 = clean, 1 = unacknowledged destructive migration, 2 = cannot run, or a new
    migration that git holds cannot be read from disk.

    CI runs the .sh; this .ps1 is the local twin for Windows devs - keep the two in sync.
    Tests for both: scripts/tests/check-migration-safety.tests.sh.

.PARAMETER Base
    Base ref to diff against (CI uses HEAD^1). Defaults to origin/main, then main.
#>

param(
    [string] $Base
)

$ErrorActionPreference = 'Stop'

# The generic-args group is [^(] (not [^>]) so nested generics still match:
# AlterColumn<Dictionary<string, string>>(.
$destructive  = '(DropColumn|DropTable|RenameColumn|RenameTable|DropIndex|AlterColumn)\s*(<[^(]*>)?\s*[(]'
$acknowledged = 'MIGRATION-SAFETY:\s*acknowledged'

$repoRoot = git rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Migration-safety guard: not inside a git repository.'
    exit 2
}

Push-Location $repoRoot
# git prints file names as UTF-8 here (core.quotePath=false). A Windows console code page would read
# a non-ASCII letter wrong, and the file would not be found on disk. The finally block sets the
# console back. A host with no console cannot change it: the guard then still fails closed on such a
# name, with exit 2.
$originalOutputEncoding = [Console]::OutputEncoding
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }
try {
    if ($Base) {
        git rev-parse --quiet --verify "$Base^{commit}" *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Migration-safety guard: cannot resolve base ref '$Base'."
            exit 2
        }
    }
    else {
        foreach ($candidate in 'origin/main', 'main') {
            git rev-parse --quiet --verify "$candidate^{commit}" *> $null
            if ($LASTEXITCODE -eq 0) { $Base = $candidate; break }
        }
        if (-not $Base) {
            Write-Host 'Migration-safety guard: no base ref given and neither origin/main nor main exists;'
            Write-Host 'scanning only staged and untracked migration files.'
        }
    }

    # --no-renames: default rename detection pairs a deleted migration with a similar
    # new one (the regenerate-a-migration flow) and hides the addition from
    # --diff-filter=A. Each git leg fails CLOSED (exit 2) - a gate that cannot compute
    # its input must not pass.
    # core.quotePath=false: by default git prints a name with a non-ASCII letter in quotes, with
    # escape codes, so the .cs$ filter below would drop that migration unscanned (#1079).
    $candidateFiles = @()
    if ($Base) {
        $candidateFiles += @(git -c core.quotePath=false diff --no-renames --name-only --diff-filter=A "$Base...HEAD" --)
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Migration-safety guard: git diff against base '$Base' failed."
            exit 2
        }
    }
    $candidateFiles += @(git -c core.quotePath=false diff --no-renames --cached --name-only --diff-filter=A --)
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Migration-safety guard: git diff --cached failed.'
        exit 2
    }
    $candidateFiles += @(git -c core.quotePath=false ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Migration-safety guard: git ls-files failed.'
        exit 2
    }
    # A new migration removed with git rm leaves with the next commit, so it never ships.
    $stagedDeletions = @(git -c core.quotePath=false diff --no-renames --cached --name-only --diff-filter=D --)
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Migration-safety guard: git diff --cached failed.'
        exit 2
    }

    # core.quotePath=false still quotes a name that holds a ", a \ or a control character. The filter
    # below would drop such a migration unscanned, so stop instead (#1162).
    $quotedFiles = @($candidateFiles | Where-Object { $_ -cmatch '^"(.*/)?Migrations/[^/]+\.cs"$' })
    if ($quotedFiles) {
        Write-Host 'Migration-safety guard: git quotes the name of this new migration, so the guard cannot read it:'
        $quotedFiles | ForEach-Object { Write-Host "    $_" }
        Write-Host 'Rename it without a ", a \ or a control character, then run the guard again.'
        exit 2
    }

    $newFiles = $candidateFiles |
        Where-Object { $_ -cmatch '(^|/)Migrations/[^/]+\.cs$' } |
        Where-Object { $_ -cnotmatch '(\.Designer\.cs|ModelSnapshot\.cs)$' } |
        Sort-Object -Unique

    if (-not $newFiles) {
        Write-Host 'OK Migration-safety guard passed - no newly-added migration files.'
        exit 0
    }

    $fail = $false
    $scanned = 0

    foreach ($file in $newFiles) {
        # A byte that is not valid UTF-8 reaches us as U+FFFD, and .NET cannot open a file whose name
        # holds one. The guard cannot scan such a migration, so it must not pass it (#1162). This runs
        # before the staged-deletion skip on purpose: two different bad bytes both become U+FFFD, so
        # that skip could match the wrong file.
        if ($file.Contains([char]0xFFFD)) {
            Write-Host "Migration-safety guard: the name of $file is not valid UTF-8, so the guard cannot open it."
            Write-Host 'Rename the migration to a valid UTF-8 name, then run the guard again.'
            exit 2
        }
        if (-not (Test-Path -LiteralPath $file)) {
            # Removed from disk only, the file is still in git and would ship unscanned (#1079).
            if ($stagedDeletions -ccontains $file) { continue }
            Write-Host "Migration-safety guard: $file is in git but not on disk, so it cannot be scanned."
            Write-Host 'Restore it, or remove it with git rm, then run the guard again.'
            exit 2
        }
        $scanned++

        try {
            $lines = Get-Content -LiteralPath $file -ErrorAction Stop
        }
        catch {
            Write-Host "Migration-safety guard: could not read ${file}: $($_.Exception.Message)"
            exit 2
        }

        # Only the Up() body: lines from `void Up(` up to (excluding) `void Down(` or EOF.
        $inUp = $false
        $hits = @()
        $lineNumber = 0
        foreach ($line in $lines) {
            $lineNumber++
            if ($line -cmatch 'void\s+Up\s*\(')   { $inUp = $true }
            if ($line -cmatch 'void\s+Down\s*\(') { $inUp = $false }
            if ($inUp -and $line -cmatch $destructive) {
                $hits += ('    {0}:{1}: {2}' -f $file, $lineNumber, $line.Trim())
            }
        }
        if (-not $hits) { continue }

        $marker = Select-String -LiteralPath $file -Pattern $acknowledged -CaseSensitive |
            Select-Object -First 1
        if ($marker) {
            Write-Host 'OK Acknowledged destructive migration (deliberate - ADR-0023 section 3 step):'
            Write-Host ('    {0}:{1}: {2}' -f $file, $marker.LineNumber, $marker.Line.Trim())
            Write-Host ''
            continue
        }

        $fail = $true
        Write-Host 'X Destructive operation(s) in a newly-added migration, without acknowledgment:'
        $hits | ForEach-Object { Write-Host $_ }
        Write-Host ''
    }

    if ($fail) {
        Write-Host '----------------------------------------------------------------------'
        Write-Host 'Migration-safety guard FAILED - migrations must be N-1 backward-compatible.'
        Write-Host 'The previous app revision serves on this schema during every deploy (ADR-0023).'
        Write-Host 'Split the change: expand -> backfill -> contract, across >= 2 deploys.'
        Write-Host 'Deliberate contract step (or the dropped shape never shipped)? Add a comment'
        Write-Host 'line inside the migration file:'
        Write-Host '    // MIGRATION-SAFETY: acknowledged - <reason>'
        Write-Host "See CLAUDE.md -> 'Migrations are forward-only' and docs/adr/0023-*.md."
        exit 1
    }

    Write-Host "OK Migration-safety guard passed - $scanned newly-added migration file(s) scanned."
}
finally {
    try { [Console]::OutputEncoding = $originalOutputEncoding } catch { }
    Pop-Location
}
