"""Real localhost SOCKS5 -> TLS -> binary WS -> obfuscated MTProto framing.

The checked-in self-signed certificate/key belong only to this local fixture.
No Telegram credentials and no connections to the public network.
"""
import asyncio
import collections
import contextlib
import logging
from pathlib import Path
import ssl
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tools' / 'telegram'))
import wss_transport as t
import worker as w
from telethon.crypto import AESModeCTR
from websockets.asyncio.server import serve


class WssIntegration(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tasks = set()
        self.targets = []
        self.upgrades = []
        self.sockets = []
        self.fail_auth = False
        self.hang = False
        self.eof = asyncio.Event()
        self.original_context = ssl.create_default_context
        fixture = Path(__file__).with_name('fixtures')
        self.cert = fixture / 'wss-test-cert.pem'
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(self.cert, fixture / 'wss-test-key.pem')
        self.web = await serve(self.websocket, '127.0.0.1', 0, ssl=context, subprotocols=['binary'])
        self.web_port = self.web.sockets[0].getsockname()[1]
        self.socks = await asyncio.start_server(self.accept, '127.0.0.1', 0)
        self.port = self.socks.sockets[0].getsockname()[1]
        self.proxy = w.parse_proxy(f'socks5://fixture:local-test-password@127.0.0.1:{self.port}')

    def context(self):
        return self.original_context(cafile=str(self.cert))

    async def asyncTearDown(self):
        self.socks.close()
        await self.socks.wait_closed()
        self.web.close()
        await self.web.wait_closed()
        for sock in self.sockets:
            sock.close()
        for task in self.tasks:
            task.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)

    def accept(self, reader, writer):
        task = asyncio.create_task(self.socks_client(reader, writer))
        self.tasks.add(task)
        task.add_done_callback(self.tasks.discard)

    async def socks_client(self, reader, writer):
        self.sockets.append(writer)
        remote = None
        try:
            version, size = await reader.readexactly(2)
            self.assertEqual(version, 5)
            self.assertIn(2, await reader.readexactly(size))
            writer.write(b'\x05\x02'); await writer.drain()
            version, size = await reader.readexactly(2)
            user = await reader.readexactly(size)
            size = (await reader.readexactly(1))[0]
            password = await reader.readexactly(size)
            self.assertEqual((version,user,password), (1,b'fixture',b'local-test-password'))
            writer.write(b'\x01\x01' if self.fail_auth else b'\x01\x00'); await writer.drain()
            if self.fail_auth:
                return
            version, command, reserved, kind = await reader.readexactly(4)
            self.assertEqual((version,command,reserved,kind), (5,1,0,3))
            size = (await reader.readexactly(1))[0]
            host = (await reader.readexactly(size)).decode()
            port = int.from_bytes(await reader.readexactly(2),'big')
            self.targets.append((host,port))
            self.assertEqual((host,port), ('vesta.web.telegram.org',443))
            if self.hang:
                await reader.read(); self.eof.set(); return
            incoming, remote = await asyncio.open_connection('127.0.0.1', self.web_port)
            writer.write(b'\x05\x00\x00\x01\x7f\x00\x00\x01\x01\xbb'); await writer.drain()
            async def pipe(source, target):
                while data := await source.read(65536):
                    target.write(data); await target.drain()
            tasks = [asyncio.create_task(pipe(reader,remote)),asyncio.create_task(pipe(incoming,writer))]
            try:
                await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
            finally:
                for task in tasks:task.cancel()
                await asyncio.gather(*tasks,return_exceptions=True)
        except (asyncio.IncompleteReadError,ConnectionError):
            pass
        finally:
            writer.close()
            if remote:remote.close()

    async def websocket(self, ws):
        self.upgrades.append((ws.request.path, ws.subprotocol))
        try:
            header = await ws.recv()
            self.assertEqual(len(header), 64)
            decoder = AESModeCTR(header[8:40], header[40:56])
            clear = decoder.encrypt(header)
            self.assertEqual(clear[56:60], b'\xef'*4)
            reverse = header[55:7:-1]
            encoder = AESModeCTR(reverse[:32],reverse[32:])
            packet = decoder.encrypt(await ws.recv())
            self.assertEqual(packet,b'\x02abcdefgh')
            response = encoder.encrypt(b'\x02reply123\x02reply456')
            await ws.send(response[:2])  # Split MTProto frame across WS messages.
            await ws.send(response[2:])  # Two MTProto frames in one WS message.
            await ws.wait_closed()
        except Exception as exc:
            # Upgrade-only and rejected TLS tests intentionally don't send data.
            if type(exc).__name__ not in ('ConnectionClosedOK','ConnectionClosedError'):
                raise

    async def test_tls_binary_upgrade_and_telethon_obfuscation_roundtrip(self):
        loggers = collections.defaultdict(lambda:logging.getLogger('fixture'))
        conn = t.ConnectionWss('149.154.167.91',443,4,loggers=loggers,proxy=self.proxy)
        with patch.object(t.ssl,'create_default_context',self.context):
            await conn.connect(timeout=2)
            try:
                await conn.send(b'abcdefgh')
                self.assertEqual(await asyncio.wait_for(conn.recv(),2),b'reply123')
                self.assertEqual(await asyncio.wait_for(conn.recv(),2),b'reply456')
            finally:
                await conn.disconnect()
        self.assertEqual(self.targets,[('vesta.web.telegram.org',443)])
        self.assertEqual(self.upgrades,[('/apiws','binary')])
        self.assertFalse(conn._connected)

    async def test_invalid_tls_certificate_is_rejected(self):
        with self.assertRaises(t.TransportError) as error:
            await t.open_socket(self.proxy,4,timeout=2)
        self.assertEqual(error.exception.diagnostic_code,'tls_certificate')
        self.assertEqual(len(self.targets),1)
        self.assertEqual(self.upgrades,[])

    async def test_wrong_proxy_auth_does_not_connect_directly(self):
        self.fail_auth = True
        with self.assertRaises(t.TransportError) as error:
            await t.open_socket(self.proxy,4,timeout=2)
        self.assertEqual(error.exception.diagnostic_code,'proxy_tunnel')
        self.assertEqual(self.targets,[])
        self.assertEqual(self.upgrades,[])
        self.assertNotIn('password',str(error.exception))

    async def test_cancelled_proxy_handshake_closes_socket(self):
        self.hang = True
        task = asyncio.create_task(t.open_socket(self.proxy,4,timeout=2))
        while not self.targets:await asyncio.sleep(.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):await task
        await asyncio.wait_for(self.eof.wait(),1)

    async def test_no_proxy_and_unknown_dc_fail_before_connect(self):
        with self.assertRaises(ValueError):await t.open_socket(None,4)
        with self.assertRaises(ValueError):await t.open_socket(self.proxy,9)
        self.assertEqual(self.targets,[])


if __name__ == '__main__':unittest.main()
