"""Local Telegram worker. Credentials travel over stdin only; no unattended login.
Import never connects. Network operations use a MemorySession, never source files.
"""
import asyncio
import contextlib
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import stat
import sys
import tempfile
import threading
import time
import zipfile
from urllib.parse import urlsplit, unquote

VERSION = '2026-10-16-telegram-polish-v1'
PROXY_TIMEOUT = 7
ACCOUNT_TIMEOUT = 22
CANCEL = threading.Event()
MAX_ARCHIVE = 256 * 1024 * 1024
MAX_FILES = 10000
MAX_INPUT = 8 * 1024 * 1024


class StopJob(Exception):
    pass


def emit(event, **data):
    print(json.dumps(dict(event=event, **data), ensure_ascii=False), flush=True)


def cancelled():
    if CANCEL.is_set():
        raise StopJob('Остановлено')


async def pause(seconds):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        cancelled()
        await asyncio.sleep(min(.2, max(0, deadline - time.monotonic())))


def watch_input():
    for line in sys.stdin:
        if line.strip() == 'cancel':
            CANCEL.set()


def parse_proxy(value):
    """No direct fallback for a configured proxy. Preserve credentials, IPv6, DNS."""
    import socks
    text = (value or '').strip()
    if not text:
        return None
    if '://' not in text:
        parts = text.split(':', 3)
        if len(parts) == 4:
            host, port, user, password = parts
            if not host or not port.isdigit():
                raise ValueError('Неверный адрес прокси')
            return dict(proxy_type=socks.SOCKS5, addr=host, port=valid_port(port),
                        username=user, password=password, rdns=True)
        text = 'socks5://' + text
    parsed = urlsplit(text)
    kind = {'socks5': socks.SOCKS5, 'socks4': socks.SOCKS4, 'http': socks.HTTP}.get(parsed.scheme.lower())
    if kind is None or not parsed.hostname or parsed.path not in ('', '/') or parsed.query or parsed.fragment:
        raise ValueError('Прокси: socks5://, socks4:// или http://host:port')
    try:
        port = valid_port(parsed.port)
    except (TypeError, ValueError):
        raise ValueError('Неверный порт прокси') from None
    return dict(proxy_type=kind, addr=parsed.hostname, port=port,
                username=unquote(parsed.username) if parsed.username else None,
                password=unquote(parsed.password) if parsed.password else None, rdns=True)


def valid_port(port):
    p = int(port)
    if not 1 <= p <= 65535:
        raise ValueError('Неверный порт прокси')
    return p


def proxy_connector(value):
    from python_socks.async_.asyncio import Proxy
    from python_socks import ProxyType
    import socks
    p = parse_proxy(value)
    if p is None:
        return None
    return Proxy(proxy_type={socks.SOCKS5: ProxyType.SOCKS5, socks.SOCKS4: ProxyType.SOCKS4,
                             socks.HTTP: ProxyType.HTTP}[p['proxy_type']],
                 host=p['addr'], port=p['port'], username=p['username'],
                 password=p['password'], rdns=True)


async def probe_proxy(value, host='149.154.167.51', port=443):
    """Actual authenticated tunnel to a Telegram DC, not just ping/TCP proxy port."""
    if not value:
        return dict(status='direct', text='Без прокси', latency_ms=0)
    start = time.monotonic()
    sock = None
    try:
        connector = proxy_connector(value)
        # Outer deadline also bounds resolver/handshake delays, not only TCP connect.
        sock = await asyncio.wait_for(connector.connect(dest_host=host, dest_port=port, timeout=PROXY_TIMEOUT), PROXY_TIMEOUT)
        return dict(status='ready', text='Туннель до Telegram доступен',
                    latency_ms=int((time.monotonic()-start)*1000))
    except asyncio.CancelledError:
        raise
    except Exception as exc:
        from python_socks import ProxyError, ProxyConnectionError, ProxyTimeoutError
        if isinstance(exc, ValueError):
            code, text = 'configuration', 'Неверный формат прокси: проверьте протокол, адрес и порт'
        elif isinstance(exc, (TimeoutError, ProxyTimeoutError)):
            code, text = 'timeout', f'Прокси не открыл туннель до Telegram за {PROXY_TIMEOUT} сек. Проверьте протокол и доступ к Telegram'
        elif isinstance(exc, ProxyConnectionError):
            code, text = 'connection', 'Не удалось подключиться к адресу прокси'
        elif isinstance(exc, ProxyError):
            code, text = 'handshake', 'Прокси отказал: проверьте протокол, логин/пароль и доступ до Telegram'
        else:
            code, text = 'network', 'Не удалось открыть туннель через прокси'
        return dict(status='failed', text=text, code=code, diagnostic=type(exc).__name__, latency_ms=int((time.monotonic()-start)*1000))
    finally:
        if sock is not None:
            sock.close()


