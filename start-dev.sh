#!/usr/bin/env bash
# Запуск AwardsFerm локально на Linux (без Docker).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

export SQLITE_DB_PATH="${SQLITE_DB_PATH:-$ROOT/data/awardsferm.db}"
export BROWSER_HEADLESS="${BROWSER_HEADLESS:-true}"
mkdir -p "$(dirname "$SQLITE_DB_PATH")" profiles

if [ ! -d "src/AwardsFerm.Web/node_modules" ]; then
  echo "npm install..."
  (cd src/AwardsFerm.Web && npm install)
fi

if ! dotnet build src/AwardsFerm.Worker/AwardsFerm.Worker.csproj -v q >/dev/null 2>&1; then
  echo "dotnet build failed"
  exit 1
fi

PW_SH="src/AwardsFerm.Worker/bin/Debug/net8.0/playwright.sh"
if [ -f "$PW_SH" ] && [ ! -d "$HOME/.cache/ms-playwright/chromium-"* ] 2>/dev/null; then
  echo "Installing Playwright Chromium..."
  bash "$PW_SH" install chromium
fi

cleanup() {
  echo "Stopping..."
  jobs -p | xargs -r kill 2>/dev/null || true
}
trap cleanup EXIT INT TERM

echo "API    http://localhost:8080"
echo "Worker http://localhost:8081"
echo "UI     http://localhost:5173"
echo "SQLITE $SQLITE_DB_PATH"

dotnet run --project src/AwardsFerm.Api/AwardsFerm.Api.csproj &
sleep 3
dotnet run --project src/AwardsFerm.Worker/AwardsFerm.Worker.csproj &
(cd src/AwardsFerm.Web && npm run dev) &
wait
