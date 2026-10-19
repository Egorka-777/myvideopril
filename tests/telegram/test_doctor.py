import asyncio
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'tools'/'telegram'))
import worker as w
from test_worker import FakeClient
import test_connect_flow as flow
from telethon import errors


class Doctor(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):w.CANCEL.clear()

    async def test_report_requires_get_me_and_contains_no_account_secrets(self):
        account=flow.EndpointRoutes().account()
        events=[];client=FakeClient()
        with patch.object(w,'client_for',return_value=client),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.check_accounts(dict(action='doctor',accounts=[account]))
        reports=[d['report'] for e,d in events if e=='diagnostic_report']
        self.assertEqual(len(reports),1)
        row=reports[0]['accounts'][0]
        self.assertEqual((row['status'],row['code'],row['transport']),('ready','get_me','wss'))
        self.assertEqual(row['repair'],'none')
        text=json.dumps(reports)
        for secret in (account['proxy'],account['session'],account['api_hash'],'private-password'):
            self.assertNotIn(secret,text)
        self.assertNotIn('username',text);self.assertNotIn('phone',text)

    async def test_revoked_session_has_manual_action_and_no_network_retry(self):
        account=flow.EndpointRoutes().account();client=FakeClient();events=[]
        async def revoked():raise errors.SessionRevokedError(request=None)
        client.get_me=revoked
        with patch.object(w,'client_for',return_value=client) as create,patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.check_accounts(dict(action='doctor',accounts=[account]))
        self.assertEqual(create.call_count,1)
        row=next(d['report']['accounts'][0] for e,d in events if e=='diagnostic_report')
        self.assertEqual(row['repair'],'manual_login')
        self.assertEqual(row['status'],'unauthorized')
        self.assertFalse(client.connected)

    async def test_wss_error_is_preserved_after_tcp_failure(self):
        from wss_transport import TransportError
        account=flow.EndpointRoutes().account();events=[];seen=[]
        def create(candidate):
            seen.append(candidate)
            client=FakeClient()
            async def fail():
                if candidate.get('_transport')=='wss':raise TransportError('proxy_tunnel')
                raise ConnectionError('must-not-leak-password')
            client.connect=fail
            return client
        async def web(value,host):return dict(status='ready')
        with patch.object(w,'client_for',create),patch.object(w,'probe_proxy',web),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.check_accounts(dict(action='doctor',accounts=[account]))
        row=next(d['report']['accounts'][0] for e,d in events if e=='diagnostic_report')
        self.assertEqual(row['code'],'proxy_tunnel')
        self.assertEqual(row['repair'],'check_proxy')
        self.assertEqual(row['attempts'][0],dict(transport='wss',code='proxy_tunnel'))
        self.assertTrue(all(a['proxy']==account['proxy'] for a in seen))
        self.assertNotIn('must-not-leak-password',str(events))

    async def test_network_probe_is_not_account_confirmation(self):
        import wss_transport
        events=[]
        async def web(*args):return dict(status='ready',latency_ms=1)
        async def wss(*args):return dict(status='ready',code='wss_ready')
        with patch.object(w,'probe_proxy',web),patch.object(wss_transport,'probe_wss',wss),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.check_proxies(dict(proxies=[dict(id='p',url='socks5://host:1080')]))
        self.assertTrue(any(e=='proxy' and d['status']=='ready' for e,d in events))
        self.assertFalse(any(e=='account' for e,d in events))

    async def test_saved_telegram_cooldown_prevents_network_attempt(self):
        from datetime import datetime,timezone,timedelta
        account=flow.EndpointRoutes().account();events=[]
        account['wait_until']=(datetime.now(timezone.utc)+timedelta(minutes=5)).isoformat()
        with patch.object(w,'client_for',side_effect=AssertionError('Must not connect')),patch.object(w,'emit',lambda e,**d:events.append((e,d))):
            await w.check_accounts(dict(action='doctor',accounts=[account]))
        row=next(d['report']['accounts'][0] for e,d in events if e=='diagnostic_report')
        self.assertEqual(row['repair'],'wait')

    def test_action_plan_distinguishes_network_auth_tls_and_telegram_wait(self):
        for status,code,repair in [('network','tls_certificate','check_tls_wss'),('unauthorized','SessionRevokedError','manual_login'),('blocked','UserDeactivatedBanError','stop'),('flood_wait','FloodWaitError','wait')]:
            self.assertEqual(w.diagnostic_row(1,status,code)['repair'],repair)


if __name__=='__main__':unittest.main()
