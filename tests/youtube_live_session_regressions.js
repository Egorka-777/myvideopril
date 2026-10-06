"use strict";
const assert = require("assert"), fs = require("fs"), os = require("os"), path = require("path");
const { SessionEngine, OperationStore, rtmpsAddress, sanitize, validateJob } = require("../tools/uploader/worker-youtube-live-session");
const dolphin = require("../tools/uploader/dolphin-session");
(async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "vb-live-session-"));
  const id = "a".repeat(32), video = "abcdefghijk";
  const base = { command: "create", profileId: "123", localPort: 3001, operationId: id, expectedChannelId: "UCtest", stateDirectory: path.join(root, "live-session"),
    options: { title: "Test", description: "Desc", tags: ["one", "two"], thumbnail: "", privacy: "unlisted", madeForKids: false } };
  let creates = 0, starts = 0, stops = 0, state = "ready", loseCreate = false, loseStart = false, configFail = false;
  const ui = {
    verify: async () => ({ channelId: "UCtest" }),
    create: async () => { creates++; if (loseCreate) throw new Error("response lost"); return video; },
    discover: async () => video, open: async got => assert.equal(got, video),
    ingest: async () => ({ address: "rtmp://a.rtmp.youtube.com/live2", key: "fixture-key-1234" }),
    configure: async o => { assert.deepEqual(o, base.options); if (configFail) throw new Error("tags not saved"); },
    state: async () => state, canStart: async () => true,
    start: async () => { starts++; if (loseStart) throw new Error("lost go live response"); state = "live"; },
    complete: async attempted => { stops++; if (attempted && state !== "live" && state !== "complete") throw new Error("uncertain start"); state = "complete"; }
  };
  const call = (command, extra = {}) => new SessionEngine({ ...base, command, ...extra }, ui).execute();
  try {
    assert.throws(() => validateJob({ ...base, operationId: "../escape" }));
    assert.throws(() => rtmpsAddress("rtmp://evil.example/live2"));
    assert.equal(rtmpsAddress("rtmp://a.rtmp.youtube.com/live2"), "rtmps://a.rtmps.youtube.com/live2");
    assert(!sanitize(new Error("Cookie: secret123 https://example.com/path?token=private rt m")).includes("secret123"));
    const wrong = { ...ui, verify: async () => ({ channelId: "UCother" }) };
    await assert.rejects(new SessionEngine(base, wrong).execute(), /другой канал/); assert.equal(creates, 0);
    await call("create"); assert.equal(creates, 1);
    const store = new OperationStore(base); assert.equal(store.read().broadcastId, video);
    const cache = path.join(root, "dolphin-sessions");
    assert.throws(() => dolphin.assertLiveLease(base, cache), /занят прямым эфиром/);
    dolphin.assertLiveLease({ ...base, liveOperationId: id }, cache);
    await assert.rejects(call("create"), /уже запрашивалось/); assert.equal(creates, 1);
    const ingest = await call("ingest"); assert.equal(ingest.key, "fixture-key-1234");
    assert(!fs.readFileSync(store.file, "utf8").includes(ingest.key));
    await assert.rejects(call("ingest"), /ключа уже/);
    await call("status", { broadcastId: video }); assert.equal(starts, 0, "cannot start without metadata confirmation");
    configFail = true; await assert.rejects(call("configure"), /tags not saved/);
    await call("status"); assert.equal(starts, 0);
    configFail = false; await call("configure");
    assert.equal((await call("status")).broadcastState, "live"); assert.equal(starts, 1);
    await call("status"); assert.equal(starts, 1);
    await assert.rejects(call("complete", { broadcastId: "zzzzzzzzzzz" }), /не совпадает/); assert.equal(stops, 0);
    await call("complete"); assert.equal(stops, 1); await call("complete"); assert.equal(stops, 1);
    dolphin.assertLiveLease(base, cache);
    assert(store.read().completed);
    // Lost creation response: the operation stays reserved, never auto-creates a second broadcast.
    base.operationId = "b".repeat(32); state = "ready"; loseCreate = true;
    await assert.rejects(call("create"), /response lost/);
    await assert.rejects(call("create"), /уже запрашивалось/); assert.equal(creates, 2);
    await call("complete"); dolphin.assertLiveLease(base, cache);
    // Lost start response: a repeated status check does not re-click Go live; cleanup remains pending.
    base.operationId = "c".repeat(32); state = "ready"; loseCreate = false; loseStart = true;
    await call("create"); await call("configure"); await assert.rejects(call("status"), /lost go live/);
    await call("status"); assert.equal(starts, 2);
    await assert.rejects(call("complete"), /uncertain start/);
    assert.throws(() => dolphin.assertLiveLease(base, cache), /занят/);
    state = "complete"; await call("complete"); dolphin.assertLiveLease(base, cache);
    console.log("OK: YouTube live session (identity, ownership, leases, lost responses, metadata gates, no retries, cleanup)");
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
})().catch(e => { console.error(e); process.exitCode = 1; });
