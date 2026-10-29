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
    assert.equal(url, "/v1.0/auth/login-with-token");
    assert.equal(options.method, "POST");
    assert.equal(JSON.parse(options.body).token, token);
    assert(timeout > 0 && timeout <= 2500);
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

  const slow = { token, localPort: 44444 };
  await assert.rejects(loginWithToken(slow, async () => {
    await new Promise(resolve => setTimeout(resolve, 25));
    return reply(503, {});
  }, () => {}, { timeoutMs: 5 }), /лимит времени/);
  assert.equal(slow.localPort, 44444);
  await assert.rejects(loginWithToken({ localPort: 3001 }, () => { throw Error("must not call"); }), /Нет API-токена/);
  console.log("PASS: Dolphin auth (63464/401 discovery, strict success, fast preferred port, no duplicate POST, safe logs, restored port, deadline)");
})().catch(e => { console.error(e); process.exitCode = 1; });
