@echo off
chcp 65001 >nul
setlocal

rem AcadHttpServer: removes the per-user installation made by install.cmd.
rem AutoCADHttp.settings.json lives inside the bundle and is removed too - copy it first if you need it.
rem Usage: uninstall.cmd [/quiet]   /quiet - do not wait for a key press at the end.

set "PAUSE_AT_END=1"
if /i "%~1"=="/quiet" set "PAUSE_AT_END="

if not defined APPDATA (
    echo Переменная APPDATA не задана.
    goto :fail
)

set "BUNDLE=%APPDATA%\Autodesk\ApplicationPlugins\AcadHttpServer.bundle"

rem Paths are echoed outside of ( ) blocks: a ")" in a user profile path would end the block early.
if exist "%BUNDLE%" goto :remove
echo AcadHttpServer не установлен: %BUNDLE% не найден.
goto :done

:remove
rmdir /s /q "%BUNDLE%"
if not exist "%BUNDLE%" goto :removed
echo Не удалось удалить %BUNDLE%. Закройте AutoCAD и запустите uninstall.cmd снова.
goto :fail

:removed

echo Удалено: %BUNDLE%
echo Готово.

:done
if defined PAUSE_AT_END pause
exit /b 0

:fail
if defined PAUSE_AT_END pause
exit /b 1
