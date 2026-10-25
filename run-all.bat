@echo off
setlocal
cd /d "%~dp0"

echo ==================================================
echo   NET Deploy - Starting (Windows)...
echo ==================================================

set ASPNETCORE_URLS=http://localhost:6234

echo.
echo [1/2] Starting API Server (Port 6234)...
if exist "%~dp0server\NET.Deploy.Api.exe" (
    start "NET Deploy - Server" cmd /k "cd /d "%~dp0server" && NET.Deploy.Api.exe"
) else if exist "%~dp0net-deploy-server\net-deploy-api\bin\Release\net10.0\NET.Deploy.Api.exe" (
    start "NET Deploy - Server" cmd /k "cd /d "%~dp0net-deploy-server\net-deploy-api\bin\Release\net10.0" && NET.Deploy.Api.exe"
) else if exist "%~dp0net-deploy-server\net-deploy-api\bin\Debug\net10.0\NET.Deploy.Api.exe" (
    start "NET Deploy - Server" cmd /k "cd /d "%~dp0net-deploy-server\net-deploy-api\bin\Debug\net10.0" && NET.Deploy.Api.exe"
) else (
    start "NET Deploy - Server" cmd /k "cd /d "%~dp0net-deploy-server\net-deploy-api" && dotnet run"
)

echo.
echo [2/2] Starting UI Server (Port 5432)...
if exist "%~dp0ui" (
    start "NET Deploy - UI" cmd /k "npx -y serve -s "%~dp0ui" -l 5432 --no-clipboard"
) else if exist "%~dp0net-deploy-ui\dist\net-deploy-ui\browser" (
    start "NET Deploy - UI" cmd /k "npx -y serve -s "%~dp0net-deploy-ui\dist\net-deploy-ui\browser" -l 5432 --no-clipboard"
) else (
    start "NET Deploy - UI" cmd /k "cd /d "%~dp0net-deploy-ui" && npm start"
)

echo.
echo ==================================================
echo   Done! Open your browser:
echo   http://localhost:5432/deploy
echo ==================================================
echo.
