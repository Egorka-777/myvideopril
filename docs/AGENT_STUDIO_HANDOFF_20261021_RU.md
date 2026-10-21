# Handoff: Studio Shorts + SEO-описание (октябрь 2026)

**Репозиторий:** https://github.com/Egorka-777/myvideopril — ветка **`main`**  
**Сборка этой доработки:** `2026-10-21-studio-shorts-queue-v19f` в ветке `fix/studio-description-stability-20261021` от main `47515b9`. Main на момент начала задачи содержал v19e.
**Kit на ПК пользователя:** `D:\VideoBatch\VideoBatch\tools\uploader\`  
**Лог провального прогона:** `%LOCALAPPDATA%\VideoBatchDesktop\youtube-20261021-195806.log`  
**Диагностика:** `%LOCALAPPDATA%\VideoBatchDesktop\diagnostics\youtube-studio-*.json`

См. также: `docs/AGENT_STUDIO_SHORTS_20261021_RU.md`, `docs/AGENT_STUDIO_DESCRIPTION_20261021_RU.md`

---

## Установленные проблемы (факты из лога 19:58–20:04, build v19d)

1. **Трейдер с нуля** — не автomation: `Требуется вход в Google в выбранном Dolphin-профиле` (19:58:21).

2. **SEO-описание ~1648–1714 символов** — основной массовый сбой на v19d:
   - `Studio не подtвердил точный текст поля описания` (Trader Max 1/3, BaronFilms 2/3, 10 RU 2/3).
   - В v19d стояли **timeout 5 s** и чанки **80** символов. По переданной сводке сбой возникал на ~960–1120 символах. Сам таймаут не доказывает отставание DOM: возможны постоянная потеря/изменение текста или замена поля. Исходный лог и указанный JSON в среду разработки не переданы.
   - **v19e:** чанки 40, verify 35 s, 3 попытки, activation 25 s — **не проверено на реальных аккаунтах**.

3. **Школа @marinhoffer 1/3** — после ~1120 символов retry описания; поле перекрыто **DIV** (`point: null`, JSON `youtube-studio-1792587588524-22812.json`). До Publish не дошли.

4. **Частичный успех в том же прогоне** — путаница у пользователя:
   - BaronFilms **1/3** опубликован (`ivz1OTskd18`, «Видео опубликовано»).
   - 10 RU **1/3** опубликован (`-mwKV2RX1sQ`), но ~2 мин «Жду экран завершения» до детекта (v19d publish detection).

5. **Параллель** — 5 Studio-воркеров стартовали одновременно (19:58:06). В UI Dolphin «Параллельно профилей» рекомендуется **2–3**.

---

## Ключевые файлы

| Файл | Роль |
|------|------|
| `tools/uploader/studio-editor-controls.js` | Клавиатурный ввод, `waitTyped`, retry описания |
| `tools/uploader/youtube-studio-upload.js` | BUILD, metadata, publish, verify ID, queue |
| `tools/uploader/studio-browser.js` | `publicationState`, Close, post-publish |
| `tools/uploader/worker.js` | WORKER_BUILD, прогресс в UI |
| `src/Uploader.cs` | `MaxParallelUploads`, Studio batch |
| `tests/browser/studio-shorts-queue.js` | Shorts queue + exact description |
| `tests/browser/studio-editor-focus.js` | Shadow DOM editor |

Политика: **без paste/fill innerText** для метаданных — только клавиатура (Shift+Enter для переносов).

---

## Что должно работать (критерий готовности)

Shorts, 3 ролика подряд на канале: файл → title → **полное SEO-описание** → No/No → upload 100% → Next → Public → Publish **один раз** → «Видео опубликовано» → verify по ID → Close → Create → **следующий файл без дубля Publish**.

---

## Автопроверки (обязательны перед «готово»)

```powershell
cd D:\VideoBatch\repo
npm ci --prefix tools/uploader --ignore-scripts
node tests/browser/studio-shorts-queue.js
node tests/browser/studio-editor-focus.js
node tests/browser/studio-state-flow.js
```

---

## Риски для следующего агента

- Если v19e на ПК всё ещё падает на описании — не ослаблять до «успех без текста» без явного решения; рассмотреть paste только для description или readback после Tab с допустимым diff.
- DIV-blocker на retry — scroll/ожидание гидратации, не force-click.
- Google login — только пользователь.
- Проверить в Studio черновики после сбоев 2/3 (CDP отключался с открытым мастером).


## Доработка v19f: точный статус

**Живой критерий 3+3 пока НЕ подтверждён.** У разработчика нет доступа к диску D:, установленному Dolphin, `%LOCALAPPDATA%` или указанным исходным логам. Статус после успешного CI — **готово к проверке на ПК**, а не «6 видео опубликованы в аккаунтах». Google login не автоматизируется.

Изменения ограничены вводом/диагностикой метаданных, тестами и доставкой kit. `studio-browser.js`, публикация, Public, проверка ID, Close и запрет повторного Publish сохранены из main. BUILD обновлён в worker.js и youtube-studio-upload.js.

### Что можно доказать на исходниках и изолированных страницах

1. Проверка точки ввода v19e могла вернуть `point:null`/DIV для длинного поля, если его верх и середина недоступны, хотя часть поля видна ниже закреплённой панели. Новая сетка точек внутри видимой области проверяется настоящим `elementFromPoint`. Полностью закрытое модальным окном поле по-прежнему блокирует ввод. Нет force-click, удаления DIV и присваивания текста.
2. В v19e внутренние 3 попытки/35–45 секунд сверки не участвовали в расчёте внешнего срока `metadataBudget`. Теперь единая политика задаёт конечный срок каждой попытки и общий бюджет; завершение этапа запрещает дальнейшие чанки через AbortSignal. Это найденное несоответствие кода, **не установленная причина конкретного лога v19d**.
3. Равенство текста остаётся точным после прежней нормализации CRLF/NBSP/краевых пробелов. Сверка не отключена; paste/fill/innerText write не добавлены. Диагностика хранит длины, SHA256, первый индекс расхождения, тип символа и геометрию/перекрытие, без текста описания. Успешный финал поля получает отдельную строку `Описание проверено целиком: ... SHA256 ...` после Tab.

### Обязательный CI и доказательства

`tests/browser/studio-shorts-queue.js`, `studio-editor-focus.js`, `studio-state-flow.js` остаются обязательными. Дополнительный `studio-description-stability.js` прогоняет **3 RU + 3 EN Shorts** на изолированных страницах, с SEO-текстами из скомпилированных банков около 1650 символов и настоящим клавиатурным вводом 20 ms/символ. Он также проверяет ложное DIV-перекрытие v19e, настоящий модальный блок, прекращение чанков при отмене после 1000 символов, точную диагностику несовпадения и бюджет всех попыток.

Артефакт **Studio-Description-Evidence** содержит `description-evidence.json`, исходные тестовые банки и при сбое `youtube-studio-*.json`. В JSON явно указано `source: isolated-browser-fixture`, `liveAccountsTested:false`. Это воспроизведение дефекта кода и контракт цепочки; не выдавать его за лог с пользовательского ПК. Полный лог теста доступен в Actions.

### Sync kit на ПК

Из корня проверенной ветки/распакованного update:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Sync-StudioUploader.ps1 -KitRoot D:\VideoBatch\VideoBatch -WhatIf
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Sync-StudioUploader.ps1 -KitRoot D:\VideoBatch\VideoBatch
```

