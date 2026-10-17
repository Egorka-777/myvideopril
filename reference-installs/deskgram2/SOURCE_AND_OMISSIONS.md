# Deskgram2 — эталон установки

**Источник на ПК:** `C:\Deskgram2`  
**Ветка:** `ref/deskgram2-install-20261017`

## В Git

Полная установка: бинарники, `settings/` (лицензия, HWID, `program_settings.json`), `data/` (логи, кэши, `security.key`, порты), `web/`, ключи — **без** данных Telegram-аккаунтов.

## Не в Git (аккаунты)

| Что | Где локально |
|-----|----------------|
| `data/app.db` | SQLite с аккаунтами |
| `sessions/*_tdata.session`, `sessions/*_tdata.json` | Telethon/TData сессии |

Пустые `sessions/archived`, `sessions/deleted` и служебные файлы без сессий — в репозитории.
