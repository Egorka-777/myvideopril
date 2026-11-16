'use strict';
// Execute the real worker lifecycle with only Dolphin/CDP boundaries substituted.
const assert=require('assert'),fs=require('fs'),path=require('path'),vm=require('vm');
const filename=path.resolve(__dirname,'../tools/uploader/worker.js');
const source=fs.readFileSync(filename,'utf8').replace('const { automationEndpoint, releaseTaskPage }','let { automationEndpoint, releaseTaskPage }')+`
module.exports.lifecycle={closeProfileSafely,stopProfile,set:(j,owned,request,b)=>{job=j;profileStarted=true;profileOwned=owned;api=request;browser=b;releaseTaskPage=async()=>{};send=()=>{};},started:()=>profileStarted};`;
const mod={exports:{}};
vm.runInNewContext(source,{require:require('module').createRequire(filename),module:mod,exports:mod.exports,console,process,URL,AbortController,setTimeout,clearTimeout,Buffer,__dirname:path.dirname(filename)},{filename});
(async()=>{
 const W=mod.exports.lifecycle;
 const page={waitForTimeout:async()=>{},isClosed:()=>false};
 for(const owned of [true,false]){
  let stops=0,disconnects=0;
  W.set({profileId:'shared-profile',keepProfileOpen:true},owned,async()=>{stops++;return {success:true};},{close:async()=>{disconnects++;}});
  await W.closeProfileSafely(page);assert.equal(stops,0);assert.equal(disconnects,1);assert(W.started());
  W.set({profileId:'shared-profile',keepProfileOpen:false},owned,async route=>{assert(route.endsWith('/shared-profile/stop'));stops++;return {success:true};},{close:async()=>{disconnects++;}});
  await W.closeProfileSafely(page);assert.equal(stops,1);assert.equal(disconnects,2);assert(!W.started());
 }
 for(const response of [{},{success:false}]){
  W.set({profileId:'shared-profile',keepProfileOpen:false},false,async()=>response,{close:async()=>{}});
  await assert.rejects(W.closeProfileSafely(page),/не подтвердил закрытие/);assert(W.started());
 }
 W.set({profileId:'shared-profile',keepProfileOpen:false},false,async()=>{throw Error('network failure');},{close:async()=>{}});
 await assert.rejects(W.closeProfileSafely(page),/network failure/);assert(W.started());
 console.log('PASS actual worker lifecycle: intermediate batches stay open; final success closes owned/attached profiles; missing/rejected stop and network errors never become success.');
})().catch(e=>{console.error(e);process.exitCode=1;});
