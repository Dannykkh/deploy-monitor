@echo off
setlocal enabledelayedexpansion

REM ============================================
REM DeployMonitor deploy.bat - Build & Restart
REM ============================================

set "SCRIPT_DIR=%~dp0"
set "PROJECT_NAME=DeployMonitor"
set "CSPROJ=%SCRIPT_DIR%DeployMonitor\DeployMonitor.csproj"
set "PUBLISH_DIR=%SCRIPT_DIR%publish"
set "EXE_NAME=DeployMonitor.exe"

echo.
echo ============================================
echo   [%PROJECT_NAME%] Auto Deploy Start
echo   Time: %date% %time%
echo ============================================

REM ========================================
REM [1/4] Build
REM ========================================
echo.
echo [1/4] Building %PROJECT_NAME%...
dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%PUBLISH_DIR%" 2>&1
if not exist "%PUBLISH_DIR%\%EXE_NAME%" (
    echo [ERROR] Build failed - %EXE_NAME% not found
    exit /b 1
)
echo       Build complete

REM ========================================
REM [2/4] Stop running instance
REM ========================================
echo.
echo [2/4] Stopping running %EXE_NAME%...
taskkill /IM %EXE_NAME% /F >nul 2>&1
if not errorlevel 1 (
    echo       Process stopped
    timeout /t 2 /nobreak >nul
) else (
    echo       No running instance found
)

REM ========================================
REM [3/4] Copy to install location
REM ========================================
echo.
echo [3/4] Deploying %EXE_NAME%...

REM Install location: same as current running exe, or fallback to script dir
set "INSTALL_DIR=%SCRIPT_DIR%"

copy /Y "%PUBLISH_DIR%\%EXE_NAME%" "%INSTALL_DIR%\%EXE_NAME%" >nul
if errorlevel 1 (
    echo [ERROR] Copy failed
    exit /b 2
)
echo       Deployed to %INSTALL_DIR%

REM ========================================
REM [4/4] Restart
REM ========================================
echo.
echo [4/4] Starting %EXE_NAME%...
start "" "%INSTALL_DIR%\%EXE_NAME%"
echo       Started

echo.
echo ============================================
echo   [%PROJECT_NAME%] Deploy Complete
echo   Time: %date% %time%
echo ============================================

exit /b 0