async def account_route(account):
    value = account.get('proxy', '')
    host = '149.154.167.51'
    if value and account.get('session'):
        from telethon.sessions import StringSession
        host = StringSession(account['session']).server_address
    emit('stage', id=account['id'], text='Проверка прокси…' if value else 'Подключение без прокси…')
    result = await probe_proxy(value, host)
    emit('proxy', id=account['id'], proxy_key=hashlib.sha256(value.strip().encode()).hexdigest(), **result)
    if result['status'] == 'failed':
        raise RouteError(result['text'])


class RouteError(ConnectionError):
    pass


async def check_proxies(job):
    ready = failed = 0
    for p in job.get('proxies', []):
        cancelled()
        result = await probe_proxy(p.get('url', ''))
        emit('proxy', proxy_id=p['id'], proxy_key=hashlib.sha256(p.get('url','').strip().encode()).hexdigest(), **result)
        ready += result['status'] == 'ready'
        failed += result['status'] == 'failed'
    emit('summary', text=f'Прокси: доступны {ready}; ошибок {failed}', failures=failed)


def api_data(meta, default_id=0, default_hash=''):
    api_id = meta.get('api_id', meta.get('app_id', default_id))
    api_hash = meta.get('api_hash', meta.get('app_hash', default_hash))
    if not str(api_id).isdigit() or int(api_id) <= 0 or not re.fullmatch(r'[0-9a-fA-F]{32}', str(api_hash)):
        raise ValueError('Для .session нужен парный JSON с api_id/api_hash или настройки API приложения')
    return int(api_id), api_hash


def read_metadata(path):
    if path.is_symlink() or path.stat().st_size > MAX_INPUT:
        raise ValueError('JSON аккаунта: ссылка или слишком большой файл')
    try:
        meta = json.loads(path.read_text(encoding='utf-8-sig'))
    except (UnicodeError, json.JSONDecodeError):
        raise ValueError('Не удалось прочитать JSON аккаунта') from None
    if not isinstance(meta, dict):
        raise ValueError('JSON аккаунта должен быть объектом')
    return meta


def metadata_api(meta, defaults):
    # A partial supplied pair must never be combined with a different app's hash.
    keys = ('api_id', 'app_id', 'api_hash', 'app_hash')
    if any(k in meta for k in keys):
        return (*api_data(meta), 'json')
    if defaults.get('api_id') or defaults.get('api_hash'):
        return (*api_data(defaults), 'custom')
    from opentele.api import API
    api = API.TelegramDesktop
    return api.api_id, api.api_hash, 'desktop_compat'


def record(session, meta, label, defaults):
    from telethon.sessions import StringSession
    s = StringSession(session)
    if not s.auth_key or len(s.auth_key.key) != 256:
        raise ValueError('В сессии нет действующего ключа авторизации')
    api_id, api_hash, source = metadata_api(meta, defaults)
    uid = meta.get('user_id', 0)
    if uid and (not str(uid).isdigit() or int(uid) <= 0):
        raise ValueError('В JSON неверный user_id')
    return dict(id=hashlib.sha256(s.auth_key.key).hexdigest(), session=session,
                api_id=api_id, api_hash=api_hash, api_source=source, name=label,
                expected_user_id=int(uid or 0),
                device=str(meta.get('device', meta.get('device_model', 'Telegram Desktop'))),
                system=str(meta.get('sdk', meta.get('system_version', 'Windows'))),
                app_version=str(meta.get('app_version', '1.0')),
                lang=str(meta.get('lang_code', meta.get('lang_pack', 'en'))),
                system_lang=str(meta.get('system_lang_code', 'en')),
                proxy='', status='unchecked', country='', username='',
                phone=str(meta.get('phone', meta.get('phone_number', ''))), note='')


