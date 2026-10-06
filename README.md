# VideoBatch (myvideopril)

Windows-приложение: пакетная обработка видео + массовая загрузка на **YouTube** и **TikTok** через **Dolphin Anty**.

**Репозиторий публичный** — весь исходный код в `src/` и `tools/uploader/`.

## Быстрый обзор для GPT / аудита

| Файл | Назначение |
|------|------------|
| [src/Uploader.cs](src/Uploader.cs) | UI YouTube, каналы, загрузка, расписание |
| [tools/uploader/worker.js](tools/uploader/worker.js) | Playwright: Studio, upload, schedule |
| [src/Core.cs](src/Core.cs) | Настройки, обработка видео |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Полная схема потоков |

## Запуск

1. Клонировать репозиторий
2. `cd tools/uploader && npm ci`
3. Запустить `VideoBatch.exe` (Windows 10/11, .NET 4.8)

## Сборка из исходников

```powershell
dotnet build VideoBatch.csproj -c Release
# exe → bin/Release/net48/VideoBatch.exe (скопировать рядом с tools/)
```

## Dolphin

- Токен API вводится в приложении, хранится локально (DPAPI), **не в git**
- Profile ID каналов — только в локальном `settings.xml`

## Приватные данные

В репозитории **нет**: токенов, IP прокси, имён каналов, логов, settings.xml.

Подробнее: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
# Прямые эфиры YouTube

В разделе YouTube: галочки каналов → «Запустить эфир» → папка, название, превью, теги. Видео идут бесконечно по кругу до остановки. Новые эфиры используют сохранённую сессию Dolphin: сначала нажмите «Проверить сессию Dolphin» в окне запуска. Управление Studio — HTTPS; видео — RTMPS.

Сборка, установка и проверка: [AGENT_YOUTUBE_LIVE_20261005_RU.md](docs/AGENT_YOUTUBE_LIVE_SESSION_20261006_RU.md).
