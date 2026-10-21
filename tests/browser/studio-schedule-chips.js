'use strict';
process.env.TZ='Asia/Krasnoyarsk';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path'),cp=require('child_process');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload'),B=require('../../tools/uploader/studio-browser');
const {html}=require('./youtube-studio-upload');
function fixture(lang,state,editor=false,{rejectDate=false,unmount=true}={}){
 let body=html(lang,state,editor);
 body=body.replace('<ytcp-datepicker-trigger>','<div id="datepicker-trigger"><ytcp-dropdown-trigger>').replace('</ytcp-datepicker-trigger>','</ytcp-dropdown-trigger></div>');
 body=body.replace('<ytcp-date-picker hidden>','<ytcp-date-picker hidden style="position:absolute;z-index:99;background:white;top:30px;left:0;width:400px;min-height:500px"><iron-input><input id="date-value"></iron-input>');
 body=body.replace('<ytcp-time-of-day-picker><input',`<div id="time-of-day-trigger"><ytcp-dropdown-trigger><button id="time-chip"></button></ytcp-dropdown-trigger></div><ytcp-time-of-day-picker hidden><input`);
 return body.replace('</body>',`<ytcp-dropdown-trigger><button>Category 22</button></ytcp-dropdown-trigger><div id="time-list" hidden><span role="option">12:00 AM</span><span role="option">1:35 PM</span><span role="option">2:35 PM</span></div><script>
 window.dateEdits=0;window.dayClicks=0;window.dateEscapes=0;
 const zoneOpen=q('#timezone-button').onclick;q('#timezone-button').onclick=()=>setTimeout(zoneOpen,120);q('#zones').querySelectorAll('[role=option]').forEach(o=>{const choose=o.onclick;o.onclick=()=>setTimeout(choose,120);});
 const dateChip=q('#date-trigger'),picker=q('ytcp-date-picker'),dateValue=q('#date-value');
 const display=iso=>new Date(iso+'T12:00:00Z').toLocaleDateString(ru?'ru-RU':'en-US',{day:'numeric',month:'short',year:'numeric',timeZone:'UTC'});
 dateChip.textContent=display(state.date);dateValue.value=dateChip.textContent;
 dateValue.onkeydown=e=>{if(e.key!=='Enter')return;e.preventDefault();dateEdits++;if(${rejectDate})return;const expected=display('2030-01-02');if(e.target.value!==expected)throw Error('Unexpected date text '+e.target.value);dateChip.textContent=e.target.value;if(${unmount})picker.remove();};
 const oldOpen=dateChip.onclick;dateChip.onclick=()=>{oldOpen();dateValue.value=dateChip.textContent;const extra=document.createElement('div');extra.id='second-month';extra.innerHTML='<div>January 2030</div><button>2</button><button>22</button>';picker.append(extra);picker.querySelectorAll('#days button,#second-month button').forEach(b=>b.addEventListener('click',()=>dayClicks++));};
 document.addEventListener('keydown',e=>{if(e.key==='Escape'&&picker.isConnected&&!picker.hidden){dateEscapes++;picker.hidden=true;}});
 const timeChip=q('#time-chip'),rawTime=q('ytcp-time-of-day-picker input'),menu=q('#time-list');
 const timeLabel=t=>{const h=+t.slice(0,2);return (h%12||12)+t.slice(2)+' '+(h<12?'AM':'PM');};timeChip.textContent=timeLabel(state.time);
 timeChip.onclick=()=>{if(picker.isConnected&&!picker.hidden)throw Error('Calendar must close before time');menu.hidden=false;};
 menu.querySelectorAll('[role=option]').forEach(o=>o.onclick=()=>{timeChip.textContent=o.textContent;const match=o.textContent.match(/(\\d+):(\\d+) (AM|PM)/);rawTime.value=String(+match[1]%12+(match[3]==='PM'?12:0)).padStart(2,'0')+':'+match[2];menu.hidden=true;});
 </script></body>`);
}
(async()=>{
 const browser=await chromium.launch({headless:true}),dir=fs.mkdtempSync(path.join(os.tmpdir(),'vb-chip-calendar-'));
 try{
  for(const lang of ['ru','en']){
   const ctx=await browser.newContext({timezoneId:'America/New_York'});ctx.setDefaultTimeout(2500);const page=await ctx.newPage();let saved=null,index=0;const events=[];
   await ctx.route('https://studio.youtube.com/**',async route=>{
    const u=new URL(route.request().url());if(u.pathname==='/fixture/submit'){saved=JSON.parse(route.request().postData());return route.fulfill({body:'ok'});}
    return route.fulfill({contentType:'text/html; charset=utf-8',body:u.pathname.includes('/video/')?fixture(lang,{...saved,date:S.parseDate(saved.date)},true):'<main>Studio</main>'});
   });await page.goto('https://studio.youtube.com/');
   const pack=[0,1].map(n=>{const date='2030-01-02',time=(13+n)+':35',at=new Date(date+'T'+time+':00'),video=path.join(dir,lang+n+'.mp4');fs.writeFileSync(video,'fixture');return {video,localJobId:'job-'+lang+n,title:'App title '+n,description:'Описание из приложения '+n+'\nSecond line with punctuation.',scheduleDate:date,scheduleTime:time,scheduledUnixSeconds:at.getTime()/1000,scheduledUtcOffsetMinutes:-at.getTimezoneOffset()};});
   const output=await S.runSequential(page,pack,{titleDelay:1,descriptionDelay:1,writeDescription:true},{progress:(stage,text)=>events.push({stage,text}),checkpoint:async()=>{},thumbnail:async()=>{},waitUpload:async()=>{},dismiss:async()=>{},openFile:async(p,file,checkpoint)=>{
    if(index){assert.equal(saved.description,pack[index-1].description);assert.equal(S.parseDate(saved.date),pack[index-1].scheduleDate);}
    await checkpoint();index++;await p.goto('https://studio.youtube.com/');await p.setContent(fixture(lang,{id:'video'+String(index).padStart(6,'0'),title:'Filename',description:'Old Studio default',tags:['keep'],paid:true,ai:true,date:'2029-12-31',time:'00:00',zone:'America/New_York'}));
    await p.locator('#video-file').setInputFiles(file);
   }});
   assert.equal(output.length,2);assert.equal(saved.description,pack[1].description);assert.equal(saved.time,'14:35');assert.equal(saved.zone,'Asia/Krasnoyarsk');assert.equal(await page.evaluate(()=>dateEdits),1);assert.equal(await page.evaluate(()=>dayClicks),0);
   assert(events.some(e=>e.stage==='studio_date')&&events.some(e=>e.stage==='studio_time'));await ctx.close();
   console.log('PASS',lang,'two sequential uploads: popup date input + duplicate day numbers + input unmount + AM/PM time chip + PC zone + saved app descriptions');
  }
  for(const lang of ['ru','en']){
   const page=await browser.newPage();await page.setContent(fixture(lang,{id:'video000001',title:'T',description:'D',tags:[],paid:false,ai:false,date:'2030-01-02',time:'00:00',zone:'Asia/Krasnoyarsk'},false,{unmount:false}));
   await page.locator('#video-file').setInputFiles({name:'f.mp4',mimeType:'video/mp4',buffer:Buffer.from('fixture')});
   await page.locator('#details').evaluate(e=>e.hidden=true);await page.locator('ytcp-visibility-scheduler,#schedule-controls').evaluateAll(nodes=>nodes.forEach(e=>e.hidden=false));
   await page.locator('#date-trigger').click();await S.fillDate(page.locator('ytcp-uploads-dialog'),'2030-01-02');assert.equal(await page.evaluate(()=>dateEscapes),1);assert.equal(await page.evaluate(()=>dateEdits),0);
   await S.fillTime(page.locator('ytcp-uploads-dialog'),'13:35');assert.equal(S.parseTime(await page.locator('#time-chip').innerText()),'13:35');await page.close();
  }
  for(const lang of ['ru','en']){
   const p=await browser.newPage();await p.setContent(`<html lang="${lang}"><ytcp-uploads-dialog><div id="time-of-day-trigger"><ytcp-dropdown-trigger><button id="clock">12:00 AM</button></ytcp-dropdown-trigger></div><ytcp-time-of-day-picker hidden><input value="12:00 AM"></ytcp-time-of-day-picker></ytcp-uploads-dialog><script>
    document.querySelector('#clock').onclick=()=>document.querySelector('ytcp-time-of-day-picker').hidden=false;
    document.querySelector('input').onkeydown=e=>{if(e.key==='Enter'){document.querySelector('#clock').textContent=e.target.value;document.querySelector('ytcp-time-of-day-picker').remove();}};</script></html>`);
   await S.fillTime(p.locator('ytcp-uploads-dialog'),'13:37');assert.equal(await p.locator('#clock').innerText(),'1:37 PM');await p.close();
  }
  const ambiguous=await browser.newPage();await ambiguous.setContent('<html lang="en"><ytcp-uploads-dialog><button id="datepicker-trigger" onclick="document.querySelector(\'ytcp-date-picker\').hidden=false">Jan 1, 2030</button><ytcp-date-picker hidden><span class="month-label">January 2030</span><button onclick="window.wrong=1">22</button><span class="month-label">February 2030</span><button onclick="window.wrong=1">22</button></ytcp-date-picker></ytcp-uploads-dialog></html>');
  await assert.rejects(S.fillDate(ambiguous.locator('ytcp-uploads-dialog'),'2030-01-22'),/однозначный месяц/);assert.equal(await ambiguous.evaluate(()=>window.wrong),undefined);await ambiguous.close();
  console.log('PASS editable time popup accepts exact 13:37, async timezone menus, and ambiguous month/day buttons are never guessed');
  const bad=await browser.newPage();await bad.setContent(fixture('ru',{id:'video000001',title:'T',description:'D',tags:[],paid:false,ai:false,date:'2029-12-31',time:'00:00',zone:'Asia/Krasnoyarsk'},false,{rejectDate:true,unmount:false}));
  await bad.locator('#video-file').setInputFiles({name:'f.mp4',mimeType:'video/mp4',buffer:Buffer.from('fixture')});await bad.locator('#details').evaluate(e=>e.hidden=true);await bad.locator('ytcp-visibility-scheduler,#schedule-controls').evaluateAll(nodes=>nodes.forEach(e=>e.hidden=false));
  const start=Date.now();await assert.rejects(S.fillDate(bad.locator('ytcp-uploads-dialog'),'2030-01-02',{timeoutMs:1500}),/не подтвердил|истекло/);assert(Date.now()-start<5000);assert.equal(await bad.evaluate(()=>dateEdits),1);assert.equal(await bad.evaluate(()=>dayClicks),0);await bad.close();
  console.log('PASS already-selected date closes popup; rejected input stops within budget without day-number guessing');
  // Zero-box upload host must block navigation; context guard handles each native
  // beforeunload once on both the main task and verification tab.
  const ctx=await browser.newContext(),page=await ctx.newPage(),reports=[];let navigations=0;
  await ctx.route('https://studio.youtube.com/**',r=>{navigations++;return r.fulfill({contentType:'text/html',body:'<ytcp-uploads-dialog style="display:block;width:0;height:0"><div role="dialog" style="position:fixed;width:400px;height:200px"><button>Activate</button></div></ytcp-uploads-dialog><script>onbeforeunload=e=>{e.preventDefault();e.returnValue=""}</script>'});});
  B.installUploadDialogGuard(ctx,(stage,text)=>reports.push(stage));B.installUploadDialogGuard(ctx,()=>{throw Error('duplicate guard');});
  await page.goto('https://studio.youtube.com/guard');await B.openStudio(page,'',()=>{});assert.equal(navigations,1);await page.getByRole('button').click();await assert.rejects(page.reload({timeout:1000}));assert.equal(reports.filter(x=>x==='studio_dialog').length,1);
  const verify=await ctx.newPage();await verify.goto('https://studio.youtube.com/verify');await verify.getByRole('button').click();await assert.rejects(verify.reload({timeout:1000}));assert.equal(reports.filter(x=>x==='studio_dialog').length,2);await ctx.close();
  console.log('PASS zero-box wizard is never reloaded; one dialog handler covers task and verification tabs');
  for(const [text,expected] of [['22 окт. 2026г.','2026-10-22'],['Oct 22, 2026','2026-10-22'],['Jan 2, 2030','2030-01-02'],['31.02.2030',null],['12:00 AM',null]])assert.equal(S.parseDate(text),expected);
  assert.equal(S.parseTime('12:00 AM'),'00:00');assert.equal(S.parseTime('12:00 PM'),'12:00');assert.equal(S.parseTime('1:35 PM'),'13:35');assert.equal(S.parseTime('25:10'),null);
 }finally{await browser.close();fs.rmSync(dir,{recursive:true,force:true});}
 // A real CDP client disconnect must terminate while leaving its browser/page.
 const dir2=fs.mkdtempSync(path.join(os.tmpdir(),'vb-cdp-preserve-')),ctx=await chromium.launchPersistentContext(dir2,{headless:true,args:['--remote-debugging-port=0']});
 try{
  const page=ctx.pages()[0];await page.setContent('<input id="preserved" value="unsaved fixture">');
  const [port,ws]=fs.readFileSync(path.join(dir2,'DevToolsActivePort'),'utf8').trim().split(/\r?\n/),endpoint=`ws://127.0.0.1:${port}${ws}`;
  const code=`const {chromium}=require(${JSON.stringify(path.resolve('tools/uploader/node_modules/playwright-core'))});const B=require(${JSON.stringify(path.resolve('tools/uploader/studio-browser'))});(async()=>{const b=await chromium.connectOverCDP(${JSON.stringify(endpoint)});await B.disconnect(b);process.exitCode=1;})().catch(e=>{console.error(e);process.exitCode=2;});`;
  const child=cp.spawn(process.execPath,['-e',code],{stdio:['ignore','ignore','pipe']});let error='';child.stderr.on('data',b=>error+=b);let timer;
  try{await Promise.race([new Promise((resolve,reject)=>child.on('exit',code=>code===1?resolve():reject(Error(error||'exit '+code)))),new Promise((_,reject)=>{timer=setTimeout(()=>{child.kill();reject(Error('CDP client did not exit'));},6000);})]);}finally{clearTimeout(timer);}
  assert.equal(await page.locator('#preserved').inputValue(),'unsaved fixture');assert((await fetch(`http://127.0.0.1:${port}/json/version`).then(r=>r.json())).webSocketDebuggerUrl);
  console.log('PASS real CDP failure teardown exits and preserves browser and unsaved page');
 }finally{await ctx.close();fs.rmSync(dir2,{recursive:true,force:true,maxRetries:10,retryDelay:100});}
})().catch(e=>{console.error(e);process.exitCode=1;});
