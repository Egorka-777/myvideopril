"use strict";
const assert=require('assert');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const {openChannelsPaused}=require('../../tools/uploader/youtube-open-today');
const channels={en:{id:'ENTODAY0001',owner:'UC-en',date:'2026-10-01',label:'Videos'},ru:{id:'RUTODAY0001',owner:'UC-ru',date:'2026-10-01',label:'Видео'},old:{id:'OLDVIDEO001',owner:'UC-old',date:'2026-09-30',label:'Videos'}};
(async()=>{
  const browser=await chromium.launch({headless:true});
  try {
    const context=await browser.newContext();
    let mediaReached=0,likeClicks=0;
    await context.exposeBinding('recordLike',()=>{likeClicks++;});
    await context.route('**/*',async route=>{
      const u=new URL(route.request().url());
      if(/googlevideo/.test(u.hostname)){mediaReached++;return route.abort();}
      const name=/^\/@(en|ru|old)\/videos$/.exec(u.pathname)?.[1];
      if(name) {
        const ch=channels[name];
        const initialData={metadata:{channelMetadataRenderer:{externalId:ch.owner}},contents:{twoColumnBrowseResultsRenderer:{tabs:[{tabRenderer:{selected:true,content:{richGridRenderer:{contents:[{richShelfRenderer:{contents:[{videoRenderer:{videoId:'SHORTS00001'}}]}},{richItemRenderer:{content:{lockupViewModel:{contentType:'LOCKUP_CONTENT_TYPE_VIDEO',contentId:ch.id}}}}]}}}}]}}};
        return route.fulfill({contentType:'text/html',body:`<script>var ytInitialData=${JSON.stringify(name==='ru'?initialData:{metadata:initialData.metadata})};</script><ytd-browse>${ch.label}<ytd-rich-grid-renderer><ytd-rich-shelf-renderer><a href="/shorts/SHORTS00001">Shorts</a><a href="/watch?v=SHORTS00001">Shorts watch</a></ytd-rich-shelf-renderer>${name==='ru'?'<yt-lockup-view-model>':'<ytd-rich-grid-media>'}<a href="/watch?v=${ch.id}">Today</a>${name==='ru'?'</yt-lockup-view-model>':'</ytd-rich-grid-media>'}</ytd-rich-grid-renderer></ytd-browse>`});
      }
      if(u.pathname==='/watch') {
        const id=u.searchParams.get('v'),ch=Object.values(channels).find(c=>c.id===id);
        if(!ch)return route.fulfill({status:404,body:'Unexpected Shorts or catalog ID'});
        const player={videoDetails:{videoId:id,channelId:ch.owner},playabilityStatus:{status:'OK'},microformat:{playerMicroformatRenderer:{publishDate:ch.date}}};
        return route.fulfill({contentType:'text/html',body:`<script>var ytInitialPlayerResponse=${JSON.stringify(player)};</script><video autoplay src="https://rr.googlevideo.com/videoplayback"></video><button aria-label="Like this video" onclick="recordLike()">Like</button><script>document.querySelector('video').play();</script>`});
      }
      return route.fulfill({status:404,body:''});
    });
    const target=name=>({channelUrl:'https://www.youtube.com/@'+name+'/videos',ownerProfileId:name,ownerName:name,catalogVideos:[{videoId:'STALE000001'}]});
    for(const viewer of ['en','ru']) {
      const page=await context.newPage(),opened=[];
      const order=viewer==='en'?['en','ru']:['ru','en'];
      const result=await openChannelsPaused(page,order.map(target),{profileId:viewer,todayDate:'2026-10-01'},(stage,text,extra)=>{if(stage==='mesh')opened.push(extra);});
      assert.equal(result.opened,2,'Both own and other channel must open');
      assert.deepEqual(opened.map(x=>x.meshOwner),order);
      assert.deepEqual(await page.locator('video').evaluate(v=>({paused:v.paused,autoplay:v.autoplay})),{paused:true,autoplay:false});
      assert(opened.every(x=>!x.url.includes('STALE000001')));
    }
    const page=await context.newPage(),opened=[],errors=[];
    await assert.rejects(openChannelsPaused(page,['old','ru'].map(target),{todayDate:'2026-10-01'},(stage,text,extra)=>{
      if(stage==='mesh')opened.push(extra.meshOwner);if(stage==='channel_error')errors.push(text);
    }),/Открыто 1\/2/);
    assert.deepEqual(opened,['ru'],'Failure must not skip remaining channels');
    assert.equal(errors.length,1);
    assert.equal(mediaReached,0);assert.equal(likeClicks,0);
    console.log('OK: paused grid browser (all links including own, RU/EN labels, Shorts exclusion, stale history ignored, partial failure)');
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
