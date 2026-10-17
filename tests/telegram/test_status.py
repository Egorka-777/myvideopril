"""Status, limits and transport tests. Synthetic sessions, loopback only."""
import asyncio
import base64
import importlib.util
import io
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch
import unittest
from test_worker import w, FakeClient, session_bytes
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'tools/telegram'))
from web_proxy import Bridge,allowed_host


class Status(unittest.IsolatedAsyncioTestCase):
    async def test_failed_real_connection_never_marks_account_blocked(self):
        events=[];client=FakeClient()
        async def fail():raise ConnectionError('synthetic')
        client.connect=fail
        async def failed(*a,**kw):return dict(status='failed',text='fixture',latency_ms=0)
        with patch.object(w,'probe_proxy',failed),patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.check_accounts(dict(action='check',accounts=[dict(id='a',proxy='socks5://host:1080')]))
        account=[d for e,d in events if e=='account'][0]
        self.assertEqual(account['status'],'network');self.assertNotEqual(account['status'],'blocked')
        self.assertEqual(events[-1][1]['failures'],1)
    async def test_success_requires_get_me_not_merely_proxy_connection(self):
        client=FakeClient();client.get_me=lambda:asyncio.sleep(0,result=None)
        events=[]
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.check_accounts(dict(action='check',accounts=[dict(id='a')]))
        self.assertFalse(any(e=='account' and d['status']=='ready' for e,d in events))
        self.assertEqual(events[-1][1]['ready'],0)
    async def test_independent_proxy_probe_uses_authenticated_connect_to_telegram(self):
        seen=[]
        async def socks(reader,writer):
            try:
                version,n=await reader.readexactly(2);methods=await reader.readexactly(n)
                writer.write(b'\x05\x02');await writer.drain()
                authver,n=await reader.readexactly(2);username=await reader.readexactly(n);n=(await reader.readexactly(1))[0];password=await reader.readexactly(n)
                seen.append((username,password));writer.write(b'\x01\x00');await writer.drain()
                head=await reader.readexactly(4)
                host=await reader.readexactly(4 if head[3]==1 else (await reader.readexactly(1))[0])
                port=int.from_bytes(await reader.readexactly(2),'big');seen.append((host,port))
                writer.write(b'\x05\x00\x00\x01'+b'\x00'*6);await writer.drain();await reader.read()
            finally:writer.close();await writer.wait_closed()
        server=await asyncio.start_server(socks,'127.0.0.1',0)
        try:
            port=server.sockets[0].getsockname()[1]
            result=await w.probe_proxy(f'socks5://user:p%40ss@127.0.0.1:{port}')
            self.assertEqual(result['status'],'ready');self.assertEqual(seen[0],(b'user',b'p@ss'));self.assertEqual(seen[1][1],443)
        finally:server.close();await server.wait_closed()
    async def test_proxy_refusal_is_not_ready_and_does_not_echo_auth(self):
        from python_socks import ProxyError
        class Refused:
            async def connect(self,**kw):raise ProxyError('user:secret refused')
        with patch.object(w,'proxy_connector',return_value=Refused()):result=await w.probe_proxy('fixture')
        self.assertEqual(result['status'],'failed');self.assertNotIn('secret',result['text'])
    async def test_preview_has_no_invitation_rpc(self):
        client=FakeClient();events=[]
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.invite_preview(dict(accounts=[dict(id='a')],target='@group'))
        self.assertEqual(client.calls,[]);self.assertTrue(any(e=='target' for e,d in events))


