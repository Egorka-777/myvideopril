'use strict';
const {bounded}=require('./studio-browser');
const {createHash}=require('crypto');
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
  // A tall editor can extend above/below the scrollport. Its top and centre
  // may both be covered by sticky DIVs while its lower visible area is usable.
  if(clip.right>clip.left&&clip.bottom>clip.top){
   const xs=[.5,.25,.75],ys=[.5,.8,.2];
   for(const fy of ys)for(const fx of xs)points.unshift([clip.left+(clip.right-clip.left)*fx-rect.left,clip.top+(clip.bottom-clip.top)*fy-rect.top]);
   points.unshift([(clip.left+clip.right)/2-rect.left,Math.min(clip.bottom-1,clip.top+16)-rect.top]);
  }
  let point=null,blocker=null,tooltip=false;
  for(const [x,y] of points){
   if(rect.left+x<0||rect.top+y<0||rect.left+x>=doc.documentElement.clientWidth||rect.top+y>=doc.documentElement.clientHeight)continue;
   const hit=deepHit(rect.left+x,rect.top+y);
   if(contains(el,hit)){point={x,y};break;}
   if(!blocker&&hit){blocker=describe(hit);for(let p=hit;p;p=parent(p))if(p.matches('[role=tooltip],#tooltip,tp-yt-iron-tooltip,ytcp-paper-tooltip-placeholder')){tooltip=true;break;}}
  }
  return {tag:el.tagName,id:el.id||'',role:el.getAttribute('role')||'',connected:el.isConnected,visible,editable:editable&&!el.disabled&&!inert,focused:contains(el,active),shadowDepth:depth,active:describe(active),point,blocker,tooltip,rect:{x:rect.x,y:rect.y,width:rect.width,height:rect.height},clip,length:(el.isContentEditable?el.innerText:el.value||el.textContent||'').length};
 });
}
function typingPolicy(text,{name='текста',delay=45,activationMs=12000,verifyMs=5000}={}){
 const description=name==='описания',chars=Array.from(String(text||'')).length;
 const chunkSize=description?40:80,typeDelay=description?Math.max(delay,20):delay;
 const chunkVerify=description?Math.max(verifyMs,35000):verifyMs,finalVerify=description?Math.max(verifyMs,45000):verifyMs;
 const maxAttempts=description?3:2;
 // One total deadline per attempt bounds repeated 35-second polls. This also
 // gives the outer metadata deadline the same retry policy as the editor.
 const attemptMs=activationMs+5000+10000+chars*(Math.max(0,typeDelay)+30)+Math.ceil(chars/chunkSize)*300+chunkVerify+finalVerify;
 return {chunkSize,typeDelay,chunkVerify,finalVerify,maxAttempts,activationMs,attemptMs,totalMs:maxAttempts*attemptMs+(maxAttempts-1)*800+5000};
}
function mismatch(expected,actual){
 const a=Array.from(expected),b=Array.from(actual);let at=0;while(at<Math.min(a.length,b.length)&&a[at]===b[at])at++;
 const kind=c=>c===undefined?'end':c==='\n'?'newline':/\s/.test(c)?'whitespace':'non-whitespace';
 const sha=s=>createHash('sha256').update(s).digest('hex');
 return {expectedLength:expected.length,actualLength:actual.length,expectedCodePoints:a.length,actualCodePoints:b.length,firstDifference:at,expectedKind:kind(a[at]),actualKind:kind(b[at]),relation:expected===actual?'exact':expected.startsWith(actual)?'missing-suffix':actual.startsWith(expected)?'extra-suffix':'different',expectedSha256:sha(expected),actualSha256:sha(actual),expectedNewlines:(expected.match(/\n/g)||[]).length,actualNewlines:(actual.match(/\n/g)||[]).length};
}
module.exports=function({until,norm,read,typingBudget}){
 function check(signal,deadline){
  if(signal?.aborted){const e=new Error('Studio: ввод остановлен вместе с этапом метаданных.');e.code='STUDIO_EDITOR_CANCELLED';throw e;}
  if(deadline&&Date.now()>=deadline){const e=new Error('Studio: исчерпан общий срок попытки ввода.');e.code='STUDIO_EDITOR_ATTEMPT_TIMEOUT';throw e;}
 }
 async function waitTyped(box,want,{timeoutMs=5000,name='текста',signal,deadline}={}){
  const expected=norm(want);let actual=null;
  try{return await until(async()=>{check(signal,deadline);actual=await read(box);return actual===expected;},timeoutMs,'Studio не подтвердил точный текст поля '+name+'.');}
  catch(error){if(actual!==null)error.editorMismatch=mismatch(expected,actual);throw error;}
 }
 async function editorState(box){
  const rows=await bounded(()=>box.evaluateAll(inspectEditors),3000,'Studio не ответил при проверке поля ввода.');
  if(rows.length>1)throw new Error('Studio: несколько полей вместо одного. Ввод остановлен.');return rows[0]||null;
 }
 async function activateEditor(box,{timeoutMs=12000,name='текста',onState=()=>{},signal}={}){
  const end=Date.now()+timeoutMs;let last=null,dismissed=false;
  const left=()=>{check(signal,end);return Math.max(1,end-Date.now());};
  try{
   await box.scrollIntoViewIfNeeded({timeout:Math.min(4000,left())});
   await until(async()=>{
    check(signal,end);last=await editorState(box);
    if(!last?.connected||!last.visible||!last.editable)return null;
    if(last.point)return last;
    // Only an actual tooltip can receive Escape; never a modal DIV/backdrop.
    if(last.tooltip&&!dismissed){
     dismissed=true;await box.page().mouse.move(0,0);await new Promise(r=>setTimeout(r,100));check(signal,end);last=await editorState(box);
     if(last&&!last.point&&last.tooltip){onState('tooltip','Закрываю подсказку, перекрывающую поле '+name+'.');await box.page().keyboard.press('Escape');}
    }
    return null;
   },left(),'Studio: поле '+name+' пока недоступно для ввода.');
   if(!last.focused){
    await box.click({position:last.point,timeout:Math.min(4000,left())});
    await box.focus({timeout:Math.min(3000,left())});
   }
   await until(async()=>{check(signal,end);last=await editorState(box);return last?.connected&&last.editable&&last.focused&&last.point?last:null;},left(),'Studio не подтвердил фокус поля '+name+'.');
   onState('focus','Поле '+name+' готово; глубина Shadow DOM: '+last.shadowDepth+'.');return last;
  }catch(error){
   const blocker=last?.blocker?last.blocker.tag+(last.blocker.id?'#'+last.blocker.id:''):null;
   const result=new Error('Studio: не удалось подготовить поле '+name+'. '+(blocker&&!last.point?'Его перекрывает '+blocker+'. ':last&&!last.editable?'Поле ещё не доступно для редактирования. ':last?.focused?'Фокус получен, но поле изменилось. ':'Фокус не подтверждён. ')+error.message);
   result.code=error.code;result.editorState=last;throw result;
  }
 }
 async function typeText(box,text,{delay=45,onProgress=()=>{},onState=()=>{},scope=null,name='текста',activationMs=12000,verifyMs=5000,signal}={}){
  text=norm(text);const chars=Array.from(text),policy=typingPolicy(text,{name,delay,activationMs,verifyMs});
  for(let attempt=0;attempt<policy.maxAttempts;attempt++){
   const deadline=Date.now()+policy.attemptMs;
   const remaining=max=>{check(signal,deadline);return Math.max(1,Math.min(max,deadline-Date.now()));};
   const press=async key=>{await box.press(key,{timeout:remaining(3000)});check(signal,deadline);};
   const verify=want=>waitTyped(box,want,{timeoutMs:remaining(policy.chunkVerify),name,signal,deadline});
   try{
    if(attempt)await new Promise(r=>setTimeout(r,800));check(signal,deadline);
    await activateEditor(box,{timeoutMs:remaining(activationMs),name,onState,signal});
    await press('Control+A');await press('Delete');
    await until(async()=>{check(signal,deadline);return !norm(await read(box));},remaining(5000),'Studio не очистил прежний текст поля '+name+'.');
    let prefix='';
    for(let start=0;start<chars.length;start+=policy.chunkSize){
     check(signal,deadline);
     if(start){
      const state=await editorState(box);if(!state?.focused)await activateEditor(box,{timeoutMs:remaining(activationMs),name,onState,signal});
      await verify(prefix);
     }
     await press('Control+End');
     const chunk=chars.slice(start,start+policy.chunkSize).join(''),lines=chunk.split('\n');
     for(let line=0;line<lines.length;line++){
      if(line)await press('Shift+Enter');
      if(lines[line]){await box.pressSequentially(lines[line],{delay:policy.typeDelay,timeout:remaining(typingBudget(lines[line],policy.typeDelay))});check(signal,deadline);}
     }
     prefix+=chunk;await verify(prefix);
     onProgress(Math.min(start+policy.chunkSize,chars.length),chars.length);
    }
    await press('Tab');
    await waitTyped(box,text,{timeoutMs:remaining(policy.finalVerify),name,signal,deadline});return;
   }catch(error){
    const snapshot=error.editorState||await editorState(box).catch(()=>null);
    const diagnostic={field:name,attempt:attempt+1,maxAttempts:policy.maxAttempts,code:error.code||'STUDIO_EDITOR_MISMATCH',comparison:error.editorMismatch||null,state:snapshot,policy};
    error.editorDiagnostic=diagnostic;
    onState('attempt_failed','Поле '+name+': попытка '+(attempt+1)+'/'+policy.maxAttempts+' не подтверждена'+(diagnostic.comparison?' (ожидалось '+diagnostic.comparison.expectedLength+', прочитано '+diagnostic.comparison.actualLength+' символов)':'')+'.',diagnostic);
    if(signal?.aborted||attempt+1>=policy.maxAttempts||box.page().isClosed())throw error;
    onState('retry','Повторяю ввод только поля '+name+' ('+(attempt+2)+'/'+policy.maxAttempts+').',diagnostic);
   }
  }
 }
 return {editorState,activateEditor,typeText,inspectEditors};
};
module.exports.typingPolicy=typingPolicy;
module.exports.typingTotalBudget=(text,options)=>typingPolicy(text,options).totalMs;
