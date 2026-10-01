"use strict";
const assert=require("assert");
const {postEvidence,setNativeSchedule}=require("../tools/uploader/tiktok-studio-evidence.js");
const at=Math.ceil((Date.now()/1000+2000)/60)*60;
const item={caption:"Caption\n#tag",publishMode:"scheduled",scheduledUnixSeconds:at};
const req={feature_common_info_list:[{schedule_time:at}],single_post_req_list:[{single_post_feature_info:{text:item.caption}}]};
const body={status_code:0,single_post_resp_list:[{item_id:"native-post-1",status_code:0}]};
assert.equal(postEvidence(200,body,req,item).id,"native-post-1");
assert.throws(()=>postEvidence(200,{status_code:5,status_msg:"Invalid parameters"},req,item),/отклонил/);
assert.throws(()=>postEvidence(500,body,req,item),/отклонил/);
assert.throws(()=>postEvidence(200,body,{...req,feature_common_info_list:[{schedule_time:at+60}]},item),/Время/);
assert.throws(()=>postEvidence(200,body,{...req,single_post_req_list:[{text:"wrong"}]},item),/подпись/);
assert.throws(()=>postEvidence(200,body,{},item),/Неизвестная структура/);
assert.equal(postEvidence(200,{status_code:0},req,item).id,"", "ack without a post ID cannot establish Studio visibility");
(async()=>{
  await assert.rejects(setNativeSchedule({}, {...item,scheduledUnixSeconds:Math.floor(Date.now()/1000)+10}),/устарел/);
  await assert.rejects(setNativeSchedule({}, {...item,scheduledUnixSeconds:at+1}),/точностью до минуты/);
  console.log("OK: tiktok_native_evidence_regressions.js (rejection, wrong caption, wrong schedule, stale slot)");
})().catch(e=>{console.error(e);process.exitCode=1;});
