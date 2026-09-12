# MirageDiary Project Map & Quick Reference

> **Справка для AI-агента и разработчика:** этот файл предназначен для мгновенной ориентации в проекте без расходования токенов на сканирование директорий, `grep` и чтение лишних файлов.

---

## 1. Репозитории и расположение кода
* **Бэкенд (.NET 10 Web API + Telegram/MAX Bots):**
  - Путь: `c:\Users\mlowers\source\repos\MirageDiary`
  - GitHub: `https://github.com/Miragestudio-repo/MirageDiary_Backend.git`
* **Фронтенд (Telegram MiniApp - React + Vite + TypeScript):**
  - Путь: `C:\Users\mlowers\source\repos\MirageDiary-Miniapp`
  - GitHub: `https://github.com/Miragestudio-repo/MirageDiary-Miniapp.git`
* **Продакшен хост:**
  - Домен: `https://miragediary.space`
  - SSH: `mlowers@lorian.dev:44` (Ключ: `C:\Users\mlowers\key`)
  - Путь на сервере: `/home/mlowers/miragediary`

---

## 2. Архитектура бэкенда (`MirageDiary`)

### Основные проекты:
1. `MirageDiary.Server/` — ASP.NET Core хост, фоновые воркеры, API и боты:
   - **`Program.cs`** — точка входа, конфигурация сервисов, маппинг маршрутов и фоновых воркеров.
   - **`Controllers/`**:
     - `MiniAppController.cs` — авторизация (`/api/miniapp/auth/check/...`), связка токенов Госуслуг, пуллинг кодов.
     - `AdminController.cs` — защищённая админ-панель (`/api/admin/*`, статистика, кэш, бэкапы, рассылки).
     - `DiaryProxyController.cs` — обратный прокси для обращений к дневнику (`/diary-proxy/*`).
     - `BotController.cs` — эндпоинт вебхука Telegram.
     - `MaxBotController.cs` — эндпоинт вебхука мессенджера MAX.
   - **`Services/`**:
     - `BotCommandService.cs` — логика команд, меню, карточек и экранов Telegram-бота.
     - `MaxCommandService.cs` — логика команд и экранов для бота в MAX.
     - `DiaryInteractionService.cs` — парсинг расписания, оценок, домашки, профиля из Госуслуг/Дневника.
     - `TelegramBotPollingWorker.cs` — фоновый лонг-поллинг для Telegram.
     - `TokenRefreshBackgroundService.cs` — фоновое обновление токенов пользователей.
     - `DatabaseBackupService.cs` — снапшоты и бэкапы SQLite базы данных.
   - **`Data/` & `Models/`**:
     - `AppDbContext.cs` — контекст EF Core. База данных SQLite: `miragediary.db`.
     - `UserEntity.cs` — сущность пользователя (токены, настройки будильников, роль, школа/класс).
   - **`wwwroot/`**:
     - `images/banners/` — PNG-баннеры для ботов (1200x580, `banner_menu.png`, `banner_alarm.png`, `banner_settings.png` и др.).
     - `index.html`, `assets/` — скомпилированный фронтенд MiniApp (копируется из репозитория MiniApp).
     - `admin.html`, `privacy.html` — страницы админки и политики конфиденциальности.
2. `MirageDiary.Core/` — общие модели, интерфейсы и клиенты API дневника.

---

## 3. Архитектура MiniApp (`MirageDiary-Miniapp`)
- `src/App.tsx` — главный React-компонент (состояния экранов: авторизация, расписание, оценки, домашка, профиль).
- `src/App.css` — дизайн-система (темная палитра, шрифт Manrope).
- `src/api.ts` — клиент вызова бэкенда (`/api/miniapp/*`).
- `bun run build` — сборка проекта (вывод в `dist/`).

---

## 4. Сборка и деплой
* **Полное автоматическое тестирование системы:**
  ```powershell
  # В MirageDiary:
  .\test-all.ps1              # Локальные тесты (C#, Vite, ассеты, конфиги)
  .\test-all.ps1 -Remote      # Полный прогон с проверкой боевых эндпоинтов на VPS
  .\test-all.ps1 -OnlyRemote  # Только проверка здоровья VPS и домена
  ```
* **Сборка фронтенда MiniApp и перенос в сервер:**
  ```powershell
  # В MirageDiary-Miniapp:
  bun run build
  Copy-Item -Path "dist\*" -Destination "..\MirageDiary\MirageDiary.Server\wwwroot\" -Recurse -Force
  ```
* **Деплой бэкенда на Linux VPS:**
  ```powershell
  # В MirageDiary:
  .\deploy.ps1
  ```
  *(Скрипт компилирует .NET Release linux-x64, обфусцирует через Obfuscar, проверяет маскировку секретов, загружает всё по SCP и перезапускает демон `start.sh` на сервере).*

---

## 5. Дизайн и баннеры для ботов
* **Спецификация баннеров:** размер `1200x580`, белая карточка с закруглением 22px, серая тонкая рамка `#E2E8F0`.
* **Типографика:**
  - Заголовок: **`Unbounded`** (размер 128 в рендере 2400x1160, даунскейл `LANCZOS` в 1200x580).
  - Бренд `MirageDiary`: **`Inter SemiBold`** (размер 44 в 2400x1160).
  - Счётчик/секция (`07 — SETTINGS`, `08 — ALARM`): **`Inter Medium`** (`#94A3B8`).
  - Подзаголовок: **`Segoe UI Regular`** (`#475569`, размер 56 в 2400x1160).
  - Акцентная плашка: тёмный маркер `[152, 755, 420, 765]`.

---

## 6. Техническая документация ботов
* **`BOT_DOCS.md`** — детальное техническое руководство по ботам:
  - Архитектура и протокол шифрования `enc:v2:` (Envelope Encryption, DEK/KEK, AES-256-GCM).
  - Реализация требований 152-ФЗ (согласие родителей, аудит-таймштамп, право на забвение, локализация РФ).
  - Полный справочник команд и callback payload для Telegram и MAX ботов.
  - Умный будильник (`SmartAlarmService`), сетка звонков (`BellScheduleService`), связка аккаунтов.

