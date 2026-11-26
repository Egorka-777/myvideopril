'use strict';
const fs=require('fs');
const Module=require('module');
const path=require('path');
const filename=path.join(__dirname,'youtube-owner-publish.js');
const source=fs.readFileSync(filename,'utf8').split('(async()=>{')[0];
const test=new Module(filename,module);test.filename=filename;test.paths=module.paths;
test._compile(source+`
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  for(const lang of ['ru','en']){
   const f=await fixture(browser,lang);f.state.get(IDS[0]).visibility='public';const events=[];
   const item={publishedVideoId:IDS[0],title:'Existing long video',commentText:'Saved profile signature 2511',commentState:''};
   await O.runSequential(f.page,[item],{typingDelay:1,actionDelayMs:0,confirmDelayMs:0,progress:(stage,text)=>events.push({stage,text}),checkpoint:async()=>{}});
   assert.equal(f.counts().publishCalls,0);assert.equal(f.counts().commentCalls,1);assert.equal(item.commentState,'sent');
   assert.equal(f.ctx.pages().length,2);assert(f.ctx.pages()[1].url().includes('youtube.com/watch?v='+IDS[0]));
   assert(events.some(e=>e.stage==='owner_comment_sent'));
   console.log('PASS '+lang+': existing public video -> watch tab -> one comment -> profile tabs retained');await f.ctx.close();
  }
  const empty=await fixture(browser,'ru');empty.state.get(IDS[0]).visibility='public';const events=[];
  await O.runSequential(empty.page,[{publishedVideoId:IDS[0],commentText:''}],{progress:(stage,text)=>events.push({stage,text})});
  assert.equal(empty.counts().commentCalls,0);assert(events.some(e=>e.stage==='owner_item_confirmed'&&e.text.includes('подпись не задана')));await empty.ctx.close();
  console.log('PASS empty signature explicitly reported as not sent');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
`,filename);
