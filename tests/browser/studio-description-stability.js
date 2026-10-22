'use strict';
process.env.TZ='Asia/Krasnoyarsk';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path'),cp=require('child_process'),Module=require('module');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload'),C=require('../../tools/uploader/studio-editor-controls');
const B=require('../../tools/uploader/studio-browser');
const {setup}=require('./studio-shorts-queue');
const baseline='47515b90ebf064baa7251e87b25da72d8149f6f1';
function fromMain(relative){const file=path.resolve(__dirname,'../../'+relative),m=new Module(file,module);m.filename=file;m.paths=Module._nodeModulePaths(path.dirname(file));m._compile(cp.execFileSync('git',['show',baseline+':'+relative],{encoding:'utf8'}),file);return m.exports;}
const fallback=lang=>Array.from({length:3},(_,n)=>((lang==='ru'?'Разбор сделки '+n+': спокойное объяснение, риск и проверка сигнала.\n\nhttps://example.com\n':'Trade review '+n+': a complete explanation, risk and checking the signal.\n\nhttps://example.com\n').repeat(30)).slice(0,1648+n*20).trim());
(async()=>{
 const browser=await chromium.launch({headless:true}),temp=fs.mkdtempSync(path.join(os.tmpdir(),'vb-description-'));
 const evidenceDir=path.resolve(process.env.STUDIO_EVIDENCE_DIR||'artifacts/studio-description');fs.mkdirSync(evidenceDir,{recursive:true});
 const evidence={source:'isolated-browser-fixture',liveAccountsTested:false,baseline,build:S.BUILD,checks:[],channels:[]};
 const save=()=>fs.writeFileSync(path.join(evidenceDir,'description-evidence.json'),JSON.stringify(evidence,null,2));
 try{
  const oldFactory=fromMain('tools/uploader/studio-editor-controls.js'),old=oldFactory({until:S.until,norm:S.norm,read:S.read,typingBudget:S.typingBudget}),oldS=fromMain('tools/uploader/youtube-studio-upload.js');
  const page=await browser.newPage({viewport:{width:800,height:600}});
  await page.setContent('<div id="port" style="position:fixed;left:40px;top:50px;width:600px;height:300px;overflow:hidden"><div id="field" contenteditable="true" style="height:1200px;white-space:pre-wrap">Existing description</div></div><div id="sticky" style="position:fixed;left:40px;top:50px;width:600px;height:60px;z-index:8;background:#111">Sticky header</div>');
  const box=page.locator('#field'),before=(await box.evaluateAll(old.inspectEditors))[0],after=await S.editorState(box);
  const actualHit=await page.evaluate(()=>document.elementFromPoint(340,260)?.id);
  assert.equal(actualHit,'field');assert.equal(before.point,null);assert(after.point,'A visible uncovered part of the editor must be usable');
  evidence.checks.push({name:'v19e-false-DIV-blocker',realUncoveredHit:actualHit,before,after});save();
  console.log('REPRO v19e: inspector reports DIV/no point while Chromium hit-testing proves the editor has an uncovered visible area; current sampling finds it.');
  await S.activateEditor(box,{timeoutMs:2000});assert((await S.editorState(box)).focused);
  await page.evaluate(()=>{const e=document.createElement('div');e.id='real-modal';e.style.cssText='position:fixed;inset:0;z-index:20;background:#222';document.body.append(e);});
  await assert.rejects(S.activateEditor(box,{timeoutMs:350}),/перекрывает DIV#real-modal/);assert.equal(await S.read(box),'Existing description');
  evidence.checks.push({name:'real-modal-remains-blocking',state:await S.editorState(box)});save();
  await page.setContent('<textarea></textarea>');const controller=new AbortController(),events=[];let count=0;
  await assert.rejects(S.typeText(page.locator('textarea'),'A'.repeat(1650),{name:'описания',delay:20,signal:controller.signal,onProgress:n=>{count=n;if(n>=1000)controller.abort();},onState:event=>events.push(event)}),/ввод остановлен/);
  const stopped=await page.locator('textarea').inputValue();await new Promise(r=>setTimeout(r,350));assert.equal(await page.locator('textarea').inputValue(),stopped);assert(count>=1000&&count<1650);assert(!events.includes('retry'));
  evidence.checks.push({name:'cancel-at-1000-stops-later-chunks',count,stableLength:stopped.length,retries:events.filter(x=>x==='retry').length});save();
  // Precise mismatch evidence without storing the description itself.
  await page.setContent('<textarea maxlength="4"></textarea>');let failed;
  try{await S.typeText(page.locator('textarea'),'ABCDEFGHI',{delay:1,verifyMs:180});}catch(error){failed=error;}
  assert(failed?.editorDiagnostic);const diff=failed.editorDiagnostic.comparison;assert.equal(diff.relation,'missing-suffix');assert.equal(diff.firstDifference,4);assert.equal(diff.expectedLength,9);assert.equal(diff.actualLength,4);assert(!JSON.stringify(diff).includes('ABCDEFGHI'));
  evidence.checks.push({name:'strict-mismatch-diagnostic',diagnostic:failed.editorDiagnostic});save();
  const policy=C.typingPolicy('X'.repeat(1650),{name:'описания',delay:18,activationMs:25000,verifyMs:35000});
  const item={title:'Title',description:'X'.repeat(1650),tags:[]},oldBudget=oldS.metadataBudget(item,{}),newBudget=S.metadataBudget(item,{});
  assert(policy.maxAttempts===3&&policy.chunkSize===40);assert(newBudget>policy.totalMs);assert(oldBudget<policy.totalMs);
  evidence.checks.push({name:'metadata-deadline-covers-retries',oldBudget,newBudget,policy});save();await page.close();
  console.log('PASS exact mismatch diagnostics, cancellation after 1000 characters, real modal refusal, and outer metadata deadline covers all bounded attempts.');
  // main's evaluate(fn, {timeout:800}) passes a page-function ARGUMENT, not
  // Locator.evaluate's third-argument options. If the wizard disappears after
  // isVisible, it waits the context's 10s and loses the production 5s probe.
  const race=await browser.newPage();race.setDefaultTimeout(10000);
  async function disappearing(driver){
   await race.setContent('<ytcp-uploads-dialog><h2>Saving...</h2></ytcp-uploads-dialog>');
   const actual=race.locator('ytcp-uploads-dialog');
   const root={isVisible:async()=>{await actual.evaluate(el=>el.remove());return true;},evaluate:(...args)=>actual.evaluate(...args),locator:(...args)=>actual.locator(...args),getByRole:(...args)=>actual.getByRole(...args)};
   return driver.publicationState(race,{scope:{all:async()=>[root]}});
  }
  const oldB=fromMain('tools/uploader/studio-browser.js'),beforeAt=Date.now(),pending=disappearing(oldB);let priorError;
  try{await B.bounded(()=>pending,5000,'REPRO v19e: страница не отвечает (5000 ms)');}catch(error){priorError=error.message;}
  assert(priorError);await pending;const beforeMs=Date.now()-beforeAt;
  const afterAt=Date.now();await B.bounded(()=>disappearing(B),5000,'Completion probe must tolerate a removed wizard');const afterMs=Date.now()-afterAt;
  const state=await B.publicationState(race);assert.equal(state.state,'closed');assert(beforeMs>=9000);assert(afterMs<4000);
  evidence.checks.push({name:'removed-wizard-read-timeout-argument',baselineError:priorError,baselineDurationMs:beforeMs,currentDurationMs:afterMs,currentState:state.state,actionChanges:false});save();await race.close();
  console.log('REPRO v19e removed wizard exceeds the 5s state probe; PASS corrected read-only evaluate timeout returns in',afterMs,'ms; Publish/ID/Close actions unchanged.');
  const bankPath=process.env.STUDIO_DESCRIPTION_BANKS;
  const banks=bankPath?JSON.parse(fs.readFileSync(bankPath,'utf8').replace(/^\uFEFF/,'')):{ru:fallback('ru'),en:fallback('en')};
  for(const lang of ['ru','en']){
   const descriptions=banks[lang].slice(0,3);assert.equal(descriptions.length,3);for(const text of descriptions)assert(text.length>=1500&&text.length<=1900,'Use realistic SEO descriptions around 1650 characters: '+text.length);
   const f=await setup(browser,lang,temp,{count:3,descriptions,neverClose:true,checksWarning:true,warningEnableDelay:200,warningCloseDelay:350}),stages=f.stages;
   try{
    const result=await S.runSequential(f.page,f.pack,{titleDelay:1,descriptionDelay:20,saveTimeout:4000,newTabPerVideo:true,diagnosticsDir:evidenceDir},f.host);
    const stats=Object.fromEntries(['create','file','publish','close','confirmation'].map(k=>[k,f.telemetry.filter(x=>x.type===k).length]));assert.equal(result.length,3);assert.equal(f.saved.size,3);assert.equal(stats.publish,3);assert.equal(stats.close,0);assert.equal(stats.create,3);assert.equal(stats.confirmation,3);assert.equal(stages.filter(s=>s.stage==='studio_new_tab').length,2);assert.equal(stages.filter(s=>s.stage==='studio_verify').length,0);assert.equal(f.ctx.pages().length,2);
    assert.equal(stages.filter(s=>s.stage==='item_confirmed').length,3);assert.equal(stages.filter(s=>s.stage==='metadata_description'&&/Описание проверено целиком/.test(s.text)).length,3);assert(!stages.some(s=>/не подтверждена|Повторяю ввод/.test(s.text)));
    for(let n=0;n<3;n++){const record=f.saved.get('video'+String(n+1).padStart(6,'0'));assert.equal(record.description,S.norm(descriptions[n]));assert.equal(record.visibility,'public');assert(!record.paid&&!record.ai);}
    evidence.channels.push({source:'isolated-browser-fixture',lang,descriptionSource:banks.source||'synthetic fallback',descriptionMarket:banks.descriptionMarket||lang,descriptionLengths:descriptions.map(x=>S.norm(x).length),stats,confirmed:result.map(r=>r.videoId),stages});save();
    console.log('PASS',lang,'3 Shorts, SEO lengths',descriptions.map(x=>x.length).join('/'),'at production 20ms keyboard speed: exact description → No/No → Public → primary Publish once → check warning once → completion screen → new tab → Create; item_confirmed ×3.');
   }catch(error){const report=await S.diagnostics(f.page,error,evidenceDir);evidence.failure={message:error.message,diagnostic:path.basename(report)};save();throw error;}
   finally{await f.ctx.close();}
  }
  evidence.result='PASS_FIXTURES_ONLY';save();
 }finally{await browser.close();fs.rmSync(temp,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1;});
