'use strict';
const assert=require('assert');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const O=require('../../tools/uploader/youtube-owner-publish');
const S=require('../../tools/uploader/youtube-studio-upload');
const IDS=['video000001','video000002'];
function editor(lang,state,{twoStep=true,dirty=false,wrongSave=false,screenshotPicker=false,unknownLabel=false,inertPublicHost=false,readbackDelay=0}={}){
 const ru=lang==='ru',label=unknownLabel?'Visibility updated':state.visibility==='public'?(ru?(screenshotPicker?'Для всех':'Открытый доступ'):'Public'):(ru?(screenshotPicker?'Отложенная публ...':'Запланировано'):'Scheduled');
 if(screenshotPicker){
  return `<html lang="${lang}"><body><ytcp-video-metadata-editor><textarea name="title">Original title</textarea><button id="visibility">${label}</button></ytcp-video-metadata-editor>
  <script>
  document.addEventListener('keydown',e=>{if(e.key==='Escape')document.querySelector('ytcp-video-visibility-edit-dialog')?.remove();});
  document.querySelector('#visibility').onclick=()=>{
   const el=document.createElement('ytcp-video-visibility-edit-dialog');el.style.display='contents';
   el.innerHTML=${JSON.stringify(`<div role="dialog"><section><button id="expand-publish">\n          ${ru?'Сохранить или опубликовать':'Save or publish'}\n        </button><div id="choices" hidden><tp-yt-paper-radio-button name="PRIVATE">${ru?'Ограниченный доступ':'Private'}</tp-yt-paper-radio-button><tp-yt-paper-radio-button name="UNLISTED">${ru?'Доступ по ссылке':'Unlisted'}</tp-yt-paper-radio-button><tp-yt-paper-radio-button name="PUBLIC" id="public-radio-button">${ru?'Открытый доступ':'Public'}</tp-yt-paper-radio-button><label><input type="checkbox">${ru?'Провести премьеру сейчас':'Set as instant Premiere'}</label></div></section><section id="schedule">${ru?'Запланировать публикацию':'Schedule'}<input value="2 Nov 2026"><input value="06:16"></section><footer><button id="cancel">${ru?'Отмена':'Cancel'}</button><ytcp-button id="save-button"><button disabled>${ru?'ОК':'OK'}</button></ytcp-button></footer></div>`)};
   document.body.append(el);el.querySelector('#cancel').onclick=()=>el.remove();
   el.querySelectorAll('tp-yt-paper-radio-button').forEach(btn=>{
    const sr=btn.attachShadow({mode:'open'});
    sr.innerHTML='<div id="radioContainer" role="radio" tabindex="0" style="width:18px;height:18px;border:1px solid #888;border-radius:50%;display:inline-block;vertical-align:middle;margin-right:8px"></div><span id="label"></span><input id="input" type="radio" hidden>';
    sr.querySelector('#label').textContent=btn.textContent;btn.textContent='';
    const input=sr.querySelector('#input');input.name=btn.getAttribute('name');
    if(${inertPublicHost}){btn.style.cssText='display:block;width:500px;height:40px';if(input.name==='PUBLIC'){input.checked=true;btn.setAttribute('aria-checked','true');}}
    sr.querySelector('#radioContainer').addEventListener('click',e=>{if(e.isTrusted)window.trustedRadioClicks=(window.trustedRadioClicks||0)+1;});
    const sync=()=>{const on=input.checked;btn.setAttribute('aria-checked',on?'true':'false');sr.querySelector('#radioContainer').style.background=on?'#fff':'transparent';};
    sr.querySelector('#radioContainer').onclick=()=>{el.querySelectorAll('tp-yt-paper-radio-button').forEach(b=>{const i=b.shadowRoot.querySelector('#input');if(i){i.checked=b===btn;}});sync();if(btn.getAttribute('name')==='PUBLIC')setTimeout(()=>{const done=el.querySelector('#save-button button');done.textContent=${JSON.stringify(ru?'Опубликовать':'Publish')};done.disabled=false;},150);};
    btn.onclick=e=>{if(e.target===btn)e.preventDefault();};
   });
   const savedPublic=${JSON.stringify(state.visibility==='public')};
   const seedPublic=()=>{if(!savedPublic)return;setTimeout(()=>el.querySelector('tp-yt-paper-radio-button[name=PUBLIC]')?.shadowRoot?.querySelector('#radioContainer')?.click(),${readbackDelay});};
   el.querySelector('#expand-publish').onclick=()=>{window.expands=(window.expands||0)+1;el.querySelector('#choices').hidden=false;el.querySelector('#schedule').hidden=true;seedPublic();};
   el.querySelector('#save-button').onclick=async()=>{const pub=el.querySelector('tp-yt-paper-radio-button[name=PUBLIC]');const ok=el.querySelector('#save-button button');if(ok.disabled||!pub?.shadowRoot?.querySelector('#input')?.checked||el.querySelector('#choices').hidden)return;await fetch('/fixture/public',{method:'POST'});document.querySelector('#visibility').textContent=${JSON.stringify(ru?'Для всех':'Public')};el.remove();};
  };
  </script></body></html>`;
 }
 return `<html><body><ytcp-video-metadata-editor><textarea name="title">Original title</textarea><button id="visibility">${label}</button></ytcp-video-metadata-editor>
 <ytcp-button id="save-button"><button disabled>${ru?'Сохранить':'Save'}</button></ytcp-button>
 <script>
 document.addEventListener('keydown',e=>{if(e.key==='Escape')document.querySelector('ytcp-video-visibility-edit-dialog')?.remove();});
 const twoStep=${twoStep};let selected=false;
 const commit=async()=>{await fetch('/fixture/public',{method:'POST'});document.querySelector('#visibility').textContent=${JSON.stringify(ru?'Открытый доступ':'Public')};};
 document.querySelector('#visibility').onclick=()=>{const el=document.createElement('ytcp-video-visibility-edit-dialog');el.innerHTML='<label><input type="radio" name="PUBLIC">${ru?'Открытый доступ':'Public'}</label><footer><ytcp-button id="done-button"><button>${ru?'Готово':'Done'}</button></ytcp-button></footer>';document.body.append(el);el.querySelector('#done-button').onclick=async()=>{selected=el.querySelector('input').checked;if(!selected)return;el.remove();if(twoStep)setTimeout(()=>document.querySelector('#save-button button').disabled=false,350);else await commit();};};
 document.querySelector('#save-button').onclick=async()=>{if(selected){${wrongSave?'':'await commit();'}document.querySelector('#save-button button').disabled=true;}};
 </script></body></html>`;
}
function watch(lang,state,{owned=true,saveComment=true,wrongAuthor=false}={}){
 const ru=lang==='ru';return `<html><body><div id="owner"><div id="channel-name"><a href="/@owner">Owner</a></div>${owned?`<a href="https://studio.youtube.com/video/${state.id}/edit">${ru?'Изменить видео':'Edit video'}</a>`:''}</div>
 <div id="comments"><button id="simplebox-placeholder">${ru?'Введите комментарий':'Add a comment'}</button><ytd-comment-simplebox-renderer hidden><div id="contenteditable-root" contenteditable="true" role="textbox"></div><ytcp-button id="submit-button"><button>${ru?'Оставить комментарий':'Comment'}</button></ytcp-button></ytd-comment-simplebox-renderer><section id="threads"></section></div>
 <script>
 document.querySelector('#contenteditable-root').addEventListener('blur',()=>{const box=document.querySelector('#contenteditable-root');for(const n of Array.from(box.childNodes)){if(n.nodeType!==3||!/[📌📲]/u.test(n.textContent))continue;const fragment=document.createDocumentFragment();for(const part of n.textContent.split(/([📌📲])/u)){if(/^[📌📲]$/u.test(part)){const img=document.createElement('img');img.alt=part;fragment.append(img);}else fragment.append(document.createTextNode(part));}n.replaceWith(fragment);}});
 document.querySelector('#contenteditable-root').addEventListener('beforeinput',e=>{if(e.inputType==='insertText'&&e.data===':')e.preventDefault();});
 const rows=${JSON.stringify(state.comments||[])};function render(row){const thread=document.createElement('ytd-comment-thread-renderer');thread.innerHTML='<a id="author-text"></a><div id="content-text" style="white-space:pre-wrap"></div><a id="published-time-text"></a>';thread.querySelector('#author-text').href=row.author||'/@owner';thread.querySelector('#content-text').textContent=row.text;thread.querySelector('#published-time-text').href='/watch?v=${state.id}&lc='+row.id;document.querySelector('#threads').append(thread);}rows.forEach(render);
 document.querySelector('#simplebox-placeholder').onclick=()=>{document.querySelector('ytd-comment-simplebox-renderer').hidden=false;document.querySelector('#simplebox-placeholder').hidden=true;};
 document.querySelector('#submit-button').onclick=async()=>{window.sent=(window.sent||0)+1;const copy=document.querySelector('#contenteditable-root').cloneNode(true);copy.querySelectorAll('img[alt]').forEach(img=>img.replaceWith(document.createTextNode(img.alt)));copy.querySelectorAll('br').forEach(br=>br.replaceWith(document.createTextNode('\\n')));const text=copy.textContent;const row=await (await fetch('/fixture/comment',{method:'POST',body:JSON.stringify({text})})).json();${saveComment?`row.author=${JSON.stringify(wrongAuthor?'/@stranger':'/@owner')};setTimeout(()=>render(row),150);`:''}};
 </script></body></html>`;
}
async function fixture(browser,lang,options={}){
 const ctx=await browser.newContext();ctx.setDefaultTimeout(2500);const state=new Map(IDS.map(id=>[id,{id,visibility:'scheduled',comments:[]}]))
 const log=[];let publishCalls=0,commentCalls=0;
 await ctx.route('**/*',async route=>{
  const u=new URL(route.request().url()),id=u.searchParams.get('v')||u.pathname.match(/\/video\/(\w+)\/edit/)?.[1];
  if(u.pathname==='/fixture/public'){
   publishCalls++;const referer=route.request().headers().referer;const vid=referer.match(/\/video\/(\w+)\/edit/)[1];state.get(vid).visibility='public';log.push('public:'+vid);return route.fulfill({body:'ok'});
  }
  if(u.pathname==='/fixture/comment'){
   commentCalls++;const referer=route.request().headers().referer;const vid=new URL(referer).searchParams.get('v');const row={text:JSON.parse(route.request().postData()).text,id:'comment-'+commentCalls};state.get(vid).comments.push(row);log.push('comment:'+vid);return route.fulfill({contentType:'application/json',body:JSON.stringify(row)});
  }
  if(!state.has(id))return route.fulfill({contentType:'text/html; charset=utf-8',body:'Studio'});
  return route.fulfill({contentType:'text/html; charset=utf-8',body:u.hostname==='studio.youtube.com'?editor(lang,state.get(id),options):watch(lang,state.get(id),options)});
 });
 return {ctx,state,log,page:await ctx.newPage(),counts:()=>({publishCalls,commentCalls})};
}
(async()=>{
 const browser=await chromium.launch({headless:true});try{
  const picker=await browser.newPage();
  await picker.setContent('<ytcp-video-visibility-edit-dialog style="display:contents"><tp-yt-paper-dialog id="dialog" role="dialog"><ytcp-video-visibility-select><label><input type="radio" name="PUBLIC">Public</label></ytcp-video-visibility-select><button>OK</button></tp-yt-paper-dialog></ytcp-video-visibility-edit-dialog><ytcp-video-visibility-edit-dialog hidden><tp-yt-paper-dialog id="dialog"><input type="radio"></tp-yt-paper-dialog></ytcp-video-visibility-edit-dialog>');
  assert.equal(await (await O.visibilityDialog(picker)).evaluate(e=>e.tagName),'YTCP-VIDEO-VISIBILITY-EDIT-DIALOG');
  await picker.locator('body').evaluate(e=>e.insertAdjacentHTML('beforeend','<ytcp-video-visibility-select-dialog><input type="radio">Second popup</ytcp-video-visibility-select-dialog>'));
  await assert.rejects(O.visibilityDialog(picker),/несколько окон доступа/);await picker.close();
  console.log('PASS nested visibility host + paper dialog + select is one popup; hidden stale popup ignored; separate live popups rejected');
  for(const lang of ['ru','en']){
   const f=await fixture(browser,lang,{screenshotPicker:true,inertPublicHost:true});
   await f.page.goto('https://studio.youtube.com/video/'+IDS[0]+'/edit');await f.page.locator('#visibility').click();await f.page.locator('#expand-publish').click();
   const dialog=f.page.locator('ytcp-video-visibility-edit-dialog');
   await S.selectPublicRadio(dialog);
   await S.until(()=>f.page.locator('#save-button button').isEnabled(),1500,'Public circle must enable Publish even when aria-checked was already true');
   assert.equal(await f.page.evaluate(()=>window.trustedRadioClicks),1,'one real pointer click on shadow radio circle');
   await O.runSequential(f.page,[{publishedVideoId:IDS[0],commentText:'Author comment 0511'}],{typingDelay:1,actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{}});
   assert.deepEqual(f.counts(),{publishCalls:1,commentCalls:1});
   assert.deepEqual(f.log,['public:'+IDS[0],'comment:'+IDS[0]]);
   assert.equal(f.ctx.pages().length,2,'Only original Studio editor and watch/comment tab');
   assert(f.page.url().includes('studio.youtube.com/video/'),'Original editor remains unchanged');
   assert(f.ctx.pages()[1].url().includes('youtube.com/watch?v='+IDS[0]),'New tab opens watch URL, not editor');
   assert.equal(f.state.get(IDS[0]).comments[0].text,'Author comment 0511');
   await f.ctx.close();
   console.log('PASS',lang,'already checked Public + inert host: trusted circle click enables Publish, then one publication and author comment');
  }
  assert.equal(O.parseVisibility('Отложенная публикация'),'scheduled');assert.equal(O.parseVisibility('Отложенная публ...'),'scheduled');assert.equal(O.parseVisibility('Для всех'),'public');assert.equal(O.parseVisibility('Visibility: Public'),'public');
  for(const lang of ['ru','en']){
   const f=await fixture(browser,lang,{screenshotPicker:true}),events=[];
   const items=IDS.map((id,i)=>({publishedVideoId:id,title:'Scheduled '+i,localJobId:'scheduled-'+i,commentText:'📌 https://t.me/example_bot?start=t0211\n📲 Платформа: https://bit.ly/binodex-broker',commentState:''}));
   await O.runSequential(f.page,items,{typingDelay:1,actionDelayMs:0,confirmDelayMs:0,progress:(stage,text,extra)=>events.push({stage,text,...extra}),checkpoint:async()=>{}});
   assert.deepEqual(f.counts(),{publishCalls:2,commentCalls:2});
   assert.deepEqual(f.log,IDS.flatMap(id=>['public:'+id,'comment:'+id]));
   assert.equal(events.filter(e=>e.stage==='owner_item_confirmed').length,2);
   assert(events.some(e=>e.stage==='owner_build'&&e.text.includes(O.BUILD)));
   assert(f.state.get(IDS[0]).comments[0].text.includes('start=t0211'));
   await f.ctx.close();
   console.log('PASS',lang,'screenshot layout: Scheduled → expand Save or publish → Public → one Publish → watch → author comment; display:contents host and delayed Publish');
  }
  const changed=await fixture(browser,'en',{screenshotPicker:true,unknownLabel:true,readbackDelay:800});
  let visibilityOpens=0;await changed.ctx.exposeBinding('recordVisibilityOpen',()=>{visibilityOpens++;});
  await changed.ctx.addInitScript(()=>{document.addEventListener('click',e=>{if(e.target.closest('#visibility'))window.recordVisibilityOpen();});});
  await O.runSequential(changed.page,[{publishedVideoId:IDS[0],commentText:''}],{actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{}});assert.equal(changed.counts().publishCalls,1);assert.equal(visibilityOpens,1,'Only the publication picker is opened; no readback page');await changed.ctx.close();
  console.log('PASS unrecognized summary opens publication picker once; no readback tab');
  for(const lang of ['ru','en']){
   const f=await fixture(browser,lang),events=[];const items=IDS.map((id,i)=>({publishedVideoId:id,title:'Title '+i,localJobId:'job-'+i,commentText:'📌 Материалы: https://example.com/?date=2110\nSecond line',commentState:''}));
   await O.runSequential(f.page,items,{typingDelay:1,actionDelayMs:0,confirmDelayMs:0,progress:(stage,text,extra)=>events.push({stage,...extra}),checkpoint:async(item,index)=>{assert.equal(f.counts().commentCalls,index-1,'durable ACK before click');f.log.push('ack:'+item.publishedVideoId);}});
   assert.deepEqual(f.counts(),{publishCalls:2,commentCalls:2});assert.deepEqual(f.log,IDS.flatMap(id=>['public:'+id,'ack:'+id,'comment:'+id]));
   assert.equal(events.filter(e=>e.stage==='owner_comment_sent').length,2);
   assert(items.every(i=>i.commentState==='sent'));
   assert.equal(events.filter(e=>e.stage==='owner_video_public').length,2);
   // Repeat is idempotent even when the local confirmation was lost: exact own
   // comment is detected on page, with real ID, before any submit checkpoint.
   await O.runSequential(f.page,items.map(i=>({...i,commentState:''})),{typingDelay:1,actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{throw Error('must not send again');}});
   assert.deepEqual(f.counts(),{publishCalls:2,commentCalls:2});await f.ctx.close();
   console.log('PASS',lang,'two existing IDs: dialog Done + editor Save + persisted Public + own comment + durable ACK + repeat without duplicates');
  }
  const direct=await fixture(browser,'en',{twoStep:false});
  await O.runSequential(direct.page,[{publishedVideoId:IDS[0],commentText:''}],{actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{throw Error('empty comment');}});assert.deepEqual(direct.counts(),{publishCalls:1,commentCalls:0});await direct.ctx.close();
  const sameText=await fixture(browser,'ru');sameText.state.get(IDS[0]).comments.push({text:'Same text',author:'/@stranger',id:'foreign-comment'});
  await O.runSequential(sameText.page,[{publishedVideoId:IDS[0],commentText:'Same text'}],{typingDelay:1,actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{}});assert.equal(sameText.counts().commentCalls,1);await sameText.ctx.close();
  console.log('PASS one-step visibility commit, delayed editor Save, empty template, and exact text from another author is not treated as own comment');
  const wrong=await fixture(browser,'en',{wrongSave:true});
  let extraPages=0;wrong.ctx.on('page',()=>extraPages++);
  await O.runSequential(wrong.page,[{publishedVideoId:IDS[0],commentText:''}],{actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{throw Error('empty comment');}});
  assert.equal(extraPages,0,'No verification page even when persistence cannot be confirmed');assert.equal(wrong.counts().commentCalls,0);await wrong.ctx.close();
  const foreign=await fixture(browser,'ru',{owned:false});
  await foreign.page.goto('https://www.youtube.com/watch?v='+IDS[0]);await assert.rejects(O.ownVideo(foreign.page,IDS[0]),/может редактировать/);assert.equal(foreign.counts().commentCalls,0);await foreign.ctx.close();
  const uncertain=await fixture(browser,'en',{saveComment:false});const item={publishedVideoId:IDS[0],localJobId:'durable',commentText:'Account text',commentState:''};
  const next={publishedVideoId:IDS[1],commentText:'Next account text',commentState:''};
  await O.runSequential(uncertain.page,[item,next],{typingDelay:1,actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{}});
  assert.equal(item.commentState,'sent');assert.equal(next.commentState,'sent');assert.equal(uncertain.counts().commentCalls,2);
  await O.runSequential(uncertain.page,[item,next],{actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{throw Error('must not resend');}});assert.equal(uncertain.counts().commentCalls,2);await uncertain.ctx.close();
  const noAck=await fixture(browser,'ru');await assert.rejects(O.runSequential(noAck.page,[{publishedVideoId:IDS[0],commentText:'No ACK'}],{typingDelay:1,actionDelayMs:0,confirmDelayMs:0,progress:()=>{},checkpoint:async()=>{throw Error('storage full');}}),/storage full/);assert.equal(noAck.counts().commentCalls,0);await noAck.ctx.close();
  console.log('PASS persisted Public mismatch / non-owner / lost comment confirmation / failed durable ACK block comments and unsafe retries');
  // Explicit opt-out remains supported; production upload now writes app descriptions.
  for(const lang of ['ru','en']){
   const page=await browser.newPage();const {html}=require('./youtube-studio-upload');
   await page.setContent(html(lang,{id:IDS[0],title:'Filename',description:'Account default\nDo not replace',paid:true,ai:true,tags:['account-tag'],date:'2030-01-02',time:'10:00',zone:'UTC'},false));
   await page.locator('#video-file').setInputFiles({name:'fixture.mp4',mimeType:'video/mp4',buffer:Buffer.from('fixture')});const scope=await S.uploadScope(page);await S.details(page,scope,{title:'App title',description:'x'.repeat(6000),tags:['wrong']},{writeDescription:false,writeTags:false,titleDelay:1,paidPromotion:false,alteredContent:false});
   assert.equal(await S.read(await S.field(scope,'description')),'Account default\nDo not replace');assert.equal(await S.read(await S.field(scope,'title')),'App title');await page.close();
  }
  console.log('PASS RU/EN explicit opt-out preserves Studio descriptions and tags, rewrite title and apply configured disclosures');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