def from_sqlite(path, defaults):
    from telethon.crypto import AuthKey
    from telethon.sessions import MemorySession, StringSession
    meta = {}
    companion = path.with_suffix('.json')
    if companion.exists():
        meta = read_metadata(companion)
    # Read-only URI, snapshot into memory. No .session-journal, WAL or source mutation.
    try:
        with contextlib.closing(sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)) as db:
            rows = db.execute('SELECT dc_id, server_address, port, auth_key FROM sessions').fetchmany(2)
    except sqlite3.DatabaseError:
        raise ValueError('Файл не является поддерживаемой Telethon SQLite-сессией; для другого формата используйте TData') from None
    if len(rows) != 1:
        raise ValueError('Ожидалась одна Telethon SQLite-сессия')
    dc, host, port, key = rows[0]
    if not isinstance(key, bytes) or len(key) != 256 or not 1 <= int(dc) <= 5:
        raise ValueError('Нет авторизованной сессии')
    # Never connect to arbitrary endpoints embedded in an untrusted SQLite file.
    allowed = {'149.154.175.50', '149.154.167.51', '149.154.175.100', '149.154.167.91', '149.154.171.5'}
    if str(host) not in allowed or int(port) != 443:
        raise ValueError('Неизвестный DC/порт в сессии: импорт остановлен')
    s = MemorySession()
    s.set_dc(int(dc), str(host), int(port))
    s.auth_key = AuthKey(key)
    return [record(StringSession.save(s), meta, str(meta.get('first_name') or path.stem), defaults)]


async def from_tdata(path, passcode='', companion=None, defaults=None):
    from opentele.td import TDesktop
    from telethon.crypto import AuthKey
    from telethon.sessions import MemorySession, StringSession
    from opentele.exception import OpenTeleException
    try:
        td = TDesktop(str(path), passcode=passcode)
    except OpenTeleException:
        raise ValueError("TData не прочитана: неверный локальный код-пароль или повреждены файлы") from None
    if not td.isLoaded() or not td.accounts:
        raise ValueError('TData не загружена; проверьте папку и локальный код-пароль')
    supplied = read_metadata(companion) if companion else {}
    if supplied and len(td.accounts) != 1:
        raise ValueError('JSON нельзя однозначно связать с многопользовательской TData')
    result = []
    # opentele 1.15.1 FromTDesktop has an unbound auth_session for MemorySession.
    # Read the parsed authorization key directly: no SQLite output and no connect.
    servers = {1: '149.154.175.50', 2: '149.154.167.51', 3: '149.154.175.100',
               4: '149.154.167.91', 5: '149.154.171.5'}
    for acc in td.accounts:
        api = acc.api
        dc = int(acc.MainDcId)
        if dc not in servers:
            raise ValueError('Неизвестный DC в TData')
        memory = MemorySession()
        memory.set_dc(dc, servers[dc], 443)
        memory.auth_key = AuthKey(acc.authKey.key)
        meta = dict(api_id=api.api_id, api_hash=api.api_hash,
                    device_model=api.device_model or 'Telegram Desktop',
                    system_version=api.system_version or 'Windows',
                    app_version=api.app_version or '1.0',
                    lang_code=api.lang_code or 'en', system_lang_code=api.system_lang_code or 'en')
        # TData carries the key/DC/user ID, not the original device/API metadata.
        # The adjacent JSON wins only after its explicit identity is checked.
        expected = supplied.get('user_id')
        if expected is not None and str(expected) != str(acc.UserId):
            raise ValueError('user_id в JSON не совпадает с TData; файлы не объединены')
        if supplied:
            merged = {k: v for k, v in meta.items() if k not in ('api_id', 'api_hash')}
            merged.update(supplied)
        else:
            merged = meta
        row = record(StringSession.save(memory), merged,
                     str(supplied.get('first_name') or 'TData ' + str(acc.UserId)), defaults or {})
        if not supplied:
            row['api_source'] = 'tdata_desktop'
        row['user_id'] = row['expected_user_id'] = acc.UserId
        result.append(row)
    return result


