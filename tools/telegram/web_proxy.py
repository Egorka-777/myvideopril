"""Ephemeral loopback CONNECT bridge for WebView2; no direct fallback.
Credentials are received over stdin and never included in argv, stdout or errors.
"""
import asyncio
import base64
import contextlib
import hmac
import ipaddress
import json
import sys
import threading
from worker import proxy_connector


def allowed_host(host):
    value=host.lower().rstrip('.')
    return value=='telegram.org' or value.endswith('.telegram.org')


class Bridge:
    def __init__(self, proxy, secret, allow_host=allowed_host):
        self.connector=proxy_connector(proxy)
        if self.connector is None:
            raise ValueError('Bridge requires configured upstream proxy')
        self.secret=secret
        self.allow_host=allow_host
        self.connections=set()

    async def handle(self, reader, writer):
        self.connections.add(writer)
        upstream=None
        try:
            raw=await asyncio.wait_for(reader.readuntil(b'\r\n\r\n'),15)
            if len(raw)>16384:
                return
            lines=raw.decode('iso-8859-1').split('\r\n')
            method, authority, version=lines[0].split(' ')
            headers={}
            for line in lines[1:]:
                if ':' in line:
                    k,v=line.split(':',1);headers[k.strip().lower()]=v.strip()
            expected='Basic '+base64.b64encode(('local:'+self.secret).encode()).decode()
            if not hmac.compare_digest(headers.get('proxy-authorization',''),expected):
                writer.write(b'HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm="VideoBatch local proxy"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');await writer.drain();return
            if method!='CONNECT' or not version.startswith('HTTP/1.'):
                writer.write(b'HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n');await writer.drain();return
            host,port=authority.rsplit(':',1);host=host.strip('[]')
            if port!='443' or not self.allow_host(host):
                writer.write(b'HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n');await writer.drain();return
            sock=await self.connector.connect(dest_host=host,dest_port=443,timeout=15)
            try:
                remote_reader,upstream=await asyncio.open_connection(sock=sock)
            except BaseException:
                sock.close();raise
            self.connections.add(upstream)
            writer.write(b'HTTP/1.1 200 Connection Established\r\n\r\n');await writer.drain()
            async def pipe(src,dest):
                while True:
                    chunk=await src.read(65536)
                    if not chunk:
                        return
                    dest.write(chunk);await dest.drain()
            tasks=[asyncio.create_task(pipe(reader,upstream)),asyncio.create_task(pipe(remote_reader,writer))]
            try:
                await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
            finally:
                for task in tasks:task.cancel()
                await asyncio.gather(*tasks,return_exceptions=True)
        except asyncio.CancelledError:
            raise
        except Exception:
            with contextlib.suppress(Exception):
                writer.write(b'HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n');await writer.drain()
        finally:
            for stream in (writer,upstream):
                if stream is not None:
                    self.connections.discard(stream);stream.close()
                    with contextlib.suppress(Exception):await stream.wait_closed()

    async def close(self):
        for writer in list(self.connections):writer.close()


async def serve(job):
    bridge=Bridge(job.get('proxy',''),job['secret'])
    stopped=threading.Event()
    def watch():
        for line in sys.stdin:
            if line.strip()=='cancel':break
        stopped.set()  # Parent crash/EOF also closes the bridge.
    threading.Thread(target=watch,daemon=True).start()
    server=await asyncio.start_server(bridge.handle,'127.0.0.1',0,limit=16384)
    print(json.dumps(dict(event='listening',port=server.sockets[0].getsockname()[1])),flush=True)
    try:
        while not stopped.is_set():await asyncio.sleep(.1)
    finally:
        server.close();await server.wait_closed();await bridge.close()


def main():
    sys.stdin.reconfigure(encoding='utf-8-sig');sys.stdout.reconfigure(encoding='utf-8')
    try:
        line=sys.stdin.readline(16385)
        if len(line)>16384:raise ValueError()
        job=json.loads(line)
        if not isinstance(job.get('secret'),str) or len(job['secret'])<32:raise ValueError()
        asyncio.run(serve(job));return 0
    except Exception:
        print(json.dumps(dict(event='error',text='Не удалось запустить локальный маршрут прокси')),flush=True);return 1


if __name__=='__main__':raise SystemExit(main())
