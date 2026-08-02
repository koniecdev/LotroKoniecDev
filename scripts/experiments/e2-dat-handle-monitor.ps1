# E2 DAT handle/write timeline monitor (spec 0012 / #557): run across a full SSG update cycle
# (or the launcher's repair/verify mode) to record when the DAT is written and when an RW-open
# probe succeeds. Answers: does the launcher release the DAT between download and apply bursts,
# and can a probe succeed mid-update (decisive for the branch-B quiesce window).
#
# Windows-only; run from an ELEVATED PowerShell BEFORE starting the launcher, leave it running
# through the whole update, stop with Ctrl+C after the game boots:
#   powershell -ExecutionPolicy Bypass -File scripts\experiments\e2-dat-handle-monitor.ps1
# Logs one line per state change (probe result / size / mtime / process set) + a 30 s heartbeat.
param(
    [string]$DatPath = 'C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat',
    [string]$LogPath = (Join-Path $PSScriptRoot '..\..\intel\update-49\e2-handle-timeline.log'),
    [double]$IntervalSeconds = 1.0,
    [int]$HeartbeatSeconds = 30
)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin)
{
    Write-Warning 'NOT elevated - ACL denials will mask the sharing state. Re-run from an elevated PowerShell.'
}

function Get-ProbeState
{
    param([string]$Path)
    try
    {
        $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $fs.Close()
        return 'OPEN-OK'
    }
    catch [System.IO.IOException] { return 'LOCKED' }
    catch [System.UnauthorizedAccessException] { return 'ACCESS-DENIED' }
}

function Write-TimelineLine
{
    param([string]$Message)
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') | $Message"
    Write-Host $line
    Add-Content -LiteralPath $script:LogPath -Value $line
}

Write-TimelineLine "monitor started | elevated=$isAdmin | interval=${IntervalSeconds}s | dat=$DatPath"

$previous = ''
$lastHeartbeat = Get-Date
while ($true)
{
    $dat = Get-Item -LiteralPath $DatPath -ErrorAction SilentlyContinue
    $probe = if ($dat) { Get-ProbeState -Path $DatPath } else { 'FILE-MISSING' }
    $procs = (Get-Process -Name LotroLauncher, lotroclient, lotroclient64 -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join '+'
    if (-not $procs) { $procs = 'none' }
    $size = if ($dat) { $dat.Length } else { 'missing' }
    $mtime = if ($dat) { $dat.LastWriteTime.ToString('HH:mm:ss.fff') } else { '-' }
    $current = "probe=$probe | procs=$procs | size=$size | mtime=$mtime"

    $now = Get-Date
    if ($current -ne $previous)
    {
        Write-TimelineLine "CHANGE | $current"
        $previous = $current
        $lastHeartbeat = $now
    }
    elseif (($now - $lastHeartbeat).TotalSeconds -ge $HeartbeatSeconds)
    {
        Write-TimelineLine "heartbeat | $current"
        $lastHeartbeat = $now
    }

    Start-Sleep -Milliseconds ([Math]::Max(100, [int]($IntervalSeconds * 1000)))
}