def extract_zip(path, target):
    count = 0
    size = 0
    with zipfile.ZipFile(path) as archive:
        for item in archive.infolist():
            cancelled()
            count += 1
            size += item.file_size
            name = item.filename.replace('\\', '/')
            if count > MAX_FILES or size > MAX_ARCHIVE:
                raise ValueError('Архив превышает 256 МБ или 10 000 файлов')
            parts = Path(name).parts
            if not name or name.startswith('/') or '..' in parts or ':' in name or stat.S_ISLNK(item.external_attr >> 16):
                raise ValueError('Недопустимый путь в ZIP')
            dest = target.joinpath(*parts)
            if item.is_dir():
                dest.mkdir(parents=True, exist_ok=True)
            else:
                dest.parent.mkdir(parents=True, exist_ok=True)
                # Exclusive creation rejects duplicate names and collisions.
                with archive.open(item) as src, dest.open('xb') as out:
                    copied = 0
                    while True:
                        chunk = src.read(65536)
                        if not chunk:
                            break
                        copied += len(chunk)
                        if copied > item.file_size or copied > MAX_ARCHIVE:
                            raise ValueError('Размер файла ZIP не совпадает')
                        out.write(chunk)


def tdata_companion(path):
    """Only exact wrapper/account names, never the first arbitrary JSON in Downloads."""
    candidates = set()
    for folder in (path, path.parent):
        stem = re.sub(r'_tdata$', '', folder.name, flags=re.I)
        if stem.lower() == 'tdata':
            continue
        for candidate in (folder.with_name(stem + '.json'), folder / (stem + '.json')):
            if candidate.is_file():
                if candidate.is_symlink():
                    raise ValueError('Парный JSON не должен быть ссылкой')
                candidates.add(candidate.resolve())
    if len(candidates) > 1:
        raise ValueError('Найдено несколько JSON для TData; оставьте один парный файл')
    return next(iter(candidates), None)


def json_source(path):
    """LZT exports: name.json beside name.session or name_tdata/tdata."""
    candidates = [path.with_suffix('.session'), path.with_name(path.stem + '_tdata')]
    # Also support a named TData directory beside its JSON.
    named = path.with_suffix('')
    if named.is_dir():
        candidates.append(named)
    found = [p for p in candidates if p.exists()]
    if len(found) != 1:
        raise ValueError('Для JSON нужен один парный .session или папка с тем же именем _tdata')
    return found[0]


async def import_paths(job):
    seen, visited = set(), set()
    added = failures = count = 0
    async def load(path, depth=0, in_archive=False):
        nonlocal added, failures, count
        cancelled()
        try:
            count += 1
            if count > MAX_FILES or depth > 10:
                raise ValueError('Слишком много файлов или слишком глубокая структура папок')
            if path.is_symlink():
                raise ValueError('Ссылки на файлы и папки не импортируются')
            canonical = str(path.resolve())
            if canonical in visited:
                return
            visited.add(canonical)
            if not path.exists():
                raise ValueError('Выбранный источник не найден')
            if path.is_dir():
                if (path / 'key_data').is_file() or (path / 'key_datas').is_file():
                    records = await from_tdata(path, job.get('passcode', ''), tdata_companion(path), job)
                else:
                    children = []
                    for child in sorted(path.iterdir()):
                        if len(children) >= MAX_FILES:
                            raise ValueError('Слишком много файлов в папке')
                        if child.is_dir() or child.suffix.lower() in ('.session', '.zip'):
                            children.append(child)
                        # JSON is metadata, not an additional account or object.
                        elif child.suffix.lower() == '.json':
                            # Only discover matching sources; unrelated JSON is ignored.
                            try:
                                source = json_source(child)
                            except ValueError:
                                continue
                            children.append(source)
                    for child in children:
                        if not child.is_symlink():
                            await load(child, depth + 1, in_archive)
                    return
            elif path.suffix.lower() == '.zip':
                if in_archive:
                    raise ValueError('Вложенные ZIP не поддерживаются')
                with tempfile.TemporaryDirectory(prefix='import-', dir=job['temp']) as tmp:
                    extract_zip(path, Path(tmp))
                    await load(Path(tmp), depth + 1, True)
                return
            elif path.suffix.lower() == '.json':
                # Validate the chosen JSON before resolving its adjacent source.
                read_metadata(path)
                await load(json_source(path), depth + 1, in_archive)
                return
            elif path.suffix.lower() == '.session':
                records = from_sqlite(path, job)
            else:
                return
            for account in records:
                if account['id'] in seen:
                    continue
                seen.add(account['id'])
                emit('imported', account=account)
                added += 1
        except StopJob:
            raise
        except Exception as exc:
            failures += 1
            emit('import_error', text=str(exc) if isinstance(exc, ValueError) else 'Не удалось импортировать один источник', code=type(exc).__name__)
    for p in job.get('paths', []):
        await load(Path(p))
    emit('summary', text=f'Импортировано: {added}; ошибок источников: {failures}', imported=added, failures=failures)
    if added == 0:
        raise ValueError('Нет импортированных аккаунтов. Выберите TData, ZIP, .session или парный JSON; проверьте целостность файлов')


