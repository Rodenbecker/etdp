@echo off
setlocal
title ETDP Launcher

set "ROOT=%~dp0"
set "BACKEND_DIR=%ROOT%ETDP"
set "APP_DIR=%ROOT%ETDP\frontend"
set "BACKEND_URL=http://localhost:5299"
set "APP_URL=http://127.0.0.1:5173/qualifications"

if not exist "%APP_DIR%\package.json" (
    echo ETDP frontend package.json was not found:
    echo %APP_DIR%\package.json
    pause
    exit /b 1
)

if not exist "%BACKEND_DIR%\ETDP.csproj" (
    echo ETDP backend project was not found:
    echo %BACKEND_DIR%\ETDP.csproj
    pause
    exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo dotnet was not found on PATH. Please install the .NET SDK, then run this launcher again.
    pause
    exit /b 1
)

where npm >nul 2>nul
if errorlevel 1 (
    echo npm was not found on PATH. Please install Node.js, then run this launcher again.
    pause
    exit /b 1
)

echo Checking ETDP backend at %BACKEND_URL%
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $r = Invoke-WebRequest -UseBasicParsing -Uri '%BACKEND_URL%/api/Activation/status' -TimeoutSec 2; if ($r.StatusCode -lt 500) { exit 0 } else { exit 1 } } catch { exit 1 }" >nul 2>nul
if errorlevel 1 (
    echo Starting ETDP backend API at %BACKEND_URL%
    start "ETDP Backend API" cmd /k "cd /d ""%BACKEND_DIR%"" && set DOTNET_CLI_TELEMETRY_OPTOUT=1&& set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1&& set ASPNETCORE_ENVIRONMENT=Development&& set ASPNETCORE_URLS=%BACKEND_URL%&& set DOTNET_ROLL_FORWARD=Major&& set AI_MODE=hybrid&& set OCR_ENGINE=tesseract&& set GEMMA_ENABLED=true&& set GEMMA_MODEL=gemma4:26b&& set LOCAL_LLM_MODEL=gemma4:26b&& set GEMINI_MODEL=gemini-3.8-flash&& set GEMINI_TTS_MODEL=gemini-3.8-flash-tts&& set GEMMA_BASE_URL=http://127.0.0.1:8099&& set SMI_ENABLED=true&& set SMI_BASE_URL=http://127.0.0.1:8099&& set SMI_TIMEOUT_SECONDS=4&& dotnet run --no-launch-profile --urls %BACKEND_URL%"

    echo Waiting for ETDP backend to become ready...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$deadline=(Get-Date).AddSeconds(75); do { try { $r=Invoke-WebRequest -UseBasicParsing -Uri '%BACKEND_URL%/api/Activation/status' -TimeoutSec 2; if ($r.StatusCode -lt 500) { exit 0 } } catch {}; Start-Sleep -Milliseconds 750 } while ((Get-Date) -lt $deadline); exit 1"
    if errorlevel 1 (
        echo ETDP backend did not become ready in time. Check the "ETDP Backend API" window for details.
        pause
        exit /b 1
    )
) else (
    echo ETDP backend is already running.
)

cd /d "%APP_DIR%"

if not exist "%APP_DIR%\node_modules" (
    echo Installing ETDP frontend dependencies. This runs only when node_modules is missing.
    call npm install --no-audit --fund=false
    if errorlevel 1 (
        echo ETDP dependency installation failed.
        pause
        exit /b 1
    )
)

echo Starting ETDP at %APP_URL%
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $r = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5173' -TimeoutSec 2; if ($r.StatusCode -lt 500) { exit 0 } else { exit 1 } } catch { exit 1 }" >nul 2>nul
if not errorlevel 1 (
    echo ETDP frontend is already running. Opening the Qualification Home Page.
    start "" "%APP_URL%"
    exit /b 0
)

powershell -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Start-Sleep -Seconds 4; Start-Process '%APP_URL%'" >nul 2>nul
call npm run dev -- --host 127.0.0.1 --port 5173

echo.
echo ETDP launcher stopped.
pause
