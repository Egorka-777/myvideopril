'use strict';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path'),cp=require('child_process'),Module=require('module');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload'),B=require('../../tools/uploader/studio-browser');
const {setup}=require('./studio-shorts-queue');
const base='2207c946c7f85b7b9363f5641be84a223a6aba47';
function baseline(){const file=path.resolve(__dirname,'../../tools/uploader/youtube-studio-upload.js'),m=new Module(file,module);m.filename=file;m.paths=Module._nodeModulePaths(path.dirname(file));m._compile(cp.execFileSync('git',['show',base+':tools/uploader/youtube-studio-upload.js'],{encoding:'utf8'}),file);return m.exports;}
(async()=>{
 const browser=await chromium.launch({headless:true}),temp=fs.mkdtempSync(path.join(os.tmpdir(),'vb-studio-recovery-'));
 const out=path.resolve(process.env.STUDIO_RECOVERY_EVIDENCE_DIR||'artifacts/studio-recovery');fs.mkdirSync(out,{recursive:true});
 const evidence={build:S.BUILD,baseline:base,source:'isolated-browser-fixture',liveAccountsTested:false,checks:[]};
 const save=()=>fs.writeFileSync(path.join(out,'recovery-evidence.json'),JSON.stringify(evidence,null,2));
 try{
  const page=await browser.newPage();
  await page.setContent('<div role="dialog"><ytcp-dialog style="display:block"><div role="dialog"><h2>Проверка видео ещё продолжается</h2><button>Опубликовать</button><button>Исправить</button></div></ytcp-dialog></div>');
  await assert.rejects(baseline().confirmPublishDespiteRunningChecks(page),/несколько окон/);
  assert(await S.runningChecksDialog(page));
  await page.setContent('<div role="dialog"><h2>Something else</h2><button>Publish</button></div>');assert.equal(await S.runningChecksDialog(page),false);
  evidence.checks.push({name:'nested-check-warning-v19h-reproduced-and-corrected'});save();
  // A real prefix is lost at the same approximate size as the supplied screenshot.
  // This fixture proves recovery, not the cause of that live screenshot.
  await page.setContent('<textarea style="width:600px;height:300px"></textarea><script>window.clears=0;document.addEventListener("keydown",e=>{if(e.ctrlKey&&e.key.toLowerCase()==="a")clears++;});</script>');
  const text=('SEO описание: законченная мысль, риск и проверка.\n\nhttps://example.com\n').repeat(30).slice(0,1650),events=[];let interrupted=false;
  await S.typeText(page.locator('textarea'),text,{name:'описания',delay:20,onState:(state,message,diagnostic)=>events.push({state,message,diagnostic}),onProgress:async n=>{if(n===1080&&!interrupted){interrupted=true;await page.locator('textarea').evaluate(e=>{e.value=e.value.slice(0,-9);e.blur();});}}});
  assert(interrupted);assert.equal(await page.locator('textarea').inputValue(),S.norm(text));assert(events.some(x=>x.state==='resume'));assert.equal(await page.evaluate(()=>clears),1);
  evidence.checks.push({name:'description-prefix-resumes-after-1080',clears:1,finalLength:S.norm(text).length,events});save();await page.close();
  // Transfer timeout fallback must never send the same file by another transport.
  const source=fs.readFileSync(path.resolve(__dirname,'../../tools/uploader/studio-browser.js'),'utf8');
  assert(!source.includes('Native file input timed out'));
  const f=await setup(browser,'ru',temp,{count:1,neverClose:true}),item=f.pack[0],durable=[];
  const failHost={...f.host,checkpoint:async it=>{it.studioPending=true;it.studioPhase='transferring';durable.push('transferring');},accepted:async(it,ix,id)=>durable.push('accepted:'+id),waitUpload:async()=>{throw Error('fixture transfer indicator interrupted');}};
  await assert.rejects(S.runSequential(f.page,[item],{titleDelay:1,descriptionDelay:1,newTabPerVideo:true,diagnosticsDir:out},failHost),/fixture transfer/);
  assert.equal(item.studioVideoId,'video000001');assert.equal(item.studioPhase,'editing');assert(item.studioPending);assert(durable.includes('accepted:video000001'));
  const unrelated=await f.ctx.newPage();await B.openStudio(unrelated,'',()=>{});
  assert.equal(await S.selectTaskPage(f.ctx,[item]),f.page,'Find the existing draft rather than the newly created blank Studio tab');
  let confirmed=0;const resumed=await S.runSequential(f.page,[item],{titleDelay:1,descriptionDelay:1,newTabPerVideo:true,diagnosticsDir:out},{...f.host,confirmed:async()=>confirmed++});
  assert.equal(resumed.length,1);assert.equal(confirmed,1);assert.equal(f.telemetry.filter(x=>x.type==='file').length,1);assert.equal(f.telemetry.filter(x=>x.type==='publish').length,1);assert.equal(f.saved.size,1);
  evidence.checks.push({name:'restart-resumes-existing-draft-no-second-transfer',id:item.studioVideoId,durable,telemetry:f.telemetry});save();await f.ctx.close();
  // A crash after primary Publish preserves phase and only confirms its warning.
  const p=await setup(browser,'en',temp,{count:1,checksWarning:true,neverClose:true,warningEnableDelay:1200,warningCloseDelay:700}),pending=p.pack[0];
  await assert.rejects(S.runSequential(p.page,[pending],{titleDelay:1,descriptionDelay:1,newTabPerVideo:true,diagnosticsDir:out},{...p.host,progress:(stage,text,extra)=>{p.host.progress(stage,text,extra);if(stage==='studio_publish_confirm')throw Error('fixture interrupted before warning click');}}),/fixture interrupted/);
  assert.equal(pending.studioPhase,'publishing');assert.equal(p.saved.size,0,'Warning was not approved; video is not Public');
  const recovered=await S.runSequential(p.page,[pending],{titleDelay:1,descriptionDelay:1,newTabPerVideo:true,diagnosticsDir:out},p.host);
  assert.equal(recovered.length,1);assert.equal(p.telemetry.filter(x=>x.type==='publish').length,1);assert.equal(p.telemetry.filter(x=>x.type==='confirmation').length,1);
  evidence.checks.push({name:'restart-after-primary-publish-confirms-warning-once',telemetry:p.telemetry});save();await p.ctx.close();
  // Complete logs survive >80 events, with a screenshot and useful partial JSON.
  const d=await setup(browser,'ru',temp,{count:1});
  const diagHost={...d.host,waitUpload:async(_,{progress})=>{for(let i=0;i<120;i++)progress('upload_transfer','fixture '+i);await d.page.evaluate(()=>console.error('token=top-secret cookie=secret-cookie'));throw Error('fixture diagnostic error');}};
  await assert.rejects(S.runSequential(d.page,d.pack,{titleDelay:1,descriptionDelay:1,newTabPerVideo:true,diagnosticsDir:out},diagHost),/fixture diagnostic/);
  const report=await S.diagnostics(d.page,Error('fixture diagnostic error'),out),json=JSON.parse(fs.readFileSync(report,'utf8'));
  assert(json.flow.stageLog.length>120);assert(fs.existsSync(json.flow.logFile));assert(fs.existsSync(json.screenshot));
  const log=fs.readFileSync(json.flow.logFile,'utf8');assert(log.includes('run_start')&&log.includes('run_error')&&log.includes('timing'));assert(!log.includes('top-secret')&&!log.includes('secret-cookie'));
  evidence.checks.push({name:'complete-jsonl-json-and-screenshot',stageCount:json.flow.stageLog.length,log:path.basename(json.flow.logFile),report:path.basename(report),screenshot:path.basename(json.screenshot)});save();await d.ctx.close();
  evidence.result='PASS_FIXTURES_ONLY';save();console.log('PASS v20 recovery: nested warning, exact description suffix repair, draft reuse, one Publish across restart, complete redacted logs + PNG.');
 }catch(error){evidence.failure=error.message;save();throw error;}
 finally{await browser.close();fs.rmSync(temp,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1;});
