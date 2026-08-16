# E5 per-SubFile metadata snapshot (spec 0012 / #656): does an SSG chunk replacement move a
# SubFile's stored size, iteration number or version?
#
# Why: the Tier-0 sentinel of #565 samples fragment CONTENT because finding E1-F1 killed the
# whole-file size+mtime fingerprint. The per-SubFile metadata in between was never measured, and
# one native call (GetSubfileSizes) returns size + iteration for EVERY SubFile at once. If SSG's
# chunk replacement moves either value, detection is total and costs one DAT open with zero
# content reads. PatchingService deliberately preserves version and iteration when it writes a
# patched SubFile back, so any movement we see is SSG's, not ours.
#
# Windows-only. datexport.dll is x86, so this script re-launches itself in the 32-bit PowerShell
# host when needed. The read-only open needs NO elevation (#629) - run it as a normal user, the
# same way `export` runs.
#
# Protocol (snapshot order matters - the baseline must be taken on an already-patched DAT):
#   1. run `patch`, then:   .\e5-subfile-metadata-snapshot.ps1 -Label after-patch
#   2. plain launcher start, no update pending, then close it:
#                           .\e5-subfile-metadata-snapshot.ps1 -Label after-plain-launch
#   3. force the update (swap the live DAT for the 48.8 backup, let the launcher replay the
#      48.8 -> 49.1 cycle), then:
#                           .\e5-subfile-metadata-snapshot.ps1 -Label after-update
#   4. optional, after logging in and entering the game (the client writes at session start too):
#                           .\e5-subfile-metadata-snapshot.ps1 -Label after-session
#
# Add -IncludeVersion to any snapshot to also record the per-SubFile version (one native call per
# SubFile, timed separately so the one-call size+iteration cost stays visible).
#
# Then diff. Snapshot 2 vs 1 is the negative control and MUST be empty for the signal to be
# usable; snapshot 3 vs 1 is the measurement:
#   .\e5-subfile-metadata-snapshot.ps1 -Diff -Baseline <after-patch.csv> -Compare <after-update.csv>
#
# Snapshots land in the gitignored intel\update-49\ directory (~300k rows each, never committed).

[CmdletBinding(DefaultParameterSetName = 'Snapshot')]
param(
    [Parameter(ParameterSetName = 'Snapshot')]
    [string]$DatPath = 'C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat',

    [Parameter(ParameterSetName = 'Snapshot')]
    [string]$Label = 'unlabeled',

    [Parameter(ParameterSetName = 'Snapshot')]
    [string]$DllPath = (Join-Path $PSScriptRoot '..\..\src\Patcher\LotroKoniecDev.Infrastructure\datexport.dll'),

    [Parameter(ParameterSetName = 'Snapshot')]
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\intel\update-49'),

    [Parameter(ParameterSetName = 'Snapshot')]
    [switch]$IncludeVersion,

    [Parameter(ParameterSetName = 'Diff', Mandatory = $true)]
    [switch]$Diff,

    [Parameter(ParameterSetName = 'Diff', Mandatory = $true)]
    [string]$Baseline,

    [Parameter(ParameterSetName = 'Diff', Mandatory = $true)]
    [string]$Compare
)

$ErrorActionPreference = 'Stop'

# Text SubFiles carry 0x25 as the high byte of their FileId (DatFileConstants.TextFileMarker) -
# only those can hold our translations, so they get counted separately everywhere below.
$TextFileMarker = 0x25

function Test-IsTextFile([int]$fileId)
{
    return ($fileId -shr 24) -eq $TextFileMarker
}

# CSV shape: FileId,Size,Iteration[,Version]. Read by hand instead of Import-Csv - 300k
# PSCustomObjects per file take minutes in Windows PowerShell 5.1, plain lines take seconds.
function Read-Snapshot([string]$path)
{
    if (-not (Test-Path -LiteralPath $path))
    {
        throw "Snapshot not found: $path"
    }

    $lines = [System.IO.File]::ReadAllLines($path)
    if ($lines.Length -lt 2)
    {
        throw "Snapshot has no rows: $path"
    }

    $columns = $lines[0].Split(',')
    $map = [System.Collections.Generic.Dictionary[int, string[]]]::new($lines.Length)
    for ($i = 1; $i -lt $lines.Length; $i++)
    {
        $parts = $lines[$i].Split(',')
        if ($parts.Length -lt 3)
        {
            continue
        }
        $map[[int]$parts[0]] = $parts
    }

    return @{ Columns = $columns; Map = $map }
}

