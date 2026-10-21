'use strict';
// Real Chromium + local Studio-shaped fixtures; no accounts or real publications.
process.env.TZ='Asia/Krasnoyarsk';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload');
const T=require('../../tools/uploader/youtube-thumbnail');
const {html}=require('./youtube-studio-upload');
const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAGAAAABgCAIAAABt+uBvAAAA2ElEQVR4nO3QMRHAIADAQEBDZ/yLQkwV9DLS4V9BLvM8e/Bt3Q74O4OCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUXmVXAc6VBGjVAAAAAElFTkSuQmCC','base64');
function fixture(lang,state,editor){
 let body=html(lang,state,editor);
 if(!editor){
  body=body.replaceAll('ytcp-visibility-scheduler','ytcp-video-visibility-select')
   .replace('<div id="schedule-radio-button">','<div id="schedule-radio-button" role="radio" aria-checked="false">')
   .replaceAll('schedule-radio-button','schedule-card')
   .replace('<button id="next-button">','<ytcp-button id="next-button"><button>')
   .replace('</button><button id="done-button">','</button></ytcp-button><footer><ytcp-button id="done-button"><button>')
   .replace('</button></ytcp-uploads-dialog>','</button></ytcp-button></footer></ytcp-uploads-dialog>')
   .replace("q('#done-button').textContent=", "q('#done-button button').textContent=");
 }
 const image=`<div id="thumbnail-editor"><img width="96" height="96" src="https://i.ytimg.com/${state.cover?'custom':'old'}.png"><input type="file" accept="image/png" hidden></div>`;
 body=body.replace(editor?'</ytcp-video-metadata-editor>':'</div><ytcp-video-visibility-select',editor?image+'</ytcp-video-metadata-editor>':image+'</div><ytcp-video-visibility-select');
 return body.replace('</body>',`<script>
 window.coverUploaded=${!!state.cover};window.nextClicks=0;window.earlyClicks=0;
 const coverInput=document.querySelector('#thumbnail-editor input');if(coverInput)coverInput.onchange=()=>{window.coverUploaded=true;setTimeout(()=>document.querySelector('#thumbnail-editor img').src=URL.createObjectURL(coverInput.files[0]),600);};
 if(!${editor}){
  const next=document.querySelector('#next-button'),go=next.onclick;let moving=false;
  next.onclick=async()=>{window.nextClicks++;if(moving){window.earlyClicks++;return;}moving=true;next.setAttribute('aria-disabled','true');setTimeout(()=>{go();moving=false;next.setAttribute('aria-disabled','false');},600);};
  const card=document.querySelector('#schedule-card'),expand=card.onclick;
  card.onclick=()=>{card.setAttribute('aria-checked','true');setTimeout(expand,300);};
  const oldFetch=window.fetch;window.fetch=(url,opts)=>{if(String(url).endsWith('/fixture/submit')){const record=JSON.parse(opts.body);record.cover=window.coverUploaded;record.nextClicks=window.nextClicks;record.earlyClicks=window.earlyClicks;opts.body=JSON.stringify(record);}return oldFetch(url,opts);};
 }
 </script></body>`);
}
(async()=>{
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'studio-state-')),browser=await chromium.launch({headless:true});
 try{
  // Visibility replaces Next after an async render. No old scheduler host/id.
  for(const lang of ['ru','en']){
   const page=await browser.newPage();await page.setContent(`<ytcp-uploads-dialog><div id="details">Details</div><ytcp-button id="next-button"><button>${lang==='ru'?'Далее':'Next'}</button></ytcp-button></ytcp-uploads-dialog><script>
    window.clicks=0;document.querySelector('button').onclick=()=>{clicks++;document.querySelector('#details').remove();document.querySelector('#next-button').remove();setTimeout(()=>document.querySelector('ytcp-uploads-dialog').innerHTML='<section><label><input type="radio" name="visibility">${lang==='ru'?'Запланировать публикацию':'Schedule'}</label></section><footer><ytcp-button id="done-button"><button>${lang==='ru'?'Запланировать':'Schedule'}</button></ytcp-button></footer>',600);};</script>`);
   await S.visibility(page,page.locator('ytcp-uploads-dialog'),{readyMs:2000,transitionMs:2000});assert.equal(await page.evaluate(()=>clicks),1);assert.equal((await S.wizardState(page.locator('ytcp-uploads-dialog'))).stage,'visibility');await page.close();
  }
  const stuck=await browser.newPage();await stuck.setContent('<ytcp-uploads-dialog><div id="details">Required question unanswered</div><button id="next-button" onclick="window.clicks=(window.clicks||0)+1">Next</button></ytcp-uploads-dialog>');
  await assert.rejects(S.visibility(stuck,stuck.locator('ytcp-uploads-dialog'),{readyMs:1000,transitionMs:350}),/не подтвердил переход/);assert.equal(await stuck.evaluate(()=>clicks),1);await stuck.close();
  console.log('PASS RU/EN delayed Visibility without Next, nested buttons, stalled step gets one click');
  for(const lang of ['ru','en']){
   const page=await browser.newPage();await page.setContent(`<ytcp-uploads-dialog><label><input type="radio" name="PUBLIC">${lang==='ru'?'Открытый доступ':'Public'}</label><label><input type="radio" name="SCHEDULE">${lang==='ru'?'Запланировать публикацию':'Schedule'}</label><footer><ytcp-button id="done-button"><button>${lang==='ru'?'Опубликовать':'Publish'}</button></ytcp-button></footer></ytcp-uploads-dialog>`);
   const scope=page.locator('ytcp-uploads-dialog');await S.visibility(page,scope,{readyMs:1000});const button=await S.publishPublic(page,scope);assert(await page.locator('[name=PUBLIC]').isChecked());assert.equal(await button.innerText(),lang==='ru'?'Опубликовать':'Publish');await page.close();
  }
  console.log('PASS RU/EN immediate Public path with nested final button');
  for(const lang of ['ru','en']){
   const page=await browser.newPage();
   const title=lang==='ru'?'Видео опубликовано':'Video published';
   await page.setContent(`<ytcp-uploads-dialog><h2>${title}</h2><yt-share-target-renderer></yt-share-target-renderer><button aria-label="${lang==='ru'?'Копировать ссылку на видео':'Copy video link'}">Copy A</button><ytcp-button id="done-button" aria-disabled="true"><button>${lang==='ru'?'Опубликовать':'Publish'}</button></ytcp-button><button id="close-button">${lang==='ru'?'Закрыть':'Close'}</button></ytcp-uploads-dialog>`);
   assert(await S.publishSuccessVisible(page));
   await page.close();
  }
  console.log('PASS RU/EN Shorts post-publish screen detected inside upload dialog');
  for(const lang of ['ru','en']){
   const ctx=await browser.newContext({timezoneId:'America/New_York'});ctx.setDefaultTimeout(4000);
   const page=await ctx.newPage(),saved=new Map(),events=[];let sequence=0,documentLoads=0;
   await ctx.route('**/*',async route=>{
    const u=new URL(route.request().url());
    if(u.hostname==='i.ytimg.com')return route.fulfill({contentType:'image/png',body:u.pathname.includes('custom')?png:await wrongPng(page)});
    if(u.pathname==='/fixture/submit'){const item=JSON.parse(route.request().postData());saved.set(item.id,item);return route.fulfill({body:'ok'});}
    if(route.request().isNavigationRequest()&&route.request().frame()===page.mainFrame())documentLoads++;
    const id=u.pathname.match(/\/video\/(\w+)\/edit/)?.[1];
    return route.fulfill({contentType:'text/html; charset=utf-8',body:id?fixture(lang,saved.get(id),true):'<main>Studio</main>'});
   });
   await page.goto('https://studio.youtube.com/');
   const cover=path.join(dir,'cover.png');fs.writeFileSync(cover,png);
   const pack=Array.from({length:2},(_,i)=>{const video=path.join(dir,`${lang}-${i}.mp4`);fs.writeFileSync(video,'fixture');const date='2030-01-02',time=`1${i}:35`,at=new Date(date+'T'+time+':00');return {video,title:'Title '+i,description:'Description '+i,tags:[],thumbnail:cover,scheduleDate:date,scheduleTime:time,scheduledUnixSeconds:at.getTime()/1000,scheduledUtcOffsetMinutes:-at.getTimezoneOffset()};});
   const result=await S.runSequential(page,pack,{titleDelay:1,descriptionDelay:1,writeTags:true},{
    progress:(stage,text)=>events.push({stage,text}),checkpoint:async()=>assert.equal(saved.size,sequence),
    openFile:async(p,file,checkpoint)=>{await checkpoint();sequence++;await p.goto('https://studio.youtube.com/');await p.setContent(fixture(lang,{id:'video'+String(sequence).padStart(6,'0'),title:'Original',description:'Default',paid:true,ai:true,tags:[],date:'2029-12-31',time:'00:00',zone:'America/New_York'},false));await p.locator('#video-file').setInputFiles(file);},
    thumbnail:(p,file,opts)=>T.uploadThumbnail(p,file,{...opts,timeout:4000}),waitUpload:async()=>{},dismiss:async()=>{}
   });
   assert.equal(result.length,2);assert.equal(documentLoads,pack.length+1,'Only fixture startup navigations, no reload while selecting/verifying thumbnail');
   for(const item of saved.values()){assert(item.scheduled&&item.cover);assert.equal(item.nextClicks,3);assert.equal(item.earlyClicks,0);assert.equal(item.zone,'Asia/Krasnoyarsk');}
   const report=await S.diagnostics(page,new Error('Synthetic fixture report'),dir);const diagnostic=JSON.parse(fs.readFileSync(report));assert.equal(diagnostic.flow.lastStage,'item_confirmed');assert.equal(diagnostic.build,S.BUILD);assert(!fs.readFileSync(report,'utf8').includes('Description 1'));
   console.log('PASS',lang,'two sequential uploads + real thumbnail input/verification + async steps + separate Schedule card/submit + saved PC schedule + diagnostics');
   await ctx.close();
  }
  // Polling thumbnail candidates must never scroll to offscreen suggestions.
  const page=await browser.newPage({viewport:{width:800,height:600}});await page.setContent('<div id="thumbnail-editor"><img width="96" height="96"><input type="file" accept="image/png" hidden><div style="height:1600px"></div><img width="96" height="96"></div>');
  const wrong=await wrongPng(page);await page.locator('img').evaluateAll((nodes,src)=>nodes.forEach(e=>e.src=src),'data:image/png;base64,'+wrong.toString('base64'));
  await page.evaluate(()=>{window.scrolled=0;onscroll=()=>scrolled++;document.querySelector('input').onchange=e=>setTimeout(()=>document.querySelector('img').src=URL.createObjectURL(e.target.files[0]),900);});
  const cover=path.join(dir,'cover.png');fs.writeFileSync(cover,png);await T.uploadThumbnail(page,cover,{timeout:4000});assert.equal(await page.evaluate(()=>scrollY),0);assert.equal(await page.evaluate(()=>scrolled),0);await page.close();
  console.log('PASS thumbnail polling does not scroll to offscreen candidates');
 }finally{await browser.close();fs.rmSync(dir,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
async function wrongPng(page){return Buffer.from(await page.evaluate(()=>{const c=document.createElement('canvas');c.width=c.height=96;c.getContext('2d').fillRect(0,0,96,96);return c.toDataURL().split(',')[1];}),'base64');}