class Limits(unittest.IsolatedAsyncioTestCase):
    async def test_local_limit_and_id_unknown_never_fabricates_hash(self):
        import tempfile
        from telethon import functions,types
        class Client(FakeClient):
            async def get_entity(self,value):
                if isinstance(value,types.PeerUser):raise ValueError('not known')
                return await super().get_entity(value)
            async def __call__(self,request):
                if isinstance(request,functions.contacts.GetContactsRequest):return SimpleNamespace(users=[])
                return await super().__call__(request)
        events=[];client=Client()
        async def pause(s):pass
        with tempfile.TemporaryDirectory() as root:
            job=dict(root=root,accounts=[dict(id='a')],target='@group',users='123456\nuserone\nusertwo',delay=10,limit=2)
            with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))),patch.object(w,'pause',pause):await w.invite(job)
        sent=[r for r in client.calls if isinstance(r,functions.channels.InviteToChannelRequest)]
        self.assertEqual(len(sent),1);self.assertEqual(sent[0].users[0].id,101)
        summary=events[-1][1];self.assertEqual(summary['confirmed'],1);self.assertEqual(summary['unresolved'],1)
    async def test_private_invite_link_never_auto_joins(self):
        from telethon import functions,types
        client=FakeClient()
        async def invoke(request):
            self.assertIsInstance(request,functions.messages.CheckChatInviteRequest)
            return types.ChatInvite(title='Not joined',photo=types.PhotoEmpty(1),participants_count=1,color=0)
        client.__class__=type('Private',(FakeClient,),{'__call__':lambda self,r:invoke(r)})
        with self.assertRaisesRegex(ValueError,'Вступите вручную'):await w.resolve_target(client,'https://t.me/+test_link')
    async def test_web_login_only_accepts_explicit_token_and_expected_identity(self):
        from telethon import functions
        client=FakeClient();client.me.id=123
        calls=[]
        async def invoke(self,request):calls.append(request);return SimpleNamespace()
        client.__class__=type('WebLogin',(FakeClient,),{'__call__':invoke})
        job=dict(accounts=[dict(id='a',expected_user_id=999)],login_token='a'*32)
        with patch.object(w,'client_for',return_value=client):
            with self.assertRaises(ValueError):await w.web_login(job)
        self.assertEqual(calls,[])
        job['accounts'][0]['expected_user_id']=123
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit'):await w.web_login(job)
        self.assertIsInstance(calls[0],functions.auth.AcceptLoginTokenRequest)


class WebTransport(unittest.IsolatedAsyncioTestCase):
    async def test_authenticated_bridge_pipes_only_through_upstream(self):
        # Local fake upstream uses a connected socket pair; no external traffic.
        import socket
        left,right=socket.socketpair();left.setblocking(False);right.setblocking(False)
        class Connector:
            def __init__(self):self.calls=[]
            async def connect(self,**kw):self.calls.append(kw);return left
        connector=Connector()
        with patch('web_proxy.proxy_connector',return_value=connector):bridge=Bridge('fake','s'*32)
        server=await asyncio.start_server(bridge.handle,'127.0.0.1',0)
        try:
            port=server.sockets[0].getsockname()[1]
            r,wr=await asyncio.open_connection('127.0.0.1',port)
            auth=base64.b64encode(('local:'+'s'*32).encode()).decode()
            wr.write(f'CONNECT web.telegram.org:443 HTTP/1.1\r\nProxy-Authorization: Basic {auth}\r\n\r\n'.encode());await wr.drain()
            self.assertIn(b'200',await r.readuntil(b'\r\n\r\n'))
            wr.write(b'probe');await wr.drain();self.assertEqual(await asyncio.get_running_loop().sock_recv(right,5),b'probe')
            await asyncio.get_running_loop().sock_sendall(right,b'reply');self.assertEqual(await r.readexactly(5),b'reply')
            self.assertEqual(connector.calls[0]['dest_host'],'web.telegram.org')
            wr.close();await wr.wait_closed()
        finally:right.close();server.close();await server.wait_closed();await bridge.close()
    async def test_no_auth_or_external_host_never_opens_upstream(self):
        from unittest.mock import AsyncMock
        connector=SimpleNamespace(connect=AsyncMock())
        with patch('web_proxy.proxy_connector',return_value=connector):bridge=Bridge('fake','s'*32)
        server=await asyncio.start_server(bridge.handle,'127.0.0.1',0)
        try:
            port=server.sockets[0].getsockname()[1]
            for host,auth,code in [('web.telegram.org','',b'407'),('evil.test',base64.b64encode(('local:'+'s'*32).encode()).decode(),b'403')]:
                r,wr=await asyncio.open_connection('127.0.0.1',port);wr.write(f'CONNECT {host}:443 HTTP/1.1\r\nProxy-Authorization: Basic {auth}\r\n\r\n'.encode());await wr.drain();self.assertIn(code,await r.read());wr.close();await wr.wait_closed()
            connector.connect.assert_not_called()
        finally:server.close();await server.wait_closed();await bridge.close()
