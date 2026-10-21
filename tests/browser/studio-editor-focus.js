'use strict';
process.env.TZ='Asia/Krasnoyarsk';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path'),cp=require('child_process'),Module=require('module');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload');
const {html}=require('./youtube-studio-upload');
const baseline='cae5217ddec22d305543ab9bef30493e57defe32';
function oldWorker(){
 const file=path.resolve(__dirname,'../../tools/uploader/youtube-studio-upload.js'),m=new Module(file,module);m.filename=file;m.paths=Module._nodeModulePaths(path.dirname(file));
 m._compile(cp.execFileSync('git',['show',baseline+':tools/uploader/youtube-studio-upload.js'],{encoding:'utf8'}),file);return m.exports;
}
function shadowFixture(lang,state,editor=false){
 let body=html(lang,state,editor);
 const helpers=`function allIn(root,selector){const result=Array.from(root.querySelectorAll(selector));if(root.shadowRoot)result.push(...allIn(root.shadowRoot,selector));for(const e of root.querySelectorAll('*'))if(e.shadowRoot)result.push(...allIn(e.shadowRoot,selector));return result;}function deepQuery(root,selector){return allIn(root,selector)[0]||null;}`;
 body=body.replace('<script>','<script>'+helpers).replace('q=s=>document.querySelector(s)','q=s=>deepQuery(document,s)');
 body=body.replaceAll('e.target.closest','e.composedPath()[0].closest').replaceAll('state.title=e.target.innerText','state.title=e.composedPath()[0].innerText').replaceAll('state.description=e.target.innerText','state.description=e.composedPath()[0].innerText');
 body=body.replaceAll("document.querySelectorAll('ytcp-chip')","allIn(document,'ytcp-chip')");
 return body.replace('</body>',`<script>
 const content=editor?q('ytcp-video-metadata-editor'):q('#details');
 q('#paid-yes').closest('.question').id='paid-promotion';q('#ai-yes').closest('.question').id='altered-content';
 const host=document.createElement('ytcp-test-editor'),inner=document.createElement('ytcp-inner-editor');host.style.display=inner.style.display='block';
 host.attachShadow({mode:'open'}).append(inner);const root=inner.attachShadow({mode:'open'});root.append(...content.childNodes);const css=document.createElement('style');css.textContent='[contenteditable]{white-space:pre-wrap;min-height:40px}';root.prepend(css);content.append(host);
 window.metadataEscapes=0;window.submits=0;
 if(!editor){
  const backdrop=document.createElement('div');backdrop.className='dialog-scrim';backdrop.style.cssText='position:fixed;inset:0;z-index:-1';q('ytcp-uploads-dialog').append(backdrop);
  document.addEventListener('keydown',e=>{if(e.key==='Escape'&&!q('#details').hidden){metadataEscapes++;q('ytcp-uploads-dialog').remove();}});
  const submit=q('#done-button').onclick;q('#done-button').onclick=e=>{submits++;return submit(e);};
 }
 </script></body>`);
}
async function simpleShadow(page){
 await page.setContent('<ytcp-focus-test></ytcp-focus-test><script>(()=>{const h=document.querySelector("ytcp-focus-test");const root=h.attachShadow({mode:"open"});const inner=document.createElement("ytcp-focus-inner");root.append(inner);inner.attachShadow({mode:"open"}).innerHTML=\'<div id="textbox" contenteditable="true" role="textbox" style="min-height:40px">old</div>\';window.escapes=0;document.addEventListener("keydown",e=>{if(e.key==="Escape")escapes++;});})();</script>');
 return page.locator('#textbox');
}
(async()=>{
 const browser=await chromium.launch({headless:true}),dir=fs.mkdtempSync(path.join(os.tmpdir(),'vb-editor-focus-'));
 try{
  const old=oldWorker(),page=await browser.newPage();page.setDefaultTimeout(5000);
  let box=await simpleShadow(page);
  await assert.rejects(old.typeText(box,'New text',{delay:1}),/не активировало поле ввода/);
  assert(await box.evaluate(e=>e.getRootNode().activeElement===e),'The old check fails while the real shadow editor HAS focus');
  await S.typeText(box,'Новый заголовок with emoji 👍',{delay:1});
  assert.equal(await S.read(box),'Новый заголовок with emoji 👍');assert.equal(await page.evaluate(()=>escapes),0);
  console.log('REPRO v10: document.activeElement rejects a truly focused nested Shadow DOM editor; v11 types and verifies it without Escape.');
  // A visible backdrop is part of the modal layout, not proof that the field is blocked.
  await page.setContent('<ytcp-uploads-dialog><div class="dialog-scrim" style="position:fixed;inset:0;z-index:-1"></div><div id="textbox" contenteditable="true" style="min-height:40px">old</div></ytcp-uploads-dialog><script>window.escapes=0;addEventListener("keydown",e=>{if(e.key==="Escape"){escapes++;document.querySelector("ytcp-uploads-dialog").remove();}});</script>');
  await S.typeText(page.locator('#textbox'),'Keep the wizard open',{delay:1,scope:page.locator('ytcp-uploads-dialog')});assert.equal(await page.evaluate(()=>escapes),0);assert(await page.locator('ytcp-uploads-dialog').isVisible());
  // Actual tooltip overlay: dismiss once. No force clicks through a real modal.
  await page.setContent('<div role="tooltip" style="position:fixed;inset:0;z-index:5">Help</div><div id="textbox" contenteditable="true" style="height:50px">old</div><script>window.escapes=0;addEventListener("keydown",e=>{if(e.key==="Escape"){escapes++;document.querySelector("[role=tooltip]")?.remove();}});</script>');
  await S.typeText(page.locator('#textbox'),'After help',{delay:1});assert.equal(await page.evaluate(()=>escapes),1);assert.equal(await S.read(page.locator('#textbox')),'After help');
  await page.setContent('<div role="dialog" aria-modal="true" style="position:fixed;inset:0;z-index:5">Confirm an unrelated action</div><div id="textbox" contenteditable="true" style="height:50px">unchanged</div>');
  await assert.rejects(S.typeText(page.locator('#textbox'),'must not type',{delay:1,activationMs:700}),/перекрывает DIV/);assert.equal(await S.read(page.locator('#textbox')),'unchanged');
  console.log('PASS modal backdrop remains; one real tooltip Escape; unknown blocking modal is never force-clicked or typed through.');
  // The target can be replaced between chunks. Only the local draft field is retried.
  box=await simpleShadow(page);
  await box.evaluate(el=>{window.replaced=false;el.addEventListener('input',()=>{if(window.replaced||el.innerText.length<30)return;window.replaced=true;el.getRootNode().innerHTML='<div id="textbox" contenteditable="true" role="textbox" style="min-height:40px">default after rerender</div>';});});
  const long='Точный текст с пробелами, пунктуацией и символами. '.repeat(5).trim(),states=[];
  await S.typeText(box,long,{delay:1,onState:(event,text)=>states.push(event)});assert.equal(await S.read(box),long);assert(states.includes('retry'));assert.equal(await page.evaluate(()=>replaced),true);
  // Diagnostics use the same composed focus check and never record the field text.
  await box.focus();const snapshot=await S.editorState(box);assert(snapshot.focused&&snapshot.shadowDepth===2);assert(!JSON.stringify(snapshot).includes('Точный текст'));
  await page.close();console.log('PASS replaced editor is rewritten once with exact text; diagnostics report real composed focus without content.');
  for(const lang of ['ru','en']){
   const context=await browser.newContext({timezoneId:'Europe/Moscow'});context.setDefaultTimeout(5000);const p=await context.newPage();let saved=null,index=0;const stages=[];
   await context.route('https://studio.youtube.com/**',async route=>{
    const u=new URL(route.request().url());if(u.pathname==='/fixture/submit'){saved=JSON.parse(route.request().postData());return route.fulfill({body:'ok'});}
    return route.fulfill({contentType:'text/html; charset=utf-8',body:u.pathname.includes('/video/')?shadowFixture(lang,saved,true):'<main>Studio</main>'});
   });await p.goto('https://studio.youtube.com/');
   const pack=[0,1].map(n=>{const video=path.join(dir,lang+n+'.mp4');fs.writeFileSync(video,'fixture');const date='2030-01-02',time='1'+n+':35',at=new Date(date+'T'+time+':00');return {video,localJobId:'shadow-'+n,title:'Название '+lang+' '+n,description:'Описание из приложения '+lang+' '+n+'\n'+long,tags:['keep tag'],scheduleDate:date,scheduleTime:time,scheduledUnixSeconds:at.getTime()/1000,scheduledUtcOffsetMinutes:420};});
   const result=await S.runSequential(p,pack,{titleDelay:1,descriptionDelay:1,writeDescription:true,writeTags:true},{progress:(stage,text)=>stages.push({stage,text}),checkpoint:async()=>{},thumbnail:async()=>{},waitUpload:async()=>{},dismiss:async()=>{},openFile:async(page,file,checkpoint)=>{
    await checkpoint();await page.goto('https://studio.youtube.com/');await page.setContent(shadowFixture(lang,{id:'video'+String(++index).padStart(6,'0'),title:'Filename',description:'Old channel description',paid:true,ai:true,tags:['old tag'],date:'2029-12-31',time:'00:00',zone:'America/New_York'}));await page.locator('#video-file').setInputFiles(file);
   }});
   assert.equal(result.length,2);assert.equal(saved.title,pack[1].title);assert.equal(saved.description,pack[1].description);assert.deepEqual(saved.tags,['keep tag']);assert(!saved.paid&&!saved.ai);assert.equal(saved.time,pack[1].scheduleTime);assert.equal(saved.zone,'Asia/Krasnoyarsk');assert.equal(await p.evaluate(()=>submits),1);assert.equal(await p.evaluate(()=>metadataEscapes),0);assert.equal(stages.filter(s=>s.stage==='item_confirmed').length,2);
   await context.close();console.log('PASS',lang,'two sequential nested-shadow editors → typed title/description → tags and No/No → timezone → Schedule once → saved metadata readback.');
  }
  // Without stable ids, separate question components can also contain shadow controls.
  const questions=await browser.newPage();await questions.setContent('<ytcp-uploads-dialog><ytcp-question-a></ytcp-question-a><ytcp-question-b></ytcp-question-b></ytcp-uploads-dialog><script>for(const [tag,title,name] of [["ytcp-question-a","Paid promotion","p"],["ytcp-question-b","Altered content","a"]])document.querySelector(tag).attachShadow({mode:"open"}).innerHTML=`<h3>${title}</h3><label><input type="radio" name="${name}" value="yes" checked>Yes</label><label><input type="radio" name="${name}" value="no">No</label>`;</script>');
  await S.question(questions.locator('ytcp-uploads-dialog'),'paid',false);await S.question(questions.locator('ytcp-uploads-dialog'),'ai',false);assert(await questions.locator('ytcp-question-a input[value=no]').isChecked());assert(await questions.locator('ytcp-question-b input[value=no]').isChecked());await questions.close();
  console.log('PASS isolated Paid promotion/Altered content shadow components without stable ids.');
 }finally{await browser.close();fs.rmSync(dir,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
