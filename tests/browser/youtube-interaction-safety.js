'use strict';
const assert=require('assert');
const {chromium}=require('../../tools/uploader/node_modules/playwright-core');
const {assertNoAds,installPlaybackBoundary}=require('../../tools/uploader/youtube-interaction-safety');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try {
  const context=await browser.newContext();
  const page=await context.newPage();
  // Fixtures are fulfilled locally: no requests reach YouTube or advertising hosts.
  await context.route('**/*',r=>r.fulfill({contentType:'text/html',body:'<div id="movie_player"><video></video></div>'}));
  await page.goto('https://www.youtube.com/shorts/fixture');
  await assertNoAds(page);
  await page.evaluate(()=>document.querySelector('#movie_player').classList.add('ad-showing'));
  await assert.rejects(assertNoAds(page),/реклама/);
  await page.evaluate(()=>document.body.innerHTML='<ytd-reel-video-renderer is-active><ad-button-view-model>Visit advertiser</ad-button-view-model></ytd-reel-video-renderer>');
  await assert.rejects(assertNoAds(page),/реклама/);
  await page.evaluate(()=>document.body.innerHTML='<div style="display:none" class="ytp-ad-player-overlay">Hidden ad</div><video></video>');
  await assertNoAds(page);
  const boundary=await installPlaybackBoundary(page);
  const popup=page.waitForEvent('popup');
  await page.evaluate(()=>window.open('https://advertiser.invalid/'));
  const child=await popup;
  if(!child.isClosed())await child.waitForEvent('close',{timeout:10000});
  assert(child.isClosed());
  await assert.rejects(boundary.check(),/постороннюю/);
  await boundary.dispose();
  assert(!page.isClosed());
  console.log('Native browser advertising safety: OK');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
