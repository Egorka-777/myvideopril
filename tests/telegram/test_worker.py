"""Contract tests with synthetic auth keys and local fake RPCs; no Telegram login."""
import asyncio
import contextlib
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
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('worker', ROOT/'tools/telegram/worker.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)
from telethon import errors, functions, types
from telethon.sessions import MemorySession, StringSession
from telethon.crypto import AuthKey


def session_bytes(n=1):
    return bytes((i+n)%256 for i in range(256))


def make_session(path, n=1):
    db=sqlite3.connect(path)
    db.execute('CREATE TABLE sessions(dc_id INTEGER, server_address TEXT, port INTEGER, auth_key BLOB)')
    db.execute('INSERT INTO sessions VALUES(2,?,?,?)',('149.154.167.51',443,session_bytes(n)))
    db.commit();db.close()
    path.with_suffix('.json').write_text(json.dumps({'api_id':1234,'api_hash':'a'*32,'device':'Test PC','first_name':'Тест'}),encoding='utf-8')


class FakeClient:
    def __init__(self, failure=None, missing=False):
        self.failure=failure;self.missing=missing;self.calls=[];self.connected=False
        self.target=types.Channel(id=10,title='Owned test group',photo=types.ChatPhotoEmpty(),date=None,creator=True)
        self.me=types.User(id=999,first_name='Owner')
    async def connect(self): self.connected=True
    async def disconnect(self): self.connected=False
    async def is_user_authorized(self): return True
    async def get_me(self): return self.me
    async def get_entity(self,value):
        if str(value) in ('@group','https://t.me/group', '10'):return self.target
        return types.User(id={'userone':101,'usertwo':102,'userthree':103}.get(value,101),first_name=str(value))
    async def get_dialogs(self,limit): self.calls.append(('dialogs',limit));return [1,2]
    async def __call__(self,request):
        self.calls.append(request)
        if isinstance(request,functions.channels.InviteToChannelRequest):
            if self.failure:raise self.failure
            return SimpleNamespace(missing_invitees=[object()] if self.missing else [])
        return SimpleNamespace(participant=True)


class Imports(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);(self.root/'temp').mkdir();w.CANCEL.clear()
    async def asyncTearDown(self):self.tmp.cleanup()
    async def test_session_preserves_source_and_deduplicates_archive(self):
        path=self.root/'account.session';make_session(path)
        before={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in self.root.iterdir() if p.is_file()}
        result=w.from_sqlite(path,{})[0]
        self.assertEqual(StringSession(result['session']).auth_key.key,session_bytes())
        self.assertEqual(result['device'],'Test PC');self.assertEqual(result['api_id'],1234)
        after={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in self.root.iterdir() if p.is_file()}
        self.assertEqual(before,after)
        archive=self.root/'accounts.zip'
        with zipfile.ZipFile(archive,'w') as z:
            z.write(path,'one/account.session');z.write(path.with_suffix('.json'),'one/account.json')
        events=[]
        with patch.object(w,'emit',lambda event,**kw:events.append((event,kw))):
            await w.import_paths(dict(paths=[str(path),str(archive)],temp=str(self.root/'temp')))
        self.assertEqual(len([e for e in events if e[0]=='imported']),1)
        self.assertEqual(list((self.root/'temp').iterdir()),[])
    async def test_zip_traversal_and_nested_zip_rejected(self):
        archive=self.root/'bad.zip'
        with zipfile.ZipFile(archive,'w') as z:z.writestr('../escape','secret')
        with self.assertRaises(ValueError):w.extract_zip(archive,self.root/'extract')
        self.assertFalse((self.root.parent/'escape').exists())
        with zipfile.ZipFile(archive,'w') as z:z.writestr('nested.zip',b'')
        events=[]
        with patch.object(w,'emit',lambda event,**kw:events.append((event,kw))):
            with self.assertRaises(ValueError):await w.import_paths(dict(paths=[str(archive)],temp=str(self.root/'temp')))
        self.assertTrue(any(e[0]=='import_error' for e in events))
    async def test_tdata_encrypted_offline_roundtrip(self):
        # Generated with upstream writer, all network connects forbidden throughout.
        from opentele.tl import TelegramClient
        from opentele.api import API,UseCurrentSession
        memory=MemorySession();memory.set_dc(2,'149.154.167.51',443);memory.auth_key=AuthKey(session_bytes(5))
        client=TelegramClient(memory,api=API.TelegramDesktop);client.UserId=123456
        async def forbidden(*a,**k):raise AssertionError('Import attempted network access')
        with patch.object(TelegramClient,'connect',forbidden):
            td=await client.ToTDesktop(flag=UseCurrentSession)
            path=self.root/'tdata';td.SaveTData(str(path),passcode='local-pin')
            rows=await w.from_tdata(path,'local-pin')
        self.assertEqual(len(rows),1);self.assertEqual(rows[0]['user_id'],123456)
        self.assertEqual(StringSession(rows[0]['session']).auth_key.key,session_bytes(5))
        with self.assertRaises(Exception):await w.from_tdata(path,'wrong-pin')
    async def test_session_requires_api_pair_and_rejects_fake_dc(self):
        path=self.root/'account.session';make_session(path);path.with_suffix('.json').unlink()
        with self.assertRaises(ValueError):w.from_sqlite(path,{})
        self.assertEqual(w.from_sqlite(path,{'api_id':12,'api_hash':'a'*32})[0]['api_id'],12)
        db=sqlite3.connect(path);db.execute("UPDATE sessions SET server_address='127.0.0.1'");db.commit();db.close()
        with self.assertRaises(ValueError):w.from_sqlite(path,{'api_id':12,'api_hash':'a'*32})


class Queue(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);w.CANCEL.clear();self.events=[];self.waits=[]
        self.job=dict(root=str(self.root),action='invite',accounts=[dict(id='a'*64)],target='@group',users='@userone\n@usertwo\n@userthree',delay=15)
    async def asyncTearDown(self):self.tmp.cleanup()
    async def execute(self,client):
        async def pause(seconds):self.waits.append(seconds)
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda event,**kw:self.events.append((event,kw))),patch.object(w,'pause',pause):
            await w.invite(self.job)
    async def test_sequential_and_restart_history(self):
        client=FakeClient();await self.execute(client)
        requests=[r for r in client.calls if not isinstance(r,tuple)]
        self.assertEqual([type(r) for r in requests],[functions.channels.InviteToChannelRequest,functions.channels.GetParticipantRequest]*3)
        self.assertEqual(self.waits,[15,15]);self.assertFalse(client.connected)
        again=FakeClient();await self.execute(again)
        self.assertEqual(again.calls,[])
    async def test_flood_stops_without_second_user(self):
        client=FakeClient(errors.FloodWaitError(request=None,capture=777))
        with self.assertRaises(w.StopJob):await self.execute(client)
        self.assertEqual(len(client.calls),1)
        event=[e for e in self.events if e[0]=='invite'][0][1]
        self.assertEqual(event['state'],'flood_wait');self.assertEqual(event['seconds'],777)
        self.assertFalse(client.connected)
    async def test_uncertain_send_never_retried(self):
        self.job['users']='userone';client=FakeClient(TimeoutError())
        with self.assertRaises(w.StopJob):await self.execute(client)
        db=sqlite3.connect(self.root/'invites.sqlite');self.assertEqual(db.execute('select state from invites').fetchone()[0],'uncertain');db.close()
        again=FakeClient();await self.execute(again);self.assertEqual(again.calls,[])
    async def test_pending_reservation_survives_crash(self):
        ledger=w.InviteLedger(self.root/'invites.sqlite');self.assertTrue(ledger.reserve(10,101,'old-account'));ledger.close()
        self.job['users']='userone';client=FakeClient();await self.execute(client);self.assertEqual(client.calls,[])
    async def test_missing_invitee_not_false_success(self):
        self.job['users']='userone';await self.execute(FakeClient(missing=True))
        self.assertFalse(any(e[0]=='invite' and e[1]['state']=='confirmed' for e in self.events))
    async def test_permissions_and_multiple_accounts(self):
        self.job['accounts'].append(dict(id='b'*64))
        with self.assertRaises(ValueError):await self.execute(FakeClient())
        self.job['accounts'].pop();client=FakeClient();client.target.creator=False;client.target.admin_rights=None
        with self.assertRaises(ValueError):await self.execute(client)
        self.assertEqual(client.calls,[])
    async def test_saved_cooldown_stops_before_connect(self):
        self.job['accounts'][0]['wait_until']='2099-01-01T00:00:00+00:00'
        client=FakeClient()
        with self.assertRaises(ValueError):await self.execute(client)
        self.assertFalse(client.connected);self.assertEqual(client.calls,[])
    async def test_real_socks_transport_and_no_direct_fallback(self):
        # A real local SOCKS5 handshake, then refused connection. No Telegram socket.
        import struct
        handshakes=[]
        async def proxy(reader,writer):
            try:
                version,methods=await reader.readexactly(2)
                handshakes.append((version,await reader.readexactly(methods)))
                writer.write(b'\x05\x00');await writer.drain()
                head=await reader.readexactly(4)
                if head[3]==1: host=await reader.readexactly(4)
                elif head[3]==3:host=await reader.readexactly((await reader.readexactly(1))[0])
                else:host=await reader.readexactly(16)
                port=struct.unpack('!H',await reader.readexactly(2))[0]
                handshakes.append((host,port))
                writer.write(b'\x05\x05\x00\x01'+b'\x00'*6);await writer.drain()
            finally:writer.close();await writer.wait_closed()
        server=await asyncio.start_server(proxy,'127.0.0.1',0)
        memory=MemorySession();memory.set_dc(2,'149.154.167.51',443);memory.auth_key=AuthKey(session_bytes())
        client=w.client_for(dict(session=StringSession.save(memory),api_id=1234,api_hash='a'*32,proxy='socks5://127.0.0.1:'+str(server.sockets[0].getsockname()[1])))
        try:
            with self.assertRaises(ConnectionError):await asyncio.wait_for(client.connect(),5)
            self.assertEqual(handshakes[0][0],5);self.assertEqual(handshakes[1][1],443)
            self.assertEqual(len(handshakes),2)  # One proxy connection, no direct retry.
        finally:
            await client.disconnect();server.close();await server.wait_closed()
    async def test_warmup_read_only(self):
        client=FakeClient();self.job['action']='warmup'
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda event,**kw:self.events.append((event,kw))):await w.check_accounts(self.job)
        self.assertEqual(client.calls,[('dialogs',20)])
        self.assertTrue(any(e[0]=='account' and e[1]['status']=='ready' for e in self.events))
    async def test_cancel_before_send(self):
        w.CANCEL.set();client=FakeClient()
        with self.assertRaises(w.StopJob):await self.execute(client)
        self.assertEqual(client.calls,[])


