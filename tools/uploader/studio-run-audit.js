'use strict';
// Offline verification of AppDiagnostics JSONL. Never connects to accounts or publishes.
const fs=require('fs'),path=require('path'),crypto=require('crypto');
function audit(records,{profiles,since,build,perChannel=3,minLength=1500,maxLength=1900}){
 if(!Array.isArray(profiles)||profiles.length!==2||new Set(profiles).size!==2)throw Error('Specify two distinct Dolphin profile IDs.');
 if(!/(?:Z|[+-]\d{2}:\d{2})$/.test(since)||!Number.isFinite(Date.parse(since)))throw Error('--since must include its UTC offset.');
 const all=records.filter(r=>Date.parse(r.time)>=Date.parse(since)),channels=[];
 for(const profile of profiles){
  const rows=all.filter(r=>r.account===profile&&r.task),tasks=[...new Set(rows.map(r=>r.task))],issues=[];
  if(tasks.length!==1)issues.push('Expected one fresh task, found '+tasks.length+'; narrow the --since window.');
  const starts=rows.filter(r=>r.stage==='start');if(starts.length!==1||!starts[0].message?.includes('Worker '+build))issues.push('Missing or different worker BUILD.');
  const errors=rows.filter(r=>r.level==='error'||r.stage==='error');if(errors.length)issues.push('Task contains '+errors.length+' error records.');
  const confirmed=[],descriptions=[];let published=0,closed=0,ready=0,files=0,full=0,newTabs=0,screens=0;
  for(const row of rows){
   const text=row.message||'';
   if(row.stage==='file_select'){
    if(files&&ready<files)issues.push('Next file was selected before prior confirmation/ready.');if(files&&newTabs<files)issues.push('Next file was not opened in a new tab.');files++;
   }
   if(row.stage==='metadata_description'&&text.startsWith('Описание проверено целиком:')){
    const match=text.match(/целиком: (\d+) символов; SHA256 ([a-f0-9]{64})\./);
    if(!match||Number(match[1])<minLength||Number(match[1])>maxLength)issues.push('Missing or unexpected SEO description length/hash.');
    else {full++;descriptions.push({length:Number(match[1]),sha256:match[2]});}
   }
   if(row.stage==='studio_publish'&&text==='Нажимаю кнопку публикации один раз.')published++;
   if(row.stage==='item_confirmed'){
    const id=text.match(/·\s*([A-Za-z0-9_-]{11})\s*$/)?.[1];
    if(!id)issues.push('Confirmation has no video ID.');else confirmed.push(id);
    if(full<confirmed.length||published<confirmed.length)issues.push('Confirmation before full description verification or Publish.');
   }
   if(row.stage==='studio_publish_confirmed')screens++;
   if(row.stage==='studio_new_tab'){newTabs++;if(newTabs>confirmed.length)issues.push('New tab before confirmation.');}
   if(row.stage==='studio_close'&&/Нажимаю.*один раз/.test(text)){closed++;if(closed>confirmed.length)issues.push('Close before saved-ID confirmation.');}
   if(row.stage==='studio_ready'&&text.startsWith('Видео подтверждено.')){ready++;if(ready>confirmed.length)issues.push('Ready before confirmation.');}
  }
  for(const [name,count] of Object.entries({files,fullDescriptions:full,publishClicks:published,itemConfirmed:confirmed.length,completionScreens:screens,ready}))if(count!==perChannel)issues.push(name+': '+count+', expected '+perChannel+'.');
  if(newTabs!==perChannel-1)issues.push('newTabs: '+newTabs+', expected '+(perChannel-1)+'.');
  if(new Set(confirmed).size!==confirmed.length)issues.push('Repeated confirmed video ID.');
  if(!rows.some(r=>r.stage==='done'))issues.push('Missing completed task.');
  channels.push({profile,tasks,counts:{files,fullDescriptions:full,publishClicks:published,itemConfirmed:confirmed.length,closeClicks:closed,newTabs,completionScreens:screens,ready},confirmed,descriptions,errors:errors.map(r=>({time:r.time,stage:r.stage,message:r.message})),issues,status:issues.length?'FAIL':'PASS_LOG_CONTRACT'});
 }
 const ids=channels.flatMap(c=>c.confirmed);if(new Set(ids).size!==ids.length)for(const channel of channels){channel.issues.push('Video ID repeated across selected profiles.');channel.status='FAIL';}
 return {kind:'offline-app-log-audit',since,build,liveAccountAccessPerformed:false,status:channels.every(c=>!c.issues.length)?'PASS_LOG_CONTRACT':'FAIL',channels};
}
module.exports={audit};
if(require.main===module){
 try{
  const args={};for(let n=2;n<process.argv.length;n+=2){if(!process.argv[n].startsWith('--')||!process.argv[n+1])throw Error('Use --log file --since ISO-time --profiles id1,id2 [--out file].');args[process.argv[n].slice(2)]=process.argv[n+1];}
  const bytes=fs.readFileSync(args.log),raw=bytes.toString('utf8').replace(/^\uFEFF/,''),records=raw.split(/\r?\n/).filter(Boolean).map(line=>JSON.parse(line));
  const report=audit(records,{profiles:args.profiles?.split(',').map(x=>x.trim()).filter(Boolean),since:args.since,build:args.build||require('./youtube-studio-upload').BUILD});
  report.input={path:path.resolve(args.log),sha256:crypto.createHash('sha256').update(bytes).digest('hex')};
  const json=JSON.stringify(report,null,2);if(args.out){fs.writeFileSync(args.out,json,{flag:'wx'});console.log(path.resolve(args.out));}else console.log(json);
  if(report.status!=='PASS_LOG_CONTRACT')process.exitCode=1;
 }catch(error){console.error(error.message);process.exitCode=2;}
}
