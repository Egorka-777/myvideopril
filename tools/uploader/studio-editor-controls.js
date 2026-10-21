'use strict';
const {bounded}=require('./studio-browser');
// Serialized into the page. No text writes, synthetic clicks or token reads.
function inspectEditors(nodes){
 const parent=n=>n?.parentElement||n?.getRootNode()?.host||null;
 const contains=(root,node)=>{for(let p=node;p;p=parent(p))if(p===root)return true;return false;};
 const describe=e=>e?{tag:e.tagName,id:e.id||'',role:e.getAttribute('role')||''}:null;
 return nodes.map(el=>{
  const doc=el.ownerDocument;let active=doc.activeElement,depth=0;
  while(active?.shadowRoot?.activeElement){active=active.shadowRoot.activeElement;depth++;}
  const rect=el.getBoundingClientRect(),style=getComputedStyle(el);
  let visible=!!el.getClientRects().length&&style.visibility!=='hidden',inert=false;
  for(let p=el;p;p=parent(p)){const s=getComputedStyle(p);if(p.hidden||s.display==='none'||s.visibility==='hidden')visible=false;if(p.inert||p.getAttribute('aria-disabled')==='true')inert=true;}
  const editable=!!el.isContentEditable||/^(INPUT|TEXTAREA)$/.test(el.tagName)&&!el.readOnly;
  const deepHit=(x,y)=>{let hit=doc.elementFromPoint(x,y);const visited=new Set();while(hit?.shadowRoot&&!visited.has(hit)){visited.add(hit);const next=hit.shadowRoot.elementFromPoint(x,y);if(!next||next===hit)break;hit=next;}return hit;};
  // Prefer a real unobscured point in the editor, never the overlay itself.
  const clip={left:Math.max(0,rect.left),right:Math.min(doc.documentElement.clientWidth,rect.right),top:Math.max(0,rect.top),bottom:Math.min(doc.documentElement.clientHeight,rect.bottom)};
  for(let p=parent(el);p;p=parent(p)){const css=getComputedStyle(p),r=p.getBoundingClientRect();if(/auto|scroll|hidden|clip/.test(css.overflowX)){clip.left=Math.max(clip.left,r.left);clip.right=Math.min(clip.right,r.right);}if(/auto|scroll|hidden|clip/.test(css.overflowY)){clip.top=Math.max(clip.top,r.top);clip.bottom=Math.min(clip.bottom,r.bottom);}}
  const points=[[rect.width/2,Math.min(16,rect.height/2)],[rect.width/2,rect.height/2],[Math.min(16,rect.width/2),Math.min(16,rect.height/2)]];
  if(clip.right>clip.left&&clip.bottom>clip.top)points.unshift([(clip.left+clip.right)/2-rect.left,Math.min(clip.bottom-1,clip.top+16)-rect.top]);
  let point=null,blocker=null,tooltip=false;
  for(const [x,y] of points){
   if(rect.left+x<0||rect.top+y<0||rect.left+x>=doc.documentElement.clientWidth||rect.top+y>=doc.documentElement.clientHeight)continue;
   const hit=deepHit(rect.left+x,rect.top+y);
   if(contains(el,hit)){point={x,y};break;}
   if(!blocker&&hit){blocker=describe(hit);for(let p=hit;p;p=parent(p))if(p.matches('[role=tooltip],#tooltip,tp-yt-iron-tooltip,ytcp-paper-tooltip-placeholder')){tooltip=true;break;}}
  }
  return {tag:el.tagName,id:el.id||'',role:el.getAttribute('role')||'',connected:el.isConnected,visible,editable:editable&&!el.disabled&&!inert,focused:contains(el,active),shadowDepth:depth,active:describe(active),point,blocker,tooltip,length:(el.isContentEditable?el.innerText:el.value||el.textContent||'').length};
 });
}
module.exports=function({until,norm,read,typingBudget}){
 async function editorState(box){
  const rows=await bounded(()=>box.evaluateAll(inspectEditors),3000,'Studio не ответил при проверке поля ввода.');
  if(rows.length>1)throw new Error('Studio: несколько полей вместо одного. Ввод остановлен.');return rows[0]||null;
 }
 async function activateEditor(box,{timeoutMs=12000,name='текста',onState=()=>{}}={}){
  const end=Date.now()+timeoutMs;let last=null,dismissed=false;
  const left=()=>Math.max(1,end-Date.now());
  try{
   await box.scrollIntoViewIfNeeded({timeout:Math.min(4000,left())});
   await until(async()=>{
    last=await editorState(box);
    if(!last?.connected||!last.visible||!last.editable)return null;
    if(last.point)return last;
    // Only an actual tooltip covering the editor can receive one Escape. A modal
    // backdrop elsewhere is normal; blindly pressing Escape can close the wizard.
    if(last.tooltip&&!dismissed){
     dismissed=true;await box.page().mouse.move(0,0);await new Promise(r=>setTimeout(r,100));last=await editorState(box);
     if(last&&!last.point&&last.tooltip){onState('tooltip','Закрываю подсказку, перекрывающую поле '+name+'.');await box.page().keyboard.press('Escape');}
    }
    return null;
   },left(),'Studio: поле '+name+' пока недоступно для ввода.');
   if(!last.focused){
    await box.click({position:last.point,timeout:Math.min(4000,left())});
    await box.focus({timeout:Math.min(3000,left())});
   }
   await until(async()=>{last=await editorState(box);return last?.connected&&last.editable&&last.focused&&last.point?last:null;},left(),'Studio не подтвердил фокус поля '+name+'.');
   onState('focus','Поле '+name+' готово; глубина Shadow DOM: '+last.shadowDepth+'.');return last;
  }catch(error){
   const blocker=last?.blocker?last.blocker.tag+(last.blocker.id?'#'+last.blocker.id:''):null;
   throw new Error('Studio: не удалось подготовить поле '+name+'. '+(blocker&&!last.point?'Его перекрывает '+blocker+'. ':last&&!last.editable?'Поле ещё не доступно для редактирования. ':last?.focused?'Фокус получен, но поле изменилось. ':'Фокус не подтверждён. ')+error.message);
  }
 }
 async function typeText(box,text,{delay=45,onProgress=()=>{},onState=()=>{},scope=null,name='текста',activationMs=12000}={}){
  text=norm(text);const chars=Array.from(text);
  // A local editor may be replaced during hydration. Retrying this exact field
  // is idempotent; file selection, Schedule and Publish are never retried here.
  for(let attempt=0;attempt<2;attempt++){
   try{
    await activateEditor(box,{timeoutMs:activationMs,name,onState});
    await box.press('Control+A',{timeout:3000});await box.press('Delete',{timeout:3000});
    await until(async()=>await read(box)==='',3000,'Studio не очистил прежний текст поля '+name+'.');
    let prefix='';
    for(let start=0;start<chars.length;start+=80){
     if(start){
      const state=await editorState(box);if(!state?.focused)await activateEditor(box,{timeoutMs:activationMs,name,onState});
      if(await read(box)!==norm(prefix))throw new Error('Studio заменил поле '+name+' во время ввода.');
     }
     await box.press('Control+End',{timeout:3000});
     const chunk=chars.slice(start,start+80).join('');
     await box.pressSequentially(chunk,{delay,timeout:typingBudget(chunk,delay)});prefix+=chunk;
     await until(async()=>await read(box)===norm(prefix),2500,'Studio не подтвердил введённую часть поля '+name+'.');
     onProgress(Math.min(start+80,chars.length),chars.length);
    }
    await box.press('Tab',{timeout:3000});
    await until(async()=>await read(box)===text,5000,'Studio не подтвердил введённый текст поля '+name+'.');return;
   }catch(error){
    if(attempt||box.page().isClosed())throw error;
    onState('retry','Повторяю ввод только поля '+name+' после изменения редактора (2/2).');
   }
  }
 }
 return {editorState,activateEditor,typeText,inspectEditors};
};
