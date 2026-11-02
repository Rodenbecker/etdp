@echo off
setlocal
title ETDP Distribution Publisher

set "ROOT=%~dp0"
set "BACKEND_DIR=%ROOT%ETDP"
set "FRONTEND_DIR=%ROOT%ETDP\frontend"
set "OUTPUT_DIR=%ROOT%distribution\etdp_server"

echo ========================================================
echo        ETDP Standalone Distribution Publisher
echo        AI Engine: Google Gemini & Gemma 4:26B
echo ========================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found on PATH.
    pause
    exit /b 1
)

where npm >nul 2>nul
if errorlevel 1 (
    echo [ERROR] npm was not found on PATH.
    pause
    exit /b 1
)

echo [1/3] Building frontend production bundle...
cd /d "%FRONTEND_DIR%"
call npm run build
if errorlevel 1 (
    echo [ERROR] Frontend build failed.
    pause
    exit /b 1
)

echo.
echo [2/3] Publishing ASP.NET Core server...
cd /d "%BACKEND_DIR%"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet publish ETDP.csproj -c Release -o "%OUTPUT_DIR%"
if errorlevel 1 (
    echo [ERROR] Backend publish failed.
    pause
    exit /b 1
)

echo.
echo [3/3] Packaging static assets and configuration...
if not exist "%OUTPUT_DIR%\wwwroot" mkdir "%OUTPUT_DIR%\wwwroot"
xcopy /E /Y /I "%FRONTEND_DIR%\dist\*" "%OUTPUT_DIR%\wwwroot\" >nul

if exist "%BACKEND_DIR%\etdp.db" (
    echo Copying database template...
    copy /Y "%BACKEND_DIR%\etdp.db" "%OUTPUT_DIR%\etdp.db" >nul
)

if exist "%BACKEND_DIR%\google.env" (
    copy /Y "%BACKEND_DIR%\google.env" "%OUTPUT_DIR%\google.env" >nul
)

:: Create standalone run script in the output folder
(
echo @echo off
echo title ETDP Portable Server
echo set "SERVER_URL=http://localhost:5299"
echo echo Starting ETDP Portable Server on %%SERVER_URL%%...
echo start "" "%%SERVER_URL%%/qualifications"
echo set ASPNETCORE_ENVIRONMENT=Production
echo set ASPNETCORE_URLS=%%SERVER_URL%%
echo set AI_MODE=hybrid
echo set GEMMA_MODEL=gemma4:26b
echo .\ETDP.exe
echo pause
) > "%OUTPUT_DIR%\run_server.bat"

echo.
echo ========================================================
echo [SUCCESS] Distribution package created at:
echo %OUTPUT_DIR%
echo.
echo Users can run: %OUTPUT_DIR%\run_server.bat
echo ========================================================
pause
