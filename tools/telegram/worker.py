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

VERSION = '2026-10-15-telegram-v1'
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
        parts = text.split(':')
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


def api_data(meta, default_id=0, default_hash=''):
    api_id = meta.get('api_id', meta.get('app_id', default_id))
    api_hash = meta.get('api_hash', meta.get('app_hash', default_hash))
    if not str(api_id).isdigit() or int(api_id) <= 0 or not re.fullmatch(r'[0-9a-fA-F]{32}', str(api_hash)):
        raise ValueError('Для .session нужен парный JSON с api_id/api_hash или настройки API приложения')
    return int(api_id), api_hash


def record(session, meta, label, defaults):
    from telethon.sessions import StringSession
    s = StringSession(session)
    if not s.auth_key or len(s.auth_key.key) != 256:
        raise ValueError('В сессии нет действующего ключа авторизации')
    api_id, api_hash = api_data(meta, defaults.get('api_id', 0), defaults.get('api_hash', ''))
    return dict(id=hashlib.sha256(s.auth_key.key).hexdigest(), session=session,
                api_id=api_id, api_hash=api_hash, name=label,
                device=str(meta.get('device', meta.get('device_model', 'VideoBatch Desktop'))),
                system=str(meta.get('sdk', meta.get('system_version', 'Windows'))),
                app_version=str(meta.get('app_version', '1.0')),
                lang=str(meta.get('lang_code', meta.get('lang_pack', 'en'))),
                system_lang=str(meta.get('system_lang_code', 'en')),
                proxy='', status='unchecked', country='', username='', phone='', note='')


def from_sqlite(path, defaults):
    from telethon.crypto import AuthKey
    from telethon.sessions import MemorySession, StringSession
    meta = {}
    companion = path.with_suffix('.json')
    if companion.exists():
        if companion.stat().st_size > MAX_INPUT:
            raise ValueError('Слишком большой JSON аккаунта')
        meta = json.loads(companion.read_text(encoding='utf-8-sig'))
        if not isinstance(meta, dict):
            raise ValueError('JSON аккаунта должен быть объектом')
    # Read-only URI, snapshot into memory. No .session-journal, WAL or source mutation.
    with contextlib.closing(sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)) as db:
        rows = db.execute('SELECT dc_id, server_address, port, auth_key FROM sessions').fetchmany(2)
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


async def from_tdata(path, passcode=''):
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
        row = record(StringSession.save(memory), meta, 'TData ' + str(acc.UserId), {})
        row['user_id'] = acc.UserId
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


async def import_paths(job):
    seen = set()
    added = 0
    failures = 0
    async def load(path, depth=0, in_archive=False):
        nonlocal added, failures
        cancelled()
        try:
            if depth > 10:
                raise ValueError("Слишком глубокая структура папок")
            if path.is_symlink():
                raise ValueError("Ссылки на папки не импортируются")
            if path.is_dir():
                if (path / 'key_data').is_file() or (path / 'key_datas').is_file():
                    records = await from_tdata(path, job.get('passcode', ''))
                else:
                    children = sorted(p for p in path.iterdir() if p.is_dir() or p.suffix.lower() in ('.session', '.zip'))
                    # A selected parent may contain several account folders. Reparse points rejected.
                    for child in children:
                        if child.is_symlink():
                            continue
                        await load(child, depth + 1, in_archive)
                    return
            elif path.suffix.lower() == '.zip':
                if in_archive:
                    raise ValueError('Вложенные ZIP не поддерживаются')
                with tempfile.TemporaryDirectory(prefix='import-', dir=job['temp']) as tmp:
                    extract_zip(path, Path(tmp))
                    await load(Path(tmp), depth + 1, True)
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
            # Do not echo filenames or exception strings: may contain phone/session keys.
            emit('import_error', text=str(exc) if isinstance(exc, ValueError) else 'Не удалось импортировать один источник', code=type(exc).__name__)
    for p in job.get('paths', []):
        await load(Path(p))
    emit('summary', text=f'Импортировано: {added}; ошибок источников: {failures}', imported=added, failures=failures)
    if added == 0:
        raise ValueError('Нет импортированных аккаунтов. Нужны .session + JSON, TData или ZIP с ними; проверьте API/код-пароль')


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
    if isinstance(exc, (errors.UserDeactivatedBanError, errors.UserDeactivatedError)):
        return 'blocked', 'Telegram сообщил о блокировке аккаунта', 0
    if isinstance(exc, (errors.AuthKeyUnregisteredError, errors.SessionRevokedError, errors.SessionExpiredError, errors.AuthKeyDuplicatedError)):
        return 'unauthorized', 'Сессия недействительна; импортируйте актуальную сессию', 0
    if isinstance(exc, (errors.UserPrivacyRestrictedError, errors.UserNotMutualContactError, errors.UserChannelsTooMuchError)):
        return 'privacy', 'Приглашение недоступно из-за ограничений пользователя', 0
    if isinstance(exc, errors.ChatAdminRequiredError):
        return 'permissions', 'У аккаунта нет права приглашать в выбранную группу/канал', 0
    if isinstance(exc, (TimeoutError, OSError, ConnectionError, asyncio.TimeoutError)):
        return 'network', 'Нет соединения: проверьте прокси и сеть', 0
    return 'error', 'Операция не завершена; код ошибки указан в журнале', 0


