"use strict";
// Real Chromium + real worker process. Dolphin API itself is a local fixture.
const assert = require("assert"), fs = require("fs"), os = require("os"), path = require("path"), http = require("http");
const {spawn} = require("child_process");
const {chromium} = require("../../tools/uploader/node_modules/playwright-core");

(async()=>{
  const temp=fs.mkdtempSync(path.join(os.tmpdir(),"vb-lifecycle-"));
  const profile=path.join(temp,"browser"); fs.mkdirSync(profile);
  const chrome=await chromium.launchPersistentContext(profile,{headless:true,args:["--remote-debugging-port=0"]});
  let control=null, server=null, activeWorker=null, starts=0, stops=0, endpoint="";
  const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
  try {
    const portFile=path.join(profile,"DevToolsActivePort");
    for(let i=0;i<100&&!fs.existsSync(portFile);i++)await delay(100);
    assert(fs.existsSync(portFile),"Chromium CDP must become available");
    const [port,wsPath]=fs.readFileSync(portFile,"utf8").trim().split(/\r?\n/);
    endpoint=`ws://127.0.0.1:${port}${wsPath}`;
    control=await chromium.connectOverCDP(endpoint);
    const context=control.contexts()[0];
    const userPage=context.pages()[0]; await userPage.goto("about:blank#user-tab");
    server=http.createServer((req,res)=>{
      res.setHeader("Content-Type","application/json");
      if(req.url.includes("/stop")){stops++;return res.end('{"success":true}');}
      if(req.url.includes("/start")){starts++;return res.end(JSON.stringify({success:true,automation:{port:Number(port),wsEndpoint:endpoint}}));}
      res.end('{"success":true}');
    });
    await new Promise(resolve=>server.listen(0,"127.0.0.1",resolve));
    const jobs=path.join(temp,"jobs");fs.mkdirSync(jobs);
    async function run(fields,cancelWhenWaiting=false){
      const file=path.join(jobs,"job-"+Date.now()+".json");
      fs.writeFileSync(file,JSON.stringify({profileId:"111",localPort:server.address().port,token:"fixture",watchMesh:true,keepProfileOpen:true,...fields}));
      const child=spawn(process.execPath,[path.resolve(__dirname,"../../tools/uploader/worker.js"),file],{stdio:["pipe","pipe","pipe"]});
      activeWorker=child;let stdout="",stderr="";
      child.stdout.on("data",chunk=>{stdout+=chunk;});child.stderr.on("data",chunk=>{stderr+=chunk;});
      const completed=new Promise((resolve,reject)=>{child.on("error",reject);child.on("exit",code=>resolve({code,stdout,stderr}));});
      const timer=setTimeout(()=>child.kill("SIGKILL"),30000);
      if(cancelWhenWaiting){
        for(let i=0;i<100&&!stdout.includes('"stage":"open_channel"');i++)await delay(100);
        assert(stdout.includes('"stage":"open_channel"'),"Worker must reach navigation before cancellation");
        child.stdin.write("cancel\n");
      }
      try{return await completed;}finally{clearTimeout(timer);activeWorker=null;}
    }
    for(let i=0;i<2;i++){
      const r=await run({checkOnly:true});assert.equal(r.code,0,r.stdout+r.stderr);
      assert.equal(userPage.isClosed(),false);assert.equal(userPage.url(),"about:blank#user-tab");
      assert.equal(control.isConnected(),true);assert.equal(context.pages().length,1);
    }
    assert.equal(starts,1,"Second worker must attach to the persisted live browser endpoint");
    const failed=await run({watchTargets:[]});
    assert.equal(failed.code,1);assert.match(failed.stdout,/Нет ссылок каналов/);
    assert.equal(userPage.isClosed(),false);assert.equal(control.isConnected(),true);
    // A page without YouTube data holds the worker in a cancellable readiness wait.
    await context.route("**/*",route=>route.fulfill({contentType:"text/html",body:"<ytd-browse>Loading</ytd-browse>"}));
    const cancelled=await run({todayDate:"2026-10-05",watchTargets:[{channelUrl:"https://www.youtube.com/@fixture/videos"}]},true);
    assert.equal(cancelled.code,130,cancelled.stdout+cancelled.stderr);
    assert.match(cancelled.stdout,/Задание остановлено/);
    assert.equal(userPage.isClosed(),false);assert.equal(control.isConnected(),true);assert.equal(stops,0);
    // Release the only remaining tab via production helper: browser must survive.
    for(const p of context.pages())if(p!==userPage)await p.close();
    await require("../../tools/uploader/worker.js").releaseTaskPage(userPage);
    assert.equal(control.isConnected(),true);assert.equal(context.pages().length,1);
    assert.equal(context.pages()[0].url(),"about:blank");
    console.log("OK: real CDP lifecycle (repeat attach, user tab preserved, failure exit, cancellation, last-tab retention, zero profile stops)");
  } finally {
    if(activeWorker)activeWorker.kill("SIGKILL");
    if(control)await control.close();
    if(server)await new Promise(resolve=>server.close(resolve));
    await chrome.close();
    fs.rmSync(temp,{recursive:true,force:true});
  }
})().catch(e=>{console.error(e);process.exitCode=1;});
