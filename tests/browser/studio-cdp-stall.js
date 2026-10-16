'use strict';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const B=require('../../tools/uploader/studio-browser');
(async()=>{
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'vb-cdp-stall-'));
 const context=await chromium.launchPersistentContext(dir,{headless:true,args:['--remote-debugging-port=0']});
 try{
  const [port,ws]=fs.readFileSync(path.join(dir,'DevToolsActivePort'),'utf8').trim().split(/\r?\n/);
  const control=await context.browser().newBrowserCDPSession();
  await control.send('Target.createTarget',{url:'data:text/html,<title>Stalled fixture</title><script>while(true){}</script>'});
  await new Promise(r=>setTimeout(r,300));
  const started=Date.now();
  await assert.rejects(B.connect(chromium,`ws://127.0.0.1:${port}${ws}`,()=>{},{timeout:1500}),/CDP за 1.5 секунд/);
  assert(Date.now()-started<5000,'No repeated 60-second waits');
  const version=await fetch(`http://127.0.0.1:${port}/json/version`).then(r=>r.json());
  assert(version.webSocketDebuggerUrl,'Browser is still alive after the connection timeout');
  await control.detach();
  console.log('PASS stalled renderer reproduces CDP timeout; bounded disconnect leaves browser alive');
 }finally{await context.close();fs.rmSync(dir,{recursive:true,force:true,maxRetries:10,retryDelay:100});}
})().catch(e=>{console.error(e);process.exitCode=1;});
