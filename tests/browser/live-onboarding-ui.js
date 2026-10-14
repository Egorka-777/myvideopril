"use strict";
// Native browser events on isolated Studio pages; no real channels or live broadcasts.
const assert=require('assert');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const {NativeStudio}=require('../../tools/uploader/worker-youtube-live-session');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  for(const lang of ['ru','en']) {
   const context=await browser.newContext();let step=0,later=0,encoder=0;
   await context.route('**/*',async route=>{
    const u=new URL(route.request().url());
    if(u.pathname==='/setup/later'){later++;step=1;return route.fulfill({body:'ok'});}
    if(u.pathname==='/setup/encoder'){encoder++;step=2;return route.fulfill({body:'ok'});}
    const question=step===0?(lang==='ru'?'Когда вы хотите выйти в эфир?':'When do you want to go live?'):(lang==='ru'?'Как вы хотите проводить трансляцию?':'How would you like to go live?');
    const label=step===0?(lang==='ru'?'Позже':'Later date'):(lang==='ru'?'Видеокодер':'Streaming software');
    const body=step<2?`<h1>${question}</h1><button>${label}</button><script>document.querySelector('button').onclick=async()=>{await fetch('/setup/${step===0?'later':'encoder'}',{method:'POST'});location.reload();};</script>`:
     `<h1>${lang==='ru'?'Управление':'Manage'}</h1><button>${lang==='ru'?'Запланировать трансляцию':'Schedule stream'}</button>`;
    return route.fulfill({contentType:'text/html; charset=utf-8',body});
   });
   const page=await context.newPage(),progress=[];const ui=new NativeStudio(page,'UCfixture',s=>progress.push(s));
   await ui.prepare();assert.equal(later,1);assert.equal(encoder,1);assert.equal(step,2);assert(progress.some(s=>s.includes('Видеокодер')));assert(progress.some(s=>s.includes('готов')));
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
  const context=await browser.newContext();await context.route('**/*',route=>route.fulfill({contentType:'text/html',body:'<h1>Activate live streaming</h1><p>24 hours</p>'}));
  const page=await context.newPage();await assert.rejects(new NativeStudio(page,'UCfixture').prepare(),/активации/);await context.close();
  console.log('PASS: RU/EN first-time Later→Encoder setup, readiness before creation, activation error, native writeText/write/execCommand capture without system clipboard or retained secrets');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
