"use strict";
const fs=require('fs'),path=require('path');
const HOST='ytcp-video-thumbnail-editor, ytcp-thumbnails-compact-editor, #thumbnail-editor';
const UPLOAD=/^(Upload thumbnail|Upload file|Загрузить значок|Загрузить файл|Загрузить превью)$/i;
const SAVE=/^(Save|Save changes|Сохранить|Сохранить изменения)$/i;
async function oneVisible(locator){const found=[];for(let i=0;i<await locator.count();i++)if(await locator.nth(i).isVisible())found.push(locator.nth(i));if(found.length>1)throw new Error('Несколько редакторов/кнопок превью. Выбери один ролик.');return found[0]||null;}
async function editorRoot(scope){const candidates=scope.locator(HOST),roots=[];for(let i=0;i<await candidates.count();i++){const node=candidates.nth(i);if(!await node.isVisible())continue;const nested=await node.evaluate((e,selector)=>{const p=e.parentElement?.closest(selector);if(!p)return false;const b=p.getBoundingClientRect();return b.width>0&&b.height>0;},HOST);if(!nested)roots.push(node);}if(roots.length>1)throw new Error('Несколько независимых редакторов превью. Выбери один ролик.');return roots[0]||null;}
async function imageSources(scope){const editor=await editorRoot(scope);return editor?editor.locator('img').evaluateAll(nodes=>nodes.map(n=>n.currentSrc||n.src).filter(Boolean)):[];}
async function until(page,read,timeout=30000){const end=Date.now()+timeout;do{const result=await read();if(result)return result;await page.waitForTimeout(250);}while(Date.now()<end);throw new Error('YouTube не подтвердил выбранное превью. Видео повторно не загружай; проверь редактор существующего ролика.');}
function fileData(file){if(!fs.existsSync(file)||!/^\.(png|jpe?g|webp)$/i.test(path.extname(file)))throw new Error('Превью: нужен существующий JPG/PNG/WebP.');if(fs.statSync(file).size>50*1024*1024)throw new Error('Превью превышает 50 МБ.');const type=/\.webp$/i.test(file)?'image/webp':/\.png$/i.test(file)?'image/png':'image/jpeg';return {type,data:'data:'+type+';base64,'+fs.readFileSync(file).toString('base64')};}
async function compareImages(page,shot,reference,style){
 return page.evaluate(async ({actual,source,style})=>{
  const load=src=>new Promise((resolve,reject)=>{const i=new Image();i.onerror=()=>reject(new Error('Не удалось прочитать изображение превью'));i.onload=()=>resolve(i);i.src=src;});
  const [a,s]=await Promise.all([load(actual),load(source)]),canvas=document.createElement('canvas');canvas.width=a.width;canvas.height=a.height;const c=canvas.getContext('2d',{willReadFrequently:true});
  c.drawImage(a,0,0);const got=c.getImageData(0,0,a.width,a.height);c.fillStyle=style.bg;c.fillRect(0,0,a.width,a.height);
  let w=a.width,h=a.height,x=0,y=0;
  if(style.fit==='contain'||style.fit==='cover'){const scale=style.fit==='cover'?Math.max(w/s.width,h/s.height):Math.min(w/s.width,h/s.height);w=s.width*scale;h=s.height*scale;const pos=style.position.split(' ').map(v=>parseFloat(v)/100);x=(a.width-w)*(Number.isFinite(pos[0])?pos[0]:.5);y=(a.height-h)*(Number.isFinite(pos[1])?pos[1]:.5);}
  c.drawImage(s,x,y,w,h);const expected=c.getImageData(0,0,a.width,a.height);let abs=0,square=0,n=0;
  const edge=Math.min(2,Math.floor(Math.min(a.width,a.height)/10));
  for(let py=edge;py<a.height-edge;py++)for(let px=edge;px<a.width-edge;px++){const at=(py*a.width+px)*4;for(let ch=0;ch<3;ch++){const d=got.data[at+ch]-expected.data[at+ch];abs+=Math.abs(d);square+=d*d;n++;}}
  return n>0 && abs/n<=10 && Math.sqrt(square/n)<=25;
 },{actual:'data:image/png;base64,'+shot.toString('base64'),source:reference.data,style});
}
async function matches(page,img,reference){
 if(!await img.isVisible()||!await img.evaluate(e=>e.complete&&e.naturalWidth>0))return false;
 const rect=await img.evaluate(e=>{const r=e.getBoundingClientRect();return r.left>=0&&r.top>=0&&r.right<=innerWidth&&r.bottom<=innerHeight&&r.width>=8&&r.height>=8?{x:r.left,y:r.top,width:r.width,height:r.height}:null;});
 if(!rect)return false;
 const style=await img.evaluate(e=>{const css=getComputedStyle(e);let p=e,bg='rgb(255,255,255)';while(p){const color=getComputedStyle(p).backgroundColor;if(color!=='rgba(0, 0, 0, 0)'&&color!=='transparent'){bg=color;break;}p=p.parentElement;}return {fit:css.objectFit,position:css.objectPosition,bg};});
 // Locator.screenshot scrolls to every suggestion and waits for its animations.
 // Capture only the already-visible rectangle; never move the upload wizard.
 const shot=await page.screenshot({clip:rect,scale:'css',animations:'allow',caret:'initial',timeout:5000});
 return compareImages(page,shot,reference,style);
}
async function matchingImage(page,editor,reference,{persistent=false,cache=new Map()}={}){
 const imgs=editor.locator('img');
 for(let i=0;i<await imgs.count();i++){
  const img=imgs.nth(i),info=await img.evaluate(e=>{const r=e.getBoundingClientRect();return {src:e.currentSrc||e.src,ready:e.complete&&e.naturalWidth>0,key:JSON.stringify([e.currentSrc||e.src,e.naturalWidth,e.naturalHeight,r.x,r.y,r.width,r.height])};});
  if(!info.ready||persistent&&!/^https:\/\//.test(info.src||''))continue;
  // Readback may show a generated suggestion while the saved custom image is
  // outside the viewport. Reveal each persistent candidate, not just the first
  // visible image in the editor. Never scroll the live upload wizard here.
  if(persistent&&await img.isVisible())await img.scrollIntoViewIfNeeded({timeout:5000});
  const old=cache.get(info.key);if(old&&Date.now()-old.at<2000){if(old.match)return info.src;continue;}
  const match=await matches(page,img,reference);cache.set(info.key,{at:Date.now(),match});if(match)return info.src;
 }return null;
}
async function revealEditor(editor){
 const images=editor.locator('img');
 for(const img of await images.all())if(await img.isVisible()&&await img.evaluate(e=>{const r=e.getBoundingClientRect();return r.left>=0&&r.top>=0&&r.right<=innerWidth&&r.bottom<=innerHeight;}))return;
 for(const img of await images.all())if(await img.isVisible()){await img.scrollIntoViewIfNeeded();return;}
 await editor.scrollIntoViewIfNeeded();
}
async function verifyThumbnail(page,file,{scope=page,timeout=30000}={}){
 const reference=fileData(file),cache=new Map();const editor=await until(page,()=>editorRoot(scope),timeout);await revealEditor(editor);
 return until(page,()=>matchingImage(page,editor,reference,{persistent:true,cache}),timeout);
}
async function pngPayload(page,file,reference){
 if(reference.type!=='image/webp')return file;
 const data=await page.evaluate(source=>new Promise((resolve,reject)=>{const i=new Image();i.onerror=()=>reject(new Error('WebP не читается'));i.onload=()=>{const c=document.createElement('canvas');c.width=i.width;c.height=i.height;c.getContext('2d').drawImage(i,0,0);resolve(c.toDataURL('image/png').split(',')[1]);};i.src=source;}),reference.data);
 const buffer=Buffer.from(data,'base64');if(buffer.length>50*1024*1024)throw new Error('PNG после преобразования больше 50 МБ.');
 return {name:path.basename(file,path.extname(file))+'.png',mimeType:'image/png',buffer};
}
async function uploadThumbnail(page,file,{scope=page,save=false,verifyReload=false,timeout=30000,progress=()=>{}}={}){
 if(!file)return null;const reference=fileData(file);progress('Превью: открываю редактор изображения');
 const editor=await until(page,()=>editorRoot(scope),timeout);await revealEditor(editor);
 if(save&&verifyReload&&await matchingImage(page,editor,reference,{persistent:true})) {
  await page.reload({waitUntil:'domcontentloaded',timeout:90000});
  const confirmed=await verifyThumbnail(page,file,{timeout});progress('Превью: выбранная картинка уже сохранена, подтверждено после перезагрузки');return confirmed;
 }
 let inputs=editor.locator('input[type="file"]');if(!await inputs.count()){const open=await oneVisible(editor.getByRole('button',{name:UPLOAD}));if(open)await open.click();inputs=scope.locator('input[type="file"][accept*="image" i],input[type="file"][accept*="png" i],input[type="file"][accept*="jpeg" i]');}
 if(await inputs.count()!==1)throw new Error('Не найдено однозначное поле изображения в редакторе превью.');
 await inputs.setInputFiles(await pngPayload(page,file,reference));
 const cache=new Map();
 const selected=await until(page,async()=>{const errors=await editor.locator('[role="alert"],.error,ytcp-error-tip').allTextContents();if(errors.some(t=>t.trim()))throw new Error('YouTube отклонил превью: '+errors.join(' ').slice(0,180));return matchingImage(page,editor,reference,{cache});},timeout);
 progress('Превью: выбранная картинка подтверждена в редакторе');
 if(save){const button=await until(page,async()=>{const b=await oneVisible(scope.getByRole('button',{name:SAVE}));return b&&!await b.isDisabled()?b:null;},timeout);await button.click();
  await until(page,async()=>!await button.isVisible()||await button.isDisabled()||await scope.getByText(/changes saved|изменения сохранены/i).first().isVisible().catch(()=>false),timeout);
  progress('Превью: сохранено, проверяю результат');
  if(verifyReload){await page.reload({waitUntil:'domcontentloaded',timeout:90000});const result=await verifyThumbnail(page,file,{timeout});progress('Превью: картинка подтверждена после перезагрузки');return result;}
 }
 return selected;
}
module.exports={uploadThumbnail,verifyThumbnail,imageSources,matches};
