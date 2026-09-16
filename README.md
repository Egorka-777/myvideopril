# VideoBatch

Windows-приложение для пакетной обработки видео и загрузки на YouTube / TikTok через профили **Dolphin Anty**.

## Быстрый старт

1. Скачайте или соберите проект.
2. В папке `tools/uploader` выполните один раз:
   ```powershell
   npm ci
   ```
3. Запустите `VideoBatch.exe` (нужны Windows 10/11 x64 и .NET Framework 4.8).

Подробная инструкция — в `Start_here.txt`.

## Сборка из исходников

```powershell
dotnet build VideoBatch.csproj -c Release
```

Готовый `VideoBatch.exe` появится в `bin/Release/net48/`. Скопируйте его рядом с папкой `tools/`.

## Структура

| Путь | Описание |
|------|----------|
| `src/` | Исходники C# (WinForms) |
| `tools/uploader/worker.js` | Автоматизация YouTube Studio (Playwright + Dolphin CDP) |
| `tools/uploader/youtube-title-banks.json` | Банки заголовков RU/EN |
| `VideoBatch.exe` | Собранное приложение |

## Dolphin

- API-токен хранится локально в `%LocalAppData%\VideoBatchDesktop\settings.xml` (зашифрован Windows DPAPI).
- В репозиторий настройки и токены **не попадают**.

## Лицензия

Приватный проект. Все права у автора.
