"""Regression tests for bounded diagnostics, auth failures and web session state."""
import asyncio
from unittest.mock import patch
import unittest
from test_worker import w, FakeClient

class Timeouts(unittest.IsolatedAsyncioTestCase):
    async def test_stalled_proxy_has_one_total_deadline(self):
        class Stalled:
            async def connect(self, **kw): await asyncio.sleep(10)
        start=asyncio.get_running_loop().time()
        with patch.object(w,'proxy_connector',return_value=Stalled()),patch.object(w,'PROXY_TIMEOUT',.04):
            result=await w.probe_proxy('fixture')
        self.assertEqual(result['code'],'timeout')
        self.assertLess(asyncio.get_running_loop().time()-start,.5)

    async def test_malformed_proxy_is_result_not_worker_crash(self):
        result=await w.probe_proxy('socks5://secret:password@localhost:bad')
        self.assertEqual(result['code'],'configuration')
        self.assertNotIn('secret',str(result));self.assertNotIn('password',str(result))

    async def test_total_account_deadline_includes_get_me(self):
        client=FakeClient()
        async def connect():await asyncio.sleep(.03)
        async def authorized():await asyncio.sleep(.03);return True
        client.connect=connect;client.is_user_authorized=authorized
        with patch.object(w,'ACCOUNT_TIMEOUT',.04):
            with self.assertRaises(TimeoutError):await w.connect(client)

    async def test_wrong_socks_credentials_fail_fast_and_hide_secret(self):
        async def denied(reader,writer):
            try:
                version,n=await reader.readexactly(2);await reader.readexactly(n)
                writer.write(b'\x05\x02');await writer.drain()
                v,n=await reader.readexactly(2);await reader.readexactly(n)
                n=(await reader.readexactly(1))[0];await reader.readexactly(n)
                writer.write(b'\x01\x01');await writer.drain()
            finally:writer.close();await writer.wait_closed()
        server=await asyncio.start_server(denied,'127.0.0.1',0)
        try:
            port=server.sockets[0].getsockname()[1]
            result=await w.probe_proxy(f'socks5://u:private-password@127.0.0.1:{port}')
            self.assertEqual(result['status'],'failed');self.assertEqual(result['code'],'handshake')
            self.assertNotIn('private-password',str(result))
        finally:server.close();await server.wait_closed()

    async def test_web_login_updates_real_account_only_after_get_me(self):
        events=[];client=FakeClient()
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.web_login(dict(accounts=[dict(id='a')],login_token='a'*32))
        self.assertTrue(any(e=='account' and d['status']=='ready' for e,d in events))
        client.get_me=lambda:asyncio.sleep(0,result=None);events.clear()
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            with self.assertRaises(ValueError):await w.web_login(dict(accounts=[dict(id='a')],login_token='a'*32))
        self.assertFalse(any(e=='account' and d['status']=='ready' for e,d in events))
