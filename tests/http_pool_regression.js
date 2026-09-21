"use strict";

const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");
const worker = require("../tools/uploader/worker-http.js");

const scheduled = Math.floor(Date.now() / 1000) + 7200;
const data = {
  channelId: "UC123456789",
  apiKey: "test-key",
  authUser: "2",
  delegatedSessionId: "delegate",
  channelRoleType: "CREATOR_CHANNEL_ROLE_TYPE_OWNER",
  clientVersion: "1.20260918.00.00"
};

const scheduledBody = worker.makeMetadataBody(data, "session", "video-id", "scheduled", scheduled);
assert.strictEqual(scheduledBody.scheduledPublishing.set.timeSec, String(scheduled));
assert.strictEqual(scheduledBody.privacyState.newPrivacy, "PRIVATE");

const immediateBody = worker.makeMetadataBody(data, "session", "video-id", "immediate", scheduled);
assert.strictEqual(immediateBody.privacyState.newPrivacy, "PUBLIC");
assert.strictEqual(immediateBody.scheduledPublishing, undefined);

const privateBody = worker.makeMetadataBody(data, "session", "video-id", "private", scheduled);
assert.strictEqual(privateBody.privacyState.newPrivacy, "PRIVATE");
assert.strictEqual(privateBody.scheduledPublishing, undefined);

const temp = fs.mkdtempSync(path.join(os.tmpdir(), "vb-http-pool-"));
try {
  const video = path.join(temp, "one.mp4");
  fs.writeFileSync(video, Buffer.from([1, 2, 3]));
  const thumb = path.join(temp, "one.jpg");
  fs.writeFileSync(thumb, Buffer.from([9, 9]));

  const immediateJob = worker.validateBatchJob({
    profileId: "profile",
    localPort: 3001,
    expectedIp: "1.2.3.4",
    publishMode: "immediate",
    runId: "run-test-1",
    items: [{ localJobId: "j1", video, title: "Test title", thumbnail: thumb, contentKind: "long" }]
  });
  assert.strictEqual(immediateJob.publishMode, "immediate");
  assert.strictEqual(immediateJob.items[0].thumbnail, thumb);
  assert.strictEqual(immediateJob.items[0].contentKind, "long");
  assert.strictEqual(immediateJob.items[0].scheduledUnixSeconds, 0);

  assert.throws(
    () => worker.validateJob({
      profileId: "profile", localPort: 3001, video, title: "Test title",
      expectedIp: "1.2.3.4", publishMode: "scheduled", scheduledUnixSeconds: Math.floor(Date.now() / 1000) + 60
    }),
    /15 минут/
  );
} finally {
  fs.rmSync(temp, { recursive: true, force: true });
}

const source = fs.readFileSync(path.join(__dirname, "..", "tools", "uploader", "worker-http.js"), "utf8");
assert.match(source, /finishProcess\(0\)/);
assert.match(source, /WORKER_EXIT_DELAY_MS/);
assert.match(source, /thumbnail_warning/);
assert.doesNotMatch(source, /throw new Error\("YouTube вернул videoId, но проверочная страница/);
assert.match(source, /record\("warning"/);

console.log("OK: http_pool_regression.js");
