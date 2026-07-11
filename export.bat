@echo off
setlocal
cd /d "%~dp0"

rem Export opens the DAT read-only (#443 Option A, native flag 2), so no elevation here.
rem If a live-DAT export ever fails with DatFile.CannotOpen, run it from an elevated shell
rem and report on #443 — that would disprove the read-only-flag assumption.
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
