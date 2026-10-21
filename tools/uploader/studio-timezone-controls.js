'use strict';
// Read the timezone list in ONE browser evaluation. Per-option CDP reads can
// exceed the polling budget even when the Studio renderer is perfectly healthy.
module.exports=function({one,until,norm}){
 const optionSelector='[role=option],tp-yt-paper-item,ytcp-text-menu [role=menuitem],[role=menuitemradio]';
 const zoneName=/^(Time zone|Timezone|Часовой пояс)$/i;
 function offsetLabel(text){
  const s=String(text||'').replace(/\u2212/g,'-');
  const m=s.match(/(?:GMT|UTC)\s*([+-])\s*(\d{1,2})(?::?(\d{2}))?(?!\d)/i);
  if(!m)return /\b(?:GMT|UTC)\b/i.test(s)&&!/(?:GMT|UTC)\s*[+-]/i.test(s)?0:null;
  const h=+m[2],minutes=+(m[3]||0);if(h>14||minutes>59||h===14&&minutes)return null;
  return (m[1]==='+'?1:-1)*(h*60+minutes);
 }
 function offsetOf(data,plan){
  if(data?.value)try{
   const part=new Intl.DateTimeFormat('en',{timeZone:data.value,timeZoneName:'longOffset'}).formatToParts(new Date(plan.unix*1000)).find(x=>x.type==='timeZoneName');
   const value=offsetLabel(part?.value);if(value!==null)return value;
  }catch{}
  return offsetLabel(data?.label);
 }
 // This function is serialized by Playwright; keep it independent of Node state.
 function describe(nodes){
  const visible=e=>{if(!e.getClientRects().length)return false;for(let p=e;p;p=p.parentElement||p.getRootNode().host){const s=getComputedStyle(p);if(p.hidden||s.display==='none'||s.visibility==='hidden')return false;}return true;};
  const label=e=>(e.label||e.innerText||e.textContent||e.getAttribute('aria-label')||'').replace(/\s+/g,' ').trim();
  const value=e=>{const v=e.value||e.getAttribute('data-value')||e.getAttribute('data-timezone')||e.getAttribute('value')||'';return typeof v==='string'?v:'';};
  return nodes.map((e,index)=>({index,tag:e.tagName,id:e.id,role:e.getAttribute('role')||'',visible:visible(e),inMenu:!!e.closest('[role=listbox],[role=menu],ytcp-text-menu,[role=option]'),label:e.tagName==='SELECT'?label(e.selectedOptions[0]||e):label(e),value:value(e),
   disabled:!!e.disabled||e.getAttribute('aria-disabled')==='true',selected:!!e.selected||e.getAttribute('aria-selected')==='true'||e.getAttribute('aria-checked')==='true'||e.hasAttribute('selected'),
   options:e.tagName==='SELECT'?Array.from(e.options).map((o,index)=>({index,value:o.value,label:label(o),selected:o.selected,disabled:o.disabled||!!o.closest('optgroup[disabled]')})):undefined}));
 }
 async function snapshot(locator){return locator.evaluateAll(describe);}
 function nativeCandidate(row){return /time.?zone|часов.*пояс/i.test(row.id+' '+row.label)||row.options.filter(o=>offsetLabel(o.label)!==null).length>=2;}
 async function nativeControl(scope){
  const locator=scope.locator('select'),rows=(await snapshot(locator)).filter(r=>r.visible&&nativeCandidate(r));
  if(rows.length>1)throw new Error('Studio: несколько списков часовых поясов, выбор неоднозначен.');
  return rows[0]?locator.nth(rows[0].index):null;
 }
 async function zoneControl(scope,{optional=false}={}){
  const native=await nativeControl(scope);if(native)return native;
  for(const id of ['timezone-button','time-zone-button']){const c=await one(scope.locator('#'+id),{optional:true});if(c)return c;}
  const locator=scope.locator('ytcp-dropdown-trigger,button,[role=button],[role=combobox]');
  const rows=(await snapshot(locator)).filter(r=>r.visible&&!r.inMenu&&(zoneName.test(r.label)||offsetLabel(r.label)!==null));
  // Remove wrapper components that contain the same button, using one DOM read.
  const indexes=await locator.evaluateAll((nodes,eligible)=>eligible.filter(i=>!eligible.some(j=>i!==j&&nodes[i].contains(nodes[j]))),rows.map(r=>r.index));
  if(indexes.length>1)throw new Error('Studio: неоднозначный выбор часового пояса.');
  if(indexes.length===1)return locator.nth(indexes[0]);
  if(optional)return null;throw new Error('Studio: не найден список часовых поясов. Расписание не подтверждено.');
 }
 async function controlData(control){return (await snapshot(control))[0];}
 async function menuRows(page){return (await snapshot(page.locator(optionSelector))).filter(r=>r.visible&&offsetLabel(r.label)!==null);}
 async function closeMenu(scope,{timeoutMs=5000}={}){
  const page=scope.page(),control=await zoneControl(scope);
  if((await controlData(control)).tag==='SELECT'){await control.press('Tab',{timeout:timeoutMs});return;}
  if((await menuRows(page)).length)await page.keyboard.press('Escape');
  await until(async()=>!(await menuRows(page)).length,timeoutMs,'Studio не закрыл список часовых поясов.');
 }
 async function readZone(scope,plan,{timeoutMs=5000,leaveOpen=false}={}){
  const control=await zoneControl(scope),data=await controlData(control);
  const offset=offsetOf(data,plan);
  if(offset!==null){if(!leaveOpen&&data.tag!=='SELECT')await closeMenu(scope,{timeoutMs});return {...data,offset,kind:data.tag==='SELECT'?'native':'trigger'};}
  if(!(await menuRows(scope.page())).length)await control.click({timeout:timeoutMs});
  const selected=await until(async()=>{
   const rows=(await menuRows(scope.page())).filter(r=>r.selected);
   if(rows.length>1)throw new Error('Studio: несколько выбранных часовых поясов.');return rows[0]||null;
  },timeoutMs,'Studio не показал выбранный часовой пояс.');
  const actual=offsetOf(selected,plan);if(actual===null)throw new Error('Studio: не удалось прочитать смещение выбранного пояса.');
  if(!leaveOpen)await closeMenu(scope,{timeoutMs});return {...selected,offset:actual,kind:'menu'};
 }
 async function setZone(scope,plan,zone,{progress=()=>{},timeoutMs=10000}={}){
  const control=await zoneControl(scope),data=await controlData(control),label='UTC'+(plan.offset>=0?'+':'')+plan.offset/60;
  progress('studio_zone','Список поясов: '+(data.tag==='SELECT'?'стандартный select':'меню Studio')+'; выбран '+data.label+'.');
  if(offsetOf(data,plan)===plan.offset){await closeMenu(scope);progress('studio_zone','Часовой пояс уже верный: '+data.label+'.');return {...data,offset:plan.offset};}
  if(data.tag==='SELECT'){
   const rows=data.options.filter(o=>!o.disabled),choice=rows.find(o=>o.value===zone)||rows.find(o=>offsetOf(o,plan)===plan.offset);
   if(!choice)throw new Error('В списке Studio нет пояса ПК '+label+'. Доступные смещения: '+[...new Set(rows.map(o=>offsetLabel(o.label)).filter(x=>x!==null))].join(', '));
   progress('studio_zone','Выбираю '+choice.label+' через стандартный список.');
   await control.selectOption({index:choice.index},{timeout:timeoutMs});
  }else{
   if(!(await menuRows(scope.page())).length)await control.click({timeout:5000});
   const choice=await until(async()=>{
    const rows=(await menuRows(scope.page())).filter(o=>!o.disabled);
    return rows.find(o=>o.value===zone)||rows.find(o=>offsetOf(o,plan)===plan.offset)||null;
   },timeoutMs,'Studio не показал пояс ПК '+label);
   const option=scope.page().locator(optionSelector).nth(choice.index);
   const current=await controlData(option);
   if(current.value!==choice.value||current.label!==choice.label)throw new Error('Studio изменил список поясов перед выбором; подтверждение остановлено.');
   progress('studio_zone','Выбираю '+choice.label+'.');await option.click({timeout:5000});
   await until(async()=>!(await menuRows(scope.page())).length,5000,'Studio не закрыл меню после выбора пояса.');
  }
  const actual=await until(async()=>{
   const got=await readZone(scope,plan,{timeoutMs:5000});return got.offset===plan.offset?got:null;
  },timeoutMs,'Studio не подтвердил пояс ПК '+label);
  await closeMenu(scope);progress('studio_zone','Подтверждён пояс '+actual.label+' ('+label+').');return actual;
 }
 async function diagnostics(scope){
  return {controls:(await snapshot(scope.locator('select,#timezone-button,#time-zone-button,ytcp-dropdown-trigger,[role=combobox]'))).filter(r=>r.visible&&(r.tag==='SELECT'?nativeCandidate(r):offsetLabel(r.label)!==null||zoneName.test(r.label))).slice(0,6),openOptions:(await menuRows(scope.page())).slice(0,160)};
 }
 return {offsetLabel,zoneControl,readZone,setZone,closeZoneMenu:closeMenu,zoneDiagnostics:diagnostics};
};
