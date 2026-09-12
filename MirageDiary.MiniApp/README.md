# Mirage Diary — Telegram MiniApp

Frontend Telegram-мини-приложения для подключения электронного дневника.
Стек: **React 19 + TypeScript + Vite 8**, анимации — Framer Motion, иконки — lucide-react.

Пакетный менеджер и рантайм — [**Bun**](https://bun.sh) (>= 1.3).

## Установка

```bash
bun install
```

## Команды

| Команда           | Описание                                                        |
| ----------------- | --------------------------------------------------------------- |
| `bun run dev`     | Dev-сервер Vite с HMR (через рантайм Bun, `--bun`)              |
| `bun run build`   | Проверка типов (`tsc -b`) + продакшн-сборка в `dist/`          |
| `bun run preview` | Локальный предпросмотр собранного бандла                        |
| `bun run typecheck` | Только проверка типов, без сборки                             |
| `bun run lint`    | Линтер [Oxlint](https://oxc.rs)                                 |

> Скрипты Vite запускаются через `bun --bun`, чтобы использовать рантайм Bun
> вместо Node — быстрее старт dev-сервера и сборки. Убрать `--bun` в
> `package.json` можно в любой момент, если понадобится рантайм Node.

## Заметки

- Единственный лок-файл проекта — `bun.lock`. `package-lock.json` / `yarn.lock`
  / `pnpm-lock.yaml` игнорируются намеренно (см. `.gitignore`).
- Настройки установки Bun — в `bunfig.toml` (точные версии, текстовый лок-файл, кеш).
- Интеграция с Telegram — через `window.Telegram.WebApp` (скрипт подключён в `index.html`).
