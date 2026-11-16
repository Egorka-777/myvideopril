'use strict';
// Publish only saved video IDs through the selected Studio profile. One author
// comment per owned video; no likes, viewing loops, search or third-party comments.
const S=require('./youtube-studio-upload');
const BUILD='2026-11-02-owner-publish-public-v3';
const ID=/^[A-Za-z0-9_-]{11}$/;
const EDITOR='ytcp-video-metadata-editor,ytcp-video-details';
const VISIBILITY='ytcp-video-visibility-edit-dialog,ytcp-video-visibility-select-dialog,ytcp-visibility-dialog,tp-yt-paper-dialog#dialog,ytcp-video-visibility-select';
const SAVE=/^(Save|Сохранить|Done|Готово|Publish|Опубликовать|OK|ОК)$/i;
async function visible(locator){return S.one(locator,{optional:true});}
async function enabled(button){return S.enabled(button);}
async function pacedClick(control,actionDelayMs,afterDelayMs=actionDelayMs){
 if(actionDelayMs)await control.page().waitForTimeout(actionDelayMs);
 await control.click();
 if(afterDelayMs)await control.page().waitForTimeout(afterDelayMs);
}
async function visibilityControl(page){
 for(const selector of ['#visibility-button','#visibility','ytcp-video-visibility-select','#select-button[aria-label="Изменить параметры доступа к видео"],#select-button[aria-label="Edit video visibility"]']){const control=await visible(page.locator(selector));if(control)return control;}
 const root=await visible(page.locator(EDITOR));
 return root?visible(root.getByRole('button',{name:/visibility|доступ|scheduled|отложенная публ|запланирован|для всех|^public$/i})):null;
}
function parseVisibility(label){
 label=S.norm(label).replace(/^(?:Visibility|Access|Доступ)\s*:\s*/i,'');
 if(/^(Public|Открытый доступ|Доступно всем|Для всех)(?:\s|$)/i.test(label))return 'public';
 if(/Scheduled|Запланирован|Публикация запланирована|Отложенная публ/i.test(label))return 'scheduled';
 if(/Private|Ограниченный доступ|Закрытый доступ/i.test(label))return 'private';
 if(/Unlisted|Доступ по ссылке|По ссылке/i.test(label))return 'unlisted';
 return '';
}
async function visibilitySummary(control){
 const label=(await control.innerText())+' '+(await control.getAttribute('aria-label')||'');
 if(parseVisibility(label))return label;
 // Studio's select-button is an icon. The persisted value is rendered beside
 // it in the visibility card, not inside the icon's accessible label.
 return control.evaluate(el=>{
  for(let p=el;p;p=p.parentElement||p.getRootNode()?.host){
   const text=(p.innerText||p.textContent||'').replace(/\s+/g,' ').trim();
   if(text.length>300)break;
   if(/Public|Открытый доступ|Доступно всем|Для всех|Private|Ограниченный доступ|Закрытый доступ|Unlisted|Доступ по ссылке|По ссылке|Scheduled|Запланирован|Отложенная публ/i.test(text))return text.replace(/^(?:Параметры доступа|Visibility|Доступ)\s*/i,'');
  }
  return '';
 });
}
async function readVisibility(page){
 const control=await visibilityControl(page);
 return control?parseVisibility(await visibilitySummary(control)):'';
}
async function editor(page,id){
 await page.goto(`https://studio.youtube.com/video/${id}/edit`,{waitUntil:'domcontentloaded',timeout:35000});
 await S.until(async()=>{
  if(/accounts\.google\.com/.test(page.url()))throw new Error('Требуется вход в выбранный профиль Google.');
  if(!new URL(page.url()).pathname.startsWith('/video/'+id+'/'))return null;
  const root=await visible(page.locator(EDITOR));return root&&await S.field(root,'title',true)?root:null;
 },30000,'Studio не открыл редактор указанного видео. Доступ не менялся.');
 // The label is informational. A changed translation must not block opening
 // the native visibility picker, where PUBLIC is selected explicitly.
 await S.until(()=>visibilityControl(page),60000,'Studio не показал управление доступом указанного видео.');
}
async function visibilityDialog(page){
 const locator=page.locator(VISIBILITY);
 const indices=await locator.evaluateAll(nodes=>{
  const parent=e=>e.parentElement||e.getRootNode().host;
  const visible=e=>{if(!e.getClientRects().length)return false;for(let p=e;p;p=parent(p)){const s=getComputedStyle(p);if(p.hidden||s.display==='none'||s.visibility==='hidden')return false;}return true;};
  // display:contents hosts have no box, but their live popup does.
  const alive=e=>visible(e)||Array.from(e.querySelectorAll('tp-yt-paper-dialog,ytcp-dialog,[role=dialog],input[type=radio],tp-yt-paper-radio-button')).some(visible);
  const live=nodes.filter(alive);
  return live.filter(e=>{for(let p=parent(e);p;p=parent(p))if(live.includes(p))return false;return true;}).map(e=>nodes.indexOf(e));
 });
 const roots=indices.map(i=>locator.nth(i));
 if(!roots.length){
  const fallback=await visible(page.locator('[role=dialog]').filter({hasText:/Save or publish|Сохранить или опубликовать|Открытый доступ|Public/i}));
  if(fallback)roots.push(fallback);
 }
 // Nested hosts represent one physical popup; separate live roots remain ambiguous.
 if(roots.length>1)throw new Error('Studio открыл несколько окон доступа. Выбор видео неоднозначен.');
 return roots[0]||null;
}
async function publicRadio(scope){
 return visible(scope.locator('#public-radio-button,[name=PUBLIC],tp-yt-paper-radio-button[name=PUBLIC]').or(scope.getByRole('radio',{name:S.L.public})));
}
async function expandPublish(scope,actionDelayMs=2000){
 if(await publicRadio(scope))return;
 const card=await S.until(()=>visible(scope.getByText(/^\s*(Save\s+or\s+publish|Сохранить\s+или\s+опубликовать)\s*$/i)),15000,'Studio не показал блок «Сохранить или опубликовать / Save or publish».');
 await pacedClick(card,actionDelayMs);
 await S.until(()=>publicRadio(scope),15000,'Studio не раскрыл варианты доступа после «Сохранить или опубликовать».');
}
async function publish(page,item,{progress=()=>{},actionDelayMs=2000,confirmDelayMs=5000}={}){
 const id=item.publishedVideoId;
 await editor(page,id);
 if(await readVisibility(page)==='public'){progress('owner_publish','Видео уже в открытом доступе.',{videoId:id});return;}
 const control=await visibilityControl(page);await pacedClick(control,actionDelayMs);
 const dialog=await S.until(()=>visibilityDialog(page),30000,'Studio не открыл окно изменения доступа.');
 progress('owner_publish','Раскрываю «Сохранить или опубликовать / Save or publish».',{videoId:id});
 await expandPublish(dialog,actionDelayMs);
 progress('owner_publish','Выбираю «Открытый доступ / Public» и жду активную «ОК / Publish».',{videoId:id});
 // Editor dialog confirms through #save-button (OK), not upload wizard #done-button.
 const button=await S.confirmEditorVisibility(dialog,{actionDelayMs});
 await S.until(async()=>{const radio=await publicRadio(dialog);return radio&&await S.publicRadioSelected(radio)&&await enabled(button);},15000,'Studio ещё не применил выбор открытого доступа.');
 if(!SAVE.test(S.norm(await button.innerText())))throw new Error('Studio не показал кнопку сохранения открытого доступа.');
 progress('owner_publish','Выбран открытый доступ; подтверждаю публикацию один раз.',{videoId:id});
 await pacedClick(button,actionDelayMs,confirmDelayMs);
 await S.until(async()=>!await S.scopeAlive(dialog),15000,'Studio не закрыл окно доступа после подтверждения. Проверьте видео перед повтором.');
 // Some Studio versions apply the dialog's Done locally, then require the
 // editor's Save. Other versions commit in the dialog and have no dirty Save.
 let save=null;
 const saveDeadline=Date.now()+15000;
 do{
  save=await S.actionButton(page,'save',/^(Save|Сохранить)$/i)
   ||await S.actionButton(page,'save-bar',/^(Save|Сохранить)$/i);
  if(await enabled(save)){
   progress('owner_publish','Нажимаю верхнюю «Сохранить».',{videoId:id});
   await pacedClick(save,actionDelayMs);
   await S.until(async()=>!await enabled(save),30000,'Studio не завершил сохранение открытого доступа. Комментарий не отправлен.');
   progress('owner_publish','Верхняя «Сохранить» нажата, кнопка стала неактивной.',{videoId:id});
   break;
  }
  await page.waitForTimeout(150);
 }while(Date.now()<saveDeadline);
}
function canonicalAuthor(href){
 try{const u=new URL(href,'https://www.youtube.com');return u.hostname.replace(/^www\./,'')==='youtube.com'?u.pathname.replace(/\/$/,'').toLowerCase():'';}catch{return '';}
}
async function ownVideo(page,id){
 const current=new URL(page.url());
 if(current.hostname.replace(/^www\./,'')!=='youtube.com'||current.searchParams.get('v')!==id)throw new Error('Открылась страница другого видео. Комментарий не отправлен.');
 // The edit control is supplied only to a signed-in owner/channel manager.
 const edit=await visible(page.locator(`#owner a[href*="studio.youtube.com/video/${id}/edit"],ytd-video-owner-renderer a[href*="/video/${id}/edit"]`).or(page.getByRole('link',{name:/^(Edit video|Изменить видео)$/i})).or(page.getByRole('button',{name:/^(Edit video|Изменить видео)$/i})));
 if(!edit)throw new Error('YouTube не подтвердил, что выбранный профиль может редактировать это видео. Комментарий не отправлен.');
 const hrefs=await page.locator('#owner #channel-name a[href],#owner #upload-info a[href],ytd-video-owner-renderer ytd-channel-name a[href]').evaluateAll(nodes=>nodes.map(e=>e.getAttribute('href')));
 const authors=[...new Set(hrefs.map(canonicalAuthor).filter(x=>/^\/(?:@|channel\/)/.test(x)))];
 if(authors.length!==1)throw new Error('YouTube не показал однозначного автора видео. Комментарий не отправлен.');
 return authors[0];
}
async function commentMatches(page,text,author){
 const rows=await page.locator('#comments ytd-comment-thread-renderer').evaluateAll(nodes=>nodes.map(e=>{
  const body=e.querySelector('#content-text');const name=e.querySelector('#author-text');
  const stamp=e.querySelector('a[href*="lc="]');
  let id='';try{id=new URL(stamp?.getAttribute('href')||'',location.href).searchParams.get('lc')||'';}catch{}
  const model=e.querySelector('ytd-comment-view-model,ytd-comment-renderer');
  id=id||model?.data?.commentId||'';
  const copy=body?.cloneNode(true);
  if(copy){copy.querySelectorAll('img[alt]').forEach(img=>img.replaceWith(document.createTextNode(img.alt)));copy.querySelectorAll('br').forEach(br=>br.replaceWith(document.createTextNode('\n')));}
  return {text:copy?.textContent||'',author:name?.getAttribute('href')||'',id};
 }));
 return rows.filter(x=>S.norm(x.text)===S.norm(text)&&canonicalAuthor(x.author)===author&&x.id);
}
async function readComment(box){
 return S.norm(await box.evaluate(e=>{
  const copy=e.cloneNode(true);
  copy.querySelectorAll('img[alt]').forEach(img=>img.replaceWith(document.createTextNode(img.alt)));
  copy.querySelectorAll('br').forEach(br=>br.replaceWith(document.createTextNode('\n')));
  return copy.textContent;
 }));
}
async function comment(studioPage,item,options={}){
 if(!S.norm(item.commentText)||item.commentState==='confirmed'||item.commentState==='sent')return;
 if(item.commentState==='pending')throw new Error('Предыдущая отправка комментария не подтверждена. Проверьте видео вручную перед повтором.');
 const page=await studioPage.context().newPage();
 if(options.usePage)options.usePage(page);
 return commentOnWatch(page,item,options);
}
async function commentOnWatch(page,item,{progress=()=>{},checkpoint,commentMs=30000,typingDelay=45}={}){
 const id=item.publishedVideoId,text=S.norm(item.commentText);
 if(!text||item.commentState==='confirmed'||item.commentState==='sent')return;
 if(item.commentState==='pending')throw new Error('Предыдущая отправка комментария не подтверждена. Проверьте видео вручную перед повтором.');
 progress('owner_comment','Открываю опубликованное видео на YouTube для комментария автора.',{videoId:id});
 await page.goto(`https://www.youtube.com/watch?v=${id}`,{waitUntil:'domcontentloaded',timeout:60000});
 progress('owner_comment','Открыта страница комментария: '+page.url(),{videoId:id});
 const current=new URL(page.url());
 if(current.hostname!=='www.youtube.com'||current.pathname!=='/watch'||current.searchParams.get('v')!==id)throw new Error('Не открылась страница просмотра указанного видео: '+page.url()+'. Комментарий не отправлен.');
 // This page is opened for its author controls, not for automated engagement.
 await page.locator('video').evaluateAll(nodes=>nodes.forEach(v=>v.pause()));
 await S.until(()=>visible(page.getByRole('link',{name:/^(Edit video|Изменить видео)$/i}).or(page.getByRole('button',{name:/^(Edit video|Изменить видео)$/i})).or(page.locator(`#owner a[href*="/video/${id}/edit"]`))),15000,'На странице видео нет управления автора. Комментарий не отправлен.');
 const author=await ownVideo(page,id);
 const comments=page.locator('#comments').first();await comments.scrollIntoViewIfNeeded();
 const already=await commentMatches(page,text,author);
 if(already.length){progress('owner_comment_confirmed','Такой комментарий автора уже есть; повтор пропущен.',{videoId:id,commentId:already[0].id,commentText:text});return;}
 const placeholder=await S.until(()=>visible(comments.locator('#simplebox-placeholder').or(comments.getByRole('textbox',{name:/add a comment|добавьте комментарий|оставьте комментарий/i}))),15000,'YouTube не открыл поле комментария. Возможно, комментарии отключены или ещё недоступны.');
 await placeholder.click();
 const box=await S.until(()=>visible(comments.locator('ytd-comment-simplebox-renderer #contenteditable-root[contenteditable=true]').or(comments.getByRole('textbox',{name:/add a comment|добавьте комментарий|оставьте комментарий/i}))),10000,'YouTube не открыл редактор комментария.');
 await S.typeText(box,text,{name:'комментария',delay:typingDelay,readValue:readComment});
 const button=await S.until(async()=>{const b=await S.actionButton(comments,'submit-button',/^(Comment|Post comment|Оставить комментарий|Комментировать)$/i);return await enabled(b)?b:null;},10000,'Кнопка отправки комментария недоступна.');
 // Re-check after typing, before the single irreversible click.
 await ownVideo(page,id);
 if((await commentMatches(page,text,author)).length)throw new Error('Такой комментарий появился во время подготовки. Повторная отправка остановлена.');
 if(typeof checkpoint!=='function')throw new Error('Нет подтверждения сохранения состояния комментария. Отправка отменена.');
 await checkpoint(item,item._packIndex);
 item.commentState='pending';
 await button.click();
 progress('owner_comment_sent','Комментарий отправлен нажатием кнопки, без проверки появления.',{videoId:id,commentText:text});
 item.commentState='sent';
}
async function runSequential(page,items,host){
 if(!items.length)throw new Error('Не выбраны загруженные видео для публикации.');
 host.progress('owner_build','Публикация и комментарий · '+BUILD);
 const seen=new Set();
 for(const item of items){
  if(!ID.test(item.publishedVideoId||''))throw new Error('Нужен сохранённый ID видео; поиск по названию не используется.');
  if(seen.has(item.publishedVideoId))throw new Error('Одно видео выбрано дважды.');seen.add(item.publishedVideoId);
  if(item.commentState==='pending')throw new Error('Есть неподтверждённая отправка комментария. Сначала проверьте её на странице видео.');
  if(item.commentText&&([...item.commentText].length>10000||/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(item.commentText)))throw new Error('Недопустимый текст комментария.');
 }
 for(let i=0;i<items.length;i++){
  const item=items[i];item._packIndex=i+1;
  const progress=(stage,text,extra={})=>host.progress(stage,text,{packIndex:i+1,localJobId:item.localJobId,videoId:item.publishedVideoId,...extra});
  progress('owner_publish',`Публикация ${i+1}/${items.length}: ${item.title||item.publishedVideoId}`);
  await publish(page,item,{...host,progress});
  progress('owner_video_public','Этап установки открытого доступа завершён; повторная страница проверки не открывалась.',{visibility:'public'});
  await comment(page,item,{...host,progress});
  progress('owner_item_confirmed','Готово: видео опубликовано, комментарий обработан без проверки после отправки.');
 }
}
module.exports={BUILD,visibilityDialog,parseVisibility,canonicalAuthor,visibilitySummary,readVisibility,ownVideo,commentMatches,publish,comment,readComment,runSequential};
