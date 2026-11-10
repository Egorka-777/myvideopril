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
  await assert.rejects(B.openFile(page,path.join(temp,'selected-ru-false.mp4'),async()=>{touched=true;},()=>{},{readyMs:350}),/Файл не передавался/);
  assert(!touched,'A failure before file selection must not lock the item as submitted');
  await assert.rejects(B.bounded(()=>new Promise(()=>{}),20,'bounded diagnostic'),/bounded diagnostic/);
  // Completed transfer is not completed checking; only the latter permits Publish.
  for(const lang of ['ru','en']){
   await page.setContent('<ytcp-uploads-dialog><ytcp-video-upload-progress></ytcp-video-upload-progress></ytcp-uploads-dialog>');
   const status=page.locator('ytcp-video-upload-progress');
   await status.evaluate((e,text)=>e.textContent=text,lang==='ru'?'Проверка: 5 % ... Осталось 5 минут':'Checks running: 5%');
   let completed=false;const waiting=S.waitChecks(page,{timeout:4000}).then(()=>{completed=true;});
   await page.waitForTimeout(650);assert(!completed,'Running checks must not permit publication');
   await status.evaluate((e,text)=>e.textContent=text,lang==='ru'?'Проверка завершена. На контент заявлены права.':'Checks complete. Copyright-protected content found.');
   await waiting;assert(completed);
   await status.evaluate(e=>e.textContent='Uploaded: 100%');
   await assert.rejects(S.waitChecks(page,{timeout:250,soft:false}),/не подтвердил окончание проверки/);
  }
  const {setup}=require('./studio-shorts-queue');
  const fixture=await setup(browser,'ru',temp,{count:1});
  let skipped=false;
  const result=await S.runSequential(fixture.page,fixture.pack,{writeDescription:false,titleDelay:1},{...fixture.host,waitUpload:async p=>{
   await S.waitUpload(p);
   await p.locator('ytcp-video-upload-progress').evaluate(e=>{e.textContent='Проверка: 5 %';});
  },progress:(stage,text,extra)=>{if(stage==='studio_checks'&&/не ожидаю/.test(text))skipped=true;fixture.host.progress(stage,text,extra);}});
  assert.equal(result.length,1);assert(skipped,'Background checks must not gate publication');
  assert.equal((await fixture.page.evaluate(()=>stats)).publishes,1);
  assert.equal((await fixture.page.evaluate(()=>stats)).escapes,0);
  assert.equal(fixture.saved.get('video000001').description,'Old default','Studio description must remain untouched');
  assert(!fixture.stages.some(s=>s.stage==='metadata_description'));
  await fixture.ctx.close();
  // A normal modal backdrop must never trigger Escape (which closes Studio).
  await page.setContent(`<style>ytcp-uploads-dialog{display:block;position:fixed;inset:40px;background:white;z-index:2}tp-yt-iron-overlay-backdrop{display:block;position:fixed;inset:0;background:#0008;z-index:1}</style><tp-yt-iron-overlay-backdrop opened class="opened"></tp-yt-iron-overlay-backdrop><ytcp-uploads-dialog><label><input type="radio" name="PUBLIC">Public</label><button id="done-button">Publish</button></ytcp-uploads-dialog><script>window.escapes=0;window.clicks=0;document.addEventListener('keydown',e=>{if(e.key==='Escape'){escapes++;document.querySelector('ytcp-uploads-dialog')?.remove();}});document.querySelector('input').onclick=()=>clicks++;</script>`);
  await S.publishPublic(page,page.locator('ytcp-uploads-dialog'));
  assert.equal(await page.evaluate(()=>escapes),0);assert.equal(await page.evaluate(()=>clicks),1);
  // Readiness probes survive a temporary blocker and replacement of the radio.
  await page.evaluate(()=>{document.querySelector('input').checked=false;const cover=document.createElement('div');cover.id='cover';cover.style.cssText='position:fixed;inset:0;z-index:3';document.body.append(cover);setTimeout(()=>{const old=document.querySelector('input');const fresh=old.cloneNode();old.replaceWith(fresh);fresh.onclick=()=>clicks++;cover.remove();},400);});
  await S.publishPublic(page,page.locator('ytcp-uploads-dialog'));
  assert.equal(await page.evaluate(()=>clicks),2);assert.equal(await page.evaluate(()=>escapes),0);
  await page.evaluate(()=>document.querySelector('ytcp-uploads-dialog').remove());
  await assert.rejects(S.publishPublic(page,page.locator('ytcp-uploads-dialog')),/закрыл окно доступа/);
  await context.close();
  console.log('PASS ready controls with loading resources; background checks do not block publication; modal backdrop, replaced radio, closed wizard; explicit diagnostic checks remain strict');
 }finally{await browser.close();fs.rmSync(temp,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
