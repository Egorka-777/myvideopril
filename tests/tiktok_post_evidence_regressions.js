"use strict";
const assert = require("assert");
const fs = require("fs");
const worker = require("../tools/uploader/worker-tiktok-http.js");
function verdict(status, body) {
  return worker.evaluatePostAcceptance(worker.parsePostResponse({status, json:body}), {listed:true});
}
assert.equal(verdict(200,{status_code:0}).ok,true);
assert.equal(verdict(500,{status_code:0}).ok,false);
assert.equal(verdict(200,{status_code:8,status_msg:"private review pending"}).ok,false);
assert.equal(verdict(200,{}).ok,false);
assert.equal(verdict(200,{single_post_resp_list:[{video_id:"upload-only"}]}).ok,false);
assert.equal(verdict(200,{status_code:0,single_post_resp_list:[{status_code:8}]}).ok,false);
assert.equal(verdict(200,{single_post_resp_list:[{status_code:0,item_id:"post-1"}]}).ok,true);
assert.equal(verdict(200,{status_code:9,single_post_resp_list:[{item_id:"post-1"}]}).ok,false);
const cs=fs.readFileSync(require.resolve("../src/TikTok.cs"),"utf8");
assert.doesNotMatch(cs,/foreach\(var it in pack.items\)it.Published=true/);
assert.match(cs,/HttpNeedsReview=true/);
assert.match(cs,/m.localJobId!=item.LocalJobId/);
assert.match(cs,/accepted!=pack.items.Count/);
// Exercise production transport: a failed browser POST must not be retried.
(async()=>{
  let calls=0;
  await assert.rejects(worker.browserFetch({evaluate:async()=>{calls++;return {error:"connection lost"};}},
    "POST","https://www.tiktok.com/tiktok/web/project/post/v1/"),/connection lost/);
  assert.equal(calls,1);
  const context={request:{get:async()=>{throw new Error("must not substitute request IP");}}};
  const page={goto:async()=>{},evaluate:async()=>({error:"blocked"})};
  await assert.rejects(worker.readBrowserIp(context,page),/Browser IP/);
  console.log("OK: tiktok_post_evidence_regressions.js");
})().catch(e=>{console.error(e);process.exitCode=1;});
