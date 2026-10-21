# Studio: длинное описание (~1600 символов) — v18

Контекст для следующего агента. Ветка **main**, сборка uploader **`2026-10-21-studio-description-v18`**.

## Что просил пользователь

Стабильная публикация через YouTube Studio (в т.ч. Shorts): файл → название → **SEO-описание ~1600 символов** → теги → публикация → «Закрыть» → следующий ролик. Без зависания на MessageBox «Загрузка» с `(профиль оставлен)`.

## Почему «раньше не было проблем»

1. **Короткие описания** — ввод укладывался в один–два чанка по 80 символов; Studio успевал обновить DOM до жёсткой проверки.
2. **Банки SEO** (коммиты `b3b4a98`, `b568874`) — в очередь пошли тексты **~1600 символов**; тот же keyboard-only ввод стал длиннее на порядок.
3. **YouTube Studio** — contenteditable в Shadow DOM, возможная нормализация пробелов/переносов и задержка `innerText` на длинном тексте. Это не HTTP API: нет атомарного «set description».
4. **Ограничение проекта** — в `studio-editor-controls.js` запрещены `fill`, `innerText`/`textContent` и вставка из буфера (политика «только клавиатура»). Альтернатива «вставить целиком» потребует явного решения продукта.

Итог: цепочка не «усложнилась костылями ради костылей», а **старый контракт «печатаем и сверяем DOM»** начал упираться в длину текста и рассинхрон между **ожидаемым prefix** и **фактическим innerText**.

## Наблюдаемые ошибки (октябрь 2026)

| Сообщение | Суть | Подтверждение |
|-----------|------|----------------|
| Studio не подтвердил введённую часть поля **описания** | Таймаут сверки чанка; Studio отстаёт | `lastStage: metadata_description`, build v17 |
| Studio **не очистил** прежний текст | После Delete в contenteditable остаётся `\n`; проверка без `.trim()` | headless repro + `studio-editor-focus.js` |
| Studio **заменил поле описания** во время ввода | После `waitTyped` допускался `trim`, а перед следующим чанком требовалось **строгое** `===` → ложный сбой при нормализации Studio | `youtube-studio-1792583406965-26584.json`: описание в DOM **347** симв., ошибка на этапе ввода |

Отдельно чинились Shorts/post-publish (`yt-share-target-renderer`, «Закрыть», «Создать» 90 с) — см. `AGENT_STUDIO_PUBLISH_20261021_RU.md`, build v16.

## Архитектура ввода

```
runItems → metadata (youtube-studio-upload.js)
  → field(scope, 'description')
  → typeText (studio-editor-controls.js)
       activateEditor → Ctrl+A Delete → чанки pressSequentially
       waitTyped (poll innerText)
       Tab → финальная waitTyped
```

Повтор **только поля** (до 3 попыток для описания), не файла и не Publish.

## Изменения v18

- **`fieldSame`**: одна логика сравнения для `waitTyped` и проверки перед чанком (равенство, trim, prefix/suffix).
- После каждого чанка **`prefix` берётся из DOM** (`waitTyped` возвращает фактический текст), а не только из накопленной строки клавиатуры.
- Логи этапа `metadata_description`: `chunk`, `chunk_ok`, `desync`, `retry` (видны в progress worker и в тексте ошибки при реальной замене поля).
- Описание: чанки **40**, delay **≥22 ms**, таймаут чанка **30 s**, **3** попытки, финал **35 s**.

## Автопроверка

```powershell
cd D:\VideoBatch\repo
node tests/browser/studio-editor-focus.js
node tests/browser/studio-state-flow.js
```

Реальный аккаунт @traderprdp с полным SEO-описанием **не прогонялся** в CI.

## Файлы

- `tools/uploader/studio-editor-controls.js` — ввод и сверка
- `tools/uploader/youtube-studio-upload.js` — BUILD, metadata_description, diagnostics JSON
- `tools/uploader/studio-browser.js` — post-publish, «Создать»
- `tools/uploader/worker.js` — WORKER_BUILD, прогресс в UI
- Диагностика на ПК: `%LOCALAPPDATA%\VideoBatchDesktop\diagnostics\youtube-studio-*.json`, лог `youtube-*.log`

## Направления для более сильной модели (если v18 снова упадёт)

1. Снять **readback** описания после Tab и сравнить с `norm(item.description)`; при расхождении ≤N символов — warn, не fail (явная политика).
2. Рассмотреть **trusted paste** или execCommand только для description (продуктовое исключение из keyboard-only).
3. Убедиться, что локator `#description-textarea #textbox` всегда один элемент (в diagnostics иногда два `#textbox` с разной `length`).
4. Тест fixture: описание ровно **1614** символов с `\n` и emoji, как в production bank.

## Kit на ПК пользователя

После pull/sync скопировать `tools/uploader/*.js` в `D:\VideoBatch\VideoBatch\tools\uploader\` или взять из Actions. EXE пересборка не обязательна для правок JS.