def client_for(account):
    from telethon import TelegramClient
    from telethon.sessions import StringSession
    api_id, api_hash = api_data(account)
    proxy = parse_proxy(account.get('proxy'))
    if proxy is not None:
        import python_socks  # Required: Telethon 1.45 can otherwise ignore configured proxies.
    session = StringSession(account['session'])  # MemorySession, no writes of auth key.
    return TelegramClient(session, api_id=api_id, api_hash=api_hash, proxy=proxy, timeout=15,
                          connection_retries=0, request_retries=0, auto_reconnect=False,
                          flood_sleep_threshold=0, receive_updates=False,
                          device_model=account.get('device') or 'VideoBatch Desktop',
                          system_version=account.get('system') or 'Windows',
                          app_version=account.get('app_version') or '1.0',
                          lang_code=account.get('lang') or 'en',
                          system_lang_code=account.get('system_lang') or 'en')


def error_info(exc):
    from telethon import errors
    if isinstance(exc, errors.FloodWaitError):
        return 'flood_wait', 'Telegram требует подождать', int(exc.seconds)
    if isinstance(exc, errors.PeerFloodError):
        return 'restricted', 'Telegram ограничил приглашения; задача остановлена', 0
    if isinstance(exc, errors.UserDeactivatedBanError):
        return 'blocked', 'Telegram сообщил о блокировке аккаунта', 0
    if isinstance(exc, errors.UserDeactivatedError):
        return 'deactivated', 'Аккаунт удалён/деактивирован в Telegram', 0
    if isinstance(exc, (errors.AuthKeyUnregisteredError, errors.SessionRevokedError, errors.SessionExpiredError, errors.AuthKeyDuplicatedError)):
        return 'unauthorized', 'Сессия недействительна; импортируйте актуальную сессию', 0
    if isinstance(exc, (errors.UserPrivacyRestrictedError, errors.UserNotMutualContactError, errors.UserChannelsTooMuchError)):
        return 'privacy', 'Приглашение недоступно из-за ограничений пользователя', 0
    if isinstance(exc, errors.ChatAdminRequiredError):
        return 'permissions', 'У аккаунта нет права приглашать в выбранную группу/канал', 0
    if isinstance(exc, RouteError):
        return 'network', str(exc), 0
    from python_socks import ProxyError
    if isinstance(exc, ProxyError):
        return 'network', 'Прокси отказал при соединении с Telegram; проверьте авторизацию и доступ', 0
    if isinstance(exc, (TimeoutError, OSError, ConnectionError, asyncio.TimeoutError)):
        return 'network', 'Нет соединения: проверьте прокси и сеть', 0
    return 'error', 'Операция не завершена; код ошибки указан в журнале', 0


async def connect(client, account=None):
    cancelled()
    async def handshake():
        await client.connect()
        if not await client.is_user_authorized():
            raise ValueError('Сессия не авторизована; войдите через официальный клиент и импортируйте её заново')
        me = await client.get_me()
        if me is None or me.bot:
            raise ValueError('Нужна авторизованная пользовательская сессия')
        expected = (account or {}).get('expected_user_id')
        if expected and int(expected) != me.id:
            raise ValueError('user_id аккаунта не совпадает с импортированными данными')
        return me
    return await asyncio.wait_for(handshake(), ACCOUNT_TIMEOUT)


