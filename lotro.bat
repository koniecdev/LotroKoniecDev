@echo off
setlocal DisableDelayedExpansion
rem Delayed expansion stays off, even when the registry turns it on, so the arguments and this
rem path keep every exclamation mark and caret. Read the path before the cd, which can change it.
set "LOTRO_WRAPPER_SELF=%~f0"
cd /d "%~dp0"

rem AUDIT-SEC-06: the relaunch as administrator must never make an elevated shell parse the
rem arguments. An elevated process does not get this window's variables (#658), so the
rem arguments and this script's path cross as Base64 text. The elevated PowerShell decodes
rem them and clears the variable first, so a saved user variable is never used. Inside its
rem quotes, the relaunch line must have no double quote, percent sign or exclamation mark,
rem and only single spaces. Delayed expansion below inserts the arguments after cmd parsing.
if not "%~1"=="--elevated" set "LOTRO_WRAPPER_ARGS=%*"

net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator privileges...
    powershell -NoProfile -Command "$u=[Text.Encoding]::UTF8;$b={param($s)[Convert]::ToBase64String($u.GetBytes([string]$s))};$inner='$u=[Text.Encoding]::UTF8;$d={param($s)$u.GetString([Convert]::FromBase64String($s))};$a=(& $d '''+(& $b $env:LOTRO_WRAPPER_ARGS)+''');$env:LOTRO_WRAPPER_ARGS=$null;if($a){$env:LOTRO_WRAPPER_ARGS=$a};& (& $d '''+(& $b $env:LOTRO_WRAPPER_SELF)+''') --elevated';Start-Process -FilePath powershell -ArgumentList ('-NoProfile -Command '+$inner) -Verb RunAs"
    exit /b
)

dotnet build src\Patcher\LotroKoniecDev.Cli -v:minimal -nologo
if errorlevel 1 exit /b 1

setlocal EnableDelayedExpansion
if defined LOTRO_WRAPPER_ARGS (
    src\Patcher\LotroKoniecDev.Cli\bin\Debug\net10.0-windows\win-x86\LotroKoniecDev.Cli.exe launch !LOTRO_WRAPPER_ARGS!
) else (
    src\Patcher\LotroKoniecDev.Cli\bin\Debug\net10.0-windows\win-x86\LotroKoniecDev.Cli.exe launch
)
pause