class Parsing(unittest.TestCase):
    def test_proxy_ipv6_credentials_and_rejection(self):
        p=w.parse_proxy('socks5://hello:p%40ss@[::1]:1080')
        self.assertEqual((p['addr'],p['port'],p['password'],p['rdns']),('::1',1080,'p@ss',True))
        self.assertEqual(w.parse_proxy('127.0.0.1:8080:u:p')['username'],'u')
        self.assertIsNone(w.parse_proxy(''))
        for value in ['https://host:42','host:70000','host/path','socks5://host']:
            with self.assertRaises(ValueError):w.parse_proxy(value)
    def test_usernames(self):
        self.assertEqual(w.usernames('@UserOne\nhttps://t.me/usertwo\n@userone'),['UserOne','usertwo'])
        with self.assertRaises(ValueError):w.usernames('not a valid user')
    def test_real_client_is_memory_and_keeps_proxy(self):
        memory=MemorySession();memory.set_dc(2,'149.154.167.51',443);memory.auth_key=AuthKey(session_bytes())
        client=w.client_for(dict(session=StringSession.save(memory),api_id=1234,api_hash='a'*32,proxy='http://user:password@127.0.0.1:8080'))
        self.assertIsInstance(client.session,StringSession);self.assertEqual(client._proxy['addr'],'127.0.0.1')
        self.assertEqual(client._proxy['username'],'user');self.assertFalse(client._auto_reconnect)
    def test_process_exclusive(self):
        with tempfile.TemporaryDirectory() as root:
            with w.exclusive(root):
                with self.assertRaises(OSError):
                    with w.exclusive(root):pass


if __name__=='__main__':unittest.main(verbosity=2)
