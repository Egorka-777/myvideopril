"use strict";

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

// Only reuse endpoints issued for this profile, pinned to a browser instance.
// Never scan arbitrary ports or stop/restart a user's open browser.
function automationEndpoint(data) {
  if (!data || typeof data !== "object") return null;
  for (const key of ["automation", "data", "success"]) {
    if (data[key] && typeof data[key] === "object") {
      const endpoint = automationEndpoint(data[key]);
      if (endpoint) return endpoint;
    }
  }
  const port = Number(data.port || data.automationPort || data.automation_port);
  const ws = data.wsEndpoint || data.ws_endpoint;
  if (ws && /^wss?:\/\//i.test(ws)) return String(ws);
  if (ws && port > 0 && port <= 65535) return `ws://127.0.0.1:${port}/${String(ws).replace(/^\//, "")}`;
  return null;
}

function localBrowserEndpoint(value) {
  try {
    const u = new URL(value);
    return u.protocol === "ws:" && ["localhost", "127.0.0.1", "[::1]"].includes(u.hostname) &&
      !u.username && !u.password && /^\/devtools\/browser\/[^/]+$/.test(u.pathname) && !u.search && !u.hash;
  } catch (_) { return false; }
}

function cachePath(job, directory) {
  const key = crypto.createHash("sha256").update(`${job.localPort}:${job.profileId}`).digest("hex");
  return path.join(directory, key + ".json");
}
function liveLeasePath(job, directory) {
  const key = crypto.createHash("sha256").update(`${job.localPort}:${job.profileId}`).digest("hex");
  return path.join(path.dirname(directory), "live-session", "leases", key + ".json");
}
function assertLiveLease(job, directory) {
  const file = liveLeasePath(job, directory);
  if (!fs.existsSync(file)) return;
  const saved = JSON.parse(fs.readFileSync(file, "utf8"));
  if (!job.liveOperationId || saved.operationId !== job.liveOperationId)
    throw new Error("Этот Dolphin-профиль занят прямым эфиром. Сначала завершите эфир; профиль не перезапускался.");
}

async function releaseTaskPage(page) {
  if (!page || page.isClosed()) return;
  // The last tab must not close the whole Chromium window.
  if (!page.context().pages().some(other => other !== page && !other.isClosed()))
    await page.context().newPage();
  await page.close({ runBeforeUnload: false });
}

async function requestLocal(job, apiPath, options = {}, timeoutMs = 35000) {
  const controller = new AbortController(), timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(`http://127.0.0.1:${job.localPort}${apiPath}`, { ...options, signal: controller.signal });
    const text = await response.text();
    let data = {}; try { data = JSON.parse(text); } catch (_) {}
    return { ok: response.ok, status: response.status, data, text };
  } finally { clearTimeout(timer); }
}

async function endpointAlive(endpoint, fetchImpl = fetch) {
  if (!localBrowserEndpoint(endpoint)) return false;
  const u = new URL(endpoint);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 2500);
  try {
    const response = await fetchImpl(`http://${u.host}/json/version`, { signal: controller.signal });
    if (!response.ok) return false;
    const live = new URL((await response.json()).webSocketDebuggerUrl);
    // A port may have been reused by another profile. Browser UUID must match.
    return localBrowserEndpoint(live.href) && live.pathname === u.pathname && live.port === u.port;
  } catch (_) { return false; }
  finally { clearTimeout(timer); }
}

async function readCached(job, directory, fetchImpl) {
  try {
    const saved = JSON.parse(fs.readFileSync(cachePath(job, directory), "utf8"));
    if (String(saved.profileId) !== String(job.profileId) || saved.localPort !== job.localPort) return null;
    return await endpointAlive(saved.endpoint, fetchImpl) ? saved.endpoint : null;
  } catch (_) { return null; }
}

