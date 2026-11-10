'use strict';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload');
const {setup}=require('./studio-shorts-queue');
(async()=>{
 const browser=await chromium.launch({headless:true}),dir=fs.mkdtempSync(path.join(os.tmpdir(),'vb-description-visible-'));
 try{
  const page=await browser.newPage();
  await page.setContent('<div contenteditable style="white-space:pre-wrap;min-height:50px">old</div>');
  const box=page.locator('[contenteditable]');
  await box.evaluate(e=>{
   window.sizes=[];
   // Studio's social-suggestions editor can rebuild its selection while a
   // warning/link paragraph is still arriving. Reproduce the observed Range
   // failure and loss of that paragraph on incremental input.
   e.addEventListener('input',event=>{
    sizes.push(e.innerText.length);
    if(event.data==='⚠️'){
     e.textContent=e.innerText.replace('⚠️',' ');
     const range=document.createRange();range.setEnd(e.firstChild,e.firstChild.length+2);
    }
   });
  });
  const text='Visible typing\n\nWarning '+String.fromCodePoint(0x26a0,0xfe0f)+' link https://example.com\nEnd';
  await S.typeText(box,text,{name:'описания',delay:2});
  assert.equal(await S.read(box),text);
  assert.equal(await page.evaluate(()=>sizes.length),7,'Each nonempty line and explicit line break is a single input transaction');
  assert.equal(await box.evaluate(e=>e.innerText),text);
  const controller=new AbortController();let inserts=0;
  const original=page.keyboard.insertText.bind(page.keyboard);
  page.keyboard.insertText=async text=>{await original(text);inserts++;controller.abort();};
  await assert.rejects(S.typeText(box,'abcdefghijklmnopqrstuvwxyz',{name:'описания',delay:1,signal:controller.signal}),/остановлен/);
  assert.equal(inserts,1);
  controller.abort();
  await assert.rejects(S.typeText(box,'must not replace text',{name:'описания',signal:controller.signal}),/остановлен/);
  assert.equal(inserts,1);await page.close();
  console.log('PASS native atomic description input avoids incremental Range corruption; Unicode, newlines, cancellation without retry');
  for(const lang of ['ru','en']){
   const p=await browser.newPage();
   await p.setContent(`<html lang="${lang}"><input id="title"><div id="description" contenteditable style="white-space:pre-wrap;height:100px">Existing description</div><ytcp-social-suggestions-dropdown style="position:fixed;inset:0;background:white;z-index:10">#crypto</ytcp-social-suggestions-dropdown></html>`);
   await p.evaluate(()=>{
    document.querySelector('#title').focus();
    document.addEventListener('keydown',event=>{if(event.key==='Escape')setTimeout(()=>document.querySelector('ytcp-social-suggestions-dropdown')?.remove(),50);});
   });
   const target=p.locator('#description');
   await S.activateEditor(target,{timeoutMs:1500,name:'описания'});
   assert.equal(await p.locator('ytcp-social-suggestions-dropdown').count(),0);
   assert.equal(await S.read(target),'Existing description');
   assert((await S.editorState(target)).focused);
   await p.evaluate(()=>{
    window.escapeCount=0;document.addEventListener('keydown',event=>{if(event.key==='Escape')escapeCount++;});
    const modal=document.createElement('div');modal.id='real-modal';modal.style.cssText='position:fixed;inset:0;z-index:20;background:white';document.body.append(modal);
   });
   await assert.rejects(S.activateEditor(target,{timeoutMs:300,name:'описания'}),/перекрывает DIV#real-modal/);
   assert.equal(await p.evaluate(()=>escapeCount),0,'An unrelated modal must not be dismissed');
   assert.equal(await S.read(target),'Existing description');
   await p.close();console.log('PASS',lang,'title hashtag suggestions dismissed before description focus; existing text preserved; unrelated modal remains blocking');
  }
  const description=('SEO text '+String.fromCodePoint(0x26a0,0xfe0f)+' risk notice\n\nhttps://example.com\n').repeat(35).trim();
  for(const lang of ['ru','en']){
  const f=await setup(browser,lang,dir,{count:3,descriptions:[description,description,description]});
  try{
   for(const item of f.pack){item.studioVideoId='eqNOk_v0nIg';item.studioPending=true;item.studioPhase='editing';}
   const oldPage=f.page;
   const fresh=await S.selectTaskPage(f.ctx,f.pack);
   assert.notEqual(fresh,oldPage,'A run must use a fresh tab, not search saved draft IDs');
   // An uncertain prior transfer must remain blocked in production. Use new
   // jobs for the queue test; selecting a fresh tab is not recovery permission.
   for(const item of f.pack){delete item.studioVideoId;delete item.studioPending;delete item.studioPhase;}
   f.page=fresh;
   await require('../../tools/uploader/studio-browser').openStudio(f.page,'',()=>{});
   const result=await S.runSequential(f.page,f.pack,{titleDelay:1,descriptionDelay:1,saveTimeout:4000,newTabPerVideo:true},f.host);
   assert.equal(result.length,3);assert.equal(f.saved.size,3);
   for(const record of f.saved.values()){
    assert.equal(record.description,description);assert.equal(record.visibility,'public');assert(!record.paid&&!record.ai);
   }
   assert.equal(f.stages.filter(s=>s.stage==='item_confirmed').length,3);
   assert(!f.stages.some(s=>s.stage==='studio_verify'||s.stage==='studio_recovery'));
   assert.equal(f.telemetry.filter(e=>e.type==='file').length,3);
   assert(!f.stages.some(s=>/Повторяю ввод/.test(s.text)));
   assert.equal(f.stages.filter(s=>s.stage==='metadata_description'&&/Описание проверено целиком/.test(s.text)).length,3);
   console.log('PASS',lang,'three new uploads: exact full description -> No/No -> Public -> confirmed, no recovery/readback; chars='+description.length);
  }finally{await f.ctx.close();}
  }
 }finally{await browser.close();fs.rmSync(dir,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1;});
