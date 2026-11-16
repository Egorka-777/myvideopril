"use strict";
const assert=require("assert"),fs=require("fs"),path=require("path");
const native=require("../tools/uploader/tiktok-native-publish");
const row=(id,caption,text=caption+" Everyone")=>({id,caption,text});
const full="Identical first twenty characters but full caption A";
const other="Identical first twenty characters but full caption B";
const inventory={complete:true,rows:[row("1",full),row("2",other),row("3","pending","pending Only me · Content under review")]};
assert.deepEqual(native.reconcileInventory(inventory,[{caption:full},{caption:"new"},{caption:"pending",studioPending:true}]),[
  {id:"1",review:false,public:true},null,{id:"3",review:true,public:false}
]);
assert.equal(native.reconcileInventory(inventory,[{caption:full.slice(0,20)}])[0],null,"prefix matched a different full caption");
assert.throws(()=>native.reconcileInventory({...inventory,rows:[row("1",full),row("2",full)]},[{caption:full}]),/несколько/);
assert.throws(()=>native.reconcileInventory(inventory,[{caption:full},{caption:full}]),/нескольким заданиям/);
assert.throws(()=>native.reconcileInventory(inventory,[{caption:"absent",studioPending:true}]),/блокировка повтора/);
assert.throws(()=>native.reconcileInventory(inventory,[{caption:full,publishedVideoId:"missing"}]),/блокировка повтора/);
assert.throws(()=>native.reconcileInventory({...inventory,complete:false},[{caption:full}]),/не полностью/);
assert.throws(()=>native.reconcileInventory({...inventory,complete:false},[{caption:"new"}]),/не полностью/);
assert.equal(native.reconcileInventory({...inventory,complete:false},[{caption:"edited",publishedVideoId:"2"}])[0].id,"2","saved ID must remain authoritative");
assert.throws(()=>native.reconcileInventory({complete:true,rows:[row("1","Identical first twenty…")]},[{caption:full}]),/сокращённую/);
assert.throws(()=>native.reconcileInventory({complete:true,rows:[row("1",full),row("2","Identical first twenty…")]},[{caption:full}]),/сокращённую/,"truncated second candidate makes even an exact match ambiguous");
assert.throws(()=>native.reconcileInventory({complete:true,rows:[row("1","")]},[{caption:full}]),/недоступную/);
assert.throws(()=>native.reconcileInventory({complete:true,rows:[row("1",full,"Friends")]},[{caption:full}]),/доступ/);
assert.equal(native.reconcileInventory({complete:true,rows:[row("1","  one\n\n two  ")]},[{caption:"one two"}])[0].id,"1");
const worker=fs.readFileSync(path.join(__dirname,"../tools/uploader/worker-tiktok.js"),"utf8");
assert(worker.indexOf("const reconciled=")<worker.indexOf("lastUrl = await uploadOne"),"preflight must precede file upload");
assert(worker.includes("if(reconciled[i])continue;"));
assert(worker.includes("uploaded===0"),"skipped first item must not trigger next-upload navigation");
assert(worker.includes("requiresPersistenceAck:true"));
console.log("PASS: exact full-caption/manual match, ID authority, review, duplicates, prefix rejection, incomplete inventory, pending blocking, skipped-first navigation, persistence ACK contract");
