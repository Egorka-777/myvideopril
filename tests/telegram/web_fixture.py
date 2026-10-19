"""Local TLS + SOCKS5 + worker WebSocket fixture. No Telegram API/accounts.

Used only by the native Windows integration test, never distributed in the app.
"""
import asyncio
import contextlib
from http import HTTPStatus
import json
from pathlib import Path
import ssl
import sys
from websockets.asyncio.server import serve


async def main(job):
    root=Path(__file__).with_name('fixtures')
    ctx=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(root/'web-test-cert.pem',root/'web-test-key.pem')
    image=job['image']  # Synthetic QR image; contains no real authorization token.
    html="""<!doctype html><meta charset=utf-8><style>body{margin:0}#auth-qr-form{padding:50px}.qr-container{width:300px;height:300px}.qr-inner{display:none}</style>
<div id=auth-qr-form><div class=qr-inner><div class=qr-container></div></div><div class=qr-loading>Loading QR...</div></div>
<script>
const worker=new Worker(URL.createObjectURL(new Blob([`const s=new WebSocket('wss://zws2.web.telegram.org/apiws','binary');s.onopen=()=>s.send('qr');s.onmessage=e=>postMessage(e.data);`],{type:'text/javascript'})));
worker.onmessage=()=>{document.querySelector('.qr-container').innerHTML='<img width=300 height=300 src="data:image/png;base64,IMAGE">';document.querySelector('.qr-inner').style.display='block';document.querySelector('.qr-loading').remove();};
</script>""".replace('IMAGE',image)
    async def request(connection,request):
        if request.path=='/a/':
            response=connection.respond(HTTPStatus.OK,html)
            del response.headers['Content-Type']
            response.headers['Content-Type']='text/html; charset=utf-8'
            return response
    async def websocket(ws):
        if await ws.recv()!='qr':return
        await asyncio.sleep(1)
        await ws.send('qr')
        with contextlib.suppress(Exception):await ws.wait_closed()
    web=await serve(websocket,'127.0.0.1',0,ssl=ctx,subprotocols=['binary'],process_request=request)
    web_port=web.sockets[0].getsockname()[1]
    async def proxy(reader,writer):
        remote=None
        try:
            version,size=await reader.readexactly(2);await reader.readexactly(size)
            writer.write(b'\x05\x02');await writer.drain()
            version,size=await reader.readexactly(2);user=await reader.readexactly(size)
            size=(await reader.readexactly(1))[0];password=await reader.readexactly(size)
            if (user,password)!=(b'fixture',b'local-test-password'):
                writer.write(b'\x01\x01');await writer.drain();return
            writer.write(b'\x01\x00');await writer.drain()
            head=await reader.readexactly(4)
            if head!=b'\x05\x01\x00\x03':return
            size=(await reader.readexactly(1))[0];host=(await reader.readexactly(size)).decode()
            port=int.from_bytes(await reader.readexactly(2),'big')
            if port!=443 or host not in ('web.telegram.org','zws2.web.telegram.org','venus.web.telegram.org'):return
            if host=='zws2.web.telegram.org':
                writer.write(b'\x05\x05\x00\x01'+b'\x00'*6);await writer.drain();return
            incoming,remote=await asyncio.open_connection('127.0.0.1',web_port)
            writer.write(b'\x05\x00\x00\x01\x7f\x00\x00\x01\x01\xbb');await writer.drain()
            async def pipe(src,dst):
                while data:=await src.read(65536):dst.write(data);await dst.drain()
            tasks=[asyncio.create_task(pipe(reader,remote)),asyncio.create_task(pipe(incoming,writer))]
            try:await asyncio.wait(tasks,return_when=asyncio.FIRST_COMPLETED)
            finally:
                for task in tasks:task.cancel()
                await asyncio.gather(*tasks,return_exceptions=True)
        except (asyncio.IncompleteReadError,ConnectionError):pass
        finally:
            writer.close()
            if remote:remote.close()
    socks=await asyncio.start_server(proxy,'127.0.0.1',0)
    print(json.dumps(dict(port=socks.sockets[0].getsockname()[1])),flush=True)
    await asyncio.to_thread(sys.stdin.readline)
    socks.close();await socks.wait_closed();web.close();await web.wait_closed()


if __name__=='__main__':asyncio.run(main(json.loads(sys.stdin.readline())))
