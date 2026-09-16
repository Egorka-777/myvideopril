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
│   ├── Uploader.cs             # YouTube + Dolphin + загрузка
│   ├── TikTok.cs               # TikTok workspace
│   ├── Audio.cs                # Озвучка (Windows / ElevenLabs)
│   └── Windows.cs              # DPAPI, системные утилиты
├── tools/uploader/
│   ├── worker.js               # YouTube Studio automation (основной)
│   ├── worker-tiktok.js
│   ├── package.json            # playwright-core
│   └── youtube-title-banks.json
└── tests/                      # Интеграционные скрипты
```

## Поток загрузки YouTube

1. **Uploader.cs** → `UploadAll()` — staging файлов, расписание, `draftOnly=false`
2. Запуск **worker.js** через Node с JSON-job (токен Dolphin в env, не в файле)
3. **worker.js**:
   - `startOrConnectProfile()` — Dolphin API
   - `verifyYouTubeStudioReady()` — вход в Studio
   - `resolveStudioChannelId()` — реальный `UC…` из URL
   - `openUploadForPack()` — `…/videos/upload` или Создать → Добавить видео (RU/EN)
   - `setInputFilesViaCdp()` — передача файлов через CDP
   - `waitForBulkUploadComplete()` — дождаться загрузки
   - `processUploadItemInDialog()` — заголовок, превью, Далее → **Запланировать публикацию**
4. Расписание: `AllocateSchedules()` в C# + `setSchedule()` / `publish()` в worker

## Версия worker

В журнале загрузки ищите строку: `Загрузчик 2026-09-16-schedule-v3`

## Что НЕ в репозитории (личные данные)

- `%LocalAppData%\VideoBatchDesktop\settings.xml` — каналы, Profile ID, зашифрованный токен Dolphin
- `C:\VideoBatch\Upload\` — staging
- Логи `youtube-*.log`
- `node_modules/` — ставится через `npm ci`

## Сборка

```powershell
dotnet build VideoBatch.csproj -c Release
cd tools/uploader && npm ci
```
