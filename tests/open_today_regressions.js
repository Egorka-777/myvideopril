"use strict";
const assert = require("assert");
const {channelVideosUrl, extractAssignedJson, classifyVideo, openToday} = require("../tools/uploader/youtube-open-today.js");
const id="abcdefghijk", other="lmnopqrstuv", today="2026-10-01";
function player(date, changes={}) {
  return {videoDetails:{videoId:id,channelId:"UCowner",...changes},playabilityStatus:{status:"OK"},
    microformat:{playerMicroformatRenderer:{publishDate:date}}};
}
assert.equal(channelVideosUrl("https://www.youtube.com/@traderprdp/videos"),"https://www.youtube.com/@traderprdp/videos");
assert.equal(channelVideosUrl("https://youtube.com/channel/UCowner"),"https://www.youtube.com/channel/UCowner/videos");
for(const url of ["https://www.youtube.com/watch?v="+id,"https://youtube.com.evil.test/@owner/videos","javascript:alert(1)"])
  assert.throws(()=>channelVideosUrl(url));
assert.equal(classifyVideo(player(today),id,"UCowner",today).eligible,true);
for(const date of ["2026-09-30","2026-10-02",""])
  assert.equal(classifyVideo(player(date),id,"UCowner",today).eligible,false);
assert.equal(classifyVideo(player(today,{channelId:"UCstranger"}),id,"UCowner",today).eligible,false);
assert.equal(classifyVideo(player(today,{isLiveContent:true}),id,"UCowner",today).eligible,false);
assert.equal(classifyVideo(player(today),other,"UCowner",today).eligible,false);
const obj={text:'braces } and escaped " quote',nested:{value:1}};
assert.deepEqual(extractAssignedJson("var ytInitialPlayerResponse = "+JSON.stringify(obj)+"; trailing", "ytInitialPlayerResponse"),obj);
assert.equal(extractAssignedJson("ytInitialPlayerResponse = {broken};","ytInitialPlayerResponse"),null);

// Exercise the real navigation flow, including dates from fetched watch HTML.
async function run(dates) {
  const visited=[], events=[]; let metadataIndex=0, evalIndex=0;
  const page={route:async()=>{},addInitScript:async()=>{},goto:async url=>visited.push(url),
    waitForFunction:async()=>{},locator:()=>({first:()=>({waitFor:async()=>{}})}),
    evaluate:async()=>{
      if(evalIndex++===0)return "UCowner";
      if(evalIndex===2)return [id,other];
      const p=player(dates[metadataIndex],{videoId:metadataIndex++===0?id:other});
      return "var ytInitialPlayerResponse = "+JSON.stringify(p)+";";
    }};
  const promise=openToday(page,{channelUrl:"https://www.youtube.com/@owner/videos",todayDate:today},(stage,text)=>events.push({stage,text}));
  return {promise,visited,events};
}
(async()=>{
  let test=await run(["2026-09-30",today]);
  assert.equal(await test.promise,"https://www.youtube.com/watch?v="+other);
  assert.deepEqual(test.visited,["https://www.youtube.com/@owner/videos","https://www.youtube.com/watch?v="+other]);
  test=await run(["2026-09-30","2026-09-29"]);
  await assert.rejects(test.promise,/Нет подтверждённого видео/);
  assert.deepEqual(test.visited,["https://www.youtube.com/@owner/videos"]);
  console.log("OK: open_today_regressions.js (navigation, ownership and no old-video fallback)");
})().catch(e=>{console.error(e);process.exitCode=1;});
