@echo off
chcp 65001 >nul 2>&1
cd /d "%~dp0"
set "VIDEOBATCH_HOME=%~dp0app"
if not exist "%~dp0app\VideoBatch.exe" (
    echo Не найден VideoBatch.exe. Распакуйте архив полностью, не отдельные файлы.
    pause
    exit /b 1
)
echo VideoBatch — подготовка...
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Restore-VideoBatchPortable.ps1" -Quiet
if errorlevel 1 (
    echo.
    echo Не удалось подготовить данные. Закройте VideoBatch, если он открыт, и запустите этот файл снова.
    pause
    exit /b 1
)
start "" "%~dp0app\VideoBatch.exe"