function Invoke-Diff([string]$baselinePath, [string]$comparePath)
{
    $baseline = Read-Snapshot $baselinePath
    $compare = Read-Snapshot $comparePath
    $hasVersion = ($baseline.Columns -contains 'Version') -and ($compare.Columns -contains 'Version')

    $sizeChanged = [System.Collections.Generic.List[int]]::new()
    $iterationChanged = [System.Collections.Generic.List[int]]::new()
    $versionChanged = [System.Collections.Generic.List[int]]::new()
    $anyChanged = [System.Collections.Generic.List[int]]::new()
    $added = [System.Collections.Generic.List[int]]::new()
    $removed = [System.Collections.Generic.List[int]]::new()

    foreach ($fileId in $compare.Map.Keys)
    {
        if (-not $baseline.Map.ContainsKey($fileId))
        {
            [void]$added.Add($fileId)
            continue
        }

        $before = $baseline.Map[$fileId]
        $after = $compare.Map[$fileId]
        $sizeMoved = $before[1] -ne $after[1]
        $iterationMoved = $before[2] -ne $after[2]
        $versionMoved = $hasVersion -and ($before[3] -ne $after[3])

        if ($sizeMoved) { [void]$sizeChanged.Add($fileId) }
        if ($iterationMoved) { [void]$iterationChanged.Add($fileId) }
        if ($versionMoved) { [void]$versionChanged.Add($fileId) }
        if ($sizeMoved -or $iterationMoved -or $versionMoved) { [void]$anyChanged.Add($fileId) }
    }

    foreach ($fileId in $baseline.Map.Keys)
    {
        if (-not $compare.Map.ContainsKey($fileId))
        {
            [void]$removed.Add($fileId)
        }
    }

    $textOf = { param($ids) @($ids | Where-Object { Test-IsTextFile $_ }).Count }

    Write-Host ''
    Write-Host "E5 diff" -ForegroundColor Cyan
    Write-Host "  baseline : $baselinePath ($($baseline.Map.Count) subfiles)"
    Write-Host "  compare  : $comparePath ($($compare.Map.Count) subfiles)"
    Write-Host ''
    Write-Host "  size changed      : $($sizeChanged.Count)   (text: $(& $textOf $sizeChanged))"
    Write-Host "  iteration changed : $($iterationChanged.Count)   (text: $(& $textOf $iterationChanged))"
    if ($hasVersion)
    {
        Write-Host "  version changed   : $($versionChanged.Count)   (text: $(& $textOf $versionChanged))"
    }
    else
    {
        Write-Host "  version changed   : n/a (take both snapshots with -IncludeVersion to compare it)"
    }
    Write-Host "  any changed       : $($anyChanged.Count)   (text: $(& $textOf $anyChanged))"
    Write-Host "  added             : $($added.Count)   (text: $(& $textOf $added))"
    Write-Host "  removed           : $($removed.Count)   (text: $(& $textOf $removed))"
    Write-Host ''

    if ($anyChanged.Count -eq 0 -and $added.Count -eq 0 -and $removed.Count -eq 0)
    {
        Write-Host "  VERDICT: no movement at all between these two snapshots." -ForegroundColor Yellow
        Write-Host "  As a negative control that is the PASS we want. As the post-update measurement" -ForegroundColor Yellow
        Write-Host "  it means the signal is dead and #565 stays a content sentinel." -ForegroundColor Yellow
    }

    $changedTextIds = @($anyChanged | Where-Object { Test-IsTextFile $_ } | Sort-Object)
    $compareItem = Get-Item -LiteralPath $comparePath
    $outPath = Join-Path $compareItem.DirectoryName ($compareItem.BaseName + '-changed-text-fileids.txt')
    [System.IO.File]::WriteAllLines($outPath, [string[]]$changedTextIds)
    Write-Host "  changed TEXT FileIds written to: $outPath"
    Write-Host "  Cross-check these against the 1,277 SubFiles the 48.8 vs 49.1 export pair-diff"
    Write-Host "  already knows were touched (update-49/RESULTS.md) - overlap is the real proof."
    Write-Host ''
}

