@echo off
setlocal
cd /d "%~dp0"

echo ==================================================
echo   NET Deploy - Starting...
echo ==================================================

set ASPNETCORE_URLS=http://localhost:6234

echo.
echo [1/2] Starting API Server (Port 6234)...
start "NET Deploy - Server" cmd /k "cd /d "%~dp0server" && NET.Deploy.Api.exe"

echo.
echo [2/2] Starting UI Server (Port 5432)...
start "NET Deploy - UI" cmd /k "npx serve "%~dp0ui" -l 5432 --no-clipboard"

echo.
echo ==================================================
echo   Done! Open your browser:
echo   http://localhost:5432
echo ==================================================
echo.
