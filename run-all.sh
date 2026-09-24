#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "=================================================="
echo "  NET Deploy - Starting (Linux)..."
echo "=================================================="

export ASPNETCORE_URLS="http://0.0.0.0:6234"

echo ""
echo "[1/2] Starting API Server (Port 6234)..."
if [ -f "$SCRIPT_DIR/server/NET.Deploy.Api.dll" ]; then
    dotnet "$SCRIPT_DIR/server/NET.Deploy.Api.dll" &
elif [ -f "$SCRIPT_DIR/server/NET.Deploy.Api" ]; then
    "$SCRIPT_DIR/server/NET.Deploy.Api" &
elif [ -f "$SCRIPT_DIR/net-deploy-server/net-deploy-api/bin/Release/net10.0/NET.Deploy.Api.dll" ]; then
    dotnet "$SCRIPT_DIR/net-deploy-server/net-deploy-api/bin/Release/net10.0/NET.Deploy.Api.dll" &
elif [ -f "$SCRIPT_DIR/net-deploy-server/net-deploy-api/bin/Debug/net10.0/NET.Deploy.Api.dll" ]; then
    dotnet "$SCRIPT_DIR/net-deploy-server/net-deploy-api/bin/Debug/net10.0/NET.Deploy.Api.dll" &
else
    (cd "$SCRIPT_DIR/net-deploy-server/net-deploy-api" && dotnet run) &
fi
API_PID=$!

echo ""
echo "[2/2] Starting UI Server (Port 5432)..."
if [ -d "$SCRIPT_DIR/ui" ]; then
    npx serve "$SCRIPT_DIR/ui" -l 5432 --no-clipboard &
elif [ -d "$SCRIPT_DIR/net-deploy-ui/dist/net-deploy-ui/browser" ]; then
    npx serve "$SCRIPT_DIR/net-deploy-ui/dist/net-deploy-ui/browser" -l 5432 --no-clipboard &
else
    (cd "$SCRIPT_DIR/net-deploy-ui" && npm start) &
fi
UI_PID=$!

echo ""
echo "=================================================="
echo "  Done! Open your browser:"
echo "  http://localhost:5432"
echo "=================================================="
echo "Press Ctrl+C to stop both processes."

# Graceful shutdown on Ctrl+C or kill
trap "kill -TERM $API_PID $UI_PID 2>/dev/null || true; exit 0" SIGINT SIGTERM EXIT
wait