if ($PSCmdlet.ParameterSetName -eq 'Diff')
{
    Invoke-Diff -baselinePath $Baseline -comparePath $Compare
    return
}

# datexport.dll is x86; a 64-bit host cannot load it. Re-launch in the 32-bit PowerShell instead of
# failing, so the protocol above works from whatever shell the box opens by default.
if ([Environment]::Is64BitProcess)
{
    $wow = Join-Path $env:SystemRoot 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $wow))
    {
        throw "This is a 64-bit PowerShell and the 32-bit host was not found at $wow. datexport.dll is x86."
    }

    Write-Host "64-bit host detected - relaunching in 32-bit PowerShell (datexport.dll is x86)..." -ForegroundColor DarkGray
    $relaunchArgs = @(
        '-ExecutionPolicy', 'Bypass', '-NoProfile', '-File', $PSCommandPath,
        '-DatPath', $DatPath, '-Label', $Label, '-DllPath', $DllPath, '-OutDir', $OutDir)
    if ($IncludeVersion)
    {
        $relaunchArgs += '-IncludeVersion'
    }
    & $wow @relaunchArgs
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $DatPath))
{
    throw "DAT not found: $DatPath"
}

if (-not (Test-Path -LiteralPath $DllPath))
{
    throw "datexport.dll not found: $DllPath (it is committed in the repo - check the clone, or pass -DllPath)"
}

