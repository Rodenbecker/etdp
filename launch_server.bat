@echo off
setlocal
title ETDP Unified Server Launcher

set "ROOT=%~dp0"
set "BACKEND_DIR=%ROOT%ETDP"
set "FRONTEND_DIR=%ROOT%ETDP\frontend"
set "SERVER_URL=http://localhost:5299"
set "APP_URL=%SERVER_URL%/qualifications"

echo ========================================================
echo        ETDP Unified Server - Distribution Launcher
echo        AI Engine: Google Gemini & Gemma 4:26B
echo ========================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found on PATH. Please install .NET SDK/Runtime 8.0+.
    pause
    exit /b 1
)

:: Check if frontend static distribution exists; build if missing and npm is available
if not exist "%FRONTEND_DIR%\dist\index.html" (
    echo [INFO] Frontend distribution bundle not found. Building with Vite...
    where npm >nul 2>nul
    if not errorlevel 1 (
        cd /d "%FRONTEND_DIR%"
        if not exist "node_modules" (
            echo Installing frontend packages...
            call npm install --no-audit --fund=false
        )
        call npm run build
        cd /d "%ROOT%"
    ) else (
        echo [WARN] npm not found to build frontend dist. Proceeding with backend.
    )
)

echo Checking if ETDP Server is already active on %SERVER_URL%...
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $r = Invoke-WebRequest -UseBasicParsing -Uri '%SERVER_URL%/api/Activation/status' -TimeoutSec 2; if ($r.StatusCode -lt 500) { exit 0 } else { exit 1 } } catch { exit 1 }" >nul 2>nul
if errorlevel 1 (
    echo Starting ETDP Standalone Server at %SERVER_URL%...
    start "ETDP Standalone Server" cmd /k "cd /d ""%BACKEND_DIR%"" && set DOTNET_CLI_TELEMETRY_OPTOUT=1&& set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1&& set ASPNETCORE_ENVIRONMENT=Production&& set ASPNETCORE_URLS=%SERVER_URL%&& set DOTNET_ROLL_FORWARD=Major&& set AI_MODE=hybrid&& set GEMMA_ENABLED=true&& set GEMMA_MODEL=gemma4:26b&& set GEMMA_BASE_URL=http://127.0.0.1:8099&& set SMI_ENABLED=true&& set SMI_BASE_URL=http://127.0.0.1:8099&& dotnet run --no-launch-profile --urls %SERVER_URL%"

    echo Waiting for ETDP Server to become ready...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$deadline=(Get-Date).AddSeconds(45); do { try { $r=Invoke-WebRequest -UseBasicParsing -Uri '%SERVER_URL%/api/Activation/status' -TimeoutSec 2; if ($r.StatusCode -lt 500) { exit 0 } } catch {}; Start-Sleep -Milliseconds 750 } while ((Get-Date) -lt $deadline); exit 1"
    if errorlevel 1 (
        echo [WARN] ETDP Server did not signal ready within 45s. Check the Server window.
    )
) else (
    echo ETDP Server is already running.
)

echo.
echo Opening ETDP in browser: %APP_URL%
start "" "%APP_URL%"

echo.
echo ETDP Unified Server is running at %SERVER_URL%
echo Press any key to stop this launcher window (server will remain running in its window).
pause >nul
