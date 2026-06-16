# Запуск локально (Windows, без Docker)

## Требования

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/)
- Playwright Chromium (устанавливается автоматически при первом запуске Worker)

## Быстрый старт

```powershell
# Из корня репозитория e:\Project\Cursor\AwardsFerm

# 1. Установить зависимости фронтенда
cd src\AwardsFerm.Web
npm install
cd ..\..\..

# 2. Установить браузер Playwright (один раз)
cd src\AwardsFerm.Worker
dotnet build
pwsh bin\Debug\net8.0\playwright.ps1 install chromium
cd ..\..\..

# 3. Запустить все сервисы (3 терминала)

# Терминал 1 — API (порт 8080)
dotnet run --project src\AwardsFerm.Worker\..\AwardsFerm.Api\AwardsFerm.Api.csproj

# Терминал 2 — Worker (порт 8081, откроется окно Chromium)
dotnet run --project src\AwardsFerm.Worker\AwardsFerm.Worker.csproj

# Терминал 3 — React UI (порт 5173)
cd src\AwardsFerm.Web
npm run dev
```

Откройте **http://localhost:5173** — на панели **2 сессии по умолчанию** (можно добавлять и удалять).

- У каждой сессии: **Старт** / **Стоп**, live-экран, лог и **автозапуск по расписанию (МСК)**.
- Кнопка **«+ Добавить сессию»** — до 10 слотов; **✕** — удалить слот (минимум 1).
- Расписание хранится в `profiles/slots.json`; автозапуск выполняет API (должен быть запущен).
- При закрытии браузера или после **20 игр** сессия **перезапускается автоматически** с новым отпечатком устройства и чистым профилем браузера, пока не нажмёте **Стоп**.
- Окна **Chromium** открываются на рабочем столе (headed-режим).

Или одной командой: `.\start-dev.ps1` (откроет 3 окна PowerShell).

**Перед пересборкой Worker** остановите старый процесс, иначе DLL будут заблокированы:

```powershell
.\stop-dev.ps1
dotnet build src\AwardsFerm.Worker\AwardsFerm.Worker.csproj
```

Сообщение Vite `ws proxy socket error: ECONNRESET` — нормально при перезапуске API; обновите страницу в браузере.

Если в консоли браузера 404 на `/api/...` или `/hubs/...` — сначала запустите **API** (порт 8080). UI показывает бейдж «API недоступен», пока бэкенд не поднят.

## Docker (разработка)

```powershell
docker compose up --build
```

Откройте **http://localhost:3000**.

## Публикация (production)

По образцу проекта bebochka: папки `backend/` и `frontend/`, GitHub Actions, `docker-compose.production.yml`.

Подробности: [deploy/DEPLOY.md](deploy/DEPLOY.md)

```powershell
# Локальный production-стек
copy .env.production.example .env.production
docker compose -f docker-compose.production.yml --env-file .env.production up -d --build
```

| Сервис | URL (production) | Порт |
|--------|------------------|------|
| Web UI | http://localhost:55502 | 55502 |
| API | http://localhost:55501 | 55501 |
| Worker | внутренний | 8081 |

### GitHub Actions

- `.github/workflows/deploy-production.yml` — полный стек через SSH + compose
- `.github/workflows/deploy-api.yml` — только API
- `.github/workflows/deploy-worker.yml` — только Worker
- `.github/workflows/deploy-frontend.yml` — только UI

Секреты: `PROD_HOST`, `PROD_USER`, `PROD_SSH_PRIVATE_KEY`, `PROD_SSH_PORT`, опционально `RSYA_OAUTH_TOKEN`.

## Docker (dev)

| Сервис | URL | Порт |
|--------|-----|------|
| Web UI | http://localhost:3000 | 3000 |
| API | http://localhost:8080 | 8080 |
| Worker | http://localhost:8081/health | 8081 |

## SQLite + Auth + Accounts (итерация 1)

