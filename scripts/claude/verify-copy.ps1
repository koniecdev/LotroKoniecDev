# The PowerShell twin of verify-copy.sh, for a shell without bash: the same checks, the same exit
# codes. Change both together; the engine's suite runs the same cases against each.
#
#   pwsh scripts/claude/verify-copy.ps1 [-Ref <commit>] [-Engine <engine checkout>]
#
# Exit codes: 0 the copy and the settings fit · 1 they do not · 2 usage error
param(
    [string]$Ref = 'HEAD',
    [string]$Engine = ''
)
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$checkout = (Resolve-Path (Join-Path $scriptDir '../..')).Path
$script:failed = $false

function Problem([string]$text) {
    [Console]::Error.WriteLine("verify-copy: $text")
    $script:failed = $true
}

# Runs git and returns its output as one string, or $null when git fails.
function Invoke-GitText([string[]]$arguments) {
    $output = & git @arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($output -join "`n")
}

# The contract and the keys come from loop-config.sh, so the twins cannot disagree about them.
$loopConfig = Get-Content -Raw (Join-Path $scriptDir 'loop-config.sh')
$contract = [regex]::Match($loopConfig, '(?m)^LOOP_CONTRACT=(\d+)$').Groups[1].Value
$keys = [regex]::Match($loopConfig, '(?m)^LOOP_CONFIG_KEYS="([^"]*)"$').Groups[1].Value -split ' '
$shapes = @{
    'contract'             = '^[1-9][0-9]*$'
    'repo'                 = '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$'
    'gh_user'              = '^[A-Za-z0-9-]*$'
    'claude_config_dir'    = '^.+$'
    'provenance_gate'      = '^(on|off)$'
    'require_private'      = '^(on|off)$'
    'code_scanning'        = '^(on|off)$'
    'trusted_associations' = '^([A-Z_]+(,[A-Z_]+)*)?$'
    'trusted_logins'       = '^([A-Za-z0-9-]+(,[A-Za-z0-9-]+)*)?$'
    'red_main_workflow'    = '^([A-Za-z0-9_.-]+\.ya?ml)?$'
    'e2e_image_label'      = '^[A-Za-z0-9_.-]*$'
    'skip_labels'          = '^.*$'
    'skip_titles'          = '^.*$'
    'skip_issues'          = '^([0-9]+( [0-9]+)*)?$'
}

$commit = Invoke-GitText @('-C', $checkout, 'rev-parse', '-q', '--verify', "$Ref^{commit}")
if (-not $commit) {
    [Console]::Error.WriteLine("verify-copy: $Ref is not a commit of $checkout")
    exit 2
}

$stamp = Invoke-GitText @('-C', $checkout, 'show', "${commit}:scripts/claude-loop.engine")
$stampCommit = ''
$stampTree = ''
if ($stamp) {
    foreach ($line in $stamp -split "`n") {
        if ($line -match '^commit=(.*)$') { $stampCommit = $Matches[1] }
        if ($line -match '^tree=(.*)$') { $stampTree = $Matches[1] }
    }
}
if (-not $stamp) {
    Problem "scripts/claude-loop.engine is missing at ${Ref}: copy the engine with its tools/sync.sh"
} elseif ($stampCommit -notmatch '^[0-9a-f]{40}$' -or $stampTree -notmatch '^[0-9a-f]{40}$') {
    Problem 'scripts/claude-loop.engine needs a commit= and a tree= line, each a full object id'
}

$copyTree = Invoke-GitText @('-C', $checkout, 'rev-parse', '-q', '--verify', "${commit}:scripts/claude")
if (-not $copyTree) {
    Problem "scripts/claude/ is missing at $Ref"
} elseif ($stampTree -and $copyTree -ne $stampTree) {
    Problem "scripts/claude/ at $Ref is tree $copyTree, but the stamp says the engine's tree $stampTree."
    Problem 'The copy was changed by hand. Make the change in the engine and copy it again with its tools/sync.sh.'
}

$settings = Invoke-GitText @('-C', $checkout, 'show', "${commit}:scripts/claude-loop.conf")
if ($null -eq $settings) {
    Problem "scripts/claude-loop.conf is missing at $Ref (CONTRACT.md, section 2)"
} else {
    $values = @{}
    $problems = @()
    foreach ($raw in $settings -split "`n") {
        $line = $raw.TrimEnd("`r")
        if ($line -eq '' -or $line.StartsWith('#')) { continue }
        $at = $line.IndexOf('=')
        if ($at -lt 0) { $problems += "not a key=value line: $line"; continue }
        $key = $line.Substring(0, $at)
        $value = $line.Substring($at + 1)
        if ($keys -notcontains $key) { $problems += "unknown key: $key"; continue }
        if ($values.ContainsKey($key)) { $problems += "key given twice: $key"; continue }
        $values[$key] = $value
        if ($value -cnotmatch $shapes[$key]) { $problems += "$key has a value of the wrong shape: $value" }
    }
    foreach ($key in $keys) {
        if (-not $values.ContainsKey($key)) { $problems += "missing key: $key" }
    }
    if ($problems.Count -eq 0 -and $values['contract'] -ne $contract) {
        $problems += "contract=$($values['contract']), but this engine speaks contract ${contract}: update the prompts and the settings to it (scripts/claude/CONTRACT.md)"
    }
    if ($problems.Count -eq 0 -and $values['provenance_gate'] -eq 'on' -and
        "$($values['trusted_associations'])$($values['trusted_logins'])" -eq '') {
        $problems += 'provenance_gate=on trusts nobody: set trusted_associations or trusted_logins'
    }
    if ($problems.Count -gt 0) {
        Problem "scripts/claude-loop.conf is not usable:`n  $($problems -join "`n  ")"
    }
}

if ($Engine -and $stampCommit) {
    $engineTree = Invoke-GitText @('-C', $Engine, 'rev-parse', '-q', '--verify', "${stampCommit}:engine")
    if (-not $engineTree) {
        Problem "the engine checkout $Engine has no commit $stampCommit with an engine/ folder: fetch it, or the stamp is wrong"
    } elseif ($engineTree -ne $stampTree) {
        Problem "the engine's commit $stampCommit has tree $engineTree, but the stamp says ${stampTree}: the stamp was changed by hand"
    } else {
        $behind = Invoke-GitText @('-C', $Engine, 'rev-list', '--count', "$stampCommit..origin/main", '--', 'engine')
        if ($null -eq $behind) { $behind = '?' }
        Write-Output "verify-copy: the copy is engine commit $($stampCommit.Substring(0, 12)); origin/main of the engine has $behind newer commit(s) that change engine/"
    }
}

if ($script:failed) { exit 1 }
Write-Output "verify-copy: scripts/claude/ at $Ref is the engine's tree $($stampTree.Substring(0, 12)), and the settings fit contract $contract"
exit 0
