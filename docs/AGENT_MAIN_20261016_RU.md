# Актуальный main — Studio startup + UI/Dolphin — 16.10.2026

**Ветка:** `main`  
**Коммит:** `bb9e7cb` (`Merge studio startup with warmup UI, Dolphin profiles, and upload UX polish`)  
**Предыдущая база:** `1a65b34` (telegram polish) → влиты `fix/youtube-studio-startup-20261016` + локальные правки пользователя.

## Что в этом main (кратко)

| Область | Суть |
|--------|------|
| **YouTube Studio / Dolphin** | Worker `2026-10-16-studio-startup-v2`: быстрый старт Studio/CDP, переименование исходника при выборе (без копии), Shadow DOM/CDP для больших файлов, RU/EN последовательная загрузка. См. `docs/AGENT_STUDIO_STARTUP_20261016_RU.md`. |
| **Загрузка без «повторной проверки»** | Убрана блокировка по `StudioPending` перед загрузкой (`Uploader.cs`, `QueueManager`, `youtube-studio-upload.js`). При новой загрузке флаг сбрасывается. Черновик в Studio пользователь чистит сам при дубле. |
| **Автопрогрев** | Две range-полоски (сессия мин–макс, просмотр сек–сек), вкладки **RU / EN**, список: галочка + имя канала, «Сохранить» для интервалов. `RangeSlider.cs`, `YouTubeWarmupPanel.cs`. |
| **YouTube сетка** | Убраны лишние колонки «Задать превью»; превью по клику на **аватарку** (ПКМ — задать/удалить). Столбцы страна/заголовок/статус/ссылка **не трогали** — пользователь просил отложить. |
| **Профили** | «Загрузить из Dolphin», RU/EN по папке/имени/тегам, ПКМ → рынок, колонка **Нагрузка прокси** `YT x/2 · TT x/2`. `DolphinProfileCatalog.cs`. |
| **UI** | Карточки `Theme.MakeCard`, модалки `Theme.StyleModal` (Dialog, переименование профиля). Глобальные скроллбары и все окна — **не завершено**. |

## Установка агентом на ПК пользователя

1. Закрыть **VideoBatch** и дочерние **node** (workers). **Не** закрывать профили Dolphin в Anty.
2. Бэкап: `%LOCALAPPDATA%\VideoBatchDesktop\` и папка запуска (на ПК: `D:\VideoBatch\VideoBatch\`).
3. Репозиторий:
   ```powershell
   git fetch origin
   git switch main
   git pull --ff-only origin main
   # Ожидаемый HEAD: bb9e7cb или новее с тем же содержимым
   npm ci --prefix tools/uploader --ignore-scripts
   dotnet build VideoBatch.csproj -c Release
   ```
4. **Обязательно вместе:** скопировать в установленную папку:
   - `bin/Release/net48\VideoBatch.exe`, `*.dll`, `runtimes\`
   - **зеркало** `tools/uploader\` (в т.ч. `worker.js`, `studio-browser.js`, `youtube-studio-upload.js`, `dolphin-session.js`, `node_modules` после `npm ci`)
   - при необходимости `tools/telegram`, `tools/highlights` из output сборки
   - записать `SOURCE_COMMIT.txt` = `git rev-parse HEAD`
   - **Не** оставлять новый EXE со старым `worker.js`.
5. В логе первой загрузки YouTube: **`Worker 2026-10-16-studio-startup-v2`**.

## Проверки (авто)

```powershell
Get-ChildItem tests -Filter *.js | ForEach-Object { node $_.FullName; if ($LASTEXITCODE -ne 0) { throw $_.Name } }
npx --prefix tools/uploader playwright-core install chromium
node tests/browser/studio-startup.js
node tests/browser/studio-cdp-stall.js
node tests/browser/youtube-studio-upload.js
node tests/browser/dolphin_lifecycle.js
powershell -File scripts/Test-HttpSelfTests.ps1
# StudioUploadTests + DolphinProfileCatalogTests — через LoadFrom VideoBatch.exe
```

## Проверка на ПК пользователя (ручная)

1. **Профили:** токен Dolphin в настройках → «Загрузить из Dolphin» → прокси и «Нагрузка прокси».
2. **Автопрогрев:** RU/EN, две полоски, сохранение, запуск на 1–2 каналах.
3. **YouTube:** одно тестовое видео — переименование в исходной папке, Studio без блока StudioPending, лог worker v2.
4. Не коммитить `tools/uploader/_user_titles_raw.txt` (локальный мусор).

## Не делать без запроса

- `git reset --hard`, `git clean`, удаление `%LOCALAPPDATA%\VideoBatchDesktop\`.
- Установка только EXE без `tools/uploader`.
- Возврат жёсткой блокировки StudioPending без согласования с пользователем.
- Массовое удаление столбцов YouTube (страна, заголовок, статус, ссылка) — отложено.

## Состояние на ПК (16.10.2026, сессия агента)

- Репозиторий: `D:\VideoBatch\repo`, **main** @ `bb9e7cb`, запушено на `origin/main`.
- Kit: `D:\VideoBatch\VideoBatch\`, `SOURCE_COMMIT.txt` = `bb9e7cb`.
- Бэкап перед studio-startup: `D:\VideoBatch\backups\studio-startup-20261016-173124\`.
- Stash (можно удалить после проверки): `stash@{0}` — «WIP local before fix/youtube-studio-startup-20261016» (уже в main).

## Связанные документы

- `docs/AGENT_STUDIO_STARTUP_20261016_RU.md` — детали worker/Studio (ветка влита в main).
- `docs/AGENT_TELEGRAM_POLISH_20261016_RU.md` — оболочка/Telegram (база до studio fix).

## Открытые задачи (backlog)

- Закругления и аккуратные скроллбары **везде** в приложении.
- Упростить таблицу YouTube / вынести тех. поля в карточку аккаунта.
- Подстроить `DetectMarket` под реальные имена папок Dolphin пользователя.
