"use strict";
const fs=require("fs"), path=require("path");
const HOST="ytcp-video-thumbnail-editor, ytcp-thumbnails-compact-editor, #thumbnail-editor";
const UPLOAD=/^(Upload thumbnail|Upload file|Загрузить значок|Загрузить файл|Загрузить превью)$/i;
const SAVE=/^(Save|Save changes|Сохранить|Сохранить изменения)$/i;
async function oneVisible(locator) {
  const result=[];
  for(let i=0;i<await locator.count();i++) if(await locator.nth(i).isVisible()) result.push(locator.nth(i));
  if(result.length>1) throw new Error("Найдено несколько редакторов/кнопок превью. Выбери один ролик.");
  return result[0] || null;
}
async function imageSources(scope) {
  return scope.locator(HOST.split(",").map(s=>s.trim()+":visible").join(",")).locator("img").evaluateAll(nodes=>nodes.map(n=>n.currentSrc || n.src).filter(Boolean));
}
async function until(page,read,timeout=30000) {
  const end=Date.now()+timeout;
  do {const result=await read();if(result)return result;await page.waitForTimeout(250);}while(Date.now()<end);
  throw new Error("YouTube не подтвердил загрузку/сохранение превью. Проверь право канала на пользовательские превью.");
}
async function uploadThumbnail(page,file,{scope=page,save=false,verifyReload=false,timeout=30000}={}) {
  if(!file)return null;
  if(!fs.existsSync(file) || !/\.(png|jpe?g)$/i.test(path.extname(file)))throw new Error("Превью: нужен существующий JPG/PNG.");
  if(fs.statSync(file).size>50*1024*1024)throw new Error("Превью превышает 50 МБ.");
  const editor=await until(page,()=>oneVisible(scope.locator(HOST)),timeout);
  await editor.scrollIntoViewIfNeeded();
  const before=new Set(await imageSources(scope));
  let inputs=editor.locator('input[type="file"]');
  if(!await inputs.count()) {
    const open=await oneVisible(editor.getByRole("button",{name:UPLOAD}));
    if(open)await open.click();
    // Some Studio versions attach their hidden input beside the editor.
    inputs=scope.locator('input[type="file"][accept*="image"], input[type="file"][accept*="png"], input[type="file"][accept*="jpeg"]');
  }
  if(await inputs.count()!==1)throw new Error("Не найдено однозначное поле изображения в редакторе превью.");
  await inputs.setInputFiles(file);
  const selected=await until(page,async()=>{
    const errors=await editor.locator('[role="alert"], .error, ytcp-error-tip').allTextContents();
    if(errors.some(t=>t.trim()))throw new Error("YouTube отклонил превью: "+errors.join(" ").slice(0,180));
    const changed=[...new Set((await imageSources(scope)).filter(src=>/^https:\/\//.test(src)&&!before.has(src)))];
    return changed.length===1 ? changed[0] : null;
  },timeout);
  if(save) {
    const button=await until(page,async()=>{
      const b=await oneVisible(scope.getByRole("button",{name:SAVE}));
      return b && !await b.isDisabled() ? b : null;
    },timeout);
    await button.click();
    await until(page,async()=>!await button.isVisible() || await button.isDisabled() ||
      await scope.getByText(/changes saved|изменения сохранены/i).first().isVisible().catch(()=>false),timeout);
    if(verifyReload) {
      await page.reload({waitUntil:"domcontentloaded",timeout:90000});
      await until(page,async()=>(await imageSources(page)).includes(selected),timeout);
    }
  }
  return selected;
}
module.exports={uploadThumbnail,imageSources};
