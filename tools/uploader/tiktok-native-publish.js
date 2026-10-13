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
  const row=matches[0], review=REVIEW.test(row.text);
  const publicPost=/(?:^|\s)(?:Everyone|Public|Все|Для всех)(?:\s|$)/i.test(row.text) && !review;
  if(!review && !publicPost)throw new Error("Ролик найден в Studio, но доступ «Все» не подтверждён. Повтор запрещён.");
  return {id:row.id,review,public:publicPost};
}
async function publishNative(page,item,{beforeSubmit,accepted,reviewTimeout=600000,pollMs=5000,id=""}={}) {
  await immediatePublic(page);
  const content=await contentPage(page);
  try {
    const before=new Set((await rows(content)).map(r=>r.id));
    await beforeSubmit();
    const button=page.getByRole("button",{name:POST}).first();
    if(!await ready(button) || await button.isDisabled())throw new Error("Кнопка публикации недоступна. Проверь Studio перед повтором.");
    await button.click();
    let confirmed=false, result=null, notified=false;
    const end=Date.now()+reviewTimeout;
    while(Date.now()<end){
      if(!confirmed)confirmed=await confirmReviewDialog(page);
      await content.reload({waitUntil:"domcontentloaded",timeout:90000});
      result=matchRow(await rows(content),id,item.caption,before);
      if(result){if(!notified){await accepted(result);notified=true;}if(result.public)return result;}
      await page.waitForTimeout(pollMs);
    }
    if(result && result.review)return result;
    throw new Error("TikTok не подтвердил новую запись в Studio. Автоповтор запрещён; проверь профиль.");
  }finally{await content.close();}
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
module.exports={publishNative,recheckNative,immediatePublic,confirmReviewDialog,matchRow,rows};
