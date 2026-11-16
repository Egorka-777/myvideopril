"use strict";
const assert = require("assert");
const { loginWithToken, candidatePorts } = require("../tools/uploader/dolphin-local-auth");

(async () => {
  const token = 'test-token-"\\\n-never-log';
  const logs = [], calls = [];
  const job = { token, localPort: 3001 };
  const reply = (status, data) => ({ status, ok: status >= 200 && status < 300, data });
  const found = await loginWithToken(job, async (url, options, timeout) => {
    calls.push(job.localPort);
    assert.equal(timeout,job.localPort===3001?30000:2500);
    assert.equal(url, "/v1.0/auth/login-with-token");
    assert.equal(options.method, "POST");
    assert.equal(JSON.parse(options.body).token, token);
    assert(timeout > 0 && timeout <= 30000);
    if (job.localPort === 3001) return reply(404, {});
    if (job.localPort === 63464) return reply(401, { success: false, message: token });
    return reply(200, { success: true });
  }, s => logs.push(s), { discoverPorts: () => [63464, 55001] });
  assert.equal(found, 55001);
  assert.equal(job.localPort, found);
  assert.deepEqual(calls, [3001, 63464, 55001]);
  assert(!logs.join("\n").includes(token));

  // A valid preferred API must not enumerate processes or other ports.
  let discovers = 0;
  const fast = { token: " token ", localPort: 55555 };
  assert.equal(await loginWithToken(fast, async (_, options) => {
    assert.equal(JSON.parse(options.body).token, "token");
    return reply(200, { success: true });
  }, () => {}, { discoverPorts: () => { discovers++; throw Error("unneeded discovery"); } }), 55555);
  assert.equal(discovers, 0);
  assert.deepEqual(candidatePorts(55555, [55555, 3001, 0, 99999, 55001]).slice(0, 3), [55555, 3001, 55001]);

  // Old code accepted HTTP 200 with missing success; every such response is rejected.
  const bad = { token, localPort: 63464 };
  const statuses = [];
  await assert.rejects(loginWithToken(bad, async () => {
    statuses.push(bad.localPort);
    if (bad.localPort === 63464) return reply(401, { message: token });
    if (bad.localPort === 3001) return reply(200, {});
    if (bad.localPort === 55002) throw Error("echoed secret: " + token);
    return reply(200, { success: "true" });
  }, s => logs.push(s), { discoverPorts: () => [55002] }), e => {
    assert(!e.message.includes(token));
    assert(!e.message.includes("Создайте токен"));
    return /Не удалось подтвердить/.test(e.message);
  });
  assert.equal(bad.localPort, 63464, "failed discovery must restore the original job port");
  assert.equal(statuses.length, new Set(statuses).size, "one POST per port");
  assert(!logs.join("\n").includes(token));

  const network = { token, localPort: 44444 };
  assert.equal(await loginWithToken(network, async () => {
    if (network.localPort === 44444) throw Error("ECONNREFUSED");
    return reply(200, { success: true });
  }), 3001);

  // Fetch wraps socket failures in cause.code. Only fixed labels may reach logs/errors.
  const failures = [
    [Object.assign(Error(token), { name: "AbortError" }), "таймаут запроса"],
    [Object.assign(Error(token), { cause: { code: "ECONNREFUSED" } }), "соединение отклонено (ECONNREFUSED)"],
    [Object.assign(Error(token), { code: "ECONNRESET" }), "соединение сброшено (ECONNRESET)"],
    [Object.assign(Error(token), { cause: { code: "ETIMEDOUT" } }), "таймаут соединения"],
    [Object.assign(Error(token), { cause: { code: "UND_ERR_CONNECT_TIMEOUT" } }), "таймаут соединения"],
    [Object.assign(Error(token), { name: token, code: token, cause: { code: token } }), "ошибка запроса"]
  ];
  for (const [error, label] of failures) {
    const failureLogs = [], failedJob = { token, localPort: 3001 };
    await assert.rejects(loginWithToken(failedJob, async () => { throw error; }, s => failureLogs.push(s),
      { discoverPorts: () => [] }), e => e.message.includes(label) && !e.message.includes(token));
    assert(failureLogs[1].includes("→ " + label + "; вход не подтверждён;"));
    assert(/; \d+ мс; лимит запроса \d+ мс$/.test(failureLogs[1]));
    assert(!failureLogs.join("\n").includes(token));
    assert.equal(failedJob.localPort, 3001);
  }

  // Each invocation has its own deadline and visited ports; another job's failure
  // must not cancel it or reuse its successful authentication implicitly.
  const concurrentCalls = [[], []];
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const first = { token, localPort: 3001 }, second = { token, localPort: 3001 };
  const firstLogin = loginWithToken(first, async (_, options, timeout) => {
    concurrentCalls[0].push(first.localPort);
    assert.equal(JSON.parse(options.body).token, token);
    assert(timeout > 0 && timeout <= 30000);
    await pending;
    return reply(200, { success: true });
  }, () => {}, { discoverPorts: () => { throw Error("unexpected discovery"); } });
  await assert.rejects(loginWithToken(second, async () => {
    concurrentCalls[1].push(second.localPort);
    throw Object.assign(Error("not logged"), { name: "AbortError" });
  }, () => {}, { discoverPorts: () => [] }), /таймаут запроса/);
  release();
  assert.equal(await firstLogin, 3001);
  assert.deepEqual(concurrentCalls[0], [3001]);
  assert.equal(concurrentCalls[1].length, 20);

  const slow = { token, localPort: 44444 };
  await assert.rejects(loginWithToken(slow, async () => {
    await new Promise(resolve => setTimeout(resolve, 25));
    return reply(503, {});
  }, () => {}, { timeoutMs: 5 }), /лимит времени/);
  assert.equal(slow.localPort, 44444);
  await assert.rejects(loginWithToken({ localPort: 3001 }, () => { throw Error("must not call"); }), /Нет API-токена/);
  console.log("PASS: Dolphin auth (discovery, strict success, safe failure labels/timing, restored port, deadline, concurrent isolation)");
})().catch(e => { console.error(e); process.exitCode = 1; });
