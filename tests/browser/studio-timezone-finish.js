'use strict';
process.env.TZ='Asia/Krasnoyarsk';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path'),cp=require('child_process'),Module=require('module');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload');
const {html}=require('./youtube-studio-upload');
const baseline='a4b013b13267b1c5ba15edf9dd5a156e79fbf087';
function oldWorker(){
 const file=path.resolve(__dirname,'../../tools/uploader/youtube-studio-upload.js'),m=new Module(file,module);
 m.filename=file;m.paths=module.paths.concat(Module._nodeModulePaths(path.dirname(file)));
 m._compile(cp.execFileSync('git',['show',baseline+':tools/uploader/youtube-studio-upload.js'],{encoding:'utf8'}),file);return m.exports;
}
function plan(time='14:45'){const date='2030-01-02',at=new Date(date+'T'+time+':00');return {date,time,unix:at.getTime()/1000,offset:420};}
function nativeFixture(lang,state,editor=false,{numeric=false,reset=false}={}){
 const body=html(lang,state,editor),local=lang==='ru'?'Местное время':'Local time';
 const initial=editor?'Europe/Moscow':state.zone;
 const choices=[{value:'Europe/Moscow',label:'((GMT+0300)) '+local},...Array.from({length:150},(_,i)=>({value:'noise-'+i,label:'(GMT-05:00) Other city '+i})),{value:numeric?'217':'Asia/Krasnoyarsk',label:'(GMT+07:00) '+(lang==='ru'?'Красноярск':'Krasnoyarsk')}];
 return body.replace('</body>',`<script>
 const native=document.createElement('select');native.id='timezone-native';native.setAttribute('aria-label',${JSON.stringify(lang==='ru'?'Часовой пояс':'Time zone')});
 const choices=${JSON.stringify(choices)};for(const c of choices){const o=document.createElement('option');o.value=c.value;o.label=c.label;o.textContent=c.label;native.append(o);}native.value=${JSON.stringify(initial)};
 q('#timezone-button').replaceWith(native);window.zoneChanges=0;window.submits=0;
 native.onchange=()=>{zoneChanges++;zone=native.value==='217'?'Asia/Krasnoyarsk':native.value;
  if(${reset})setTimeout(()=>{q('ytcp-time-of-day-picker input').value='00:00';window.resetApplied=true;},250);
 };
 if(!editor){const submit=q('#done-button').onclick;q('#done-button').onclick=e=>{submits++;if(native.value!=='Asia/Krasnoyarsk'&&native.value!=='217')throw Error('Wrong zone at final click');return submit(e);};}
 </script></body>`);
}
// A delayed CDP connection makes one browser call cost 10 ms. It must not turn
// a 180-option menu into 900 round trips / a false "page not responding" error.
function delayed(locator,ms=10){
 return new Proxy(locator,{get(target,key){
  if(key==='page')return()=>new Proxy(target.page(),{get(page,key){if(['locator','getByRole','getByText'].includes(key))return(...a)=>delayed(page[key](...a),ms);const v=page[key];return typeof v==='function'?v.bind(page):v;}});
  if(key==='all')return async()=> (await target.all()).map(x=>delayed(x,ms));
  if(['locator','getByRole','getByText','filter','nth','first','last','or'].includes(key))return(...a)=>delayed(target[key](...a),ms);
  if(['isVisible','innerText','getAttribute','evaluate','evaluateAll','click','selectOption','press'].includes(key))return async(...a)=>{await new Promise(r=>setTimeout(r,ms));return target[key](...a);};
  const value=target[key];return typeof value==='function'?value.bind(target):value;
 }});
}
(async()=>{
 const browser=await chromium.launch({headless:true}),dir=fs.mkdtempSync(path.join(os.tmpdir(),'vb-timezone-finish-'));
 try{
  const old=oldWorker();
  const native=await browser.newPage();await native.setContent(nativeFixture('ru',{id:'video000001',title:'T',description:'D',tags:[],paid:false,ai:false,date:plan().date,time:plan().time,zone:'Europe/Moscow'}));
  await native.locator('#video-file').setInputFiles({name:'test.mp4',mimeType:'video/mp4',buffer:Buffer.from('fixture')});
  await native.locator('ytcp-visibility-scheduler,#schedule-controls').evaluateAll(nodes=>nodes.forEach(e=>e.hidden=false));
  await assert.rejects(old.setZone(native.locator('ytcp-uploads-dialog'),plan(),'Asia/Krasnoyarsk'),/не найден выбор часового пояса/);
  await S.setZone(native.locator('ytcp-uploads-dialog'),plan(),'Asia/Krasnoyarsk');assert.equal(await native.locator('select').inputValue(),'Asia/Krasnoyarsk');assert.equal(await native.evaluate(()=>zoneChanges),1);await native.close();
  console.log('REPRO v8 native select: old code cannot find zone; current code selects and reads back UTC+7.');
  for(const version of [old,S]){
   const page=await browser.newPage();
   await page.setContent('<ytcp-uploads-dialog><button id="timezone-button" onclick="document.querySelector(\'#zones\').hidden=false">Time zone</button><div id="zones" role="listbox" hidden style="height:180px;overflow:auto">'+Array.from({length:180},(_,i)=>`<div role="option" data-value="noise-${i}">(GMT-05:00) City ${i}</div>`).join('')+'<div role="option" data-value="Asia/Krasnoyarsk" onclick="this.setAttribute(\'aria-selected\',\'true\');document.querySelector(\'#zones\').hidden=true">(GMT+07:00) Krasnoyarsk</div></div></ytcp-uploads-dialog><script>document.addEventListener("keydown",e=>{if(e.key==="Escape")document.querySelector("#zones").hidden=true;});</script>');
   const began=Date.now(),task=version.setZone(delayed(page.locator('ytcp-uploads-dialog')),plan(),'Asia/Krasnoyarsk');
   if(version===old)await assert.rejects(task,/Studio не показал пояс ПК.*страница не отвечает/);
   else {await task;assert(Date.now()-began<4000,'Batch menu read should finish well inside the poll budget');}
   await page.close();
  }
  console.log('REPRO v8 long menu with CDP latency: old code reports false unresponsiveness; batched read completes <4s.');
  for(const lang of ['ru','en'])for(const numeric of [false,true]){
   const context=await browser.newContext({timezoneId:'Europe/Moscow'});context.setDefaultTimeout(4000);const page=await context.newPage();
   let saved=null,index=0;const events=[],opened=[];
   await context.route('https://studio.youtube.com/**',async route=>{
    const u=new URL(route.request().url());if(u.pathname==='/fixture/submit'){saved=JSON.parse(route.request().postData());return route.fulfill({body:'ok'});}
    // Studio may reopen an editor using the browser zone, while the stored instant stays the same.
    let record=saved;if(record){const at=new Date(Date.parse(saved.date+'T'+saved.time+':00Z')-4*3600000);record={...saved,date:at.toISOString().slice(0,10),time:at.toISOString().slice(11,16),zone:'Europe/Moscow'};}
    return route.fulfill({contentType:'text/html; charset=utf-8',body:u.pathname.includes('/video/')?nativeFixture(lang,record,true,{numeric}):'<main>Studio</main>'});
   });await page.goto('https://studio.youtube.com/');
   const pack=[plan('14:45'),plan('00:15')].map((p,n)=>{const video=path.join(dir,lang+'-'+numeric+'-'+n+'.mp4');fs.writeFileSync(video,'fixture');return {video,localJobId:'job-'+n,title:'App title '+n,description:'Different app description '+n,scheduleDate:p.date,scheduleTime:p.time,scheduledUnixSeconds:p.unix,scheduledUtcOffsetMinutes:p.offset};});
   const results=await S.runSequential(page,pack,{titleDelay:1,descriptionDelay:1,writeDescription:true},{progress:(stage,text)=>events.push({stage,text}),checkpoint:async()=>{},thumbnail:async()=>{},waitUpload:async()=>{},dismiss:async()=>{},openFile:async(p,file,checkpoint)=>{
    await checkpoint();opened.push(file);await p.goto('https://studio.youtube.com/');
    await p.setContent(nativeFixture(lang,{id:'video'+String(++index).padStart(6,'0'),title:'Filename',description:'Old',tags:['keep'],paid:true,ai:true,date:pack[index-1].scheduleDate,time:'00:00',zone:'Europe/Moscow'},false,{numeric,reset:true}));
    await p.locator('#video-file').setInputFiles(file);
   }});
   assert.equal(results.length,2);assert.equal(opened.length,2);assert.equal(saved.time,'00:15');assert.equal(saved.zone,'Asia/Krasnoyarsk');assert.equal(await page.evaluate(()=>submits),1);assert.equal(await page.evaluate(()=>zoneChanges),1);assert.equal(await page.evaluate(()=>resetApplied),true);
   assert(events.some(e=>e.stage==='studio_schedule'&&/восстанавливаю/.test(e.text)),'Delayed reset must be repaired');
   assert.equal(events.filter(e=>e.stage==='item_confirmed').length,2);assert.equal((await context.pages()).length,1,'Readback tabs cleaned after confirmation');
   const diagnostic=await S.zoneDiagnostics(page.locator('body'));assert.equal(diagnostic.controls.length,0,'Closed wizard must not be confused with a live selection');
   await context.close();console.log('PASS',lang,numeric?'numeric option values':'IANA option values','2 uploads, delayed time reset, exactly one Schedule, persisted UTC instant and previous-day readback.');
  }
  const bad=await browser.newPage();await bad.setContent('<ytcp-uploads-dialog><button id="datepicker-trigger">2030-01-02</button><input type="time" value="14:45"><select aria-label="Time zone"><option>(GMT+03:00) Moscow</option><option>(GMT+07:00) Krasnoyarsk</option></select><button id="done-button" disabled>Запланировать публикацию</button><div id="error" hidden>Указать время в прошлом нельзя</div></ytcp-uploads-dialog>');
  await bad.locator('select').selectOption({index:1});setTimeout(()=>bad.locator('#error').evaluate(e=>e.hidden=false).catch(()=>{}),650);
  await assert.rejects(S.schedule(bad,bad.locator('ytcp-uploads-dialog'),plan(),'Asia/Krasnoyarsk',{readyMs:4000}),/отклоняет время как прошедшее/);
  await bad.locator('input').fill('00:00');await assert.rejects(S.assertSchedule(bad.locator('ytcp-uploads-dialog'),plan()),/Кнопка не нажата/);await bad.close();
  assert.equal(S.offsetLabel('((GMT+0300)) Местное время'),180);assert.equal(S.offsetLabel('(GMT+05:30) India'),330);assert.equal(S.offsetLabel('(UTC−03:30) City'),-210);assert.equal(S.offsetLabel('GMT+99:99'),null);
  console.log('PASS late past-time errors stop immediately; last-millisecond field mismatch blocks Schedule; GMT+0300 and half-hour offsets.');
 }finally{await browser.close();fs.rmSync(dir,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
