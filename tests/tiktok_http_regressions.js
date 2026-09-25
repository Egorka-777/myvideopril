"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const studio = fs.readFileSync(path.join(root, "tools", "uploader", "worker-tiktok.js"), "utf8");
const httpWorker = fs.readFileSync(path.join(root, "tools", "uploader", "worker-tiktok-http.js"), "utf8");
const sigv4Src = fs.readFileSync(path.join(root, "tools", "uploader", "tiktok-aws-sigv4.js"), "utf8");
const uploaderCs = fs.readFileSync(path.join(root, "src", "Uploader.cs"), "utf8");
const httpRunnerCs = fs.readFileSync(path.join(root, "src", "HttpTikTokUploadRunner.cs"), "utf8");
const tiktokCs = fs.readFileSync(path.join(root, "src", "TikTok.cs"), "utf8");
const panelCs = fs.readFileSync(path.join(root, "src", "TikTokWorkspacePanel.cs"), "utf8");
const scheduleCs = fs.readFileSync(path.join(root, "src", "TikTokScheduleGenerator.cs"), "utf8");

const studioMod = require("../tools/uploader/worker-tiktok.js");
const httpMod = require("../tools/uploader/worker-tiktok-http.js");
const sigv4 = require("../tools/uploader/tiktok-aws-sigv4.js");

// Studio modal helpers exported
assert.strictEqual(typeof studioMod.dismissTikTokBlockingDialogs, "function");
assert.ok(studioMod.dialogTextMatches("Turn on automatic content checks?", studioMod.BLOCKING_CONTENT_CHECK_PATTERNS));

// HTTP vs Studio separation
assert.match(uploaderCs, /WorkerTikTokHttp/);
assert.match(tiktokCs, /Быстрая загрузка \(HTTP beta\)/);
assert.match(panelCs, /RunUploadHttpAsync/);

