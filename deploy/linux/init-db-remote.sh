#!/usr/bin/env bash
set -euo pipefail
cd /opt/awardsferm

COMPOSE="docker compose -f docker-compose.production.yml --env-file .env.production"

$COMPOSE exec -T api sh -c \
  'apt-get update -qq && apt-get install -y -qq sqlite3 >/dev/null'

COUNT=$($COMPOSE exec -T api \
  sqlite3 /var/lib/awardsferm/awardsferm.db "SELECT COUNT(*) FROM users;")

echo "Users in DB: $COUNT"

if [ "$COUNT" = "0" ]; then
  echo "Applying init-admin.sql..."
  cat deploy/sql/init-admin.sql | $COMPOSE exec -T api \
    sqlite3 /var/lib/awardsferm/awardsferm.db
  echo "Admin created: login=admin password=Admin123!"
else
  echo "Skip init-admin — users already exist"
fi

$COMPOSE exec -T api \
  sqlite3 /var/lib/awardsferm/awardsferm.db "SELECT Id, Login, CreatedAt FROM users LIMIT 5;"
