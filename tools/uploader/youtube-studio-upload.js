"use strict";
// Native Studio UI only. No metadata/publish HTTP endpoints and no clipboard/DOM text writes.
const fs=require('fs');
const {bounded}=require('./studio-browser');
const UPLOAD_DIALOG='ytcp-video-upload-dialog,ytcp-uploads-dialog,ytcp-upload-dialog';
const BUILD='2026-10-21-studio-publish-v5';
const traces=new WeakMap();
function tracePage(page){
 const trace={build:BUILD,lastStage:'preflight',navigations:[],networkErrors:[]};traces.set(page,trace);
 const navigation=frame=>{if(frame!==page.mainFrame())return;try{const u=new URL(frame.url());trace.navigations.push({time:new Date().toISOString(),host:u.hostname,path:u.pathname});trace.navigations=trace.navigations.slice(-12);}catch{}};
 const failed=request=>{try{const u=new URL(request.url());if(!/^(studio\.youtube\.com|upload\.youtube\.com|i\.ytimg\.com)$/.test(u.hostname))return;trace.networkErrors.push({host:u.hostname,type:request.resourceType(),error:request.failure()?.errorText});trace.networkErrors=trace.networkErrors.slice(-12);}catch{}};
 page.on('framenavigated',navigation);page.on('requestfailed',failed);
 return {trace,dispose:()=>{page.off('framenavigated',navigation);page.off('requestfailed',failed);}};
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
function metadataBudget(item,opts){return 60000+typingBudget(item.title,opts.titleDelay??55)+typingBudget(opts.writeDescription===false?null:item.description,opts.descriptionDelay??18)+typingBudget(tags(item.tags).join(','),20);}
async function typeText(box,text,{delay=45,onProgress=()=>{}}={}){
 text=norm(text);await box.scrollIntoViewIfNeeded();await box.click();
 await box.focus();
 await box.press('Control+A');await box.press('Delete');
 await until(async()=>await read(box)==='',3000,'Studio не очистил прежний текст');
 const chars=Array.from(text);
 // Each keyboard action has its own native, cancellable timeout. A 10-second
 // context default must not truncate a 5000-character description midway.
 for(let start=0;start<chars.length;start+=80){
  const chunk=chars.slice(start,start+80).join('');
  await box.pressSequentially(chunk,{delay,timeout:typingBudget(chunk,delay)});
  onProgress(Math.min(start+80,chars.length),chars.length);
 }
 await box.press('Tab');
 await until(async()=>await read(box)===text,5000,'Studio не подтвердил введённый текст');
}
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
   const found=[];
   for(const heading of root.querySelectorAll('h1,h2,h3,h4,[role=heading],label,span,div,p')){
    const text=(heading.innerText||heading.textContent||'').trim();if(text.length>500||!mine.test(text))continue;
    for(let p=heading.parentElement;p&&p!==root;p=p.parentElement){
     const options=p.querySelectorAll('[role=radio],tp-yt-paper-radio-button,input[type=radio],[role=checkbox],tp-yt-paper-checkbox,input[type=checkbox]');
     if(!options.length)continue;if(other.test(p.innerText||'')||options.length>5)break;
     if(!found.includes(p))found.push(p);break;
    }
   }
   if(!found.length)return false;const min=Math.min(...found.map(e=>(e.innerText||'').length));const smallest=found.filter(e=>(e.innerText||'').length===min);if(smallest.length!==1)return false;
   root.querySelectorAll('[data-vb-question="'+kind+'"]').forEach(e=>e.removeAttribute('data-vb-question'));smallest[0].setAttribute('data-vb-question',kind);return true;
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
 if(!verify){report('metadata_title','Очищаю и печатаю заголовок.');await typeText(await field(scope,'title'),item.title,{delay:opts.titleDelay??55});report('metadata_title','Заголовок введён и проверен.');}
 else if(await read(await field(scope,'title'))!==norm(item.title))throw new Error('После перезагрузки изменился заголовок');
 if(opts.writeDescription!==false&&item.description!==null&&item.description!==undefined){
  const description=await field(scope,'description');
  if(!verify){report('metadata_description','Ввожу описание.');await typeText(description,item.description,{delay:opts.descriptionDelay??18,onProgress:(done,total)=>report('metadata_description',`Описание: ${done}/${total} символов.`)});}
  else if(await read(description)!==norm(item.description))throw new Error('После перезагрузки изменилось описание');
 }
 const kids=await one(scope.locator('[name=VIDEO_MADE_FOR_KIDS_NOT_MFK]').or(scope.getByRole('radio',{name:/not made for kids|не для детей/i})),{optional:true});
 if(kids&&!verify)await kids.click();
 report('metadata_options','Открываю настройки рекламы и ИИ.');await showMore(scope);
 // Expansion mounts these controls asynchronously, so a one-shot lookup is too early.
 await until(async()=>{
  const text=await scope.innerText();return L.paid.test(text)&&L.ai.test(text);
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
async function enabled(button){return button&&await button.evaluate(e=>{
 for(let p=e;p;p=p.parentElement||p.getRootNode().host){if(p.hasAttribute('disabled')||p.getAttribute('aria-disabled')==='true')return false;}
 return true;
});}
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
 const controls=[];
 for(const button of await scope.getByRole('button').all())if(await button.isVisible()&&!await button.evaluate(e=>!!e.closest('#next-button,#done-button,#stepper,ytcp-stepper,footer,ytcp-dialog-footer'))){
  const label=norm(await button.getAttribute('aria-label')||await button.innerText()).slice(0,80);if(!L.next.test(label))controls.push(label);
 }
 const visibility=!!(card||publicRadio||publishLabel);
 const next=await pickNextButton(scope);
 const state={stage:visibility?'visibility':panels.includes('#details')||panels.includes('ytcp-video-metadata-editor')?'details':'intermediate',panels,selected,stepper,controls:controls.slice(0,30),next:!!next,nextEnabled:!!await enabled(next)};
 state.key=JSON.stringify({stage:state.stage,panels,selected,stepper,controls:state.controls});return state;
}
const MONTHS=[['january','январ','янв'],['february','феврал','фев'],['march','марта','март','мар'],['april','апрел','апр'],['may','мая','май'],['june','июня','июнь','июн'],['july','июля','июль','июл'],['august','август','авг'],['september','сентябр','сен'],['october','октябр','окт'],['november','ноябр','ноя'],['december','декабр','дек']];
function parseDate(text){
 text=norm(text).toLowerCase();let m=text.match(/(\d{4})-(\d{2})-(\d{2})/);if(m)return `${m[1]}-${m[2]}-${m[3]}`;
 m=text.match(/\b(\d{1,2})[./](\d{1,2})[./](\d{4})\b/);if(m)return `${m[3]}-${m[2].padStart(2,'0')}-${m[1].padStart(2,'0')}`;
 let month=MONTHS.findIndex(names=>names.some(n=>text.includes(n)||text.includes(n.slice(0,3))));
 const year=text.match(/\b(20\d{2})\b/);const nums=text.replace(/20\d{2}/g,'').match(/\b\d{1,2}\b/g);
 if(month>=0&&year&&nums?.length===1)return `${year[1]}-${String(month+1).padStart(2,'0')}-${nums[0].padStart(2,'0')}`;return null;
}
function parseTime(text){let m=norm(text).match(/\b(\d{1,2}):(\d{2})\s*(am|pm)?\b/i);if(!m)return null;let h=Number(m[1]);if(m[3])h=(h%12)+(m[3].toLowerCase()==='pm'?12:0);if(h>23||Number(m[2])>59)return null;return `${String(h).padStart(2,'0')}:${m[2]}`;}
async function dateControl(scope){for(const selector of ['ytcp-datepicker-trigger','#datepicker-trigger','input[type=date]','input[aria-label*="date" i],input[aria-label*="дат" i]']){const x=await one(scope.locator(selector),{optional:true});if(x)return x;}throw new Error('Studio: не найден элемент даты');}
async function timeControl(scope){return one(scope.locator('input[type=time],ytcp-time-of-day-picker input,#time-of-day-container input,#time-field input,input[aria-label="Time" i],input[aria-label="Время" i]'));}
async function controlValue(control){return control.evaluate(e=>e.value||e.querySelector('input')?.value||e.innerText||e.textContent||'');}
async function fillDate(scope,date){
 const trigger=await dateControl(scope);
 if(parseDate(await controlValue(trigger))===date)return;
 const input=await trigger.evaluate(e=>e.tagName==='INPUT')?trigger:await one(trigger.locator('input'),{optional:true});
 if(input&&!await input.getAttribute('readonly')){
  const type=await input.getAttribute('type');const current=await read(input);
  const d=new Date(date+'T12:00:00Z');const value=type==='date'?date:/[а-я]/i.test(current)?d.toLocaleDateString('ru-RU',{day:'numeric',month:'short',year:'numeric',timeZone:'UTC'}):d.toLocaleDateString('en-US',{day:'numeric',month:'short',year:'numeric',timeZone:'UTC'});
  if(type==='date'){await input.fill(value);await input.press('Tab');}else await typeText(input,value,{delay:35});
  if(parseDate(await controlValue(trigger))===date)return;
 }
 await trigger.click();
 const calendar=await one(scope.page().locator('ytcp-date-picker:visible,ytcp-datepicker:visible,[role=dialog][aria-label*="calendar" i]:visible,[role=dialog][aria-label*="календар" i]:visible'));
 for(let step=0;step<36;step++){
  // Accessible full dates are independent of the site's language and month layout.
  const all=calendar.locator('[data-date],button[aria-label],[role=gridcell],td[aria-label]');
  const cells=[];for(const c of await all.all()){
   const value=await c.getAttribute('data-date')||await c.getAttribute('aria-label')||'';
   if(parseDate(value)===date&&await c.isVisible()&&!await c.isDisabled().catch(()=>false))cells.push(c);
  }
  if(cells.length===1){await cells[0].click();break;}
  const header=await one(calendar.locator('#month-label,#month,.month-label,.month-title,[data-calendar-month]'),{optional:true});
  if(!header)throw new Error('Studio: календарь не показал однозначные даты или месяц');
  const text=norm(await header.innerText()).toLowerCase();const year=text.match(/20\d{2}/);const month=MONTHS.findIndex(a=>a.some(x=>text.includes(x)));
  if(!year||month<0)throw new Error('Studio: не удалось прочитать месяц/год календаря');
  const target=Number(date.slice(0,4))*12+Number(date.slice(5,7))-1,current=Number(year[0])*12+month;
  if(target===current){
   const day=calendar.locator('[role=gridcell],td,button.day,.day').filter({hasText:new RegExp('^'+Number(date.slice(8))+'$')});
   const candidates=[];for(const c of await day.all())if(await c.isVisible()&&!(await c.getAttribute('class')||'').match(/outside|other.month|disabled/i)&&!await c.isDisabled().catch(()=>false))candidates.push(c);
   if(candidates.length!==1)throw new Error('Studio: день календаря неоднозначен');await candidates[0].click();break;
  }
  const button=await one(calendar.locator(target>current?'#next-month,#next-month-button':'#prev-month,#previous-month-button').or(calendar.getByRole('button',{name:target>current?/next month|следующ.*месяц/i:/previous month|предыдущ.*месяц/i})));
  await button.click();
 }
 await until(async()=>parseDate(await controlValue(trigger))===date,5000,'Studio не подтвердил выбранную дату');
}
function offsetLabel(text){const m=String(text).match(/(?:GMT|UTC)\s*([+−-])\s*(\d{1,2})(?::?(\d{2}))?/i);return m?(m[1]==='+'?1:-1)*(Number(m[2])*60+Number(m[3]||0)):/\b(?:GMT|UTC)\b/.test(text)?0:null;}
async function zoneValue(element){return element.evaluate(e=>{const value=e.value||e.getAttribute('data-value')||e.getAttribute('data-timezone')||e.getAttribute('value')||'';return typeof value==='string'?value:'';});}
async function zoneOffset(element,plan){
 const value=await zoneValue(element);
 if(value)try{const part=new Intl.DateTimeFormat('en',{timeZone:value,timeZoneName:'longOffset'}).formatToParts(new Date(plan.unix*1000)).find(x=>x.type==='timeZoneName');return offsetLabel(part.value);}catch{}
 return offsetLabel(await element.innerText());
}
async function setZone(scope,plan,zone){
 const button=await one(scope.getByRole('button',{name:L.zone}).or(scope.locator('#timezone-button, #time-zone-button')),{optional:true});
 if(!button)throw new Error('Studio: не найден выбор часового пояса, расписание не будет подтверждено');
 await button.click();
 const page=scope.page();const options=page.locator('[role=option],tp-yt-paper-item,ytcp-text-menu [role=menuitem]');
 await until(async()=>(await options.count())>0,5000,'Studio не показал список часовых поясов');
 const choices=[];
 for(const option of await options.all())if(await option.isVisible()){
  const text=norm(await option.innerText());const value=await zoneValue(option);
  if(value===zone)choices.unshift({option,text,exact:true});
  else if(await zoneOffset(option,plan)===plan.offset)choices.push({option,text,exact:false});
 }
 if(!choices.length)throw new Error('Studio не показал пояс ПК UTC'+(plan.offset>=0?'+':'')+plan.offset/60);
 const choice=choices.find(x=>x.exact)||choices[0];await choice.option.click();
 // Read back selection from reopening the menu: a click alone is not confirmation.
 await button.click();
 const selected=await one(page.locator('[role=option][aria-selected=true],tp-yt-paper-item[aria-selected=true],[role=option][selected],tp-yt-paper-item[selected]'),{optional:true});
 if(!selected)throw new Error('Studio не подтвердил выбранный часовой пояс');
 const value=await zoneValue(selected);
 const text=norm(await selected.innerText());
 if(value!==zone&&await zoneOffset(selected,plan)!==plan.offset)throw new Error('Studio сохранил другой часовой пояс');
 await page.keyboard.press('Escape');return {label:text,offset:plan.offset};
}
function planFor(item,now=Date.now()){
 const date=String(item.scheduleDate||''),time=String(item.scheduleTime||'');
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
async function schedule(page,scope,plan,zone){
 if(!await one(scope.locator('input[type=time],ytcp-time-of-day-picker input,#time-of-day-container input,#time-field input'),{optional:true})){
  const expand=await until(()=>scheduleCard(scope),10000,'Studio не показал карточку «Запланировать публикацию / Schedule».');
  await expand.click();
 }
 await until(async()=>await one(scope.locator('input[type=time],ytcp-time-of-day-picker input,#time-of-day-container input,#time-field input'),{optional:true}),10000,'Studio не открыл поля даты/времени после карточки планирования');
 const privateLater=await one(scope.getByRole('radio',{name:/private.*(?:then|to).*public|сначала закрытый, потом открытый/i}),{optional:true});if(privateLater){await privateLater.click();await until(async()=>await privateLater.evaluate(e=>typeof e.checked==='boolean'?e.checked:e.getAttribute('aria-checked')==='true'||e.hasAttribute('checked')),4000,'Studio не подтвердил доступ после публикации');}
 await fillDate(scope,plan.date);await setZone(scope,plan,zone);await fillDate(scope,plan.date);
 const time=await timeControl(scope);if(await time.getAttribute('type')==='time'){await time.fill(plan.time);await time.press('Tab');}else await typeText(time,plan.time,{delay:45});
 if(parseTime(await read(time))!==plan.time)throw new Error('Studio не подтвердил время');
 if(parseDate(await controlValue(await dateControl(scope)))!==plan.date)throw new Error('Studio не подтвердил дату');
 const button=await until(async()=>{const x=await actionButton(scope,'done-button',L.schedule);return await enabled(x)&&L.schedule.test(norm(await x.innerText()))?x:null;},120000,'Кнопка Запланировать недоступна');
 const label=norm(await button.innerText());if(!L.schedule.test(label))throw new Error('Studio предлагает «'+label+'» вместо планирования. Публикация остановлена.');
 return button;
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
async function readback(page,item,id,plan,opts,zone){
 await page.goto(`https://studio.youtube.com/video/${id}/edit`,{waitUntil:'domcontentloaded',timeout:90000});
 const scope=page.locator('ytcp-video-metadata-editor,ytcp-video-details,main').first();
 await until(()=>field(scope,'title',true),30000,'Не открылся редактор загруженного видео');
 await details(page,scope,item,opts,{verify:true});
 const visible=await one(page.locator('#visibility, #visibility-button, ytcp-video-visibility-select').or(page.getByRole('button',{name:/scheduled|запланирован/i})),{optional:true});
 if(!visible||!L.scheduled.test(norm(await visible.innerText())+' '+(await visible.getAttribute('aria-label'))))throw new Error('Studio не подтвердил статус «Запланировано» после перезагрузки');
 await visible.click();
 const dialog=await one(page.locator('ytcp-video-visibility-edit-dialog,ytcp-video-visibility-select-dialog,ytcp-visibility-dialog,[role=dialog]'));
 if(parseDate(await controlValue(await dateControl(dialog)))!==plan.date||parseTime(await read(await timeControl(dialog)))!==plan.time)throw new Error('После сохранения дата/время Studio не совпали с расписанием');
 // Verify zone in the persisted visibility dialog without changing it.
 const zoneButton=await one(dialog.getByRole('button',{name:L.zone}).or(dialog.locator('#timezone-button,#time-zone-button')));await zoneButton.click();
 const selected=await one(page.locator('[role=option][aria-selected=true],tp-yt-paper-item[aria-selected=true],[role=option][selected],tp-yt-paper-item[selected]'));
 const value=await zoneValue(selected);
 if(value!==zone&&await zoneOffset(selected,plan)!==plan.offset)throw new Error('После сохранения изменился часовой пояс');
 await page.keyboard.press('Escape');await page.keyboard.press('Escape');
 if(item.thumbnail)await require('./youtube-thumbnail').verifyThumbnail(page,item.thumbnail);
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
 const data={build:BUILD,time:new Date().toISOString(),error:String(error.message||error),flow:traces.get(page)||null,url:page&&!page.isClosed()?new URL(page.url()).origin+new URL(page.url()).pathname:''};
 if(page&&!page.isClosed()&&/studio\.youtube\.com/.test(page.url())){
  data.controls=await page.locator('button,ytcp-button,[role=button],[role=radio],[role=option][aria-selected=true],input[type=date],input[type=time]').evaluateAll(nodes=>nodes.filter(e=>e.getClientRects().length).map(e=>({tag:e.tagName,id:e.id,role:e.getAttribute('role'),label:(e.getAttribute('aria-label')||e.innerText||'').slice(0,100),checked:e.getAttribute('aria-checked'),disabled:e.getAttribute('aria-disabled')})).slice(0,100));
  data.fields=await page.locator('#title-textarea,#description-textarea,[contenteditable=true]').evaluateAll(nodes=>nodes.map(e=>({tag:e.tagName,id:e.id,visible:!!e.getClientRects().length,editable:e.isContentEditable,role:e.getAttribute('role'),length:(e.innerText||'').length,focused:e===document.activeElement||e.contains(document.activeElement)})));
  data.dialogs=await page.locator(UPLOAD_DIALOG).evaluateAll(nodes=>nodes.map(e=>({tag:e.tagName,visible:!!e.getClientRects().length,next:e.querySelectorAll('#next-button').length,fields:e.querySelectorAll('[contenteditable=true]').length})));
  const scope=await uploadScope(page);if(scope)data.wizard=await wizardState(scope);
 }
 const file=path.join(folder,'youtube-studio-'+Date.now()+'-'+process.pid+'.json');fs.writeFileSync(file,JSON.stringify(data,null,2));return file;
}
async function runSequential(page,pack,opts,host){
 const monitor=tracePage(page);try{return await runItems(page,pack,opts,{...host,progress:(stage,text,extra)=>{monitor.trace.lastStage=stage;host.progress(stage,text,extra);}});}finally{monitor.dispose();}
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
 const usedIds=new Set();
 for(let i=0;i<pack.length;i++){
  const item=pack[i],immediate=isImmediatePublish(item),packIndex=item._packIndex||i+1;
  let plan=null,zone=null;
  if(!immediate){plan=planFor(item);zone=await syncClock(page,plan,host.progress);}
  else host.progress('upload_mode','Shorts: сразу в открытый доступ, без превью и расписания.',{packIndex});
  host.progress('upload',`Studio: ролик ${i+1}/${pack.length}: ${item.title}`,{packIndex});
  await host.openFile(page,item.video,()=>host.checkpoint(item,packIndex));
  host.progress('studio_editor','Файл передан; ищу активное окно и редактируемое поле названия.',{packIndex});
  const scope=await until(()=>uploadScope(page),25000,'Studio принял файл, но не показал окно редактирования за 25 секунд. Проверьте черновик перед повтором.');
  await until(()=>field(scope,'title',true),25000,'Не появилось поле названия');
  host.progress('metadata','Удаляю название файла и печатаю заголовок из приложения.',{packIndex});
  const progress=(stage,text,extra={})=>host.progress(stage,text,{packIndex,...extra});
  await bounded(()=>details(page,scope,item,{...opts,progress}),metadataBudget(item,opts),'Studio не завершил ввод метаданных в рассчитанное время');
  progress('upload_transfer','Проверяю завершение передачи файла.');await host.waitUpload(page,{progress});
  // The video link may appear only after processing starts. Do not block typing on it.
  const id=await until(async()=>{try{return await readVideoId(scope);}catch{return null;}},60000,'YouTube не показал ID ролика');
  if(usedIds.has(id))throw new Error('Studio повторно открыл предыдущее видео вместо нового');usedIds.add(id);
  host.progress('item_accepted','Studio создал видео, сохраняю ID',{packIndex,videoId:id,localJobId:item.localJobId});
  if(!immediate&&item.thumbnail)await host.thumbnail(page,item.thumbnail,{scope});
  progress('studio_steps','Перехожу через «Далее / Next» к доступу.');await visibility(page,scope,{progress});
  let submit;
  if(immediate){
   progress('studio_publish','Публикую Shorts в открытый доступ.');
   submit=await publishPublic(page,scope);
  }else{
   progress('studio_schedule','Устанавливаю дату, время и часовой пояс.');
   submit=await schedule(page,scope,plan,zone);
   planFor(item);
  }
  await submit.click();
  await until(async()=>!await scopeAlive(scope)||await one(page.locator('ytcp-video-share-dialog,ytcp-video-upload-success'),{optional:true}),120000,'YouTube не подтвердил сохранение. Проверьте Studio; повторная загрузка заблокирована.');
  if(!immediate){
   progress('studio_verify','Проверяю сохранённые метаданные и расписание в Studio.');
   const verify=await bounded(()=>page.context().newPage(),15000,'Не открылась вкладка проверки Studio');try{await syncClock(verify,plan,host.progress);await readback(verify,item,id,plan,opts,zone);}finally{await bounded(()=>verify.close(),2000,'Не закрылась вкладка проверки').catch(()=>{});}
  }else progress('studio_verify','Shorts опубликованы; проверка расписания не требуется.');
  const result={url:`https://www.youtube.com/watch?v=${id}`,videoId:id,packIndex,visibility:immediate?'public':'scheduled',schedule:immediate?null:plan};results.push(result);
  host.progress('item_confirmed',immediate?`Studio опубликовал Shorts · ${id}`:`Studio подтвердил расписание ${plan.date} ${plan.time}`,result);
  await host.dismiss(page);
 }
 return results;
}
module.exports={actionButton,enabled,BUILD,L,one,until,norm,read,field,typeText,typingBudget,metadataBudget,tags,validateTags,setTags,question,details,showMore,uploadScope,scopeAlive,parseDate,parseTime,fillDate,offsetLabel,planFor,syncClock,schedule,publishPublic,visibility,wizardState,readback,runSequential,videoId,waitUpload,diagnostics,isImmediatePublish};
