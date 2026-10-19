import asyncio
import socket
import unittest
from types import SimpleNamespace
from unittest.mock import patch
from test_status import ROOT
from web_proxy import Bridge,allowed_host,route_hosts


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


if __name__=='__main__':unittest.main()
