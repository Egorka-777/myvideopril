'use strict';
const assert=require('assert'),fs=require('fs'),cp=require('child_process'),os=require('os'),path=require('path'),crypto=require('crypto');
const C=require('../tools/uploader/studio-editor-controls'),S=require('../tools/uploader/youtube-studio-upload'),B=require('../tools/uploader/studio-browser');
const base='2207c946c7f85b7b9363f5641be84a223a6aba47';
const before=cp.execFileSync('git',['show',base+':tools/uploader/youtube-studio-upload.js'],{encoding:'utf8'}).replace(/\r\n/g,'\n'),after=fs.readFileSync('tools/uploader/youtube-studio-upload.js','utf8').replace(/\r\n/g,'\n');
const body=(s,a,b)=>s.slice(s.indexOf(a),s.indexOf(b,s.indexOf(a)));
assert.equal(body(after,'async function publishPublic','function videoId'),body(before,'async function publishPublic','function videoId'),'Public selection must retain the known working behavior');
const text='X'.repeat(1650),p=C.typingPolicy(text,{name:'описания',delay:18,activationMs:25000,verifyMs:35000});
assert.equal(p.chunkSize,40);assert.equal(p.typeDelay,20);assert.equal(p.chunkVerify,35000);assert.equal(p.finalVerify,45000);assert.equal(p.maxAttempts,3);assert.equal(p.totalMs,3*p.attemptMs+6600);
assert(S.metadataBudget({title:'Title',description:text,tags:[]},{})>p.totalMs);
assert.equal(S.BUILD,'2026-10-22-studio-upload-recovery-v20');
const temp=fs.mkdtempSync(path.join(os.tmpdir(),'vb-studio-manifest-'));
try{
 const root=path.join(temp,'tools/uploader');fs.mkdirSync(root,{recursive:true});
 const files=['VideoBatch.exe','tools/uploader/worker.js','tools/uploader/youtube-studio-upload.js','tools/uploader/studio-editor-controls.js','tools/uploader/studio-browser.js'].map(file=>{const bytes=Buffer.from(file);fs.writeFileSync(path.join(temp,file),bytes);return {file,sha256:crypto.createHash('sha256').update(bytes).digest('hex')};});
 const manifest={build:S.BUILD,protocolVersion:2,files},write=()=>fs.writeFileSync(path.join(root,'STUDIO_UPLOADER_VERSION.json'),'\uFEFF'+JSON.stringify(manifest));
 write();B.assertRuntimePackage(S.BUILD,root);
 manifest.protocolVersion=1;write();assert.throws(()=>B.assertRuntimePackage(S.BUILD,root),/разными комплектами/);
 manifest.protocolVersion=2;manifest.files=[];write();assert.throws(()=>B.assertRuntimePackage(S.BUILD,root),/неполный/);
 manifest.files=files;write();fs.appendFileSync(path.join(root,'worker.js'),'changed');assert.throws(()=>B.assertRuntimePackage(S.BUILD,root),/отличается/);
}finally{fs.rmSync(temp,{recursive:true,force:true});}
console.log('Studio v20 contract PASS: strict finite description policy, unchanged Public choice, complete EXE/JS manifest and tamper detection.');
