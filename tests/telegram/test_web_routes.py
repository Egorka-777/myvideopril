import asyncio
import base64
import json
import socket
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock,Mock,patch
from test_status import ROOT
from web_proxy import Bridge,allowed_host,route_hosts,safe_host,serve,main


class WebRoutes(unittest.IsolatedAsyncioTestCase):
    def test_aliases_are_official_same_dc_and_malformed_hosts_rejected(self):
        self.assertEqual(route_hosts('zws2.web.telegram.org'),['zws2.web.telegram.org','venus.web.telegram.org'])
        self.assertEqual(route_hosts('kws4-1.web.telegram.org'),['kws4-1.web.telegram.org','vesta-1.web.telegram.org'])
        self.assertEqual(route_hosts('web.telegram.org'),['web.telegram.org'])
        for host in ('user:password@web.telegram.org','web.telegram.org.evil.test','x/y.telegram.org'):
            self.assertFalse(allowed_host(host))

    async def test_browser_alias_failure_uses_same_dc_through_same_connector(self):
        from python_socks import ProxyError
        left,right=socket.socketpair();calls=[];events=[]
        async def connect(**kwargs):
            calls.append(kwargs)
            if len(calls)==1:raise ProxyError('secret-password')
            return left
        try:
            with patch('web_proxy.proxy_connector',return_value=SimpleNamespace(connect=connect)):
                bridge=Bridge('same-proxy','s'*32,on_route=lambda **ev:events.append(ev))
            result=await bridge.connect('zws2.web.telegram.org')
            self.assertIs(result,left)
            self.assertEqual([c['dest_host'] for c in calls],['zws2.web.telegram.org','venus.web.telegram.org'])
            self.assertEqual([e['status'] for e in events],['failed','ready'])
            self.assertNotIn('secret-password',str(events))
        finally:left.close();right.close()

    async def test_web_hostname_does_not_retry_other_dc_or_direct(self):
        from python_socks import ProxyError
        calls=[]
        async def connect(**kwargs):calls.append(kwargs);raise ProxyError('secret')
        with patch('web_proxy.proxy_connector',return_value=SimpleNamespace(connect=connect)):
            bridge=Bridge('same-proxy','s'*32)
        with self.assertRaises(ConnectionError):await bridge.connect('web.telegram.org')
        self.assertEqual(len(calls),1)

    def test_instagram_allowlist_is_service_specific(self):
        for domain in ('instagram.com','threads.com','threads.net','cdninstagram.com','fbcdn.net'):
            for host in (domain,'www.'+domain,'a.b.'+domain,('WWW.'+domain).upper()+'.'):
                with self.subTest(host=host):
                    self.assertTrue(allowed_host(host,'instagram'))
                    self.assertFalse(allowed_host(host))
            for host in ('evil'+domain,domain+'.evil.test','user:password@'+domain,'x/y.'+domain):
                with self.subTest(host=host):self.assertFalse(allowed_host(host,'instagram'))
        for host in ('telegram.org','web.telegram.org','zws2.web.telegram.org','evil.test','127.0.0.1'):
            self.assertFalse(allowed_host(host,'instagram'))
        self.assertFalse(allowed_host('instagram.com','unknown'))

    def test_instagram_routes_and_diagnostics_do_not_use_telegram_aliases(self):
        for host in ('www.instagram.com','www.threads.com','www.threads.net','scontent.cdninstagram.com','video.fbcdn.net'):
            self.assertEqual(route_hosts(host,'instagram'),[host])
            self.assertEqual(safe_host(host,'instagram'),host)
        self.assertEqual(route_hosts('zws2.web.telegram.org','instagram'),['zws2.web.telegram.org'])
        self.assertEqual(safe_host('user:password@www.instagram.com','instagram'),'other.instagram.com')
        self.assertEqual(safe_host('web.telegram.org','instagram'),'other.instagram.com')

    async def test_instagram_failure_has_one_target_and_safe_diagnostics(self):
        from python_socks import ProxyError
        connector=SimpleNamespace(connect=AsyncMock(side_effect=ProxyError('user:secret-password')))
        events=[]
        with patch('web_proxy.proxy_connector',return_value=connector):
            bridge=Bridge('same-proxy','s'*32,on_route=lambda **ev:events.append(ev),service='instagram')
        with self.assertRaisesRegex(ConnectionError,'Instagram Web proxy tunnel failed'):
            await bridge.connect('www.instagram.com')
        connector.connect.assert_awaited_once_with(dest_host='www.instagram.com',dest_port=443,timeout=6)
        self.assertEqual(events,[dict(host='www.instagram.com',target='www.instagram.com',status='failed',code='proxy_tunnel')])
        self.assertNotIn('secret-password',str(events))

    async def test_bridge_rejects_cross_service_connect_before_upstream(self):
        auth=base64.b64encode(('local:'+'s'*32).encode()).decode()
        for service,hosts in (('telegram',('www.instagram.com','scontent.cdninstagram.com','video.fbcdn.net')),
                              ('instagram',('web.telegram.org','zws2.web.telegram.org','www.instagram.com.evil.test','user:password@www.instagram.com'))):
            connector=SimpleNamespace(connect=AsyncMock())
            with patch('web_proxy.proxy_connector',return_value=connector):
                bridge=Bridge('same-proxy','s'*32,service=service)
            for host in hosts:
                with self.subTest(service=service,host=host):
                    reader=asyncio.StreamReader()
                    reader.feed_data(f'CONNECT {host}:443 HTTP/1.1\r\nProxy-Authorization: Basic {auth}\r\n\r\n'.encode())
                    writer=Mock(drain=AsyncMock(),wait_closed=AsyncMock())
                    await bridge.handle(reader,writer)
                    self.assertIn(b'403 Forbidden',writer.write.call_args.args[0])
            connector.connect.assert_not_awaited()

    async def test_stdin_service_reaches_bridge_with_telegram_default(self):
        for service in (None,'instagram'):
            job=dict(proxy='same-proxy',secret='s'*32)
            if service is not None:job['service']=service
            with patch('web_proxy.Bridge',side_effect=ValueError('stop before server startup')) as constructor:
                with self.assertRaises(ValueError):await serve(job)
            self.assertEqual(constructor.call_args.kwargs['service'],service or 'telegram')

    def test_main_reads_optional_service_from_stdin(self):
        for service in (None,'instagram'):
            job=dict(proxy='same-proxy',secret='s'*32)
            if service is not None:job['service']=service
            with patch('web_proxy.sys.stdin',Mock(readline=Mock(return_value=json.dumps(job)))),patch('web_proxy.sys.stdout'),patch('web_proxy.serve',new_callable=AsyncMock) as run:
                self.assertEqual(main(),0)
            run.assert_awaited_once_with(job)


if __name__=='__main__':unittest.main()
