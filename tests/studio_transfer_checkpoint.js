'use strict';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path'),cp=require('child_process');
const B=require('../tools/uploader/studio-browser');
(async()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'vb-studio-transfer-'));
 try{
  const file=path.join(temp,'video.mp4');fs.writeFileSync(file,'fixture');let native=0,cdp=0,acks=0;
  const page={context:()=>({newCDPSession:async()=>{cdp++;throw Error('Unexpected second transport');}})};
  const input={evaluate:async()=>false,setInputFiles:async()=>{assert.equal(acks,1,'Durable checkpoint must finish BEFORE file transfer');native++;throw Error('timeout after the browser accepted onchange');}};
  await assert.rejects(B.transferFile(page,input,file,()=>{},{beforeTransfer:async()=>acks++}),/timeout after/);
  assert.equal(native,1);assert.equal(cdp,0);assert.equal(acks,1);
  await assert.rejects(B.transferFile(page,input,file,()=>{},{beforeTransfer:async()=>{throw Error('state not saved');}}),/state not saved/);assert.equal(native,1,'No file transfer if persistence failed');
  const job=path.join(temp,'job.json');fs.writeFileSync(job,JSON.stringify({profileId:'fixture',token:'fixture-token',localPort:65534,skipQueueDelay:true,items:[]}));
  const run=cp.spawnSync(process.execPath,[path.resolve(__dirname,'../tools/uploader/worker.js'),job],{encoding:'utf8',timeout:5000});
  assert.equal(run.status,1,run.stderr);const events=run.stdout.split(/\r?\n/).filter(Boolean).map(x=>JSON.parse(x));
  assert(events.some(x=>x.stage==='error'&&/требуется обновить VideoBatch.exe/.test(x.error||x.text)));
  assert(!events.some(x=>x.stage==='dolphin'||x.stage==='file_select'),'An old EXE must fail before profile or file operations');
  console.log('PASS Studio transaction: checkpoint before transfer; rejected persistence sends nothing; ambiguous acceptance never uses a second transport; old EXE rejected before browser connection.');
 }finally{fs.rmSync(temp,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