Сначала остановить задачи и закрыть VideoBatch; браузеры/аккаунты скрипт не останавливает. Он проверяет синтаксис, резервирует заменяемые JS, копирует uploader, сверяет SHA256 и пишет `STUDIO_UPLOADER_VERSION.json`. При ошибке копирования восстанавливает предыдущие JS. EXE/node.exe/node_modules, видео, аккаунты, прокси, настройки и банки не заменяются. Эта доработка не меняет C#-контракт v19e, поэтому новый EXE не обязателен. Если установлен EXE до v19 с отсутствующим `studioVideoId`, сначала обновить приложение целиком согласно Shorts-doc.

### Контроль 3+3 и автоматическая проверка лога

1. Сверить manifest и новый BUILD **v19f** в обоих JS и журнале запуска. Сохранить текущий SHA и время начала прогона с часовым поясом. Не запускать из старой копии приложения.
2. В Dolphin/приложении выбрать **2 подключённых профиля**, у обоих вручную подтверждён вход Google. На время контроля установить `MaxParallelUploads=2` в интерфейсе. Не править settings.xml при работающем приложении. Проверить старые черновики перед выбором новых тестовых задач.
3. Запустить по **3 Shorts** на каждом. Текст около 1650 символов берётся из текущей базы. Дождаться окончания обеих задач. Ничего автоматически не переотправлять и не удалять на YouTube.
4. Анализировать структурированный `diagnostics/app-YYYYMMDD.jsonl`: обычный `youtube-*.log` не хранит явное имя каждого stage. Подставить реальные Profile ID и время начала; пример времени ниже не копировать как готовый слот:

```powershell
$since = (Get-Date).ToString('o') # сохранить ПЕРЕД запуском 3+3
# после окончания задач, в той же сессии PowerShell:
& D:\VideoBatch\VideoBatch\tools\uploader\node.exe D:\VideoBatch\VideoBatch\tools\uploader\studio-run-audit.js --log "$env:LOCALAPPDATA\VideoBatchDesktop\diagnostics\app-20261021.jsonl" --since $since --profiles "PROFILE_ID_1,PROFILE_ID_2" --build 2026-10-21-studio-shorts-queue-v19f --out .\studio-live-audit.json
```

Скрипт ничего не открывает и не публикует. Код выхода **0** означает, что в указанном интервале у каждого из двух профилей один свежий task, BUILD совпадает, есть по 3 полных сверки SEO, 3 одиночных Publish, **item_confirmed ×3 с разными video ID**, 3 Close/ready в правильном порядке и завершение без ошибок. Он отклоняет частичный успех, другой BUILD, повторный Publish, отсутствующее описание/Close и смешанные задачи. Это проверка предоставленного лога, а не самостоятельное обращение к YouTube. На следующий день указывать актуальную дату JSONL.

5. Агент сверяет шесть ID в Studio, прикладывает `studio-live-audit.json`, исходный `youtube-*.log`, относящиеся к прогону строки app JSONL, manifest и при сбое diagnostics JSON. Только после этого отметить **LIVE_3_PLUS_3_CONFIRMED** и записать дату/профили/ID в этот handoff.

Если снова сломалось описание: смотреть `editorFailure.comparison` / `flow.editorAttempts`, `relation`, длины, SHA256, `firstDifference`, `state.rect/clip/point/blocker/focused`. `missing-suffix` — фактического текста меньше; постоянное различие не лечить ещё большим таймаутом. Реальный DIV сверху не обходить force-кликом. Прислать исходный JSON и лог сильному агенту, а не придумывать причину по скриншоту.
