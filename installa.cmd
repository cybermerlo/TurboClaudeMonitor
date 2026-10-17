@echo off
rem Double-click launcher for installa.ps1 (extra arguments such as -Relogin are passed through).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installa.ps1" %*
echo.
pause
