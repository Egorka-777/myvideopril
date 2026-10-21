'use strict';
const assert=require('assert'),fs=require('fs'),cp=require('child_process');
const C=require('../tools/uploader/studio-editor-controls'),S=require('../tools/uploader/youtube-studio-upload');
const base='47515b90ebf064baa7251e87b25da72d8149f6f1';
const old=relative=>cp.execFileSync('git',['show',base+':'+relative],{encoding:'utf8'}).replace(/\r\n/g,'\n');
const current=relative=>fs.readFileSync(relative,'utf8').replace(/\r\n/g,'\n');
const file='tools/uploader/youtube-studio-upload.js',before=old(file),after=current(file);
for(const [start,end] of [['async function publishSuccessVisible','async function diagnostics'],["  progress('upload_transfer','Проверяю завершение передачи файла.');",'module.exports=']]){
 assert.equal(after.slice(after.indexOf(start),after.indexOf(end)),before.slice(before.indexOf(start),before.indexOf(end)),'Publication, ID verification and Close contract changed');
}
assert.equal(current('tools/uploader/studio-browser.js'),old('tools/uploader/studio-browser.js').replace(".slice(0,1200),{timeout:800})",".slice(0,1200),null,{timeout:800})"),'Only correct the read-only completion probe timeout argument; preserve Publish/ID/Close actions');
assert.equal(current('tools/uploader/worker.js').replaceAll('2026-10-21-studio-shorts-queue-v19f','2026-10-21-studio-shorts-queue-v19e'),old('tools/uploader/worker.js'));
const text='X'.repeat(1650),p=C.typingPolicy(text,{name:'описания',delay:18,activationMs:25000,verifyMs:35000});
assert.equal(p.chunkSize,40);assert.equal(p.typeDelay,20);assert.equal(p.chunkVerify,35000);assert.equal(p.finalVerify,45000);assert.equal(p.maxAttempts,3);assert.equal(p.totalMs,3*p.attemptMs+6600);
assert(S.metadataBudget({title:'Title',description:text,tags:[]},{})>p.totalMs);
console.log('Description contract PASS: exact v19e publication/ID/Close code preserved; metadata budget covers finite three-attempt policy.');
