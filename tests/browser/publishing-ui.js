"use strict";
// Real Chromium, native input/click events, isolated pages. No real accounts/posts.
const assert=require("assert"),fs=require("fs"),os=require("os"),path=require("path");
const {chromium}=require("../../tools/uploader/node_modules/playwright-core");
const {uploadThumbnail}=require("../../tools/uploader/youtube-thumbnail");
const native=require("../../tools/uploader/tiktok-native-publish");
const {fillCaption}=require("../../tools/uploader/worker-tiktok");
const pixel=Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lX0AAAAASUVORK5CYII=","base64");
(async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),"publishing-dom-"));const image=path.join(root,"cover.png");fs.writeFileSync(image,pixel);
  const browser=await chromium.launch({headless:true});
  try{
    const page=await browser.newPage();let saved=false, selected=false, reject=false;
    await page.route("**/*",route=>{
      const u=new URL(route.request().url());
      if(u.hostname!=="studio.youtube.com")return route.fulfill({contentType:"image/png",body:pixel});
      return route.fulfill({contentType:"text/html; charset=utf-8",body:`<main><input id="video" type="file" accept="video/mp4">
        <div id="thumbnail-editor"><img width="80" height="80" src="https://i.ytimg.com/${saved?'custom':'original'}.jpg">
        <input id="cover" type="file" accept="image/png" hidden><div role="alert"></div></div>
        <button ${saved?'disabled':''}>${u.searchParams.get('lang')==='ru'?'Сохранить':'Save'}</button></main><script>
        cover.onchange=()=>{if(${reject}){document.querySelector('[role=alert]').textContent='Upload rejected';return;}
          document.querySelector('img').src='https://i.ytimg.com/custom.jpg';};
        document.querySelector('button').onclick=async function(){await fetch('/saved',{method:'POST'});this.disabled=true;};</script>`});
    });
    await page.route("**/saved",async route=>{saved=true;await route.fulfill({body:"ok"});});
    for(const lang of ["en","ru"]){saved=false;await page.goto("https://studio.youtube.com/video/abcdefghijk/edit?lang="+lang);
      const result=await uploadThumbnail(page,image,{save:true,verifyReload:true,timeout:2000});assert(result.includes("custom"));assert(saved);
      assert.equal(await page.locator('#video').evaluate(e=>e.files.length),0,"thumbnail went to video input");
    }
    reject=true;saved=false;await page.goto("https://studio.youtube.com/video/abcdefghijk/edit");
    await assert.rejects(uploadThumbnail(page,image,{save:true,timeout:1000}),/отклонил/);assert(!saved);
    await page.close();

    for(const variant of ["ru","en","pending"]){
      const lang=variant==="ru"?"ru":"en",pending=variant==="pending";
      const context=await browser.newContext();let posted=false, clicks=0, confirmations=0, captions="", polls=0, keys=0;
      const old={id:"111",caption:"Старый ролик",review:false};
      await context.route("**/*",async route=>{
        const u=new URL(route.request().url());
        if(u.pathname==="/fixture/post"){
          captions=JSON.parse(route.request().postData()).caption;posted=true;clicks++;
          return route.fulfill({body:"{}"});
        }
        if(u.pathname==="/fixture/confirm"){confirmations++;return route.fulfill({body:"{}"});}
        if(u.pathname.includes("/content")){
          if(posted)polls++;
          const reviewing=posted&&(pending||polls<3);
          const row=(id,text,status)=>`<tr data-row-key="${id}"><td><span title="${text}">${text}</span></td><td>${status}</td></tr>`;
          return route.fulfill({contentType:"text/html; charset=utf-8",body:`<h1>${lang==='ru'?'Публикации':'Posts'}</h1><table><tbody>${row(old.id,old.caption,lang==='ru'?'Все':'Everyone')}
            ${posted?row('222',captions,reviewing?(lang==='ru'?'Только я · Контент проверяется':'Only me · Content under review'):(lang==='ru'?'Все':'Everyone')):''}</tbody></table>`});
        }
        return route.fulfill({contentType:"text/html; charset=utf-8",body:`<main>
          <a href="/tiktokstudio/content">${lang==='ru'?'Публикации':'Posts'}</a>
          <label><input type="radio" name="when" aria-label="${lang==='ru'?'Сейчас':'Now'}">${lang==='ru'?'Сейчас':'Now'}</label>
          <label><input type="radio" name="when" checked aria-label="Schedule">Schedule</label>
          <section><h2>${lang==='ru'?'Кто может видеть эту публикацию':'Who can watch this video'}</h2><button>${lang==='ru'?'Все':'Everyone'}</button></section>
          <div contenteditable="true" role="textbox">original filename caption</div>
          <button id="post">${lang==='ru'?'Опубликовать':'Post'}</button></main><script>
          window.keys=0;document.querySelector('[contenteditable]').addEventListener('keydown',()=>window.keys++);
          post.onclick=async()=>{await fetch('/fixture/post',{method:'POST',body:JSON.stringify({caption:document.querySelector('[contenteditable]').innerText})});post.disabled=true;
            const d=document.createElement('div');d.setAttribute('role','dialog');d.innerHTML='<h2>${lang==='ru'?'Продолжить публикацию?':'Continue publishing?'}</h2><button>${lang==='ru'?'Отмена':'Cancel'}</button><button>${lang==='ru'?'Опубликовать':'Post'}</button>';
            d.querySelectorAll('button')[1].onclick=async()=>{await fetch('/fixture/confirm',{method:'POST'});d.remove();};document.body.append(d);};</script>`});
      });
      const page=await context.newPage();await page.goto("https://www.tiktok.com/tiktokstudio/upload");
      const caption=lang==='ru'?"Бот в профиле 👇\n\n#бинодекс #трейдинг":"Bot in profile 👇\n\n#binodex #trading";
      const typedAt=Date.now();await fillCaption(page,caption);assert(Date.now()-typedAt>=1000,"caption still inserted in a burst");assert(await page.evaluate(()=>window.keys)>10,"caption was not typed through keyboard");
      let persisted=false, accepted=false;
      const result=await native.publishNative(page,{caption},{beforeSubmit:async()=>{persisted=true;},accepted:async r=>{accepted=true;assert(persisted&&r.id==='222');},reviewTimeout:pending?150:5000,pollMs:5});
      assert(pending?result.review&&!result.public:result.public&&!result.review);assert.equal(clicks,1);assert.equal(confirmations,1);assert(accepted);assert.equal(captions,caption);
      assert(!await page.getByRole("radio",{name:"Schedule",exact:true}).isChecked());
      const again=await native.recheckNative(page,{caption,publishedVideoId:result.id},{timeout:1000});assert(pending?again.review:again.public);assert.equal(clicks,1,"status check uploaded a duplicate");
      await assert.rejects(native.recheckNative(page,{caption},{timeout:1000}),/ID публикации/);
      await context.close();
    }
    const invalid=await browser.newPage();await invalid.setContent('<span>Now</span><label><input type=radio aria-label=Schedule>Schedule</label><section><h2>Who can watch this video</h2><button>Everyone</button></section>');
    await assert.rejects(native.immediatePublic(invalid),/переключатель/);await invalid.close();
    assert.equal(native.matchRow([{id:'7',text:'same caption Все',caption:'same caption'}],'','same caption',new Set(['7'])),null,"old same caption was mistaken for a new post");
    assert.throws(()=>native.matchRow([{id:'7',text:'wrong Only me',caption:'wrong'}],'7','wrong',new Set()),/доступ/);
    console.log("PASS: real Chromium RU/EN keyboard captions, Now/Everyone, review confirmation, new-row identity, pending→public/pending timeout, ID-only duplicate-free recheck, YouTube image-input targeting/save/reload/rejection");
  }finally{await browser.close();fs.rmSync(root,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
