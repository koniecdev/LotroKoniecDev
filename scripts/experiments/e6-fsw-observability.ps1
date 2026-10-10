# E6 FileSystemWatcher observability (spec 0012 Tier 1 rule 4 / #660): does a FileSystemWatcher
# see the launcher's DAT writes WHILE the launcher holds the file, or only at the end of the burst?
#
# Why: branch B of the update-day orchestrator (#566) may kill the launcher only after "no DAT
# writes for 30+ s while the handle is held". E2 polled size/mtime once per second and saw them
# move only at the END of the apply burst. If a watcher is also blind during the hold, a long apply
# looks like silence and branch B would kill the launcher in the middle of a write.
#
# This is the E2 monitor (same 1 s RW probe, size, mtime and process line, same CHANGE/heartbeat
# format) plus three FileSystemWatchers on the DAT, one per kind of change, so the log says WHICH
# change the OS reported:
#   size      - NotifyFilters.Size
#   lastwrite - NotifyFilters.LastWrite
#   other     - Attributes, CreationTime, Security, FileName, LastAccess
# Every watcher event is logged with the time it was raised and the file size at that moment.
# A watcher Error (internal buffer overflow) is logged too and counted in the heartbeat.
#
# Windows-only. Run from an ELEVATED PowerShell (the RW probe needs it, see E1) BEFORE starting the
# launcher, and stop it with Ctrl+C when done:
#   powershell -ExecutionPolicy Bypass -File scripts\experiments\e6-fsw-observability.ps1
# Leave it alone for the first minute: probe-only lines show whether our own probe makes the
# watchers fire. Then start the launcher. Protocol and results: docs/knowledge-base/update-49/RESULTS.md §E6.
#
# Our own polling can make the OS report a change: on NTFS a size or time change made through an
# open handle may reach the watchers only when someone reads the file's metadata or the handle
# closes. To measure the watchers alone, switch the polling off:
#   -NoProbe  no RW open attempt (the probe column says 'off')
#   -NoStat   no size/mtime read through Get-Item (those columns say 'off')
param(
    [string]$DatPath = 'C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat',
    [string]$LogPath = '',
    [double]$IntervalSeconds = 1.0,
    [int]$HeartbeatSeconds = 30,
    [switch]$NoProbe,
    [switch]$NoStat
)

# Resolve the default here, not in param(): Windows PowerShell 5.1 can leave $PSScriptRoot empty
# there (the E5 script hits it when started with -File).
if (-not $LogPath)
{
    $LogPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..\intel\update-49.7\e6-fsw-timeline.log'
}

$logDirectory = Split-Path -Parent $LogPath
if (-not (Test-Path -LiteralPath $logDirectory))
{
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin)
{
    Write-Warning 'NOT elevated - ACL denials will mask the sharing state. Re-run from an elevated PowerShell.'
}

# The callbacks run on thread-pool threads. They only take the time and the size and put a line in a
# queue; the main loop writes the lines. Windows PowerShell 5.1 compiles this as C# 5.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

public sealed class DatWatcherSet : IDisposable
{
    private readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
    private readonly FileSystemWatcher[] _watchers;
    private readonly string _path;
    private int _errors;

    public DatWatcherSet(string path)
    {
        _path = path;
        string directory = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        _watchers = new FileSystemWatcher[]
        {
            Create(directory, name, NotifyFilters.Size, "size"),
            Create(directory, name, NotifyFilters.LastWrite, "lastwrite"),
            Create(directory, name,
                NotifyFilters.Attributes | NotifyFilters.CreationTime | NotifyFilters.Security
                | NotifyFilters.FileName | NotifyFilters.LastAccess,
                "other")
        };
    }

    public int Errors
    {
        get { return Volatile.Read(ref _errors); }
    }

    public bool TryDequeue(out string line)
    {
        return _lines.TryDequeue(out line);
    }

    public void Dispose()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }

    private FileSystemWatcher Create(string directory, string name, NotifyFilters filter, string tag)
    {
        FileSystemWatcher watcher = new FileSystemWatcher(directory, name);
        watcher.NotifyFilter = filter;
        watcher.IncludeSubdirectories = false;
        watcher.InternalBufferSize = 65536;
        FileSystemEventHandler handler = (sender, e) => Enqueue(tag, e.ChangeType.ToString());
        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += (sender, e) => Enqueue(tag, "Renamed " + e.OldName + " -> " + e.Name);
        watcher.Error += (sender, e) =>
        {
            Interlocked.Increment(ref _errors);
            Exception exception = e.GetException();
            Enqueue(tag, "ERROR " + exception.GetType().Name + ": " + exception.Message);
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Enqueue(string tag, string change)
    {
        DateTime raisedAt = DateTime.Now;
        string size;
        try
        {
            size = new FileInfo(_path).Length.ToString();
        }
        catch (Exception exception)
        {
            size = "error:" + exception.GetType().Name;
        }

        _lines.Enqueue(raisedAt.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | FSW | " + tag + " | " + change + " | size=" + size);
    }
}
'@

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
    param([string]$Line)
    Write-Host $Line
    Add-Content -LiteralPath $script:LogPath -Value $Line
}

function Write-StampedLine
{
    param([string]$Message)
    Write-TimelineLine "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff') | $Message"
}

function Write-QueuedWatcherLines
{
    $line = $null
    while ($script:watchers.TryDequeue([ref]$line))
    {
        Write-TimelineLine $line
    }
}

$watchers = New-Object DatWatcherSet -ArgumentList $DatPath
Write-StampedLine "monitor started | elevated=$isAdmin | interval=${IntervalSeconds}s | probe=$(-not $NoProbe) | stat=$(-not $NoStat) | watchers=size,lastwrite,other | dat=$DatPath"

$previous = ''
$lastHeartbeat = Get-Date
$lastProbe = [DateTime]::MinValue
try
{
    while ($true)
    {
        Write-QueuedWatcherLines

        $now = Get-Date
        if (($now - $lastProbe).TotalSeconds -ge $IntervalSeconds)
        {
            $lastProbe = $now
            $probe = if ($NoProbe) { 'off' } else { Get-ProbeState -Path $DatPath }
            $procs = (Get-Process -Name LotroLauncher, lotroclient, lotroclient64 -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join '+'
            if (-not $procs) { $procs = 'none' }
            $size = 'off'
            $mtime = 'off'
            if (-not $NoStat)
            {
                $dat = Get-Item -LiteralPath $DatPath -ErrorAction SilentlyContinue
                $size = if ($dat) { $dat.Length } else { 'missing' }
                $mtime = if ($dat) { $dat.LastWriteTime.ToString('HH:mm:ss.fff') } else { '-' }
            }
            $current = "probe=$probe | procs=$procs | size=$size | mtime=$mtime"

            if ($current -ne $previous)
            {
                Write-StampedLine "CHANGE | $current"
                $previous = $current
                $lastHeartbeat = $now
            }
            elseif (($now - $lastHeartbeat).TotalSeconds -ge $HeartbeatSeconds)
            {
                Write-StampedLine "heartbeat | $current | watcher-errors=$($watchers.Errors)"
                $lastHeartbeat = $now
            }
        }

        Start-Sleep -Milliseconds 100
    }
}
finally
{
    Write-QueuedWatcherLines
    Write-StampedLine "monitor stopped | watcher-errors=$($watchers.Errors)"
    $watchers.Dispose()
}
