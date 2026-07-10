@echo off
setlocal
cd /d "%~dp0"

rem AUDIT-SEC-06: user arguments must never be re-parsed by an elevated shell. They cross
rem the elevation boundary as an environment variable (inherited by the elevated instance),
rem and delayed expansion below inserts them after cmd parsing, so metacharacters stay literal.
if not "%~1"=="--elevated" set "LOTRO_WRAPPER_ARGS=%*"

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '--elevated' -Verb RunAs"
    exit /b
)

dotnet build src\Patcher\LotroKoniecDev.Cli -v:minimal -nologo
if errorlevel 1 exit /b 1

setlocal EnableDelayedExpansion
if defined LOTRO_WRAPPER_ARGS (
    src\Patcher\LotroKoniecDev.Cli\bin\Debug\net10.0-windows\win-x86\LotroKoniecDev.Cli.exe export !LOTRO_WRAPPER_ARGS!
) else (
    src\Patcher\LotroKoniecDev.Cli\bin\Debug\net10.0-windows\win-x86\LotroKoniecDev.Cli.exe export
)
pause
