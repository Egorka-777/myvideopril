# VideoBatch — архитектура (для аудита кода)

## Стек

| Слой | Технология |
|------|------------|
| UI | C# WinForms (.NET Framework 4.8) |
| Автоматизация YouTube | Node.js + Playwright Core + CDP (Dolphin Anty) |
| Автоматизация TikTok | `tools/uploader/worker-tiktok.js` |
| Профили браузера | Dolphin Anty API (`localhost:3001`) |

## Файлы проекта

```
myvideopril/
├── VideoBatch.exe              # Собранное приложение
├── VideoBatch.csproj           # Проект C#
├── app.manifest
├── Start_here.txt              # Инструкция пользователя
├── src/
│   ├── Core.cs                 # Настройки, batch-обработка видео, JSON
│   ├── UI.cs                   # Главное окно (создание видео)
│   ├── Uploader.cs             # YouTube UI + запуск загрузки
│   ├── QueueManager.cs         # Подготовка задач → UploadJob
│   ├── TitleCleaner.cs         # Заголовок YouTube + Windows-имя файла
│   ├── ScheduleGenerator.cs    # Shorts: 15–60 мин между роликами профиля
│   ├── UploadItemState.cs      # pending / scheduled / error / unknown …
│   ├── ChannelStatus.cs        # Готов / Подготовка / Загрузка / …
│   ├── TikTok.cs               # TikTok workspace
│   ├── Audio.cs                # Озвучка (Windows / ElevenLabs)
│   └── Windows.cs              # DPAPI, системные утилиты
├── tools/uploader/
│   ├── worker.js               # YouTube Studio automation (основной)
│   ├── worker-tiktok.js
│   ├── package.json            # playwright-core
│   └── youtube-title-banks.json
└── tests/                      # Регрессионные скрипты
```

## Поток загрузки YouTube

1. UI: выбор канала → «Добавить видео» (видео сразу привязано к каналу)
2. **QueueManager.Prepare()** — профиль, staging, `TitleCleaner.CleanForUpload()`, `ScheduleGenerator.GenerateForProfile()` → `UploadJob`
3. **Uploader.cs** → `UploadAll()` → `RunUploadWithRetry()` (2 попытки, job пересобирается) → **worker.js**
4. Запуск **worker.js** через Node с JSON-job (токен Dolphin в env, не в файле)
5. **worker.js** (один профиль на пачку):
   - `startOrConnectProfile()` — Dolphin API
   - `verifyYouTubeStudioReady()` — вход в Studio
   - **`uploadPackSequentially()`** — для каждого ролика:
     - `openUploadAndSetFiles(page, [oneVideo])` — **только один файл**
     - `waitForUploadsReady()` — 100%
     - заголовок (из C#, `validateUploadTitle()` — защита без изменения)
     - `scheduleAndConfirmUpload()` — Schedule, проверка radio/date/time, подтверждение
     - сообщение `stage: item` → C# сохраняет `UploadState=scheduled` через `SafeSave()`
   - `uploadDraftsBulk()` — массовая передача **только для черновиков**
6. Расписание: интервал **15–60 мин** (случайный) между соседними роликами одного `ProfileId`; состояние в `%LocalAppData%\VideoBatchDesktop\youtube-schedule.json` → `{"profiles":{"<ProfileId>":"ISO8601"}}`. После последнего ролика сохраняется **следующий свободный слот**, не время последнего ролика.
7. При долгой загрузке worker переносит просроченное время вперёд (`adjustScheduleIfNeeded`) и логирует план/факт.
8. TikTok: тот же сценарий «аккаунт → добавить видео»; `TitleCleaner` перед отправкой

## Заголовки

- **YouTube:** `TitleCleaner.CleanForUpload()` — единственный источник (убирает префикс номера, нормализует `|`, ≤100 символов с ошибкой)
- **Windows-файл:** `TitleCleaner.MakeWindowsSafeTitle()` — `|` → ` . `, без запрещённых символов
- **worker.js:** `validateUploadTitle()` — только проверка, без `.slice(0,100)`

## Контрольные точки и повтор

| UploadState | Поведение |
|-------------|-----------|
| `pending` | загружать |
| `scheduled` | пропустить |
| `error` | повторить (если финальная кнопка не была подтверждена) |
| `unknown` | не загружать автоматически |

- Статус «Отложено N ✓» — только если **все N** роликов канала подтверждены как `scheduled`
- Ошибка расписания **не** сохраняется как Private/success; профиль остаётся открытым

## Версия worker

В журнале загрузки ищите строку: `Загрузчик 2026-09-17-sequential-schedule-v1`

## Что НЕ в репозитории (личные данные)

- `%LocalAppData%\VideoBatchDesktop\settings.xml` — каналы, Profile ID, зашифрованный токен Dolphin
- `C:\VideoBatch\Upload\` — staging
- Логи `youtube-*.log`
- `node_modules/` — ставится через `npm ci`

## Сборка

```powershell
dotnet build VideoBatch.csproj -c Release
cd tools/uploader && npm ci
node --check ../tools/uploader/worker.js
node tests/title_cleaner_regressions.js
node tests/schedule_regressions.js
node tests/sequential_upload_regressions.js
node tests/watch_regressions.js
```
