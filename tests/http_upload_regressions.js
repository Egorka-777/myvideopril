"use strict";

const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const worker = require("../tools/uploader/worker-http-test.js");

const html = '<script>ytcfg.set({"INNERTUBE_API_KEY":"test-key","SESSION_INDEX":"2","DELEGATED_SESSION_ID":"delegate","INNERTUBE_CLIENT_VERSION":"1.20260918.00.00"})</script>';
const bootstrap = worker.parseStudioBootstrap(html,"https://studio.youtube.com/channel/UC123456789/videos");
assert.deepStrictEqual(bootstrap,{
  channelId:"UC123456789",apiKey:"test-key",authUser:"2",delegatedSessionId:"delegate",clientVersion:"1.20260918.00.00"
});

assert.deepStrictEqual(worker.studioPageState("https://studio.youtube.com/", "Загрузка"),{ready:false,blocker:""});
assert.deepStrictEqual(worker.studioPageState("https://studio.youtube.com/channel/UC123456789", "Панель управления"),{ready:true,blocker:""});
assert.match(worker.studioPageState("https://accounts.google.com/ServiceLogin", "").blocker,/вход в Google/);
assert.match(worker.studioPageState("https://studio.youtube.com/", "unusual traffic challenge").blocker,/проверку безопасности/);
assert.strictEqual(worker.safeDiagnosticUrl("https://studio.youtube.com/channel/UC123/videos?key=secret#part"),"https://studio.youtube.com/channel/UC123/videos");
assert.doesNotMatch(worker.redactDiagnostic("Authorization: Bearer top-secret"),/top-secret/);
assert.doesNotMatch(worker.redactDiagnostic("SAPISID=private-value"),/private-value/);
assert.doesNotMatch(worker.redactDiagnostic("Ошибка https://studio.youtube.com/path?token=private"),/private/);
assert.strictEqual(worker.dolphinProfileStatus({data:{status:"stopped"}}),"stopped");
assert.strictEqual(worker.dolphinProfileIsRunning("stopped"),false);
assert.strictEqual(worker.dolphinProfileIsRunning("running"),true);
assert.strictEqual(worker.dolphinProfileIsRunning("запущен"),true);

worker.assertExpectedChannel("UC123456789","UC123456789");
assert.throws(()=>worker.assertExpectedChannel("UCexpected","UCwrong"),/другой YouTube-канал/);
assert.throws(()=>worker.assertProxy("1.2.3.4","9.9.9.9","9.9.9.9"),/Прокси не используется/);
worker.assertProxy("1.2.3.4","1.2.8.9","9.9.9.9");
assert.throws(()=>worker.assertProxy("1.2.3.4","5.6.7.8","9.9.9.9"),/IP профиля изменился/);

const data=bootstrap,sessionToken="session",front="front",scotty="scotty",title="Тест | без цифр";
const create=worker.makeCreateVideoBody(data,sessionToken,front,scotty,title);
assert.strictEqual(create.channelId,data.channelId);
assert.strictEqual(create.context.request.sessionInfo.token,sessionToken);
assert.strictEqual(create.initialMetadata.title.newTitle,title);
assert.strictEqual(create.initialMetadata.privacy.newPrivacy,"PRIVATE");
assert.strictEqual(create.resourceId.scottyResourceId.id,scotty);

const scheduled=Math.floor(Date.now()/1000)+7200;
const metadata=worker.makeMetadataBody(data,sessionToken,"video-id",scheduled);
assert.strictEqual(metadata.encryptedVideoId,"video-id");
assert.strictEqual(metadata.draftState.operation,"MDE_DRAFT_STATE_UPDATE_OPERATION_REMOVE_DRAFT_STATE");
assert.strictEqual(metadata.scheduledPublishing.set.timeSec,String(scheduled));
assert.strictEqual(metadata.scheduledPublishing.set.privacy,"PUBLIC");

const temp=fs.mkdtempSync(path.join(os.tmpdir(),"videobatch-http-test-"));
try {
  const video=path.join(temp,"one.mp4");fs.writeFileSync(video,Buffer.from([1,2,3]));
  const valid=worker.validateJob({profileId:"profile",localPort:3001,video,title,expectedIp:"1.2.3.4",scheduledUnixSeconds:scheduled});
  assert.strictEqual(valid.title,title);
  assert.throws(()=>worker.validateJob({profileId:"profile",localPort:3001,video,title,expectedIp:"",scheduledUnixSeconds:scheduled}),/ожидаемый IP/);
  assert.throws(()=>worker.validateJob({profileId:"profile",localPort:3001,video,title:"x".repeat(101),expectedIp:"1.2.3.4",scheduledUnixSeconds:scheduled}),/100 символов/);
} finally { fs.rmSync(temp,{recursive:true,force:true}); }

const mainWorker=fs.readFileSync(path.join(__dirname,"..","tools","uploader","worker.js"),"utf8");
const testWorker=fs.readFileSync(path.join(__dirname,"..","tools","uploader","worker-http-test.js"),"utf8");
assert.match(testWorker,/never\s*\/\/ stops a Dolphin profile/);
assert.doesNotMatch(testWorker,/browser_profiles\/\$\{[^}]+\}\/stop/);
assert.match(testWorker,/credentials:\s*"include"/);
assert.match(testWorker,/x-goog-upload-offset/);
assert.match(testWorker,/STUDIO_READY_TIMEOUT_MS\s*=\s*5\s*\*\s*60\s*\*\s*1000/);
assert.doesNotMatch(testWorker,/page\.goto\("https:\/\/studio\.youtube\.com"[\s\S]{0,500}waitForTimeout\(2500\)/,
  "Studio readiness must not be decided by a fixed 2.5 second sleep.");
assert.match(testWorker,/diagnostics/);
assert.match(testWorker,/screenshot/);
assert.match(testWorker,/requestfailed/);
assert.match(testWorker,/DOLPHIN_START_TIMEOUT_MS\s*=\s*2\s*\*\s*60\s*\*\s*1000/);
assert.doesNotMatch(testWorker,/throw new Error\("Профиль уже открыт без порта автоматизации/,
  "The worker must not invent an already-running state after an unrelated start failure.");
assert.match(testWorker,/Исходная ошибка запуска|Исходная ошибка:/);
assert.ok(mainWorker.length>100000,"The existing production worker must remain present and separate.");

console.log("http upload regression checks passed");
