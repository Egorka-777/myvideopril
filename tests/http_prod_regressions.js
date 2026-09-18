"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const worker = require("../tools/uploader/worker-http.js");
const testWorker = fs.readFileSync(path.join(__dirname, "..", "tools", "uploader", "worker-http-test.js"), "utf8");
const prodWorker = fs.readFileSync(path.join(__dirname, "..", "tools", "uploader", "worker-http.js"), "utf8");

assert.match(prodWorker, /validateBatchJob/);
assert.match(prodWorker, /HTTP batch/);
assert.match(prodWorker, /isAmbiguousUploadError/);
assert.match(prodWorker, /manual_check/);
assert.match(prodWorker, /for \(let i = 0; i < total; i\+\+\)/);
assert.doesNotMatch(prodWorker, /browser_profiles\/\$\{[^}]+\}\/stop/);
assert.notStrictEqual(prodWorker, testWorker, "production worker must differ from test reference");

const batch = worker.validateBatchJob({
  profileId: "p1",
  localPort: 3001,
  expectedIp: "1.2.3.4",
  items: [
    { localJobId: "a", video: path.join(__dirname, "title_cleaner_regressions.js"), title: "Test title", scheduledUnixSeconds: Math.floor(Date.now() / 1000) + 3600 }
  ]
});
assert.strictEqual(batch.items.length, 1);
assert.strictEqual(batch.items[0].localJobId, "a");

console.log("http production worker regression checks passed");
