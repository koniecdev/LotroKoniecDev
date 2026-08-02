# E1 RW-open probe (spec 0012 / #557): can we open the LOTRO DAT ReadWrite with FileShare.None?
# Windows-only; run from an ELEVATED PowerShell (Users have only RX on the game dir, so a
# non-elevated run reports ACCESS-DENIED and proves nothing about sharing).
#
# Usage (from repo root, elevated):
#   powershell -ExecutionPolicy Bypass -File scripts\experiments\e1-rw-probe.ps1 -Label "login-screen"
# Labels used by the E1 protocol: baseline | login-screen | in-game
param(
    [string]$DatPath = 'C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat',
    [string]$LogPath = (Join-Path $PSScriptRoot '..\..\intel\update-49\e1-probe-results.log'),
    [string]$Label = 'unlabeled'
)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin)
{
    Write-Warning 'NOT elevated - an ACL denial would mask the sharing state. Re-run from an elevated PowerShell.'
}

$ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
$procs = (Get-Process -Name LotroLauncher, lotroclient -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join '+'
if (-not $procs) { $procs = 'none' }
$dat = Get-Item -LiteralPath $DatPath
$state = "size=$($dat.Length) mtime=$($dat.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"

try
{
    $fs = [IO.File]::Open($DatPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $fs.Close()
    $result = 'OPEN-OK - nobody holds the DAT; silent in-place patch is possible'
}
catch [System.IO.IOException]
{
    $hr = '0x{0:X8}' -f ($_.Exception.HResult)
    # 0x80070020 ERROR_SHARING_VIOLATION / 0x80070021 ERROR_LOCK_VIOLATION
    $result = "LOCKED - IOException HResult=$hr : $($_.Exception.Message.Trim())"
}
catch [System.UnauthorizedAccessException]
{
    $result = 'ACCESS-DENIED - elevation/ACL problem; result NOT conclusive, re-run elevated'
}

$line = "$ts | elevated=$isAdmin | procs=$procs | label=$Label | $state | $result"
Write-Host $line
Add-Content -LiteralPath $LogPath -Value $line
