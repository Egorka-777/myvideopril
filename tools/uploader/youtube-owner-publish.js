'use strict';
// Publish only saved video IDs through the selected Studio profile. One author
// comment per owned video; no likes, viewing loops, search or third-party comments.
const S=require('./youtube-studio-upload');
const ID=/^[A-Za-z0-9_-]{11}$/;
const EDITOR='ytcp-video-metadata-editor,ytcp-video-details';
const VISIBILITY='ytcp-video-visibility-edit-dialog,ytcp-video-visibility-select-dialog,ytcp-visibility-dialog';
const SAVE=/^(Save|Сохранить|Done|Готово|Publish|Опубликовать)$/i;
async function visible(locator){return S.one(locator,{optional:true});}
async function enabled(button){return S.enabled(button);}
async function visibilityControl(page){
 for(const selector of ['#visibility-button','#visibility','ytcp-video-visibility-select']){const control=await visible(page.locator(selector));if(control)return control;}return null;
}
function parseVisibility(label){
 label=S.norm(label);
 if(/^(Public|Открытый доступ|Доступно всем)(?:\s|$)/i.test(label))return 'public';
 if(/Scheduled|Запланирован|Публикация запланирована/i.test(label))return 'scheduled';
 if(/Private|Ограниченный доступ|Закрытый доступ/i.test(label))return 'private';
 if(/Unlisted|Доступ по ссылке|По ссылке/i.test(label))return 'unlisted';
 return '';
}
async function readVisibility(page){
 const control=await visibilityControl(page);
 return control?parseVisibility((await control.innerText())+' '+(await control.getAttribute('aria-label')||'')):'';
}
async function editor(page,id){
 await page.goto(`https://studio.youtube.com/video/${id}/edit`,{waitUntil:'domcontentloaded',timeout:35000});
 await S.until(async()=>{
  if(/accounts\.google\.com/.test(page.url()))throw new Error('Требуется вход в выбранный профиль Google.');
  if(!new URL(page.url()).pathname.startsWith('/video/'+id+'/'))return null;
  const root=await visible(page.locator(EDITOR));return root&&await S.field(root,'title',true)?root:null;
 },30000,'Studio не открыл редактор указанного видео. Доступ не менялся.');
 await S.until(()=>readVisibility(page),15000,'Studio не показал распознаваемый статус доступа.');
}
async function publish(page,item,{progress=()=>{},verifyMs=30000}={}){
 const id=item.publishedVideoId;
 await editor(page,id);
 if(await readVisibility(page)==='public'){progress('owner_publish','Видео уже в открытом доступе.',{videoId:id});return;}
 const control=await visibilityControl(page);await control.click();
 const dialog=await S.until(()=>visible(page.locator(VISIBILITY)),10000,'Studio не открыл окно изменения доступа.');
 // Stable Public radio + footer. The Schedule card is never a submit fallback.
 const button=await S.publishPublic(page,dialog);
 if(!SAVE.test(S.norm(await button.innerText())))throw new Error('Studio не показал кнопку сохранения открытого доступа.');
 await button.click();
 await S.until(async()=>!await S.scopeAlive(dialog),15000,'Studio не закрыл окно доступа после подтверждения. Проверьте видео перед повтором.');
 // Some Studio versions apply the dialog's Done locally, then require the
 // editor's Save. Other versions commit in the dialog and have no dirty Save.
 let save=null;
 const saveDeadline=Date.now()+3000;
 do{
  save=await S.actionButton(page,'save-button',/^(Save|Сохранить)$/i)
   ||await S.actionButton(page,'save',/^(Save|Сохранить)$/i);
  if(await enabled(save)){await save.click();break;}
  if(!save)break;
  await page.waitForTimeout(150);
 }while(Date.now()<saveDeadline);
 // A fresh editor tab is the proof of persisted Public; no reload of dirty UI.
 const check=await page.context().newPage();
 try{await editor(check,id);await S.until(async()=>await readVisibility(check)==='public',verifyMs,'YouTube не подтвердил открытый доступ после сохранения. Комментарий не отправлен.');}
 finally{await check.close().catch(()=>{});}
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
  return {text:body?.innerText||'',author:name?.getAttribute('href')||'',id};
 }));
 return rows.filter(x=>S.norm(x.text)===S.norm(text)&&canonicalAuthor(x.author)===author&&x.id);
}
async function comment(page,item,{progress=()=>{},checkpoint,commentMs=30000,typingDelay=45}={}){
 const id=item.publishedVideoId,text=S.norm(item.commentText);
 if(!text||item.commentState==='confirmed')return;
 if(item.commentState==='pending')throw new Error('Предыдущая отправка комментария не подтверждена. Проверьте видео вручную перед повтором.');
 await page.goto(`https://www.youtube.com/watch?v=${id}`,{waitUntil:'domcontentloaded',timeout:35000});
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
 await S.typeText(box,text,{delay:typingDelay});
 const button=await S.until(async()=>{const b=await S.actionButton(comments,'submit-button',/^(Comment|Post comment|Оставить комментарий|Комментировать)$/i);return await enabled(b)?b:null;},10000,'Кнопка отправки комментария недоступна.');
 // Re-check after typing, before the single irreversible click.
 await ownVideo(page,id);
 if((await commentMatches(page,text,author)).length)throw new Error('Такой комментарий появился во время подготовки. Повторная отправка остановлена.');
 if(typeof checkpoint!=='function')throw new Error('Нет подтверждения сохранения состояния комментария. Отправка отменена.');
 await checkpoint(item,item._packIndex);
 item.commentState='pending';
 await button.click();
 const found=await S.until(async()=>{const matches=await commentMatches(page,text,author);return matches.length===1?matches[0]:null;},commentMs,'Комментарий мог отправиться, но YouTube не показал его ID. Повтор заблокирован до ручной проверки.');
 progress('owner_comment_confirmed','Комментарий автора подтверждён.',{videoId:id,commentId:found.id,commentText:text});
 item.commentState='confirmed';
}
async function runSequential(page,items,host){
 if(!items.length)throw new Error('Не выбраны загруженные видео для публикации.');
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
  progress('owner_video_public','Открытый доступ проверен в Studio.',{visibility:'public'});
  await comment(page,item,{...host,progress});
  progress('owner_item_confirmed','Готово: публикация и комментарий автора проверены.');
 }
}
module.exports={parseVisibility,canonicalAuthor,readVisibility,ownVideo,commentMatches,publish,comment,runSequential};
