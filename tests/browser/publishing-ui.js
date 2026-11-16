"use strict";
// Real Chromium, native input/click events, isolated pages. No real accounts/posts.
const assert=require("assert"),fs=require("fs"),os=require("os"),path=require("path");
const {chromium}=require("../../tools/uploader/node_modules/playwright-core");
const {uploadThumbnail}=require("../../tools/uploader/youtube-thumbnail");
const native=require("../../tools/uploader/tiktok-native-publish");
const {fillCaption}=require("../../tools/uploader/worker-tiktok");
const pixel=Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAGAAAABgCAIAAABt+uBvAAAA2ElEQVR4nO3QMRHAIADAQEBDZ/yLQkwV9DLS4V9BLvM8e/Bt3Q74O4OCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUXmVXAc6VBGjVAAAAAElFTkSuQmCC","base64"),oldPixel=Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAGAAAABgCAIAAABt+uBvAAAA2UlEQVR4nO3QQRHAIADAMEDDROBfD2KmYNcneyQKep3PPoNv63bA3xkUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGgYFAwKBgUDAoGBYOCQcGg8AKfuQHYOECCawAAAABJRU5ErkJggg==","base64");
(async()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),"publishing-dom-"));const image=path.join(root,"cover.png");fs.writeFileSync(image,pixel);
  const browser=await chromium.launch({headless:true});
  try{
    if(!process.argv.includes('--tiktok-only')){
    const page=await (await browser.newContext()).newPage();let saved=false, selected=false, reject=false,discard=false;
    await page.route("**/*",route=>{
      const u=new URL(route.request().url());
      if(u.hostname!=="studio.youtube.com")return route.fulfill({contentType:"image/png",body:u.pathname.includes("original")?oldPixel:pixel});
      return route.fulfill({contentType:"text/html; charset=utf-8",body:`<main><input id="video" type="file" accept="video/mp4">
        <div id="thumbnail-editor"><img width="80" height="80" src="https://i.ytimg.com/${saved?'custom?revision='+Date.now():'original'}.jpg">
        <input id="cover" type="file" accept="image/png" hidden><div role="alert"></div></div>
        <button ${saved?'disabled':''}>${u.searchParams.get('lang')==='ru'?'Сохранить':'Save'}</button></main><script>
        cover.onchange=()=>{if(${reject}){document.querySelector('[role=alert]').textContent='Upload rejected';return;}
          document.querySelector('img').src=URL.createObjectURL(cover.files[0]);};
        document.querySelector('button').onclick=async function(){await fetch('/saved',{method:'POST'});this.disabled=true;};</script>`});
    });
    await page.route("**/saved",async route=>{saved=!discard;await route.fulfill({body:"ok"});});
    for(const lang of ["en","ru"]){saved=false;await page.goto("https://studio.youtube.com/video/abcdefghijk/edit?lang="+lang);
      const result=await uploadThumbnail(page,image,{save:true,verifyReload:true,timeout:2000});assert(result.includes("custom"));assert(saved);
      assert.equal(await page.locator('#video').evaluate(e=>e.files.length),0,"thumbnail went to video input");
    }
    discard=true;saved=false;await page.goto("https://studio.youtube.com/video/abcdefghijk/edit");
    await assert.rejects(uploadThumbnail(page,image,{save:true,verifyReload:true,timeout:1000}),/не подтвердил/);assert(!saved);
    discard=false;reject=true;saved=false;await page.goto("https://studio.youtube.com/video/abcdefghijk/edit");
    await assert.rejects(uploadThumbnail(page,image,{save:true,timeout:1000}),/отклонил/);assert(!saved);
    await page.close();
    }

    // Draft.js innerText inserts layout newlines between blocks; those are not caption text.
    const draftPage=await browser.newPage();
    await draftPage.setContent('<div contenteditable="true" class="public-DraftEditor-content"><div data-block="true"><div style="margin-top:1px">Первая строка</div></div><div data-block="true"><div><br></div></div><div data-block="true"><div>Последняя строка</div></div></div>');
    await draftPage.evaluate(()=>{window.keys=0;document.addEventListener('keydown',()=>window.keys++);});
    await fillCaption(draftPage,'Первая строка\n\nПоследняя строка');
    assert.equal(await draftPage.evaluate(()=>window.keys),0,'matching Draft.js caption was retyped');
    await draftPage.setContent('<div contenteditable="true"></div>');
    await draftPage.evaluate(()=>{window.clears=0;document.addEventListener('keydown',e=>{if(e.key==='Backspace')window.clears++;});document.querySelector('div').addEventListener('input',e=>{e.currentTarget.textContent='changed by editor';});});
    await assert.rejects(fillCaption(draftPage,'original'),/Повторный ввод остановлен/);
    assert.equal(await draftPage.evaluate(()=>window.clears),1,'caption mismatch triggered repeated clearing');
    await draftPage.close();

    for(const variant of ["ru","en","pending","private"]){
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
          return route.fulfill({contentType:"text/html; charset=utf-8",body:`<a href="/tiktokstudio/content">${lang==='ru'?'Публикации':'Posts'}</a><h1>${lang==='ru'?'Публикации':'Posts'}</h1><table><tbody>${row(old.id,old.caption,lang==='ru'?'Все':'Everyone')}
            ${posted?row('222',captions,reviewing?(variant==='private'?'Only me':lang==='ru'?'Только я · Контент проверяется':'Only me · Content under review'):(lang==='ru'?'Все':'Everyone')):''}</tbody></table>
            <button onclick="document.querySelector('[role=menuitem]').hidden=false">${lang==='ru'?'Загрузить':'Upload'}</button>
            <a role="menuitem" hidden href="/tiktokstudio/upload">${lang==='ru'?'Видео':'Video'}</a>`});
        }
        return route.fulfill({contentType:"text/html; charset=utf-8",body:`<main>
          <a href="/tiktokstudio/content">${lang==='ru'?'Публикации':'Posts'}</a>
          <label><input type="radio" name="when" aria-label="${lang==='ru'?'Сейчас':'Now'}">${lang==='ru'?'Сейчас':'Now'}</label>
          <label><input type="radio" name="when" checked aria-label="Schedule">Schedule</label>
          <section><h2>${lang==='ru'?'Кто может видеть эту публикацию':'Who can watch this video'}</h2><button>${lang==='ru'?'Все':'Everyone'}</button></section>
          <div contenteditable="true" role="textbox">original filename caption</div>
          <input type="file" hidden><div style="height:1800px"></div><button id="post">${lang==='ru'?'Опубликовать':'Post'}</button></main><script>
          window.scrolledAt=0;addEventListener('scroll',()=>window.scrolledAt=Date.now());
          window.keys=0;document.querySelector('[contenteditable]').addEventListener('keydown',()=>window.keys++);
          post.onclick=async()=>{await fetch('/fixture/post',{method:'POST',body:JSON.stringify({caption:document.querySelector('[contenteditable]').innerText})});post.disabled=true;
            const d=document.createElement('div');d.setAttribute('role','dialog');d.innerHTML='<h2>${lang==='ru'?'Продолжить публикацию?':'Continue publishing?'}</h2><button>${lang==='ru'?'Отмена':'Cancel'}</button><button>${lang==='ru'?'Опубликовать':'Post'}</button>';
            d.querySelectorAll('button')[1].onclick=async()=>{await fetch('/fixture/confirm',{method:'POST'});location.href='/tiktokstudio/content';};document.body.append(d);};</script>`});
      });
      const page=await context.newPage();await page.goto("https://www.tiktok.com/tiktokstudio/upload");
      const caption=lang==='ru'?"Бот в профиле 👇\n\n#бинодекс #трейдинг":"Bot in profile 👇\n\n#binodex #trading";
      const typedAt=Date.now();await fillCaption(page,caption);assert(Date.now()-typedAt>=1000,"caption still inserted in a burst");assert(await page.evaluate(()=>window.keys)>10,"caption was not typed through keyboard");
      let persisted=false, accepted=false;
      const task=native.publishNative(page,{caption},{beforeSubmit:async()=>{
        persisted=true;
        assert(await page.evaluate(()=>window.scrolledAt>0&&Date.now()-window.scrolledAt>=1900),"no two-second pause after scrolling");
        assert(!await page.getByRole("radio",{name:"Schedule",exact:true}).isChecked());
      },accepted:async r=>{accepted=true;assert(persisted&&r.id==='222');},reviewTimeout:pending?2500:8000,pollMs:50});
      if(pending)await assert.rejects(task,/доступ «Все»/);
      else {const result=await task;assert(result.public&&!result.review);}
      assert.equal(clicks,1);assert.equal(confirmations,1);assert(accepted);assert.equal(captions,caption);
      assert(page.url().includes('/content'),"did not stay in the resulting Studio list");
      assert.equal(context.pages().length,1,"left an extra verification tab open");
      const again=await native.recheckNative(page,{caption,publishedVideoId:'222'},{timeout:1000});assert(pending?again.review:again.public);assert.equal(clicks,1,"status check uploaded a duplicate");
      await assert.rejects(native.recheckNative(page,{caption},{timeout:1000}),/ID публикации/);
      if(!pending){await native.openNextUpload(page);assert(page.url().includes('/upload'));assert.equal(await page.locator('input[type=file]').count(),1);}
      await context.close();
    }
    // Reconciliation must never treat an unverified first page as the full inventory.
    const reconcileContext=await browser.newContext();let files=0,postRequests=0;
    await reconcileContext.route("**/*",route=>{
      if(route.request().method()!=="GET")postRequests++;
      return route.fulfill({contentType:"text/html",body:'<a href="/tiktokstudio/content">Posts</a><input type="file"><table><tbody><tr data-row-key="manual-1"><td><span title="Full manual caption">Full manual…</span></td><td>Everyone</td></tr></tbody></table><script>document.querySelector("input").onchange=()=>fetch("/file",{method:"POST"})</script>'});
    });
    const reconcilePage=await reconcileContext.newPage();await reconcilePage.goto("https://www.tiktok.com/tiktokstudio/upload");
    const idResults=await native.reconcileNative(reconcilePage,[{publishedVideoId:"manual-1",caption:"changed"}],{matched:async r=>{assert.equal(r.id,"manual-1");files++;}});
    assert(idResults[0].public);assert.equal(files,1);
    await assert.rejects(native.reconcileNative(reconcilePage,[{caption:"Full manual caption"}]),/не полностью/);
    assert.equal(postRequests,0,"reconciliation transmitted a file or post");assert.equal(reconcileContext.pages().length,1,"reconciliation leaked its content tab");
    await reconcileContext.route("**/*",route=>route.fulfill({contentType:"text/html",body:'<a href="/tiktokstudio/content">Posts</a><p data-e2e="empty">No posts</p>'}));
    await reconcilePage.reload();
    assert.deepEqual(await native.reconcileNative(reconcilePage,[{caption:"new caption"}]),[null]);
    await assert.rejects(native.reconcileNative(reconcilePage,[{caption:"new caption",studioPending:true}]),/блокировка повтора/);
    await reconcileContext.close();
    console.log("PASS: reconciliation read-only ID recovery, incomplete list blocks caption matching, empty list, uncertain no-match blocks, verification tab cleanup");
    const invalid=await browser.newPage();await invalid.setContent('<span>Now</span><label><input type=radio aria-label=Schedule>Schedule</label><section><h2>Who can watch this video</h2><button>Everyone</button></section>');
    await assert.rejects(native.immediatePublic(invalid),/переключатель/);await invalid.close();
    assert.equal(native.matchRow([{id:'7',text:'same caption Все',caption:'same caption'}],'','same caption',new Set(['7'])),null,"old same caption was mistaken for a new post");
    assert(native.matchRow([{id:'7',text:'wrong Only me',caption:'wrong'}],'7','wrong',new Set()).review);
    assert.throws(()=>native.matchRow([{id:'7',text:'wrong Friends',caption:'wrong'}],'7','wrong',new Set()),/доступ/);
    console.log("PASS: Chromium TikTok RU/EN captions, scroll + 2s pause, single publish, review/Only me → public, pending timeout stops queue, ID-only recheck, Upload → Video batch navigation");
    if(!process.argv.includes('--tiktok-only'))console.log("PASS: YouTube image-input targeting/save/reload/rejection");
  }finally{await browser.close();fs.rmSync(root,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});
