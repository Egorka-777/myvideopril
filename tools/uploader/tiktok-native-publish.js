"use strict";
const NOW=/^(Now|Сейчас)$/i, PUBLIC=/^(Everyone|Public|Все|Для всех)$/i;
const POST=/^(Post|Publish|Post now|Опубликовать|Опубликовать сейчас)$/i;
const REVIEW=/content.*(?:review|checking)|under review|being reviewed|контент проверяется|на проверке|ид[её]т проверка/i;
const clean=s=>String(s||"").replace(/\s+/g," ").trim();
async function ready(locator){return await locator.isVisible().catch(()=>false);}
async function immediatePublic(page) {
  const now=page.getByRole("radio",{name:NOW}).first();
  if(await ready(now))await now.check();
  else {
    let region=page.getByText(NOW,{exact:true}).first(),selected=false;
    if(await ready(region))for(let i=0;i<4;i++) {
      region=region.locator("xpath=..");
      const radio=region.locator('input[type="radio"], [role="radio"]');
      if(await radio.count()!==1)continue;
      const label=await radio.evaluate(e=>e.getAttribute("aria-label") || [...(e.labels||[])].map(l=>l.innerText).join(" ") || e.closest("label")?.innerText || (e.getAttribute("role")==="radio"?e.innerText:e.parentElement.innerText));
      if(!NOW.test(String(label||"").trim()))continue;
      await radio.check({force:true});
      selected=await radio.isChecked();if(selected)break;
    }
    if(!selected)throw new Error("TikTok не подтвердил переключатель «Сейчас». Публикация не начиналась.");
  }
  const label=page.getByText(/^(Who can (?:watch|view).*|Кто может видеть эту публикацию)$/i).first();
  if(!await ready(label))throw new Error("Не найдена настройка видимости TikTok. Публикация не начиналась.");
  let region=label;
  for(let i=0;i<4;i++){
    region=region.locator("xpath=..");
    if(await ready(region.getByText(PUBLIC,{exact:true}).first()))return;
    const control=region.locator('select, [role="combobox"], button');
    if(await control.count()!==1)continue;
    if(await control.evaluate(e=>e.tagName)==="SELECT")await control.selectOption({label:"Everyone"}).catch(()=>control.selectOption({label:"Все"}));
    else {await control.click();await page.getByRole("option",{name:PUBLIC}).first().click();}
    if(await ready(region.getByText(PUBLIC,{exact:true}).first()))return;
  }
  throw new Error("TikTok не подтвердил видимость «Все». Публикация не начиналась.");
}
async function rows(page) {
  return page.evaluate(()=>{
    const items=[...document.querySelectorAll('tbody tr, [role="row"], [data-row-key], [data-post-id], [data-e2e="post-item"]')];
    return items.filter(e=>{const r=e.getBoundingClientRect();return r.width&&r.height;}).map(row=>{
      let id=row.getAttribute("data-post-id")||row.getAttribute("data-row-key")||row.getAttribute("data-id")||"";
      if(!id){for(const link of row.querySelectorAll("a[href]")){const m=(link.getAttribute("href")||"").match(/\/video\/(\d+)/);if(m){id=m[1];break;}}}
      const text=(row.innerText||"").replace(/\s+/g," ").trim();
      const caption=row.getAttribute("data-caption") || [...row.querySelectorAll('[title], [data-e2e*="caption"], a[href*="/video/"]')].map(e=>e.getAttribute("title")||e.innerText||"").find(Boolean)||"";
      return {id,text,caption};
    }).filter(r=>r.id).filter((r,i,list)=>list.findIndex(other=>other.id===r.id)===i);
  });
}
async function contentPage(upload) {
  const link=upload.getByRole("link",{name:/^(Posts|Content|Публикации|Контент)$/i}).first();
  const href=await link.getAttribute("href").catch(()=>"");
  if(!href)throw new Error("Не найдена ссылка «Публикации» в TikTok Studio. Публикация не начиналась.");
  const url=new URL(href,upload.url());
  if(url.hostname!=="www.tiktok.com" && url.hostname!=="tiktok.com")throw new Error("Некорректная ссылка Studio.");
  const page=await upload.context().newPage();
  try {await page.goto(url.href,{waitUntil:"domcontentloaded",timeout:90000});
    await page.locator('table, [role="table"], [data-e2e*="post-list"], [data-e2e*="empty"]').first().waitFor({state:"visible",timeout:30000});
    await page.waitForFunction(()=>{
      const loading=document.querySelector('[aria-busy="true"], [data-e2e="loading"]');if(loading && loading.getBoundingClientRect().height)return false;
      return !!document.querySelector('tbody tr[data-row-key], [data-post-id], [data-id], tr a[href*="/video/"]') || /no posts|no videos|нет публикаций|публикаций нет|загрузите первое видео|upload your first video/i.test(document.body.innerText);
    },null,{timeout:30000});
    return page;
  } catch(e){await page.close();throw e;}
}
async function confirmReviewDialog(page) {
  let dialog=page.getByRole("dialog").filter({hasText:/continue (?:posting|publishing)|продолжить публикацию/i}).first();
  if(!await ready(dialog)){
    const heading=page.getByText(/^(Continue (?:posting|publishing)\?|Продолжить публикацию\?)$/i).first();
    if(!await ready(heading))return false;
    dialog=heading;for(let i=0;i<4;i++){dialog=dialog.locator("xpath=..");if(await dialog.getByRole("button",{name:POST}).count()===1)break;}
  }
  const confirm=dialog.getByRole("button",{name:POST}).first();
  if(!await ready(confirm) || await confirm.isDisabled())return false;
  await confirm.scrollIntoViewIfNeeded();
  await page.waitForTimeout(2000);
  await confirm.click();return true;
}
function matchRow(list,id,caption,before) {
  const expected=clean(caption);
  const matches=list.filter(row=>{
    if(id)return row.id===id;
    if(before.has(row.id))return false;
    const full=clean(row.caption), text=clean(row.text);
    return full===expected || text.includes(expected) || expected.length>=20 && text.includes(expected.slice(0,20));
  });
  if(matches.length>1)throw new Error("Найдено несколько новых публикаций с этой подписью. Повтор запрещён; нужна сверка Studio.");
  if(!matches.length)return null;
  const row=matches[0], review=REVIEW.test(row.text) || /(?:^|\s)(?:Only me|Только я)(?:\s|$)/i.test(row.text);
  const publicPost=/(?:^|\s)(?:Everyone|Public|Все|Для всех)(?:\s|$)/i.test(row.text) && !review;
  if(!review && !publicPost)throw new Error("Ролик найден в Studio, но доступ «Все» не подтверждён. Повтор запрещён.");
  return {id:row.id,review,public:publicPost};
}
async function publishNative(page,item,{beforeSubmit,accepted,reviewTimeout=600000,pollMs=5000,id=""}={}) {
  await immediatePublic(page);
  const content=await contentPage(page);
  let before, contentUrl;
  try {
    before=new Set((await rows(content)).map(r=>r.id));
    contentUrl=new URL(content.url());
  }finally{await content.close();}
  const button=page.getByRole("button",{name:POST}).first();
  if(!await ready(button) || await button.isDisabled())throw new Error("Кнопка публикации недоступна. Проверь Studio перед повтором.");
  await button.scrollIntoViewIfNeeded();
  await page.waitForTimeout(2000);
  await beforeSubmit();
  await button.click();
  let confirmed=false, result=null, notified=false, inContent=false;
  const end=Date.now()+reviewTimeout;
  while(Date.now()<end){
    if(!confirmed)confirmed=await confirmReviewDialog(page);
    const current=new URL(page.url());
    if(current.origin===contentUrl.origin && current.pathname===contentUrl.pathname){
      if(inContent)await page.reload({waitUntil:"domcontentloaded",timeout:90000});
      inContent=true;
      result=matchRow(await rows(page),id,item.caption,before);
      if(result){
        id=result.id;
        if(!notified){await accepted(result);notified=true;}
        if(result.public)return result;
      }
    }
    await page.waitForTimeout(pollMs);
  }
  if(notified)throw new Error("TikTok ещё не подтвердил доступ «Все» за отведённое время. Профиль оставлен открытым; повторная загрузка запрещена.");
  throw new Error("TikTok не подтвердил новую запись в списке Studio. Профиль оставлен открытым; автоповтор запрещён.");
}
async function openNextUpload(page) {
  const uploadName=/^(Upload|Upload video|Загрузить|Загрузить видео)$/i;
  let control=page.getByRole("link",{name:uploadName}).first();
  if(!await ready(control))control=page.getByRole("button",{name:uploadName}).first();
  await control.waitFor({state:"visible",timeout:30000});
  await control.scrollIntoViewIfNeeded();
  await page.waitForTimeout(2000);
  await control.click();
  const file=page.locator('input[type="file"]').first();
  const video=page.getByRole("menuitem",{name:/^(Video|Видео)$/i}).or(page.getByRole("link",{name:/^(Video|Видео)$/i})).or(page.getByRole("button",{name:/^(Video|Видео)$/i})).first();
  await file.or(video).first().waitFor({state:"attached",timeout:30000});
  if(!await file.count()){
    await video.waitFor({state:"visible",timeout:30000});
    await page.waitForTimeout(2000);
    await video.click();
  }
  await file.waitFor({state:"attached",timeout:30000});
}
async function recheckNative(page,item,{timeout=30000,pollMs=5000}={}) {
  const id=String(item.videoId||item.publishedVideoId||"");
  if(!id)throw new Error("Нет сохранённого ID публикации. Проверка по одной подписи запрещена.");
  const content=await contentPage(page);
  try {
    const end=Date.now()+timeout;
    do {const result=matchRow(await rows(content),id,item.caption,new Set());if(result)return result;
      await page.waitForTimeout(pollMs);await content.reload({waitUntil:"domcontentloaded",timeout:90000});}while(Date.now()<end);
    throw new Error("Сохранённый ID публикации не найден. Блокировка повтора сохранена.");
  }finally{await content.close();}
}
// Reconciliation deliberately does not use matchRow's post-submit prefix heuristic.
// A caption without an ID is only unique after the entire Studio list was read.
function reconcileInventory(inventory,items) {
  const list=inventory.rows, used=new Set();
  return items.map((item,index)=>{
    const id=String(item.publishedVideoId||""),caption=clean(item.caption||item.description||item.title);
    if(!id && !caption)throw new Error(`Ролик ${index+1}: нет подписи для сверки Studio.`);
    if(!id && !inventory.complete)throw new Error("Список Studio прочитан не полностью: уникальность полной подписи не подтверждена. Загрузка не начиналась; нужна проверка пагинации Studio.");
    if(!id && list.some(row=>{
      const value=clean(row.caption);
      return !value || /(?:…|\.{3})$/.test(value) && value!==caption && caption.startsWith(value.replace(/(?:…|\.{3})$/,""));
    }))throw new Error(`Ролик ${index+1}: Studio показывает сокращённую или недоступную подпись; точное совпадение не доказано. Повтор запрещён.`);
    const matches=list.filter(row=>id?row.id===id:clean(row.caption)===caption);
    if(matches.length>1)throw new Error(`Ролик ${index+1}: несколько публикаций с одинаковой полной подписью/ID. Автоматическая сверка остановлена.`);
    if(!matches.length) {
      if(id || item.studioPending)throw new Error(`Ролик ${index+1}: ранее отправленная публикация не найдена; блокировка повтора сохранена.`);
      return null;
    }
    const row=matches[0];
    if(used.has(row.id))throw new Error("Одна публикация Studio соответствует нескольким заданиям. Автоматическая сверка остановлена.");
    used.add(row.id);
    return matchRow([row],row.id,caption,new Set());
  });
}
async function reconcileNative(page,items,{matched=async()=>{}}={}) {
  const content=await contentPage(page);
  try {
    const list=await rows(content);
    // Empty-state text is already awaited by contentPage. For a populated list,
    // pagination must be verified against real Studio DOM before claiming completeness.
    const complete=!list.length && await content.locator("body").innerText().then(text=>/no posts|no videos|нет публикаций|публикаций нет|загрузите первое видео|upload your first video/i.test(text));
    const results=reconcileInventory({rows:list,complete},items);
    // Resolve every item before persisting anything, so ambiguity cannot authorize a partial upload.
    for(let i=0;i<results.length;i++)if(results[i])await matched(results[i],items[i],i+1,items.length);
    return results;
  }finally{await content.close();}
}
module.exports={publishNative,recheckNative,immediatePublic,confirmReviewDialog,matchRow,rows,contentPage,openNextUpload,reconcileInventory,reconcileNative};
