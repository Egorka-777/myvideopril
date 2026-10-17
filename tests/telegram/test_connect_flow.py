"""Synthetic formats, fresh QR protocol and route diagnosis. No real accounts."""
import asyncio
import base64
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sqlite3
import struct
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

from test_worker import w, FakeClient, session_bytes, ROOT
from telethon import errors, functions
from telethon.sessions import StringSession


class Formats(unittest.TestCase):
    def test_pyrogram_readonly_conversion_and_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'account.session'
            with contextlib.closing(sqlite3.connect(path)) as db:
                db.execute('CREATE TABLE sessions(dc_id INTEGER,api_id INTEGER,test_mode INTEGER,auth_key BLOB,user_id INTEGER,is_bot INTEGER)')
                db.execute('INSERT INTO sessions VALUES(?,?,?,?,?,?)',(4,1234,0,session_bytes(),999,0));db.commit()
            path.with_suffix('.json').write_text(json.dumps(dict(api_id=1234,api_hash='a'*32,user_id=999)))
            before={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in Path(folder).iterdir()}
            row=w.from_sqlite(path,{})[0];session=StringSession(row['session'])
            self.assertEqual((row['source_format'],row['expected_user_id'],session.dc_id),('pyrogram',999,4))
            self.assertEqual(session.auth_key.key,session_bytes())
            self.assertEqual(before,{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in Path(folder).iterdir()})
            for meta in (dict(api_id=999,api_hash='a'*32),dict(api_id=1234,api_hash='a'*32,user_id=1000)):
                path.with_suffix('.json').write_text(json.dumps(meta))
                with self.assertRaises(ValueError):w.from_sqlite(path,{})
            path.with_suffix('.json').unlink()
            with self.assertRaisesRegex(ValueError,'Pyrogram'):w.from_sqlite(path,{})
            with contextlib.closing(sqlite3.connect(path)) as db:db.execute('UPDATE sessions SET test_mode=1');db.commit()
            with self.assertRaisesRegex(ValueError,'тестовый'):w.from_sqlite(path,{})