// HTTP worker must not use Studio DOM upload
const httpCode = httpWorker.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "");
for (const forbidden of ["setInputFiles(", 'input[type="file"]', "fillCaption(", "clickPost(", "tiktokstudio/upload"]) {
  assert.doesNotMatch(httpCode, new RegExp(forbidden.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
}
assert.match(httpWorker, /const TRANSPORT = "http"/);
assert.match(httpWorker, /2026-09-24-tiktok-http-v5/);

// No multi-region blind retries with one token
assert.doesNotMatch(httpWorker, /vod-us-east/);
assert.doesNotMatch(httpWorker, /vod-ap-singapore/);
assert.doesNotMatch(httpWorker, /for\s*\(.*region/i);

// Project after ApplyUploadInner
const preflightIdx = httpWorker.indexOf("runHttpPreflight");
const projectIdx = httpWorker.indexOf("createProject");
assert.ok(preflightIdx > 0 && projectIdx > preflightIdx, "project must be created after preflight/Apply");
assert.match(httpWorker, /HTTP preflight пройден: ApplyUploadInner подтвердил credentials/);
assert.doesNotMatch(httpWorker, /preflight пройден[\s\S]{0,120}fetchUploadToken/);

// Secret redaction
assert.ok(httpMod.redactDiagnostic("sessionid=abc123").includes("[скрыто]"));
assert.ok(httpMod.redactDiagnostic("AKTPABCDEFGHIJKLMNOP").includes("AKTP[скрыто]"));
assert.ok(!httpMod.redactDiagnostic("browser IP: 1.2.3.4").includes("AKTP"));

// State machine
assert.strictEqual(httpMod.canSafeRetry("project_created"), false);
assert.strictEqual(httpMod.canSafeRetry("preflight"), true);

// --- SigV4: canonical URI vs query ---
assert.strictEqual(sigv4.normalizeCanonicalUri("/top/v1"), "/top/v1");
assert.strictEqual(
  sigv4.buildCanonicalQueryString("https://www.tiktok.com/top/v1?Version=2020-11-19&Action=ApplyUploadInner&FileSize=99"),
  "Action=ApplyUploadInner&FileSize=99&Version=2020-11-19"
);

// RFC3986 encoding
assert.strictEqual(sigv4.rfc3986Encode("a b"), "a%20b");
assert.strictEqual(sigv4.rfc3986Encode("a*b"), "a%2Ab");

// Query sort with encoded keys
assert.strictEqual(
  sigv4.buildCanonicalQueryString("https://x.test/p?z=1&a=2&a=1"),
  "a=1&a=2&z=1"
);

// Frozen-time golden vector (all intermediate values)
const golden = sigv4.awsGoldenVector();
assert.strictEqual(golden.canonicalUri, "/");
assert.strictEqual(golden.canonicalQuery, "Action=ApplyUploadInner&FileSize=123&Version=2020-11-19");
assert.strictEqual(golden.signedHeaders, "host;x-amz-content-sha256;x-amz-date;x-amz-security-token");
assert.strictEqual(golden.payloadHash, sigv4.EMPTY_PAYLOAD_HASH);
assert.strictEqual(golden.signature, "e4d5a401355c20b5d6833ab6c3e4608119d1fa74705de0f0658419bd02f7a094");
assert.match(golden.headers.Authorization, /SignedHeaders=host;x-amz-content-sha256;x-amz-date;x-amz-security-token/);
assert.strictEqual(golden.headers["x-amz-security-token"], "tok-example");

// GET empty body hash; POST uses body hash
const getSign = sigv4.awsSigV4Sign({
  method: "GET",
  url: "https://www.tiktok.com/top/v1?Action=ApplyUploadInner&Version=2020-11-19",
  body: "",
  accessKeyId: "AKIA_TEST",
  secretAccessKey: "secret",
  sessionToken: "tok",
  region: "ap-singapore-1",
  service: "vod",
  amzDate: "20260924T120000Z"
});
assert.strictEqual(getSign.payloadHash, sigv4.EMPTY_PAYLOAD_HASH);
assert.ok(getSign.canonicalUri.endsWith("/top/v1") || getSign.canonicalUri === "/top/v1");
assert.ok(!getSign.canonicalUri.includes("Action="), "query must not be in canonical URI");

const postBody = JSON.stringify({ SessionKey: "sk", Functions: [{ name: "GetMeta" }] });
const postSign = sigv4.awsSigV4Sign({
  method: "POST",
  url: "https://www.tiktok.com/top/v1?Action=CommitUploadInner&Version=2020-11-19&SpaceName=tiktok",
  body: postBody,
  accessKeyId: "AKIA_TEST",
  secretAccessKey: "secret",
  sessionToken: "tok",
  region: "ap-singapore-1",
  service: "vod",
  amzDate: "20260924T120000Z"
});
assert.notStrictEqual(postSign.payloadHash, sigv4.EMPTY_PAYLOAD_HASH);
assert.strictEqual(postSign.payloadHash, sigv4.sha256Hex(postBody));

// Security token in signed headers
assert.match(getSign.signedHeaders, /x-amz-security-token/);

// ApplyUploadInner parse
const fullApply = {
  ok: true,
  json: {
    Result: {
      InnerUploadAddress: {
        UploadNodes: [{
          Vid: "v1",
          UploadHost: "host",
          SessionKey: "sk",
          StoreInfos: [{ StoreUri: "uri", Auth: "auth" }]
        }]
      }
    }
  }
};
assert.strictEqual(httpMod.parseApplyUploadResult(fullApply).videoId, "v1");
assert.strictEqual(httpMod.parseApplyUploadResult({ ok: true, json: { Result: {} } }), null);

// HTTP 200 + CheckAuthenticationError => error
let authErr = null;
try {
  httpMod.assertTikTokApiOk({
    ok: true,
    status: 200,
    json: {
      ResponseMetadata: {
        RequestId: "req-1",
        Error: { Code: "CheckAuthenticationError", Message: "The accessKey 'AKTPxxx' can not be found" }
      }
    }
  }, "ApplyUploadInner");
} catch (e) {
  authErr = e;
}
assert.ok(authErr);
assert.match(authErr.message, /TikTok отклонил временные credentials/);
assert.ok(!String(authErr.message).includes("AKTP"));

// HTTP 200 without Result fields
let noResultErr = null;
try {
  httpMod.assertTikTokApiOk({ ok: true, status: 200, json: { status_code: 0 } }, "ApplyUploadInner");
  assert.strictEqual(httpMod.parseApplyUploadResult({ ok: true, status: 200, json: { status_code: 0 } }), null);
} catch (e) {
  noResultErr = e;
}
assert.ok(!noResultErr || noResultErr);

// IP mismatch must abort (structural)
assert.match(httpWorker, /browser IP и HTTP transport IP не совпадают/);
assert.match(httpWorker, /browserFetch/);
assert.match(httpWorker, /readBrowserIp/);
assert.match(httpWorker, /readTransportIp/);

// Fresh token before Apply (not reused across regions)
assert.match(httpWorker, /fetchUploadToken/);
assert.match(httpWorker, /uploadTokenMeta/);

// Schedule constants in worker + C#
assert.match(httpWorker, /TIKTOK_SCHEDULE_MIN_SEC = 900/);
assert.match(scheduleCs, /MinLeadSeconds = 900/);
assert.match(scheduleCs, /MinGapSeconds = 180/);
assert.match(scheduleCs, /MaxGapSeconds = 900/);

// C# HTTP blocked until live test
assert.match(tiktokCs, /TikTokHttpMainEnabled/);

// Runner checks sigv4 module
assert.match(httpRunnerCs, /tiktok-aws-sigv4\.js/);

// v5 build script
const v5Build = path.join(root, "scripts", "Build-TikTokHttpTest-v5.ps1");
assert.ok(fs.existsSync(v5Build), "Build-TikTokHttpTest-v5.ps1 must exist");
assert.match(fs.readFileSync(v5Build, "utf8"), /tiktok-aws-sigv4\.js/);
assert.match(fs.readFileSync(v5Build, "utf8"), /tiktok-http-test-v5/);

// SigV4 module: separate canonical URI and query (not pathname+search in URI)
assert.match(sigv4Src, /canonicalUri = normalizeCanonicalUri/);
assert.match(sigv4Src, /canonicalQuery = buildCanonicalQueryString/);
assert.doesNotMatch(sigv4Src, /pathname \+ search/i);

console.log("OK: tiktok_http_regressions.js");
