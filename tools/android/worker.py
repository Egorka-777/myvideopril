"""Open only explicitly bound existing AVDs, retaining their always-on VPN.

No device creation, account switching, proxy reconfiguration or VPN stopping.
Proxy credentials arrive on stdin only and are never sent to native commands.
The imported private VPN config is inaccessible on this non-root Play image;
verify its active profile notification and actual proxy endpoint instead.
"""
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import threading
import xml.etree.ElementTree as ET
from urllib.parse import urlsplit

MESSAGES = {
    'cancelled': 'Прогрев Android остановлен пользователем.',
    'invalid_request': 'Некорректные параметры запуска Android.',
    'unbound': 'Для этого Dolphin ID нет привязанного Android-устройства в bindings.json.',
    'tools_missing': 'Не найдены Android SDK или сохранённое Android-устройство PsyDroid.',
    'wrong_device': 'Порт Android занят другим устройством. Запуск остановлен; чужое устройство не изменено.',
    'boot_timeout': 'Android не завершил загрузку. Проверьте окно эмулятора.',
    'vpn_unverified': 'Не подтверждены сохранённый VPN профиля, блокировка сети и соединение с назначенным прокси Dolphin. Запуск TikTok остановлен; VPN не изменён.',
    'app_unverified': 'TikTok не подтвердил открытие. Проверьте Android вручную.',
    'internal_error': 'Ошибка запуска Android. Результат не подтверждён; VPN не изменён.'
}


class OpenError(Exception):
    pass


def emit(event, text='', **fields):
    print(json.dumps(dict(event=event, text=text, **fields), ensure_ascii=True), flush=True)


PACKAGE = 'com.zhiliaoapp.musically'


def classify_session(xml):
    """Only own-profile evidence is positive; return no raw labels or secrets."""
    try:
        root = ET.fromstring(xml)
    except ET.ParseError:
        return 'unknown', 'ui_unavailable', ''
    nodes = [n for n in root.iter('node') if n.get('visible-to-user') != 'false']
    app = [n for n in nodes if n.get('package') == PACKAGE]
    if not app or any(n.get('package') not in (None, '', PACKAGE) for n in nodes):
        return 'unknown', 'ambiguous_ui', ''
    labels = {n.get(k, '').strip().casefold() for n in app for k in ('text', 'content-desc')}
    challenges = ('captcha', 'verify your identity', 'security check', 'verification code',
                  'enter code', 'verify to continue', 'проверка безопасности', 'подтвердите', 'код подтверждения')
    errors = ('loading', 'something went wrong', 'network error', 'no internet', 'try again',
              'загрузка', 'ошибка', 'нет подключения', 'повторите попытку')
    if any(any(marker in label for marker in challenges + errors) for label in labels) or any(
            n.get('class') == 'android.widget.ProgressBar' for n in app):
        return 'unknown', 'manual_or_unavailable', ''
    login = {'log in', 'sign in', 'log in to tiktok', 'sign up for tiktok', 'войти', 'вход в tiktok',
             'вход', 'зарегистрироваться', 'зарегистрироваться в tiktok', 'phone / email / username',
             'телефон / почта / имя пользователя', 'номер телефона или email'}
    if 'for you' in labels or 'для вас' in labels or 'following' in labels or 'подписки' in labels:
        return 'verified', 'feed', ''
    if labels & {'home', 'главная', 'профиль', 'profile'} and not (labels & login):
        if any(n.get('resource-id', '').endswith(':id/mje') for n in app):
            return 'verified', 'tab_bar', ''
    if labels & login or any(n.get('password') == 'true' for n in app):
        return 'not_logged_in', 'login_screen', ''
    identities = {n.get(k, '').strip() for n in app for k in ('text', 'content-desc')
                  if re.fullmatch(r'@[A-Za-z0-9_.]{1,30}', n.get(k, '').strip())}
    if labels & {'edit profile', 'изменить профиль', 'редактировать профиль'} and len(identities) == 1:
        return 'verified', 'own_profile', identities.pop()
    if 'for you' in labels:
        return 'verified', 'feed', ''
    return 'unknown', 'no_identity_evidence', ''


