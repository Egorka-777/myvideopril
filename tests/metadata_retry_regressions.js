"use strict";
const assert = require("assert");
const {readVideoMetadataHtml} = require("../tools/uploader/youtube-open-today");
(async()=>{
  let requests=0, tabs=0, closed=0;
  const page={
    evaluate:async()=>{ if(++requests<3)throw new Error("page.evaluate: TypeError: Failed to fetch");return "confirmed html"; },
    isClosed:()=>false, waitForTimeout:async()=>{},
    context:()=>({newPage:async()=>{tabs++;return {goto:async()=>{},waitForFunction:async()=>{},content:async()=>"fallback html",close:async()=>{closed++;}};}})
  };
  const events=[];
  assert.equal(await readVideoMetadataHtml(page,"abcdefghijk",x=>x,(s,t)=>events.push(s)),"confirmed html");
  assert.equal(requests,3);assert.equal(tabs,0);assert.equal(events.length,2);
  page.evaluate=async()=>{throw new Error("Failed to fetch");};
  assert.equal(await readVideoMetadataHtml(page,"abcdefghijk",x=>x,()=>{}),"fallback html");
  assert.equal(tabs,1);assert.equal(closed,1);
  requests=0;
  page.evaluate=async()=>{requests++;throw new Error("HTTP 403");};
  await assert.rejects(readVideoMetadataHtml(page,"abcdefghijk",x=>x,()=>{}),/HTTP 403/);
  assert.equal(requests,1);assert.equal(tabs,1);
  console.log("OK: metadata recovery (transient fetch, same-context fallback, no retry for forbidden)");
})().catch(e=>{console.error(e);process.exitCode=1;});
