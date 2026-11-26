"""TikTok saved-session feed warmup. No login, account switching or unlike."""
import random
import re
import time
import xml.etree.ElementTree as ET

PACKAGE = 'com.zhiliaoapp.musically'


class WarmupError(Exception):
    pass


def nodes(root):
    return [n for n in root.iter('node') if n.get('visible-to-user') != 'false']


def labels(node):
    return (node.get('text', '').strip(), node.get('content-desc', '').strip())


def screen(device, notification_handled=False):
    # Android's notification permission is not a login challenge. Deny only this
    # exact TikTok permission dialog; never grant permissions or dismiss CAPTCHA.
    focus = device.run('shell', 'dumpsys', 'window')
    permission = 'com.google.android.permissioncontroller/' in focus or 'com.android.permissioncontroller/' in focus
    def foreground():
        focus = device.run('shell', 'dumpsys', 'window')
        return any('mCurrentFocus' in line and PACKAGE + '/' in line for line in focus.splitlines())
    if not permission and not any('mCurrentFocus' in line and PACKAGE + '/' in line for line in focus.splitlines()):
        raise WarmupError('TikTok не в фокусе; требуется ручная проверка.')
    path = '/sdcard/vb-feed-warmup.xml'
    device.run('shell', 'rm', '-f', path)
    result = device.run('shell', 'uiautomator', 'dump', path)
    if 'dumped to: ' + path not in result:
        raise WarmupError('Свежий снимок Android не получен; прогрев остановлен.')
    try:
        root = ET.fromstring(device.run('shell', 'cat', path))
    except ET.ParseError:
        raise WarmupError('Снимок Android повреждён; прогрев остановлен.') from None
    if permission:
        title = any(s == 'Allow TikTok to send you notifications?' for n in nodes(root) for s in labels(n))
        deny = [n for n in nodes(root) if n.get('resource-id','').endswith(':id/permission_deny_button')]
        if notification_handled or not title or len(deny)!=1:
            raise WarmupError('Неизвестный запрос разрешения Android; прогрев остановлен.')
        tap(device,deny[0])
        device.wait(1)
        return screen(device, notification_handled=True)
    if not foreground():
        raise WarmupError('TikTok потерял фокус; прогрев остановлен.')
    visible = nodes(root)
    if not visible or any(n.get('package') not in ('', None, PACKAGE) for n in visible):
        raise WarmupError('Постороннее окно Android; требуется ручная проверка.')
    text = [s.casefold() for n in visible for s in labels(n) if s]
    markers = ('captcha', 'verify', 'verification', 'log in', 'sign up', 'sign in',
               "isn't responding", 'not responding', 'something went wrong', 'network error',
               'no internet', 'try again', 'войти', 'вход в', 'зарегистр', 'проверка',
               'подтвердите', 'не отвечает', 'ошибка', 'нет подключения')
    if any(any(marker in s for marker in markers) for s in text) or any(
            n.get('password') == 'true' or n.get('class') == 'android.widget.ProgressBar' for n in visible):
        raise WarmupError('Вход, ручная проверка или недоступный экран; прогрев остановлен.')
    return root


def exact(root, label):
    return [n for n in nodes(root) if label in labels(n)]