async def check_accounts(job):
    ready = failed = 0
    for account in job.get('accounts', []):
        cancelled()
        client = None
        try:
            await account_route(account)
            emit('stage', id=account['id'], text='Проверка аккаунта в Telegram…')
            client = client_for(account)
            me = await connect(client, account)
            ready += 1
            name = ' '.join(filter(None, [me.first_name, me.last_name])) or str(me.id)
            emit('account', id=account['id'], status='ready', name=name,
                 user_id=me.id, username=me.username or '', phone=me.phone or '',
                 text='Telegram подтвердил аккаунт')
            if job['action'] == 'warmup':
                # Read-only session diagnostic, no view counters, fake reactions, marks-read or messages.
                dialogs = await asyncio.wait_for(client.get_dialogs(limit=20), 35)
                emit('log', id=account['id'], text=f'Прогрев: доступно диалогов — {len(dialogs)}; соединение проверено')
        except StopJob:
            raise
        except Exception as exc:
            failed += 1
            state, text, seconds = error_info(exc)
            if isinstance(exc, ValueError):
                state, text = 'configuration', 'Проверьте формат сессии, API и прокси'
                if 'не авторизована' in str(exc):
                    state, text = 'unauthorized', 'Сессия не авторизована'
                elif 'user_id' in str(exc):
                    state, text = 'configuration', 'user_id не совпал: проверьте парный JSON'
            emit('account', id=account['id'], status=state, text=text, seconds=seconds, code=type(exc).__name__)
        finally:
            if client:
                await client.disconnect()

    emit('summary', text=f'Проверка: доступны {ready}; ошибок {failed}', ready=ready, failures=failed)


def usernames(text):
    result = []
    for item in re.split(r'[\s,;]+', text.strip()):
        if not item:
            continue
        item = re.sub(r'^https?://(?:www\.)?t\.me/', '', item, flags=re.I).strip('@').rstrip('/')
        if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{3,31}', item) and not (item.isascii() and item.isdigit() and 0 < int(item) < 2**63):
            raise ValueError('Укажите @username, t.me/username или положительный ID пользователя')
        if item.lower() not in [v.lower() for v in result]:
            result.append(item)
    if not result:
        raise ValueError('Список пользователей пуст')
    if len(result)>10000:
        raise ValueError('В списке больше 10 000 пользователей')
    return result


class InviteLedger:
    def __init__(self, path):
        self.db = sqlite3.connect(path)
        self.db.execute('CREATE TABLE IF NOT EXISTS invites (target INTEGER, user INTEGER, account TEXT, state TEXT, stamp INTEGER, PRIMARY KEY(target,user))')
        self.db.commit()

    def reserve(self, target, user, account):
        try:
            self.db.execute('INSERT INTO invites VALUES (?,?,?,?,?)', (target, user, account, 'pending', int(time.time())))
            self.db.commit()  # durable BEFORE the request; no resend after a lost response.
            return True
        except sqlite3.IntegrityError:
            return False

    def finish(self, target, user, state):
        self.db.execute('UPDATE invites SET state=?,stamp=? WHERE target=? AND user=?', (state, int(time.time()), target, user))
        self.db.commit()

    def close(self):
        self.db.close()


async def resolve_target(client, text):
    from telethon import functions, types
    text = text.strip()
    private = re.fullmatch(r'(?:https?://)?t\.me/(?:\+|joinchat/)([A-Za-z0-9_-]+)', text, re.I)
    if private:
        result = await asyncio.wait_for(client(functions.messages.CheckChatInviteRequest(private[1])), 35)
        if not isinstance(result, types.ChatInviteAlready):
            raise ValueError('Аккаунт не состоит в этой группе. Вступите вручную и выдайте права добавления участников')
        target = result.chat
    else:
        if re.fullmatch(r'-?\d+', text):
            await asyncio.wait_for(client.get_dialogs(limit=100), 35)
            text = int(text)
        target = await asyncio.wait_for(client.get_entity(text), 35)
    if not isinstance(target, (types.Channel, types.Chat)):
        raise ValueError('Нужна ссылка на группу/канал, а не на пользователя или сообщение')
    if not target.creator and not (getattr(target, 'admin_rights', None) and target.admin_rights.invite_users):
        raise ValueError('У выбранного аккаунта нет прав добавлять участников. Для теста выдайте ему права администратора в вашей группе')
    return target


async def invite_preview(job):
    accounts = job.get('accounts', [])
    if len(accounts) != 1:
        raise ValueError('Выберите одного отправителя')
    account = accounts[0]
    client = None
    me = None
    try:
        await account_route(account)
        client = client_for(account)
        me = await connect(client, account)
        emit('account', id=account['id'], status='ready', user_id=me.id, text='Telegram подтвердил аккаунт')
        target = await resolve_target(client, job.get('target',''))
        emit('target', title=target.title, target_id=target.id, text='Права подтверждены: '+target.title)
        emit('summary', text='Права подтверждены; приглашения не отправлялись')
    except Exception as exc:
        if me is None:
            state, text, seconds = error_info(exc)
            emit('account', id=account['id'], status=state, text=str(exc) if isinstance(exc, ValueError) else text, seconds=seconds, code=type(exc).__name__)
        raise
    finally:
        if client:
            await client.disconnect()


