@echo off
net session >nul 2>&1
if errorlevel 1 (
    echo Requesting administrator privileges...
    powershell -Command "Start-Process cmd -Verb RunAs -ArgumentList '/c cd /d \"%CD%\" && \"%~f0\" %*'"
    exit /b
)
dotnet build src\LotroKoniecDev.Cli -v:minimal -nologo
if errorlevel 1 exit /b 1
src\LotroKoniecDev.Cli\bin\Debug\net10.0-windows\LotroKoniecDev.Cli.exe patch %*
pause
