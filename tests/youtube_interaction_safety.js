"use strict";
const assert=require('assert');
const {youtubeUrl,assertNoAds,installPlaybackBoundary,isAdVisible}=require('../tools/uploader/youtube-interaction-safety');
(async()=>{
  assert(youtubeUrl('https://www.youtube.com/shorts/abc'));
  assert(!youtubeUrl('https://youtube.com.attacker.test/'));
  assert(!youtubeUrl('http://www.youtube.com/'));
  let reads=0;
  await assert.rejects(assertNoAds({url:()=> 'https://ads.example/',evaluate:async()=>{reads++;}}),/вне YouTube/);
  assert.equal(reads,0);
  await assert.rejects(assertNoAds({url:()=> 'https://www.youtube.com/shorts/a',evaluate:async()=>true}),/реклама/);
  const withDom = async (document, run) => {
    global.document = document;
    global.getComputedStyle = () => ({ visibility: 'visible' });
    try { return await run(); }
    finally { delete global.document; delete global.getComputedStyle; }
  };
  const playerAd = {
    classList: { contains: n => n === 'ad-showing' },
    querySelector: () => null,
    querySelectorAll: () => []
  };
  const playerOk = {
    classList: { contains: () => false },
    querySelector: () => null,
    querySelectorAll: () => []
  };
  // In-player ad only: class ad-showing on #movie_player.
  assert.equal(await withDom({
    querySelector: () => null,
    querySelectorAll: sel => String(sel).includes('movie_player') ? [playerAd] : []
  }, () => isAdVisible({ url: () => 'https://www.youtube.com/watch?v=abcdefghijk', evaluate: async fn => fn() })), true);
  // Sidebar slot alone is not an in-player ad.
  assert.equal(await withDom({
    querySelector: () => null,
    querySelectorAll: sel => String(sel).includes('movie_player') ? [playerOk] : []
  }, () => isAdVisible({ url: () => 'https://www.youtube.com/watch?v=abcdefghijk', evaluate: async fn => fn() })), false);
  const events={};let handler,closed=0,removed=false;
  const frame={};
  const page={url:()=> 'https://www.youtube.com/shorts/a',evaluate:async()=>false,mainFrame:()=>frame,on:(k,f)=>events[k]=f,off:(k,f)=>{assert.strictEqual(events[k],f);delete events[k];},route:async(p,f)=>handler=f,unroute:async(p,f)=>{assert.strictEqual(f,handler);removed=true;}};
  const boundary=await installPlaybackBoundary(page);
  await boundary.check();
  let aborted=false,continued=false;
  await handler({request:()=>({isNavigationRequest:()=>true,frame:()=>frame,url:()=> 'https://ad.example/'}),abort:async()=>aborted=true,continue:async()=>continued=true});
  assert(aborted&&!continued);
  await assert.rejects(boundary.check(),/постороннюю/);
  await events.popup({close:async()=>closed++});assert.equal(closed,1);
  await boundary.dispose();assert(removed&&!events.popup);
  console.log('YouTube interaction safety: OK');
})().catch(e=>{console.error(e);process.exitCode=1;});
