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
    if (process.platform === "win32") {
      const appDataDir = path.join(dir, "appdata");
      const profileJob = { ...job, profileId: "836981176" };
      const profileDir = path.join(appDataDir, "dolphin_anty", "browser_profiles", profileJob.profileId, "data_dir");
      fs.mkdirSync(profileDir, { recursive: true });
      const activePortFile = path.join(profileDir, "DevToolsActivePort");
      const liveEndpoint = `ws://127.0.0.1:${port}/devtools/browser/${browserId}`;
      const activePort = `${port}\r\n/devtools/browser/${browserId}\r\n`;
      const alreadyRunning = async apiPath => {
        assert(apiPath.endsWith(`/${profileJob.profileId}/start?automation=1`));
        attempts++;
        return { ok: false, status: 409, data: { code: "PROFILE_ALREADY_RUNNING" } };
      };
      let attachCount = 0;
      const options = { appDataDir, maxAttempts: 1, onAttached: () => { attachCount++; } };
      // A stale cache must not prevent recovery from this exact profile's file.
      session.saveEndpoint(profileJob, dir, endpoint, report);
      fs.writeFileSync(activePortFile, activePort);
      attempts = 0;
      assert.equal(await session.startOrAttach(profileJob, dir, alreadyRunning, report, options), liveEndpoint);
      assert.equal(attempts, 1);
      assert.equal(attachCount, 1);
      assert.equal(JSON.parse(fs.readFileSync(session.cachePath(profileJob, dir), "utf8")).endpoint, liveEndpoint);
      fs.unlinkSync(activePortFile);
      assert.equal(await session.startOrAttach(profileJob, dir, () => { throw new Error("must reuse recovered cache"); }, report, options), liveEndpoint);
      assert.equal(attachCount, 2);
      fs.unlinkSync(session.cachePath(profileJob, dir));
      // Missing file, malformed port/path, and a stale browser UUID cannot attach.
      for (const contents of [null, `0\n/devtools/browser/${browserId}`, `65536\n/devtools/browser/${browserId}`,
        `${port}x\n/devtools/browser/${browserId}`, `${port}\n/json/version`, `${port}\n/devtools/browser/stale-browser`]) {
        if (contents !== null) fs.writeFileSync(activePortFile, contents);
        await assert.rejects(session.startOrAttach(profileJob, dir, alreadyRunning, report, options), /Профиль открыт.*CDP/);
        assert.equal(attachCount, 2);
        assert.equal(fs.existsSync(session.cachePath(profileJob, dir)), false);
      }
      fs.writeFileSync(activePortFile, activePort);
      // Another profile's file is not a fallback, nor is a traversing profile ID.
      for (const profileId of ["222", "../836981176", "836981176\n"]) {
        await assert.rejects(session.startOrAttach({ ...profileJob, profileId }, dir,
          async () => ({ ok: false, status: 409, data: { code: "PROFILE_ALREADY_RUNNING" } }), report, options), /Профиль открыт.*CDP/);
      }
      // A valid file is consulted only for ALREADY_RUNNING, not other API failures.
      await assert.rejects(session.startOrAttach(profileJob, dir,
        async () => ({ ok: false, status: 500, data: {} }), report, options), /не подтвердил подключение/);
      assert.equal(attachCount, 2);
      assert.equal(fs.existsSync(session.cachePath(profileJob, dir)), false);
      // Keep the caller's existing three-attempt limit; add no recovery retries.
      attempts = 0;
      fs.unlinkSync(activePortFile);
      await assert.rejects(session.startOrAttach(profileJob, dir, alreadyRunning, report,
        { ...options, maxAttempts: 3, delay: async () => {} }), /Профиль открыт.*CDP/);
      assert.equal(attempts, 3);
    }
    console.log("OK: Dolphin session (persisted reuse, profile-file recovery, UUID validation, bounded retry, auth, non-destructive failure)");
  } finally { await new Promise(resolve=>server.close(resolve)); fs.rmSync(dir,{recursive:true,force:true}); }
})().catch(e=>{ console.error(e); process.exitCode=1; });
