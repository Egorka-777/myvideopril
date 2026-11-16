"use strict";

const { execSync } = require("child_process");
const BUILD = "2026-11-11-dolphin-auth-v3";

function discoverDolphinListeningPorts() {
  if (process.platform !== "win32") return [];
  try {
    const taskOut = execSync('tasklist /FI "IMAGENAME eq Dolphin Anty.exe" /FO CSV /NH', {
      encoding: "utf8", timeout: 2500, windowsHide: true
    });
    const pids = new Set();
    for (const line of taskOut.split(/\r?\n/)) {
      const m = line.match(/^"Dolphin Anty\.exe","(\d+)"/i);
      if (m) pids.add(Number(m[1]));
    }
    if (!pids.size) return [];
    const out = execSync("netstat -ano -p tcp", { encoding: "utf8", timeout: 2500, windowsHide: true });
    const ports = [];
    for (const line of out.split(/\r?\n/)) {
      const parts = line.trim().split(/\s+/);
      if (parts.length < 5 || !/LISTENING/i.test(line) || !pids.has(Number(parts[parts.length - 1]))) continue;
      const port = Number((parts[1] || "").slice((parts[1] || "").lastIndexOf(":") + 1));
      if (Number.isInteger(port) && port > 0 && port <= 65535 && !ports.includes(port)) ports.push(port);
    }
    return ports;
  } catch (_) { return []; }
}

function candidatePorts(preferred, discovered = []) {
  const valid = p => Number.isInteger(Number(p)) && Number(p) > 0 && Number(p) <= 65535;
  const primary = valid(preferred) ? Number(preferred) : 3001;
  return [...new Set([primary, 3001, ...discovered, ...Array.from({ length: 19 }, (_, i) => i + 3002)].filter(valid).map(Number))];
}

async function loginWithToken(job, requestRaw, report = () => {}, options = {}) {
  const token = typeof job.token === "string" ? job.token.trim() : "";
  if (!token) throw new Error("Нет API-токена Dolphin. Сохраните токен в настройках.");
  const previous = job.localPort;
  const deadline = Date.now() + (options.timeoutMs || 45000);
  const checked = new Set(), replies = [];
  let confirmed = false;
  report("Проверка API Dolphin · " + BUILD);
  async function probe(port) {
    if (checked.has(port)) return false;
    const remaining = deadline - Date.now();
    if (remaining <= 0) throw new Error("Проверка API Dolphin превысила лимит времени. Проверьте API-порт в окне Health; подробности — в журнале.");
    checked.add(port);
    job.localPort = port;
    const timeoutMs = Math.min(port===Number(previous||3001)?30000:2500, remaining), startedAt = Date.now();
    let auth, failure = "нет ответа";
    try {
      auth = await requestRaw("/v1.0/auth/login-with-token", {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token })
      }, timeoutMs);
    } catch (error) {
      // Never log arbitrary messages, names, or codes: they can echo credentials.
      const code = error && (error.cause && error.cause.code || error.code);
      if (error && error.name === "AbortError") failure = "таймаут запроса";
      else if (code === "ECONNREFUSED") failure = "соединение отклонено (ECONNREFUSED)";
      else if (code === "ECONNRESET") failure = "соединение сброшено (ECONNRESET)";
      else if (code === "ETIMEDOUT" || code === "UND_ERR_CONNECT_TIMEOUT") failure = "таймаут соединения";
      else failure = "ошибка запроса";
      auth = { ok: false, status: 0 };
    }
    const status = Number(auth && auth.status) || 0;
    const ok = !!(auth && auth.ok && status >= 200 && status < 300 && auth.data && auth.data.success === true);
    const detail = status ? "HTTP " + status : failure;
    report("POST 127.0.0.1:" + port + "/v1.0/auth/login-with-token → " + detail + (ok ? "; success=true" : "; вход не подтверждён") +
      "; " + (Date.now() - startedAt) + " мс; лимит запроса " + timeoutMs + " мс");
    replies.push(port + ": " + detail);
    return ok;
  }
  try {
    // Fast path before tasklist/netstat.
    const initial = candidatePorts(previous);
    for (const port of initial.slice(0, initial[0] === 3001 ? 1 : 2)) {
      if (await probe(port)) { confirmed = true; return port; }
    }
    const discovered = options.discoverPorts ? await options.discoverPorts() : discoverDolphinListeningPorts();
    for (const port of candidatePorts(previous, discovered)) {
      if (await probe(port)) { confirmed = true; return port; }
    }
    throw new Error("Не удалось подтвердить вход в локальный API Dolphin. Ответы: " + replies.slice(0, 5).join("; ") +
      ". Проверьте API-порт в окне Health. Отказ служебного порта не доказывает, что токен неверный; все попытки записаны в журнале.");
  } finally {
    if (!confirmed) job.localPort = previous;
  }
}

module.exports = { BUILD, candidatePorts, discoverDolphinListeningPorts, loginWithToken };