async def web_login(job):
    import base64
    from telethon import functions
    accounts = job.get('accounts', [])
    token = job.get('login_token','')
    if len(accounts)!=1 or not re.fullmatch(r'[A-Za-z0-9_-]{16,1024}', token):
        raise ValueError('Не удалось прочитать действующий QR-код Telegram Web')
    account = accounts[0]
    client = client_for(account)
    try:
        try:
            await account_route(account)
            me = await connect(client, account)
        except Exception as exc:
            state, text, seconds = error_info(exc)
            emit('account', id=account['id'], status=state, text=str(exc) if isinstance(exc, ValueError) else text, seconds=seconds, code=type(exc).__name__)
            raise
        emit('account', id=account['id'], status='ready', text='Telegram подтвердил аккаунт', username=me.username or '', name=me.first_name or '', phone=me.phone or '', user_id=me.id)
        await asyncio.wait_for(client(functions.auth.AcceptLoginTokenRequest(base64.urlsafe_b64decode(token+'='*((-len(token))%4)))), 35)
        emit('summary', text='Вход в Telegram Web подтверждён. Дождитесь загрузки; при запросе 2FA введите пароль в веб-окне')
    finally:
        await client.disconnect()


async def invite(job):
    from telethon import errors, functions, types
    accounts = job.get('accounts', [])
    if len(accounts) != 1:
        raise ValueError('Для приглашений выберите один аккаунт; ошибки не обходятся сменой аккаунта')
    names = usernames(job.get('users', ''))
    limit = int(job.get('limit', 10000))
    if not 1 <= limit <= 10000:
        raise ValueError('Лимит на запуск: от 1 до 10 000 пользователей')
    names = names[:limit]
    delay = int(job.get('delay', 60))
    if not 10 <= delay <= 3600:
        raise ValueError('Задержка: от 10 до 3600 секунд')
    account = accounts[0]
    if account.get('wait_until'):
        from datetime import datetime, timezone
        until = datetime.fromisoformat(account['wait_until'].replace('Z', '+00:00'))
        if until > datetime.now(timezone.utc):
            raise ValueError('Ещё действует сохранённая пауза Telegram; дождитесь её окончания')
    client = client_for(account)
    ledger = InviteLedger(Path(job['root']) / 'invites.sqlite')
    try:
        me = await connect(client, account)
        target = await resolve_target(client, job.get('target', ''))
        emit('target', title=target.title, target_id=target.id, text='Права подтверждены: '+target.title)
        done = attempted = unresolved = skipped = 0
        if any(name.isdigit() for name in names):
            await asyncio.wait_for(client.get_dialogs(limit=100), 35)
            await asyncio.wait_for(client(functions.contacts.GetContactsRequest(hash=0)), 35)
        for i, name in enumerate(names):
            cancelled()
            try:
                reference = types.PeerUser(int(name)) if name.isdigit() else name
                user = await asyncio.wait_for(client.get_entity(reference), 35)
            except (ValueError, errors.UsernameInvalidError, errors.UsernameNotOccupiedError):
                unresolved += 1
                emit('invite', username=name, state='unresolved', text='Не найден. Для незнакомого ID нужен доступ к пользователю; используйте @username')
                continue
            if not isinstance(user, types.User) or user.bot or user.deleted or user.id == me.id:
                skipped += 1
                emit('invite', username=name, state='skipped', text='Пропущен недоступный пользователь')
                continue
            if not ledger.reserve(target.id, user.id, account['id']):
                skipped += 1
                emit('invite', username=name, state='skipped', text='Уже есть в истории; повтор автоматически не отправляется')
                continue
            try:
                attempted += 1
                request = functions.channels.InviteToChannelRequest(target, [user]) if isinstance(target, types.Channel) else functions.messages.AddChatUserRequest(target.id,user,0)
                result = await asyncio.wait_for(client(request), 45)
                missing = getattr(result, 'missing_invitees', []) or []
                if missing:
                    ledger.finish(target.id, user.id, 'privacy')
                    emit('invite', username=name, state='privacy', text='Telegram не добавил пользователя; приватность/ограничение')
                else:
                    # A successful RPC response alone is insufficient on newer Telegram layers.
                    if isinstance(target, types.Channel):
                        membership = await asyncio.wait_for(client(functions.channels.GetParticipantRequest(target, user)), 35)
                        valid = (types.ChannelParticipant, types.ChannelParticipantSelf, types.ChannelParticipantCreator, types.ChannelParticipantAdmin)
                        confirmed = isinstance(getattr(membership, 'participant', None), valid)
                    else:
                        full = await asyncio.wait_for(client(functions.messages.GetFullChatRequest(target.id)),35)
                        confirmed = any(getattr(p,'user_id',None)==user.id for p in getattr(getattr(full.full_chat,'participants',None),'participants',[]))
                    if not confirmed:
                        raise ValueError('Telegram не подтвердил активное участие; нужна ручная проверка')
                    ledger.finish(target.id, user.id, 'confirmed')
                    done += 1
                    emit('invite', username=name, state='confirmed', text='Участие подтверждено')
            except errors.UserAlreadyParticipantError:
                ledger.finish(target.id, user.id, 'already')
                emit('invite', username=name, state='already', text='Уже состоит в группе/канале')
            except Exception as exc:
                state, text, seconds = error_info(exc)
                ledger.finish(target.id, user.id, 'uncertain' if state in ('network', 'error') else state)
                emit('invite', username=name, state=state, text=text, seconds=seconds, code=type(exc).__name__)
                if state != 'privacy':
                    # Stop entire job: no account rotation, retry or silent FloodWait bypass.
                    raise StopJob(text)
            if i + 1 < len(names):
                emit('log', text=f'Ожидание {delay} сек перед следующим пользователем')
                await pause(delay)
        emit('summary', text=f'Приглашено: {done}; попыток: {attempted}; пропущено: {skipped}; не найдены: {unresolved}', confirmed=done, attempted=attempted, unresolved=unresolved)
    except StopJob:
        raise
    except Exception as exc:
        state, text, seconds = error_info(exc)
        emit('account', id=account['id'], status=state, text=str(exc) if isinstance(exc, ValueError) else text, seconds=seconds, code=type(exc).__name__)
        raise
    finally:
        ledger.close()
        await client.disconnect()


