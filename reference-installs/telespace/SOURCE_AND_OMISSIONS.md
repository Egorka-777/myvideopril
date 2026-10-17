# Telespace — эталон установки

**Источник на ПК:** `D:\Telespace`  
**Ветка:** `ref/telespace-install-20261017`

## В Git

Всё из установки, включая ключи/лицензию (`data/.license`, `data/.session_secret`).

## Не в Git (данные аккаунтов)

| Файл | Причина |
|------|---------|
| `telespace.db`, `telespace.db-shm`, `telespace.db-wal` | SQLite с привязанными Telegram-аккаунтами |

Локальная копия для отладки: `D:\Telespace\telespace.db*`.
