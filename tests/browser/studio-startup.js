'use strict';
// Real Chromium + local fixture pages. No Google account or publication is used.
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const B=require('../../tools/uploader/studio-browser');
const S=require('../../tools/uploader/youtube-studio-upload');
(async()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'vb-studio-start-'));
 const browser=await chromium.launch({headless:true});
 try{
  const context=await browser.newContext(),page=await context.newPage();
  context.setDefaultTimeout(3000);
  for(const lang of ['ru','en']){
   for(const shadow of [false,true]){
    let checkpoints=0;const requests=[];
    const file=path.join(temp,`selected-${lang}-${shadow}.mp4`);fs.writeFileSync(file,'chosen-video');
    await context.route('https://studio.youtube.com/**',async route=>{
     requests.push(route.request().url());
     return route.fulfill({contentType:'text/html; charset=utf-8',body:`<button id="create-icon">${lang==='ru'?'Создать':'Create'}</button><div id="menu" hidden><button role="menuitem">${lang==='ru'?'Загрузить видео':'Upload videos'}</button></div><ytcp-uploads-dialog hidden><input type="file" accept="video/mp4" id="stale"></ytcp-uploads-dialog><input type="file" accept="image/png" id="thumbnail"><ytcp-uploads-file-picker id="picker" hidden></ytcp-uploads-file-picker><script>
      const create=document.querySelector('#create-icon'),menu=document.querySelector('#menu'),picker=document.querySelector('#picker');
      create.onclick=()=>menu.hidden=false;menu.querySelector('button').onclick=()=>{picker.hidden=false;const root=${shadow} ? picker.attachShadow({mode:'open'}) : picker;root.innerHTML='<input type="file" accept="video/mp4" id="chosen">';root.querySelector('input').onchange=e=>{window.chosen=Array.from(e.target.files).map(f=>f.name);};};
     </script>`});
    });
    await B.openStudio(page,'https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv',()=>{});
    const started=Date.now();await B.openFile(page,file,async()=>{checkpoints++;},()=>{},{forceCdp:shadow});
    assert(Date.now()-started<10000,'Picker must not cycle through reloads');
    assert.equal(checkpoints,1);assert.deepEqual(await page.evaluate(()=>window.chosen),[path.basename(file)]);
    assert.equal(await page.locator('#thumbnail').evaluate(e=>e.files.length),0,'Never select the thumbnail input');
    assert.equal(await page.locator('#stale').evaluate(e=>e.files.length),0,'Never select a hidden old picker');
    assert.equal(requests.length,1,'Studio navigation happens once, with no IP-service pages');
    assert.equal(await page.locator('[data-vb-file]').count(),0,'CDP marker must be cleaned');
    await context.unrouteAll();
    console.log('PASS native Studio picker',lang,shadow?'shadow CDP':'Playwright');
   }
  }
  // A loading resource must not block Studio controls that are already interactive.
  let held=null;
  await context.route('https://studio.youtube.com/**',async route=>{
   if(route.request().url().endsWith('/never-load.js')){held=route;return;}
   await route.fulfill({contentType:'text/html',body:'<button id="create-icon">Create</button><script src="/never-load.js"></script>'});
  });
  const start=Date.now();await B.openStudio(page,'',()=>{});assert(Date.now()-start<5000);
  await held?.abort().catch(()=>{});await context.unrouteAll();
  // Current Studio uses a different editor host, with an old hidden dialog retained.
  await page.setContent('<ytcp-uploads-dialog hidden></ytcp-uploads-dialog><ytcp-video-upload-dialog><div id="title-textarea"><div contenteditable="true" id="textbox">filename</div></div></ytcp-video-upload-dialog>');
  const scope=await S.uploadScope(page);await S.typeText(await S.field(scope,'title'),'Новый заголовок',{delay:1});assert.equal(await S.read(await S.field(scope,'title')),'Новый заголовок');
  let touched=false;await page.setContent('<main>No picker</main>');
  await assert.rejects(B.openFile(page,path.join(temp,'selected-ru-false.mp4'),async()=>{touched=true;},()=>{}),/Файл не передавался/);
  assert(!touched,'A failure before file selection must not lock the item as submitted');
  await assert.rejects(B.bounded(()=>new Promise(()=>{}),20,'bounded diagnostic'),/bounded diagnostic/);
  await context.close();
  console.log('PASS no navigation wait on loading resources, active dialog, no false pending, bounded diagnostics');
 }finally{await browser.close();fs.rmSync(temp,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
