#!/usr/bin/env bash
# Первичная подготовка Linux VPS для AwardsFerm (Ubuntu/Debian).
set -euo pipefail

DEPLOY_PATH="${1:-/opt/awardsferm}"

echo "==> Install Docker (if missing)"
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh
  systemctl enable --now docker
fi

echo "==> Directories"
mkdir -p "$DEPLOY_PATH/profiles"
mkdir -p /var/lib/awardsferm
chmod -R a+rwX "$DEPLOY_PATH/profiles" 2>/dev/null || true

echo "==> .env.production"
if [ ! -f "$DEPLOY_PATH/.env.production" ]; then
  if [ -f "$DEPLOY_PATH/.env.production.example" ]; then
    cp "$DEPLOY_PATH/.env.production.example" "$DEPLOY_PATH/.env.production"
  else
  cat >"$DEPLOY_PATH/.env.production" <<EOF
DEPLOY_PATH=$DEPLOY_PATH
FRONTEND_PORT=55502
API_PORT=55501
JWT_SECRET=$(openssl rand -base64 48)
BROWSER_HEADLESS=true
EOF
  fi
  chmod 600 "$DEPLOY_PATH/.env.production"
  echo "Created $DEPLOY_PATH/.env.production — review JWT_SECRET and ports"
fi

echo "==> Init admin (run once after first API start)"
echo "  sqlite3 /var/lib/awardsferm/awardsferm.db < deploy/sql/init-admin.sql"
echo "  (or copy from container volume after first compose up)"

echo "==> Done. Deploy with:"
echo "  cd $DEPLOY_PATH && docker compose -f docker-compose.production.yml --env-file .env.production up -d --build"
