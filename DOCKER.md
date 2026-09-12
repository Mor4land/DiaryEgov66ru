# 🐳 Руководство по развёртыванию MirageDiary в Docker

Надёжное, изолированное и безопасное развёртывание бэкенда **MirageDiary (.NET 10)** в Docker-контейнере.

---

## 🏗 Архитектура контейнера

1. **Multi-Stage Build**:
   * **Сборщик (`build`)**: официальный образ SDK `mcr.microsoft.com/dotnet/sdk:10.0`. Кеширует слой `dotnet restore` по `.slnx` и `.csproj`, исключая повторную загрузку NuGet-пакетов.
   * **Рантайм (`runtime`)**: оптимизированный образ `mcr.microsoft.com/dotnet/aspnet:10.0`. Минимальный размер, без инструментов компиляции.
2. **Сертификаты Минцифры (Russian Trusted Root CA)**:
   * Официальные корневой и промежуточный сертификаты Минцифры автоматически скачиваются и интегрируются в системное хранилище Linux (`/usr/local/share/ca-certificates/` + `update-ca-certificates`).
   * Это обеспечивает доверенный TLS к Госуслугам и ФГИС «Моя Школа» на уровне ОС.
3. **Безопасность (Hardening)**:
   * Контейнер выполняется под непривилегированным пользователем `app` (UID 1654).
   * Права на директорию `/app/data` и файлы ключей разграничены.
4. **Персистентность данных**:
   * Вся постоянная информация хранится в монтируемой директории `./data` (`/app/data` в контейнере):
     * `miragediary.db` — база данных SQLite (режим WAL).
     * `token_master.key` — ключ шифрования токенов (права `0600`).
     * `backups/` — автоматические снимки БД (`VACUUM INTO`).
     * `admins.json` — список идентификаторов администраторов.
5. **Healthcheck**:
   * Каждые 30 секунд контейнер опрашивает локальный эндпоинт `http://localhost:5000/privacy`. При зависании Docker автоматически помечает контейнер как `unhealthy`.

---

## 🚀 Быстрый старт

### 1. Подготовка конфигурации

Скопируйте пример файла окружения и укажите рабочие параметры:

```bash
cp .env.example .env
nano .env
```

Обязательные переменные:
* `BotToken` — токен Telegram-бота из @BotFather.
* `Security__TokenEncryptionKey` — 32-байтный Base64 ключ шифрования токенов.
* `Security__AdminKey` — пароль для входа в веб-админку.

Для настройки администраторов создайте файл `data/admins.json`:
```json
{
  "Admins": [
    "YOUR_TELEGRAM_ID"
  ]
}
```

### 2. Запуск через Docker Compose

```bash
docker compose up -d --build
```

### 3. Проверка статуса и логов

```bash
# Статус контейнера и healthcheck
docker compose ps

# Просмотр логов в реальном времени
docker compose logs -f --tail 100
```

---

## 🛠 Ручной запуск через Docker CLI (без compose)

Если `docker compose` не установлен на сервере:

```bash
# 1. Сборка образа
docker build -t miragediary:latest .

# 2. Создание директории данных
mkdir -p ./data/backups

# 3. Запуск контейнера
docker run -d \
  --name miragediary \
  --restart unless-stopped \
  -p 5000:5000 \
  --env-file .env \
  -v "$(pwd)/data:/app/data" \
  miragediary:latest
```

---

## 🔄 Обновление контейнера

Для обновления до актуальной версии:

```bash
# Получить обновления из репозитория
git pull

# Пересобрать и перезапустить сервис без простоя данных
docker compose up -d --build
```
База данных, резервные копии и ключи сохраняются в каталоге `./data` и не затрагиваются пересборкой образа.

---

## 🔒 Проксирование через Nginx

Рекомендуемая конфигурация виртуального хоста Nginx на хост-машине:

```nginx
server {
    listen 443 ssl http2;
    server_name miragediary.space;

    ssl_certificate /path/to/fullchain.pem;
    ssl_certificate_key /path/to/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $http_connection;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
    }
}
```
