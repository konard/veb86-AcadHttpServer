@echo off
chcp 65001 >nul
setlocal

rem AcadHttpServer: per-user installation for AutoCAD 2020-2024, administrator rights are not required.
rem Copies AppData\Roaming from the archive into %APPDATA% of the current user:
rem   %APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle  (AutoCAD loads it automatically)
rem An existing AutoCADHttp.settings.json is kept, so address, port and widgetsDirectory survive updates.
rem Usage: install.cmd [/quiet]   /quiet - do not wait for a key press at the end.

set "PAUSE_AT_END=1"
if /i "%~1"=="/quiet" set "PAUSE_AT_END="

if not defined APPDATA (
    echo Переменная APPDATA не задана. Запустите install.cmd от имени обычного пользователя Windows.
    goto :fail
)

set "SOURCE=%~dp0AppData\Roaming"
set "BUNDLE=%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle"
set "SETTINGS=%BUNDLE%\Contents\AutoCAD2020-2024\AutoCADHttp.settings.json"

if not exist "%SOURCE%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle\PackageContents.xml" (
    echo Не найдена папка AppData рядом с install.cmd. Распакуйте архив полностью.
    goto :fail
)

set "KEEP_SETTINGS="
if exist "%SETTINGS%" set "KEEP_SETTINGS=/XF AutoCADHttp.settings.json"

echo Установка AcadHttpServer в %APPDATA% ...
robocopy "%SOURCE%" "%APPDATA%" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP %KEEP_SETTINGS%
if errorlevel 8 (
    echo Ошибка копирования. Закройте AutoCAD и запустите install.cmd снова.
    goto :fail
)

rem Files extracted from a downloaded ZIP may be marked as coming from the internet; .NET may refuse such DLLs.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath $env:BUNDLE -Recurse -File | Unblock-File" >nul 2>&1

echo.
echo Готово.
rem Paths are echoed outside of ( ) blocks: a ")" in a user profile path would end the block early.
set "SETTINGS_NOTE=Настройки"
if defined KEEP_SETTINGS set "SETTINGS_NOTE=Настройки сохранены"
echo   AutoCAD 2020-2024: %BUNDLE%
echo   %SETTINGS_NOTE%: %SETTINGS%
echo Запустите AutoCAD и выполните команду HTTPSTART. Подробности - в файле INSTALL.md.
if defined PAUSE_AT_END pause
exit /b 0

:fail
if defined PAUSE_AT_END pause
exit /b 1
