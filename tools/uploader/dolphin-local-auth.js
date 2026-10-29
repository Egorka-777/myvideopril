"use strict";

const { execSync } = require("child_process");

function discoverDolphinListeningPorts() {
  const ports = [];
  const seen = new Set();
  const add = (p) => {
    const n = Number(p);
    if (!Number.isInteger(n) || n < 1 || n > 65535 || seen.has(n)) return;
    seen.add(n);
    ports.push(n);
  };
  try {
    let taskOut = "";
    try {
      taskOut = execSync('tasklist /FI "IMAGENAME eq Dolphin Anty.exe" /FO CSV /NH', {
        encoding: "utf8",
        timeout: 5000,
        windowsHide: true
      });
    } catch (_) {
      return ports;
    }
    const pids = new Set();
    for (const line of taskOut.split(/\r?\n/)) {
      const m = line.match(/^"Dolphin Anty\.exe","(\d+)"/i);
      if (m) pids.add(Number(m[1]));
    }
    if (!pids.size) return ports;
    const netOut = execSync("netstat -ano", { encoding: "utf8", timeout: 8000, windowsHide: true });
    for (const line of netOut.split(/\r?\n/)) {
      if (!/LISTENING/i.test(line)) continue;
      const parts = line.trim().split(/\s+/);
      if (parts.length < 5) continue;
      const pid = Number(parts[parts.length - 1]);
      if (!pids.has(pid)) continue;
      const addr = parts[1] || "";
      const colon = addr.lastIndexOf(":");
      if (colon < 0) continue;
      const portText = addr.slice(colon + 1);
      if (portText.startsWith("[")) continue;
      add(portText);
    }
  } catch (_) {}
  return ports;
}

function candidatePorts(preferred) {
  const out = [];
  const seen = new Set();
  const add = (p) => {
    const n = Number(p);
    if (!Number.isInteger(n) || n < 1 || n > 65535 || seen.has(n)) return;
    seen.add(n);
    out.push(n);
  };
  for (const p of discoverDolphinListeningPorts()) add(p);
  add(preferred);
  for (let p = 3001; p <= 3020; p++) add(p);
  return out;
}

async function loginWithToken(job, requestRaw) {
  const token = job.token;
  if (!token) throw new Error("Нет API-токена Dolphin. Откройте «1. Dolphin…» и вставьте токен.");
  const options = {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token })
  };
  let lastStatus = 0;
  let wrongServiceOnPreferred = false;
  const preferred = job.localPort;
  for (const port of candidatePorts(job.localPort)) {
    job.localPort = port;
    const auth = await requestRaw("/v1.0/auth/login-with-token", options, 4000);
    lastStatus = auth.status || 0;
    if (auth.ok && auth.data && auth.data.success !== false) return port;
    if (auth.status === 401 || auth.status === 403 || (auth.data && auth.data.success === false)) {
      throw new Error("API-токен Dolphin не принят локальным API (порт " + port + "). Создайте токен в кабинете dolphin-anty.com → API и оставьте Dolphin Anty запущенным под тем же аккаунтом.");
    }
    if (port === preferred && auth.status === 404) wrongServiceOnPreferred = true;
  }
  if (wrongServiceOnPreferred) {
    throw new Error("Порт " + preferred + " занят не Dolphin (HTTP 404). Закройте другой сервис на этом порту или укажите порт из окна Health в Dolphin Anty.");
  }
  throw new Error("Dolphin: не подтверждена авторизация локального API (HTTP " + lastStatus + "). Запустите Dolphin Anty и укажите в «1. Dolphin…» порт из окна Health.");
}

module.exports = { candidatePorts, discoverDolphinListeningPorts, loginWithToken };
