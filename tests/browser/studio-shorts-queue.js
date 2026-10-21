'use strict';
process.env.TZ='Asia/Krasnoyarsk';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path'),cp=require('child_process'),Module=require('module');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload'),B=require('../../tools/uploader/studio-browser');
const {html}=require('./youtube-studio-upload');
const baseline='4a1a2299b15dbc210ef060e6ff229b9cb6bc169f';
function oldEditor(){
 const file=path.resolve(__dirname,'../../tools/uploader/studio-editor-controls.js'),m=new Module(file,module);m.filename=file;m.paths=Module._nodeModulePaths(path.dirname(file));
 m._compile(cp.execFileSync('git',['show',baseline+':tools/uploader/studio-editor-controls.js'],{encoding:'utf8'}),file);
 return m.exports({until:S.until,norm:S.norm,read:S.read,typingBudget:S.typingBudget});
}
function app(lang,options={}){
 const ru=lang==='ru';return `<html lang="${lang}"><style>[contenteditable]{white-space:pre-wrap;min-height:45px}ytcp-uploads-dialog{display:block}#create-icon{position:fixed;right:5px;top:5px}#dialog{position:relative;max-height:85vh;overflow:auto;width:700px}button{min-height:25px}</style><body>
 <button id="create-icon">${ru?'Создать':'Create'}</button><div id="menu" hidden><button role="menuitem">${ru?'Загрузить видео':'Upload videos'}</button></div><input type="file" id="image-trap" accept="image/png">
 <script>
 (()=>{
 const ru=${ru},options=${JSON.stringify(options)},q=s=>document.querySelector(s),label=(a,b)=>ru?a:b;
 window.stats={creates:0,selected:[],publishes:0,closes:0,headerCloses:0,escapes:0};
 document.addEventListener('keydown',e=>{if(e.key==='Escape')stats.escapes++;});
 q('#create-icon').onclick=()=>{if(document.querySelector('ytcp-uploads-dialog'))throw Error('Create before close');stats.creates++;q('#menu').hidden=false;};
 q('#menu button').onclick=()=>{
  q('#menu').hidden=true;const dialog=document.createElement('ytcp-uploads-dialog');dialog.innerHTML='<div id="dialog" role="dialog"><input id="video-file" type="file" accept="video/mp4"></div>';document.body.append(dialog);
  dialog.querySelector('input').onchange=async e=>{
   stats.selected.push(e.target.files[0].name);const index=stats.selected.length,id='video'+String(index).padStart(6,'0');let step=0,draft={title:'Filename',description:'Old default'};
   dialog.innerHTML='<div id="dialog" role="dialog"><button id="close-icon-button">X</button><div id="stepper"><button role="tab" aria-selected="true">Details</button><button role="tab" aria-selected="false">Elements</button><button role="tab" aria-selected="false">Checks</button><button role="tab" aria-selected="false">Visibility</button></div><div id="details"><div id="title-textarea"><div id="textbox" role="textbox" contenteditable="true">Filename</div></div><div id="description-textarea"><div id="textbox" role="textbox" contenteditable="true">Old default</div></div><label><input type="radio" name="VIDEO_MADE_FOR_KIDS_NOT_MFK">'+label('Нет, это видео не для детей',"No, it is not made for kids")+'</label><button id="toggle-button">'+label('Развернуть','Show more')+'</button><div id="advanced" hidden><section id="paid-promotion"><h3>'+label('Прямая реклама','Paid promotion')+'</h3><label><input type="radio" name="paid" value="yes" checked>'+label('Да','Yes')+'</label><label><input type="radio" name="paid" value="no">'+label('Нет','No')+'</label></section><section id="altered-content"><h3>'+label('Использование ИИ','Altered content')+'</h3><label><input type="radio" name="ai" value="yes" checked>'+label('Да','Yes')+'</label><label><input type="radio" name="ai" value="no">'+label('Нет','No')+'</label></section></div><a href="https://youtu.be/'+id+'">Video</a><button aria-label="'+label('Копировать ссылку','Copy link')+'">Copy before publish</button><span>'+label('Включить продвижение','Promote your video')+'</span></div><ytcp-video-upload-progress>Uploading: 10%</ytcp-video-upload-progress><ytcp-visibility-scheduler hidden><h3>'+label('Сохранить или опубликовать','Save or publish')+'</h3><label><input type="radio" name="PUBLIC">'+label('Открытый доступ','Public')+'</label><label><input type="radio" name="SCHEDULE">'+label('Запланировать публикацию','Schedule')+'</label></ytcp-visibility-scheduler><button id="next-button">'+label('Далее','Next')+'</button><ytcp-button id="done-button" aria-disabled="true"><button disabled>'+label('Опубликовать','Publish')+'</button></ytcp-button></div>';
   const root=dialog.querySelector('#dialog'),title=dialog.querySelector('#title-textarea #textbox'),desc=dialog.querySelector('#description-textarea #textbox');
   const header=dialog.querySelector('#close-icon-button');header.onclick=()=>{stats.headerCloses++;dialog.remove();};
   title.oninput=()=>draft.title=title.innerText;desc.oninput=()=>draft.description=desc.innerText;
   dialog.querySelector('#toggle-button').onclick=()=>{dialog.querySelector('#advanced').hidden=false;dialog.querySelector('#toggle-button').textContent=label('Свернуть','Show less');};
   setTimeout(()=>{if(dialog.isConnected)dialog.querySelector('ytcp-video-upload-progress').textContent='Uploaded: 100%';},options.transferDelay||300);
   dialog.querySelector('#next-button').onclick=()=>{step++;dialog.querySelectorAll('#stepper [role=tab]').forEach((tab,n)=>tab.setAttribute('aria-selected',String(n===step)));if(step===3){dialog.querySelector('#details').hidden=true;dialog.querySelector('#next-button').hidden=true;dialog.querySelector('ytcp-visibility-scheduler').hidden=false;setTimeout(()=>{dialog.querySelector('#done-button').setAttribute('aria-disabled','false');dialog.querySelector('#done-button button').disabled=false;},150);}};
   dialog.querySelector('#done-button button').onclick=async()=>{
    stats.publishes++;dialog.querySelector('#done-button button').disabled=true;
    const record={id,title:draft.title,description:draft.description,tags:[],paid:dialog.querySelector('[name=paid][value=yes]').checked,ai:dialog.querySelector('[name=ai][value=yes]').checked,date:'2030-01-02',time:'14:45',zone:'Asia/Krasnoyarsk',scheduled:false,visibility:options.privateSave?'private':'public'};
    await fetch('/fixture/save',{method:'POST',body:JSON.stringify(record)});
    if(options.badConfirmation)return; // Persisted check must not trust a missing card.
    root.innerHTML='<h2>'+label('Видео опубликовано','Video published')+'</h2><a href="https://youtu.be/'+id+'">Video</a><button aria-label="'+label('Копировать ссылку','Copy link')+'">Copy A</button><button aria-label="'+label('Копировать ссылку','Copy link')+'">Copy B</button><button id="close-icon-button">X</button><ytcp-button id="close-button" aria-disabled="true"><button disabled>'+label('Закрыть','Close')+'</button></ytcp-button>';
    const close=dialog.querySelector('#close-button');dialog.querySelector('#close-icon-button').onclick=()=>{stats.headerCloses++;dialog.remove();};
    close.querySelector('button').onclick=()=>{stats.closes++;close.setAttribute('aria-disabled','true');close.querySelector('button').disabled=true;setTimeout(()=>{dialog.remove();q('#create-icon').disabled=true;setTimeout(()=>q('#create-icon').disabled=false,options.createDelay||200);},options.removeDelay||250);};
    setTimeout(()=>{if(close.isConnected&&!options.neverClose){close.setAttribute('aria-disabled','false');close.querySelector('button').disabled=false;}},options.closeDelay||450);
   };
  };
 };
 })();
 </script></body></html>`;
}
function savedEditor(lang,record){
 let body=html(lang,record,true).replace('>Private</button>','>'+(record.visibility==='public'?(lang==='ru'?'Открытый доступ':'Public'):'Private')+'</button>');
 // Also support generic visibility chips and explicit checked Public in a dialog.
 return body;
}
async function setup(browser,lang,dir,options={}){
 const ctx=await browser.newContext({timezoneId:'Europe/Moscow'});ctx.setDefaultTimeout(4000);const page=await ctx.newPage(),saved=new Map(),reads=new Map(),stages=[],navigations=[];
 await ctx.route('https://studio.youtube.com/**',async route=>{
  const u=new URL(route.request().url());
  if(u.pathname==='/fixture/save'){const record=JSON.parse(route.request().postData());saved.set(record.id,record);return route.fulfill({body:'ok'});}
  const id=u.pathname.match(/\/video\/(\w+)\/edit/)?.[1];
  if(route.request().isNavigationRequest()&&route.request().frame()===page.mainFrame())navigations.push(u.pathname);
  let record=saved.get(id);if(id){reads.set(id,(reads.get(id)||0)+1);if(options.staleFirstRead&&reads.get(id)===1)record={...record,visibility:'private'};}
  return route.fulfill({contentType:'text/html; charset=utf-8',body:id?savedEditor(lang,record):app(lang,options)});
 });
 await B.openStudio(page,'',()=>{});
 const pack=Array.from({length:2},(_,n)=>{const video=path.join(dir,lang+'-'+n+'.mp4');fs.writeFileSync(video,'fixture');return {video,title:'Title '+lang+' '+n,description:('Описание '+n+' — текст, ссылка https://example.com и символы 👍.\n\n').repeat(6).trim(),contentKind:'shorts',publishMode:'immediate',thumbnail:'must never open.png',localJobId:'job-'+n};});
 pack[1].video=pack[0].video; // Deliberately reuse one test file with two fresh upload identities.
 const host={progress:(stage,text,extra={})=>stages.push({stage,text,...extra}),checkpoint:async()=>{},openFile:(p,file,checkpoint)=>B.openFile(p,file,checkpoint,()=>{},{readyMs:2500}),waitUpload:S.waitUpload,thumbnail:async()=>{throw Error('Shorts thumbnail must be skipped');},dismiss:async()=>{throw Error('Legacy unconditional dismiss must not run');}};
 return {ctx,page,saved,pack,host,stages,navigations};
}
(async()=>{
 const browser=await chromium.launch({headless:true}),dir=fs.mkdtempSync(path.join(os.tmpdir(),'vb-shorts-queue-'));
 try{
  const p=await browser.newPage();p.setDefaultTimeout(2000);await p.setContent('<textarea maxlength="4"></textarea>');const box=p.locator('textarea');
  await oldEditor().typeText(box,'ABCDEFGHI',{delay:1});assert.equal(await box.inputValue(),'ABCD');
  console.log('REPRO v18: a four-character prefix is accepted as a complete nine-character field.');
  await assert.rejects(S.typeText(box,'ABCDEFGHI',{delay:1,verifyMs:180}),/точный текст/);assert.equal(await box.inputValue(),'ABCD');
  await p.setContent('<div contenteditable="true" style="white-space:pre-wrap;min-height:50px"></div>');
  const rich='Первая строка 👍\n\nhttps://example.com/?a=1\nПоследняя строка';await S.typeText(p.locator('[contenteditable]'),rich,{delay:1});assert.equal(await S.read(p.locator('[contenteditable]')),rich);
  // A sharing panel and a disabled Publish button during processing are not success.
  await p.setContent('<ytcp-uploads-dialog><h2>Processing</h2><button>Copy link</button><button>Copy link</button><yt-share-target-renderer>Share</yt-share-target-renderer><button id="done-button" disabled>Publish</button><button id="close-button">Close</button></ytcp-uploads-dialog>');
  assert(!(await S.publishSuccessVisible(p)));await assert.rejects(S.waitSaveConfirmed(p,p.locator('ytcp-uploads-dialog'),{timeout:180}),/не показал подтверждение/);
  await B.dismissPublishDialog(p);assert.equal(await p.locator('ytcp-uploads-dialog').count(),1);await p.close();
  console.log('PASS exact multiline typing; prefixes/extra sharing buttons/disabled Publish never count as saved success; unfinished wizard stays open.');
  for(const lang of ['ru','en']){
   const f=await setup(browser,lang,dir);const result=await S.runSequential(f.page,f.pack,{titleDelay:1,descriptionDelay:1,saveTimeout:2500,closeTimeout:3000},f.host);
   const stats=await f.page.evaluate(()=>stats);assert.equal(result.length,2);assert.equal(f.saved.size,2);assert.equal(stats.publishes,2);assert.equal(stats.closes,2);assert.equal(stats.headerCloses,0);assert.equal(stats.creates,2);assert.equal(stats.escapes,0);assert.equal(stats.selected[0],stats.selected[1]);
   assert.equal(await f.page.locator('ytcp-uploads-dialog').count(),0);assert.deepEqual(f.navigations,['/']);assert.equal(await f.page.locator('#image-trap').evaluate(e=>e.files.length),0);
   for(let n=0;n<2;n++){const record=f.saved.get('video'+String(n+1).padStart(6,'0'));assert.equal(record.title,f.pack[n].title);assert.equal(record.description,f.pack[n].description);assert(!record.paid&&!record.ai);assert.equal(record.visibility,'public');}
   assert.equal(f.stages.filter(x=>x.stage==='item_confirmed').length,2);assert.equal(f.stages.filter(x=>x.stage==='studio_close'&&/один раз/.test(x.text)).length,2);
   console.log('PASS',lang,'2 real picker cycles → exact multiline descriptions → Public readback → duplicate Copy links → delayed footer Close once → delayed Create → next file, no thumbnails/reloads/header-X/Escape.');await f.ctx.close();
  }
  // A missing success card can be recovered by checking the saved ID. Only the
  // confirmed wizard may then be closed; there is still just one Publish click.
  const fallback=await setup(browser,'en',dir,{badConfirmation:true});
  const recovered=await S.runSequential(fallback.page,fallback.pack.slice(0,1),{titleDelay:1,descriptionDelay:1,saveTimeout:180,closeTimeout:2500},fallback.host);
  assert.equal(recovered.length,1);assert.equal((await fallback.page.evaluate(()=>stats)).publishes,1);assert.equal((await fallback.page.evaluate(()=>stats)).headerCloses,1);await fallback.ctx.close();
  const share=await browser.newPage();await share.setContent('<button id="unrelated" onclick="window.wrong=true">Close</button><ytcp-video-share-dialog><h2>Video published</h2><a href="https://youtu.be/video000001">Video</a><button>Copy link</button><button>Copy link</button><button id="close-button" onclick="this.parentElement.remove()">Close</button></ytcp-video-share-dialog>');
  await assert.rejects(B.dismissPublishDialog(share,()=>{},{expectedId:'video000002',confirmed:true,timeout:350}),/другое видео/);
  assert.equal(await share.locator('ytcp-video-share-dialog').count(),1);
  await B.dismissPublishDialog(share,()=>{},{expectedId:'video000001',confirmed:true,timeout:1000});assert.equal(await share.locator('ytcp-video-share-dialog').count(),0);assert(!(await share.evaluate(()=>window.wrong)));await share.close();
  console.log('PASS missing success card recovered from persisted ID without another Publish; standalone share dialog closes locally; wrong ID and unrelated Close are never clicked.');
  const late=await browser.newPage();await late.route('https://studio.youtube.com/**',route=>route.fulfill({contentType:'text/html',body:'<button id="create-icon" hidden>Create</button><ytcp-uploads-dialog><h2>Video published</h2><button id="close-button" disabled>Close</button></ytcp-uploads-dialog><script>document.querySelector("#close-button").onclick=()=>{document.querySelector("ytcp-uploads-dialog").remove();document.querySelector("#create-icon").hidden=false;};setTimeout(()=>document.querySelector("#close-button").disabled=false,5500);</script>'}));
  await B.openStudio(late,'',()=>{});assert.equal(await late.locator('ytcp-uploads-dialog').count(),0);assert(await late.locator('#create-icon').isVisible());await late.close();
  console.log('PASS completed card appearing during startup may wait beyond the five-second detection poll before Close becomes enabled.');

  const lag=await setup(browser,'ru',dir,{staleFirstRead:true});const checked=await S.runSequential(lag.page,lag.pack.slice(0,1),{titleDelay:1,descriptionDelay:1},lag.host);assert.equal(checked.length,1);assert.equal((await lag.page.evaluate(()=>stats)).publishes,1);assert(lag.stages.some(s=>/только чтение/.test(s.text)));await lag.ctx.close();
  console.log('PASS one bounded repeat of saved-ID read handles temporarily stale visibility without retransmission or another Publish.');
  // Persisted success is durable BEFORE cleanup; a disabled Close stops the queue.
  const stuck=await setup(browser,'ru',dir,{neverClose:true});await assert.rejects(S.runSequential(stuck.page,stuck.pack,{titleDelay:1,descriptionDelay:1,closeTimeout:350},stuck.host),/Закрыть/);
  assert.equal(stuck.saved.size,1);assert.equal(stuck.stages.filter(s=>s.stage==='item_confirmed').length,1);assert.equal((await stuck.page.evaluate(()=>stats)).selected.length,1);assert.equal((await stuck.page.evaluate(()=>stats)).closes,0);await stuck.ctx.close();
  // A forged/optimistic success card with a private persisted record is rejected.
  const wrong=await setup(browser,'en',dir,{privateSave:true});await assert.rejects(S.runSequential(wrong.page,wrong.pack,{titleDelay:1,descriptionDelay:1},wrong.host),/Открытый доступ/);assert.equal(wrong.stages.filter(s=>s.stage==='item_confirmed').length,0);assert.equal((await wrong.page.evaluate(()=>stats)).closes,0);await wrong.ctx.close();
  // Known pending server IDs are rechecked, without transferring the file again.
  const retry=await setup(browser,'ru',dir);const item=retry.pack[0],id='video000001';retry.saved.set(id,{...item,id,tags:[],paid:false,ai:false,scheduled:false,visibility:'public',date:'2030-01-02',time:'14:45',zone:'Asia/Krasnoyarsk'});
  const out=await S.runSequential(retry.page,[{...item,studioPending:true,studioVideoId:id}],{titleDelay:1,descriptionDelay:1},retry.host);assert.equal(out.length,1);assert.equal((await retry.page.evaluate(()=>stats)).selected.length,0);
  await assert.rejects(S.runSequential(retry.page,[{...item,studioPending:true}],{},retry.host),/ID неизвестен/);await retry.ctx.close();
  console.log('PASS durable verified result before cleanup; Close timeout blocks next transfer; private saved status rejected; known pending ID rechecked and unknown ID never reuploaded.');
 }finally{await browser.close();fs.rmSync(dir,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
