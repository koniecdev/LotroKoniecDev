@echo off
setlocal enabledelayedexpansion

set "DAT_FILE=client_local_English.dat"

if "%~1" neq "" (
    set "LOTRO_PATH=%~1"
    goto :check_dat
)

set "LOTRO_PATH=%ProgramFiles(x86)%\StandingStoneGames\The Lord of the Rings Online"
if exist "!LOTRO_PATH!\!DAT_FILE!" goto :check_dat

set "LOTRO_PATH=%ProgramFiles(x86)%\Steam\steamapps\common\The Lord of the Rings Online"

:check_dat
if not exist "!LOTRO_PATH!\!DAT_FILE!" (
    echo ERROR: LOTRO installation not found at: !LOTRO_PATH!
    echo Usage: lotro.bat [path_to_lotro_folder]
    exit /b 1
)

echo === LOTRO Polish Patcher - Launch Helper ===
echo Path: !LOTRO_PATH!
echo.

echo Setting !DAT_FILE! to read-only (protects translations from launcher^)...
attrib +R "!LOTRO_PATH!\!DAT_FILE!"

set "LAUNCHER="
if exist "!LOTRO_PATH!\LotroLauncher.exe" set "LAUNCHER=!LOTRO_PATH!\LotroLauncher.exe"
if not defined LAUNCHER if exist "!LOTRO_PATH!\TurbineLauncher.exe" set "LAUNCHER=!LOTRO_PATH!\TurbineLauncher.exe"

if not defined LAUNCHER (
    echo ERROR: No launcher found in !LOTRO_PATH!
    attrib -R "!LOTRO_PATH!\!DAT_FILE!"
    exit /b 1
)

echo Launching: !LAUNCHER!
echo.
echo DAT file is READ-ONLY. Launcher cannot overwrite translations.
echo Write access will be restored when the launcher closes.
echo.

start /wait "" "!LAUNCHER!"

echo.
echo Restoring write access to !DAT_FILE!...
attrib -R "!LOTRO_PATH!\!DAT_FILE!"
echo Done.

endlocal
