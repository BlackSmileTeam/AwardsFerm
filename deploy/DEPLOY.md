# Деплой AwardsFerm (Linux)

## Production на VPS

### 1. Подготовка сервера

```bash
sudo bash deploy/linux/setup-server.sh /opt/awardsferm
```

Устанавливает Docker (если нет), создаёт `profiles/`, генерирует `.env.production` с `JWT_SECRET`.

### 2. Конфигурация

Скопируйте и отредактируйте:

```bash
cp .env.production.example .env.production
nano .env.production
```

Обязательные поля:

- `JWT_SECRET` — `openssl rand -base64 48`
- `BROWSER_HEADLESS=true` — браузер без GUI (Linux)
- `FRONTEND_PORT`, `API_PORT` — порты на хосте

### 3. Запуск

```bash
docker compose -f docker-compose.production.yml --env-file .env.production up -d --build
```

### 4. Первый администратор

После первого старта API (миграции применяются автоматически):

```bash
bash deploy/linux/init-db-remote.sh
```

Логин: `admin`, пароль: `Admin123!` — **смените после входа**.

### 5. FirstVDS (текущий production)

Сервер: **157.22.199.24** (`vasekisov.fvds.ru`), пользователь `deploy`.  
На том же хосте уже работает Bebochka на `:55501` / `:55502`, поэтому AwardsFerm слушает:

| Сервис | URL |
|--------|-----|
| Админ-панель | http://157.22.199.24:55504 |
| API (прямой) | http://157.22.199.24:55503 |

Путь на сервере: `/opt/awardsferm`  
SSH-ключ (как у Bebochka): `C:\Users\vasek\.ssh\bebochka_firstvds_deploy`

Ручной деплой с ПК (без git на сервере):

```powershell
cd E:\Project\Cursor\AwardsFerm
tar -czf - --exclude=".git" --exclude="**/bin" --exclude="**/obj" --exclude="**/node_modules" --exclude="profiles/*/browser-data" . |
  ssh -i $env:USERPROFILE\.ssh\bebochka_firstvds_deploy deploy@157.22.199.24 "tar -xzf - -C /opt/awardsferm"
ssh -i $env:USERPROFILE\.ssh\bebochka_firstvds_deploy deploy@157.22.199.24 `
  "cd /opt/awardsferm && docker compose -f docker-compose.production.yml --env-file .env.production up -d --build"
```

SQLite хранится в Docker volume `awardsferm_awardsferm-data` (контейнер `api:/var/lib/awardsferm/awardsferm.db`).

### 6. Системный nginx (HTTPS)

Проксируйте домен на фронт:

```nginx
server {
    listen 443 ssl http2;
    server_name awards.example.com;

    location / {
        proxy_pass http://127.0.0.1:55504;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_connect_timeout 10s;
        proxy_send_timeout 120s;
        proxy_read_timeout 120s;
    }
}
```

API снаружи не обязателен — nginx фронта проксирует `/api/` и `/hubs/`.

## GitHub Actions

Workflow: `.github/workflows/deploy-production.yml` (единственный; старые split-workflow удалены)

| Secret | Значение |
|--------|----------|
| `PROD_HOST` | `157.22.199.24` |
| `PROD_USER` | `deploy` |
| `PROD_SSH_PRIVATE_KEY` | содержимое `~/.ssh/bebochka_firstvds_deploy` |
| `PROD_SSH_PORT` | `22` |
| `PROD_DEPLOY_PATH` | `/opt/awardsferm` |
| `PROD_FRONTEND_PORT` | `55504` (55502 занят Bebochka) |
| `PROD_API_PORT` | `55503` (55501 занят Bebochka) |
| `JWT_SECRET` | значение из `/opt/awardsferm/.env.production` на сервере |
| `RSYA_OAUTH_TOKEN` | опционально |

## Структура данных

| Путь на хосте | Контейнер | Назначение |
|---------------|-----------|------------|
| `./profiles` | worker:/app/profiles | cookies, proxies.txt, rsya-token |
| Docker volume `awardsferm-data` | api:/var/lib/awardsferm | SQLite |

## Обновление

```bash
cd /opt/awardsferm
git pull
docker compose -f docker-compose.production.yml --env-file .env.production up -d --build
```

## GitHub Actions: SSH / tar ошибки

### `Connection timed out during banner exchange`

TCP-порт 22 открыт, но `sshd` не отвечает баннером. Частые причины:

1. **fail2ban** заблокировал IP после серии неудачных деплоев — разбан через консоль VPS.
2. **sshd перегружен** (`MaxStartups`) — много одновременных подключений от CI.
3. **UseDNS** в `sshd_config` — обратный DNS для IP GitHub Actions зависает.

**Восстановление через консоль FirstVDS** (без SSH):

```bash
sudo bash /opt/awardsferm/deploy/linux/recover-sshd.sh
# или вручную:
sudo fail2ban-client unban --all
sudo sed -i 's/^#*UseDNS.*/UseDNS no/' /etc/ssh/sshd_config
echo "UseDNS no" | sudo tee -a /etc/ssh/sshd_config
sudo systemctl restart ssh
```

Проверка с ПК:

```powershell
ssh -i $env:USERPROFILE\.ssh\bebochka_firstvds_deploy deploy@157.22.199.24 "echo OK"
```

Приложение (порты 55503/55504) может работать, пока SSH недоступен.

### `kex_exchange_identification` / `tar` exit 141

Обрыв pipe при `tar | ssh` — workflow делает до 3 повторов с паузой 30 с.

## Локальная разработка

| ОС | Команда |
|----|---------|
| Linux | `./start-dev.sh` |
| Windows | `.\start-dev.ps1` |
| Docker dev | `docker compose up --build` (UI :3000) |
