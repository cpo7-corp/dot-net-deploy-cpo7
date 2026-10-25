#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "=================================================="
echo "  NET Deploy - Starting (Linux)..."
echo "=================================================="

# --------------------------------------------------
# [0] Clean up any old processes on ports 6234 and 5432
# --------------------------------------------------
echo "[0/2] Checking and freeing ports (6234, 5432)..."
for port in 6234 5432; do
    if command -v fuser >/dev/null 2>&1; then
        fuser -k "${port}/tcp" >/dev/null 2>&1 || true
    else
        pids=$(lsof -ti :"${port}" 2>/dev/null || true)
        if [ -n "$pids" ]; then
            kill -9 $pids 2>/dev/null || true
        fi
    fi
done

export ASPNETCORE_URLS="http://0.0.0.0:6234"

# --------------------------------------------------
# [1/2] Starting API Server (Port 6234)
# --------------------------------------------------
echo ""
echo "[1/2] Starting API Server (Port 6234)..."

if [ -f "$SCRIPT_DIR/server/NET.Deploy.Api.dll" ]; then
    (cd "$SCRIPT_DIR/server" && dotnet NET.Deploy.Api.dll) &
elif [ -f "$SCRIPT_DIR/server/NET.Deploy.Api" ]; then
    (cd "$SCRIPT_DIR/server" && ./NET.Deploy.Api) &
elif [ -f "$SCRIPT_DIR/net-deploy-server/net-deploy-api/bin/Release/net10.0/NET.Deploy.Api.dll" ]; then
    (cd "$SCRIPT_DIR/net-deploy-server/net-deploy-api/bin/Release/net10.0" && dotnet NET.Deploy.Api.dll) &
elif [ -f "$SCRIPT_DIR/net-deploy-server/net-deploy-api/bin/Debug/net10.0/NET.Deploy.Api.dll" ]; then
    (cd "$SCRIPT_DIR/net-deploy-server/net-deploy-api/bin/Debug/net10.0" && dotnet NET.Deploy.Api.dll) &
else
    (cd "$SCRIPT_DIR/net-deploy-server/net-deploy-api" && dotnet run) &
fi
API_PID=$!

# --------------------------------------------------
# [2/2] Starting UI Server (Port 5432)
# --------------------------------------------------
echo ""
echo "[2/2] Starting UI Server (Port 5432)..."

if [ -d "$SCRIPT_DIR/ui" ]; then
    echo "  Serving UI from $SCRIPT_DIR/ui..."
    npx -y serve -s "$SCRIPT_DIR/ui" -l 5432 --no-clipboard &
elif [ -d "$SCRIPT_DIR/net-deploy-ui/dist/net-deploy-ui/browser" ]; then
    echo "  Serving UI from dist/net-deploy-ui/browser..."
    npx -y serve -s "$SCRIPT_DIR/net-deploy-ui/dist/net-deploy-ui/browser" -l 5432 --no-clipboard &
else
    (cd "$SCRIPT_DIR/net-deploy-ui" && npm start) &
fi
UI_PID=$!

echo ""
echo "=================================================="
echo "  Done! Open your browser:"
echo "  http://localhost:5432/deploy"
echo "=================================================="
echo "Press Ctrl+C to stop both processes."

# Clean shutdown on Ctrl+C or kill
cleanup() {
    echo ""
    echo "Shutting down NET Deploy processes..."
    kill -TERM $API_PID $UI_PID 2>/dev/null || true
    exit 0
}
trap cleanup SIGINT SIGTERM
wait