def is_foreground(dump, package):
    return any(package + '/' in line and ('mCurrentFocus' in line or 'mFocusedApp' in line)
               for line in dump.splitlines() if 'mCurrentFocus' in line)  # focused window, not background activity


def monitor_session(device, profile_id, stopped):
    previous = None
    def report(result, reason, account=''):
        nonlocal previous
        observation = (result, reason, account)
        if observation != previous:
            emit('session', profile_id=profile_id, service='TikTok', result=result,
                 reason=reason, account_name=account)
            previous = observation
    while not stopped.is_set():
        try:
            if not is_foreground(device.run('shell', 'dumpsys', 'window'), PACKAGE):
                stopped.wait(3)
                continue
            # Stream the accessibility dump; no persistent UI file or navigation.
            xml = device.run('exec-out', 'uiautomator', 'dump', '/dev/tty')
            start, end = xml.find('<hierarchy'), xml.rfind('</hierarchy>')
            observation = classify_session(xml[start:end + len('</hierarchy>')] if start >= 0 and end >= 0 else '')
            # Never accept a dump captured across an app switch.
            if not is_foreground(device.run('shell', 'dumpsys', 'window'), PACKAGE):
                stopped.wait(3)
                continue
            report(*observation)
        except (OpenError, subprocess.TimeoutExpired, OSError):
            report('unknown', 'ui_unavailable')
        stopped.wait(3)


def binding(profile_id):
    bindings = json.loads(Path(__file__).with_name('bindings.json').read_text(encoding='utf-8'))
    if profile_id not in bindings:
        raise OpenError('unbound')
    return bindings[profile_id]


def proxy_endpoint(value):
    try:
        p = urlsplit(value)
        # Existing imported configuration uses a numeric SOCKS5 endpoint.
        if p.scheme != 'socks5' or not p.hostname or not p.port:
            raise ValueError()
        host = str(ipaddress.ip_address(p.hostname))
        return host, p.port
    except (ValueError, TypeError):
        raise OpenError('vpn_unverified') from None


def route_matches(vpn, lockdown, tunnel, notifications, sockets, bound, endpoint):
    if vpn.strip() != bound['vpn_package'] or lockdown.strip() != '1':
        return False
    if not re.search(r'\btun0:.*<[^>]*\bUP\b', tunnel):
        return False
    # Exact notification title, not an arbitrary occurrence in the dump.
    title = 'android.title=String (' + bound['vpn_profile'] + ')'
    if title not in notifications:
        return False
    # Physical emulator egress must include the assigned SOCKS endpoint.
    # tun0 peers are virtual connections, not direct physical egress.
    target = endpoint[0] + ':' + str(endpoint[1])
    physical = []
    for line in sockets.splitlines():
        fields = line.split()
        if len(fields) >= 5 and fields[0] == 'ESTAB' and fields[3].startswith('10.0.2.'):
            physical.append(fields[4])
    return bool(physical) and all(peer == target for peer in physical)