- База данных: SQLite (EF Core migrations), файл задаётся через env `SQLITE_DB_PATH`.
- Для контейнера API рекомендуется путь `SQLITE_DB_PATH=/var/lib/awardsferm/awardsferm.db`.
- На Linux-хосте монтируйте volume:
  - host: `/var/lib/awardsferm`
  - container: `/var/lib/awardsferm`

### Миграции

```powershell
dotnet ef database update --project src\AwardsFerm.Api\AwardsFerm.Api.csproj --startup-project src\AwardsFerm.Api\AwardsFerm.Api.csproj
```

### Первый админ (SQL)

Файл: `deploy/sql/init-admin.sql`

Логин: `admin`  
Пароль примера: `Admin123!` (обязательно сменить после первого входа).

### Новые API

- `POST /api/auth/login` — вход по логину/паролю, выдаёт JWT.
- `GET/POST/PATCH/DELETE /api/adaccounts` — рекламные аккаунты пользователя.
- `GET /api/userprofit` — прибыль по аккаунтам и суммарно (сегодня/месяц), со snapshot в БД.

### Слоты и сессии

- Слоты (`session-XXX`) и настройки перенесены в БД.
- `stopAtMsk` (формат `HH:mm`) — таймер остановки сессии по МСК.
- При наступлении `stopAtMsk` сессия получает `STOP`, браузер закрывается.

### Переменные окружения (Docker)

| Переменная | Сервис | Значение по умолчанию | Описание |
|------------|--------|----------------------|----------|
| `Worker__BaseUrl` | api | `http://worker:8081` | URL Worker |
| `Api__BaseUrl` | worker | `http://api:8080` | URL API для событий |
| `BROWSER_HEADLESS` | worker | `false` | `false` = headed + Xvfb в Docker |
| `DISPLAY` | worker | `:99` | Виртуальный дисплей (Xvfb) |

### Профили

Cookies и конфиг хранятся в `profiles/session-001/` … `profiles/session-003/`. Папка `profiles` монтируется в Worker как volume.

Геолокация по умолчанию: **Санкт-Петербург** (60.053085, 30.311729).

### Смена IP и отпечаток устройства

- **MAC-адрес** — сайты в браузере его **не видят** (ограничение безопасности). В логе сессии генерируется случайный MAC как локальный ID устройства.
- **IP-адрес** — меняется только через **прокси** (`profiles/proxies.txt`). Сейчас прокси **отключены** — используется ваш реальный IP, геолокация **Россия** (СПб, Москва и др. по слотам).

### Статистика РСЯ в панели

OAuth-токен API статистики Рекламной сети Яндекса можно указать одним из способов:

1. Файл `profiles/rsya-token.txt` (см. `profiles/rsya-token.txt.example`) — одна строка с токеном.
2. Переменная окружения `RSYA_OAUTH_TOKEN`.
3. Секция `YandexRsya:OAuthToken` в `src/AwardsFerm.Api/appsettings.Development.json`.

В UI сверху отображаются: вознаграждение за сегодня / вчера / месяц, показы, клики, fill rate и мини-график за 14 дней. Обновление каждые 2 минуты.

Токен получить: [partner.yandex.ru](https://partner.yandex.ru) → карточка приложения → «Получить OAuth-токен для API статистики».

## Капча «Я не робот»

Яндекс может показать капчу при первом запуске с нового профиля. Это нормально.

**Что делать:**
1. Решите капчу **вручную** в окне браузера (Worker)
2. Сценарий **автоматически продолжится** после прохождения (ожидание до 5 мин)
3. При повторных запусках капча появляется реже — профиль сохраняется в `profiles/session-001/browser-data/`

**Снижение частоты капчи:**
- Используется **Google Chrome** (если установлен), не bundled Chromium
- **Persistent-профиль** браузера (cookies, localStorage, история)
- **Прогрев** через yandex.ru перед переходом на Игры

## Сценарий

1. Открыть yandex.ru/games
2. Поиск «червячки»
3. Клик на «Slither Worms Wars!»
4. Играть на странице 2–3 минуты
