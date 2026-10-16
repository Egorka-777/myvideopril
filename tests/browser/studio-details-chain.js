'use strict';
process.env.TZ='Asia/Krasnoyarsk';
const assert=require('assert'),fs=require('fs'),os=require('os'),path=require('path');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const S=require('../../tools/uploader/youtube-studio-upload');
const B=require('../../tools/uploader/studio-browser');
const {html}=require('./youtube-studio-upload');
function fixture(lang,state,editor=false){
 let body=html(lang,state,editor,true);
 // Non-editable #textbox wrapper plus an editable child, suggestions outside it.
 body=body.replace(/<div id="title-textarea"><div id="textbox" contenteditable="true" role="textbox">([^<]*)<\/div><\/div>/,
  '<div id="title-textarea"><div id="textbox"><div contenteditable="true" role="textbox">$1</div><aside>Recommended hashtags</aside></div></div>');
 if(editor)return body;
 body=body.replace('<ytcp-uploads-dialog hidden><div id="details">','<ytcp-uploads-dialog hidden><ytcp-video-upload-dialog><div id="details">')
  .replace('<ytcp-visibility-scheduler hidden>','</ytcp-video-upload-dialog><ytcp-visibility-scheduler hidden>')
  .replace('<button id="next-button">','<ytcp-video-upload-progress><span class="progress-label">'+(lang==='ru'?'Загружено: 38 %':'Uploading: 38 %')+'</span></ytcp-video-upload-progress><div hidden class="progress-label">Upload complete</div><button id="next-button">');
 return body.replace('</body>',`<button id="create-icon">${lang==='ru'?'Создать':'Create'}</button><div id="upload-menu" hidden><button role="menuitem">${lang==='ru'?'Загрузить видео':'Upload videos'}</button></div><ytcp-uploads-file-picker id="picker" hidden></ytcp-uploads-file-picker><script>
  const picker=document.querySelector('#picker'),input=document.querySelector('#video-file');picker.append(input);
  document.querySelector('#create-icon').onclick=()=>document.querySelector('#upload-menu').hidden=false;
  document.querySelector('#upload-menu button').onclick=()=>picker.hidden=false;
  window.nextClicks=0;document.querySelector('#next-button').addEventListener('click',()=>window.nextClicks++);
  input.addEventListener('change',()=>{picker.hidden=true;setTimeout(()=>{document.querySelector('ytcp-video-upload-progress span').textContent=${JSON.stringify(lang==='ru'?'Проверка завершена. На контент заявлены права.':'Checks complete. Copyright-protected content found.')};},500);});
 </script></body>`);
}
(async()=>{
 const temp=fs.mkdtempSync(path.join(os.tmpdir(),'vb-details-')),browser=await chromium.launch({headless:true});
 try{
  // Production context uses 10 seconds. This exact 959-character case failed at
  // ~518 characters before the fix; do not accelerate typing in this regression.
  const context=await browser.newContext();context.setDefaultTimeout(10000);
  const page=await context.newPage();await page.setContent('<div contenteditable="true" id="description" style="white-space:pre-wrap">old</div>');
  const text='Описание видео. '.repeat(60).trim(),progress=[],start=Date.now();
  await S.typeText(page.locator('#description'),text,{delay:18,onProgress:(n,total)=>progress.push([n,total])});
  assert(Date.now()-start>10000);assert.equal(await S.read(page.locator('#description')),text);assert(progress.length>5);
  assert(S.metadataBudget({title:'Title',description:'x'.repeat(5000),tags:[]},{})>180000);
  for(const label of ['Add a title that describes your video (type @ to mention a channel)','Название (обязательное поле)']){
   await page.setContent('<div role="textbox" contenteditable="true"></div>');
   await page.locator('[role=textbox]').evaluate((e,label)=>e.setAttribute('aria-label',label),label);
   await S.typeText(await S.field(page,'title'),'Accessible title',{delay:1});
   assert.equal(await S.read(await S.field(page,'title')),'Accessible title');
  }
  console.log('PASS 959-character description at production speed survives 10s context default, with progress');
  await context.close();
  for(const lang of ['ru','en']){
   const ctx=await browser.newContext({timezoneId:'America/New_York'});ctx.setDefaultTimeout(10000);
   const p=await ctx.newPage(),events=[];let saved=null,checkpoints=0;
   const file=path.join(temp,lang+'.mp4');fs.writeFileSync(file,'local video fixture');
   const initial={id:'video000001',title:'Filename title',description:'Default description',paid:true,ai:true,tags:['old'],date:'2029-12-31',time:'00:00',zone:'America/New_York'};
   await ctx.route('https://studio.youtube.com/**',async route=>{
    const u=new URL(route.request().url());
    if(u.pathname==='/fixture/submit'){saved=JSON.parse(route.request().postData());return route.fulfill({body:'ok'});}
    return route.fulfill({contentType:'text/html; charset=utf-8',body:fixture(lang,u.pathname.includes('/video/')?saved:initial,u.pathname.includes('/video/'))});
   });
   await B.openStudio(p,'',()=>{});
   const date='2030-01-02',time='12:35',at=new Date(date+'T'+time+':00');
   const item={localJobId:'job-'+lang,video:file,title:lang==='ru'?'Новый заголовок':'New title',description:'Описание\nSecond line',tags:['binodex'],scheduleDate:date,scheduleTime:time,scheduledUnixSeconds:at.getTime()/1000,scheduledUtcOffsetMinutes:-at.getTimezoneOffset()};
   const output=await S.runSequential(p,[item],{titleDelay:1,descriptionDelay:1},{
    progress:(stage,text)=>events.push({stage,text}),checkpoint:async()=>{checkpoints++;},
    openFile:(page,file,checkpoint)=>B.openFile(page,file,checkpoint,(stage,text)=>events.push({stage,text})),
    waitUpload:S.waitUpload,thumbnail:async()=>{},dismiss:async()=>{}
   });
   assert.equal(checkpoints,1);assert.equal(output.length,1);assert(saved.scheduled);assert.equal(saved.title,item.title);assert.equal(saved.description,item.description);assert(!saved.paid&&!saved.ai);
   assert(saved.events.some(e=>e.key==='Delete'&&e.target==='title'));assert.equal(await p.evaluate(()=>window.nextClicks),3);
   for(const stage of ['file_selected','studio_editor','metadata_title','metadata_description','metadata_options','metadata_tags','upload_transfer','studio_steps','studio_schedule','item_confirmed'])assert(events.some(e=>e.stage===stage),stage);
   console.log('PASS',lang,'native picker → nested editor → rewritten title → footer progress → Next → persisted schedule');
   await ctx.close();
  }
  const p=await browser.newPage(),dialogs=[];
  await p.route('https://studio.youtube.com/guard',r=>r.fulfill({contentType:'text/html',body:'<button>Activate</button><script>onbeforeunload=e=>{e.preventDefault();e.returnValue=""}</script>'}));
  p.on('dialog',d=>B.handleUploadDialog(d,(stage,text)=>dialogs.push({stage,text})));
  await p.goto('https://studio.youtube.com/guard');await p.getByRole('button').click();
  await assert.rejects(p.reload({timeout:3000}),/ERR_ABORTED|cancel|aborted|Timeout/i);
  assert.equal(dialogs.length,1);assert(await p.getByRole('button').isVisible());
  await p.setContent('<ytcp-uploads-dialog><div hidden class="progress-label">Upload complete</div><p>No current status</p></ytcp-uploads-dialog>');
  await assert.rejects(S.waitUpload(p,{unknownMs:30}),/индикатор/);
  await p.close();console.log('PASS reload cancelled without losing upload; hidden/absent progress never counts as success');
 }finally{await browser.close();fs.rmSync(temp,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
