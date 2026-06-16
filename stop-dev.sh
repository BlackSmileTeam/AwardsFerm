#!/usr/bin/env bash
# Остановка локальных процессов AwardsFerm на Linux.
set -euo pipefail

pkill -f 'AwardsFerm.Api' 2>/dev/null || true
pkill -f 'AwardsFerm.Worker' 2>/dev/null || true
pkill -f 'vite.*AwardsFerm.Web' 2>/dev/null || true
echo "Stopped AwardsFerm API/Worker/Web (if running)."
