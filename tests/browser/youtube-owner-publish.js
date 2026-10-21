'use strict';
const assert=require('assert');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const O=require('../../tools/uploader/youtube-owner-publish');
const S=require('../../tools/uploader/youtube-studio-upload');
const IDS=['video000001','video000002'];
function editor(lang,state,{twoStep=true,dirty=false,wrongSave=false}={}){
 const ru=lang==='ru',label=state.visibility==='public'?(ru?'Открытый доступ':'Public'):(ru?'Запланировано':'Scheduled');
 return `<html><body><ytcp-video-metadata-editor><textarea name="title">Original title</textarea><button id="visibility">${label}</button></ytcp-video-metadata-editor>
 <ytcp-button id="save-button"><button disabled>${ru?'Сохранить':'Save'}</button></ytcp-button>
 <script>
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
 const rows=${JSON.stringify(state.comments||[])};function render(row){const thread=document.createElement('ytd-comment-thread-renderer');thread.innerHTML='<a id="author-text"></a><div id="content-text" style="white-space:pre-wrap"></div><a id="published-time-text"></a>';thread.querySelector('#author-text').href=row.author||'/@owner';thread.querySelector('#content-text').textContent=row.text;thread.querySelector('#published-time-text').href='/watch?v=${state.id}&lc='+row.id;document.querySelector('#threads').append(thread);}rows.forEach(render);
 document.querySelector('#simplebox-placeholder').onclick=()=>{document.querySelector('ytd-comment-simplebox-renderer').hidden=false;document.querySelector('#simplebox-placeholder').hidden=true;};
 document.querySelector('#submit-button').onclick=async()=>{window.sent=(window.sent||0)+1;const text=document.querySelector('#contenteditable-root').innerText;const row=await (await fetch('/fixture/comment',{method:'POST',body:JSON.stringify({text})})).json();${saveComment?`row.author=${JSON.stringify(wrongAuthor?'/@stranger':'/@owner')};setTimeout(()=>render(row),150);`:''}};
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
  for(const lang of ['ru','en']){
   const f=await fixture(browser,lang),events=[];const items=IDS.map((id,i)=>({publishedVideoId:id,title:'Title '+i,localJobId:'job-'+i,commentText:'📌 Материалы: https://example.com/?date=2110\nSecond line',commentState:''}));
   await O.runSequential(f.page,items,{typingDelay:1,progress:(stage,text,extra)=>events.push({stage,...extra}),checkpoint:async(item,index)=>{assert.equal(f.counts().commentCalls,index-1,'durable ACK before click');f.log.push('ack:'+item.publishedVideoId);}});
   assert.deepEqual(f.counts(),{publishCalls:2,commentCalls:2});assert.deepEqual(f.log,IDS.flatMap(id=>['public:'+id,'ack:'+id,'comment:'+id]));
   assert(events.filter(e=>e.stage==='owner_comment_confirmed').every(e=>e.commentId&&e.commentText==='📌 Материалы: https://example.com/?date=2110\nSecond line'));
   assert.equal(events.filter(e=>e.stage==='owner_video_public').length,2);
   // Repeat is idempotent even when the local confirmation was lost: exact own
   // comment is detected on page, with real ID, before any submit checkpoint.
   await O.runSequential(f.page,items.map(i=>({...i,commentState:''})),{typingDelay:1,progress:()=>{},checkpoint:async()=>{throw Error('must not send again');}});
   assert.deepEqual(f.counts(),{publishCalls:2,commentCalls:2});await f.ctx.close();
   console.log('PASS',lang,'two existing IDs: dialog Done + editor Save + persisted Public + own comment + durable ACK + repeat without duplicates');
  }
  const direct=await fixture(browser,'en',{twoStep:false});
  await O.runSequential(direct.page,[{publishedVideoId:IDS[0],commentText:''}],{progress:()=>{},checkpoint:async()=>{throw Error('empty comment');}});assert.deepEqual(direct.counts(),{publishCalls:1,commentCalls:0});await direct.ctx.close();
  const sameText=await fixture(browser,'ru');sameText.state.get(IDS[0]).comments.push({text:'Same text',author:'/@stranger',id:'foreign-comment'});
  await O.runSequential(sameText.page,[{publishedVideoId:IDS[0],commentText:'Same text'}],{typingDelay:1,progress:()=>{},checkpoint:async()=>{}});assert.equal(sameText.counts().commentCalls,1);await sameText.ctx.close();
  console.log('PASS one-step visibility commit, delayed editor Save, empty template, and exact text from another author is not treated as own comment');
  const wrong=await fixture(browser,'en',{wrongSave:true});
  await assert.rejects(O.runSequential(wrong.page,[{publishedVideoId:IDS[0],commentText:'Not sent'}],{verifyMs:350,progress:()=>{},checkpoint:async()=>{throw Error('must not submit');}}),/не подтвердил открытый доступ/);assert.equal(wrong.counts().commentCalls,0);await wrong.ctx.close();
  const foreign=await fixture(browser,'ru',{owned:false});
  await foreign.page.goto('https://www.youtube.com/watch?v='+IDS[0]);await assert.rejects(O.ownVideo(foreign.page,IDS[0]),/может редактировать/);assert.equal(foreign.counts().commentCalls,0);await foreign.ctx.close();
  const uncertain=await fixture(browser,'en',{saveComment:false});const item={publishedVideoId:IDS[0],localJobId:'durable',commentText:'Account text',commentState:''};
  await assert.rejects(O.runSequential(uncertain.page,[item],{typingDelay:1,commentMs:350,progress:()=>{},checkpoint:async()=>{}}),/Повтор заблокирован/);assert.equal(item.commentState,'pending');assert.equal(uncertain.counts().commentCalls,1);
  await assert.rejects(O.runSequential(uncertain.page,[item],{progress:()=>{}}),/неподтверждённая/);assert.equal(uncertain.counts().commentCalls,1);await uncertain.ctx.close();
  const noAck=await fixture(browser,'ru');await assert.rejects(O.runSequential(noAck.page,[{publishedVideoId:IDS[0],commentText:'No ACK'}],{typingDelay:1,progress:()=>{},checkpoint:async()=>{throw Error('storage full');}}),/storage full/);assert.equal(noAck.counts().commentCalls,0);await noAck.ctx.close();
  console.log('PASS persisted Public mismatch / non-owner / lost comment confirmation / failed durable ACK block comments and unsafe retries');
  // Production option leaves preconfigured Studio description/tags byte-for-byte.
  for(const lang of ['ru','en']){
   const page=await browser.newPage();const {html}=require('./youtube-studio-upload');
   await page.setContent(html(lang,{id:IDS[0],title:'Filename',description:'Account default\nDo not replace',paid:true,ai:true,tags:['account-tag'],date:'2030-01-02',time:'10:00',zone:'UTC'},false));
   await page.locator('#video-file').setInputFiles({name:'fixture.mp4',mimeType:'video/mp4',buffer:Buffer.from('fixture')});const scope=await S.uploadScope(page);await S.details(page,scope,{title:'App title',description:'x'.repeat(6000),tags:['wrong']},{writeDescription:false,writeTags:false,titleDelay:1,paidPromotion:false,alteredContent:false});
   assert.equal(await S.read(await S.field(scope,'description')),'Account default\nDo not replace');assert.equal(await S.read(await S.field(scope,'title')),'App title');await page.close();
  }
  console.log('PASS RU/EN production options preserve per-account Studio descriptions and tags, rewrite title and apply configured disclosures');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
