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
  // A slow read must not replace the real deadline with the five-second probe limit.
  let probes=0;const slowStart=Date.now();
  assert.equal(await B.waitFor(()=>++probes===1?new Promise(()=>{}):true,6500,'actual deadline'),true);
  assert(Date.now()-slowStart>=5000);assert.equal(probes,2);
  let navigationReads=0;
  assert.equal(await B.waitFor(()=>{if(!navigationReads++)throw Error('Execution context was destroyed, most likely because of a navigation');return true;},1000,'actual deadline'),true);
  const deadlineStart=Date.now();
  await assert.rejects(B.waitFor(()=>new Promise(()=>{}),300,'actual deadline'),/actual deadline/);
  assert(Date.now()-deadlineStart>=290);
  // A saved @handle must resolve to its own channel, never the current Studio account.
  await context.route('https://www.youtube.com/**',route=>route.fulfill({contentType:'text/html',body:'<script>window.ytInitialData={metadata:{channelMetadataRenderer:{externalId:"UCabcdefghijklmnopqrstuv"}}}</script>'}));
  await context.route('https://studio.youtube.com/**',route=>route.fulfill({contentType:'text/html',body:'<button>Create</button>'}));
  let lookupTimeout=0;
  const newPage=context.newPage.bind(context);
  context.newPage=async()=>{const lookup=await newPage(),goto=lookup.goto.bind(lookup);lookup.goto=(url,opts)=>{lookupTimeout=opts.timeout;return goto(url,opts);};return lookup;};
  try{await B.openStudio(page,'https://www.youtube.com/@requested/videos',()=>{});}
  finally{context.newPage=newPage;}
  assert(lookupTimeout>0&&lookupTimeout<=60000,'Channel lookup navigation shares the 60-second deadline');
  assert.equal(page.url(),'https://studio.youtube.com/channel/UCabcdefghijklmnopqrstuv');
  assert.equal(context.pages().length,1,'Handle lookup tab must close');await context.unrouteAll();
  // Access denied is a concrete account/channel error, not a missing Create button.
  await context.route('https://studio.youtube.com/**',route=>route.fulfill({contentType:'text/html; charset=utf-8',body:'<h1>У вас нет доступа к этой странице.</h1>'}));
  const deniedStart=Date.now();
  await assert.rejects(B.openStudio(page,'https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv',()=>{}),/нет доступа к каналу UCabcdefghijklmnopqrstuv/);
  assert(Date.now()-deniedStart<5000);await context.unrouteAll();
  await context.route('https://studio.youtube.com/**',route=>route.fulfill({contentType:'text/html',body:'<button>Create</button>'}));
  const originalGoto=page.goto.bind(page);
  page.goto=(_,options)=>originalGoto('https://studio.youtube.com/channel/UCwrongchannel00000000000',options);
  try{await assert.rejects(B.openStudio(page,'https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv',()=>{}),/ожидался UCabcdefghijklmnopqrstuv, открыт UCwrongchannel00000000000/);}
  finally{page.goto=originalGoto;}
  await context.unrouteAll();
  // A delayed file input in an already open picker must not click Create behind its backdrop.
  await context.route('https://studio.youtube.com/**',route=>route.fulfill({contentType:'text/html',body:'<button id="create-icon" onclick="window.createClicks++">Create</button><ytcp-uploads-dialog style="display:block"><div role="dialog">Select files</div></ytcp-uploads-dialog><script>window.createClicks=0;setTimeout(()=>document.querySelector("ytcp-uploads-dialog").insertAdjacentHTML("beforeend",\'<input type="file" accept="video/mp4">\'),500)</script>'}));
  await page.goto('https://studio.youtube.com/channel/UCabcdefghijklmnopqrstuv');
  await B.openFile(page,path.join(temp,'selected-ru-false.mp4'),async()=>{},()=>{},{channelUrl:'https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv'});
  assert.equal(await page.evaluate(()=>window.createClicks),0);
  assert.equal(await page.locator('input[type=file]').evaluate(e=>e.files.length),1);
  await context.unrouteAll();
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
