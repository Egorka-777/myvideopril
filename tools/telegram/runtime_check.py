"""Offline runtime smoke test. No account files and no network connections."""
import asyncio
import importlib.metadata
import json
from pathlib import Path
import re
import sys

VERSION = '2026-10-19-telegram-profile-v6'


def check():
    if sys.version_info[:2] != (3, 11) or sys.maxsize <= 2**32:
        raise RuntimeError('Python 3.11 x64 is required')
    expected = {}
    for line in Path(__file__).with_name('requirements.txt').read_text().splitlines():
        match = re.fullmatch(r'([A-Za-z0-9_-]+)(?:\[[^]]+\])?==([^\s]+)', line.strip())
        if match:
            expected[match[1]] = match[2]
    for name, version in expected.items():
        if importlib.metadata.version(name) != version:
            raise RuntimeError('Version mismatch: ' + name)
    from opentele.td import TDesktop
    from telethon.sessions import StringSession
    from PyQt5 import QtCore
    import tgcrypto
    from worker import proxy_connector
    from wss_transport import ConnectionWss, dc_host
    if dc_host(4) != 'vesta.web.telegram.org' or ConnectionWss.packet_codec is None:
        raise RuntimeError('WSS transport is unavailable')
    async def proxy_smoke():
        for value in ('socks5://127.0.0.1:1080', 'http://127.0.0.1:8080'):
            if proxy_connector(value) is None:
                raise RuntimeError('Proxy runtime is unavailable')
    asyncio.run(proxy_smoke())
    return dict(version=VERSION, python='.'.join(map(str, sys.version_info[:3])),
                telethon=importlib.metadata.version('Telethon'), status='ready')


if __name__ == '__main__':
    try:
        result = check()
        if len(sys.argv) == 2:
            Path(sys.argv[1], 'runtime.ready').write_text(VERSION, encoding='utf-8')
        print(json.dumps(result), flush=True)
    except Exception as exc:
        print(json.dumps(dict(status='failed', code=type(exc).__name__,
                              text='Компоненты Telegram не готовы; повторите подготовку Python 3.11 x64')), flush=True)
        raise SystemExit(1)