@contextlib.contextmanager
def exclusive(root):
    """OS lock released on crash; no concurrent jobs from a second app instance."""
    handle = open(Path(root) / 'worker.lock', 'a+b')
    try:
        handle.seek(0)
        if os.name == 'nt':
            import msvcrt
            if handle.read(1) == b'':
                handle.write(b'0'); handle.flush()
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        handle.close()


async def run(job):
    root = Path(job['root'])
    root.mkdir(parents=True, exist_ok=True)
    job['temp'] = str(root / 'temp')
    Path(job['temp']).mkdir(exist_ok=True)
    with exclusive(root):
        # Remove plaintext extracted archives left by a killed previous worker.
        import shutil
        for old in Path(job['temp']).glob('import-*'):
            if old.is_dir() and not old.is_symlink():
                shutil.rmtree(old)
        emit('version', text=VERSION)
        action = job.get('action')
        if action == 'import':
            await import_paths(job)
        elif action in ('check', 'warmup'):
            await check_accounts(job)
        elif action == 'invite':
            await invite(job)
        elif action == 'invite_preview':
            await invite_preview(job)
        elif action == 'check_proxy':
            await check_proxies(job)
        elif action == 'web_login':
            await web_login(job)
        else:
            raise ValueError('Неизвестное действие Telegram')
        emit('complete', text='Задача завершена')


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    # No third-party DEBUG logger or traceback containing credentials on stdout/stderr.
    import logging
    logging.disable(logging.CRITICAL)
    sys.stdin.reconfigure(encoding='utf-8-sig')
    line = sys.stdin.readline(MAX_INPUT + 1)
    if len(line) > MAX_INPUT:
        emit('error', text='Слишком большое задание'); return 1
    try:
        job = json.loads(line)
        threading.Thread(target=watch_input, daemon=True).start()
        asyncio.run(run(job))
        return 0
    except StopJob as exc:
        emit('stopped', text=str(exc)); return 2
    except Exception as exc:
        state, text, seconds = error_info(exc)
        if isinstance(exc, ValueError):
            text = str(exc)
        emit('error', text=text, code=type(exc).__name__, status=state, seconds=seconds)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
