'use strict';
const assert=require('assert'),{audit}=require('../tools/uploader/studio-run-audit');
const options={profiles:['profile-a','profile-b'],since:'2026-10-21T20:00:00+07:00',build:'2026-10-22-studio-upload-recovery-v20'};
function fixture(){const rows=[];for(const account of options.profiles){const add=(stage,message,level='info')=>rows.push({time:'2026-10-21T20:01:00+07:00',account,task:'task-'+account,stage,message,level});add('start','Worker '+options.build);for(let i=1;i<=3;i++){add('file_select','file');add('metadata_description','Описание проверено целиком: 1650 символов; SHA256 '+'a'.repeat(64)+'.');add('studio_publish','Нажимаю кнопку публикации один раз.');add('studio_publish_confirmed','Studio showed the completion screen.');add('item_confirmed','Studio подтвердил открытый доступ · '+(account==='profile-a'?'alpha00000':'bravo00000')+i);add('studio_ready','Видео подтверждено. Следующий ролик откроется в новой вкладке.');if(i<3)add('studio_new_tab','Открыта новая вкладка загрузки следующего ролика.');}add('done','Complete');}return rows;}
assert.equal(audit(fixture(),options).status,'PASS_LOG_CONTRACT');
for(const change of [rows=>rows.filter(r=>r.stage!=='item_confirmed'),rows=>rows.filter(r=>r.stage!=='studio_new_tab'),rows=>rows.filter(r=>r.stage!=='metadata_description'),rows=>rows.map(r=>r.stage==='start'?{...r,message:'Worker old'}:r),rows=>[...rows,{...rows[0],stage:'error',level:'error',message:'description mismatch'}],rows=>[...rows,rows.find(r=>r.stage==='studio_publish')]])assert.equal(audit(change(fixture()),options).status,'FAIL');
const shared=fixture().map(row=>row.stage==='item_confirmed'?{...row,message:row.message.replace('bravo','alpha')}:row);assert.equal(audit(shared,options).status,'FAIL');
assert.throws(()=>audit(fixture(),{...options,since:'2026-10-21T20:00:00'}),/UTC offset/);
assert.throws(()=>audit(fixture(),{...options,profiles:['same','same']}),/distinct/);
// Exercise the exact PC command, including .NET JSONL's UTF-8 BOM/CRLF.
const fs=require('fs'),os=require('os'),path=require('path'),cp=require('child_process'),crypto=require('crypto');
const temp=fs.mkdtempSync(path.join(os.tmpdir(),'vb-studio-audit-'));
try{
 const log=path.join(temp,'app.jsonl'),out=path.join(temp,'audit.json');
 const raw=Buffer.from('\uFEFF'+fixture().map(x=>JSON.stringify(x)).join('\r\n'),'utf8');fs.writeFileSync(log,raw);
 const args=[path.resolve(__dirname,'../tools/uploader/studio-run-audit.js'),'--log',log,'--since',options.since,'--profiles','profile-a, profile-b','--build',options.build,'--out',out];
 let result=cp.spawnSync(process.execPath,args,{encoding:'utf8'});assert.equal(result.status,0,result.stderr);
 const report=JSON.parse(fs.readFileSync(out,'utf8'));assert.equal(report.status,'PASS_LOG_CONTRACT');assert.equal(report.input.sha256,crypto.createHash('sha256').update(raw).digest('hex'),'Evidence hash must match original file bytes, including BOM');
 result=cp.spawnSync(process.execPath,args,{encoding:'utf8'});assert.equal(result.status,2);assert(/EEXIST/.test(result.stderr));
 fs.unlinkSync(out);fs.writeFileSync(log,fixture().filter(x=>x.stage!=='studio_new_tab').map(x=>JSON.stringify(x)).join('\n'));
 result=cp.spawnSync(process.execPath,args,{encoding:'utf8'});assert.equal(result.status,1);assert.equal(JSON.parse(fs.readFileSync(out,'utf8')).status,'FAIL');
}finally{fs.rmSync(temp,{recursive:true,force:true});}
console.log('Studio run audit: two channels ×3, exact SEO evidence, version, errors, ordering, missing new tab and duplicate Publish checks PASS (fixtures).');
