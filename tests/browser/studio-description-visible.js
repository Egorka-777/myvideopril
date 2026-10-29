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
  await box.evaluate(e=>{window.sizes=[];e.addEventListener('input',()=>sizes.push(e.innerText.length));});
  const text='Visible typing\n\nWarning '+String.fromCodePoint(0x26a0,0xfe0f)+' link https://example.com\nEnd';
  await S.typeText(box,text,{name:'описания',delay:2});
  assert.equal(await S.read(box),text);
  assert((await page.evaluate(()=>sizes.length))>30,'Input must be visibly incremental');
  const controller=new AbortController();let inserts=0;
  const original=page.keyboard.insertText.bind(page.keyboard);
  page.keyboard.insertText=async text=>{await original(text);if(++inserts===10)controller.abort();};
  await assert.rejects(S.typeText(box,'abcdefghijklmnopqrstuvwxyz',{name:'описания',delay:1,signal:controller.signal}),/остановлен/);
  assert.equal(inserts,10);await page.close();
  console.log('PASS visible per-character input, Unicode, newlines, cancellation without retry');
  const description=('SEO text '+String.fromCodePoint(0x26a0,0xfe0f)+' risk notice\n\nhttps://example.com\n').repeat(35).trim();
  const f=await setup(browser,'ru',dir,{count:3,descriptions:[description,description,description]});
  try{
   for(const item of f.pack){item.studioVideoId='eqNOk_v0nIg';item.studioPending=true;item.studioPhase='editing';}
   const oldPage=f.page;
   const fresh=await S.selectTaskPage(f.ctx,f.pack);
   assert.notEqual(fresh,oldPage,'A run must use a fresh tab, not search saved draft IDs');
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
   assert(!f.stages.some(s=>/Повторяю ввод|Описание:.*символов|SHA256/.test(s.text)));
   console.log('PASS three fresh uploads with stale draft IDs: full description -> No/No -> Public -> confirmed, no recovery/readback; chars='+description.length);
  }finally{await f.ctx.close();}
 }finally{await browser.close();fs.rmSync(dir,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1;});
