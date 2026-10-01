"use strict";
const assert=require('assert');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const {openToday}=require('../../tools/uploader/youtube-open-today');
const ids=['YESTERDAY01','TODAYVIDEO1'];
function player(id,date) {
  return {videoDetails:{videoId:id,channelId:'UC-owned'},playabilityStatus:{status:'OK'},microformat:{playerMicroformatRenderer:{publishDate:date}}};
}
(async()=>{
  const browser=await chromium.launch({headless:true});
  try {
    const context=await browser.newContext();
    let hasToday=true, mediaReached=false;
    await context.route('**/*',async route=>{
      const u=new URL(route.request().url());
      if(/googlevideo/.test(u.hostname)){mediaReached=true;return route.abort();}
      if(u.pathname==='/@owned/videos') {
        return route.fulfill({contentType:'text/html',body:`<script>window.ytInitialData={metadata:{channelMetadataRenderer:{externalId:'UC-owned'}}};</script><ytd-browse><ytd-rich-grid-renderer>${ids.map(id=>`<ytd-rich-grid-media><a href="/watch?v=${id}">${id}</a></ytd-rich-grid-media>`).join('')}</ytd-rich-grid-renderer></ytd-browse>`});
      }
      if(u.pathname==='/watch') {
        const id=u.searchParams.get('v');
        return route.fulfill({contentType:'text/html',body:`<script>var ytInitialPlayerResponse=${JSON.stringify(player(id,id===ids[1]&&hasToday?'2026-10-01':'2026-09-30'))};</script><video autoplay preload="auto" src="https://rr1.googlevideo.com/videoplayback"></video><script>document.querySelector('video').play();</script>`});
      }
      return route.fulfill({status:404,body:''});
    });
    const page=await context.newPage();
    assert.equal(await openToday(page,{channelUrl:'https://www.youtube.com/@owned/videos',todayDate:'2026-10-01',pauseAfterOpen:false}),'https://www.youtube.com/watch?v='+ids[1]);
    const state=await page.locator('video').evaluate(v=>({paused:v.paused,autoplay:v.autoplay}));
    assert.deepEqual(state,{paused:true,autoplay:false});
    assert.equal(mediaReached,false,'media must not reach the transport');
    hasToday=false;
    const second=await context.newPage();
    await assert.rejects(openToday(second,{channelUrl:'https://www.youtube.com/@owned/videos',todayDate:'2026-10-01'}),/Нет подтверждённого видео/);
    assert.equal(new URL(second.url()).pathname,'/@owned/videos');
    console.log('OK: YouTube browser navigation (today selection, paused media, no old-video fallback)');
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
