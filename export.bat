@echo off
dotnet build src\LotroKoniecDev -v:minimal -nologo
if errorlevel 1 exit /b 1
src\LotroKoniecDev\bin\Debug\net10.0\LotroKoniecDev.exe export %*
