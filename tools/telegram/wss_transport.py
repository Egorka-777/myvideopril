"""Telegram's documented MTProto-over-WSS, always through the assigned proxy.

Framing and obfuscation belong to Telethon; this module only adapts the binary
WebSocket byte stream. No account keys, tokens or proxy URLs in exceptions.
https://core.telegram.org/mtproto/transports#websocket
"""
import asyncio
import contextlib
import ssl
import time

from telethon.network.connection.tcpobfuscated import ConnectionTcpObfuscated
from websockets.asyncio.client import connect
from websockets.exceptions import ConnectionClosed, InvalidHandshake

DC_NAMES = {1: 'pluto', 2: 'venus', 3: 'aurora', 4: 'vesta', 5: 'flora'}


class TransportError(ConnectionError):
    def __init__(self, code):
        super().__init__('Telegram WSS: ' + code)
        self.diagnostic_code = code


def dc_host(dc_id):
    if dc_id not in DC_NAMES:
        raise ValueError('Неизвестный DC Telegram; проверьте импорт сессии')
    return DC_NAMES[dc_id] + '.web.telegram.org'


async def open_socket(proxy, dc_id, timeout=7):
    """Explicit SOCKS/CONNECT socket + verified TLS + binary WS upgrade.

    Passing sock and proxy=None prevents websockets from choosing an ambient
    system proxy or making a direct connection. DNS is delegated to the proxy.
    """
    if not proxy:
        raise ValueError('WSS требует назначенный прокси')
    host = dc_host(dc_id)
    sock = None
    try:
        from python_socks.async_.asyncio import Proxy
        parsed = (ConnectionTcpObfuscated._parse_proxy(**proxy) if isinstance(proxy, dict)
                  else ConnectionTcpObfuscated._parse_proxy(*proxy))
        connector = Proxy.create(*parsed)
        try:
            sock = await asyncio.wait_for(connector.connect(dest_host=host, dest_port=443, timeout=timeout), timeout)
        except asyncio.CancelledError:
            raise
        except Exception:
            raise TransportError('proxy_tunnel') from None
        try:
            ws = await connect('wss://' + host + '/apiws', sock=sock, proxy=None,
                               ssl=ssl.create_default_context(), server_hostname=host,
                               origin='https://web.telegram.org', subprotocols=['binary'],
                               compression=None, open_timeout=timeout, close_timeout=1,
                               max_size=16 * 1024 * 1024, max_queue=4)
        except asyncio.CancelledError:
            raise
        except ssl.SSLError:
            raise TransportError('tls_certificate') from None
        except InvalidHandshake:
            raise TransportError('websocket_upgrade') from None
        except Exception:
            raise TransportError('tls_or_websocket') from None
        if ws.subprotocol != 'binary':
            with contextlib.suppress(Exception):
                await ws.close()
            raise TransportError('binary_protocol')
        sock = None  # Ownership transferred to websockets.
        return ws
    finally:
        if sock is not None:
            sock.close()


class WebSocketStream:
    def __init__(self, ws):
        self.ws = ws
        self.incoming = bytearray()
        self.outgoing = bytearray()
        self.closing = None

    async def readexactly(self, size):
        if size < 0 or size > 16 * 1024 * 1024:
            raise TransportError('packet_size')
        try:
            while len(self.incoming) < size:
                chunk = await self.ws.recv()
                if not isinstance(chunk, bytes):
                    raise TransportError('non_binary_packet')
                self.incoming.extend(chunk)
            result = bytes(self.incoming[:size])
            del self.incoming[:size]
            return result
        except ConnectionClosed:
            raise ConnectionError('Telegram WSS closed') from None

    def write(self, data):
        self.outgoing.extend(data)

    async def drain(self):
        if self.outgoing:
            data = bytes(self.outgoing)
            self.outgoing.clear()
            try:
                await self.ws.send(data)
            except ConnectionClosed:
                raise ConnectionError('Telegram WSS closed') from None

    def close(self):
        if self.closing is None:
            self.closing = asyncio.create_task(self.ws.close())

    async def wait_closed(self):
        self.close()
        await self.closing
        await self.ws.wait_closed()


class ConnectionWss(ConnectionTcpObfuscated):
    async def _connect(self, timeout=None, ssl=None):
        # Abridged packet codec and obfuscation are the pinned Telethon code.
        # WebSocket boundaries are deliberately invisible to that codec.
        ws = await open_socket(self._proxy, self._dc_id, timeout=min(timeout or 7, 7))
        stream = WebSocketStream(ws)
        self._reader = self._writer = stream
        try:
            self._codec = self.packet_codec(self)
            self._init_conn()
            await stream.drain()
        except BaseException:
            stream.close()
            with contextlib.suppress(Exception):
                await asyncio.wait_for(stream.wait_closed(), 2)
            raise


async def probe_wss(proxy, dc_id):
    """Network-only test. Never claims that an account is authorized."""
    ws = None
    start = time.monotonic()
    try:
        ws = await asyncio.wait_for(open_socket(proxy, dc_id), 8)
        return dict(status='ready', code='wss_ready', text='TLS и WebSocket Telegram доступны', latency_ms=int((time.monotonic()-start)*1000))
    except asyncio.CancelledError:
        raise
    except Exception as exc:
        code = getattr(exc, 'diagnostic_code', 'wss_timeout' if isinstance(exc, TimeoutError) else 'wss_network')
        return dict(status='failed', code=code, text='WSS Telegram не подтверждён', latency_ms=int((time.monotonic()-start)*1000))
    finally:
        if ws is not None:
            with contextlib.suppress(Exception):
                await ws.close()