function saveEndpoint(job, directory, endpoint, report) {
  if (!localBrowserEndpoint(endpoint)) throw new Error("Dolphin вернул некорректный локальный адрес CDP.");
  try {
    fs.mkdirSync(directory, { recursive: true });
    const target = cachePath(job, directory), temp = target + "." + process.pid + ".tmp";
    fs.writeFileSync(temp, JSON.stringify({ profileId: String(job.profileId), localPort: job.localPort, endpoint }), { mode: 0o600 });
    fs.renameSync(temp, target);
  } catch (_) { report("dolphin", "Не удалось сохранить адрес подключения; повторное подключение будет зависеть от ответа Dolphin API."); }
}

async function startOrAttach(job, directory, request, report = () => {}, options = {}) {
  assertLiveLease(job, directory);
  const fetchImpl = options.fetchImpl || fetch;
  const cached = await readCached(job, directory, fetchImpl);
  if (cached) {
    if (options.onAttached) options.onAttached();
    report("dolphin", "Профиль уже открыт — подключаюсь к существующему браузеру (CDP проверен).");
    return cached;
  }
  const startPath = `/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}/start?automation=1`;
  let last = "", alreadyRunning = false;
  for (let attempt = 1; attempt <= 2; attempt++) {
    const startedAt = Date.now();
    report("dolphin", `Запрос подключения Dolphin · попытка ${attempt}/2.`);
    let response;
    try { response = await request(startPath, {}, 120000); }
    catch (e) { response = { ok: false, status: 0, data: {}, text: e.message }; }
    const data = response.data || {};
    const code = /^[A-Z_]+$/.test(String(data.code || "")) ? data.code : "";
    report("dolphin", `Ответ Dolphin · попытка ${attempt}/2 · ${Date.now()-startedAt} мс · HTTP ${response.status || "сеть"}${code ? " · " + code : ""}.`);
    // Endpoints in an ALREADY_RUNNING response may still be usable.
    const endpoint = automationEndpoint(data);
    if (endpoint) {
      if ((!response.ok || data.success === false) && options.onAttached) options.onAttached();
      saveEndpoint(job, directory, endpoint, report);
      report("dolphin", "Адрес автоматизации получен; профиль остаётся открытым.");
      return endpoint;
    }
    last = `HTTP ${response.status || "сеть"}`;
    const detail = String(data.code || "") + " " + String(data.message || data.error || response.text || "");
    alreadyRunning = data.code === "PROFILE_ALREADY_RUNNING" || /already running|уже запущен|profile is running/i.test(detail);
    if (data.code === "PROFILE_BUSY_BLOCKED") throw new Error("Dolphin: профиль занят на другом устройстве. Окно на другом устройстве не изменено.");
    if ([400, 401, 402, 403, 499].includes(response.status)) throw new Error("Dolphin отклонил подключение: " + last + ". Проверьте авторизацию, тариф и ограничения профилей.");
    // Lost start response: another worker may have obtained and cached its endpoint.
    const recovered = await readCached(job, directory, fetchImpl);
    if (recovered) return recovered;
    if (attempt < 2 && (alreadyRunning || !response.status || response.status >= 500 || response.status === 429)) {
      report("dolphin", "Адрес CDP пока недоступен; повтор подключения без закрытия профиля.");
      await (options.delay || (ms => new Promise(resolve => setTimeout(resolve, ms))))(1500);
      continue;
    }
    break;
  }
  if (alreadyRunning) throw new Error("Профиль открыт, но Dolphin не предоставил CDP. Окно сохранено. Для управления нужен профиль, запущенный с automation=1; автоматического перезапуска нет.");
  throw new Error("Dolphin не подтвердил подключение (" + last + "). Профиль не закрывался; проверьте API и прокси. Повторная публикация не выполнялась.");
}

module.exports = { automationEndpoint, localBrowserEndpoint, endpointAlive, cachePath, readCached, saveEndpoint, startOrAttach, requestLocal, releaseTaskPage, liveLeasePath, assertLiveLease };
