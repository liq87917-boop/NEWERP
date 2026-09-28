@echo off
setlocal
cd /d "%~dp0"
chcp 65001 >nul
title NEWERP Local Development Console
mode con: cols=150 lines=46 >nul 2>nul
cls
echo ============================================================
echo  NEWERP LOCAL DEVELOPMENT CONSOLE
echo ============================================================
echo  ChatGPT plan ^> GitHub ^> Scheduler ^> DeepSeek ^> Build ^> Push
echo  Repository: %CD%
echo  Close this window or press Ctrl+C to stop the local scheduler.
echo.
echo  Loading task plan, execution results and GitHub status...
echo.

rem Local development runtime. Values identify the provider/model only; secrets remain in ignored env files.
set AI_CLINE_PROVIDER=deepseek
set AI_CLINE_MODEL=deepseek-v4-pro
set PYTHONUTF8=1
set DOTNET_NOLOGO=true
set DOTNET_CLI_TELEMETRY_OPTOUT=true
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true

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
echo NEWERP local development console exited with code %EXITCODE%.
timeout /t 3 /nobreak >nul
exit /b %EXITCODE%