class Login(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        w.CANCEL.clear()
        while not w.LOGIN_TOKENS.empty():w.LOGIN_TOKENS.get_nowait()

    async def test_fresh_qr_requested_after_identity_and_expired_only_retried(self):
        client=FakeClient();events=[];order=[];tokens=['a'*32,'b'*32]
        original=client.get_me
        async def me():order.append('identity');return await original()
        client.get_me=me
        class Client(FakeClient):
            async def __call__(self,request):
                client.calls.append(request)
                if len(client.calls)==1:raise errors.AuthTokenExpiredError(request=request)
                return SimpleNamespace()
        client.__class__=Client
        def emit(e,**d):
            events.append((e,d))
            if e=='web_token_request':
                order.append('qr');w.LOGIN_TOKENS.put_nowait(tokens[d['attempt']-1])
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',emit):
            await w.web_login(dict(accounts=[dict(id='a')],refresh_token=True))
        self.assertEqual(order,['identity','qr','qr'])
        self.assertEqual([r.token for r in client.calls],[base64.urlsafe_b64decode(t) for t in tokens])
        self.assertFalse(client.connected)
        self.assertEqual(len([e for e,d in events if e=='web_accepted']),1)
        self.assertNotIn('a'*32,str(events));self.assertNotIn('b'*32,str(events))

    async def test_lost_accept_response_never_retries_authorization(self):
        class Lost(FakeClient):
            async def __call__(self,request):self.calls.append(request);raise TimeoutError()
        client=Lost();requests=[]
        def emit(e,**d):
            if e=='web_token_request':requests.append(d);w.LOGIN_TOKENS.put_nowait('a'*32)
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',emit):
            with self.assertRaises(TimeoutError):await w.web_login(dict(accounts=[dict(id='a')],refresh_token=True))
        self.assertEqual(len(client.calls),1);self.assertEqual(len(requests),1);self.assertFalse(client.connected)

    async def test_wait_for_qr_is_cancellable_and_bounded(self):
        with patch.object(w,'emit'):
            with self.assertRaisesRegex(ValueError,'QR'):await w.fresh_login_token(dict(id='a'),1,.01)
            task=asyncio.create_task(w.fresh_login_token(dict(id='a'),1))
            await asyncio.sleep(.01);w.CANCEL.set()
            with self.assertRaises(w.StopJob):await asyncio.wait_for(task,.5)

    async def test_stdin_token_not_logged_and_eof_cancels_interactive_login(self):
        token='a'*32
        with patch.object(w.sys,'stdin',io.StringIO('web_login:'+token+'\ninvalid\n')),patch.object(w,'emit') as emit:
            w.watch_input(cancel_on_eof=True)
        self.assertTrue(w.CANCEL.is_set());self.assertEqual(w.LOGIN_TOKENS.get_nowait(),token);emit.assert_not_called()

    async def test_probe_cannot_veto_successful_real_session_login(self):
        events=[];client=FakeClient()
        with patch.object(w,'probe_proxy',side_effect=AssertionError('Probe before login')),patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            result,me=await w.open_account(dict(id='a',proxy='socks5://host:1080'))
        self.assertIs(result,client);self.assertEqual(me.id,999)
        self.assertTrue(any(e=='proxy' and d['status']=='ready' for e,d in events))
        await client.disconnect()

    async def test_web_access_is_not_claimed_as_session_or_account_success(self):
        events=[];client=FakeClient()
        async def fail():raise ConnectionError('private-password')
        client.connect=fail
        async def probe(value,host):return dict(status='ready',text='fixture',latency_ms=1)
        with patch.object(w,'probe_proxy',probe),patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            with self.assertRaises(w.SessionConnectionError):await w.open_account(dict(id='a',proxy='socks5://host:1080'))
        self.assertTrue(any(e=='proxy' and d['status']=='partial' for e,d in events))
        self.assertFalse(any(e=='account' and d.get('status')=='ready' for e,d in events))
        self.assertNotIn('private-password',str(events))

    async def test_proxy_checker_reports_web_and_assigned_dc_separately(self):
        from telethon.sessions import MemorySession
        from telethon.crypto import AuthKey
        memory=MemorySession();memory.set_dc(4,'149.154.167.91',443);memory.auth_key=AuthKey(session_bytes())
        url='socks5://user:private-password@localhost:1080';events=[];calls=[]
        async def probe(value,host,port=443):
            calls.append((host,port));await asyncio.sleep(.02)
            return dict(status='ready' if host=='web.telegram.org' else 'failed',code='timeout',latency_ms=20)
        start=asyncio.get_running_loop().time()
        with patch.object(w,'probe_proxy',probe),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.check_proxies(dict(proxies=[dict(id='p',url=url)],accounts=[dict(proxy=url,session=StringSession.save(memory))]))
        self.assertEqual(set(calls),{('web.telegram.org',443),('149.154.167.91',443)})
        self.assertTrue(any(e=='proxy' and d['status']=='partial' and 'DC4' in d['text'] for e,d in events))
        self.assertNotIn('private-password',str(events))
        self.assertLess(asyncio.get_running_loop().time()-start,.2)

    async def test_opt_in_link_checks_admin_and_never_sends_to_users(self):
        class Links(FakeClient):
            async def __call__(self,request):self.calls.append(request);return SimpleNamespace(link='https://t.me/+fixture_link')
        client=Links();events=[];job=dict(accounts=[dict(id='a')],target='@group')
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.invite_link(job)
        self.assertEqual(len(client.calls),1)
        self.assertIsInstance(client.calls[0],functions.messages.ExportChatInviteRequest)
        self.assertTrue(client.calls[0].request_needed)
        self.assertTrue(any(e=='invite_link' for e,d in events));self.assertFalse(client.connected)
        client.target.creator=False;client.target.admin_rights=None;client.calls.clear()
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit'):
            with self.assertRaises(ValueError):await w.invite_link(job)
        self.assertEqual(client.calls,[])


class EndpointRoutes(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):w.CANCEL.clear()
    def account(self):
        from telethon.crypto import AuthKey
        from telethon.sessions import MemorySession
        memory=MemorySession();memory.set_dc(2,'149.154.167.51',443);memory.auth_key=AuthKey(session_bytes())
        return dict(id='a',session=StringSession.save(memory),api_id=1234,api_hash='a'*32,
                    proxy='socks5://user:private-password@localhost:1080',
                    dc_options=[dict(host='149.154.167.50',port=443),dict(host='127.0.0.1',port=443)])
    async def test_alternate_same_dc_preserves_auth_and_configured_proxy(self):
        a=self.account();first=FakeClient();second=FakeClient();seen=[];events=[]
        async def failed():raise ConnectionError()
        first.connect=failed
        def factory(row):seen.append(row);return first if len(seen)==1 else second
        with patch.object(w,'client_for',factory),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            client,me=await w.open_account(a)
        self.assertEqual(len(seen),2);self.assertFalse(first.connected)
        self.assertTrue(second.connected);self.assertEqual(me.id,999)
        for row in seen:
            s=StringSession(row['session']);self.assertEqual(s.dc_id,2);self.assertEqual(s.auth_key.key,session_bytes())
            self.assertEqual(row['proxy'],a['proxy'])
        self.assertEqual(StringSession(seen[1]['session']).server_address,'149.154.167.50')
        # The connection event contains a vault update; logs must not echo it.
        self.assertNotIn(a['proxy'],str([(e,d) for e,d in events if e!='connection']))
        await client.disconnect()
    async def test_revoked_auth_is_never_retried_on_another_endpoint(self):
        a=self.account();client=FakeClient()
        async def revoked():raise errors.AuthKeyUnregisteredError(request=None)
        client.get_me=revoked
        with patch.object(w,'client_for',return_value=client) as factory,patch.object(w,'emit'):
            with self.assertRaises(errors.AuthKeyUnregisteredError):await w.open_account(a)
        self.assertEqual(factory.call_count,1);self.assertFalse(client.connected)
    async def test_tdata_config_endpoint_instead_of_hardcoded_address(self):
        from opentele.td import MTP
        from opentele.tl import TelegramClient
        from opentele.api import API,UseCurrentSession
        from telethon.crypto import AuthKey
        from telethon.sessions import MemorySession
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder)/'tdata';memory=MemorySession();memory.set_dc(2,'149.154.167.51',443);memory.auth_key=AuthKey(session_bytes())
            client=TelegramClient(memory,api=API.TelegramDesktop);client.UserId=999
            td=await client.ToTDesktop(flag=UseCurrentSession)
            # Use an official Telegram range but a different endpoint from the defaults.
            td.accounts[0].MtpConfig._dcOptions._data[2]=[MTP.DcOptions.Endpoint(2,MTP.DcOptions.Flag.f_static,'149.154.167.50',443,b'')]
            from PyQt5.QtCore import QByteArray
            ip=b'149.154.167.50'
            # Official Desktop layout: version, count, id, flags, PORT, IP size,
            # IP, secret size, CDN key count. opentele's test writer omits port.
            options=struct.pack('>iiiiii',-2,1,2,16,443,len(ip))+ip+struct.pack('>ii',0,0)
            with patch.object(type(td),'kPerformanceMode',False),patch.object(td.accounts[0].MtpConfig._dcOptions,'Serialize',return_value=QByteArray(options)):
                td.SaveTData(str(root))
            before={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in root.rglob('*') if p.is_file()}
            row=(await w.from_tdata(root))[0]
            self.assertEqual(StringSession(row['session']).server_address,'149.154.167.50')
            self.assertEqual(row['dc_options'][0],dict(host='149.154.167.50',port=443))
            self.assertEqual(before,{str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in root.rglob('*') if p.is_file()})


class Runtime(unittest.TestCase):
    def test_actual_pinned_runtime_smoke_is_offline(self):
        spec=importlib.util.spec_from_file_location('runtime_check',ROOT/'tools/telegram/runtime_check.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with patch.dict('sys.modules',worker=w),patch.object(w,'probe_proxy',side_effect=AssertionError('Network')):
            result=module.check()
        self.assertEqual(result['version'],w.VERSION);self.assertEqual(result['status'],'ready')