class Device:
    def __init__(self, sdk, bound, stopped=None):
        self.adb = str(Path(sdk) / 'platform-tools' / 'adb.exe')
        self.bound = bound
        self.stopped = stopped or threading.Event()

    def check_cancelled(self):
        if self.stopped.is_set():
            raise OpenError('cancelled')

    def wait(self, seconds):
        self.stopped.wait(seconds)
        self.check_cancelled()

    def run(self, *args, timeout=20, check=True):
        self.check_cancelled()
        result = subprocess.run([self.adb, '-s', self.bound['serial'], *args],
                                capture_output=True, encoding='utf-8', errors='replace',
                                timeout=timeout, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        if check and result.returncode:
            raise OpenError('internal_error')
        return result.stdout

    def verify_device(self):
        name = self.run('emu', 'avd', 'name').splitlines()
        if not name or name[0].strip() != self.bound['avd']:
            raise OpenError('wrong_device')

    def verify_route(self, endpoint):
        b = self.bound
        if not route_matches(
                self.run('shell', 'settings', 'get', 'secure', 'always_on_vpn_app'),
                self.run('shell', 'settings', 'get', 'secure', 'always_on_vpn_lockdown'),
                self.run('shell', 'ip', 'addr', 'show', 'tun0', check=False),
                self.run('shell', 'dumpsys', 'notification', '--noredact'),
                self.run('shell', 'ss', '-nt'), b, endpoint):
            raise OpenError('vpn_unverified')


def _visible_nodes(root):
    return [n for n in root.iter('node') if n.get('visible-to-user') != 'false']


def _label_nodes(root, label):
    return [n for n in _visible_nodes(root)
            if label in (n.get('text', '').strip(), n.get('content-desc', '').strip())]


def _tap_node(device, node):
    bounds = re.fullmatch(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', node.get('bounds', ''))
    if not bounds:
        raise OpenError('vpn_unverified')
    x1, y1, x2, y2 = map(int, bounds.groups())
    device.run('shell', 'input', 'tap', str((x1 + x2) // 2), str((y1 + y2) // 2))


def _ui_dump(device, path):
    device.run('shell', 'rm', '-f', path)
    result = device.run('shell', 'uiautomator', 'dump', path, timeout=40)
    if 'dumped to:' not in result and path not in result:
        raise OpenError('vpn_unverified')
    return ET.fromstring(device.run('shell', 'cat', path))


def start_saved_vpn(device):
    """Start the bound sing-box profile once; never change proxy configuration."""
    b = device.bound
    pkg = b['vpn_package']
    device.run('shell', 'am', 'start', '-W', '-n', pkg + '/.compose.MainActivity', timeout=30)
    device.wait(2)
    path = '/sdcard/vb-vpn-start.xml'
    root = _ui_dump(device, path)
    if not _label_nodes(root, b['vpn_profile']):
        raise OpenError('vpn_unverified')
    for label in ('OK', 'ALLOW', 'Allow'):
        matches = _label_nodes(root, label)
        if len(matches) == 1:
            _tap_node(device, matches[0])
            device.wait(1)
            root = _ui_dump(device, path)
    if _label_nodes(root, 'Stop'):
        return
    starts = _label_nodes(root, 'Start')
    if len(starts) != 1:
        raise OpenError('vpn_unverified')
    _tap_node(device, starts[0])
    device.wait(2)
    root = _ui_dump(device, path)
    for label in ('OK', 'ALLOW', 'Allow'):
        matches = _label_nodes(root, label)
        if len(matches) == 1:
            _tap_node(device, matches[0])
            device.wait(1)


def open_profile(job, stopped=None):
    if not isinstance(job, dict) or job.get('action') not in ('open', 'warmup') or job.get('package') != PACKAGE:
        raise OpenError('invalid_request')
    if job['action'] == 'warmup' and (type(job.get('duration_seconds')) is not int or job['duration_seconds'] <= 0
            or type(job.get('like_percent')) is not int or not 0 <= job['like_percent'] <= 100):
        raise OpenError('invalid_request')
    b = binding(job.get('profile_id'))
    endpoint = proxy_endpoint(job.get('proxy'))
    sdk = Path(job['sdk'])
    home = Path(job['avd_home'])
    device = Device(sdk, b, stopped)
    emulator = sdk / 'emulator' / 'emulator.exe'
    if not all(p.exists() for p in (Path(device.adb), emulator, home / (b['avd'] + '.ini'))):
        raise OpenError('tools_missing')
    # Inspect all occupants before launching; never take over another AVD's port.
    devices = subprocess.run([device.adb, 'devices'], capture_output=True, text=True, timeout=20,
                             creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0)).stdout
    occupied = any(line.split() and line.split()[0] == b['serial'] for line in devices.splitlines())
    if occupied:
        device.verify_device()
    else:
        emit('status', 'Запускаю сохранённый Android профиля ' + job['profile_id'] + '…')
        env = dict(os.environ, ANDROID_AVD_HOME=str(home), ANDROID_HOME=str(sdk))
        # Detached emulator survives both worker and VideoBatch; no inherited pipes.
        subprocess.Popen([str(emulator), '-avd', b['avd'], '-port', str(b['port']), '-no-snapshot-save'],
                         env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         creationflags=getattr(subprocess, 'DETACHED_PROCESS', 0) | getattr(subprocess, 'CREATE_NEW_PROCESS_GROUP', 0),
                         close_fds=True)
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        if device.run('shell', 'getprop', 'sys.boot_completed', check=False).strip() == '1':
            break
        device.wait(1)
    else:
        raise OpenError('boot_timeout')
    device.verify_device()
    emit('status', 'Проверяю сохранённый VPN, блокировку сети и назначенный прокси…')
    vpn_start_attempted = False
    deadline = time.monotonic() + 60
    while True:
        try:
            device.verify_route(endpoint)
            break
        except OpenError as error:
            if str(error) != 'vpn_unverified':
                raise
            if not vpn_start_attempted:
                vpn_start_attempted = True
                emit('status', 'Подключаю сохранённый VPN ' + b['vpn_profile'] + '…')
                try:
                    start_saved_vpn(device)
                except OpenError:
                    pass
                device.wait(2)
                continue
            if time.monotonic() >= deadline:
                raise
            device.wait(1)
    package = job['package']
    component = device.run('shell', 'cmd', 'package', 'resolve-activity', '--brief', package).splitlines()
    if not component or not component[-1].startswith(package + '/'):
        raise OpenError('app_unverified')
    result = device.run('shell', 'am', 'start', '-W', '-n', component[-1], timeout=30)
    if 'Status: ok' not in result:
        raise OpenError('app_unverified')
    foreground = device.run('shell', 'dumpsys', 'activity', 'activities')
    if not any(package + '/' in line and ('mResumedActivity' in line or 'topResumedActivity' in line)
               for line in foreground.splitlines()):
        raise OpenError('app_unverified')
    device.verify_route(endpoint)
    if job['action'] == 'warmup':
        from feed_warmup import run
        run(device, job['duration_seconds'], job['like_percent'] / 100, device.stopped, emit, classify_session,
            lambda: device.verify_route(endpoint))
    else:
        emit('ready', 'TikTok открыт в привязанном Android; сохранённый VPN продолжает работать.')
        monitor_session(device, job['profile_id'], stopped)


def main():
    from feed_warmup import WarmupError
    try:
        # Keep stdin open for a cooperative stop, without terminating the visible emulator.
        request = bytearray()
        while True:
            char = os.read(sys.stdin.fileno(), 1)
            if not char or char == b'\n':
                break
            request.extend(char)
        job = json.loads(request)
        stopped = threading.Event()
        def controls():
            # Raw reads avoid a blocked daemon holding Python's stdin buffer at shutdown.
            pending = b''
            while True:
                data = os.read(sys.stdin.fileno(), 256)
                if not data:
                    return
                pending += data
                while b'\n' in pending:
                    line, pending = pending.split(b'\n', 1)
                    if line.strip() == b'cancel':
                        stopped.set()
                        return
        threading.Thread(target=controls, daemon=True).start()
        open_profile(job, stopped)
        return 0
    except WarmupError as error:
        emit('error', str(error))
    except OpenError as error:
        emit('error', MESSAGES.get(str(error), MESSAGES['internal_error']))
    except subprocess.TimeoutExpired:
        emit('error', 'Android не завершил команду за отведённое время. Прогрев прерван, успешное завершение не подтверждено.')
    except Exception:
        emit('error', MESSAGES['internal_error'])
    return 1


if __name__ == '__main__':
    sys.exit(main())