$resolvedDll = (Resolve-Path -LiteralPath $DllPath).Path
$resolvedDat = (Resolve-Path -LiteralPath $DatPath).Path

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class E5Native
{
    // Pre-loading by absolute path pins the module the later DllImport("datexport.dll") calls bind
    // to, so the loader never probes the working directory or %PATH% (the DLL-planting guard the
    // production interop gets from DefaultDllImportSearchPaths). LOAD_WITH_ALTERED_SEARCH_PATH is
    // required: datexport.dll's own dependencies (msvcr71, msvcp71/90, zlib1T - committed beside
    // it) must resolve from ITS directory, not the PowerShell host's, or the load dies with
    // win32 error 126.
    public const uint LoadWithAlteredSearchPath = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("datexport.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int OpenDatFileEx2(
        int datFileHandle,
        [MarshalAs(UnmanagedType.LPStr)] string fileName,
        uint flags,
        out int didMasterMap,
        out int blockSize,
        out int vnumDatFile,
        out int vnumGameData,
        out uint datFileId,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] datIdStamp,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 64)] byte[] firstIterGuid);

    [DllImport("datexport.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetNumSubfiles(int datFileHandle);

    [DllImport("datexport.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void GetSubfileSizes(
        int datFileHandle,
        [Out, MarshalAs(UnmanagedType.LPArray)] int[] fileIds,
        [Out, MarshalAs(UnmanagedType.LPArray)] int[] sizes,
        [Out, MarshalAs(UnmanagedType.LPArray)] int[] iterations,
        int offset,
        int count);

    [DllImport("datexport.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSubfileVersion(int datFileHandle, int fileId);

    [DllImport("datexport.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseDatFile(int datFileHandle);
}
'@

if ([E5Native]::LoadLibraryExW($resolvedDll, [IntPtr]::Zero, [E5Native]::LoadWithAlteredSearchPath) -eq [IntPtr]::Zero)
{
    throw "LoadLibraryExW failed for $resolvedDll (win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
}

# 2 | ReadOnly(4). Bit 0x4 is what makes the native library ask the OS for GENERIC_READ only, which
# is why this needs no elevation - see docs/knowledge-base/datexport-readonly-open-2026-08-07.md.
$openFlagsRead = [uint32]6
$requestedHandle = 1
$datIdStamp = New-Object byte[] 64
$firstIterGuid = New-Object byte[] 64

# PowerShell needs a real variable behind every [ref] - it cannot bind an out parameter to $null.
$didMasterMap = 0
$blockSize = 0
$vnumDatFile = 0
$vnumGameData = 0
$datFileId = [uint32]0

$datFile = Get-Item -LiteralPath $resolvedDat
$processes = (Get-Process -Name LotroLauncher, lotroclient, lotroclient64 -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join '+'
if (-not $processes) { $processes = 'none' }

$sizesWatch = [System.Diagnostics.Stopwatch]::StartNew()

$handle = [E5Native]::OpenDatFileEx2(
    $requestedHandle, $resolvedDat, $openFlagsRead,
    [ref]$didMasterMap, [ref]$blockSize, [ref]$vnumDatFile, [ref]$vnumGameData, [ref]$datFileId,
    $datIdStamp, $firstIterGuid)

if ($handle -ne $requestedHandle)
{
    throw "OpenDatFileEx2 failed (returned $handle, expected $requestedHandle). Is the client running and holding the DAT?"
}

$versions = $null
$versionMs = 0

try
{
    $count = [E5Native]::GetNumSubfiles($handle)
    if ($count -le 0)
    {
        throw "GetNumSubfiles returned $count"
    }

    $fileIds = New-Object int[] $count
    $sizes = New-Object int[] $count
    $iterations = New-Object int[] $count

    [E5Native]::GetSubfileSizes($handle, $fileIds, $sizes, $iterations, 0, $count)
    $sizesWatch.Stop()

    if ($IncludeVersion)
    {
        $versionWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $versions = New-Object int[] $count
        for ($i = 0; $i -lt $count; $i++)
        {
            $versions[$i] = [E5Native]::GetSubfileVersion($handle, $fileIds[$i])
        }
        $versionWatch.Stop()
        $versionMs = $versionWatch.ElapsedMilliseconds
    }
}
finally
{
    [E5Native]::CloseDatFile($handle)
}

if (-not (Test-Path -LiteralPath $OutDir))
{
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csvPath = Join-Path $OutDir "e5-$Label-$timestamp.csv"

$builder = [System.Text.StringBuilder]::new()
if ($IncludeVersion)
{
    [void]$builder.AppendLine('FileId,Size,Iteration,Version')
    for ($i = 0; $i -lt $count; $i++)
    {
        [void]$builder.AppendLine("$($fileIds[$i]),$($sizes[$i]),$($iterations[$i]),$($versions[$i])")
    }
}
else
{
    [void]$builder.AppendLine('FileId,Size,Iteration')
    for ($i = 0; $i -lt $count; $i++)
    {
        [void]$builder.AppendLine("$($fileIds[$i]),$($sizes[$i]),$($iterations[$i])")
    }
}
[System.IO.File]::WriteAllText($csvPath, $builder.ToString())

$textCount = 0
for ($i = 0; $i -lt $count; $i++)
{
    if (Test-IsTextFile $fileIds[$i]) { $textCount++ }
}

$metaPath = Join-Path $OutDir "e5-$Label-$timestamp.meta.txt"
@(
    "label                 : $Label"
    "taken                 : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "dat                   : $resolvedDat"
    "dat size (B)          : $($datFile.Length)"
    "dat mtime             : $($datFile.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
    "vnumDatFile           : $vnumDatFile"
    "vnumGameData          : $vnumGameData"
    "subfiles total        : $count"
    "subfiles text         : $textCount"
    "running procs         : $processes"
    "open+sizes call ms    : $($sizesWatch.ElapsedMilliseconds)"
    "version loop ms       : $(if ($IncludeVersion) { $versionMs } else { 'skipped' })"
) | Set-Content -LiteralPath $metaPath -Encoding UTF8

Write-Host ''
Write-Host "E5 snapshot '$Label' taken - open + GetSubfileSizes in $($sizesWatch.ElapsedMilliseconds) ms" -ForegroundColor Green
if ($IncludeVersion)
{
    Write-Host "  version loop : $versionMs ms ($count per-SubFile calls)"
}
Write-Host "  subfiles     : $count total, $textCount text"
Write-Host "  csv          : $csvPath"
Write-Host "  meta         : $metaPath"
Write-Host ''
Write-Host "The open + sizes duration is the cost the whole Tier-0 detector would pay per launch if"
Write-Host "this signal turns out to work. Note it in the E5 write-up."
Write-Host ''
