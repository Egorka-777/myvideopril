"""Synthetic formats, fresh QR protocol and route diagnosis. No real accounts."""
import asyncio
import base64
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sqlite3
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
            with sqlite3.connect(path) as db:
                db.execute('CREATE TABLE sessions(dc_id INTEGER,api_id INTEGER,test_mode INTEGER,auth_key BLOB,user_id INTEGER,is_bot INTEGER)')
                db.execute('INSERT INTO sessions VALUES(?,?,?,?,?,?)',(4,1234,0,session_bytes(),999,0))
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
            with sqlite3.connect(path) as db:db.execute('UPDATE sessions SET test_mode=1')
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

    async def test_web_only_proxy_cannot_mark_account_ready(self):
        events=[];calls=[]
        async def probe(value,host):
            calls.append(host)
            return dict(status='ready' if host=='web.telegram.org' else 'failed',text='fixture',latency_ms=1)
        with patch.object(w,'probe_proxy',probe),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            with self.assertRaises(w.RouteError):await w.account_route(dict(id='a',proxy='fixture'))
        self.assertEqual(calls,['149.154.167.51','web.telegram.org'])
        self.assertTrue(any(e=='proxy' and d['status']=='limited' and d['code']=='mtproto_unreachable' for e,d in events))
        self.assertFalse(any(e=='account' for e,d in events))

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


class Runtime(unittest.TestCase):
    def test_actual_pinned_runtime_smoke_is_offline(self):
        spec=importlib.util.spec_from_file_location('runtime_check',ROOT/'tools/telegram/runtime_check.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        with patch.dict('sys.modules',worker=w),patch.object(w,'probe_proxy',side_effect=AssertionError('Network')):
            result=module.check()
        self.assertEqual(result['version'],w.VERSION);self.assertEqual(result['status'],'ready')
