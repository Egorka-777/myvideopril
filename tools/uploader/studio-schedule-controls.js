'use strict';
// Scheduling controls only. Prefer the date picker's editable input. Never select
// a day number elsewhere in the wizard or infer a month from screen coordinates.
module.exports=function({one,until,norm,read}){
 const MONTHS=[['january','январ','янв'],['february','феврал','фев'],['march','марта','март','мар'],['april','апрел','апр'],['may','мая','май'],['june','июня','июнь','июн'],['july','июля','июль','июл'],['august','август','авг'],['september','сентябр','сен'],['october','октябр','окт'],['november','ноябр','ноя'],['december','декабр','дек']];
 const CALENDAR='ytcp-date-picker,ytcp-datepicker,[role=dialog][aria-label*="calendar" i],[role=dialog][aria-label*="календар" i]';
 const dateLabel=/^(?:Date|Дата)(?:\s|$)|выбрать дату|choose date/i;
 function monthYear(text){
  text=norm(text).toLowerCase();const years=text.match(/\b(?:20|21)\d{2}\b/g);if(years?.length!==1)return null;
  const months=MONTHS.map((a,i)=>[...a,a[0].slice(0,3)].some(w=>new RegExp('(?<![\\p{L}])'+w+'[\\p{L}]*','u').test(text))?i+1:0).filter(Boolean);
  return months.length===1?{year:Number(years[0]),month:months[0]}:null;
 }
 function iso(y,m,d){if(y<2000||y>2199||m<1||m>12||d<1||d>new Date(Date.UTC(y,m,0)).getUTCDate())return null;return `${y}-${String(m).padStart(2,'0')}-${String(d).padStart(2,'0')}`;}
 function parseDate(text,locale=''){
  text=norm(text).replace(/\u202f/g,' ');let m=text.match(/^(\d{4})-(\d{2})-(\d{2})$/);if(m)return iso(+m[1],+m[2],+m[3]);
  m=text.match(/^(\d{1,2})([./])(\d{1,2})\2(\d{4})\s*(?:г\.?)?$/i);
  if(m){let a=+m[1],b=+m[3];if(m[2]==='/'&&/^en(?:-US)?$/i.test(locale))return iso(+m[4],a,b);if(m[2]==='/'&&a<=12&&b<=12&&!locale)return null;return iso(+m[4],b,a);}
  const my=monthYear(text);if(!my)return null;
  const numbers=text.replace(/\b(?:20|21)\d{2}\b/,'').match(/\b\d{1,2}\b/g);return numbers?.length===1?iso(my.year,my.month,+numbers[0]):null;
 }
 function parseTime(text){const m=norm(text).match(/^(\d{1,2}):(\d{2})\s*(am|pm)?$/i);if(!m)return null;let h=+m[1];if(+m[2]>59||(m[3]?(h<1||h>12):h>23))return null;if(m[3])h=h%12+(m[3].toLowerCase()==='pm'?12:0);return `${String(h).padStart(2,'0')}:${m[2]}`;}
 function budget(timeout){const end=Date.now()+timeout;return ()=>{const ms=end-Date.now();if(ms<=0)throw new Error('Studio: время настройки календаря/часов истекло. Повторные клики остановлены.');return Math.min(ms,4000);};}
 async function value(control){return control.evaluate(e=>e.value||e.querySelector('input')?.value||e.innerText||e.textContent||'');}
 async function locale(scope){return scope.page().locator('html').getAttribute('lang')||'';}
 async function active(node){if(await node.isVisible())return true;for(const c of await node.locator('input,button,[role=button],[role=gridcell],[data-date]').all())if(await c.isVisible())return true;return false;}
 async function calendarSurface(scope,optional=false){
  const roots=[];for(const candidate of await scope.page().locator(CALENDAR).all())if(await active(candidate)){
   if(!await candidate.evaluate((e,selector)=>!!e.parentElement?.closest(selector),CALENDAR))roots.push(candidate);
  }
  if(roots.length>1)throw new Error('Studio: открыто несколько календарей. Выбор даты остановлен.');
  if(!roots.length&&!optional)throw new Error('Studio не показал календарь даты.');return roots[0]||null;
 }
 async function dateControl(scope){
  for(const selector of ['#datepicker-trigger','ytcp-datepicker-trigger','input[type=date]']){
   const c=await one(scope.locator(selector),{optional:true});if(!c)continue;
   const buttons=[];for(const b of await c.getByRole('button').all())if(await b.isVisible()&&parseDate(await value(b),await locale(scope)))buttons.push(b);
   if(buttons.length===1)return buttons[0];
   const inner=await one(c.locator('ytcp-dropdown-trigger'),{optional:true});return inner||c;
  }
  const choices=[];
  for(const c of await scope.locator('ytcp-dropdown-trigger,button,[role=button],input[aria-label]').all())if(await c.isVisible()){
   if(parseDate(await value(c),await locale(scope))||dateLabel.test(await c.getAttribute('aria-label')||''))choices.push(c);
  }
  // Parent custom component and its native child represent one control.
  const leaves=[];for(const c of choices){let contains=false;for(const other of choices)if(c!==other&&await c.evaluate((e,o)=>e!==o&&e.contains(o),await other.elementHandle())){contains=true;break;}if(!contains)leaves.push(c);}
  if(leaves.length!==1)throw new Error('Studio: не найдено однозначное поле/карточка даты.');return leaves[0];
 }
 async function timeControl(scope,optional=false){
  for(const selector of ['input[type=time]','ytcp-time-of-day-picker input,#time-of-day-container input,#time-field input,input[aria-label="Time" i],input[aria-label="Время" i]','#time-of-day-trigger']){
   const c=await one(scope.locator(selector),{optional:true});if(!c)continue;
   const buttons=[];for(const b of await c.getByRole('button').all())if(await b.isVisible()&&parseTime(await value(b)))buttons.push(b);
   if(buttons.length===1)return buttons[0];
   const inner=await one(c.locator('ytcp-dropdown-trigger'),{optional:true});return inner||c;
  }
  const choices=[];for(const c of await scope.locator('ytcp-dropdown-trigger').all())if(await c.isVisible()&&parseTime(await value(c)))choices.push(c);
  if(choices.length>1||!optional&&!choices.length)throw new Error('Studio: не найдено однозначное поле/карточка времени.');return choices[0]||null;
 }
 async function closeCalendar(scope,left){
  if(!await calendarSurface(scope,true))return;
  await scope.page().keyboard.press('Escape');
  await until(async()=>!await calendarSurface(scope,true),left(),'Studio не закрыл календарь после выбора даты.');
 }
 function formattedDate(date,current,lang){
  if(/^\d{4}-\d{2}-\d{2}$/.test(current))return date;
  const [y,m,d]=date.split('-');
  if(/^\d{1,2}\.\d{1,2}\.\d{4}/.test(current))return `${d}.${m}.${y}`;
  if(/^\d{1,2}\/\d{1,2}\/\d{4}/.test(current))return /^en(?:-US)?$/i.test(lang)?`${m}/${d}/${y}`:`${d}/${m}/${y}`;
  return new Date(date+'T12:00:00Z').toLocaleDateString(/[а-я]/i.test(current)||/^ru/i.test(lang)?'ru-RU':'en-US',{day:'numeric',month:'short',year:'numeric',timeZone:'UTC'});
 }
 async function replaceInput(input,text,left){
  await input.click({timeout:left()});await input.press('Control+A',{timeout:left()});await input.press('Delete',{timeout:left()});
  await input.pressSequentially(text,{delay:25,timeout:left()});
  // Enter commits the picker. It may unmount its input; do not read that stale node.
  await input.press('Enter',{timeout:left()});
 }
 async function exactDay(calendar,date){
  const candidates=[];
  for(const c of await calendar.locator('[data-date],[aria-label]').all())if(await c.isVisible()&&!await c.isDisabled()&&await c.getAttribute('aria-disabled')!=='true'){
   const label=await c.getAttribute('data-date')||await c.getAttribute('aria-label');
   if(parseDate(label)===date)candidates.push(c);
  }
  const leaves=[];for(const c of candidates){let nested=false;for(const d of candidates)if(c!==d&&await c.evaluate((e,o)=>e!==o&&e.contains(o),await d.elementHandle())){nested=true;break;}if(!nested)leaves.push(c);}
  if(leaves.length>1)throw new Error('Studio: дата неоднозначна в календаре.');return leaves[0]||null;
 }
 async function calendarMonth(calendar){
  const headers=[];for(const h of await calendar.locator('#month-label,#month,.month-label,.month-title,[data-calendar-month],ytcp-calendar-header').all())if(await h.isVisible()){
   const my=monthYear(await h.innerText());if(my)headers.push(my);
  }
  return headers.length===1?headers[0]:null;
 }
 async function selectCalendarDay(calendar,date,left){
  for(let step=0;step<36;step++){
   left();const full=await exactDay(calendar,date);if(full){await full.click({timeout:left()});return;}
   const my=await calendarMonth(calendar);if(!my)throw new Error('Studio: календарь без поля ввода не показал однозначный месяц/полную дату.');
   const target=+date.slice(0,4)*12+(+date.slice(5,7)-1),current=my.year*12+my.month-1;
   if(target===current){
    const cells=[];for(const c of await calendar.locator('button,[role=gridcell],td').all())if(await c.isVisible()&&!await c.isDisabled()&&await c.getAttribute('aria-disabled')!=='true'&&!/outside|other.month|disabled/i.test(await c.getAttribute('class')||'')&&norm(await c.innerText())===String(+date.slice(8)))cells.push(c);
    if(cells.length!==1)throw new Error('Studio: номер дня неоднозначен внутри выбранного месяца.');await cells[0].click({timeout:left()});return;
   }
   const forward=target>current;
   const nav=await one(calendar.locator(forward?'#next-month,#next-month-button':'#prev-month,#previous-month-button').or(calendar.getByRole('button',{name:forward?/next month|следующ.*месяц/i:/previous month|предыдущ.*месяц/i})),{optional:true});
   if(!nav)throw new Error('Studio не показал переход к нужному месяцу.');await nav.click({timeout:left()});
   await until(async()=>{const changed=await calendarMonth(calendar);return changed&&(changed.month!==my.month||changed.year!==my.year);},left(),'Studio не подтвердил смену месяца.');
  }
  throw new Error('Studio: календарь превышает диапазон 36 месяцев.');
 }
 async function fillDate(scope,date,{timeoutMs=20000,progress=()=>{}}={}){
  if(parseDate(date)!==date)throw new Error('Некорректная дата расписания.');
  const left=budget(timeoutMs),lang=await locale(scope),readDate=async()=>parseDate(await value(await dateControl(scope)),lang);
  progress('studio_date','Выбираю дату '+date+'.');
  if(await readDate()===date){await closeCalendar(scope,left);return;}
  const trigger=await dateControl(scope);
  let input=await trigger.evaluate(e=>e.tagName==='INPUT')?trigger:await one(trigger.locator('input'),{optional:true});
  let calendar=await calendarSurface(scope,true);
  if(!input){
   if(!calendar){await trigger.click({timeout:left()});calendar=await until(()=>calendarSurface(scope,true),left(),'Studio не открыл календарь даты.');}
   input=await one(calendar.locator('input:not([type=hidden])'),{optional:true});
  }
  if(input&&!await input.getAttribute('readonly')){
   if(await input.getAttribute('type')==='date'){await input.fill(date,{timeout:left()});await input.press('Tab',{timeout:left()});}
   else await replaceInput(input,formattedDate(date,await read(input),lang),left);
  }else{
   if(!calendar){await trigger.click({timeout:left()});calendar=await until(()=>calendarSurface(scope,true),left(),'Studio не открыл календарь даты.');}
   await selectCalendarDay(calendar,date,left);
  }
  await until(async()=>await readDate()===date,left(),'Studio не подтвердил введённую дату '+date+'.');
  await closeCalendar(scope,left);
  if(await readDate()!==date)throw new Error('После закрытия календаря дата изменилась.');
  progress('studio_date','Дата подтверждена: '+date+'.');
 }
 async function fillTime(scope,time,{timeoutMs=15000,progress=()=>{}}={}){
  if(parseTime(time)!==time)throw new Error('Некорректное время расписания.');const left=budget(timeoutMs);
  const readTime=async()=>parseTime(await value(await timeControl(scope)));
  progress('studio_time','Выбираю время '+time+'.');
  await closeCalendar(scope,left);
  if(await readTime()===time)return;
  const control=await timeControl(scope);
  if(await control.evaluate(e=>e.tagName==='INPUT')&&!await control.getAttribute('readonly')){
   if(await control.getAttribute('type')==='time'){await control.fill(time,{timeout:left()});await control.press('Tab',{timeout:left()});}
   else{const current=await read(control),h=+time.slice(0,2),text=/\b(?:am|pm)\b/i.test(current)?`${h%12||12}:${time.slice(3)} ${h<12?'AM':'PM'}`:time;await replaceInput(control,text,left);}
  }else{
   await control.click({timeout:left()});
   const opened=await until(async()=>{
    const input=await one(scope.page().locator('ytcp-time-of-day-picker input,#time-of-day-trigger input,input[aria-label="Time" i],input[aria-label="Время" i]'),{optional:true});
    if(input&&!await input.getAttribute('readonly'))return {input};
    const choices=[];for(const o of await scope.page().locator('[role=option],tp-yt-paper-item,[role=menuitem]').all())if(await o.isVisible()&&parseTime(await o.innerText())===time&&await o.getAttribute('aria-disabled')!=='true')choices.push(o);
    if(choices.length>1)throw new Error('Studio: время неоднозначно в списке.');return choices[0]?{option:choices[0]}:null;
   },left(),'Studio не показал поле или точное время '+time+'. Ближайшее время не выбиралось.');
   if(opened.input){if(await opened.input.getAttribute('type')==='time'){await opened.input.fill(time,{timeout:left()});await opened.input.press('Tab',{timeout:left()});}else{const current=await read(opened.input),h=+time.slice(0,2);await replaceInput(opened.input,/\b(?:am|pm)\b/i.test(current)?`${h%12||12}:${time.slice(3)} ${h<12?'AM':'PM'}`:time,left);}}
   else await opened.option.click({timeout:left()});
  }
  await until(async()=>await readTime()===time,left(),'Studio не подтвердил время '+time+'.');progress('studio_time','Время подтверждено: '+time+'.');
 }
 return {parseDate,parseTime,dateControl,timeControl,controlValue:value,calendarSurface,fillDate,fillTime};
};
