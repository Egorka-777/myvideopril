# Deskgram2 — эталон установки

**Источник на ПК:** `C:\Deskgram2`  
**Дата снимка:** 2026-10-17  
**Ветка:** `ref/deskgram2-install-20261017`

## Что включено

- `unis.exe`, `launcher.exe` — приложение (`unis.exe` через Git LFS)
- `settings/program_settings.json` — **эталон** таймаутов, ретраев, `check_proxies_use`, `proxy_source`, замены прокси (пароли UI обнулены)
- `data/ports.json`, `data/proxy_geo_cache.json` — порты/кэш гео прокси
- `web/` — статика и identity (faker person.json по локалям)

## Намеренно не в Git

| Что | Где на ПК |
|-----|-----------|
| `sessions/*_tdata.session`, `*_tdata.json` | `C:\Deskgram2\sessions\` |
| `licensekey.txt`, `hwid.txt`, `*.runtime.json` | `C:\Deskgram2\settings\` |
| `data/app.db`, `data/security.key` | SQLite и ключ безопасности |
| Логи и `data/results` с user_id в именах | удалены из снимка |

Папка `sessions/` в репозитории — только структура (`archived/`, `deleted/`).

## Для агента VideoBatch

Сверить блок `settings.settings` (connection_timeout, check_proxies_use, proxy_replacement) с `tools/telegram` и проверкой прокси в UI. Логи успешного прогона смотреть локально в `C:\Deskgram2\data\logs\`.
