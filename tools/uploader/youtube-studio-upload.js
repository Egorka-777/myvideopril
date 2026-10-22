"use strict";
// Native Studio UI only. No metadata/publish HTTP endpoints and no clipboard/DOM text writes.
const fs=require('fs');
const {bounded,dismissPublishDialog,isPostPublishScreen,publicationState,findPublishCloseButton,uploadWizardOpen}=require('./studio-browser');
const UPLOAD_DIALOG='ytcp-video-upload-dialog,ytcp-uploads-dialog,ytcp-upload-dialog';
const BUILD='2026-10-22-studio-upload-recovery-v20';
const traces=new WeakMap();
const taskOwnedPages=new WeakSet();
function safeLog(text){return String(text||'').replace(/Bearer\s+\S+/gi,'Bearer [hidden]').replace(/((?:password|authorization|cookie|api[-_ ]?(?:token|key)|token|signature)\s*[:=]\s*)[^\s,;]+/gi,'$1[hidden]').replace(/(https?:\/\/)[^/\s@]+@/gi,'$1[hidden]@');}
function tracePage(page,opts={}){
 const trace={build:BUILD,lastStage:'preflight',navigations:[],networkErrors:[],stageLog:[]},listeners=[];
 if(opts.diagnosticsDir)try{fs.mkdirSync(opts.diagnosticsDir,{recursive:true});trace.logFile=require('path').join(opts.diagnosticsDir,'studio-trace-'+Date.now()+'-'+process.pid+'.jsonl');}catch(error){trace.logError=error.message;}
 function record(type,data){const row={time:new Date().toISOString(),type,...data};if(trace.logFile)try{fs.appendFileSync(trace.logFile,JSON.stringify(row)+'\n');}catch(error){trace.logError=error.message;}return row;}
 function watch(p){
  if(traces.get(p)===trace)return;traces.set(p,trace);
  const navigation=frame=>{if(frame!==p.mainFrame())return;try{const u=new URL(frame.url()),row=record('navigation',{host:u.hostname,path:u.pathname});trace.navigations.push(row);}catch{}};
  const failed=request=>{try{const u=new URL(request.url());if(!/^(studio\.youtube\.com|upload\.youtube\.com|i\.ytimg\.com)$/.test(u.hostname))return;trace.networkErrors.push(record('request_failed',{host:u.hostname,path:u.pathname,resource:request.resourceType(),error:safeLog(request.failure()?.errorText)}));}catch{}};
  const response=r=>{if(r.status()<400)return;try{const u=new URL(r.url());if(u.hostname==='studio.youtube.com'||u.hostname==='upload.youtube.com')record('http_error',{host:u.hostname,path:u.pathname,status:r.status()});}catch{}};
  const consoleMessage=m=>{if(m.type()==='error'||m.type()==='warning')record('console',{level:m.type(),message:safeLog(m.text()).slice(0,1500)});};
  const pageError=e=>record('page_error',{message:safeLog(e.message).slice(0,1500)});
  for(const [event,fn] of [['framenavigated',navigation],['requestfailed',failed],['response',response],['console',consoleMessage],['pageerror',pageError]]){p.on(event,fn);listeners.push(()=>p.off(event,fn));}
 }
 watch(page);record('run_start',{build:BUILD,profileId:opts.profileId||'',items:opts.itemCount||0});
 return {trace,record,watch,dispose:()=>listeners.forEach(fn=>fn())};
}
async function scopeAlive(scope){
 if(await scope.isVisible())return true;
 // A component host can have no box while its positioned dialog and fields
 // are visible. Hidden stale hosts still have no visible descendants.
 for(const child of await scope.locator('[contenteditable=true],#next-button,#done-button,ytcp-video-upload-progress,tp-yt-paper-dialog,ytcp-dialog,[role=dialog]').all())
  if(await child.isVisible())return true;
 return false;
}
async function uploadScope(page){
 // The editor may be nested in the upload wizard. Its footer/Next button belongs
 // to the outer dialog. Never take a visible inner host as the complete wizard.
 const roots=[];
 for(const selector of UPLOAD_DIALOG.split(',')){
  for(const candidate of await page.locator(selector).all())if(await scopeAlive(candidate)){
   const nested=await candidate.evaluate((e,selectors)=>{
    for(let p=e.parentElement||e.getRootNode().host;p;p=p.parentElement||p.getRootNode().host)
     if(p.matches(selectors)&&!p.hidden&&getComputedStyle(p).display!=='none'&&getComputedStyle(p).visibility!=='hidden')return true;
    return false;
   },UPLOAD_DIALOG);
   if(!nested)roots.push(candidate);
  }
 }
 if(roots.length>1)throw new Error('Studio: открыто несколько активных окон загрузки; выбор ролика неоднозначен');
 return roots[0]||null;
}
const L={
 next:/^(Next|Далее)$/i, more:/^(Show more|Показать больше|Показать ещё|Показать еще|Развернуть|Ещё|Еще)$/i,
 schedule:/^(Schedule(?: publication)?|Запланировать(?: публикацию)?)$/i,
 zone:/^(Time zone|Timezone|Часовой пояс)$/i,
 paid:/paid promotion|paid product|branded content|прямая реклама|спонсорск|продакт.плейсмент/i,
 ai:/altered content|synthetic content|synthetic media|AI.generated content|was AI used|use of AI|AI usage|использование ИИ|изменённый контент|измененный контент/i,
 no:/^(No(?:,.*)?|Нет(?:,.*)?)$/i, yes:/^(Yes(?:,.*)?|Да(?:,.*)?)$/i,
 private:/^(Private|Ограниченный доступ|Закрытый доступ|Скрыт)/i,
 public:/^(Public|Открытый доступ|Доступно всем|Public access)/i,
 scheduled:/Scheduled|Запланирован|Публикация запланирована/i
};
function isShorts(item){return /^(shorts|short)$/.test(String(item.contentKind||'').trim().toLowerCase());}
function isImmediatePublish(item){
 const kind=String(item.contentKind||'').trim().toLowerCase();
 const mode=String(item.publishMode||'').trim().toLowerCase();
 return kind==='shorts'||kind==='short'||mode==='immediate';
}
const pause=ms=>new Promise(r=>setTimeout(r,ms));
async function one(locator,{optional=false}={}){
 const all=[];for(const x of await locator.all())if(await x.isVisible())all.push(x);
 if(all.length===1)return all[0];if(optional&&!all.length)return null;
 throw new Error(`Studio: ожидается один элемент, найдено ${all.length}: ${locator}`);
}
async function anyVisible(locator){
 for(const x of await locator.all())if(await x.isVisible())return true;
 return false;
}
async function until(fn,timeout=30000,message='Studio не подтвердил состояние'){
 const end=Date.now()+timeout;while(Date.now()<end){const value=await bounded(fn,Math.min(5000,Math.max(1,end-Date.now())),message+' · страница не отвечает');if(value)return value;await pause(150);}throw new Error(message);
}
function norm(s){return String(s||'').replace(/\u00a0/g,' ').replace(/\r\n?/g,'\n').trim();}
async function read(box){return norm(await box.evaluate(e=>e.isContentEditable?e.innerText:('value'in e?e.value:e.textContent)));}
async function field(scope,kind,optional=false){
 const selector=kind==='title'?'#title-textarea #textbox, #title-textarea [contenteditable=true], input[name="title"],textarea[name="title"]'
  :'#description-textarea #textbox, #description-textarea [contenteditable=true],textarea[name="description"]';
 const editable=[];for(const candidate of await scope.locator(selector).all())
  if(await candidate.isVisible()&&await candidate.isEditable().catch(()=>false))editable.push(candidate);
 if(editable.length>1)throw new Error(`Studio: несколько редактируемых полей ${kind}`);
 let target=editable[0]||null;
 if(!target)target=await one(scope.getByRole('textbox',{name:kind==='title'?/^(Title|Название)(?:\b|\s|\()|add a title|добавьте название|введите название/i:/^(Description|Описание)(?:\b|\s|\()|tell viewers about your video|расскажите зрителям о/i}),{optional:true});
 if(!target&&!optional)throw new Error(`Studio: не найдено однозначное поле ${kind}`);return target;
}
function typingBudget(text,delay){return 10000+Array.from(norm(text)).length*(Math.max(0,delay)+30);}
function metadataBudget(item,opts){
 const {typingTotalBudget}=require('./studio-editor-controls');
 return 60000+typingTotalBudget(item.title,{delay:opts.titleDelay??55})+(opts.writeDescription===false||item.description==null?0:typingTotalBudget(item.description,{name:'описания',delay:opts.descriptionDelay??18,activationMs:25000,verifyMs:35000}))+typingBudget(tags(item.tags).join(','),20);
}
const {editorState,activateEditor,typeText,inspectEditors}=require('./studio-editor-controls')({until,norm,read,typingBudget});
function tags(raw){const list=(Array.isArray(raw)?raw:String(raw||'').split(/[,;\n\r]/)).map(s=>String(s).trim().replace(/^#+/,'')).filter(Boolean);return [...new Set(list)];}
function validateTags(list){const length=list.reduce((n,t)=>n+t.length+(t.includes(' ')?2:0),Math.max(0,list.length-1));if(length>500)throw new Error('Теги превышают лимит YouTube 500 символов с разделителями/кавычками. Сократите список в приложении.');}
async function chips(scope){return scope.locator('#tags-container ytcp-chip, #tags-container [role=listitem],ytcp-free-text-chip-bar#tags-container .chip').evaluateAll(a=>a.map(e=>String(e.getAttribute('label')||e.getAttribute('data-tag')||e.querySelector('#label')?.textContent||e.textContent||'').replace(/\s*[×✕]\s*$/,'').trim()).filter(Boolean));}
async function setTags(scope,list,{verify=false}={}){
 validateTags(list);const container=scope.locator('#tags-container');
 if(!list.length&&!await container.count())return;
 const input=await one(scope.locator('#tags-container input, input[name=tags]'),{optional:true});
 if(!input)throw new Error('Studio: поле тегов не найдено после «Показать больше»');
 if(!verify){
  let old=await chips(scope);
  for(let i=0;i<old.length;i++){
   const remove=await one(container.locator('ytcp-chip').first().locator('#delete-icon,button[aria-label], [role=button][aria-label]'),{optional:true});
   if(remove)await remove.click();else{await input.click();await input.press('Home');await input.press('Backspace');await input.press('Backspace');}
  }
  if((await chips(scope)).length)throw new Error('Studio не удалил прежние теги');
  for(const tag of list){await input.pressSequentially(tag,{delay:20});await input.press('Enter');}
 }
 const actual=await chips(scope);if(JSON.stringify(actual.slice().sort())!==JSON.stringify(list.slice().sort()))throw new Error('Studio не подтвердил точный список тегов');
}
async function showMore(scope){
 // The screenshot's «Развернуть» is the metadata toggle below age restrictions.
 // Prefer its stable id; unrelated expanders elsewhere must not make it ambiguous.
 const toggles=scope.locator('#toggle-button');
 for(const toggle of await toggles.all())if(await toggle.isVisible()&&L.more.test(norm(await toggle.innerText()))){await toggle.scrollIntoViewIfNeeded();await toggle.click();return;}
 const button=await one(scope.getByRole('button',{name:L.more}),{optional:true});
 if(button){await button.scrollIntoViewIfNeeded();await button.click();}
}
async function question(scope,kind,answer,{verify=false}={}){
 const marker=kind==='paid'?L.paid:L.ai;
 // Stable component ids first, then smallest containing section. Never fall back to the whole page.
 const selector=kind==='paid'?'#paid-promotion, #paid-promotion-container,ytcp-paid-promotion,ytcp-paid-product-placement':'#altered-content, #altered-content-container, #synthetic-media,ytkp-altered-content-select,ytcp-altered-content,ytcp-video-metadata-editor-altered-content';
 let block=null;for(const part of selector.split(',')){block=await one(scope.locator(part),{optional:true});if(block)break;}
 if(!block){
  const handles=await scope.locator('ytcp-form-section,ytcp-video-metadata-editor-section,section,fieldset').all();
  const found=[];for(const x of handles){if(!await x.isVisible())continue;const text=await x.innerText();if(marker.test(text)&&await x.locator('[role=radio],tp-yt-paper-radio-button,input[type=radio],input[type=checkbox],[role=checkbox],tp-yt-paper-checkbox').count())found.push(x);}
  // Refuse ambiguous sections rather than clicking an unrelated "No".
  if(found.length){const lengths=await Promise.all(found.map(async x=>(await x.innerText()).length));const min=Math.min(...lengths);const smallest=found.filter((x,i)=>lengths[i]===min);if(smallest.length===1)block=smallest[0];}
 }
 if(!block){
  const marked=await scope.evaluate((root,kind)=>{
   const mine=kind==='paid'?/paid promotion|paid product|branded content|прямая реклама|спонсорск|продакт.плейсмент/i:/altered content|synthetic content|synthetic media|AI.generated content|was AI used|use of AI|AI usage|использование ИИ|измен[её]нный контент/i;
   const other=kind!=='paid'?/paid promotion|paid product|branded content|прямая реклама/i:/altered content|was AI used|использование ИИ/i;
   const parent=e=>e.parentElement||e.getRootNode().host||null;
   const all=(node,selector)=>{
    const out=Array.from(node.querySelectorAll(selector));
    if(node.shadowRoot)out.push(...all(node.shadowRoot,selector));
    for(const child of node.querySelectorAll('*'))if(child.shadowRoot)out.push(...all(child.shadowRoot,selector));return out;
   };
   const textOf=node=>(node.innerText||node.textContent||'')+' '+all(node,'h1,h2,h3,h4,[role=heading],label,span,p').map(x=>x.innerText||x.textContent||'').join(' ');
   const found=[];
   for(const heading of all(root,'h1,h2,h3,h4,[role=heading],label,span,div,p')){
    const text=(heading.innerText||heading.textContent||'').trim();if(!heading.getClientRects().length||text.length>500||!mine.test(text))continue;
    for(let p=parent(heading);p&&p!==root;p=parent(p)){
     const options=all(p,'[role=radio],tp-yt-paper-radio-button,input[type=radio],[role=checkbox],tp-yt-paper-checkbox,input[type=checkbox]');
     if(!options.length)continue;if(other.test(textOf(p))||options.length>5)break;
     if(!found.includes(p))found.push(p);break;
    }
   }
   if(!found.length)return false;const min=Math.min(...found.map(e=>textOf(e).length));const smallest=found.filter(e=>textOf(e).length===min);if(smallest.length!==1)return false;
   all(root,'[data-vb-question="'+kind+'"]').forEach(e=>e.removeAttribute('data-vb-question'));smallest[0].setAttribute('data-vb-question',kind);return true;
  },kind);
  if(marked)block=scope.locator('[data-vb-question="'+kind+'"]');
 }
 if(!block)throw new Error(`Studio: не найден отдельный блок ${kind==='paid'?'прямой рекламы / Paid promotion':'использования ИИ / Altered content'}`);
 const radios=block.locator('[role=radio],tp-yt-paper-radio-button,input[type=radio]');
 if(await radios.count()){
  const accessible=await one(block.getByRole('radio',{name:answer?L.yes:L.no}),{optional:true});
  const matches=accessible?[accessible]:[];for(const radio of accessible?[]:await radios.all())if(await radio.isVisible()){
   const label=norm((await radio.getAttribute('aria-label'))||await radio.innerText().catch(()=>''));
   if((answer?L.yes:L.no).test(label))matches.push(radio);
  }
  if(matches.length!==1)throw new Error(`Studio: неоднозначный ответ ${answer?'Да':'Нет'} в блоке ${kind}`);
  const selected=matches[0];if(!verify)await selected.click();
  await until(async()=>await selected.evaluate(e=>typeof e.checked==='boolean'?e.checked:e.hasAttribute('aria-checked')?e.getAttribute('aria-checked')==='true':e.hasAttribute('checked')),4000,'Studio не подтвердил ответ в блоке '+kind);return;
 }
 // Legacy Paid promotion is a checkbox, not a Yes/No radio group.
 if(kind!=='paid')throw new Error('Studio: неизвестный элемент ответа ИИ');
 const checkbox=await one(block.locator('input[type=checkbox],[role=checkbox],tp-yt-paper-checkbox'));
 const checked=await checkbox.evaluate(e=>typeof e.checked==='boolean'?e.checked:e.hasAttribute('aria-checked')?e.getAttribute('aria-checked')==='true':e.hasAttribute('checked'));
 if(checked!==answer&&!verify)await checkbox.click();
 if(await checkbox.evaluate(e=>typeof e.checked==='boolean'?e.checked:e.hasAttribute('aria-checked')?e.getAttribute('aria-checked')==='true':e.hasAttribute('checked'))!==answer)throw new Error('Studio не подтвердил прямую рекламу');
}
async function details(page,scope,item,opts,{verify=false}={}){
 const report=opts.progress||(()=>{});
 if(!verify){report('metadata_title','Очищаю и печатаю заголовок.');await typeText(await field(scope,'title'),item.title,{delay:opts.titleDelay??55,scope,name:'названия',signal:opts.signal,onState:(event,text)=>report('metadata_title',text)});report('metadata_title','Заголовок введён и проверен.');if(opts.captureIdentity)await opts.captureIdentity();}
 else if(await read(await field(scope,'title'))!==norm(item.title))throw new Error('После перезагрузки изменился заголовок');
 if(opts.writeDescription!==false&&item.description!==null&&item.description!==undefined){
  const description=await field(scope,'description');
  if(!verify){await description.scrollIntoViewIfNeeded().catch(()=>{});report('metadata_description','Ввожу описание.');await typeText(description,item.description,{delay:opts.descriptionDelay??18,scope,name:'описания',signal:opts.signal,activationMs:25000,verifyMs:35000,onState:(event,text,diagnostic)=>{report('metadata_description',text,diagnostic?{editorDiagnostic:diagnostic}:{});if(diagnostic){const trace=traces.get(page);if(trace){trace.editorAttempts=trace.editorAttempts||[];trace.editorAttempts.push(diagnostic);}}},onProgress:(done,total)=>report('metadata_description',`Описание: ${done}/${total} символов.`)});report('metadata_description','Описание проверено целиком: '+norm(item.description).length+' символов; SHA256 '+require('crypto').createHash('sha256').update(norm(item.description)).digest('hex')+'.');}
  else if(await read(description)!==norm(item.description))throw new Error('После перезагрузки изменилось описание');
 }
 const kids=await one(scope.locator('[name=VIDEO_MADE_FOR_KIDS_NOT_MFK]').or(scope.getByRole('radio',{name:/not made for kids|не для детей/i})),{optional:true});
 if(kids&&!verify)await kids.click();
 report('metadata_options','Открываю настройки рекламы и ИИ.');await showMore(scope);
 // Expansion mounts these controls asynchronously, so a one-shot lookup is too early.
 await until(async()=>{
  const visible=async pattern=>{for(const label of await scope.getByText(pattern).all())if(await label.isVisible())return true;return false;};
  return await visible(L.paid)&&await visible(L.ai);
 },10000,'Studio не показал настройки рекламы и ИИ после «Развернуть»');
 await question(scope,'paid',opts.paidPromotion===true,{verify});
 await question(scope,'ai',opts.alteredContent===true,{verify});
 if(opts.writeTags===true&&item.tags!==null&&item.tags!==undefined){report('metadata_tags','Проверяю теги.');await setTags(scope,tags(item.tags),{verify});}
 report('metadata_ready','Название и настройки проверены.');
}
async function pickNextButton(scope){
 return actionButton(scope,'next-button',L.next);
}
async function actionButton(scope,id,label){
 // A ytcp-button wrapper and its native child are one control, not two buttons.
 for(const host of await scope.locator('#'+id).all()){
  const inner=await one(host.getByRole('button',{name:label}),{optional:true});
  if(inner)return inner;if(await host.isVisible())return host;
 }
 return one(scope.getByRole('button',{name:label}),{optional:true});
}
async function enabled(button){
 if(!button||!await button.count())return false;
 try{return await button.evaluate(e=>{
  for(let p=e;p;p=p.parentElement||p.getRootNode().host){if(p.hasAttribute('disabled')||p.getAttribute('aria-disabled')==='true')return false;}
  return true;
 },undefined,{timeout:1000});}
 catch(e){if(!await button.count()||!await button.isVisible())return false;throw e;}
}
async function scheduleCard(scope){
 const byId=await one(scope.locator('#schedule-radio-button,[name=SCHEDULE]'),{optional:true});if(byId)return byId;
 const radio=await one(scope.getByRole('radio',{name:L.schedule}),{optional:true});if(radio)return radio;
 const cards=[];
 for(const node of await scope.getByText(L.schedule).all())if(await node.isVisible()&&!await node.evaluate(e=>!!e.closest('#done-button,footer,#footer,ytcp-dialog-footer'))){
  // Do not confuse the final Schedule button with the card that opens its fields.
  cards.push(node);
 }
 if(cards.length>1)throw new Error('Studio: несколько карточек планирования');return cards[0]||null;
}
async function wizardState(scope){
 const card=await scheduleCard(scope);
 const publicRadio=await one(scope.locator('#public-radio-button,[name=PUBLIC]').or(scope.getByRole('radio',{name:L.public})),{optional:true});
 const publishLabel=await one(scope.getByText(/^(Save or publish|Сохранить или опубликовать)$/i),{optional:true});
 const panels=[];
 for(const selector of ['#details','ytcp-video-metadata-editor','ytcp-video-elements','#video-elements','ytcp-video-checks','ytcp-video-upload-checks','#checks','ytcp-visibility-scheduler','ytcp-video-visibility-select'])
  for(const panel of await scope.locator(selector).all())if(await scopeAlive(panel)){panels.push(selector);break;}
 const selected=await scope.locator('ytcp-stepper [aria-selected=true],ytcp-stepper [aria-current=step],#stepper [aria-selected=true],#stepper [aria-current=step],[role=tab][aria-selected=true],ytcp-stepper [selected],ytcp-stepper [active]').evaluateAll(nodes=>nodes.filter(e=>e.getAttribute('selected')!=='false'&&e.getAttribute('active')!=='false').map(e=>e.getAttribute('id')||e.getAttribute('data-step')||e.textContent.trim().slice(0,80)).slice(0,8));
 const stepper=await scope.locator('ytcp-stepper,#stepper').evaluateAll(nodes=>nodes.map(e=>Object.fromEntries(['selected','selected-step','active-step','current-step','data-step'].filter(a=>e.hasAttribute(a)).map(a=>[a,e.getAttribute(a)]))));
 const controls=await scope.getByRole('button').evaluateAll(nodes=>nodes.filter(e=>e.getClientRects().length&&!e.closest('#next-button,#done-button,#stepper,ytcp-stepper,footer,ytcp-dialog-footer')).map(e=>(e.getAttribute('aria-label')||e.innerText||'').replace(/\u00a0/g,' ').trim().slice(0,80)).filter(label=>!/^(Next|Далее)$/i.test(label)));
 const visibility=!!(card||publicRadio||publishLabel);
 const next=await pickNextButton(scope);
 const state={stage:visibility?'visibility':panels.includes('#details')||panels.includes('ytcp-video-metadata-editor')?'details':'intermediate',panels,selected,stepper,controls:controls.slice(0,30),next:!!next,nextEnabled:!!await enabled(next)};
 state.key=JSON.stringify({stage:state.stage,panels,selected,stepper,controls:state.controls});return state;
}
const {parseDate,parseTime,dateControl,timeControl,controlValue,calendarSurface,fillDate,fillTime}=require('./studio-schedule-controls')({one,until,norm,read});
const {offsetLabel,zoneControl,readZone,setZone,closeZoneMenu,zoneDiagnostics}=require('./studio-timezone-controls')({one,until,norm});
function normalizeScheduleTime(raw){
 const m=String(raw||'').trim().match(/^(\d{1,2}):(\d{2})/);
 if(!m)return '';
 return String(m[1]).padStart(2,'0')+':'+m[2];
}
function planFor(item,now=Date.now()){
 const date=String(item.scheduleDate||'').trim();
 let time=normalizeScheduleTime(item.scheduleTime);
 if(!/^\d{4}-\d{2}-\d{2}$/.test(date)||!/^\d{2}:\d{2}$/.test(time))throw new Error('Нужно расписание из приложения');
 const local=new Date(date+'T'+time+':00');const unix=Number(item.scheduledUnixSeconds);
 if(!Number.isFinite(unix)||unix*1000<now+60000)throw new Error('Время публикации уже прошло или осталось меньше минуты. Пересоздайте расписание.');
 if(!Number.isFinite(local.getTime())||local.getFullYear()!==Number(date.slice(0,4))||local.getMonth()+1!==Number(date.slice(5,7))||local.getDate()!==Number(date.slice(8)))throw new Error('Некорректная дата');
 if(Math.abs(local.getTime()/1000-unix)>.1)throw new Error('Время ПК изменилось после подготовки расписания. Пересоздайте расписание.');
 const offset=-local.getTimezoneOffset();if(offset!==Number(item.scheduledUtcOffsetMinutes))throw new Error('Часовой пояс ПК изменился. Пересоздайте расписание.');
 return {date,time,unix,offset};
}
async function syncClock(page,plan,progress){
 const zone=Intl.DateTimeFormat().resolvedOptions().timeZone;
 // Scheduling selects the PC zone in Studio itself. Do not override the browser's
 // emulation settings or hold an extra CDP session in the Dolphin profile.
 const actual=await bounded(()=>page.evaluate(unix=>({zone:Intl.DateTimeFormat().resolvedOptions().timeZone,offset:-new Date(unix*1000).getTimezoneOffset()}),plan.unix),5000,'Вкладка Dolphin не отвечает при подготовке расписания');
 if(actual.offset!==plan.offset)progress('timezone',`Пояс браузера ${actual.zone} отличается от ПК; обязательна проверка выбранного пояса Studio.`);
 progress('timezone',`Время ПК: ${plan.date} ${plan.time} · ${zone} · UTC${plan.offset>=0?'+':''}${plan.offset/60}`);return zone;
}
async function visibility(page,scope,{progress=()=>{},readyMs=120000,transitionMs=30000}={}){
 for(let i=0;i<10;i++){
  // Re-evaluate BOTH the terminal screen and Next while the UI changes. Waiting
  // only for Next after its removal used to miss the final Visibility screen.
  const before=await until(async()=>{const state=await wizardState(scope);return state.stage==='visibility'||state.nextEnabled?state:null;},readyMs,'Studio не показал доступный следующий шаг или экран «Доступ».');
  if(before.stage==='visibility'){progress('studio_steps','Открыт экран «Доступ / Visibility»; «Далее» больше не требуется.');return;}
  progress('studio_steps','Подтверждён текущий шаг Studio; нажимаю «Далее / Next» один раз.');
  const next=await pickNextButton(scope);if(!await enabled(next))continue;
  await next.click();
  await until(async()=>{const state=await wizardState(scope);return state.stage==='visibility'||state.key!==before.key?state:null;},transitionMs,'Studio не подтвердил переход после «Далее». Повторные клики остановлены; проверьте обязательные поля и отчёт.');
 }
 throw new Error('Studio показал больше 10 шагов. Автоматический переход остановлен.');
}
function wallClockUnix(date,time,offset){
 if(parseDate(date)!==date||parseTime(time)!==time||!Number.isFinite(offset))throw new Error('Studio не показал корректные дату, время или часовой пояс.');
 return Date.parse(date+'T'+time+':00Z')/1000-offset*60;
}
async function scheduleValues(scope,plan){
 const lang=await scope.page().locator('html').getAttribute('lang')||'';
 const date=parseDate(await controlValue(await dateControl(scope)),lang),time=parseTime(await controlValue(await timeControl(scope)));
 const zone=await readZone(scope,plan);
 return {date,time,offset:zone.offset,zone:zone.label,unix:date&&time?wallClockUnix(date,time,zone.offset):null};
}
function scheduleMatches(actual,plan){return actual.date===plan.date&&actual.time===plan.time&&actual.offset===plan.offset&&actual.unix===plan.unix;}
async function assertSchedule(scope,plan){
 const got=await scheduleValues(scope,plan);
 if(!scheduleMatches(got,plan))throw new Error(`Studio изменил расписание перед подтверждением: ${got.date||'?'} ${got.time||'?'} UTC${got.offset/60}; ожидалось ${plan.date} ${plan.time} UTC${plan.offset/60}. Кнопка не нажата.`);
}
async function schedule(page,scope,plan,zone,{progress=()=>{},readyMs=120000,settleMs=600}={}){
 if(!await timeControl(scope,true)){
  const expand=await until(()=>scheduleCard(scope),10000,'Studio не показал карточку «Запланировать публикацию / Schedule».');
  await expand.click({timeout:5000});
 }
 await until(()=>timeControl(scope,true),10000,'Studio не открыл поле или карточку времени после выбора планирования');
 const privateLater=await one(scope.getByRole('radio',{name:/private.*(?:then|to).*public|сначала закрытый, потом открытый/i}),{optional:true});if(privateLater){await privateLater.click();await until(async()=>await privateLater.evaluate(e=>typeof e.checked==='boolean'?e.checked:e.getAttribute('aria-checked')==='true'||e.hasAttribute('checked')),4000,'Studio не подтвердил доступ после публикации');}
 await fillDate(scope,plan.date,{progress});await fillTime(scope,plan.time,{progress});
 await setZone(scope,plan,zone,{progress});
 // Changing a timezone may rerender date/time asynchronously. Repair only the
 // fields and verify a stable snapshot. Never reload Studio or upload again.
 for(let attempt=0;attempt<3;attempt++){
  await fillDate(scope,plan.date,{progress});await fillTime(scope,plan.time,{progress});
  let stableSince=0,reported=0;
  const result=await until(async()=>{
   const got=await scheduleValues(scope,plan);
   if(!scheduleMatches(got,plan))return {repair:got};
   if(!stableSince)stableSince=Date.now();if(Date.now()-stableSince<settleMs)return null;
   const past=await one(scope.getByText(/указать время в прошлом|time in the past|cannot specify a time in the past/i),{optional:true});
   if(past)throw new Error('YouTube отклоняет время как прошедшее: '+got.date+' '+got.time+' '+got.zone+'. Проверьте расписание в приложении.');
   if(plan.unix*1000<=Date.now())throw new Error('Время публикации наступило во время загрузки. Требуется новый слот; повторная передача видео не выполнялась.');
   const x=await actionButton(scope,'done-button',L.schedule);
   if(await enabled(x)&&L.schedule.test(norm(await x.innerText())))return {button:x,actual:got};
   if(Date.now()-reported>5000){progress('studio_schedule','Расписание проверено. Ожидаю доступную кнопку «Запланировать»; прошедшее время проверяется повторно.');reported=Date.now();}return null;
  },readyMs,'Кнопка «Запланировать» недоступна; повторная отправка остановлена.');
  if(result.button){progress('studio_schedule',`Проверено перед нажатием: ${result.actual.date} ${result.actual.time} UTC${result.actual.offset/60}. Кнопка «Запланировать» доступна.`);return result.button;}
  const got=result.repair;
  progress('studio_schedule',`Studio изменил поля после выбора пояса: ${got.date||'?'} ${got.time||'?'} UTC${got.offset/60}; восстанавливаю расписание (${attempt+1}/3).`);
  if(got.offset!==plan.offset)await setZone(scope,plan,zone,{progress});
 }
 throw new Error('Studio повторно сбрасывает дату/время после выбора пояса. Подтверждение остановлено, открытый черновик сохранён.');
}
async function publishSuccessVisible(page){return isPostPublishScreen(page);}
const CHECKS_STILL_RUNNING=/проверк(?:а|и) (?:видео )?(?:ещ[её] )?продолжается|video (?:check|review) (?:is )?still (?:in progress|running)|checks? (?:are )?still (?:in progress|running)/i;
async function runningChecksDialog(page){
 const locator=page.locator('ytcp-dialog,ytcp-prompt-dialog,ytcp-confirm-dialog,tp-yt-paper-dialog,[role=dialog]');
 const indices=await locator.evaluateAll((nodes,pattern)=>{
  const re=new RegExp(pattern,'i'),parent=e=>e?.parentElement||e?.getRootNode()?.host||null;
  const within=(a,b)=>{for(let p=parent(b);p;p=parent(p))if(p===a)return true;return false;};
  const visible=el=>{if(!el.getClientRects().length)return false;for(let p=el;p;p=parent(p)){const s=getComputedStyle(p);if(p.hidden||s.display==='none'||s.visibility==='hidden')return false;}return true;};
  const candidates=nodes.map((node,index)=>({node,index})).filter(x=>visible(x.node)&&re.test(x.node.innerText||''));
  // The upload dialog contains the warning. Nested custom hosts and their
  // role=dialog child represent ONE physical warning, not multiple dialogs.
  return candidates.filter(x=>!candidates.some(y=>x!==y&&within(x.node,y.node))).map(x=>x.index);
 },CHECKS_STILL_RUNNING.source);
 const matches=indices.map(i=>locator.nth(i));
 if(!matches.length)return false;
 if(matches.length>1)throw new Error('Studio: несколько окон «проверка видео продолжается».');
 return matches[0];
}
async function confirmPublishDespiteRunningChecks(page,progress=()=>{},state={clicked:false}){
 const dialog=await runningChecksDialog(page);if(!dialog)return false;
 if(state.clicked)return true;
 let publish=null;
 for(const btn of await dialog.getByRole('button',{name:/^(Publish|Опубликовать|Schedule|Запланировать)$/i}).all()){
  if(!await btn.isVisible()||!/^(Publish|Опубликовать|Schedule|Запланировать)$/i.test(norm(await btn.innerText())))continue;
  if(await enabled(btn)){publish=btn;break;}
 }
 if(!publish)return true; // A disabled confirmation is a wait, not a failed upload.
 state.clicked=true; // latch BEFORE the click: do not retry an uncertain action.
 progress('studio_publish_confirm','Studio: проверка видео ещё идёт — один раз подтверждаю публикацию в предупреждении.');
 await publish.click({timeout:10000});
 return true;
}
async function waitSaveConfirmed(page,scope,{timeout=180000,progress=()=>{},expectedId=''}={}){
 const end=Date.now()+timeout,confirmation={clicked:false};let reported=0;
 while(Date.now()<end){
  // Actions may take 10s. Keep them outside the 5s read-only page probe.
  const warning=await bounded(()=>runningChecksDialog(page),5000,'Studio не ответил при проверке предупреждения публикации.');
  if(warning)await confirmPublishDespiteRunningChecks(page,progress,confirmation);
  else {
   const state=await bounded(()=>publicationState(page,{scope,expectedId}),5000,'Studio не ответил при проверке завершения публикации.');
   if(state.state!=='pending'){
    progress('studio_publish',state.state==='scheduled'?'Studio показал экран «Видео запланировано».':state.state==='published'?'Studio показал экран «Видео опубликовано».':'Окно загрузки исчезло; проверяю сохранение существующего ID.');return state.state;
   }
  }
  if(Date.now()-reported>5000){progress('studio_publish','Жду экран завершения Studio (без повторного нажатия «Опубликовать»).');reported=Date.now();}
  await pause(150);
 }
 const error=new Error('Studio не показал подтверждение сохранения. Проверяю существующий ID, без повторной публикации.');error.code='STUDIO_SAVE_TIMEOUT';throw error;
}
async function publishPublic(page,scope){
 let publicRadio=await one(scope.locator('#public-radio-button,[name=PUBLIC],tp-yt-paper-radio-button[name=PUBLIC]').or(scope.getByRole('radio',{name:L.public})),{optional:true});
 if(!publicRadio){
  const label=await one(scope.getByText(/^(Public|Открытый доступ|Доступно всем)$/i),{optional:true});
  if(label)await label.click();else throw new Error('Studio: не найден режим «Открытый доступ»');
 }else await publicRadio.click();
 const button=await until(async()=>{
  const x=await actionButton(scope,'done-button',/^(Publish|Опубликовать|Save|Сохранить|Done|Готово)$/i);
  if(!await enabled(x))return null;
  const label=norm(await x.innerText());
  if(L.schedule.test(label))return null;
  return x;
 },120000,'Кнопка публикации Shorts недоступна');
 return button;
}
function videoId(value){return String(value||'').match(/(?:youtu\.be\/|[?&]v=|\/shorts\/|\/video\/)([A-Za-z0-9_-]{11})(?:\b|\/|$)/)?.[1]||'';}
async function readVideoId(scope){const ids=await scope.locator('a[href],input[value]').evaluateAll(nodes=>nodes.map(e=>e.getAttribute('href')||e.getAttribute('value')||''));const unique=[...new Set(ids.map(videoId).filter(Boolean))];if(unique.length!==1)throw new Error('Studio не показал однозначный ID загруженного ролика');return unique[0];}
async function visibilityControl(page){
 for(const selector of ['#visibility-button','#visibility','ytcp-video-visibility-select']){
  const control=await one(page.locator(selector),{optional:true});
  if(control&&await control.isVisible())return control;
 }
 for(const button of await page.getByRole('button',{name:/scheduled|запланирован|visibility|доступ/i}).all())
  if(await button.isVisible())return button;
 return null;
}
async function readback(page,item,id,plan,opts,zone){
 const target=`https://studio.youtube.com/video/${id}/edit`;
 if(!opts.reuseReadback||page.url()!==target)await page.goto(target,{waitUntil:'commit',timeout:45000});
 const scope=page.locator('ytcp-video-metadata-editor,ytcp-video-details,main').first();
 await until(()=>field(scope,'title',true),30000,'Не открылся редактор загруженного видео');
 await details(page,scope,item,opts,{verify:true});
 const control=await until(()=>visibilityControl(page),30000,'Studio не показал статус доступа на странице редактирования');
 const summary=norm(await control.innerText())+' '+(await control.getAttribute('aria-label')||'');
 if(!plan&&L.public.test(summary.trim()))return {visibility:'public'};
 await control.click();
 const dialog=await until(()=>one(page.locator('ytcp-video-visibility-edit-dialog,ytcp-video-visibility-select-dialog,ytcp-visibility-dialog,[role=dialog]'),{optional:true}),15000,'Studio не открыл окно доступа при проверке сохранения');
 if(!plan){
  const radio=await one(dialog.locator('[name=PUBLIC]').or(dialog.getByRole('radio',{name:L.public})),{optional:true});
  const selected=radio&&await radio.evaluate(e=>typeof e.checked==='boolean'?e.checked:e.getAttribute('aria-checked')==='true'||e.hasAttribute('checked'));
  if(!selected)throw new Error('Studio не подтвердил сохранённый «Открытый доступ» для Shorts. Повторная публикация заблокирована.');
  await page.keyboard.press('Escape');return {visibility:'public'};
 }
 const got=await scheduleValues(dialog,plan);
 if(got.unix!==plan.unix)throw new Error(`После сохранения дата/время Studio не совпали с моментом публикации: ${got.date||'?'} ${got.time||'?'} UTC${got.offset/60}; ожидалось ${plan.date} ${plan.time} UTC${plan.offset/60}`);
 // Studio may label a scheduled video Private until it becomes public. The
 // visible scheduling dialog, its date/time and zone prove the saved instant.
 await page.keyboard.press('Escape');
 if(item.thumbnail&&!isShorts(item))await require('./youtube-thumbnail').verifyThumbnail(page,item.thumbnail);
 return {visibility:'scheduled',schedule:got};
}
async function waitUpload(page,{stallMs=1200000,unknownMs=30000,progress=()=>{}}={}){
 const scope=await uploadScope(page);if(!scope)throw new Error('Studio не показал активный диалог загрузки');let last='',moved=Date.now();
 let reported=0,unknownSince=Date.now();
 while(true){
  if(page.isClosed()||!await scopeAlive(scope))throw new Error('Studio закрыл окно до подтверждения загрузки');
  const labels=await bounded(async()=>{
   const values=[];for(const status of await scope.locator('ytcp-video-upload-progress,#progress-label,[class*=progress-label]').all())
    if(await status.isVisible())values.push(await status.innerText());return values;
  },5000,'Вкладка Studio перестала отвечать во время загрузки');
  const text=labels.map(norm).join(' ');
  if(text)unknownSince=Date.now();
  if(text!==last||Date.now()-reported>5000){progress('upload_transfer',text?'Studio: '+text.slice(0,240):'Ожидаю индикатор передачи файла Studio.');reported=Date.now();}
  if(/(?:upload(?:ed)?|загружено)[^%]{0,30}100\s*%|upload complete|загрузка завершена|checks? (?:complete|running)|checking|проверка|обработка завершена/i.test(text)){progress('upload_transfer','Studio подтвердил передачу файла.');return;}
  if(!text&&Date.now()-unknownSince>unknownMs)throw new Error(`Studio не показал распознаваемый индикатор передачи за ${Math.ceil(unknownMs/1000)} секунд. Сохраните диагностику; отсутствие индикатора не считается успехом.`);
  if(text!==last){last=text;moved=Date.now();}
  if(Date.now()-moved>stallMs)throw new Error('Studio не подтвердил завершение передачи файла. Проверьте открытый профиль.');
  await pause(500);
 }
}
async function diagnostics(page,error,folder){
 const path=require('path');fs.mkdirSync(folder,{recursive:true});
 const data={editorFailure:error.editorDiagnostic||null,build:BUILD,time:new Date().toISOString(),error:String(error.message||error),flow:traces.get(page)||null,url:page&&!page.isClosed()?new URL(page.url()).origin+new URL(page.url()).pathname:''};
 const file=path.join(folder,'youtube-studio-'+Date.now()+'-'+process.pid+'.json');
 const write=()=>fs.writeFileSync(file,JSON.stringify(data,null,2));write();
 async function capture(name,work){try{data[name]=await bounded(work,1800,'Срок получения '+name+' истёк.');}catch(error){data.captureErrors=data.captureErrors||[];data.captureErrors.push({part:name,error:safeLog(error.message)});}write();}
 if(page&&!page.isClosed()&&/studio\.youtube\.com/.test(page.url())){
  await Promise.all([
   capture('controls',()=>page.locator('button,ytcp-button,[role=button],[role=radio],[role=option][aria-selected=true],input[type=date],input[type=time]').evaluateAll(nodes=>nodes.filter(e=>e.getClientRects().length).map(e=>({tag:e.tagName,id:e.id,role:e.getAttribute('role'),label:(e.getAttribute('aria-label')||e.innerText||'').slice(0,100),checked:e.getAttribute('aria-checked'),disabled:e.getAttribute('aria-disabled')})))),
   capture('publication',async()=>{const terminal=await publicationState(page);const close=terminal.surface?await findPublishCloseButton(page,{scope:terminal.surface}):null;return {state:terminal.state,ids:terminal.ids,closePresent:!!close,closeEnabled:close?await enabled(close):false};}),
   capture('fields',()=>page.locator('#title-textarea,#description-textarea,[contenteditable=true],textarea[name=description],input[name=title]').evaluateAll(inspectEditors)),
   capture('scheduleControls',()=>page.locator('#datepicker-trigger,#time-of-day-trigger,ytcp-date-picker input,ytcp-datepicker input,ytcp-time-of-day-picker input,#time-field input').evaluateAll(nodes=>nodes.filter(e=>e.getClientRects().length).map(e=>({tag:e.tagName,id:e.id,label:e.getAttribute('aria-label')||'',value:e.value||e.innerText||'',readOnly:e.readOnly===true})))),
   capture('timezones',async()=>zoneDiagnostics(await uploadScope(page)||page.locator('body'))),
   capture('dialogs',()=>page.locator(UPLOAD_DIALOG).evaluateAll(nodes=>nodes.map(e=>({tag:e.tagName,visible:!!e.getClientRects().length,next:e.querySelectorAll('#next-button').length,fields:e.querySelectorAll('[contenteditable=true]').length})))),
   capture('wizard',async()=>{const scope=await uploadScope(page);return scope?wizardState(scope):null;})
  ]);
  if(typeof page.screenshot==='function')try{const png=file.replace(/\.json$/,'.png');await page.screenshot({path:png,timeout:3000});data.screenshot=png;}catch(error){data.screenshotError=safeLog(error.message);}write();
 }
 return file;
}
function titleMatches(item,value){const path=require('path');return [item.title,path.basename(item.video||'',path.extname(item.video||''))].some(x=>norm(x)&&norm(x)===norm(value));}
async function probeDraft(page){
 const scope=await uploadScope(page);if(!scope)return {scope:null,id:'',title:''};
 const title=await field(scope,'title',true);
 return {scope,id:await readVideoId(scope).catch(()=>''),title:title?await read(title):''};
}
async function selectTaskPage(context,pack,progress=()=>{}){
 const pending=pack.find(item=>item.studioPending||item.studioVideoId),matches=[],idle=[];
 for(const page of context.pages()){
  if(page.isClosed()||!/^https:\/\/studio\.youtube\.com(?:\/|$)/i.test(page.url()))continue;
  try{
   const draft=await bounded(()=>probeDraft(page),4000,'Studio: вкладка не ответила при поиске черновика.');
   if(pending){
    const terminal=await bounded(()=>publicationState(page),4000,'Studio: не удалось прочитать экран завершения.');
    if(pending.studioVideoId?(draft.id===pending.studioVideoId||terminal.ids.includes(pending.studioVideoId)):(draft.scope&&titleMatches(pending,draft.title)))matches.push(page);
   }
   if(!draft.scope)idle.push(page);
  }catch(error){progress('studio_recovery','Не удалось прочитать одну из вкладок Studio: '+error.message);}
 }
 if(matches.length>1)throw new Error('Studio: найдено несколько вкладок одного черновика. Оставьте открытой одну вкладку нужного ролика и повторите запуск.');
 if(matches.length){progress('studio_recovery','Продолжаю существующую вкладку нужного ролика.');return matches[0];}
 if(!pending&&idle.length)return idle[0];
 const page=await bounded(()=>context.newPage(),15000,'Dolphin не открыл вкладку задания за 15 секунд.');taskOwnedPages.add(page);return page;
}
async function acceptIdentity(item,id,packIndex,host,progress){
 if(item.studioVideoId&&item.studioVideoId!==id)throw new Error('Studio: ID открытого черновика не совпадает с сохранённым заданием.');
 if(host.accepted)await host.accepted(item,packIndex,id);
 else progress('item_accepted','Studio создал видео, сохраняю ID',{videoId:id,localJobId:item.localJobId});
 item.studioVideoId=id;item.studioPending=true;if(item.studioPhase!=='publishing')item.studioPhase='editing';
}
async function recoverUncertainPending(page,item,host,progress,packIndex){
 if(!item.studioPending&&!item.studioVideoId)return {resumeScope:null};
 const draft=await probeDraft(page);
 if(draft.scope&&(draft.title||(item.studioVideoId&&draft.id===item.studioVideoId))){
  if(item.studioVideoId?draft.id!==item.studioVideoId:!titleMatches(item,draft.title))throw new Error('Studio: открыт другой черновик. Нужный файл не передавался.');
  const id=draft.id||await until(async()=>await readVideoId(draft.scope).catch(()=>null),30000,'В открытом черновике ещё нет ссылки. Дождитесь ссылки на видео и повторите запуск.');
  await acceptIdentity(item,id,packIndex,host,progress);
  progress('studio_recovery','Продолжаю сохранённый черновик · '+id);
  return item.studioPhase==='publishing'?{resumePublication:draft.scope}:{resumeScope:draft.scope};
 }
 if(item.studioVideoId)return {verifyOnly:true,scope:draft.scope};
 throw new Error('Не найден открытый черновик прежней попытки. В этом Dolphin-профиле откройте в Studio → Контент нужный черновик «'+item.title+'» и повторите запуск в приложении. Приложение подхватит его ID; новый файл пока не передавался.');
}
async function saveEditor(page,progress){
 const button=await actionButton(page,'save-button',/^(Save|Сохранить)$/i)||await actionButton(page,'save',/^(Save|Сохранить)$/i);
 if(await enabled(button)){progress('studio_draft_save','Сохраняю изменения существующего видео.');await button.click({timeout:10000});await until(async()=>!await enabled(button),15000,'Studio не завершил сохранение изменений черновика.');}
}
async function runSequential(page,pack,opts,host){
 const monitor=tracePage(page,{...opts,itemCount:pack.length}),timing={};let current='',began=Date.now();
 const closeTiming=()=>{if(current)timing[current]=(timing[current]||0)+Date.now()-began;began=Date.now();};
 const wrapped={...host,tracePage:monitor.watch,usePage:p=>{monitor.watch(p);if(host.usePage)host.usePage(p);},progress:(stage,text,extra)=>{if(stage!==current){closeTiming();current=stage;}monitor.trace.lastStage=stage;monitor.trace.stageLog.push(monitor.record('stage',{stage,text:safeLog(text),...extra}));host.progress(stage,text,extra);}};
 for(const [name,stage] of [['accepted','item_accepted'],['publishing','item_publishing'],['confirmed','item_confirmed']])if(host[name])wrapped[name]=async(...args)=>{monitor.record('durable_begin',{stage,packIndex:args[1],localJobId:args[0].localJobId});const result=await host[name](...args);monitor.record('durable_ack',{stage,packIndex:args[1],videoId:args[0].studioVideoId||''});return result;};
 if(monitor.trace.logFile)host.progress('studio_diagnostics','Полный журнал запуска: '+monitor.trace.logFile);
 try{const result=await runItems(page,pack,opts,wrapped);monitor.record('run_success',{items:result.length});return result;}
 catch(error){monitor.record('run_error',{error:safeLog(error.message),editorFailure:error.editorDiagnostic||null,lastStage:monitor.trace.lastStage});throw error;}
 finally{closeTiming();monitor.trace.stageDurationsMs=timing;monitor.record('timing',{stageDurationsMs:timing});host.progress('studio_timing','Время этапов (сек.): '+Object.entries(timing).map(([s,ms])=>s+'='+Math.round(ms/100)/10).join('; '));if(monitor.trace.logError)host.progress('diagnostic','Не удалось записать полный журнал: '+monitor.trace.logError);monitor.dispose();}
}
async function runItems(page,pack,opts,host){
 const results=[];
 // Preflight all items before uploading even the first file.
 pack.forEach(item=>{
  if(!fs.existsSync(item.video))throw new Error('Не найден файл '+item.video);
  if(!norm(item.title)||item.title.length>100||/[<>]/.test(item.title))throw new Error('Некорректный заголовок');
  if(opts.writeTags===true&&item.tags!=null)validateTags(tags(item.tags));
  if(opts.writeDescription!==false&&item.description&&/[<>]/.test(item.description))throw new Error('Описание не должно содержать < или >');
  if(opts.writeDescription!==false&&item.description?.length>5000)throw new Error('Описание превышает 5000 символов');
  if(!isImmediatePublish(item))planFor(item);
 });
 const known=pack.map(item=>item.studioVideoId).filter(Boolean);if(new Set(known).size!==known.length)throw new Error('Studio: один сохранённый ID указан у нескольких заданий. Проверьте очередь, новые файлы не передавались.');
 const usedIds=new Set();
 const editorReadyMs=opts.editorReadyMs??90000;
 async function verifySaved(item,id,plan,zone,progress){
  progress('studio_verify','Проверяю сохранённые название, описание и '+(plan?'расписание':'открытый доступ')+' по ID '+id+'.');
  const verify=await bounded(()=>page.context().newPage(),15000,'Не открылась вкладка проверки Studio');
  if(host.tracePage)host.tracePage(verify);
  try{
   if(plan)await syncClock(verify,plan,host.progress);
   for(let attempt=0;attempt<2;attempt++){
    try{return await readback(verify,item,id,plan,opts,zone);}
    catch(error){if(attempt||verify.isClosed())throw error;progress('studio_verify','Сохранённые данные ещё не подтверждены. Повторяю только чтение существующего ID (2/2).');await pause(600);}
   }
  }
  finally{await bounded(()=>verify.close(),2000,'Не закрылась вкладка проверки').catch(()=>{});}
 }

 for(let i=0;i<pack.length;i++){
  const item=pack[i],immediate=isImmediatePublish(item),packIndex=item._packIndex||i+1;
  let plan=null,zone=null;
  if(!immediate){plan=planFor(item);zone=await syncClock(page,plan,host.progress);}
  else host.progress('upload_mode','Shorts: сразу в открытый доступ, без превью и расписания.',{packIndex});
  host.progress('upload',`Studio: ролик ${i+1}/${pack.length}: ${item.title}`,{packIndex});
  const progress=(stage,text,extra={})=>host.progress(stage,text,{packIndex,...extra});
  const recovery=await recoverUncertainPending(page,item,host,progress,packIndex);
  async function finish(id,scope,screenConfirmed=false){
   if(opts.verifyPersisted===true||!screenConfirmed)await verifySaved(item,id,plan,zone,progress);
   else progress('studio_publish_confirmed','Studio показал завершение публикации нужного ролика.',{videoId:id});
   const result={url:`https://www.youtube.com/watch?v=${id}`,videoId:id,packIndex,visibility:immediate?'public':'scheduled',schedule:plan};
   const text=immediate?`Studio подтвердил открытый доступ · ${id}`:`Studio подтвердил расписание ${plan.date} ${plan.time}`;
   if(host.confirmed)await host.confirmed(item,packIndex,result,text);else host.progress('item_confirmed',text,result);
   item.studioPhase='confirmed';item.studioPending=false;results.push(result);usedIds.add(id);
   if(opts.newTabPerVideo){
    progress('studio_ready','Видео подтверждено. Следующий ролик откроется в новой вкладке.');
    if(i+1<pack.length){
     const previous=page;page=await bounded(()=>page.context().newPage(),15000,'Не открылась вкладка следующего ролика.');taskOwnedPages.add(page);if(host.usePage)host.usePage(page);
     progress('studio_new_tab','Открыта новая вкладка загрузки следующего ролика.');
     await require('./studio-browser').openStudio(page,opts.channelUrl||'',progress);
     if(taskOwnedPages.has(previous))await bounded(()=>previous.close(),2000,'Не закрылась завершённая вкладка.').catch(error=>progress('diagnostic',error.message));
    }
    return;
   }
   await dismissPublishDialog(page,progress,{scope,expectedId:id,confirmed:true,timeout:opts.closeTimeout??90000});
   progress('studio_ready','Предыдущий мастер закрыт. Можно переходить к следующему ролику.');
  }
  let editorOnly=false,scope=recovery.resumeScope;
  if(recovery.resumePublication){
   const saved=await waitSaveConfirmed(page,recovery.resumePublication,{progress,expectedId:item.studioVideoId,timeout:opts.saveTimeout??180000});
   await finish(item.studioVideoId,recovery.resumePublication,saved==='published'||saved==='scheduled');continue;
  }
  if(recovery.verifyOnly){
   const id=item.studioVideoId;if(!/^[A-Za-z0-9_-]{11}$/.test(id))throw new Error('Некорректный сохранённый ID. Повторная передача заблокирована.');
   if(item.studioPhase!=='editing'){await finish(id,recovery.scope);continue;}
   await page.goto(`https://studio.youtube.com/video/${id}/edit`,{waitUntil:'commit',timeout:45000});
   scope=page.locator('ytcp-video-metadata-editor,ytcp-video-details,main').first();
   await until(()=>field(scope,'title',true),editorReadyMs,'Не открылся сохранённый черновик '+id);
   const control=await until(()=>visibilityControl(page),30000,'Studio не показал доступ сохранённого черновика.');
   if(!/^(?:Private|Unlisted|Ограниченный доступ|Закрытый доступ|Доступ по ссылке)/i.test(norm(await control.innerText()))){await finish(id,recovery.scope);continue;}
   editorOnly=true;progress('studio_recovery','Продолжаю редактирование сохранённого ID '+id+' без передачи файла.');
  }
  if(!scope){
   try{await host.openFile(page,item.video,()=>host.checkpoint(item,packIndex));}
   catch(error){
    const draft=await probeDraft(page).catch(()=>null);
    if(!draft?.scope||!draft.id||!titleMatches(item,draft.title))throw error;
    scope=draft.scope;await acceptIdentity(item,draft.id,packIndex,host,progress);progress('studio_recovery','Ответ передачи потерян; продолжаю уже созданный ID '+draft.id+'.');
   }
   host.progress('studio_editor','Файл передан; ищу активное окно и редактируемое поле названия.',{packIndex});
   scope=scope||await until(()=>uploadScope(page),editorReadyMs,'Studio принял файл, но не показал окно редактирования. Проверьте черновик или подождите на медленном прокси.');
  }else host.progress('studio_editor','Продолжаю с уже открытого окна редактирования.',{packIndex});
  await until(()=>field(scope,'title',true),editorReadyMs,'Не появилось поле названия');
  const captureIdentity=async()=>{if(item.studioVideoId)return;const id=await readVideoId(scope).catch(()=>null);if(id)await acceptIdentity(item,id,packIndex,host,progress);};
  await captureIdentity();
  host.progress('metadata','Удаляю название файла и печатаю заголовок из приложения.',{packIndex});
  const metadataAbort=new AbortController();
  try{await bounded(()=>details(page,scope,item,{...opts,progress,captureIdentity,signal:metadataAbort.signal}),metadataBudget(item,opts),'Studio не завершил ввод метаданных в рассчитанное время');}
  catch(error){await captureIdentity().catch(()=>{});throw error;}
  finally{metadataAbort.abort();}
  progress('upload_transfer','Проверяю завершение передачи файла.');if(!editorOnly)await host.waitUpload(page,{progress});
  // The video link may appear only after processing starts. Do not block typing on it.
  const id=item.studioVideoId||await until(async()=>{try{return await readVideoId(scope);}catch{return null;}},60000,'YouTube не показал ID ролика');
  if(usedIds.has(id))throw new Error('Studio повторно открыл предыдущее видео вместо нового');usedIds.add(id);
  if(!item.studioVideoId)await acceptIdentity(item,id,packIndex,host,progress);
  if(!isShorts(item)&&item.thumbnail)await host.thumbnail(page,item.thumbnail,{scope});
  if(editorOnly){
   await saveEditor(page,progress);const control=await visibilityControl(page);await control.click();
   scope=await until(()=>one(page.locator('ytcp-video-visibility-edit-dialog,ytcp-video-visibility-select-dialog,ytcp-visibility-dialog'),{optional:true}),15000,'Studio не открыл доступ существующего видео.');
  }else{progress('studio_steps','Перехожу через «Далее / Next» к доступу.');await visibility(page,scope,{progress});}
  let submit;
  if(immediate){
   progress('studio_publish','Публикую Shorts в открытый доступ.');
   submit=await publishPublic(page,scope);
  }else{
   progress('studio_schedule','Устанавливаю дату, время и часовой пояс.');
   submit=await schedule(page,scope,plan,zone,{progress});
   planFor(item);
  }
  if(!immediate)await assertSchedule(scope,plan);
  if(host.publishing)await host.publishing(item,packIndex,id);
  item.studioPhase='publishing';
  progress(immediate?'studio_publish':'studio_schedule',immediate?'Нажимаю кнопку публикации один раз.':'Нажимаю «Запланировать публикацию» один раз.');
  await submit.click({timeout:10000});
  let savedState='';
  try{savedState=await waitSaveConfirmed(page,scope,{progress,expectedId:id,timeout:opts.saveTimeout??180000});}
  catch(error){if(error.code!=='STUDIO_SAVE_TIMEOUT')throw error;progress('studio_verify',error.message);}
  if(editorOnly)await saveEditor(page,progress);
  // Normal path ends on Studio's completion screen. Open a read-only editor
  // only after an uncertain result, or when explicitly requested for diagnostics.
  try{await finish(id,scope,savedState==='published'||savedState==='scheduled');}
  catch(error){throw new Error((item.studioPhase==='confirmed'?'Видео '+id+' уже подтверждено; не удалось подготовить следующую загрузку. ':'Не подтверждено завершение видео '+id+'. Сохранённый ID остаётся в приложении. ')+error.message);}

 }
 return results;
}
module.exports={fillTime,dateControl,timeControl,calendarSurface,actionButton,enabled,BUILD,L,one,until,norm,read,field,editorState,activateEditor,typeText,typingBudget,metadataBudget,tags,validateTags,setTags,question,details,showMore,uploadScope,scopeAlive,parseDate,parseTime,fillDate,offsetLabel,zoneControl,readZone,setZone,closeZoneMenu,zoneDiagnostics,wallClockUnix,scheduleValues,assertSchedule,planFor,syncClock,schedule,publishPublic,publishSuccessVisible,confirmPublishDespiteRunningChecks,runningChecksDialog,waitSaveConfirmed,visibility,wizardState,visibilityControl,readback,runSequential,selectTaskPage,videoId,waitUpload,diagnostics,isImmediatePublish,isShorts};