def tap(device, node):
    bounds = re.fullmatch(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', node.get('bounds', ''))
    if not bounds or node.get('enabled') == 'false':
        raise WarmupError('Не подтверждена кнопка Android; прогрев остановлен.')
    x1, y1, x2, y2 = map(int, bounds.groups())
    if x2 <= x1 or y2 <= y1:
        raise WarmupError('Не подтверждены координаты кнопки Android.')
    device.run('shell', 'input', 'tap', str((x1 + x2) // 2), str((y1 + y2) // 2))


def navigate(device, root, label, stopped):
    matches = exact(root, label)
    # Installed TikTok exposes both the clickable tab container and its text child.
    clickable = [n for n in matches if n.get('clickable') == 'true']
    if clickable:
        matches = clickable
    if len(matches) != 1:
        raise WarmupError('Не найдена известная вкладка ' + label + '; требуется ручная проверка.')
    tap(device, matches[0])
    if stopped.wait(1):
        device.check_cancelled()
    return screen(device)


def liked(root):
    return any(s == 'Video liked' or s.startswith(('Unlike video.', 'Unlike.'))
               for n in nodes(root) for s in labels(n))


def like_button(root):
    matches = [n for n in nodes(root) if n.get('class') == 'android.widget.Button'
               and n.get('content-desc', '').startswith('Like video.')]
    if len(matches) > 1:
        raise WarmupError('Неоднозначная кнопка лайка; прогрев остановлен.')
    return matches[0] if matches else None


def feed(root):
    if not exact(root, 'For You') or not exact(root, 'Home'):
        raise WarmupError('Ожидаемая лента For You не подтверждена; прогрев остановлен.')
    live = bool(exact(root, 'Tap to watch LIVE'))
    if not live and like_button(root) is None and not liked(root):
        raise WarmupError('Видеокарточка не подтверждена; прогрев остановлен.')
    return live


def fingerprint(root):
    # Exclude the mutable like state/count; retain creator/caption evidence locally only.
    return tuple((n.get('resource-id', ''), labels(n)) for n in nodes(root)
                 if n.get('class') != 'android.widget.Button'
                 and any(labels(n)) and not any(s == 'Video liked' or s.startswith(('Like video.', 'Unlike')) for s in labels(n)))


def authenticated(root, classify):
    if classify(ET.tostring(root, encoding='unicode'))[0] == 'verified':
        return True
    # Same installed own-profile username node used by the existing Android profile editor.
    identities = [n for n in nodes(root) if n.get('resource-id') == PACKAGE + ':id/mje'
                  and re.fullmatch(r'@?[A-Za-z0-9_.]{1,30}', n.get('text', '').strip())]
    return len(identities) == 1 and bool(exact(root, 'Edit profile'))


def run(device, duration, probability, stopped, emit, classify, verify_route):
    stats = dict(video_slots=0, confirmed_likes=0, live_skipped=0, swipes=0)
    started = None
    outcome = 'failed'
    try:
        root = screen(device)
        if not authenticated(root, classify):
            root = navigate(device, root, 'Profile', stopped)
        if not authenticated(root, classify):
            raise WarmupError('Сохранённая авторизованная сессия не подтверждена. Войдите вручную.')
        emit('status', 'Авторизованная сессия Android подтверждена; открываю ленту Home.')
        root = navigate(device, root, 'Home', stopped)
        feed(root)
        started = time.monotonic()
        deadline = started + duration
        while time.monotonic() < deadline:
            device.check_cancelled()
            verify_route()
            live = feed(root)
            if stopped.wait(min(1 if live else random.uniform(8, 28), max(0, deadline - time.monotonic()))):
                device.check_cancelled()
            current = screen(device)
            current_live = feed(current)
            if live:
                stats['live_skipped'] += 1
            else:
                stats['video_slots'] += 1
                if time.monotonic() < deadline and not current_live and not liked(current) and random.random() < probability:
                    button = like_button(current)
                    if button is not None:
                        tap(device, button)
                        current = screen(device)
                        feed(current)
                        if not liked(current):
                            raise WarmupError('Лайк не подтверждён; повторное нажатие не выполняется.')
                        stats['confirmed_likes'] += 1
            emit('stats', 'Прогрев Android: просмотрено карточек {video_slots}, лайков {confirmed_likes}, LIVE пропущено {live_skipped}.'.format(**stats),
                 elapsed_seconds=round(time.monotonic() - started), **stats)
            if time.monotonic() >= deadline:
                break
            before = fingerprint(current)
            # Android hierarchy itself has no bounds; derive the screen from visible app nodes.
            coordinates = [tuple(map(int, re.findall(r'\d+', n.get('bounds', '')))) for n in nodes(current)]
            coordinates = [b for b in coordinates if len(b) == 4]
            if not coordinates:
                raise WarmupError('Размер окна Android не подтверждён.')
            width = max(b[2] for b in coordinates)
            height = max(b[3] for b in coordinates)
            device.run('shell', 'input', 'swipe', str(width // 2), str(int(height * .72)),
                       str(width // 2), str(int(height * .25)), str(random.randint(330, 550)))
            stats['swipes'] += 1
            if stopped.wait(min(1, max(0, deadline - time.monotonic()))):
                device.check_cancelled()
            root = screen(device)
            feed(root)
            # A swipe starts an asynchronous transition; observe readiness, don't swipe again.
            transition_deadline = min(deadline, time.monotonic() + 10)
            while fingerprint(root) == before and time.monotonic() < transition_deadline:
                if stopped.wait(.5):
                    device.check_cancelled()
                root = screen(device)
                feed(root)
            if fingerprint(root) == before:
                raise WarmupError('Лента не изменилась после прокрутки за 10 секунд; возможное зависание. Прогрев остановлен.')
        device.check_cancelled()
        verify_route()
        outcome = 'completed'
    finally:
        emit('stats', 'Итог Android: ' + outcome + '; карточек {video_slots}, подтверждённых лайков {confirmed_likes}, LIVE {live_skipped}.'.format(**stats),
             result=outcome, elapsed_seconds=round(time.monotonic() - started) if started is not None else 0, **stats)
    emit('completed', 'Прогрев TikTok в Android завершён. Учтено время на видимых карточках, не измеренное воспроизведение.', **stats)
