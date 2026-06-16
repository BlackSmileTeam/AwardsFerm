# AwardsFerm

Админ-панель (React) + API + Worker (Playwright). Сессии работают в фоне (headless), без live-экрана в UI.

## Быстрый старт

### Linux (рекомендуется для production)

```bash
# Клонировать на VPS
git clone git@github.com:BlackSmileTeam/AwardsFerm.git /opt/awardsferm
cd /opt/awardsferm

# Подготовка (Docker, каталоги, .env)
sudo bash deploy/linux/setup-server.sh /opt/awardsferm
# Отредактируйте .env.production — обязательно JWT_SECRET

docker compose -f docker-compose.production.yml --env-file .env.production up -d --build

# Первый админ (после первого запуска API)
docker compose -f docker-compose.production.yml exec api sh -c \
  'apt-get update && apt-get install -y sqlite3 && sqlite3 /var/lib/awardsferm/awardsferm.db' < deploy/sql/init-admin.sql
```

| Сервис | URL | Порт |
|--------|-----|------|
| Админ-панель | http://HOST:55502 | 55502 |
| API | http://HOST:55501 | 55501 |

Логин по умолчанию: `admin` / `Admin123!` — смените во вкладке «Профиль».

Подробнее: [deploy/DEPLOY.md](deploy/DEPLOY.md)

### Linux — локальная разработка (без Docker)

```bash
chmod +x start-dev.sh stop-dev.sh
./start-dev.sh
# UI: http://localhost:5173
```

```bash
export SQLITE_DB_PATH=./data/awardsferm.db
export BROWSER_HEADLESS=true   # false — окно Chromium на рабочем столе
```

### Windows — локальная разработка

```powershell
.\start-dev.ps1
# UI: http://localhost:5173
```

Перед пересборкой Worker: `.\stop-dev.ps1`

## Админ-панель

| Вкладка | Назначение |
|---------|------------|
| Прибыль | RSYA: сегодня / месяц по аккаунтам |
| Аккаунты | CRUD рекламных аккаунтов |
| Слоты | Старт/стоп, расписание, стоп по МСК (без скриншотов) |
| Профиль | Смена пароля |

## Переменные окружения

| Переменная | Сервис | Описание |
|------------|--------|----------|
| `SQLITE_DB_PATH` | api | Путь к БД (Docker: `/var/lib/awardsferm/awardsferm.db`) |
| `Auth__JwtSecret` | api | JWT-секрет (мин. 32 символа) |
| `Worker__BaseUrl` | api | URL Worker (`http://worker:8081`) |
| `Api__BaseUrl` | worker | URL API |
| `BROWSER_HEADLESS` | worker | `true` на Linux VPS (по умолчанию) |
| `RSYA_OAUTH_TOKEN` | api/worker | OAuth РСЯ (или `profiles/rsya-token.txt`) |

Volumes в production:

- `./profiles` → Worker (cookies, proxies.txt)
- `awardsferm-data` → API (`/var/lib/awardsferm`)

## GitHub Actions

Workflow `deploy-production.yml` — SSH на VPS, `docker compose` production.

Секреты: `PROD_HOST`, `PROD_USER`, `PROD_SSH_PRIVATE_KEY`, `PROD_SSH_PORT`, опционально `JWT_SECRET`, `RSYA_OAUTH_TOKEN`.

## Капча

При первом запуске слота Яндекс может показать капчу. В headless-режиме на VPS капчу решить нельзя — используйте прогретый профиль в `profiles/session-XXX/` или временно `BROWSER_HEADLESS=false` с X11/VNC для ручного прохождения.

## Сценарий Worker

1. yandex.ru/games → поиск → клик по игре из аккаунта
2. Игра 2–3 минуты
3. После 20 игр — ротация сессии (новый fingerprint)
4. `stopAtMsk` — остановка по Москве
