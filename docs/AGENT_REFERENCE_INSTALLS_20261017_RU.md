# Эталоны Telespace и Deskgram2 (2026-10-17)

Две **отдельные ветки** — снимки установок с ПК владельца для доработки Telegram в VideoBatch.

| Ветка | Папка в репо | Исходник на диске |
|-------|----------------|-------------------|
| `ref/telespace-install-20261017` | `reference-installs/telespace/` | `D:\Telespace` |
| `ref/deskgram2-install-20261017` | `reference-installs/deskgram2/` | `C:\Deskgram2` |

## Зачем

В приложении myvideopril нестабильны проверка аккаунтов и прокси. Эти установки **работают у пользователя** — использовать как эталон поведения (таймауты, проверка прокси, структура настроек, UI web-оболочки Deskgram).

## Безопасность

В Git **нет только данных аккаунтов**: `.session`, `*_tdata.json`, `telespace.db*`, `app.db`. Лицензии, HWID, ключи приложения, логи и настройки — в снимке. См. `SOURCE_AND_OMISSIONS.md`.

## С чем сравнивать в myvideopril

- `tools/telegram/` — worker, прокси, импорт
- `docs/AGENT_TELEGRAM_CONNECT_20261016_RU.md` — текущий сценарий импорта
- `src/Telegram*.cs`, `TelegramHealth`, `TelegramProxyCatalog`

## Клон одной ветки

```powershell
git clone --branch ref/deskgram2-install-20261017 --single-branch https://github.com/Egorka-777/myvideopril.git deskgram-ref
git lfs pull
# или ref/telespace-install-20261017
```

`deskgram2/unis.exe` >100 MB — **Git LFS** (`git lfs pull` после clone).
