"use strict";

const assert = require("assert");
const worker = require("../tools/uploader/worker-http-test.js");

// Reproduces youtube-20260918-160617.log themes: too-soon schedule, delegated headers, 403-after-videoId.

const fs = require("fs");
const os = require("os");
const path = require("path");
const tempVideo = path.join(os.tmpdir(), "vb-sched-test-" + Date.now() + ".mp4");
fs.writeFileSync(tempVideo, Buffer.from([1, 2, 3, 4]));
try {
  assert.throws(
    () => worker.validateJob({
      profileId: "p1", localPort: 3001, video: tempVideo, title: "Test",
      expectedIp: "1.2.3.4", scheduledUnixSeconds: Math.floor(Date.now() / 1000) + 600
    }),
    /15 минут/
  );
} finally {
  try { fs.unlinkSync(tempVideo); } catch (_) {}
}

const personal = worker.authHeaders({ authUser: "0", delegatedSessionId: null }, "sapisid");
assert.strictEqual(personal["x-goog-pageid"], undefined);
assert.ok(personal.authorization.startsWith("SAPISIDHASH "));

const delegated = worker.authHeaders({ authUser: "2", delegatedSessionId: "delegate-page-id" }, "sapisid");
assert.strictEqual(delegated["x-goog-pageid"], "delegate-page-id");

const brandHtml = '<script>ytcfg.set({"INNERTUBE_API_KEY":"k","SESSION_INDEX":"2","DELEGATED_SESSION_ID":"delegate","channelRoleType":"CREATOR_CHANNEL_ROLE_TYPE_MANAGER"})</script>';
const brand = worker.parseStudioBootstrap(brandHtml, "https://studio.youtube.com/channel/UCbrand/videos");
assert.strictEqual(brand.delegatedSessionId, "delegate");
assert.strictEqual(brand.channelRoleType, "CREATOR_CHANNEL_ROLE_TYPE_MANAGER");

assert.strictEqual(worker.isSchedule403(new Error('YouTube HTTP 403: {"error":{"status":"PERMISSION_DENIED"}}')), true);
assert.strictEqual(worker.isSchedule403(new Error("network timeout")), false);

// 403 after videoId must be manual_check path in worker (policy check via source).
const prod = require("fs").readFileSync(require("path").join(__dirname, "..", "tools", "uploader", "worker-http.js"), "utf8");
assert.match(prod, /manualCheck/);
assert.match(prod, /applyScheduleViaEditPage/);
assert.doesNotMatch(prod, /metadata_update[\s\S]{0,800}throw error[\s\S]{0,200}uploadBinary/);

console.log("http log regression checks passed");
