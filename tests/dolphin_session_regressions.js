"use strict";
const assert = require("assert");
const fs = require("fs"), os = require("os"), path = require("path"), http = require("http");
const session = require("../tools/uploader/dolphin-session");

(async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "videobatch-session-"));
  let browserId = "browser-a", starts = 0;
  const server = http.createServer((req, res) => {
    res.setHeader("Content-Type", "application/json");
    res.end(JSON.stringify({ webSocketDebuggerUrl: `ws://127.0.0.1:${server.address().port}/devtools/browser/${browserId}` }));
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port, endpoint = `ws://127.0.0.1:${port}/devtools/browser/browser-a`;
  const job = { localPort: 3001, profileId: "111", token: "secret-never-save" };
  const report = () => {};
  const success = async apiPath => {
    assert(apiPath.endsWith("/111/start?automation=1"));
    assert(!apiPath.includes("/stop")); starts++;
    return { ok: true, status: 200, data: { success: true, automation: { port, wsEndpoint: endpoint } } };
  };
  try {
    assert.equal(session.automationEndpoint({ automation: { port, wsEndpoint: "/devtools/browser/browser-a" } }), endpoint);
    assert.equal(session.automationEndpoint({ proxy: { port: 8080 } }), null);
    assert.equal(session.localBrowserEndpoint("ws://other-host:123/devtools/browser/a"), false);
    assert.equal(await session.startOrAttach(job, dir, success, report), endpoint);
    assert.equal(starts, 1);
    let attached = false;
    assert.equal(await session.startOrAttach(job, dir, () => { throw new Error("must reuse without start"); }, report,
      { onAttached: () => { attached = true; } }), endpoint);
    assert.equal(attached, true);
    assert(!fs.readFileSync(session.cachePath(job,dir),"utf8").includes(job.token));
    // Same port, different browser UUID: never attach the wrong account.
    browserId = "other-profile";
    assert.equal(await session.readCached(job, dir), null);
    assert.equal(await session.readCached({ ...job, profileId: "222" }, dir), null);
    let attempts = 0;
    await assert.rejects(session.startOrAttach(job, dir, async () => {
      attempts++; return { ok:false, status:409, data:{code:"PROFILE_ALREADY_RUNNING"} };
    }, report, { delay: async () => {} }), /Профиль открыт.*CDP/);
    assert.equal(attempts,2);
    attempts=0;
    await assert.rejects(session.startOrAttach(job, dir, async () => {
      attempts++; return { ok:false, status:401, data:{} };
    }, report), /HTTP 401/);
    assert.equal(attempts,1);
    await assert.rejects(session.startOrAttach(job, dir, async () => ({ok:false,status:409,data:{code:"PROFILE_BUSY_BLOCKED"}}), report), /другом устройстве/);
    let retry=0;
    const recovered = await session.startOrAttach(job, dir, async () => {
      if (++retry===1) throw new Error("lost response");
      return {ok:false,status:409,data:{code:"PROFILE_ALREADY_RUNNING",automation:{port,wsEndpoint:endpoint}}};
    }, report, { delay:async()=>{} });
    assert.equal(recovered,endpoint);
    assert.equal(retry,2);
    console.log("OK: Dolphin session (persisted reuse, stale/wrong profile, bounded retry, auth, non-destructive failure)");
  } finally { await new Promise(resolve=>server.close(resolve)); fs.rmSync(dir,{recursive:true,force:true}); }
})().catch(e=>{ console.error(e); process.exitCode=1; });
