'use strict';
const assert=require('assert'),fs=require('fs'),cp=require('child_process'),os=require('os'),path=require('path'),crypto=require('crypto');
const C=require('../tools/uploader/studio-editor-controls'),S=require('../tools/uploader/youtube-studio-upload'),B=require('../tools/uploader/studio-browser');
const base='2207c946c7f85b7b9363f5641be84a223a6aba47';
const before=cp.execFileSync('git',['show',base+':tools/uploader/youtube-studio-upload.js'],{encoding:'utf8'}).replace(/\r\n/g,'\n'),after=fs.readFileSync('tools/uploader/youtube-studio-upload.js','utf8').replace(/\r\n/g,'\n');
const body=(s,a,b)=>s.slice(s.indexOf(a),s.indexOf(b,s.indexOf(a)));
assert.equal(body(after,'async function publishPublic','function videoId'),body(before,'async function publishPublic','function videoId'),'Public selection must retain the known working behavior');
const text='X'.repeat(1650),p=C.typingPolicy(text,{name:'описания',delay:18,activationMs:25000,verifyMs:35000});
assert.equal(p.chunkSize,1);assert.equal(p.typeDelay,18);assert.equal(p.chunkVerify,0);assert.equal(p.finalVerify,35000);assert.equal(p.maxAttempts,1);assert.equal(p.totalMs,p.attemptMs);
assert(S.metadataBudget({title:'Title',description:text,tags:[]},{})>p.totalMs);
assert.equal(S.BUILD,'2026-10-23-studio-two-profiles-v21');
const temp=fs.mkdtempSync(path.join(os.tmpdir(),'vb-studio-manifest-'));
try{
 const root=path.join(temp,'tools/uploader');fs.mkdirSync(root,{recursive:true});
 const files=['VideoBatch.exe','tools/uploader/worker.js','tools/uploader/youtube-studio-upload.js','tools/uploader/studio-editor-controls.js','tools/uploader/studio-browser.js'].map(file=>{const bytes=Buffer.from(file);fs.writeFileSync(path.join(temp,file),bytes);return {file,sha256:crypto.createHash('sha256').update(bytes).digest('hex')};});
 const manifest={build:S.BUILD,protocolVersion:2,files},write=()=>fs.writeFileSync(path.join(root,'STUDIO_UPLOADER_VERSION.json'),'\uFEFF'+JSON.stringify(manifest));
 write();B.assertRuntimePackage(S.BUILD,root);
 manifest.protocolVersion=1;write();assert.throws(()=>B.assertRuntimePackage(S.BUILD,root),/разными комплектами/);
 manifest.protocolVersion=2;manifest.files=[];write();assert.throws(()=>B.assertRuntimePackage(S.BUILD,root),/неполный/);
 manifest.files=files;write();fs.appendFileSync(path.join(root,'worker.js'),'changed');assert.throws(()=>B.assertRuntimePackage(S.BUILD,root),/отличается/);
}finally{fs.rmSync(temp,{recursive:true,force:true});}
async function uploadRegressions(){
 // Simulate Studio's selection corruption when a variation selector arrives
 // separately. Grapheme input must never produce that event boundary.
 let value='',inserts=[];
 const page={keyboard:{insertText:async text=>{inserts.push(text);value+=text;}}};
 const box={page:()=>page,scrollIntoViewIfNeeded:async()=>{},evaluateAll:async()=>[{connected:true,visible:true,editable:true,focused:true,point:{x:1,y:1}}],press:async key=>{if(key==='Delete')value='';if(key==='Shift+Enter')value+='\n';}};
 const until=async fn=>{if(!await fn())throw Error('Studio не подтвердил точный текст');};
 const editor=C({until,norm:S.norm,read:async()=>S.norm(value),typingBudget:S.typingBudget});
 const text='Notice ⚠️ e\u0301 👩‍💻\nEnd';
 await editor.typeText(box,text,{name:'описания',delay:0});
 assert.equal(value,text);assert(inserts.includes('⚠️'));assert(inserts.includes('e\u0301'));assert(inserts.includes('👩‍💻'));assert(!inserts.includes('\ufe0f'));
 page.keyboard.insertText=async char=>{value+=char==='Z'?'':char;};
 await assert.rejects(editor.typeText(box,'XYZ',{name:'описания',delay:0}),/точный текст/);
 // An actual saved-text mismatch is a metadata failure, NOT proof that a
 // publicly saved video failed to publish. Never normalize away lost text.
 const node=text=>({isVisible:async()=>true,isEditable:async()=>true,evaluate:async()=>text});
 const title=node('Title'),description=node('Saved description with lost suffix'),control={isVisible:async()=>true,innerText:async()=> 'Public',getAttribute:async()=>null};
 const scope={locator:selector=>({all:async()=>selector.includes('title')?[title]:[description]}),getByRole:()=>({all:async()=>[]})};
 const savedPage={url:()=> 'https://studio.youtube.com/video/safevideo01/edit',locator:selector=>selector==='#visibility-button'?{all:async()=>[control]}:{first:()=>scope}};
 let mismatch;
 try{await S.readback(savedPage,{title:'Title',description:'Saved description with lost suffix AND expected end',contentKind:'shorts'},'safevideo01',null,{reuseReadback:true},null);}catch(error){mismatch=error;}
 assert(mismatch);assert.equal(mismatch.publicationConfirmed.visibility,'public');assert.equal(mismatch.editorDiagnostic.comparison.relation,'missing-suffix');assert(!JSON.stringify(mismatch.editorDiagnostic).includes('Saved description'));
 const pending={video:__filename,title:'Title',contentKind:'shorts',studioPending:true,studioVideoId:'safevideo01'};let transferred=false;
 const taskPage={on:()=>{},off:()=>{}};
 await assert.rejects(S.runSequential(taskPage,[pending],{},{progress:()=>{},openFile:async()=>{transferred=true;}}),/Повторная загрузка заблокирована/);
 assert(!transferred);assert.equal(pending.studioVideoId,'safevideo01');assert(pending.studioPending);
 await assert.rejects(S.runSequential(taskPage,[{...pending,studioVideoId:''}],{},{progress:()=>{},openFile:async()=>{transferred=true;}}),/Повторная загрузка заблокирована/);assert(!transferred);
 await focusedControls();
 await confirmationFailures();
 console.log('PASS upload regressions: intact grapheme input, exact text required, saved-description mismatch carries publication proof and redacted diagnostics, uncertain ID blocks retransfer.');
}
async function focusedControls(){
 // Reproduce a decorative/disabled icon preceding an enabled named button.
 const node=enabled=>({isVisible:async()=>true,evaluateAll:async fn=>fn([{isConnected:true,getClientRects:()=>[{}],hasAttribute:()=>!enabled,getAttribute:()=>null,parentElement:null,getRootNode:()=>({})}])});
 const icon=node(false),button=node(true),list=nodes=>({all:async()=>nodes});
 const previousStyle=global.getComputedStyle;global.getComputedStyle=()=>({visibility:'visible'});
 try{assert.strictEqual(await B.createButton({getByRole:()=>list([button]),locator:()=>list([icon])}),button);
  assert.strictEqual(await B.createButton({getByRole:()=>list([icon,button]),locator:()=>list([icon])}),button);
  assert.strictEqual(await B.createButton({getByRole:()=>list([icon]),locator:()=>list([])}),null);
 }finally{global.getComputedStyle=previousStyle;}
 // Exercise the actual thumbnail verifier without launching a browser. A
 // generated image is visible while the matching saved custom image is below
 // the viewport. Only persisted readback is allowed to scroll candidates.
 const T=require('../tools/uploader/youtube-thumbnail'),dir=fs.mkdtempSync(path.join(os.tmpdir(),'vb-thumbnail-'));
 try{
  const file=path.join(dir,'reference.png');fs.writeFileSync(file,'fixture');let scrolled=0,screenshots=0;
  const img=(src,correct)=>({isVisible:async()=>true,scrollIntoViewIfNeeded:async()=>{if(correct)scrolled++;},evaluate:async fn=>{
   const code=String(fn);if(code.includes('key:'))return {src,ready:true,key:src};if(code.includes('e.complete'))return true;
   if(code.includes('r.left'))return !correct||scrolled?{x:0,y:0,width:100,height:60}:null;
   return {fit:'contain',position:'50% 50%',bg:'white'};
  }});
  const imgs=[img('https://img/generated',false),img('https://img/custom',true)],images={count:async()=>imgs.length,nth:i=>imgs[i],all:async()=>imgs};
  const root={isVisible:async()=>true,evaluate:async()=>false,locator:()=>images};
  const page={locator:()=>({count:async()=>1,nth:()=>root}),waitForTimeout:async()=>{},screenshot:async()=>{screenshots++;return Buffer.from('image');},evaluate:async()=>screenshots===2};
  assert.equal(await T.verifyThumbnail(page,file,{timeout:50}),'https://img/custom');assert(scrolled>0);
  scrolled=0;screenshots=0;await assert.rejects(T.verifyThumbnail({...page,evaluate:async()=>false},file,{timeout:1}),/не подтвердил/);
  scrolled=0;screenshots=0;await T.matches(page,imgs[1],{data:'fixture'});assert.equal(scrolled,0,'Live wizard matcher must never scroll');assert.equal(screenshots,0);
 }finally{fs.rmSync(dir,{recursive:true,force:true});}
 console.log('PASS enabled named Create wins over stale icon; saved-thumbnail candidate revealed; wrong image rejected; upload matcher never scrolls.');
}
async function confirmationFailures(){
 // Drive real runItems/finish with only UI boundary functions substituted.
 const vm=require('vm'),filename=path.resolve(__dirname,'../tools/uploader/youtube-studio-upload.js');
 const localRequire=require('module').createRequire(filename),mod={exports:{}};
 const source=fs.readFileSync(filename,'utf8')+'\nmodule.exports.testBoundaries=f=>{uploadScope=f.scope;field=f.field;readVideoId=f.id;details=f.details;visibility=f.visibility;publishPublic=f.publish;waitSaveConfirmed=f.saved;readback=f.readback;};';
 vm.runInNewContext(source,{require:localRequire,module:mod,exports:mod.exports,console,process,URL,AbortController,setTimeout,clearTimeout,Buffer},{filename});
 const U=mod.exports;
 for(const proof of [true,false]){
  let confirmations=0,transfers=0,reads=0;const error=Error(proof?'description mismatch':'visibility not confirmed');if(proof)error.publicationConfirmed={visibility:'public'};
  U.testBoundaries({scope:async()=>({}),field:async()=>({}),id:async()=> 'safevideo01',details:async()=>{},visibility:async()=>{},publish:async()=>({click:async()=>{}}),saved:async()=> 'closed',readback:async()=>{reads++;throw error;}});
  const verify={on:()=>{},off:()=>{},close:async()=>{},isClosed:()=>!proof},page={on:()=>{},off:()=>{},context:()=>({newPage:async()=>verify})},item={video:__filename,title:'Title',contentKind:'shorts'};
  let caught;try{await U.runSequential(page,[item],{},{progress:()=>{},checkpoint:async()=>{},openFile:async()=>{transfers++;},waitUpload:async()=>{},accepted:async()=>{},confirmed:async()=>{confirmations++;}});}catch(e){caught=e;}
  assert(caught);assert.equal(transfers,1,caught.stack);assert.equal(confirmations,proof?1:0,caught.stack);assert.equal(reads,1,caught.stack);
  if(proof){assert.equal(caught.code,'STUDIO_METADATA_VERIFICATION_FAILED');assert.equal(item.studioPhase,'confirmed');assert.equal(item.studioPending,false);}
  else{assert.equal(item.studioPending,true);assert.notEqual(item.studioPhase,'confirmed');}
 }
 console.log('PASS publication proof durably confirmed before distinct metadata error; unproven publication stays pending.');
}
uploadRegressions().catch(error=>{console.error(error);process.exitCode=1;});
console.log('Studio v20 contract PASS: single-pass description with final exact check, unchanged Public choice, complete EXE/JS manifest and tamper detection.');
