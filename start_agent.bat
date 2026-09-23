@echo off
setlocal
cd /d "%~dp0"
chcp 65001 >nul
title NEWERP AI Agent
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\agent-host.ps1"
set "EXITCODE=%ERRORLEVEL%"
echo.
echo NEWERP AI Agent exited with code %EXITCODE%.
pause
exit /b %EXITCODE%