async def connect(client):
    cancelled()
    await asyncio.wait_for(client.connect(), 35)
    if not await asyncio.wait_for(client.is_user_authorized(), 35):
        raise ValueError('Сессия не авторизована; войдите через официальный клиент и импортируйте её заново')
    me = await asyncio.wait_for(client.get_me(), 35)
    if me is None or me.bot:
        raise ValueError('Нужна авторизованная пользовательская сессия')
    return me


async def check_accounts(job):
    for account in job.get('accounts', []):
        cancelled()
        client = None
        try:
            client = client_for(account)
            me = await connect(client)
            name = ' '.join(filter(None, [me.first_name, me.last_name])) or str(me.id)
            emit('account', id=account['id'], status='ready', name=name,
                 user_id=me.id, username=me.username or '', phone=me.phone or '',
                 text='Сессия работает')
            if job['action'] == 'warmup':
                # Read-only session diagnostic, no view counters, fake reactions, marks-read or messages.
                dialogs = await asyncio.wait_for(client.get_dialogs(limit=20), 35)
                emit('log', id=account['id'], text=f'Прогрев: доступно диалогов — {len(dialogs)}; соединение проверено')
        except StopJob:
            raise
        except Exception as exc:
            state, text, seconds = error_info(exc)
            if isinstance(exc, ValueError):
                state, text = 'configuration', 'Проверьте формат сессии, API и прокси'
                if 'не авторизована' in str(exc):
                    state, text = 'unauthorized', 'Сессия не авторизована'
            emit('account', id=account['id'], status=state, text=text, seconds=seconds, code=type(exc).__name__)
        finally:
            if client:
                await client.disconnect()


def usernames(text):
    result = []
    for item in re.split(r'[\s,;]+', text.strip()):
        if not item:
            continue
        item = re.sub(r'^https?://(?:www\.)?t\.me/', '', item, flags=re.I).strip('@').rstrip('/')
        if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_]{3,31}', item):
            raise ValueError('Укажите @username пользователей, по одному в строке')
        if item.lower() not in [v.lower() for v in result]:
            result.append(item)
    if not result:
        raise ValueError('Список пользователей пуст')
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


async def invite(job):
    from telethon import errors, functions, types
    accounts = job.get('accounts', [])
    if len(accounts) != 1:
        raise ValueError('Для приглашений выберите один аккаунт; ошибки не обходятся сменой аккаунта')
    names = usernames(job.get('users', ''))
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
        me = await connect(client)
        target_text = job.get('target', '').strip()
        if re.fullmatch(r'-?\d+', target_text):
            # Resolve known private chats from current dialogs, no joining by invite links.
            await asyncio.wait_for(client.get_dialogs(limit=100), 35)
            target_text = int(target_text)
        target = await asyncio.wait_for(client.get_entity(target_text), 35)
        if not isinstance(target, types.Channel):
            raise ValueError('Выберите существующую супергруппу/канал; обычные чаты пока не поддерживаются')
        if target.creator is not True and (not target.admin_rights or not target.admin_rights.invite_users):
            raise ValueError('Для приглашений нужны права администратора выбранной группы/канала')
        done = 0
        for i, name in enumerate(names):
            cancelled()
            user = await asyncio.wait_for(client.get_entity(name), 35)
            if not isinstance(user, types.User) or user.bot or user.deleted or user.id == me.id:
                emit('invite', username=name, state='skipped', text='Пропущен недоступный пользователь')
                continue
            if not ledger.reserve(target.id, user.id, account['id']):
                emit('invite', username=name, state='skipped', text='Уже есть в истории; повтор автоматически не отправляется')
                continue
            try:
                result = await asyncio.wait_for(client(functions.channels.InviteToChannelRequest(target, [user])), 45)
                missing = getattr(result, 'missing_invitees', []) or []
                if missing:
                    ledger.finish(target.id, user.id, 'privacy')
                    emit('invite', username=name, state='privacy', text='Telegram не добавил пользователя; приватность/ограничение')
                else:
                    # A successful RPC response alone is insufficient on newer Telegram layers.
                    await asyncio.wait_for(client(functions.channels.GetParticipantRequest(target, user)), 35)
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
        emit('summary', text=f'Подтверждено приглашений: {done}')
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
        else:
            raise ValueError('Неизвестное действие Telegram')
        emit('complete', text='Задача завершена')


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stderr.reconfigure(encoding='utf-8')
    # No third-party DEBUG logger or traceback containing credentials on stdout/stderr.
    import logging
    logging.disable(logging.CRITICAL)
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
