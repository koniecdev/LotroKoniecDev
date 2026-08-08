@echo off
setlocal
cd /d "%~dp0"

rem Export opens the DAT read-only (#443 Option A, native flag 6 — the read-only bit is 0x4), so
rem no elevation here. Verified 2026-08-07 against the live Program Files DAT from a non-elevated
rem shell: exit 0, DAT byte-identical (#629). A DatFile.CannotOpen here means the file is not
rem readable at all, not that it needs write access — check the path and the ACL, do not re-add
rem the elevation block.
rem Delayed expansion below inserts user arguments after cmd parsing, so metacharacters
rem stay literal (AUDIT-SEC-06 hardening, kept even without the elevation boundary).
set "LOTRO_WRAPPER_ARGS=%*"

dotnet build src\Patcher\LotroKoniecDev.Cli -v:minimal -nologo
if errorlevel 1 exit /b 1

setlocal EnableDelayedExpansion
if defined LOTRO_WRAPPER_ARGS (
    src\Patcher\LotroKoniecDev.Cli\bin\Debug\net10.0-windows\win-x86\LotroKoniecDev.Cli.exe export !LOTRO_WRAPPER_ARGS!
) else (
    src\Patcher\LotroKoniecDev.Cli\bin\Debug\net10.0-windows\win-x86\LotroKoniecDev.Cli.exe export
)
pause
