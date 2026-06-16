#!/usr/bin/env bash
set -euo pipefail
cd /opt/awardsferm
COMPOSE="docker compose -f docker-compose.production.yml --env-file .env.production"
$COMPOSE exec -T api sqlite3 /var/lib/awardsferm/awardsferm.db "UPDATE session_slots SET ProxyEnabled=1;"
$COMPOSE exec -T api sqlite3 /var/lib/awardsferm/awardsferm.db "SELECT ProfileId, ProxyEnabled FROM session_slots;"
