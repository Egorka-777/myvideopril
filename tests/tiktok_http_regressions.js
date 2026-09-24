"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const studio = fs.readFileSync(path.join(root, "tools", "uploader", "worker-tiktok.js"), "utf8");
const httpWorker = fs.readFileSync(path.join(root, "tools", "uploader", "worker-tiktok-http.js"), "utf8");
const uploaderCs = fs.readFileSync(path.join(root, "src", "Uploader.cs"), "utf8");
const httpRunnerCs = fs.readFileSync(path.join(root, "src", "HttpTikTokUploadRunner.cs"), "utf8");
const tiktokCs = fs.readFileSync(path.join(root, "src", "TikTok.cs"), "utf8");
const panelCs = fs.readFileSync(path.join(root, "src", "TikTokWorkspacePanel.cs"), "utf8");

const studioMod = require("../tools/uploader/worker-tiktok.js");
const httpMod = require("../tools/uploader/worker-tiktok-http.js");

// Studio modal helpers exported
assert.strictEqual(typeof studioMod.dismissTikTokBlockingDialogs, "function");
assert.strictEqual(typeof studioMod.collectVisibleDialogTexts, "function");
assert.ok(studioMod.dialogTextMatches("Turn on automatic content checks?", studioMod.BLOCKING_CONTENT_CHECK_PATTERNS));
assert.ok(studioMod.dialogTextMatches("New editing features added", studioMod.TIP_DIALOG_PATTERNS));
assert.match(studio, /async function dismissTikTokBlockingDialogs/);
assert.match(studio, /Cancel\|Отмена/);
assert.doesNotMatch(studio, /Turn on automatic content checks\?[\s\S]{0,400}Turn on/);
assert.match(studio, /Поле подписи TikTok недоступно/);
assert.doesNotMatch(studio, /Не найдено поле подписи TikTok\. Проверьте вход в аккаунт/);

// HTTP vs Studio separation
assert.match(uploaderCs, /WorkerTikTokHttp/);
assert.match(uploaderCs, /RunTikTokHttp/);
assert.match(uploaderCs, /RunTikTokStudio/);
assert.match(tiktokCs, /Быстрая загрузка \(HTTP beta\)/);
assert.match(tiktokCs, /Через Studio/);
assert.match(panelCs, /RunUploadHttpAsync/);
assert.match(panelCs, /RunUploadStudioAsync/);

// HTTP worker must not use Studio DOM upload (ignore comment mentions)
const httpCode = httpWorker.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "");
for (const forbidden of [
  "setInputFiles(",
  'input[type="file"]',
  "fillCaption(",
  "clickPost(",
  "tiktokstudio/upload"
]) {
  assert.doesNotMatch(httpCode, new RegExp(forbidden.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
}
assert.match(httpWorker, /transport:\s*TRANSPORT/);
assert.match(httpWorker, /const TRANSPORT = "http"/);

// Secret redaction
assert.ok(httpMod.redactDiagnostic("sessionid=abc123").includes("[скрыто]"));
assert.ok(httpMod.redactDiagnostic("Authorization: Bearer xyz").includes("[скрыто]"));

// State machine
assert.deepStrictEqual(httpMod.ITEM_STATES.includes("manual_check"), true);
assert.strictEqual(httpMod.canSafeRetry("project_created"), false);
assert.strictEqual(httpMod.canSafeRetry("preflight"), true);
assert.strictEqual(httpMod.canSafeRetry("uploading"), false);

const signedGet = httpMod.awsSigV4Headers({
  method: "GET",
  url: "https://www.tiktok.com/top/v1?Action=ApplyUploadInner&Version=2020-11-19",
  body: "",
  accessKeyId: "AKIA_TEST",
  secretAccessKey: "secret",
  sessionToken: "token",
  region: "ap-singapore-1",
  service: "vod"
});
assert.ok(!signedGet.Accept, "Accept must not be part of AWS signature headers");
assert.match(signedGet.Authorization, /SignedHeaders=host;x-amz-date;x-amz-security-token/);
assert.strictEqual(httpMod.vodRegionFromDc("useast2a"), "us-east-1");
assert.strictEqual(httpMod.parseApplyUploadResult({ ok: true, json: { Result: { InnerUploadAddress: { UploadNodes: [{ Vid: "v1", UploadHost: "h", SessionKey: "sk", StoreInfos: [{ StoreUri: "u", Auth: "a" }] }] } } } }).videoId, "v1");

// Unix schedule validation (via validateItem logic in file)
assert.match(httpWorker, /scheduledUnixSeconds/);
assert.match(httpWorker, /TIKTOK_SCHEDULE_MIN_SEC/);

// Batch items in C# job model
assert.match(httpRunnerCs, /HttpTikTokItemJob\[\]/);

// Build includes worker file
assert.match(fs.readFileSync(path.join(root, "scripts", "Build-TikTokHttpTest.ps1"), "utf8"), /worker-tiktok-http\.js/);

console.log("OK: tiktok_http_regressions.js");
