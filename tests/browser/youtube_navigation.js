"use strict";
const assert=require('assert');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const {navigateTodayVideo}=require('../../tools/uploader/youtube-open-today');
const ids=['abcdefghijk','lmnopqrstuv'];

function channelPage(id, date) {
  return `<script>var ytInitialData=${JSON.stringify({metadata:{channelMetadataRenderer:{externalId:'UCowned'}},contents:{twoColumnBrowseResultsRenderer:{tabs:[{tabRenderer:{selected:true,content:{richGridRenderer:{contents:[{richItemRenderer:{content:{lockupViewModel:{contentType:'LOCKUP_CONTENT_TYPE_VIDEO',contentId:id}}}}]}}}}]}}})};</script>` +
    `<ytd-browse><ytd-rich-grid-renderer><a href="/watch?v=${id}">Video</a></ytd-rich-grid-renderer></ytd-browse>`;
}

function watchPage(id, date) {
  return `<script>var ytInitialPlayerResponse=${JSON.stringify({videoDetails:{videoId:id,channelId:'UCowned'},playabilityStatus:{status:'OK'},microformat:{playerMicroformatRenderer:{publishDate:date}}})};</script><video></video>`;
}

async function withRoutes(handler) {
  const browser=await chromium.launch({headless:true});
  try {
    const context=await browser.newContext();
    await context.route('**/*', handler);
    const page=await context.newPage();
    return {page, context, browser};
  } catch (e) {
    await browser.close();
    throw e;
  }
}

(async()=>{
  let session=await withRoutes(async route=>{
    const u=new URL(route.request().url());
    if(u.pathname==='/@owned/videos') return route.fulfill({contentType:'text/html',body:channelPage(ids[1],'2026-10-01')});
    if(u.pathname==='/watch') return route.fulfill({contentType:'text/html',body:watchPage(u.searchParams.get('v'),'2026-10-01')});
    return route.fulfill({status:404,body:''});
  });
  assert.equal(await navigateTodayVideo(session.page,{channelUrl:'https://www.youtube.com/@owned/videos',todayDate:'2026-10-01'}),'https://www.youtube.com/watch?v='+ids[1]);
  await session.browser.close();

  session=await withRoutes(async route=>{
    const u=new URL(route.request().url());
    if(u.pathname==='/@owned/videos') return route.fulfill({contentType:'text/html',body:channelPage(ids[0],'2026-09-30')});
    if(u.pathname==='/watch') return route.fulfill({contentType:'text/html',body:watchPage(u.searchParams.get('v'),'2026-09-30')});
    return route.fulfill({status:404,body:''});
  });
  await assert.rejects(navigateTodayVideo(session.page,{channelUrl:'https://www.youtube.com/@owned/videos',todayDate:'2026-10-01'}),/Нет подтверждённого видео/);
  await session.browser.close();
  console.log('OK: YouTube browser navigation (today selection, no old-video fallback)');
})().catch(e=>{console.error(e);process.exitCode=1;});
