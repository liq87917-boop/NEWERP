@echo off
setlocal
cd /d "%~dp0"
chcp 65001 >nul
title NEWERP AI Agent
cls
echo ============================================================
echo  NEWERP AI AGENT
echo ============================================================
echo  Starting local agent...
echo  Repository: %CD%
echo.
echo  Loading project status...
echo.

:run
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\agent-bootstrap.ps1"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\agent-host.ps1"
set "EXITCODE=%ERRORLEVEL%"

if "%EXITCODE%"=="75" (
    echo.
    echo NEWERP AI Agent updated. Restarting...
    timeout /t 1 /nobreak >nul
    goto run
)

echo.
echo NEWERP AI Agent exited with code %EXITCODE%.
pause
exit /b %EXITCODE%
