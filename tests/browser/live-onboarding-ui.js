"use strict";
// Native browser events on isolated Studio pages; no real channels or live broadcasts.
const assert=require('assert');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const {NativeStudio}=require('../../tools/uploader/worker-youtube-live-session');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  for(const lang of ['ru','en','ru-actual']) {
   const context=await browser.newContext();let step=0,now=0,encoder=0,manageVisits=0;const ru=lang.startsWith('ru');
   await context.route('**/*',async route=>{
    const u=new URL(route.request().url());
    if(u.pathname==='/channel/UCfixture')return route.fulfill({contentType:'text/html; charset=utf-8',body:`<button>Создать</button><script>document.querySelector('button').onclick=()=>{const b=document.createElement('button');b.textContent='Начать трансляцию';b.onclick=()=>location.href='/channel/UCfixture/livestreaming';document.body.append(b);};</script>`});
    if(u.pathname==='/setup/now'){now++;step=1;return route.fulfill({body:'ok'});}
    if(u.pathname==='/setup/encoder'){encoder++;step=2;return route.fulfill({body:'ok'});}
    if(u.pathname.endsWith('/manage'))manageVisits++;
    const question=step===0?(ru?'Когда вы хотите выйти в эфир?':'When do you want to go live?'):(lang==='ru-actual'?'Выберите способ выхода в эфир.':ru?'Как вы хотите проводить трансляцию?':'How would you like to go live?');
    const label=step===0?(ru?'Сейчас':'Right now'):(ru?'С помощью видеокодера':'Streaming software');
    const background=`<h1>${ru?'Управление':'Manage'}</h1><button>${ru?'Запланировать трансляцию':'Schedule stream'}</button>`;
    const body=step<2?`${background}<section role="dialog" style="position:fixed;inset:0;background:#222"><h1>${question}</h1><button id="choice">${label}</button></section><script>document.querySelector('#choice').onclick=async()=>{await fetch('/setup/${step===0?'now':'encoder'}',{method:'POST'});location.reload();};</script>`:background;
    return route.fulfill({contentType:'text/html; charset=utf-8',body});
   });
   const page=await context.newPage(),progress=[];const ui=new NativeStudio(page,'UCfixture',s=>progress.push(s));
   await ui.prepare();assert.equal(now,1);assert.equal(encoder,1);assert.equal(step,2);assert(progress.some(s=>s.includes('видеокодера')));assert(progress.some(s=>s.includes('готов')));assert.equal(manageVisits,0,'onboarding must finish without forced management reloads');
   // All copying stays inside this task tab; each native clipboard path is captured.
   for(const mode of ['text','items','legacy']) {
    await page.route('**/copy',route=>route.fulfill({contentType:'text/html; charset=utf-8',body:`<section><span>Stream key (paste in encoder)</span><textarea id='key'>fixture-key-private</textarea><button>Copy</button></section><script>
      window.originalWrites=0;navigator.clipboard.writeText=async()=>window.originalWrites++;navigator.clipboard.write=async()=>window.originalWrites++;
      document.querySelector('button').onclick=()=>{${mode==='text'?"navigator.clipboard.writeText(key.value);":mode==='items'?"navigator.clipboard.write([new ClipboardItem({'text/plain':new Blob([key.value],{type:'text/plain'})})]);":"key.focus();key.select();document.execCommand('copy');"}};</script>`}));
    await page.goto('https://studio.youtube.com/copy');
    assert.equal(await ui.copySetting(/^Stream key \(paste in encoder\)$/),'fixture-key-private');
    assert.equal(await page.evaluate(()=>window.originalWrites),0,'secret written to system clipboard');
    assert.equal(await page.evaluate(()=>('__vbCopied' in window)||('__vbCopyListener' in window)),false,'secret retained in tab globals');
   }
   await context.close();
  }
  // Verification must use the loaded document's channel URL, not page.content()
  // (which failed during the real Studio redirect). No Data API bootstrap is needed.
  const identityContext=await browser.newContext();
  await identityContext.route('**/*',route=>route.fulfill({contentType:'text/html',body:
    new URL(route.request().url()).pathname==='/' ? '<script>location.replace("/channel/UCfixture");</script>' : '<div id="channel-name">Fixture channel</div>'}));
  const identityPage=await identityContext.newPage();
  identityPage.content=async()=>{throw new Error('page.content: Unable to retrieve content because the page is navigating and changing the content.');};
  const identity=await new NativeStudio(identityPage).verify('https://www.youtube.com/channel/UCfixture');
  assert.equal(identity.channelId,'UCfixture');assert.equal(identity.channelName,'Fixture channel');
  await assert.rejects(new NativeStudio(identityPage).verify('https://www.youtube.com/channel/UCother'),/другой YouTube-канал/);
  await identityContext.close();
  const context=await browser.newContext();await context.route('**/*',route=>route.fulfill({contentType:'text/html',body:new URL(route.request().url()).pathname==='/channel/UCfixture' ? `<button>Create</button><script>document.querySelector('button').onclick=()=>{const b=document.createElement('button');b.textContent='Go live';b.onclick=()=>location.href='/channel/UCfixture/livestreaming';document.body.append(b);};</script>` : '<h1>Activate live streaming</h1><p>24 hours</p>'}));
  const page=await context.newPage();await assert.rejects(new NativeStudio(page,'UCfixture').prepare(),/активации/);await context.close();
  console.log('PASS: RU/EN first-time Now→Encoder setup, readiness before creation, activation error, native writeText/write/execCommand capture without system clipboard or retained secrets');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
