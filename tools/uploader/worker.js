"use strict";

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { chromium } = require("playwright-core");

/** Меняется при каждом деплое — сверяйте в журнале загрузки. */
const WORKER_BUILD = "2026-09-17-watch-recovery-v1";

const jobPath = process.argv[2];
let job = null;
let browser = null;
let profileStarted = false;
let finished = false;
let youtubeOpened = false;

function localStatePath(name) {
  return path.join(path.dirname(path.dirname(jobPath)), name);
}

function randomDelaySeconds() { return crypto.randomInt(1, 31); }

/** Playwright CDP к Dolphin часто не «collocated» → setInputFiles режет файлы >50 МБ. Локальный путь через DOM.setFileInputFiles. */
async function setInputFilesViaCdp(page, locator, paths) {
  const handle = await locator.elementHandle({ timeout: 90000 });
  if (!handle) throw new Error("Не найден input для выбора файла.");
  const client = await page.context().newCDPSession(page);
  try {
    await handle.evaluate((el) => { el.setAttribute("data-vb-file", "1"); });
    const doc = await client.send("DOM.getDocument", { depth: 0 });
    const found = await client.send("DOM.querySelector", {
      nodeId: doc.root.nodeId,
      selector: 'input[data-vb-file="1"]'
    });
    if (!found || !found.nodeId) throw new Error("CDP: не удалось найти input файла.");
    await client.send("DOM.setFileInputFiles", { files: paths, nodeId: found.nodeId });
    await handle.evaluate((el) => { el.removeAttribute("data-vb-file"); }).catch(() => {});
  } finally {
    await client.detach().catch(() => {});
  }
}

async function setInputFilesRobust(page, locator, filePath, label) {
  const paths = (Array.isArray(filePath) ? filePath : [filePath]).map(p => path.resolve(String(p || "")));
  if (!paths.length || paths.some(p => !p || !fs.existsSync(p))) {
    throw new Error("Файл не найден: " + paths.filter(Boolean).join(", "));
  }
  const totalSize = paths.reduce((sum, p) => sum + fs.statSync(p).size, 0);
  if (label) send(label, `Передаю ${paths.length} файл(ов), ${Math.round(totalSize / 1024 / 1024)} МБ…`, { percent: 28 });
  try {
    await setInputFilesViaCdp(page, locator, paths);
    return;
  } catch (cdpErr) {
    send(label || "youtube", "CDP: повтор через Playwright…", { percent: 28 });
  }
  try {
    await locator.setInputFiles(paths.length === 1 ? paths[0] : paths, { timeout: 120000 });
    return;
  } catch (e) {
    const msg = String((e && e.message) || e);
    if (/50\s*Mb|50Mb|co-located|larger than|Timeout/i.test(msg)) {
      if (label) send(label, `Playwright не принял файлы — CDP повтор…`, { percent: 28 });
      await setInputFilesViaCdp(page, locator, paths);
      return;
    }
    throw e;
  }
}

function assertPageOpen(page) {
  if (!page || page.isClosed()) throw new Error("Браузер закрыт — профиль Dolphin остановлен или окно закрыто вручную.");
}

function attachPageGuards(page) {
  page.on("dialog", async (dialog) => {
    const msg = (dialog.message() || "").slice(0, 120);
    send("youtube", "Диалог браузера: " + msg, { percent: 50 });
    await dialog.accept().catch(() => dialog.dismiss().catch(() => {}));
  });
}


async function waitBetweenProfiles() {
  let lastCompleted = 0;
  try { lastCompleted = Number(JSON.parse(fs.readFileSync(localStatePath("youtube-queue-delay.json"), "utf8")).lastCompleted || 0); } catch (_) {}
  const ageMs = Date.now() - lastCompleted;
  if (!lastCompleted || ageMs < 0 || ageMs > 60000) return;
  const delay = randomDelaySeconds();
  const remainingMs = Math.max(0, delay * 1000 - ageMs);
  if (remainingMs > 0) {
    send("queue", `Пауза перед следующим профилем: ${Math.ceil(remainingMs/1000)} сек.`, { percent: 1 });
    await new Promise(resolve => setTimeout(resolve, remainingMs));
  }
}

async function pauseAfterWatch() {
  const delay = randomDelaySeconds();
  send("queue", `Пауза перед следующим профилем: ${delay} сек.`, { percent: 99 });
  await new Promise(resolve => setTimeout(resolve, delay * 1000));
}

function markProfileCompleted() {
  const target = localStatePath("youtube-queue-delay.json");
  const temp = target + ".tmp";
  fs.writeFileSync(temp, JSON.stringify({ lastCompleted: Date.now() }), "utf8");
  fs.renameSync(temp, target);
}

function send(stage, text, extra = {}) {
  process.stdout.write(JSON.stringify(Object.assign({ stage, text }, extra)) + "\n");
}

function fail(message, extra = {}) {
  const raw = message instanceof Error ? message.message : String(message);
  const error = raw.replace(/\x1b\[[0-9;]*m/g, "");
  send("error", error, Object.assign({ success: false, error }, extra));
  process.exitCode = 1;
}

async function api(path, options = {}) {
  const url = `http://127.0.0.1:${job.localPort}${path}`;
  let lastError = null;
  for (let attempt = 1; attempt <= 4; attempt++) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 35000);
    try {
      const response = await fetch(url, Object.assign({}, options, { signal: controller.signal }));
      const text = await response.text();
      let data;
      try { data = text ? JSON.parse(text) : {}; } catch { data = { raw: text }; }
      if (!response.ok) throw new Error(`Dolphin API: HTTP ${response.status}. ${data.message || data.error || text || "Нет ответа"}`);
      if (data && data.success === false) throw new Error(`Dolphin API: ${data.message || data.error || "операция отклонена"}`);
      return data;
    } catch (e) {
      lastError = e;
      const msg = e && e.name === "AbortError" ? "таймаут" : ((e && e.message) || String(e));
      const retryable = /fetch failed|ECONNREFUSED|ECONNRESET|socket|network|таймаут|aborted|AbortError/i.test(msg + " " + (e && e.name));
      if (retryable && attempt < 4) {
        send("dolphin", `Dolphin не ответил (${msg}). Повтор ${attempt}/3…`, { percent: 2 });
        await new Promise(r => setTimeout(r, 700 * attempt));
        continue;
      }
      if (/fetch failed|ECONNREFUSED/i.test(msg)) {
        throw new Error("Не удалось связаться с Dolphin Anty (порт " + job.localPort + "). Запустите Dolphin и проверьте порт в «1. Dolphin…».");
      }
      throw (e instanceof Error ? e : new Error(msg));
    } finally {
      clearTimeout(timer);
    }
  }
  throw lastError || new Error("Dolphin API недоступен.");
}

async function stopProfile() {
  if (!profileStarted || !job) return;
  try { await api(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}/stop`); } catch (_) {}
  profileStarted = false;
}

async function profileRunning() {
  try {
    const info = await api(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}`);
    const st = String((info && (info.status || info.data && info.data.status)) || "").toLowerCase();
    return st.includes("run") || st === "active" || st === "started";
  } catch (_) { return false; }
}

async function restartAlreadyRunningProfile() {
  const id = encodeURIComponent(job.profileId);
  send("dolphin", "Профиль открыт без порта автоматизации — безопасно перезапускаю…", { percent: 9 });
  await api(`/v1.0/browser_profiles/${id}/stop`);
  profileStarted = false;

  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    if (!(await profileRunning())) break;
    await new Promise(r => setTimeout(r, 750));
  }
  if (await profileRunning()) {
    throw new Error("Dolphin не остановил уже открытый профиль за 20 сек.");
  }

  const started = await api(`/v1.0/browser_profiles/${id}/start?automation=1`);
  const endpoint = automationEndpoint(started);
  if (!endpoint) throw new Error("Dolphin перезапустил профиль, но не вернул порт автоматизации.");
  profileStarted = true;
  send("dolphin", "Профиль перезапущен в режиме автоматизации.", { percent: 11 });
  return endpoint;
}

async function startOrConnectProfile() {
  send("dolphin", "Подключаюсь к Dolphin…", { percent: 3 });
  await api("/v1.0/auth/login-with-token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token: job.token })
  });

  let lastErr = null;
  for (let attempt = 1; attempt <= 3; attempt++) {
    try {
      send("dolphin", attempt === 1 ? "Запуск профиля…" : `Профиль: повтор ${attempt}/3…`, { percent: 5 + attempt });
      const started = await api(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}/start?automation=1`);
      profileStarted = true;
      const endpoint = automationEndpoint(started);
      if (!endpoint) throw new Error("Dolphin не вернул порт автоматизации.");
      return endpoint;
    } catch (e) {
      lastErr = e;
      const msg = String((e && e.message) || e);
      const already = /already running|уже запущен|HTTP 500|profile is running|запущен/i.test(msg);
      if (already) {
        send("dolphin", `Профиль уже открыт — подключаюсь (${attempt}/3)…`, { percent: 8 });
        const info = await api(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}`).catch(() => null);
        const endpoint = automationEndpoint(info);
        if (endpoint) {
          profileStarted = true;
          return endpoint;
        }
        if (await profileRunning()) {
          try {
            return await restartAlreadyRunningProfile();
          } catch (restartError) {
            lastErr = restartError;
          }
        }
      }
      if (attempt < 3) await new Promise(r => setTimeout(r, 1200 * attempt));
    }
  }
  throw lastErr || new Error("Не удалось запустить или подключиться к профилю Dolphin.");
}

async function connectBrowser(endpoint) {
  send("dolphin", "Подключение к браузеру…", { percent: 12 });
  for (let attempt = 1; attempt <= 3; attempt++) {
    try {
      const b = await chromium.connectOverCDP(endpoint, { timeout: 60000 });
      const ctx = b.contexts()[0];
      if (!ctx) throw new Error("Нет контекста браузера.");
      send("dolphin", "Браузер подключён.", { percent: 15 });
      return b;
    } catch (e) {
      if (attempt >= 3) throw e;
      send("dolphin", `Браузер: повтор ${attempt}/3…`, { percent: 12 });
      await new Promise(r => setTimeout(r, 1500 * attempt));
    }
  }
  throw new Error("Не удалось подключиться к браузеру Dolphin.");
}

async function verifyYouTubeStudioReady(page) {
  send("youtube", "Проверка YouTube Studio…", { percent: 20 });
  let lastErr = "Не удалось проверить YouTube Studio.";
  for (let attempt = 1; attempt <= 3; attempt++) {
    await gotoStable(page, "https://studio.youtube.com", { waitUntil: "domcontentloaded", timeout: 90000 }).catch(() => {});
    await page.waitForTimeout(attempt === 1 ? 2000 : 3500);
    const blocker = await page.evaluate(() => {
      const url = location.href || "";
      const body = (document.body && document.body.innerText) || "";
      if (/accounts\.google\.com|ServiceLogin|signin|oauth/i.test(url)) return "Требуется вход в Google — войдите в профиле вручную.";
      if (/challenge|captcha|unusual traffic|robot/i.test(body + url)) return "CAPTCHA или проверка безопасности Google.";
      if (/upload limit|daily upload|ограничение загрузки|лимит загрузки/i.test(body)) return "YouTube ограничил загрузку на этом аккаунте.";
      if (/confirm your account|подтвердите аккаунт|verify your/i.test(body)) return "Нужно подтверждение аккаунта Google/YouTube.";
      if (!/studio\.youtube\.com/i.test(url)) return "YouTube Studio не открылся — проверьте прокси и вход.";
      return "";
    }).catch((e) => {
      lastErr = "Не удалось проверить YouTube Studio" + (attempt < 3 ? " (повтор…)" : "") + ".";
      return lastErr;
    });
    if (!blocker) {
      send("youtube", "YouTube Studio открыт, вход выполнен.", { percent: 22 });
      return;
    }
    lastErr = blocker;
    if (/вход|CAPTCHA|ограничил|подтверждение/i.test(blocker)) throw new Error(blocker);
    if (attempt < 3) send("youtube", `Studio: повтор ${attempt}/3…`, { percent: 21 });
  }
  throw new Error(lastErr);
}

async function waitForBulkUploadComplete(page, expectedCount, fileLabels) {
  const stallMs = 5 * 60 * 1000;
  const end = Date.now() + 90 * 60 * 1000;
  let lastMove = Date.now();
  let lastSig = "";

  while (Date.now() < end) {
    assertPageOpen(page);
    const stat = await page.evaluate(() => {
      const root = document.querySelector("ytcp-uploads-dialog, ytcp-multi-progress-panel, ytcp-multi-progress-monitor") || document.body;
      const errNodes = Array.from(root.querySelectorAll("[class*='error'], .error-message, ytcp-error-tip, [role='alert']"));
      const errText = errNodes.map(n => (n.innerText || "").trim()).filter(t => t && t.length < 200).join(" | ");
      const labels = Array.from(root.querySelectorAll("#progress-label, [class*='progress-label'], ytcp-video-upload-progress, ytcp-uploads-file-picker-item, ytcp-ve"))
        .map(n => (n.innerText || n.textContent || "").replace(/\s+/g, " ").trim())
        .filter(t => t && t.length < 120);
      let done = 0;
      let maxPct = 0;
      let stuckLabel = "";
      for (const t of labels) {
        if (/100\s*%|complete|заверш|upload complete|processing complete|checks complete|готово/i.test(t)) done++;
        const m = t.match(/(\d{1,3})\s*%/);
        if (m) {
          const p = Number(m[1]);
          if (p > maxPct) maxPct = p;
          if (p >= 100) done++;
        }
        if (/uploading|загрузк|processing|обработ/i.test(t) && !stuckLabel) stuckLabel = t.slice(0, 60);
      }
      const items = Math.max(
        document.querySelectorAll("ytcp-uploads-file-picker-item").length,
        document.querySelectorAll("ytcp-multi-progress-monitor ytcp-ve").length,
        labels.filter(t => /%|upload|загруз/i.test(t)).length
      );
      const allDone = items >= 1 && (done >= items || (maxPct >= 99 && done >= Math.max(1, items - 1)));
      return { items, done, maxPct, allDone, errText, stuckLabel };
    }).catch(() => ({ items: 0, done: 0, maxPct: 0, allDone: false, errText: "", stuckLabel: "" }));

    if (stat.errText && /error|ошиб|failed|fail|denied|blocked/i.test(stat.errText)) {
      throw new Error("YouTube: " + stat.errText.slice(0, 180));
    }
    if (stat.allDone || (stat.done >= expectedCount && stat.maxPct >= 95)) {
      send("youtube", `Загрузка ${expectedCount} из ${expectedCount} завершена.`, { percent: 88 });
      return stat;
    }

    const sig = stat.done + "/" + stat.maxPct + "/" + stat.items;
    if (sig !== lastSig) { lastSig = sig; lastMove = Date.now(); }
    else if (Date.now() - lastMove > stallMs) {
      const name = (fileLabels && fileLabels[stat.done]) || stat.stuckLabel || "?";
      throw new Error(`Загрузка зависла на «${String(name).slice(0, 50)}» — нет прогресса 5 мин.`);
    }

    send("youtube", `Загрузка ${Math.min(stat.done, expectedCount)} из ${expectedCount}…`, {
      percent: 35 + Math.min(50, Math.round(stat.maxPct * 0.5))
    });
    await page.waitForTimeout(2000);
  }
  throw new Error(`Не дождался завершения загрузки ${expectedCount} файлов.`);
}

async function verifyDraftsSaved(page, expectedCount) {
  send("youtube", "Проверка черновиков…", { percent: 92 });
  await page.waitForTimeout(3000);
  const n = await countUploadQueueItems(page);
  const err = await page.evaluate(() => {
    const root = document.querySelector("ytcp-uploads-dialog, ytcp-multi-progress-panel") || document.body;
    const reds = Array.from(root.querySelectorAll("[class*='error'], ytcp-error-tip, [role='alert']"));
    return reds.map(x => (x.innerText || "").trim()).filter(t => t && /error|ошиб|fail|failed/i.test(t)).join(" | ");
  }).catch(() => "");
  if (err) throw new Error("YouTube сообщил об ошибке: " + err.slice(0, 160));
  if (n > 0 && n < expectedCount) throw new Error(`Черновики: принято ${n} из ${expectedCount}. Профиль оставлен открытым.`);
  send("youtube", "Черновики сохранены.", { percent: 94 });
}

async function applyDraftThumbnails(page, toUpload) {
  const need = toUpload.filter(it => it.thumbnail && fs.existsSync(String(it.thumbnail || "")));
  if (!need.length) return;
  send("youtube", `Превью: ${need.length} файл(ов)…`, { percent: 90 });
  await waitForUploadDialog(page).catch(() => {});
  for (let i = 0; i < toUpload.length; i++) {
    const item = toUpload[i];
    if (!item.thumbnail || !fs.existsSync(String(item.thumbnail))) continue;
    const title = cleanUploadTitle(item.title);
    try {
      if (toUpload.length > 1) await selectUploadQueueItem(page, i);
      await page.waitForTimeout(1200);
      await setThumbnail(page, item.thumbnail);
      send("youtube", `Превью установлено: «${title.slice(0, 40)}»`, { percent: 91 });
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      send("youtube", `Видео загружено, превью не установлено: «${title.slice(0, 40)}» (${msg.slice(0, 60)})`, { percent: 91 });
    }
  }
}

async function closeProfileSafely(page) {
  send("dolphin", "Жду автосохранение…", { percent: 96 });
  await page.waitForTimeout(4000).catch(() => {});
  if (browser) await browser.close().catch(() => {});
  browser = null;
  if (profileStarted) {
    for (let i = 0; i < 3; i++) {
      try { await api(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}/stop`); } catch (_) {}
      await new Promise(r => setTimeout(r, 1500));
      if (!(await profileRunning())) break;
    }
    profileStarted = false;
  }
  send("dolphin", "Профиль закрыт.", { percent: 98 });
}

function automationEndpoint(data) {
  const root = data && (data.automation || (data.success && typeof data.success === "object" ? data.success.automation || data.success : null) || data.data || data);
  if (!root) return null;
  const port = root.port || root.automationPort || root.automation_port;
  const ws = root.wsEndpoint || root.ws_endpoint;
  if (ws && /^wss?:\/\//i.test(ws)) return ws;
  if (ws && port) return `ws://127.0.0.1:${port}${ws.startsWith("/") ? "" : "/"}${ws}`;
  return port ? `http://127.0.0.1:${port}` : null;
}

async function fetchText(url, timeoutMs = 20000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(url, { signal: controller.signal, cache: "no-store", redirect: "follow" });
    const text = await response.text();
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return text;
  } finally { clearTimeout(timer); }
}

function parseIpPayload(text, kind) {
  const raw = String(text || "").trim();
  if (!raw) return "";
  try {
    if (kind === "json-ip") return String(JSON.parse(raw).ip || "").trim();
    if (kind === "json-query") return String(JSON.parse(raw).query || "").trim();
    if (kind === "cf-trace") {
      const m = raw.match(/(?:^|\n)ip=([^\s\r\n]+)/i);
      return m ? m[1].trim() : "";
    }
  } catch (_) {}
  return raw.split(/\s+/)[0].trim();
}

function validIp(value) {
  if (!value || !/^[0-9a-f:.]+$/i.test(value)) return false;
  return value.includes(".") || value.includes(":");
}

function normalizeIp(value) {
  const v = String(value || "").trim().toLowerCase();
  return validIp(v) ? v : "";
}

function ipv4Parts(ip) {
  const n = normalizeIp(ip);
  if (!n || n.includes(":")) return null;
  const p = n.split(".");
  if (p.length !== 4) return null;
  return p.map(Number);
}

/** Мягкое совпадение: тот же /16 (частая ротация прокси в одном пуле). */
function sameIpv4Pool(a, b) {
  const pa = ipv4Parts(a);
  const pb = ipv4Parts(b);
  if (!pa || !pb) return false;
  return pa[0] === pb[0] && pa[1] === pb[1];
}

async function lookupCountryCode(page, ip) {
  const target = normalizeIp(ip);
  if (!target) return "";
  const urls = [
    [`http://ip-api.com/json/${encodeURIComponent(target)}?fields=status,countryCode`, "json-cc"],
    [`https://ipapi.co/${encodeURIComponent(target)}/country/`, "text"],
    [`https://ipinfo.io/${encodeURIComponent(target)}/country`, "text"]
  ];
  for (const [url, kind] of urls) {
    try {
      const text = await page.evaluate(async ({ url, timeoutMs }) => {
        const controller = new AbortController();
        const t = setTimeout(() => controller.abort(), timeoutMs);
        try {
          const response = await fetch(url, { signal: controller.signal, cache: "no-store" });
          return await response.text();
        } finally { clearTimeout(t); }
      }, { url, timeoutMs: 8000 });
      if (kind === "json-cc") {
        const j = JSON.parse(text);
        if (j && j.status === "success" && j.countryCode) return String(j.countryCode).toUpperCase();
      } else {
        const cc = String(text || "").trim().toUpperCase().replace(/[^A-Z]/g, "");
        if (cc.length === 2) return cc;
      }
    } catch (_) {}
  }
  return "";
}

/**
 * Жёстко только если страна явно другая.
 * Ротация IP в том же пуле / той же стране — разрешаем и обновляем сохранённый IP.
 */
async function assertProxyAcceptable(page, expectedRaw, actualRaw) {
  const expected = normalizeIp(expectedRaw);
  const actual = normalizeIp(actualRaw);
  if (!actual) throw new Error("Не удалось определить IP профиля.");
  if (!expected) return { ip: actual, refreshed: true };
  if (expected === actual) return { ip: actual, refreshed: false };

  if (sameIpv4Pool(expected, actual)) {
    send("ip", `IP сменился в том же диапазоне прокси (${expected} → ${actual}). Это нормально — продолжаю.`, { ip: actual, percent: 21 });
    return { ip: actual, refreshed: true };
  }

  const [cExp, cAct] = await Promise.all([
    lookupCountryCode(page, expected),
    lookupCountryCode(page, actual)
  ]);
  if (cExp && cAct && cExp === cAct) {
    send("ip", `IP сменился, страна та же (${cAct}): ${expected} → ${actual}. Продолжаю.`, { ip: actual, percent: 21 });
    return { ip: actual, refreshed: true };
  }
  if (cExp && cAct && cExp !== cAct) {
    throw new Error(`Прокси другой страны: было ${cExp} (${expected}), стало ${cAct} (${actual}). YouTube не открыт.`);
  }

  // Страну не узнали и /16 другой — слишком далеко для автозамены
  throw new Error(`IP сильно изменился: ожидался ${expected}, получен ${actual}. Если прокси тот же — нажмите «Проверить профили» и повторите.`);
}

const IP_SERVICES = [
  ["https://api.ipify.org?format=json", "json-ip"],
  ["https://api64.ipify.org?format=json", "json-ip"],
  ["https://icanhazip.com", "text"],
  ["https://checkip.amazonaws.com", "text"],
  ["https://ipinfo.io/ip", "text"],
  ["https://api.ip.sb/ip", "text"],
  ["https://ipapi.co/ip", "text"],
  ["https://www.cloudflare.com/cdn-cgi/trace", "cf-trace"],
  ["https://ifconfig.me/ip", "text"]
];

async function publicIp(page) {
  // Сначала открываем YouTube через прокси профиля — это главная проверка доступности.
  // IP берём через fetch в контексте страницы (без обязательного ifconfig.me).
  send("ip", "Проверяю доступ к YouTube через профиль…", { percent: 12 });
  await gotoStable(page, "https://www.youtube.com/", { waitUntil: "domcontentloaded", timeout: 90000 });
  let last = "";
  for (const [url, kind] of IP_SERVICES) {
    try {
      const text = await page.evaluate(async ({ url, timeoutMs }) => {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), timeoutMs);
        try {
          const response = await fetch(url, { signal: controller.signal, cache: "no-store", redirect: "follow" });
          const body = await response.text();
          if (!response.ok) throw new Error("HTTP " + response.status);
          return body;
        } finally { clearTimeout(timer); }
      }, { url, timeoutMs: 20000 });
      const ip = normalizeIp(parseIpPayload(text, kind));
      if (ip) return ip;
    } catch (e) { last = e.message; }
  }
  // Запасной путь: короткие переходы на простые IP-страницы (не только ifconfig.me).
  const navFallbacks = [
    ["https://api.ipify.org", "text"],
    ["https://icanhazip.com", "text"],
    ["https://checkip.amazonaws.com", "text"]
  ];
  for (const [url, kind] of navFallbacks) {
    try {
      await gotoStable(page, url, { waitUntil: "domcontentloaded", timeout: 25000 });
      const body = (await page.locator("body").innerText({ timeout: 8000 })).trim();
      const ip = normalizeIp(parseIpPayload(body, kind));
      if (ip) return ip;
    } catch (e) { last = e.message; }
  }
  throw new Error("Не удалось узнать внешний IP профиля (прокси/сервисы IP недоступны). YouTube при этом уже открыт в профиле. " + last);
}

async function computerIp() {
  let last = "";
  for (const [url, kind] of IP_SERVICES) {
    try {
      const text = await fetchText(url, 20000);
      const ip = normalizeIp(parseIpPayload(text, kind));
      if (ip) return ip;
    } catch (e) { last = e.message; }
  }
  throw new Error("Не удалось проверить прямой IP компьютера. Для безопасности YouTube-загрузка не стартует. " + last);
}

async function visible(locator, timeout = 1500) {
  try { await locator.first().waitFor({ state: "visible", timeout }); return true; } catch { return false; }
}

/** Повтор goto при сбросах прокси (ERR_CONNECTION_RESET и т.п.). */
async function gotoStable(page, url, options = {}) {
  const timeout = options.timeout || 90000;
  const waitUntil = options.waitUntil || "domcontentloaded";
  let last = null;
  for (let attempt = 1; attempt <= 5; attempt++) {
    try {
      await page.goto(url, { waitUntil, timeout });
      return;
    } catch (e) {
      last = e;
      const msg = String((e && e.message) || e);
      const retryable = /ERR_CONNECTION|ERR_TIMED_OUT|ERR_NETWORK|ERR_EMPTY|ERR_SSL|Timeout|timeout|net::|NS_ERROR|Navigation/i.test(msg);
      if (!retryable || attempt === 5) throw e;
      send("youtube", `Сеть/прокси сбросили страницу, повтор ${attempt}/4…`, { percent: Math.min(28, 10 + attempt * 3) });
      await new Promise(r => setTimeout(r, 1200 * attempt));
    }
  }
  throw last || new Error("Не удалось открыть " + url);
}

async function waitEnabled(locator, timeout, hint) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (await locator.count() && await locator.first().isVisible().catch(() => false)) {
      const disabled = await locator.first().getAttribute("aria-disabled").catch(() => "true");
      const nativeDisabled = await locator.first().isDisabled().catch(() => true);
      if (disabled !== "true" && !nativeDisabled) return locator.first();
    }
    await new Promise(r => setTimeout(r, 1000));
  }
  throw new Error(hint);
}

async function waitForUploadDialog(page, timeoutMs = 120000) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    const dlg = uploadDialog(page);
    if (await dlg.isVisible().catch(() => false)) return true;
    if (await page.locator("#title-textarea, ytcp-social-suggestions-textbox#title-textarea, input[type=file]").count()) return true;
    await page.waitForTimeout(500);
  }
  return false;
}

async function fillTitle(page, title) {
  const end = Date.now() + 90000;
  while (Date.now() < end) {
    const candidates = [
      page.locator("#title-textarea #textbox"),
      page.locator("ytcp-social-suggestions-textbox#title-textarea [contenteditable=true]"),
      page.locator("ytcp-social-suggestions-textbox#title-textarea"),
      page.locator("[aria-label*='title' i][contenteditable=true]"),
      page.locator("[aria-label*='Add a title' i]"),
      page.locator("[placeholder*='title' i]"),
      page.locator("[aria-label*='назван' i][contenteditable=true]")
    ];
    for (const box of candidates) {
      if (await visible(box, 2000)) {
        await box.first().click().catch(() => {});
        await page.keyboard.press("Control+A").catch(() => {});
        try {
          await box.first().fill(title);
        } catch (_) {
          await page.keyboard.type(title, { delay: 8 }).catch(() => {});
        }
        return;
      }
    }
    const filled = await page.evaluate((t) => {
      const root = document.querySelector("ytcp-uploads-dialog") || document.body;
      const box = root.querySelector("#title-textarea #textbox, #title-textarea [contenteditable=true], [contenteditable=true][aria-label*='title' i]");
      if (!box) return false;
      box.focus();
      box.textContent = t;
      box.dispatchEvent(new InputEvent("input", { bubbles: true }));
      box.dispatchEvent(new Event("change", { bubbles: true }));
      return true;
    }, title).catch(() => false);
    if (filled) return;
    send("youtube", "Жду поле заголовка…", { percent: 35 });
    await page.waitForTimeout(900);
  }
  throw new Error("Не найдено поле заголовка. Окно Dolphin оставлено открытым для проверки.");
}

function extractVideoId(value) {
  if (!value) return "";
  const text = String(value).trim();
  // Нормальный URL / голый ID
  let m = text.match(/(?:youtu\.be\/|v=|\/shorts\/|\/embed\/|\/live\/)([A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])/i);
  if (m) return m[1];
  m = text.match(/(?:youtu\.be\/|v=|\/shorts\/|\/embed\/|\/live\/)([A-Za-z0-9_-]{11})/i);
  if (m) return m[1];
  if (/^[A-Za-z0-9_-]{11}$/.test(text)) return text;
  // URL склеен с кириллическим заголовком без пробела: берём первые 11 символов ID
  m = text.match(/(?:youtu\.be\/|v=|\/shorts\/|\/embed\/|\/live\/)([A-Za-z0-9_-]{11})/i);
  return m ? m[1] : "";
}

/** Разделяет «название» и «ссылку», даже если URL вставили в поле названия. */
function parseSearchInputs(titleRaw, urlRaw) {
  let title = String(titleRaw || "").trim();
  let url = String(urlRaw || "").trim();
  let id = extractVideoId(url) || extractVideoId(title);

  if (/youtube\.com|youtu\.be/i.test(title)) {
    const m = title.match(/https?:\/\/(?:www\.)?(?:youtube\.com\/(?:watch\?v=|shorts\/)|youtu\.be\/)[A-Za-z0-9_-]{11}/i);
    if (m && !url) url = m[0];
    if (!id) id = extractVideoId(title);
    title = title
      .replace(/https?:\/\/\S+/gi, " ")
      .replace(id ? new RegExp(id.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi") : /$^/, " ")
      .replace(/^[\s\-_|:]+/, "")
      .replace(/\s+/g, " ")
      .trim();
  }
  if (!url && id) url = "https://www.youtube.com/watch?v=" + id;
  return { title, url, id };
}

/** Короткие ключи: по одному на строку, либо через | или ; */
function parseKeywordList(keysRaw, titleFallback) {
  const raw = String(keysRaw || titleFallback || "").trim();
  if (!raw) return [];
  const lines = raw.split(/\r?\n/).map(s => s.trim()).filter(Boolean);
  if (lines.length > 1) return [...new Set(lines)];
  if (/[|;]/.test(raw)) return [...new Set(raw.split(/[|;]+/).map(s => s.trim()).filter(Boolean))];
  return raw ? [raw] : [];
}

function normalizeTitle(value) {
  return String(value || "")
    .toLowerCase()
    .replace(/[«»"'`]/g, "")
    .replace(/\s+/g, " ")
    .trim();
}

function searchFilterParam(filter) {
  switch (String(filter || "today").toLowerCase()) {
    case "week": return "EgQIAxAB";
    case "month": return "EgQIBBAB";
    case "year": return "EgQIBRAB";
    case "none": return "";
    case "today":
    default: return "EgQIAhAB";
  }
}

function titleScore(candidate, target) {
  const a = normalizeTitle(candidate);
  const b = normalizeTitle(target);
  if (!a || !b) return 0;
  if (a === b) return 100;
  if (a.includes(b) || b.includes(a)) return 85;
  const words = b.split(" ").filter(w => w.length > 1);
  if (!words.length) return 0;
  const hits = words.filter(w => a.includes(w)).length;
  return Math.round((hits / words.length) * 70);
}

function buildSearchUrl(query, filter) {
  const url = new URL("https://www.youtube.com/results");
  url.searchParams.set("search_query", String(query || "").trim());
  const sp = searchFilterParam(filter);
  if (sp) url.searchParams.set("sp", sp);
  return url.toString();
}

async function gotoSearchResults(page, query, filter) {
  await gotoStable(page, buildSearchUrl(query, filter), { waitUntil: "domcontentloaded", timeout: 60000 });
  await dismissYouTubeOverlays(page);
  await page.locator("ytd-video-renderer, ytd-rich-item-renderer, ytd-reel-item-renderer").first()
    .waitFor({ state: "attached", timeout: 20000 }).catch(() => {});
}

async function typeYouTubeSearch(page, query) {
  await gotoSearchResults(page, query, "none");
}

async function collectSearchResults(page) {
  await dismissYouTubeOverlays(page);
  const items = page.locator("ytd-video-renderer, ytd-rich-item-renderer, ytd-grid-video-renderer, ytd-compact-video-renderer, ytd-reel-item-renderer, grid-shelf-view-model");
  await items.first().waitFor({ state: "attached", timeout: 45000 }).catch(() => {});
  for (let s = 0; s < 4; s++) {
    await page.mouse.wheel(0, 1400).catch(() => {});
    await new Promise(r => setTimeout(r, 250));
  }
  const results = await page.evaluate(() => {
    const out = [];
    const seen = new Set();
    const anchors = Array.from(document.querySelectorAll(
      "ytd-video-renderer a#video-title, ytd-video-renderer a#video-title-link, ytd-rich-item-renderer a#video-title-link, ytd-rich-item-renderer a[href*='/watch'], ytd-rich-item-renderer a[href*='/shorts/'], ytd-grid-video-renderer a#video-title, ytd-reel-item-renderer a[href*='/shorts/'], ytd-reel-item-renderer a#video-title, a#video-title, a#video-title-link, a[href*='/watch?v='], a[href*='/shorts/']"
    ));
    for (const a of anchors) {
      const href = a.getAttribute("href") || "";
      const m = href.match(/(?:v=|shorts\/)([A-Za-z0-9_-]{11})/);
      const id = m ? m[1] : "";
      if (!id || seen.has(id)) continue;
      seen.add(id);
      const title = ((a.getAttribute("title") || "") || (a.textContent || "")).replace(/\s+/g, " ").trim();
      out.push({ href, title, id });
      if (out.length >= 120) break;
    }
    return out;
  }).catch(() => []);
  return results;
}

async function findIdOnSearchPage(page, targetId) {
  if (!targetId) return null;
  return page.evaluate((id) => {
    const anchors = Array.from(document.querySelectorAll(
      "a#video-title, a#video-title-link, a[href*='watch'], a[href*='/shorts/'], a[href*='youtu.be/']"
    ));
    for (const a of anchors) {
      const href = a.getAttribute("href") || "";
      const m = href.match(/(?:v=|shorts\/|youtu\.be\/)([A-Za-z0-9_-]{11})/);
      const vid = m ? m[1] : "";
      if (vid !== id && !href.includes(id)) continue;
      const title = ((a.getAttribute("title") || "") || (a.textContent || "")).replace(/\s+/g, " ").trim();
      return { href, title, id: vid || id };
    }
    return null;
  }, targetId).catch(() => null);
}

async function scanResultsForId(page, targetId, maxScrolls = 8) {
  if (!targetId) return null;
  for (let s = 0; s <= maxScrolls; s++) {
    const hit = await findIdOnSearchPage(page, targetId);
    if (hit) return hit;
    if (s < maxScrolls) {
      await page.mouse.wheel(0, 1400).catch(() => {});
      await new Promise(r => setTimeout(r, 250));
    }
  }
  return null;
}

function pickTitleMatch(results, searchText, minScore) {
  const target = normalizeTitle(searchText);
  if (!target) return null;
  let exact = null;
  let close = null;
  let closeScore = 0;
  const need = Number.isFinite(minScore) ? minScore : 88;
  for (const item of results) {
    const title = normalizeTitle(item.title);
    if (!title) continue;
    if (title === target) { exact = item; break; }
    const score = titleScore(item.title, searchText);
    if (score > closeScore) { closeScore = score; close = item; }
  }
  return exact || (closeScore >= need ? close : null);
}

function pickTitleMatchMesh(results, searchText) {
  return pickTitleMatch(results, searchText, 62);
}

async function dismissYouTubeOverlays(page) {
  const labels = [
    /Accept all/i, /Accept/i, /I agree/i, /Agree/i,
    /Принять все/i, /Принять/i, /Согласен/i, /Хорошо/i, /OK/i,
    /Reject all/i, /Отклонить все/i
  ];
  for (const re of labels) {
    try {
      const btn = page.getByRole("button", { name: re }).first();
      if (await btn.isVisible({ timeout: 500 }).catch(() => false)) {
        await btn.click({ timeout: 1500 }).catch(() => {});
        await page.waitForTimeout(300);
      }
    } catch (_) {}
  }
  const close = page.locator("#dismiss-button, button[aria-label*='Close' i], button[aria-label*='Закрыть' i], ytd-button-renderer#dismiss-button button");
  if (await visible(close, 400)) await close.first().click({ timeout: 1000 }).catch(() => {});
}

function pickSearchMatch(results, targetId, searchText) {
  if (targetId) {
    const byId = results.find(r => r.id === targetId);
    if (byId) return byId;
    return null;
  }
  return pickTitleMatch(results, searchText);
}

function searchFiltersToTry(filter) {
  const f = String(filter || "today").toLowerCase();
  return f === "none" ? ["none"] : [f, "none"];
}

/** Поиск только по ключу/заголовку. targetId — только сверка в выдаче, не запрос и не прямое открытие. */
async function searchByKey(page, passQuery, targetId, filter) {
  await gotoSearchResults(page, passQuery, filter);
  await page.waitForTimeout(350);
  if (targetId) return scanResultsForId(page, targetId, 8);
  const results = await collectSearchResults(page);
  return pickTitleMatch(results, passQuery);
}

async function openVideoFromMatch(page, match, targetId) {
  const href = match.href || "";
  const id = match.id || targetId || extractVideoId(href) || "";
  const watch = href.startsWith("http") ? href : "https://www.youtube.com" + href;
  send("youtube", "Нашёл в выдаче. Открываю…", { percent: 80 });

  const clicked = await page.evaluate((videoId, videoHref) => {
    const anchors = Array.from(document.querySelectorAll("a#video-title, a#video-title-link, a[href*='/watch?v='], a[href*='/shorts/']"));
    const hit = anchors.find(a => {
      const h = a.getAttribute("href") || "";
      return (videoId && h.includes(videoId)) || h === videoHref || h.endsWith(videoHref);
    });
    if (!hit) return false;
    hit.scrollIntoView({ block: "center", inline: "nearest" });
    hit.click();
    return true;
  }, id, href).catch(() => false);

  if (!clicked) {
    const link = page.locator("a[href*='" + (id || "___") + "']").first();
    if (await visible(link, 2500)) await link.click({ timeout: 5000 }).catch(() => {});
  }

  await page.waitForURL(/\/(watch|shorts)\//, { timeout: 15000 }).catch(() => {});
  let openedId = extractVideoId(page.url());
  if (!openedId) {
    throw new Error("Ролик найден в выдаче, но не открылся. Профиль оставлен открытым — нажмите Play вручную.");
  }
  if (targetId && openedId && openedId !== targetId) {
    throw new Error("Открылся другой ролик (ID=" + openedId + "), нужен " + targetId + ". Профиль оставлен открытым.");
  }
  await dismissYouTubeOverlays(page);
  await ensureVideoPlaying(page);
  const finalUrl = page.url().split("&")[0];
  return finalUrl || watch.split("&")[0];
}

/**
 * Поиск только по ключам → потом полный заголовок.
 * Ссылка не вводится в поиск и не открывается — из неё берётся ID для сверки в выдаче.
 */
async function openFoundVideo(page, query, searchUrl, filter, opts = {}) {
  const linkParsed = parseSearchInputs("", searchUrl);
  const targetId = linkParsed.id;
  const shortKeys = parseKeywordList(opts.searchKeys || query, query);
  const fullTitle = String(opts.searchFullTitle || "").trim();
  const normalizedFull = fullTitle && !shortKeys.includes(fullTitle) ? fullTitle : "";

  if (!shortKeys.length && !normalizedFull) {
    throw new Error("Укажите ключи (по одному на строку) или полный заголовок. Ссылка — только ориентир по ID в выдаче.");
  }

  let match = null;
  let usedQuery = "";

  for (let i = 0; i < shortKeys.length; i++) {
    const key = shortKeys[i];
    send("youtube", "Ключ " + (i + 1) + "/" + shortKeys.length + ": «" + key.slice(0, 55) + (key.length > 55 ? "…" : "") + "»…", { percent: 30 });
    for (const passFilter of searchFiltersToTry(filter)) {
      match = await searchByKey(page, key, targetId, passFilter);
      if (match) { usedQuery = key; break; }
    }
    if (match) break;
  }

  if (!match && normalizedFull) {
    send("youtube", "Ключи не нашли — полный заголовок…", { percent: 55 });
    match = await searchByKey(page, normalizedFull, targetId, "none");
    if (match) usedQuery = normalizedFull;
  }

  if (!match) {
    const linkHint = targetId
      ? " Ссылка-ориентир ID=" + targetId + " — в выдаче по ключам не встретился."
      : " Добавьте ссылку — по ID в выдаче не перепутаете похожие названия.";
    const tried = shortKeys.concat(normalizedFull ? [normalizedFull] : []).map(q => "«" + q + "»").join(", ");
    throw new Error("Видео не найдено по ключам. Пробовали: " + tried + "." + linkHint);
  }

  send("youtube", "Нашёл по «" + usedQuery.slice(0, 55) + (usedQuery.length > 55 ? "…" : "") + "»", { percent: 78 });
  return openVideoFromMatch(page, match, targetId);
}

function normalizeChannelUrl(url) {
  let u = String(url || "").trim();
  if (!u) return "";
  if (!/^https?:/i.test(u)) u = "https://www.youtube.com" + (u.startsWith("/") ? u : "/" + u);
  return u.replace(/\/(videos|shorts|streams|playlists|featured|about|community).*$/, "").replace(/\/+$/, "");
}

function parseChannelHandle(name) {
  const m = String(name || "").match(/@([A-Za-z0-9._-]+)/);
  return m ? m[1] : "";
}

function channelUrlFromHandle(handle) {
  const h = String(handle || "").trim().replace(/^@+/, "");
  return h ? ("https://www.youtube.com/@" + h) : "";
}

function channelDisplayName(name) {
  return String(name || "").replace(/\s*@[A-Za-z0-9._-]+\s*$/g, "").replace(/\s+/g, " ").trim();
}

function channelSearchQuery(ownerName) {
  return channelDisplayName(ownerName)
    .replace(/\s*\([^)]*\)\s*/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

async function openChannelByHandle(page, handle) {
  const h = String(handle || "").trim().replace(/^@+/, "");
  if (!h) return "";
  const url = "https://www.youtube.com/@" + h;
  await gotoStable(page, url, { waitUntil: "domcontentloaded", timeout: 60000 });
  await dismissYouTubeOverlays(page);
  const ok = await page.evaluate(() => {
    if (document.querySelector("ytd-channel-name, #channel-name, yt-formatted-string.ytd-channel-name")) return true;
    return /\/@[^/]+/.test(location.pathname) || /\/channel\//.test(location.pathname);
  }).catch(() => false);
  return ok ? normalizeChannelUrl(url) : "";
}

function isDurationOnly(text) {
  return /^\d{1,2}:\d{2}(:\d{2})?$/.test(String(text || "").trim());
}

function channelNameScore(pageName, ownerName) {
  const a = normalizeTitle(pageName);
  const b = normalizeTitle(channelSearchQuery(ownerName));
  if (!a || !b) return 0;
  if (a === b || a.includes(b) || b.includes(a)) return 100;
  const words = b.split(" ").filter(w => w.length > 2);
  if (!words.length) return 0;
  const hits = words.filter(w => a.includes(w)).length;
  return Math.round((hits / words.length) * 100);
}

async function readChannelPageName(page) {
  return page.evaluate(() => {
    const el = document.querySelector(
      "yt-formatted-string.ytd-channel-name, #channel-name yt-formatted-string, ytd-channel-name #text, #channel-header-container #channel-name"
    );
    let name = (el && el.textContent ? el.textContent : "").replace(/\s+/g, " ").trim();
    if (!name) name = (document.title || "").replace(/\s*- YouTube.*$/i, "").trim();
    return name;
  }).catch(() => "");
}

async function verifyChannelPageName(page, ownerName) {
  const name = await readChannelPageName(page);
  return channelNameScore(name, ownerName) >= 60;
}

async function resolveChannelUrl(page, ownerName, savedUrl) {
  const handle = parseChannelHandle(ownerName);
  const label = channelDisplayName(ownerName) || ownerName;

  if (handle) {
    const byHandle = await openChannelByHandle(page, handle);
    if (byHandle) {
      send("youtube", label + ": @" + handle + " (из сетки)", { percent: 42 });
      return byHandle;
    }
    send("youtube", label + ": @" + handle + " не открылся — пробую иначе…", { percent: 43 });
  }

  const saved = normalizeChannelUrl(savedUrl || "");
  if (saved && (!handle || saved.toLowerCase().includes("/@" + handle.toLowerCase()))) {
    await gotoStable(page, saved, { waitUntil: "domcontentloaded", timeout: 60000 });
    await dismissYouTubeOverlays(page);
    if (handle || await verifyChannelPageName(page, label)) return saved;
    send("youtube", label + ": сохранённый URL не совпал — ищу заново…", { percent: 44 });
  }

  const found = await findChannelByOwnerName(page, label);
  if (!found) return "";
  await gotoStable(page, found, { waitUntil: "domcontentloaded", timeout: 60000 });
  await dismissYouTubeOverlays(page);
  if (!(await verifyChannelPageName(page, label))) {
    throw new Error("Канал «" + label + "» не подтверждён (страница: «" + (await readChannelPageName(page)).slice(0, 40) + "»).");
  }
  return normalizeChannelUrl(found);
}

function meshCatalogPath() {
  const root = process.env.LOCALAPPDATA || process.env.APPDATA || "";
  return root ? path.join(root, "VideoBatchDesktop", "youtube-mesh-catalog.json") : "";
}

function meshChannelCachePath(profileId) {
  const root = process.env.LOCALAPPDATA || process.env.APPDATA || "";
  const safe = String(profileId || "").trim().replace(/[^A-Za-z0-9_-]/g, "_");
  return root && safe ? path.join(root, "VideoBatchDesktop", "mesh-channels", safe + ".json") : "";
}

function loadCachedChannelUrl(profileId) {
  try {
    const p = meshChannelCachePath(profileId);
    if (!p || !fs.existsSync(p)) return "";
    return normalizeChannelUrl(JSON.parse(fs.readFileSync(p, "utf8")).channelUrl || "");
  } catch (_) {
    return "";
  }
}

function saveCachedChannelUrl(profileId, channelUrl) {
  const p = meshChannelCachePath(profileId);
  const url = normalizeChannelUrl(channelUrl);
  if (!p || !url) return;
  fs.mkdirSync(path.dirname(p), { recursive: true });
  const temp = p + ".tmp-" + process.pid;
  fs.writeFileSync(temp, JSON.stringify({ profileId: String(profileId), channelUrl: url, updatedAt: new Date().toISOString() }), "utf8");
  fs.renameSync(temp, p);
}

function loadMeshCatalog() {
  try {
    const p = meshCatalogPath();
    if (!p || !fs.existsSync(p)) return null;
    return JSON.parse(fs.readFileSync(p, "utf8"));
  } catch (_) {
    return null;
  }
}

function enrichWatchTarget(target, catalog) {
  const t = Object.assign({}, target || {});
  if (!catalog || !Array.isArray(catalog.videos)) return t;
  const ownerPid = String(t.ownerProfileId || "").trim();
  const ownerNorm = normalizeTitle(t.ownerName || t.channel || "");
  const entries = catalog.videos.filter(v => {
    if (!v) return false;
    if (ownerPid && String(v.profileId || "").trim() === ownerPid) return true;
    return ownerNorm && normalizeTitle(v.channel || "") === ownerNorm;
  });
  if (!String(t.videoId || "").trim()) {
    const hit = entries.find(v => String(v.videoId || "").trim());
    if (hit) {
      t.videoId = String(hit.videoId).trim();
      if (!String(t.searchUrl || "").trim() && hit.url) t.searchUrl = hit.url;
    }
  }
  if (!String(t.channelUrl || "").trim()) {
    const fromHandle = channelUrlFromHandle(parseChannelHandle(t.ownerName || ""));
    if (fromHandle) t.channelUrl = fromHandle;
  }
  if (!String(t.channelUrl || "").trim()) {
    const hit = entries.find(v => String(v.channelUrl || "").trim());
    if (hit) t.channelUrl = String(hit.channelUrl).trim();
  }
  if (!String(t.channelUrl || "").trim() && ownerPid) {
    t.channelUrl = loadCachedChannelUrl(ownerPid);
  }
  const fromCatalog = entries.map(v => ({
    videoId: String(v.videoId || "").trim(),
    url: String(v.url || "").trim(),
    title: String(v.title || "").trim(),
    kind: String(v.kind || "long").trim()
  })).filter(v => v.videoId || v.title);
  const fromJob = (Array.isArray(t.catalogVideos) ? t.catalogVideos : []).map(v => ({
    videoId: String(v.videoId || "").trim(),
    url: String(v.url || "").trim(),
    title: String(v.title || "").trim(),
    kind: String(v.kind || "long").trim()
  })).filter(v => v.videoId || v.title);
  const merged = [];
  const seenKeys = new Set();
  for (const v of fromJob.concat(fromCatalog)) {
    const key = v.videoId || normalizeTitle(v.title);
    if (!key || seenKeys.has(key)) continue;
    seenKeys.add(key);
    merged.push(v);
  }
  t.knownVideos = merged;
  return t;
}

async function findChannelByOwnerName(page, ownerName) {
  const queries = [];
  const clean = channelSearchQuery(ownerName);
  if (clean) queries.push(clean);
  if (ownerName && ownerName !== clean) queries.push(String(ownerName).trim());
  for (const q of queries) {
    const url = "https://www.youtube.com/results?search_query=" + encodeURIComponent(q) + "&sp=EgIQAg%253D%253D";
    await gotoStable(page, url, { waitUntil: "domcontentloaded", timeout: 60000 });
    await dismissYouTubeOverlays(page);
    await page.locator("ytd-channel-renderer, ytd-item-section-renderer").first()
      .waitFor({ state: "attached", timeout: 15000 }).catch(() => {});
    const found = await page.evaluate((name) => {
      const norm = (s) => String(s || "").toLowerCase().replace(/\([^)]*\)/g, "").replace(/\s+/g, " ").trim();
      const want = norm(name);
      const words = want.split(" ").filter(w => w.length > 2);
      const rows = document.querySelectorAll("ytd-channel-renderer");
      let best = null;
      let bestScore = 0;
      for (const row of rows) {
        const titleEl = row.querySelector("#channel-title, #text, yt-formatted-string#channel-title, #main-link");
        const title = norm(titleEl ? titleEl.textContent : "");
        const a = row.querySelector("a[href*='/@'], a[href*='/channel/'], a#main-link");
        if (!a) continue;
        const href = a.getAttribute("href") || "";
        if (!/\/@|\/channel\//.test(href)) continue;
        let score = 0;
        if (title === want) score = 100;
        else if (title.includes(want) || want.includes(title)) score = 95;
        else if (words.length) {
          const hits = words.filter(w => title.includes(w)).length;
          score = Math.round((hits / words.length) * 100);
        }
        if (score > bestScore) {
          bestScore = score;
          best = href.startsWith("http") ? href.split("?")[0] : "https://www.youtube.com" + href.split("?")[0];
        }
      }
      return best && bestScore >= 55 ? best : "";
    }, ownerName).catch(() => "");
    if (found) return found;
  }
  return "";
}

async function openVideoDirect(page, videoId, preferShorts) {
  const id = String(videoId || "").trim();
  if (!id) throw new Error("Пустой ID видео.");
  const urls = preferShorts
    ? ["https://www.youtube.com/shorts/" + id, "https://www.youtube.com/watch?v=" + id]
    : ["https://www.youtube.com/watch?v=" + id, "https://www.youtube.com/shorts/" + id];
  for (const u of urls) {
    await gotoStable(page, u, { waitUntil: "domcontentloaded", timeout: 60000 });
    await dismissYouTubeOverlays(page);
    if (extractVideoId(page.url())) {
      await ensureVideoPlaying(page);
      return page.url().split("&")[0];
    }
  }
  throw new Error("Не удалось открыть видео по ID " + id);
}

/** Сетка: поиск без фильтра «сегодня», мягче совпадение заголовка. */
async function openFoundVideoMesh(page, query, searchUrl, opts = {}) {
  const linkParsed = parseSearchInputs("", searchUrl);
  const targetId = linkParsed.id;
  const shortKeys = parseKeywordList(opts.searchKeys || query, query);
  const fullTitle = String(opts.searchFullTitle || "").trim();
  const normalizedFull = fullTitle && !shortKeys.includes(fullTitle) ? fullTitle : "";

  if (!shortKeys.length && !normalizedFull && !targetId) {
    throw new Error("Нет ключей, заголовка или ID для поиска.");
  }

  let match = null;
  let usedQuery = "";

  if (targetId) {
    send("youtube", "Сверяю ID " + targetId + " в выдаче…", { percent: 55 });
    for (const key of shortKeys.length ? shortKeys : [normalizedFull].filter(Boolean)) {
      await gotoSearchResults(page, key, "none");
      match = await scanResultsForId(page, targetId, 10);
      if (match) { usedQuery = key; break; }
    }
    if (!match && normalizedFull) {
      await gotoSearchResults(page, normalizedFull, "none");
      match = await scanResultsForId(page, targetId, 10);
      if (match) usedQuery = normalizedFull;
    }
  }

  for (let i = 0; !match && i < shortKeys.length; i++) {
    const key = shortKeys[i];
    send("youtube", "Ключ " + (i + 1) + "/" + shortKeys.length + ": «" + key.slice(0, 55) + (key.length > 55 ? "…" : "") + "»…", { percent: 30 });
    await gotoSearchResults(page, key, "none");
    const results = await collectSearchResults(page);
    match = targetId ? results.find(r => r.id === targetId) : pickTitleMatchMesh(results, key);
    if (match) { usedQuery = key; break; }
  }

  if (!match && normalizedFull) {
    send("youtube", "Ключи не нашли — полный заголовок…", { percent: 55 });
    await gotoSearchResults(page, normalizedFull, "none");
    const results = await collectSearchResults(page);
    match = targetId ? results.find(r => r.id === targetId) : pickTitleMatchMesh(results, normalizedFull);
    if (match) usedQuery = normalizedFull;
  }

  if (!match) {
    const tried = shortKeys.concat(normalizedFull ? [normalizedFull] : []).map(q => "«" + q + "»").join(", ");
    throw new Error("Видео не найдено. Пробовали: " + tried + ".");
  }

  send("youtube", "Нашёл по «" + usedQuery.slice(0, 55) + (usedQuery.length > 55 ? "…" : "") + "»", { percent: 78 });
  return openVideoFromMatch(page, match, targetId);
}

async function captureUploadedVideoMeta(page, title) {
  let url = "";
  let videoId = "";
  let channelUrl = "";
  const share = page.locator("#share-url, input[value*='youtu'], a[href*='youtu.be'], a[href*='youtube.com/watch']");
  if (await share.count()) {
    url = (await share.first().getAttribute("value").catch(() => "")) || (await share.first().getAttribute("href").catch(() => "")) || "";
    videoId = extractVideoId(url);
  }
  if (!videoId) {
    await openStudioContent(page);
    const meta = await page.evaluate((wantTitle) => {
      const norm = (s) => String(s || "").toLowerCase().replace(/[«»"'`]/g, "").replace(/\s+/g, " ").trim();
      const want = norm(wantTitle);
      const wantShort = want.slice(0, Math.min(28, want.length));
      const rows = Array.from(document.querySelectorAll("ytcp-video-row, ytcp-video-section-entry, a[href*='/video/']"));
      for (const row of rows) {
        const text = norm(row.innerText || row.textContent || row.getAttribute("title") || "");
        if (!text || (!text.includes(wantShort) && want.length > 12 && !want.includes(text.slice(0, 28)))) continue;
        const a = row.matches("a[href*='/video/']") ? row : row.querySelector("a[href*='/video/']");
        if (!a) continue;
        const href = a.getAttribute("href") || "";
        const m = href.match(/\/video\/([A-Za-z0-9_-]{11})/);
        if (m) return { videoId: m[1] };
      }
      return null;
    }, title).catch(() => null);
    if (meta && meta.videoId) {
      videoId = meta.videoId;
      url = "https://www.youtube.com/watch?v=" + videoId;
    }
    channelUrl = await page.evaluate(() => {
      const picks = [
        "a[href*='youtube.com/channel/']",
        "a[href*='youtube.com/@']",
        "#channel-handle a",
        "ytcp-ve a[href*='/@']"
      ];
      for (const sel of picks) {
        const a = document.querySelector(sel);
        if (!a) continue;
        const href = a.getAttribute("href") || "";
        if (/\/channel\/|\/@/.test(href)) {
          return href.startsWith("http") ? href.split("?")[0] : "https://www.youtube.com" + href.split("?")[0];
        }
      }
      const m = location.href.match(/studio\.youtube\.com\/channel\/(UC[\w-]+)/i);
      if (m) return "https://www.youtube.com/channel/" + m[1];
      return "";
    }).catch(() => "");
  }
  if (channelUrl) channelUrl = normalizeChannelUrl(channelUrl);
  return { url, videoId, channelUrl };
}

async function extractChannelUrlFromPage(page) {
  return page.evaluate(() => {
    const picks = ["#owner a", "ytd-channel-name a", "#channel-name a", "ytd-video-owner-renderer a", "ytd-watch-metadata a[href*='/@'], ytd-watch-metadata a[href*='/channel/']"];
    for (const sel of picks) {
      const a = document.querySelector(sel);
      if (!a) continue;
      const href = a.getAttribute("href") || "";
      if (/\/@|\/channel\//.test(href)) {
        return href.startsWith("http") ? href.split("?")[0] : "https://www.youtube.com" + href.split("?")[0];
      }
    }
    return "";
  }).catch(() => "");
}

/** Часы с момента публикации; null = неизвестно, Infinity = явно старое. */
function uploadAgeHours(text) {
  const t = String(text || "").toLowerCase().replace(/\s+/g, " ").trim();
  if (!t) return null;
  if (/today|сегодня|just now|только что/.test(t)) return 0;
  let m = t.match(/(\d+)\s*(year|years|год|года|лет)(?:\s|$|[^a-zа-я])/);
  if (m) return parseInt(m[1], 10) * 8760;
  m = t.match(/(\d+)\s*(month|months|месяц|месяца|месяцев)(?:\s|$|[^a-zа-я])/);
  if (m) return parseInt(m[1], 10) * 720;
  m = t.match(/(\d+)\s*(week|weeks|недел|недели|недель)(?:\s|$|[^a-zа-я])/);
  if (m) return parseInt(m[1], 10) * 168;
  m = t.match(/(\d+)\s*(day|days|день|дня|дней)(?:\s|$|[^a-zа-я])/);
  if (m) return parseInt(m[1], 10) * 24;
  m = t.match(/(\d+)\s*(hour|hours|hr|час|часа|часов|ч\.?)(?:\s|$|[^a-zа-я])/);
  if (m) return parseInt(m[1], 10);
  m = t.match(/(\d+)\s*(minute|minutes|минут|минуту|минуты|мин)(?:\s|$|[^a-zа-я])/);
  if (m) return parseInt(m[1], 10) / 60;
  m = t.match(/(\d+)\s*(second|seconds|секунд|секунду|сек|с\.?)(?:\s|$|[^a-zа-я])/);
  if (m) return parseInt(m[1], 10) / 3600;
  return null;
}

function isTodayUploadMeta(text, maxHours) {
  const limit = Number.isFinite(maxHours) ? maxHours : 36;
  const age = uploadAgeHours(text);
  if (age === null) return false;
  return age <= limit;
}

/** Выйти из плеера watch/shorts на страницу канала (перед следующим каналом). */
async function leaveVideoPlayer(page, channelUrl) {
  const root = normalizeChannelUrl(channelUrl);
  const url = root || "https://www.youtube.com";
  await gotoStable(page, url, { waitUntil: "domcontentloaded", timeout: 45000 }).catch(() => {});
  await dismissYouTubeOverlays(page);
  await page.waitForTimeout(500);
}

async function openChannelTab(page, channelUrl, tab) {
  const root = normalizeChannelUrl(channelUrl);
  if (!root) throw new Error("Пустой URL канала.");
  const path = tab === "shorts" ? "/shorts" : "/videos";
  await gotoStable(page, root + path, { waitUntil: "domcontentloaded", timeout: 60000 });
  await dismissYouTubeOverlays(page);
  if (tab === "shorts") {
    await clickByText(page, ["Shorts", "Шортс"], 1500).catch(() => {});
    await page.waitForTimeout(600);
    await clickByText(page, ["^New$", "^Новые$", "^Newest$"], 1000).catch(() => {});
    await page.waitForTimeout(500);
  } else {
    await clickByText(page, ["Videos", "Видео"], 1200).catch(() => {});
    await page.waitForTimeout(400);
  }
  await page.locator("ytd-rich-item-renderer, ytd-grid-video-renderer, ytd-video-renderer, ytd-reel-item-renderer, ytd-rich-grid-slim-media").first()
    .waitFor({ state: "attached", timeout: 20000 }).catch(() => {});
}

async function collectLinksOnChannelTab(page, baseUrl, tab, skipId, todayOnly, limit, opts) {
  const root = normalizeChannelUrl(baseUrl);
  if (!root) return [];
  const isShorts = tab === "/shorts";
  const tabName = isShorts ? "shorts" : "videos";
  await openChannelTab(page, root, tabName);
  const scrolls = isShorts ? Math.max(6, Math.ceil((limit || 8) / 3)) : 8;
  for (let s = 0; s < scrolls; s++) {
    await page.mouse.wheel(0, 1400).catch(() => {});
    await new Promise(r => setTimeout(r, 220));
  }
  const shortsTop = !!(opts && opts.shortsTop);
  const maxHours = (opts && opts.maxHours) || 36;
  return page.evaluate(({ skip, todayOnly, max, isShorts, shortsTop, maxHours }) => {
    function durationOnly(s) {
      return /^\d{1,2}:\d{2}(:\d{2})?$/.test(String(s || "").trim());
    }
    function pickTitle(row, a) {
      const fromAttr = (a.getAttribute("title") || a.getAttribute("aria-label") || "").trim();
      if (fromAttr && !durationOnly(fromAttr)) return fromAttr.replace(/\s+/g, " ").trim();
      const inner = row.querySelector("#video-title, yt-formatted-string#video-title, h3");
      const t2 = (inner && inner.textContent ? inner.textContent : "").trim();
      if (t2 && !durationOnly(t2)) return t2.replace(/\s+/g, " ").trim();
      return "";
    }
    function uploadAgeHours(text) {
      const t = String(text || "").toLowerCase().replace(/\s+/g, " ").trim();
      if (!t) return null;
      if (/today|сегодня|just now|только что/.test(t)) return 0;
      let m = t.match(/(\d+)\s*(year|years|год|года|лет)(?:\s|$|[^a-z\u0430-\u044f])/);
      if (m) return parseInt(m[1], 10) * 8760;
      m = t.match(/(\d+)\s*(month|months|месяц|месяца|месяцев)(?:\s|$|[^a-z\u0430-\u044f])/);
      if (m) return parseInt(m[1], 10) * 720;
      m = t.match(/(\d+)\s*(week|weeks|недел|недели|недель)(?:\s|$|[^a-z\u0430-\u044f])/);
      if (m) return parseInt(m[1], 10) * 168;
      m = t.match(/(\d+)\s*(day|days|день|дня|дней)(?:\s|$|[^a-z\u0430-\u044f])/);
      if (m) return parseInt(m[1], 10) * 24;
      m = t.match(/(\d+)\s*(hour|hours|hr|час|часа|часов|ч\.?)(?:\s|$|[^a-z\u0430-\u044f])/);
      if (m) return parseInt(m[1], 10);
      m = t.match(/(\d+)\s*(minute|minutes|минут|минуту|минуты|мин)(?:\s|$|[^a-z\u0430-\u044f])/);
      if (m) return parseInt(m[1], 10) / 60;
      m = t.match(/(\d+)\s*(second|seconds|секунд|секунду|сек|с\.?)(?:\s|$|[^a-z\u0430-\u044f])/);
      if (m) return parseInt(m[1], 10) / 3600;
      return null;
    }
    function recentMeta(meta) {
      const age = uploadAgeHours(meta);
      return age !== null && age <= maxHours;
    }
    const out = [];
    const seen = new Set();
    const rows = document.querySelectorAll(
      "ytd-rich-item-renderer, ytd-grid-video-renderer, ytd-video-renderer, ytd-reel-item-renderer, ytd-rich-grid-slim-media"
    );
    for (const row of rows) {
      const a = row.querySelector("a#video-title-link, a#video-title, a[href*='/watch?v='], a[href*='/shorts/']");
      if (!a) continue;
      const href = a.getAttribute("href") || "";
      const m = href.match(/(?:v=|shorts\/)([A-Za-z0-9_-]{11})/);
      const id = m ? m[1] : "";
      if (!id || seen.has(id) || id === skip) continue;
      const metaEl = row.querySelector("#metadata-line, ytd-video-meta-block, .inline-metadata-items, #metadata");
      const meta = (metaEl ? metaEl.innerText : "").replace(/\s+/g, " ").trim();
      if (todayOnly && !recentMeta(meta)) {
        if (!(isShorts && shortsTop)) continue;
      }
      seen.add(id);
      const title = pickTitle(row, a) || id;
      out.push({ href, id, title, meta });
      if (out.length >= max) break;
    }
    return out;
  }, { skip: skipId || "", todayOnly: !!todayOnly, max: limit || 8, isShorts, shortsTop, maxHours }).catch(() => []);
}

async function readWatchPageUploadMeta(page) {
  return page.evaluate(() => {
    const picks = [
      "#info #date yt-formatted-string",
      "#info #date",
      "#info span",
      "yt-formatted-string#date",
      "#below yt-formatted-string#date",
      "#description-inline-expander #date"
    ];
    for (const sel of picks) {
      const el = document.querySelector(sel);
      const t = (el && el.textContent ? el.textContent : "").replace(/\s+/g, " ").trim();
      if (t && t.length < 80) return t;
    }
    const info = document.querySelector("#info");
    if (info) {
      const t = (info.innerText || "").replace(/\s+/g, " ");
      const m = t.match(/(\d+\s*(?:minute|minutes|hour|hours|day|days|week|weeks|month|months|year|years|минут|минуту|минуты|час|часа|часов|день|дня|dней|недел|месяц|год|лет)[^·\n]{0,20})/i);
      if (m) return m[1].trim();
    }
    return "";
  }).catch(() => "");
}

async function openVideoFromChannelList(page, item) {
  const id = item.id || "";
  const clicked = await page.evaluate((videoId) => {
    const anchors = Array.from(document.querySelectorAll("a[href*='/watch'], a[href*='/shorts/']"));
    const hit = anchors.find(a => (a.getAttribute("href") || "").includes(videoId));
    if (!hit) return false;
    hit.scrollIntoView({ block: "center", inline: "nearest" });
    hit.click();
    return true;
  }, id).catch(() => false);
  if (!clicked) throw new Error("Не удалось открыть ролик на странице канала.");
  await page.waitForURL(/\/(watch|shorts)\//, { timeout: 20000 }).catch(() => {});
  await dismissYouTubeOverlays(page);
  await ensureVideoPlaying(page);
}

async function quickLikeVideo(page) {
  await ensureVideoPlaying(page);
  await page.waitForTimeout(2800);
  await likeCurrentVideo(page);
}

function titleMatchesCatalog(videoTitle, catalogTitle) {
  const v = normalizeTitle(videoTitle || "");
  const c = normalizeTitle(catalogTitle || "");
  if (!v || !c) return false;
  if (v === c) return true;
  const chunk = Math.min(24, c.length, v.length);
  return v.includes(c.slice(0, chunk)) || c.includes(v.slice(0, chunk));
}

/** Шортс зацикливается — один проход, лайк, выход (не waitForVideoEnd). */
async function watchShortOnce(page, ownerName) {
  await dismissYouTubeOverlays(page);
  await ensureVideoPlaying(page);
  await page.waitForTimeout(1200);

  const alreadyLiked = await isVideoLiked(page);
  if (alreadyLiked) {
    send("youtube", (ownerName ? ownerName + ": " : "") + "Лайк уже стоит — всё равно смотрю шортс до конца.", { percent: 91 });
  }

  let duration = 0;
  for (let i = 0; i < 20; i++) {
    const st = await readPlaybackState(page);
    if (st.duration > 1) { duration = st.duration; break; }
    await ensureVideoPlaying(page);
    await page.waitForTimeout(400);
  }
  if (!(duration > 1)) duration = 45;

  const likeAt = Math.max(1, duration * 0.72);
  const doneAt = Math.max(likeAt + 1, duration - Math.min(0.35, duration * 0.01));
  let liked = alreadyLiked;
  let likeAttempted = alreadyLiked;
  let maxSeen = 0;
  const deadline = Date.now() + Math.ceil(duration * 1000) + 20000;

  while (Date.now() < deadline) {
    await skipAdIfPossible(page);
    const st = await readPlaybackState(page);
    const cur = Number(st.current) || 0;
    if (maxSeen >= doneAt && cur < maxSeen - 1.5) {
      if (!liked && !likeAttempted) {
        likeAttempted = true;
        liked = await tryLikeCurrentVideo(page, "Шортс");
      }
      send("youtube", (ownerName ? ownerName + ": " : "") + "Шортс: полный проход (без зацикливания).", { percent: 94 });
      return;
    }
    maxSeen = Math.max(maxSeen, cur);
    if (!liked && !likeAttempted && cur >= likeAt) {
      likeAttempted = true;
      liked = await tryLikeCurrentVideo(page, "Шортс");
    }
    if (st.ended || cur >= doneAt) {
      if (!liked && !likeAttempted) {
        likeAttempted = true;
        liked = await tryLikeCurrentVideo(page, "Шортс");
      }
      send("youtube", (ownerName ? ownerName + ": " : "") + "Шортс просмотрен полностью.", { percent: 94 });
      return;
    }
    await page.waitForTimeout(350);
  }
  throw new Error(`Шортс не дошёл до конца: ${formatClock(maxSeen)} / ${formatClock(duration)}.`);
}

const MESH_LONG_MAX = 1;
const MESH_SHORTS_MAX = 5;

async function watchTodayOnChannel(page, channelUrl, skipId, ownerName, likeOnly, knownVideos, anchorTitle) {
  let count = 0;
  const watched = new Set(skipId ? [skipId] : []);
  const stats = { long: 0, shorts: 0, skippedLiked: 0, errors: 0 };

  async function watchOne(item, isShort, skipDateCheck) {
    if (!item || !item.id || watched.has(item.id)) return false;
    watched.add(item.id);
    const label = (item.title && !isDurationOnly(item.title)) ? item.title : item.id;
    await openVideoDirect(page, item.id, isShort || /\/shorts\//.test(item.href || ""));

    if (await isVideoLiked(page)) {
      if (likeOnly) {
        send("youtube", (ownerName ? ownerName + ": " : "") + "«" + label.slice(0, 40) + "» — лайк уже есть.", { percent: 91 });
        stats.skippedLiked++;
        return false;
      }
      send("youtube", (ownerName ? ownerName + ": " : "") + "«" + label.slice(0, 40) + "» — лайк уже есть, но просмотр выполняю полностью.", { percent: 91 });
    }

    if (!skipDateCheck && !isShort) {
      const pageMeta = await readWatchPageUploadMeta(page);
      const meta = pageMeta || item.meta || "";
      const catalogHit = (knownVideos || []).some(kv => titleMatchesCatalog(label, kv.title));
      if (!isTodayUploadMeta(meta) && !titleMatchesCatalog(label, anchorTitle) && !catalogHit) {
        send("youtube", (ownerName ? ownerName + ": " : "") + "Пропуск «" + label.slice(0, 40) + "» — не сегодня (" + (meta || "?").slice(0, 28) + ")", { percent: 90 });
        return false;
      }
    }

    send("youtube", (ownerName ? ownerName + ": " : "") + (likeOnly ? "Лайк «" : "Смотрю «") + label.slice(0, 48) + "»…", { percent: 90 });
    if (isShort) {
      await watchShortOnce(page, ownerName);
      stats.shorts++;
    } else if (likeOnly) {
      await quickLikeVideo(page);
      stats.long++;
    } else {
      await waitForVideoEnd(page);
      stats.long++;
    }
    count++;
    return true;
  }

  for (const kv of (knownVideos || [])) {
    if (!kv.videoId || watched.has(kv.videoId)) continue;
    try {
      await watchOne({ id: kv.videoId, title: kv.title, href: kv.url || "", meta: "" }, kv.kind === "shorts", true);
    } catch (e) {
      stats.errors++;
      send("youtube", (ownerName ? ownerName + ": " : "") + "Каталог: пропуск — " + (e instanceof Error ? e.message : String(e)), { percent: 90 });
    }
  }

  send("youtube", (ownerName ? ownerName + ": " : "") + "канал · вкладка Видео" + (likeOnly ? " · лайки" : "") + "…", { percent: 86 });
  const allLong = await collectLinksOnChannelTab(page, channelUrl, "/videos", skipId, false, 15);
  let longList = [];
  const catalogLongs = (knownVideos || []).filter(v => v.kind !== "shorts");
  for (const plan of catalogLongs) {
    if (longList.length >= MESH_LONG_MAX) break;
    if (plan.videoId) {
      const hit = allLong.find(v => v.id === plan.videoId);
      longList.push(hit || { id: plan.videoId, title: plan.title, href: plan.url || "" });
    } else if (plan.title) {
      const hit = allLong.find(v => titleMatchesCatalog(v.title, plan.title));
      if (hit) longList.push(hit);
    }
  }
  if (!longList.length && anchorTitle) {
    const hit = allLong.find(v => titleMatchesCatalog(v.title, anchorTitle));
    if (hit) longList.push(hit);
  }
  if (!longList.length) {
    const todayLong = allLong.filter(v => isTodayUploadMeta(v.meta)).slice(0, MESH_LONG_MAX);
    if (todayLong.length) longList = todayLong;
  }
  longList = longList.slice(0, MESH_LONG_MAX);
  if (!longList.length) {
    send("youtube", (ownerName ? ownerName + ": " : "") + "длинное видео не найдено — вкладка Видео", { percent: 87 });
  }
  for (const item of longList) {
    try {
      await watchOne(item, false, false);
    } catch (e) {
      stats.errors++;
      send("youtube", (ownerName ? ownerName + ": " : "") + "Видео: пропуск — " + (e instanceof Error ? e.message : String(e)), { percent: 90 });
    }
    await openChannelTab(page, channelUrl, "videos");
  }

  send("youtube", (ownerName ? ownerName + ": " : "") + "канал · вкладка Shorts" + (likeOnly ? " · лайки" : "") + "…", { percent: 88 });
  const allShorts = await collectLinksOnChannelTab(page, channelUrl, "/shorts", skipId, false, 25, { shortsTop: true });
  send("youtube", (ownerName ? ownerName + ": " : "") + "Shorts: найдено " + allShorts.length + ", план до " + MESH_SHORTS_MAX + "…", { percent: 88 });
  const catalogShorts = (knownVideos || []).filter(v => v.kind === "shorts" && v.title);
  let shortsList = [];
  for (const plan of catalogShorts) {
    if (shortsList.length >= MESH_SHORTS_MAX) break;
    if (plan.videoId) {
      const hit = allShorts.find(s => s.id === plan.videoId);
      if (hit && !shortsList.some(x => x.id === hit.id)) shortsList.push(hit);
      else if (!shortsList.some(x => x.id === plan.videoId)) {
        shortsList.push({ id: plan.videoId, title: plan.title, href: plan.url || "" });
      }
    } else {
      const hit = allShorts.find(s => titleMatchesCatalog(s.title, plan.title));
      if (hit && !shortsList.some(x => x.id === hit.id)) shortsList.push(hit);
    }
  }
  for (const item of allShorts) {
    if (shortsList.length >= MESH_SHORTS_MAX) break;
    if (!shortsList.some(x => x.id === item.id)) shortsList.push(item);
  }
  shortsList = shortsList.slice(0, MESH_SHORTS_MAX);
  if (!shortsList.length) {
    send("youtube", (ownerName ? ownerName + ": " : "") + "шортсы на канале не найдены", { percent: 89 });
  }
  for (let si = 0; si < shortsList.length; si++) {
    try {
      await watchOne(shortsList[si], true, true);
    } catch (e) {
      stats.errors++;
      send("youtube", (ownerName ? ownerName + ": " : "") + "Шортс " + (si + 1) + "/" + shortsList.length + ": пропуск — " + (e instanceof Error ? e.message : String(e)), { percent: 90 });
    }
    if (si < shortsList.length - 1) await openChannelTab(page, channelUrl, "shorts");
  }

  const plannedLong = longList.length;
  const plannedShorts = shortsList.length;
  send("youtube", (ownerName ? ownerName + ": " : "") +
    "итог: длинных " + stats.long + "/" + plannedLong +
    ", шортсов " + stats.shorts + "/" + plannedShorts +
    (stats.skippedLiked ? ", пропуск (лайк): " + stats.skippedLiked : "") +
    (stats.errors ? ", ошибок: " + stats.errors : "") +
    " → следующий канал", { percent: 92 });
  await leaveVideoPlayer(page, channelUrl);
  if (stats.errors > 0) {
    throw new Error("Не весь контент просмотрен: ошибок " + stats.errors + ", успешно " + count + ".");
  }
  return count;
}

function sendMeshChannel(target, channelUrl) {
  if (channelUrl && target && target.ownerProfileId) {
    send("mesh", "Канал: " + channelUrl, { channelUrl, meshOwner: target.ownerProfileId });
  }
}

const CHANNEL_MESH_TIMEOUT_MS = 28 * 60 * 1000;

async function watchChannelMeshWithTimeout(page, target, filter, viewerProfileId) {
  let timer = null;
  try {
    return await Promise.race([
      watchChannelMesh(page, target, filter, viewerProfileId),
      new Promise((_, reject) => {
        timer = setTimeout(() => {
          page.close().catch(() => {}).finally(() => {
            reject(new Error("Таймаут канала (28 мин): страница закрыта, операция остановлена."));
          });
        }, CHANNEL_MESH_TIMEOUT_MS);
      })
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}

/** Канал из базы / по ID / по имени / поиск → сегодняшние видео и шортс. Свой канал — только лайки. */
async function watchChannelMesh(page, target, filter, viewerProfileId) {
  const fullTitle = String(target.searchFullTitle || target.title || "").trim();
  const videoId = String(target.videoId || extractVideoId(target.searchUrl || "")).trim();
  const keys = parseKeywordList(target.searchKeys, target.title || fullTitle);
  const owner = String(target.ownerName || "").trim();
  const orientUrl = videoId ? ("https://www.youtube.com/watch?v=" + videoId) : (target.searchUrl || "");
  const ownerPid = String(target.ownerProfileId || "").trim().toLowerCase();
  const viewerPid = String(viewerProfileId || "").trim().toLowerCase();
  const isOwn = ownerPid && viewerPid && ownerPid === viewerPid;

  let channelUrl = normalizeChannelUrl(target.channelUrl || "");
  let skipId = videoId;
  let anchorViews = 0;

  if (channelUrl || owner) {
    const resolved = await resolveChannelUrl(page, owner, channelUrl);
    if (resolved) {
      channelUrl = resolved;
      send("youtube", (owner ? owner + ": " : "") + "канал подтверждён", { percent: 42 });
      sendMeshChannel(target, channelUrl);
      const extra = await watchTodayOnChannel(page, channelUrl, skipId, owner, isOwn, target.knownVideos || [], target.searchFullTitle || target.title || "");
      if (!isOwn && extra < 1) throw new Error("Канал открыт, но ни один ролик не был просмотрен.");
      return extra;
    }
  }

  if (videoId) {
    send("youtube", (owner ? owner + ": " : "") + "открываю по ID из базы…", { percent: 44 });
    await openVideoDirect(page, videoId, false);
    if (isOwn) await quickLikeVideo(page);
    else await waitForVideoEnd(page);
    anchorViews = 1;
    channelUrl = normalizeChannelUrl(await extractChannelUrlFromPage(page));
    sendMeshChannel(target, channelUrl);
    if (channelUrl) {
      const extra = await watchTodayOnChannel(page, channelUrl, videoId, owner, isOwn, target.knownVideos || [], target.searchFullTitle || target.title || "");
      return anchorViews + extra;
    }
  }

  if (!fullTitle && !keys.length && !videoId) {
    throw new Error("Нет данных для канала «" + owner + "» (ссылка, ID, имя, ключи).");
  }

  send("youtube", (owner ? owner + ": " : "") + (isOwn ? "свой канал — поиск и лайки…" : "поиск якорного видео…"), { percent: 48 });
  await openFoundVideoMesh(page, target.title || fullTitle, orientUrl, {
    searchKeys: target.searchKeys || keys.join("\n"),
    searchFullTitle: fullTitle
  });
  if (isOwn) await quickLikeVideo(page);
  else await waitForVideoEnd(page);
  anchorViews = 1;

  if (!channelUrl) channelUrl = normalizeChannelUrl(await extractChannelUrlFromPage(page));
  sendMeshChannel(target, channelUrl);

  let extra = 0;
  if (channelUrl) {
    extra = await watchTodayOnChannel(page, channelUrl, videoId || extractVideoId(page.url()), owner, isOwn, target.knownVideos || [], target.searchFullTitle || target.title || "");
  } else {
    send("youtube", "Ссылку на канал не нашёл — только якорное видео.", { percent: 92 });
  }
  return anchorViews + extra;
}

async function skipAdIfPossible(page) {
  const skip = page.locator([
    ".ytp-ad-skip-button",
    ".ytp-ad-skip-button-modern",
    ".ytp-skip-ad-button",
    "button.ytp-ad-skip-button-modern",
    "button[id*='skip' i]"
  ].join(", "));
  if (await visible(skip, 400)) await skip.first().click({ timeout: 1000 }).catch(() => {});
}

async function ensureVideoPlaying(page) {
  await dismissYouTubeOverlays(page);
  await skipAdIfPossible(page);
  // Клик по плееру (часто нужно для старта автоплея)
  const player = page.locator("#movie_player, .html5-video-player, ytd-player");
  if (await visible(player, 1500)) {
    await player.first().click({ position: { x: 40, y: 40 }, timeout: 2000 }).catch(() => {});
  }
  const play = page.locator([
    "button.ytp-large-play-button",
    "button.ytp-play-button[aria-label*='Play' i]",
    "button.ytp-play-button[aria-label*='Смотр' i]",
    "button.ytp-play-button[aria-label*='Воспроиз' i]",
    "button.ytp-play-button[title*='Play' i]",
    "button.ytp-play-button[title*='Смотр' i]"
  ].join(", "));
  if (await visible(play, 1200)) await play.first().click({ timeout: 2000 }).catch(() => {});
  await page.evaluate(() => {
    const v = document.querySelector("video.html5-main-video") || document.querySelector("#movie_player video") || document.querySelector("video");
    if (!v) return false;
    try { v.muted = false; } catch (_) {}
    try {
      const p = v.play();
      if (p && typeof p.catch === "function") p.catch(() => {});
    } catch (_) {}
    return true;
  }).catch(() => {});
}

async function readPlaybackState(page) {
  return page.evaluate(() => {
    const v = document.querySelector("video.html5-main-video") || document.querySelector("#movie_player video") || document.querySelector("video");
    if (!v) return { missing: true };
    return {
      missing: false,
      current: Number(v.currentTime) || 0,
      duration: Number(v.duration) || 0,
      ended: !!v.ended,
      paused: !!v.paused,
      readyState: v.readyState
    };
  }).catch(() => ({ missing: true }));
}

function formatClock(seconds) {
  const total = Math.max(0, Math.floor(seconds || 0));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return m + ":" + String(s).padStart(2, "0");
}

async function waitForVideoEnd(page) {
  if (/\/shorts\//.test(page.url())) {
    await watchShortOnce(page, "");
    return;
  }
  await dismissYouTubeOverlays(page);
  await page.waitForSelector("video.html5-main-video, #movie_player video, video", { timeout: 90000 }).catch(() => {});
  for (let i = 0; i < 8; i++) {
    await ensureVideoPlaying(page);
    const st = await readPlaybackState(page);
    if (!st.missing && !st.paused && (st.current > 0.2 || st.readyState >= 2)) break;
    await new Promise(r => setTimeout(r, 800));
  }

  const alreadyLiked = await isVideoLiked(page);
  if (alreadyLiked) send("youtube", "Лайк уже стоит — всё равно смотрю видео до конца.", { percent: 86 });

  let duration = 0;
  const readyDeadline = Date.now() + 120000;
  while (Date.now() < readyDeadline) {
    await skipAdIfPossible(page);
    const state = await readPlaybackState(page);
    if (!state.missing && isFinite(state.duration) && state.duration > 1) {
      duration = state.duration;
      break;
    }
    await ensureVideoPlaying(page);
    await new Promise(r => setTimeout(r, 1000));
  }
  if (!(duration > 1)) throw new Error("Видео открылось, но воспроизведение не стартовало (нет длительности). Профиль оставлен открытым — нажмите Play вручную.");

  // Лайк в случайный момент в последние 30 секунд (или раньше, если ролик короче)
  const likeWindow = Math.min(30, Math.max(1, Math.floor(duration - 0.5)));
  const secondsBeforeEnd = 1 + Math.floor(Math.random() * likeWindow);
  const likeAt = Math.max(0, duration - secondsBeforeEnd);
  let liked = alreadyLiked;
  let likeAttempted = alreadyLiked;
  send("youtube", `Смотрю до конца (${formatClock(duration)}). Лайк ~за ${secondsBeforeEnd} с до конца…`, { percent: 85 });

  const hardLimitMs = Math.min(4 * 60 * 60 * 1000, Math.max(3 * 60 * 1000, duration * 1000 + 8 * 60 * 1000));
  const startedAt = Date.now();
  let lastReport = 0;
  let stuckAt = -1;
  let stuckSince = Date.now();
  let maxSeen = 0;

  while (true) {
    if (Date.now() - startedAt > hardLimitMs) {
      throw new Error("Видео не завершилось за отведённое время. Проверьте рекламу или паузу в открытом профиле.");
    }
    await skipAdIfPossible(page);
    const state = await readPlaybackState(page);
    if (state.missing) {
      await ensureVideoPlaying(page);
      await new Promise(r => setTimeout(r, 1500));
      continue;
    }
    const curRaw = Number(state.current) || 0;
    if (maxSeen >= duration * 0.85 && curRaw < maxSeen - 2) {
      if (!liked && !likeAttempted) {
        likeAttempted = true;
        liked = await tryLikeCurrentVideo(page, "Видео");
      }
      send("youtube", "Видео повторилось — выхожу.", { percent: 95 });
      return;
    }
    maxSeen = Math.max(maxSeen, curRaw);
    if (!liked && !likeAttempted && (state.current || 0) >= likeAt) {
      likeAttempted = true;
      liked = await tryLikeCurrentVideo(page, "Видео");
    }
    if (state.ended || (state.duration > 1 && state.current >= state.duration - 0.75)) {
      if (!liked && !likeAttempted) {
        likeAttempted = true;
        liked = await tryLikeCurrentVideo(page, "Видео");
      }
      send("youtube", "Видео закончилось.", { percent: 95 });
      return;
    }
    if (state.paused) await ensureVideoPlaying(page);

    const cur = Math.floor(state.current || 0);
    if (cur === stuckAt) {
      if (Date.now() - stuckSince > 12000) {
        send("youtube", "Просмотр завис — снова запускаю Play…", { percent: 86 });
        await ensureVideoPlaying(page);
        stuckSince = Date.now();
      }
    } else {
      stuckAt = cur;
      stuckSince = Date.now();
    }

    if (Date.now() - lastReport > 8000) {
      lastReport = Date.now();
      const left = Math.max(0, (state.duration || duration) - (state.current || 0));
      const pct = 85 + Math.min(10, Math.floor(((state.current || 0) / (state.duration || duration)) * 10));
      send("youtube", `Идёт просмотр… ${formatClock(state.current)} / ${formatClock(state.duration || duration)} · осталось ~${formatClock(left)}`, { percent: pct });
    }
    await new Promise(r => setTimeout(r, 1500));
  }
}

async function tryLikeCurrentVideo(page, mediaLabel) {
  try {
    await likeCurrentVideo(page);
    return true;
  } catch (e) {
    const msg = e instanceof Error ? e.message : String(e);
    send("youtube", (mediaLabel || "Ролик") + ": лайк не поставлен, просмотр продолжается — " + msg, { percent: 96 });
    return false;
  }
}

async function isVideoLiked(page) {
  return page.evaluate(() => {
    const unlikeNeedles = [/unlike/i, /больше не нрав/i, /remove.*like/i, /убрать.*нрав/i, /remove from liked/i];
    const buttons = Array.from(document.querySelectorAll("button, [role='button'], ytd-like-button-renderer, like-button-view-model button"));
    for (const btn of buttons) {
      const label = `${btn.getAttribute("aria-label") || ""} ${btn.getAttribute("title") || ""} ${btn.textContent || ""}`.toLowerCase();
      const pressed = (btn.getAttribute("aria-pressed") || "").toLowerCase();
      if (unlikeNeedles.some(r => r.test(label))) return true;
      if (pressed === "true" && /like|нрав/i.test(label) && !/dislike|не нрав/i.test(label)) return true;
    }
    const shortsLiked = document.querySelector(
      "ytd-reel-video-renderer like-button-view-model button[aria-pressed='true'], " +
      "reel-like-button button[aria-pressed='true'], " +
      "like-button-view-model button[aria-pressed='true']"
    );
    return !!shortsLiked;
  }).catch(() => false);
}

async function likeCurrentVideo(page) {
  send("youtube", "Ставлю лайк…", { percent: 96 });
  await page.evaluate(() => {
    const actions = document.querySelector("#actions, #actions-inner, ytd-menu-renderer, like-button-view-model");
    if (actions && actions.scrollIntoView) actions.scrollIntoView({ block: "center", inline: "nearest" });
  }).catch(() => {});
  await new Promise(r => setTimeout(r, 800));

  if (await isVideoLiked(page)) {
    send("youtube", "Лайк уже стоит.", { percent: 97 });
    return;
  }

  const candidates = [
    page.locator("reel-like-button button, ytd-reel-video-renderer like-button-view-model button").first(),
    page.locator("like-button-view-model button").first(),
    page.locator("ytd-segmented-like-dislike-button-renderer like-button-view-model button").first(),
    page.locator("#top-level-buttons-computed like-button-view-model button").first(),
    page.locator("#segmented-like-button button").first(),
    page.locator("button[aria-label*='like this video' i]").first(),
    page.locator("button[aria-label^='Нравится' i]").first(),
    page.locator("button[aria-label*='Нравится' i]:not([aria-label*='Не нравится' i])").first(),
    page.locator("ytd-toggle-button-renderer#like-button button, #like-button button").first()
  ];

  let clicked = false;
  for (const btn of candidates) {
    if (!(await visible(btn, 1200))) continue;
    const label = ((await btn.getAttribute("aria-label").catch(() => "")) || "").toLowerCase();
    if (/dislike|не нрав/.test(label) && !/^нрав|^like/.test(label)) continue;
    await btn.scrollIntoViewIfNeeded().catch(() => {});
    await btn.click({ timeout: 5000 }).catch(async () => {
      await btn.click({ force: true }).catch(() => {});
    });
    clicked = true;
    break;
  }

  if (!clicked) {
    clicked = await page.evaluate(() => {
      const nodes = Array.from(document.querySelectorAll("button, [role='button']"));
      const like = nodes.find(btn => {
        const label = `${btn.getAttribute("aria-label") || ""} ${btn.getAttribute("title") || ""}`.toLowerCase();
        if (!label) return false;
        if (/dislike|не нрав/.test(label)) return false;
        return /(^|\s)like\b|like this|нрав/.test(label);
      });
      if (!like) return false;
      like.click();
      return true;
    }).catch(() => false);
  }

  if (!clicked) throw new Error("Не найдена кнопка «Нравится». Профиль оставлен открытым — поставьте лайк вручную.");

  await new Promise(r => setTimeout(r, 1500));
  if (!(await isVideoLiked(page))) {
    throw new Error("Лайк не подтвердился. Проверьте вход в аккаунт YouTube в профиле Dolphin. Профиль оставлен открытым.");
  }
  send("youtube", "Лайк поставлен.", { percent: 98 });
}

async function setThumbnail(page, path) {
  if (!path) return;
  const inputs = page.locator("input[type=file]");
  const count = await inputs.count();
  for (let i = 0; i < count; i++) {
    const accept = (await inputs.nth(i).getAttribute("accept").catch(() => "")) || "";
    if (/image|jpeg|png/i.test(accept)) {
      await setInputFilesRobust(page, inputs.nth(i), path, "youtube");
      return;
    }
  }
  throw new Error("YouTube не показал поле превью. Проверьте, доступно ли пользовательское превью на канале.");
}

async function progressText(page) {
  const selectors = [
    "ytcp-video-upload-progress #progress-label",
    "ytcp-video-upload-progress span",
    "#dialog .progress-label",
    "[class*='progress-label']"
  ];
  for (const selector of selectors) {
    const x = page.locator(selector);
    if (await x.count()) {
      const value = (await x.first().innerText().catch(() => "")).trim();
      if (value) return value;
    }
  }
  return "YouTube обрабатывает файл…";
}

async function answerRequiredChecks(page) {
  // Аудитория: не для детей
  const notKids = await firstVisible([
    page.locator("tp-yt-paper-radio-button[name='VIDEO_MADE_FOR_KIDS_NOT_MFK']"),
    page.locator("#RADIO_BUTTON_OFF"),
    page.getByRole("radio", { name: /не для детей|not made for kids|not for kids/i }),
    page.getByText(/Нет,\s*это видео не для детей|No,\s*it'?s not made for kids/i)
  ], 700);
  if (notKids) {
    await notKids.click({ timeout: 3000 }).catch(() => {});
    await page.waitForTimeout(250);
  }

  // Использование ИИ → Нет (только если блок виден)
  const aiBlockVisible = await visible(page.getByText(/Использование ИИ|Was AI used|altered content|synthetic media|искусственн/i), 500);
  if (aiBlockVisible) {
    const aiNo = await page.evaluate(() => {
      const markers = /Использование ИИ|Was AI used|altered content|synthetic media|искусственн/i;
      const nodes = Array.from(document.querySelectorAll("h1,h2,h3,div,span,ytcp-form-section,ytcp-video-metadata-editor-section"));
      let root = null;
      for (const n of nodes) {
        const t = (n.innerText || "").trim();
        if (t && markers.test(t) && t.length < 400) { root = n.closest("ytcp-form-section, section, div") || n.parentElement; break; }
      }
      if (!root) root = document.body;
      const radios = Array.from(root.querySelectorAll("tp-yt-paper-radio-button, [role='radio'], input[type='radio']"));
      for (const r of radios) {
        const label = `${r.getAttribute("name") || ""} ${r.getAttribute("aria-label") || ""} ${r.innerText || ""}`.trim();
        if (/^нет$/i.test((r.innerText || "").trim()) || /\bno\b/i.test(label) && !/not made for kids|не для детей/i.test(label)) {
          r.click();
          return true;
        }
      }
      // Подпись «Нет» рядом с radio
      const labels = Array.from(root.querySelectorAll("label, span, div, tp-yt-paper-radio-button"));
      for (const el of labels) {
        const t = (el.innerText || "").trim();
        if (t === "Нет" || t === "No") {
          const clickable = el.closest("tp-yt-paper-radio-button, [role='radio'], label") || el;
          clickable.click();
          return true;
        }
      }
      return false;
    }).catch(() => false);
    if (aiNo) {
      send("youtube", "ИИ: выбрано «Нет».", { percent: 42 });
      await page.waitForTimeout(250);
    }
  }
}

/** Корень диалога загрузки — чтобы не кликать «Sort by Date» в таблице Studio за модалкой. */
function uploadDialog(page) {
  return page.locator("ytcp-uploads-dialog, ytcp-upload-dialog, #dialog.ytcp-uploads-dialog, tp-yt-paper-dialog.ytcp-uploads-dialog").first();
}

async function isVisibilityStep(page) {
  const dlg = uploadDialog(page);
  const markers = [
    dlg.locator("ytcp-visibility-scheduler"),
    dlg.locator("tp-yt-paper-radio-button[name='SCHEDULE'], #schedule-radio-button"),
    dlg.getByText(/Сохранить или опубликовать|Save or publish/i),
    dlg.getByText(/^Запланировать публикацию$|^Schedule publication$|^Schedule$/i),
    dlg.getByRole("radio", { name: /Schedule publication|Schedule for later|^Schedule$|Запланировать/i })
  ];
  for (const marker of markers) {
    if (await marker.first().isVisible().catch(() => false)) return true;
  }
  return page.evaluate(() => {
    const root = document.querySelector("ytcp-uploads-dialog");
    if (!root) return false;
    const selected = root.querySelector("button.selected, .step.selected, [aria-selected='true']");
    const stepLabel = ((selected && selected.innerText) || "").replace(/\s+/g, " ").trim();
    if (/^Доступ$|^Visibility$/i.test(stepLabel)) return true;
    const text = (root.innerText || "").slice(0, 5000);
    return /Сохранить или опубликовать|Save or publish|Choose when to publish|Select visibility/i.test(text)
      && /Запланировать|Schedule publication|^Schedule$|Schedule for later|Открытый доступ|Public|Private|Unlisted/i.test(text);
  }).catch(() => false);
}

async function advance(page) {
  for (let step = 1; step <= 10; step++) {
    assertPageOpen(page);
    await answerRequiredChecks(page);
    if (await isVisibilityStep(page)) {
      send("youtube", "Экран видимости (Доступ).", { percent: 75 });
      return;
    }
    const next = page.locator("#next-button");
    const doneIsVisible = await visible(page.locator("#done-button"), 800);
    const nextIsVisible = await next.first().isVisible().catch(() => false);
    if (doneIsVisible && !nextIsVisible) return;
    const button = await waitEnabled(next, 180000, "Кнопка «Далее» / Next недоступна. Проверьте обязательные поля (аудитория / использование ИИ).");
    await button.click();
    send("youtube", `Этап YouTube ${step}…`, { percent: Math.min(78, 43 + step * 6) });
    await page.waitForTimeout(900);
    if (await isVisibilityStep(page)) {
      send("youtube", "Экран видимости (Доступ).", { percent: 75 });
      return;
    }
  }
  if (await isVisibilityStep(page)) return;
  throw new Error("Не удалось дойти до экрана видимости YouTube. Профиль оставлен открытым для проверки.");
}

async function firstVisible(candidates, timeout = 2500) {
  for (const candidate of candidates) {
    if (await visible(candidate, timeout)) return candidate.first();
  }
  return null;
}

function pad(value) { return String(value).padStart(2, "0"); }

function automaticSchedule() {
  const statePath = localStatePath("youtube-schedule.json");
  const now = new Date();
  const minimum = new Date(now.getTime() + 2 * 60 * 60 * 1000);
  let next = null;
  try {
    const saved = JSON.parse(fs.readFileSync(statePath, "utf8"));
    const parsed = new Date(saved.next);
    if (Number.isFinite(parsed.getTime())) next = parsed;
  } catch (_) {}
  if (!next || next < minimum) {
    next = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, 12, 0, 0, 0);
  }
  if (next.getHours() >= 22) next = new Date(next.getFullYear(), next.getMonth(), next.getDate() + 1, 9, 0, 0, 0);
  const assigned = new Date(next);
  next = new Date(next.getTime() + 30 * 60 * 1000);
  if (next.getHours() >= 22) next = new Date(next.getFullYear(), next.getMonth(), next.getDate() + 1, 9, 0, 0, 0);
  const temp = statePath + ".tmp";
  fs.writeFileSync(temp, JSON.stringify({ next: next.toISOString() }), "utf8");
  fs.renameSync(temp, statePath);
  return { date: `${assigned.getFullYear()}-${pad(assigned.getMonth()+1)}-${pad(assigned.getDate())}`, time: `${pad(assigned.getHours())}:${pad(assigned.getMinutes())}`, automatic: true };
}

function resolveSchedule() {
  const match = String(job.title || "").match(/^\s*\[(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2})\]\s*(.+)$/s);
  if (match) {
    job.title = match[3].trim();
    return { date: match[1], time: match[2], automatic: false };
  }
  if (job.scheduleDate && job.scheduleTime) return { date: job.scheduleDate, time: job.scheduleTime, automatic: false };
  return automaticSchedule();
}

/** Выбрать «Schedule» / «Запланировать публикацию» на экране видимости. */
async function openScheduleOption(page) {
  await answerRequiredChecks(page);
  const dlg = uploadDialog(page);
  await dlg.waitFor({ state: "visible", timeout: 15000 }).catch(() => {});

  const scheduleRadio = await firstVisible([
    dlg.locator("tp-yt-paper-radio-button[name='SCHEDULE']"),
    dlg.locator("#schedule-radio-button"),
    dlg.locator("#second-container-radio-button"),
    dlg.getByRole("radio", { name: /Schedule publication|Schedule for later|^Schedule$|Schedule as|Запланировать/i }),
    page.locator("ytcp-uploads-dialog tp-yt-paper-radio-button[name='SCHEDULE']"),
    page.locator("ytcp-uploads-dialog #schedule-radio-button")
  ], 3000);
  if (scheduleRadio) {
    const ariaChecked = await scheduleRadio.getAttribute("aria-checked").catch(() => null);
    const checked = await scheduleRadio.getAttribute("checked").catch(() => null);
    if (ariaChecked !== "true" && checked === null) {
      await scheduleRadio.click({ timeout: 3000, force: true }).catch(() => scheduleRadio.click({ force: true }));
    }
    await page.waitForTimeout(700);
    return;
  }

  const opened = await page.evaluate(() => {
    const root = document.querySelector("ytcp-uploads-dialog");
    if (!root) return null;
    const done = root.querySelector("#done-button, ytcp-button#done-button");
    const match = (text) => {
      const t = String(text || "").replace(/\s+/g, " ").trim();
      if (!t || t.length > 80) return false;
      if (/^Schedule publication$/i.test(t)) return true;
      if (/^Schedule for later$/i.test(t)) return true;
      if (/^Schedule$/i.test(t)) return true;
      if (/^Schedule as /i.test(t)) return true;
      if (/^Запланировать/i.test(t)) return true;
      return false;
    };
    for (const el of root.querySelectorAll("tp-yt-paper-radio-button, [role='radio']")) {
      const t = (el.innerText || el.getAttribute("aria-label") || "").replace(/\s+/g, " ").trim();
      if (match(t)) {
        el.click();
        return "radio:" + t.slice(0, 40);
      }
    }
    const scheduler = root.querySelector("ytcp-visibility-scheduler");
    if (scheduler) {
      for (const el of scheduler.querySelectorAll("tp-yt-paper-radio-button, [role='radio'], label")) {
        const t = (el.innerText || el.getAttribute("aria-label") || "").replace(/\s+/g, " ").trim();
        if (match(t)) {
          (el.closest("tp-yt-paper-radio-button, [role='radio']") || el).click();
          return "scheduler:" + t.slice(0, 40);
        }
      }
    }
    for (const el of root.querySelectorAll("div, span, tp-yt-paper-item, ytcp-visibility-scheduler *")) {
      if (done && (el === done || done.contains(el))) continue;
      const t = (el.innerText || el.textContent || "").replace(/\s+/g, " ").trim();
      if (!match(t)) continue;
      if (el.closest("#footer, .footer")) continue;
      const target = el.closest("tp-yt-paper-radio-button, [role='radio'], tp-yt-paper-item") || el;
      target.click();
      return t.slice(0, 60);
    }
    return null;
  }).catch(() => null);

  if (!opened) throw new Error("Не найден блок Schedule / «Запланировать публикацию» (EN/RU).");
  await page.waitForTimeout(700);
}

async function fillScheduleDateTime(page, isoDate, time24) {
  if (!isoDate || !time24) return;
  const dlg = uploadDialog(page);
  const dateInput = dlg.locator("input[type='date'], ytcp-date-picker input, tp-yt-paper-input input").first();
  if (await visible(dateInput, 1500)) {
    await dateInput.fill(isoDate).catch(() => {});
  }
  const timeInput = dlg.locator("input[type='time']").first();
  if (await visible(timeInput, 1500)) {
    await timeInput.fill(time24).catch(() => {});
  }
  await page.evaluate(({ isoDate, time24 }) => {
    const root = document.querySelector("ytcp-uploads-dialog");
    if (!root) return;
    for (const inp of root.querySelectorAll("input")) {
      const label = `${inp.getAttribute("aria-label") || ""} ${inp.placeholder || ""}`.toLowerCase();
      if (label.includes("date") || label.includes("дата")) {
        inp.focus();
        inp.value = isoDate;
        inp.dispatchEvent(new Event("input", { bubbles: true }));
        inp.dispatchEvent(new Event("change", { bubbles: true }));
      }
      if (label.includes("time") || label.includes("время")) {
        inp.focus();
        inp.value = time24;
        inp.dispatchEvent(new Event("input", { bubbles: true }));
        inp.dispatchEvent(new Event("change", { bubbles: true }));
      }
    }
  }, { isoDate, time24 }).catch(() => {});
  await page.waitForTimeout(400);
}

async function saveAsPrivate(page) {
  await answerRequiredChecks(page);
  const dlg = uploadDialog(page);
  const privateRadio = await firstVisible([
    dlg.locator("tp-yt-paper-radio-button[name='PRIVATE']"),
    dlg.locator("#private-radio-button"),
    dlg.getByRole("radio", { name: /^Private$|^Скрыт|^Limited/i })
  ], 2500);
  if (privateRadio) {
    await privateRadio.click({ force: true }).catch(() => {});
    await page.waitForTimeout(500);
    return;
  }
  const picked = await page.evaluate(() => {
    const root = document.querySelector("ytcp-uploads-dialog");
    if (!root) return false;
    for (const el of root.querySelectorAll("tp-yt-paper-radio-button, [role='radio']")) {
      const t = (el.innerText || el.getAttribute("aria-label") || "").replace(/\s+/g, " ").trim();
      if (/^Private$/i.test(t) || /^Скрыт/i.test(t) || /^Limited/i.test(t)) {
        el.click();
        return true;
      }
    }
    return false;
  }).catch(() => false);
  if (!picked) {
    send("youtube", "Private не найден — оставляю текущий режим видимости.", { percent: 79 });
    return;
  }
  await page.waitForTimeout(500);
}

async function finishUploadVisibility(page, isoDate, time24) {
  try {
    await setSchedule(page, isoDate, time24);
    return await publish(page);
  } catch (e1) {
    const msg = e1 instanceof Error ? e1.message : String(e1);
    send("youtube", `Расписание: ${msg.slice(0, 72)} → сохраняю черновик (Done/Save)…`, { percent: 79 });
    await saveAsPrivate(page).catch(() => {});
    try {
      return await publish(page);
    } catch (e2) {
      const clicked = await clickByText(page, ["^Done$", "^Save$", "^Сохранить$", "^Готово$"], 2500);
      if (clicked) {
        await page.waitForTimeout(1500);
        return "";
      }
      throw e2;
    }
  }
}

async function setSchedule(page, isoDate, time24) {
  let lastErr = null;
  for (let attempt = 1; attempt <= 2; attempt++) {
    try {
      await openScheduleOption(page);
      await fillScheduleDateTime(page, isoDate, time24);
      const label = isoDate && time24 ? `${isoDate} ${time24}` : "по умолчанию YouTube";
      send("youtube", `Schedule / Запланировать (${label})…`, { percent: 80 });
      return true;
    } catch (e) {
      lastErr = e;
      send("youtube", `Расписание: повтор ${attempt}/2…`, { percent: 78 });
      await page.keyboard.press("Escape").catch(() => {});
      await page.waitForTimeout(600);
    }
  }
  throw lastErr || new Error("Не удалось выбрать Schedule / «Запланировать публикацию».");
}

async function publish(page) {
  assertPageOpen(page);
  await answerRequiredChecks(page);
  const dlg = uploadDialog(page);
  const done = dlg.locator("#done-button");
  const scheduleDone = dlg.getByRole("button", { name: /Запланировать публикацию|Schedule publication|^Schedule$|^Done$|^Publish$|^Save$|Сохранить|Готово/i });
  const hadUploadDialog = await dlg.isVisible().catch(() => false);
  const end = Date.now() + 7200000;
  let button = null;
  let lastReport = 0;
  while (Date.now() < end) {
    assertPageOpen(page);
    for (const loc of [done, scheduleDone]) {
      if (!(await loc.count().catch(() => 0))) continue;
      const el = loc.first();
      const disabled = await el.getAttribute("aria-disabled").catch(() => "true");
      const nativeDisabled = await el.isDisabled().catch(() => true);
      if (disabled !== "true" && !nativeDisabled) { button = el; break; }
    }
    if (button) break;
    if (Date.now() - lastReport > 5000) {
      send("youtube", await progressText(page), { percent: 82 });
      lastReport = Date.now();
    }
    await page.waitForTimeout(1000);
  }
  if (!button) throw new Error("Кнопка «Запланировать публикацию» / Schedule не активна — дождитесь загрузки файла или проверьте экран «Доступ».");
  send("youtube", await progressText(page), { percent: 86 });
  await button.click();
  send("youtube", "«Запланировать публикацию» / Schedule нажата…", { percent: 92 });
  const urlCandidates = [
    page.locator("#share-url"),
    page.locator("input[value*='youtu']"),
    page.locator("a[href*='youtu.be'], a[href*='youtube.com/watch']")
  ];
  let url = "";
  let confirmed = false;
  const confirmEnd = Date.now() + 120000;
  while (Date.now() < confirmEnd) {
    const success = page.locator("ytcp-video-share-dialog, ytcp-video-upload-success").first();
    if (await success.count() && await success.isVisible().catch(() => false)) confirmed = true;
    if (hadUploadDialog && !await dlg.isVisible().catch(() => false)) confirmed = true;
    for (const x of urlCandidates) {
      if (await x.count()) {
        url = (await x.first().getAttribute("value").catch(() => "")) || (await x.first().getAttribute("href").catch(() => "")) || "";
        if (url) { confirmed = true; break; }
      }
    }
    if (confirmed) break;
    await page.waitForTimeout(1000);
  }
  if (!confirmed) throw new Error("YouTube не подтвердил сохранение. Повторно не загружайте видео, сначала проверьте открытый профиль вручную.");
  return url;
}

async function dismissAfterPublish(page) {
  for (let i = 0; i < 6; i++) {
    const close = await firstVisible([
      page.locator("ytcp-uploads-dialog #close-button"),
      page.locator("ytcp-video-share-dialog #close-button"),
      page.locator("#close-button"),
      page.getByRole("button", { name: /Close|Закрыть|Done|Готово/i })
    ], 800);
    if (close) {
      await close.click({ timeout: 3000 }).catch(() => {});
      await page.waitForTimeout(400);
    } else {
      await page.keyboard.press("Escape").catch(() => {});
      await page.waitForTimeout(300);
    }
    const dialog = page.locator("ytcp-uploads-dialog, ytcp-video-share-dialog");
    if (!(await dialog.first().isVisible().catch(() => false))) break;
  }
}

function channelIdFromStudioUrl(url) {
  const m = String(url || "").match(/studio\.youtube\.com\/channel\/(UC[\w-]{10,})/i);
  return m ? m[1] : "";
}

/** Реальный UC… из адреса Studio (не плейсхолдер «UC»). */
async function resolveStudioChannelId(page) {
  let id = channelIdFromStudioUrl(page.url());
  if (id) return id;
  await gotoStable(page, "https://studio.youtube.com", { waitUntil: "domcontentloaded", timeout: 90000 }).catch(() => {});
  await page.waitForTimeout(1200);
  id = channelIdFromStudioUrl(page.url());
  if (id) return id;
  id = await page.evaluate(() => {
    const fromHref = (h) => {
      const m = String(h || "").match(/\/channel\/(UC[\w-]{10,})/i);
      return m ? m[1] : "";
    };
    let cid = fromHref(location.href);
    if (cid) return cid;
    for (const el of document.querySelectorAll("a[href*='/channel/UC'], link[href*='/channel/UC']")) {
      cid = fromHref(el.getAttribute("href"));
      if (cid) return cid;
    }
    const meta = document.querySelector("meta[property='og:url'], link[rel='canonical']");
    if (meta) {
      cid = fromHref(meta.getAttribute("content") || meta.getAttribute("href"));
      if (cid) return cid;
    }
    return "";
  }).catch(() => "");
  return id || "";
}

async function pickVideoFileInput(page) {
  const pierced = page.locator(
    "ytcp-uploads-dialog input[type=file], ytcp-uploads-file-picker input[type=file], ytcp-upload-dialog input[type=file], input[type=file][accept*='video']"
  );
  if (await pierced.count().catch(() => 0)) return pierced.first();

  await page.evaluate(() => {
    document.querySelectorAll('input[type=file][data-vb-pick="1"]').forEach(el => el.removeAttribute("data-vb-pick"));
  }).catch(() => {});

  const picked = await page.evaluate(() => {
    const walk = (root, out) => {
      if (!root) return;
      try {
        root.querySelectorAll('input[type=file]').forEach(el => out.push(el));
      } catch (_) {}
      const nodes = root.querySelectorAll ? root.querySelectorAll("*") : [];
      for (const el of nodes) {
        if (el.shadowRoot) walk(el.shadowRoot, out);
      }
    };
    const score = (el) => {
      const name = (el.getAttribute("name") || "").toLowerCase();
      const accept = (el.getAttribute("accept") || "").toLowerCase();
      if (name === "filedata") return -100;
      if (/image/i.test(accept) && !/video/i.test(accept)) return -50;
      let s = 0;
      if (/video/i.test(accept)) s += 40;
      const host = el.getRootNode && el.getRootNode().host;
      const hostTag = (host && host.tagName) || "";
      if (/ytcp-uploads|ytcp-video-dialog|ytcp-upload-dialog/i.test(hostTag)) s += 55;
      if (el.closest("ytcp-uploads-dialog, ytcp-uploads-file-picker, ytcp-video-dialog, ytcp-upload-dialog")) s += 60;
      if (location.hostname.includes("studio.youtube.com")) s += 10;
      if (location.pathname.includes("/videos/upload")) s += 15;
      if (accept) s += 5;
      return s;
    };
    const inputs = [];
    walk(document, inputs);
    let best = null;
    let bestScore = -999;
    for (const el of inputs) {
      const s = score(el);
      if (s > bestScore) { bestScore = s; best = el; }
    }
    if (!best || bestScore < 8) return null;
    best.setAttribute("data-vb-pick", "1");
    return bestScore;
  }).catch(() => null);
  if (picked == null) return null;
  const loc = page.locator('input[type=file][data-vb-pick="1"]');
  if (await loc.count()) return loc.first();
  return null;
}

async function tryPickUploadInput(page) {
  const end = Date.now() + 22000;
  while (Date.now() < end) {
    await waitForUploadDialog(page, 3000).catch(() => {});
    const input = await pickVideoFileInput(page);
    if (input) return input;
    await page.waitForTimeout(600);
  }
  return null;
}

/** Клик «Выбрать файлы» только вместе с page.waitForEvent('filechooser') — иначе зависнет системный диалог. */
async function clickBrowseForFileChooser(page) {
  const zone = page.locator("ytcp-uploads-file-picker, ytcp-uploads-dialog, #upload-area").first();
  if (await zone.isVisible().catch(() => false)) {
    await zone.click({ timeout: 4000 }).catch(() => {});
    return "zone";
  }
  return clickByText(page, [
    "Select files",
    "Выбрать файлы",
    "Choose files",
    "Browse",
    "Обзор",
    "Upload files",
    "Загрузить файлы"
  ], 3500);
}

async function injectFilesViaFileChooser(page, paths) {
  const resolved = paths.map(p => path.resolve(String(p || "")));
  if (!resolved.length || resolved.some(p => !p || !fs.existsSync(p))) {
    throw new Error("Файл не найден: " + resolved.filter(Boolean).join(", "));
  }
  await dismissAfterPublish(page);
  await dismissYouTubeOverlays(page).catch(() => {});
  const channelId = await resolveStudioChannelId(page);
  const uploadUrls = channelId
    ? [
        `https://studio.youtube.com/channel/${channelId}/videos/upload?d=ud`,
        `https://studio.youtube.com/channel/${channelId}/videos/upload`
      ]
    : ["https://studio.youtube.com"];

  for (let attempt = 1; attempt <= 3; attempt++) {
    for (const url of uploadUrls) {
      try {
        await gotoStable(page, url, { waitUntil: "domcontentloaded", timeout: 90000 });
        await page.waitForTimeout(2000);
        await waitForUploadDialog(page, 20000).catch(() => {});
        send("youtube", `Перехват диалога выбора файлов (${resolved.length})…`, { percent: 26 });
        const [chooser] = await Promise.all([
          page.waitForEvent("filechooser", { timeout: 25000 }),
          clickBrowseForFileChooser(page)
        ]);
        await chooser.setFiles(resolved);
        send("youtube", `Файлы переданы (${resolved.length}) через перехват диалога.`, { percent: 28 });
        return true;
      } catch (e) {
        if (attempt >= 3 && url === uploadUrls[uploadUrls.length - 1]) throw e;
      }
    }
    send("youtube", `Диалог файлов: повтор ${attempt}/3…`, { percent: 24 });
    await page.waitForTimeout(1200);
  }
  return false;
}

async function openUploadAndSetFiles(page, filePaths) {
  const paths = (Array.isArray(filePaths) ? filePaths : [filePaths]).map(p => String(p || "")).filter(Boolean);
  if (!paths.length) throw new Error("Нет файлов для передачи.");

  let videoInput = await openUploadForPack(page);
  if (videoInput) {
    await waitForUploadDialog(page);
    await setInputFilesRobust(page, videoInput, paths, "youtube");
    return true;
  }

  send("youtube", "Скрытый input не найден — перехват системного диалога…", { percent: 25 });
  const ok = await injectFilesViaFileChooser(page, paths);
  if (!ok) throw new Error("Не удалось передать файлы в YouTube Studio (ни CDP, ни перехват диалога).");
  return true;
}

async function openUploadViaCreateMenu(page) {
  await gotoStable(page, "https://studio.youtube.com", { waitUntil: "domcontentloaded", timeout: 90000 }).catch(() => {});
  await page.waitForTimeout(1200);
  await dismissYouTubeOverlays(page).catch(() => {});

  const createBtn = await firstVisible([
    page.locator("ytcp-button#create-icon"),
    page.locator("#create-icon"),
    page.locator("ytcp-button[aria-label*='Create' i], ytcp-button[aria-label*='Создать' i]"),
    page.getByRole("button", { name: /^Create$|^Создать$/i })
  ], 5000);
  if (!createBtn) return null;
  await createBtn.click({ timeout: 5000 }).catch(() => {});
  await page.waitForTimeout(800);

  const menuHit = await clickByText(page, [
    "Upload videos",
    "Upload video",
    "Add video",
    "Добавить видео",
    "Загрузить видео",
    "Import video",
    "^Upload$",
    "Загрузить"
  ], 4500);
  if (!menuHit) return null;
  await page.waitForTimeout(1200);
  return tryPickUploadInput(page);
}

/** Открыть «Загрузка видео» — URL канала, Create/Создать → Add/Добавить (EN/RU). */
async function openUploadForPack(page) {
  await dismissAfterPublish(page);
  await dismissYouTubeOverlays(page).catch(() => {});

  const channelId = await resolveStudioChannelId(page);
  const uploadUrls = channelId
    ? [
        `https://studio.youtube.com/channel/${channelId}/videos/upload?d=ud`,
        `https://studio.youtube.com/channel/${channelId}/videos/upload`
      ]
    : ["https://studio.youtube.com"];

  for (let attempt = 1; attempt <= 4; attempt++) {
    for (const url of uploadUrls) {
      await gotoStable(page, url, { waitUntil: "domcontentloaded", timeout: 90000 }).catch(() => {});
      await page.waitForTimeout(attempt === 1 ? 2800 : 1600);
      const input = await tryPickUploadInput(page);
      if (input) {
        send("youtube", "Окно «Загрузка видео» открыто.", { percent: 24 });
        return input;
      }
    }

    const viaMenu = await openUploadViaCreateMenu(page);
    if (viaMenu) {
      send("youtube", "Окно «Загрузка видео» (Создать → Добавить).", { percent: 24 });
      return viaMenu;
    }

    send("youtube", `Окно загрузки: повтор ${attempt}/4…`, { percent: 22 });
    await page.waitForTimeout(1000);
  }
  return null;
}

function normalizeStudioTitle(value) {
  return String(value || "").toLowerCase().replace(/[«»"'`]/g, "").replace(/\s+/g, " ").trim();
}

async function clickByText(page, patterns, timeout = 2500) {
  const hit = await page.evaluate((needles) => {
    const re = needles.map(n => new RegExp(n, "i"));
    const nodes = Array.from(document.querySelectorAll("button, a, [role='button'], [role='menuitem'], ytcp-button, tp-yt-paper-item, span, div"));
    for (const el of nodes) {
      const t = (el.innerText || el.textContent || el.getAttribute("aria-label") || "").replace(/\s+/g, " ").trim();
      if (!t || t.length > 80) continue;
      if (!re.some(r => r.test(t))) continue;
      const clickable = el.closest("button, a, [role='button'], [role='menuitem'], ytcp-button, tp-yt-paper-item") || el;
      clickable.click();
      return t.slice(0, 60);
    }
    return null;
  }, patterns).catch(() => null);
  if (hit) { await page.waitForTimeout(500); return hit; }
  for (const p of patterns) {
    const loc = page.getByRole("button", { name: new RegExp(p, "i") }).first();
    if (await visible(loc, Math.min(timeout, 1200))) {
      await loc.click({ timeout: 3000 }).catch(() => {});
      await page.waitForTimeout(400);
      return p;
    }
    const men = page.getByRole("menuitem", { name: new RegExp(p, "i") }).first();
    if (await visible(men, 600)) {
      await men.click({ timeout: 3000 }).catch(() => {});
      await page.waitForTimeout(400);
      return p;
    }
  }
  return null;
}

async function openStudioContent(page) {
  const channelId = await resolveStudioChannelId(page);
  const urls = channelId
    ? [
        `https://studio.youtube.com/channel/${channelId}/videos/short`,
        `https://studio.youtube.com/channel/${channelId}/videos`,
        "https://studio.youtube.com/"
      ]
    : ["https://studio.youtube.com/"];
  for (const u of urls) {
    await gotoStable(page, u, { waitUntil: "domcontentloaded", timeout: 90000 }).catch(() => {});
    await page.waitForTimeout(1500);
    if (/studio\.youtube\.com/i.test(page.url())) break;
  }
  // Shorts filter / вкладка если есть
  await clickByText(page, ["Shorts", "Шортс", "Короткие"], 1200).catch(() => {});
  await page.waitForTimeout(800);
}

async function openVideoEditorByTitle(page, title) {
  const want = normalizeStudioTitle(title);
  // Поиск в Studio
  const searchBox = await firstVisible([
    page.locator("input#search-input, input[aria-label*='Search' i], input[aria-label*='Поиск' i], ytcp-text-input input, #text-input")
  ], 2000);
  if (searchBox) {
    await searchBox.click({ clickCount: 3 }).catch(() => searchBox.click());
    await searchBox.fill(title.slice(0, 80)).catch(async () => {
      await searchBox.press("Control+A").catch(() => {});
      await searchBox.type(title.slice(0, 80)).catch(() => {});
    });
    await searchBox.press("Enter").catch(() => {});
    await page.waitForTimeout(1500);
  }

  const opened = await page.evaluate((wantTitle) => {
    const norm = (s) => String(s || "").toLowerCase().replace(/[«»"'`]/g, "").replace(/\s+/g, " ").trim();
    const want = norm(wantTitle);
    const rows = Array.from(document.querySelectorAll("ytcp-video-row, ytcp-video-section-entry, #video-title, a#video-title, ytcp-video-list-cell-video"));
    for (const row of rows) {
      const t = norm(row.innerText || row.textContent || row.getAttribute("title") || "");
      if (!t || (!t.includes(want.slice(0, Math.min(24, want.length))) && want.length > 10 && !want.includes(t.slice(0, 24)))) continue;
      const link = row.querySelector("a[href*='/video/'], a[href*='/edit'], #video-title, a") || row;
      link.click();
      return (row.innerText || "").replace(/\s+/g, " ").trim().slice(0, 80);
    }
    // fallback: любой элемент с похожим текстом
    const all = Array.from(document.querySelectorAll("a, ytcp-video-row, #video-title"));
    for (const el of all) {
      const t = norm(el.innerText || el.getAttribute("title") || "");
      if (want && t && (t === want || t.includes(want.slice(0, 30)) || want.includes(t.slice(0, 30)))) {
        el.click();
        return t.slice(0, 80);
      }
    }
    return null;
  }, title).catch(() => null);

  if (!opened) throw new Error("В Studio не найден ролик «" + title.slice(0, 50) + "».");
  await page.waitForTimeout(1200);
  // Если открылся список — жмём Edit / Редактировать
  await clickByText(page, ["^Edit$", "^Редактировать$", "Details", "Подробности"], 1500);
  await page.waitForTimeout(1000);
  return opened;
}

async function selectFirstFrameAsThumbnail(page) {
  // Прокрутить к секции Thumbnail / Значок
  await page.evaluate(() => {
    const needles = [/thumbnail/i, /значок/i, /обложк/i];
    const nodes = Array.from(document.querySelectorAll("h1,h2,h3,ytcp-ve,div,span,ytcp-video-thumbnail-editor,ytcp-thumbnails-compact-editor"));
    for (const el of nodes) {
      const t = (el.innerText || "").replace(/\s+/g, " ").trim();
      if (!t || t.length > 40) continue;
      if (needles.some(r => r.test(t))) { el.scrollIntoView({ block: "center" }); return; }
    }
  }).catch(() => {});
  await page.waitForTimeout(500);

  // Клик по текущему превью / меню
  const thumbArea = await firstVisible([
    page.locator("ytcp-video-thumbnail-editor, ytcp-thumbnails-compact-editor, #thumbnail-editor, ytcp-video-thumbnail-with-info"),
    page.locator("button[aria-label*='Thumbnail' i], button[aria-label*='значок' i], button[aria-label*='облож' i]")
  ], 2000);
  if (thumbArea) await thumbArea.click({ timeout: 3000 }).catch(() => {});
  await page.waitForTimeout(600);

  // Меню: Select from video / Выбрать из видео
  let menu = await clickByText(page, [
    "Select from video",
    "Выбрать из видео",
    "Choose from video",
    "Select a frame"
  ], 2500);

  if (!menu) {
    // иногда нужно открыть меню через «Change» / «Изменить»
    await clickByText(page, ["^Change$", "^Изменить$", "Edit thumbnail", "Изменить значок"], 1500);
    await page.waitForTimeout(400);
    menu = await clickByText(page, ["Select from video", "Выбрать из видео"], 2500);
  }
  if (!menu) throw new Error("Не найдено «Select from video» / «Выбрать из видео» (EN/RU Studio).");

  // Диалог выбора кадра
  await page.waitForTimeout(1200);
  const dialog = page.locator("tp-yt-paper-dialog, ytcp-dialog, [role='dialog']").filter({
    hasText: /Select a frame|Выберите кадр|frame from your video|кадр из видео/i
  }).first();
  await dialog.waitFor({ state: "visible", timeout: 20000 }).catch(() => {});

  const picked = await page.evaluate(() => {
    const root = document.querySelector("tp-yt-paper-dialog, ytcp-dialog, [role='dialog']") || document.body;
    const frames = Array.from(root.querySelectorAll("img, button, [role='button'], ytcp-thumbnail-editor-images img, .thumbnail-image, [class*='thumbnail']"));
    // Первый кликабельный кадр (не иконки меню)
    for (const el of frames) {
      const rect = el.getBoundingClientRect();
      if (rect.width < 40 || rect.height < 40) continue;
      const clickable = el.closest("button, [role='button'], ytcp-thumbnail-editor-images > *, .thumbnail") || el;
      clickable.click();
      return true;
    }
    // запас: первая большая картинка в диалоге
    const imgs = Array.from(root.querySelectorAll("img")).filter(img => img.width >= 40);
    if (imgs[0]) { imgs[0].click(); return true; }
    return false;
  }).catch(() => false);

  if (!picked) {
    // Playwright: клик по первому превью-кадру
    const frameBtn = dialog.locator("img, button, [role='option'], [class*='thumbnail']").first();
    if (await visible(frameBtn, 3000)) await frameBtn.click({ timeout: 4000 }).catch(() => {});
    else throw new Error("Диалог кадров открылся, но первый кадр не выбран.");
  }
  await page.waitForTimeout(500);

  const done = await clickByText(page, ["^Done$", "^Готово$", "^Save$", "^Сохранить$"], 3000);
  if (!done) {
    const doneBtn = page.locator("ytcp-button#done-button, #done-button, button:has-text('Done'), button:has-text('Готово')").first();
    if (await visible(doneBtn, 2000)) await doneBtn.click({ timeout: 4000 }).catch(() => {});
    else throw new Error("Не найдена кнопка Done / Готово после выбора кадра.");
  }
  await page.waitForTimeout(800);

  // Сохранить правки на странице редактора
  await clickByText(page, ["^Save$", "^Сохранить$", "Save changes", "Сохранить изменения"], 2000);
  await page.waitForTimeout(1000);
  // Иногда Save disabled until change — ещё раз Done на диалоге
  await clickByText(page, ["^Done$", "^Готово$"], 800);
}

async function applyShortsThumbFrames(page, titles) {
  const list = (titles || []).map(t => String(t || "").trim()).filter(Boolean);
  if (!list.length) throw new Error("Нет заголовков для смены превью шортсов.");
  send("youtube", "Открываю YouTube Studio…", { percent: 30 });
  await openStudioContent(page);
  for (let i = 0; i < list.length; i++) {
    const title = list[i];
    send("youtube", `Превью ${i + 1}/${list.length}: «${title.slice(0, 40)}»`, {
      percent: Math.min(90, 35 + Math.round((i / list.length) * 55))
    });
    await openStudioContent(page);
    await openVideoEditorByTitle(page, title);
    await selectFirstFrameAsThumbnail(page);
  }
  send("youtube", "Превью шортсов применено.", { percent: 95 });
}

function cleanUploadTitle(raw) {
  return String(raw || "").trim().replace(/\s*[·•]\s*\+\d+\s*$/, "").replace(/\s+\+\d+\s*$/, "").replace(/\s+/g, " ").trim().slice(0, 100);
}

function resolveItemSchedule(item, title) {
  const prevTitle = job.title;
  const prevDate = job.scheduleDate;
  const prevTime = job.scheduleTime;
  job.title = title;
  job.scheduleDate = item.scheduleDate || job.scheduleDate;
  job.scheduleTime = item.scheduleTime || job.scheduleTime;
  const schedule = resolveSchedule();
  job.title = prevTitle;
  job.scheduleDate = prevDate;
  job.scheduleTime = prevTime;
  return schedule;
}

function titleAlreadyOnChannel(existingTitles, title) {
  const want = normalizeStudioTitle(title);
  if (!want) return false;
  return (existingTitles || []).some(t => normalizeStudioTitle(t) === want);
}

async function listExistingStudioTitles(page) {
  await openStudioContent(page);
  return page.evaluate(() => {
    const out = [];
    const seen = new Set();
    const rows = document.querySelectorAll("ytcp-video-row, ytcp-video-section-entry, #video-title, a#video-title");
    for (const row of rows) {
      const t = (row.innerText || row.textContent || row.getAttribute("title") || "").replace(/\s+/g, " ").trim();
      if (!t || t.length < 8 || seen.has(t)) continue;
      seen.add(t);
      out.push(t);
    }
    return out;
  }).catch(() => []);
}

async function filterPackForUpload(page, pack, opts = {}) {
  const draftOnly = opts.draftOnly === true;
  const skipped = [];
  const toUpload = [];
  for (let i = 0; i < pack.length; i++) {
    const item = Object.assign({}, pack[i], { _origIndex: i });
    const title = cleanUploadTitle(item.title);
    if (!title) continue;
    if (String(item.publishedVideoId || "").trim() || String(item.publishedUrl || "").trim()) {
      skipped.push({ item, reason: "уже в базе (PublishedUrl/Id)" });
      continue;
    }
    toUpload.push(item);
  }
  if (draftOnly) return { toUpload, skipped };
  // Пачку назначили вручную — не сканируем Studio (медленно и ложно режет шаблоны из банка).
  return { toUpload, skipped };
}

/** После «Запланировать» Studio иногда закрывает диалог — открываем снова через панель очереди. */
async function ensureUploadDialogOpen(page) {
  if (await uploadDialog(page).isVisible().catch(() => false)) return true;
  const reopened = await page.evaluate(() => {
    const pick = (nodes) => {
      for (const el of nodes) {
        el.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
        if (document.querySelector("ytcp-uploads-dialog")) return true;
      }
      return false;
    };
    const panel = document.querySelector("ytcp-multi-progress-panel, ytcp-multi-progress-monitor");
    if (panel) {
      const rows = panel.querySelectorAll("ytcp-uploads-file-picker-item, ytcp-ve, [role='listitem']");
      if (pick(rows)) return true;
    }
    for (const el of document.querySelectorAll("ytcp-uploads-file-picker-item, ytcp-multi-progress-monitor ytcp-ve")) {
      el.click();
      if (document.querySelector("ytcp-uploads-dialog")) return true;
    }
    return false;
  }).catch(() => false);
  if (reopened) {
    await page.waitForTimeout(1200);
    return uploadDialog(page).isVisible().catch(() => false);
  }
  return false;
}

/** Состояние пачки: угол «Загрузка завершена», «Загружено: 100%», диалог, picker. */
async function scanUploadProgressState(page) {
  return page.evaluate(() => {
    const fullText = (document.body && document.body.innerText) || "";
    const completedHeader = /Загрузка завершена|Upload complete|Uploads complete|All uploads complete/i.test(fullText);
    const at100 = Math.max(
      (fullText.match(/Загружено:\s*100\s*%/gi) || []).length,
      (fullText.match(/Uploaded:\s*100\s*%/gi) || []).length,
      (fullText.match(/Upload complete/gi) || []).length
    );
    let maxPct = 0;
    for (const m of fullText.matchAll(/(\d{1,3})\s*%/g)) {
      const p = Number(m[1]);
      if (p > maxPct) maxPct = p;
    }
    let itemCount = document.querySelectorAll("ytcp-uploads-file-picker-item").length;
    const pickerItems = itemCount;
    const panel = document.querySelector("ytcp-multi-progress-panel, ytcp-multi-progress-monitor");
    if (panel) {
      const rows = panel.querySelectorAll("ytcp-ve, [role='listitem'], ytcp-upload-progress-item, .progress-item");
      itemCount = Math.max(itemCount, rows.length);
      if (rows.length === 0) {
        const lines = (panel.innerText || "").split("\n").filter(l => /100\s*%|\.mp4|\.mov|Reels|_0/i.test(l));
        itemCount = Math.max(itemCount, lines.length);
      }
    }
    itemCount = Math.max(itemCount, at100);
    const dlgOpen = !!document.querySelector("ytcp-uploads-dialog, ytcp-upload-dialog");
    return { itemCount, at100, maxPct, completedHeader, dlgOpen, pickerItems };
  }).catch(() => ({ itemCount: 0, at100: 0, maxPct: 0, completedHeader: false, dlgOpen: false, pickerItems: 0 }));
}

async function countUploadQueueItems(page) {
  const s = await scanUploadProgressState(page);
  return Math.max(s.itemCount, s.at100, s.pickerItems);
}

/** Ждёт 100% без жёсткого лимита; ошибка только если нет прогресса 20+ мин. */
async function waitForUploadsReady(page, expectedCount, fileLabels) {
  const stallMs = 20 * 60 * 1000;
  let lastSig = "";
  let lastMove = Date.now();
  send("youtube", `Жду загрузку ${expectedCount} файлов (без лимита по времени)…`, { percent: 30 });

  while (true) {
    assertPageOpen(page);
    const stat = await scanUploadProgressState(page);
    const uploaded = stat.at100 >= expectedCount
      || (stat.completedHeader && stat.at100 >= Math.max(1, expectedCount - 1))
      || (stat.maxPct >= 99 && Math.max(stat.itemCount, stat.at100) >= expectedCount);

    if (uploaded) {
      send("youtube", `Загрузка ${expectedCount}/${expectedCount} на 100% — заголовки и расписание…`, { percent: 88 });
      return stat;
    }

    const seen = Math.max(stat.itemCount, stat.at100, stat.pickerItems);
    if (seen > 0 || stat.maxPct > 0) {
      send("youtube", `Передача: ${seen} рол., ${stat.at100} на 100%, макс. ${stat.maxPct}%…`, {
        percent: 32 + Math.min(50, Math.round(stat.maxPct * 0.5))
      });
    } else {
      send("youtube", "Жду появления роликов в Studio…", { percent: 28 });
    }

    const sig = `${seen}|${stat.at100}|${stat.maxPct}|${stat.completedHeader}`;
    if (sig !== lastSig) { lastSig = sig; lastMove = Date.now(); }
    else if (Date.now() - lastMove > stallMs) {
      const name = (fileLabels && fileLabels[stat.at100]) || "?";
      throw new Error(`Загрузка зависла ${Math.round(stallMs / 60000)} мин без прогресса (${stat.at100}/${expectedCount} на 100%). «${String(name).slice(0, 40)}». Профиль оставлен открытым.`);
    }
    await page.waitForTimeout(2000);
  }
}

async function waitForUploadQueue(page, expectedCount) {
  const stat = await scanUploadProgressState(page);
  if (stat.at100 >= expectedCount || stat.itemCount >= expectedCount || stat.pickerItems >= expectedCount) {
    return Math.max(stat.itemCount, stat.at100, expectedCount);
  }
  return expectedCount;
}

async function openUploadItemFromProgressPanel(page, index) {
  send("youtube", `Открываю ролик ${index + 1} для настройки…`, { percent: 42 });
  if (await uploadDialog(page).isVisible().catch(() => false)) return true;

  const clicked = await page.evaluate((idx) => {
    const panel = document.querySelector("ytcp-multi-progress-panel, ytcp-multi-progress-monitor");
    if (!panel) return false;
    const header = panel.querySelector("[role='button'], .header, #header, ytcp-button");
    if (header) header.click();
    const candidates = [];
    const add = (el) => {
      const t = (el.innerText || el.textContent || "").replace(/\s+/g, " ").trim();
      if (t.length < 4) return;
      if (/^(Загрузка завершена|Upload complete|Свернуть|Expand|Close|Закрыть)$/i.test(t)) return;
      if (/100\s*%|Загружено|Uploaded|\.mp4|\.mov|Reels|_0\d/i.test(t)) candidates.push(el);
    };
    panel.querySelectorAll("ytcp-ve, [role='listitem'], ytcp-upload-progress-item, .progress-item, a, button").forEach(add);
    const uniq = [];
    const seen = new Set();
    for (const el of candidates) {
      const key = (el.innerText || "").slice(0, 40);
      if (seen.has(key)) continue;
      seen.add(key);
      uniq.push(el);
    }
    if (uniq.length > idx) {
      uniq[idx].click();
      return true;
    }
    return false;
  }, index).catch(() => false);

  if (!clicked) {
    const rows = page.locator("ytcp-multi-progress-panel ytcp-ve, ytcp-multi-progress-monitor ytcp-ve, ytcp-multi-progress-panel [role='listitem']");
    if (await rows.count().catch(() => 0) > index) {
      await rows.nth(index).click({ timeout: 8000 }).catch(() => {});
    }
  }
  await page.waitForTimeout(1800);
  await waitForUploadDialog(page, 180000).catch(() => {});
  if (!(await uploadDialog(page).isVisible().catch(() => false))) {
    await ensureUploadDialogOpen(page);
  }
  return uploadDialog(page).isVisible().catch(() => false);
}

async function selectUploadQueueItem(page, index) {
  if (index <= 0) return;
  const selected = await page.evaluate((idx) => {
    const tryClick = (nodes) => {
      if (nodes.length > idx) {
        nodes[idx].click();
        return true;
      }
      return false;
    };
    const lists = [
      Array.from(document.querySelectorAll("ytcp-uploads-file-picker-item")),
      Array.from(document.querySelectorAll("ytcp-uploads-dialog ytcp-uploads-file-picker ytcp-ve")),
      Array.from(document.querySelectorAll("ytcp-multi-progress-monitor ytcp-ve, ytcp-multi-progress-panel ytcp-ve"))
    ];
    for (const nodes of lists) {
      if (tryClick(nodes)) return idx + 1;
    }
    const drawer = document.querySelector("ytcp-multi-progress-panel, ytcp-multi-progress-monitor");
    if (drawer) {
      const rows = Array.from(drawer.querySelectorAll("[role='listitem'], .progress-item, ytcp-ve, div[class*='progress']"));
      if (tryClick(rows)) return idx + 1;
    }
    return 0;
  }, index).catch(() => 0);
  if (!selected) {
    send("youtube", `Очередь: не нашёл пункт ${index + 1} — продолжаю с текущим роликом.`, { percent: 40 });
  } else {
    send("youtube", `Очередь: ролик ${index + 1} выбран.`, { percent: 40 });
  }
  await page.waitForTimeout(900);
}

async function captureUploadMetaIfNeeded(page, title, url) {
  let out = url || "";
  if (out && extractVideoId(out)) return out;
  try {
    const meta = await captureUploadedVideoMeta(page, title);
    if (meta.url) out = meta.url;
    if (meta.channelUrl && job.profileId) {
      send("mesh", "Канал после загрузки: " + meta.channelUrl, {
        channelUrl: meta.channelUrl,
        meshOwner: String(job.profileId).trim()
      });
    }
  } catch (_) {}
  return out;
}

async function processUploadItemInDialog(page, item, index, total) {
  assertPageOpen(page);
  const title = cleanUploadTitle(item.title);
  const video = item.video;
  if (!video || !fs.existsSync(video)) throw new Error(`Ролик ${index}/${total}: файл не найден.`);
  if (!title) throw new Error(`Ролик ${index}/${total}: пустой заголовок.`);
  if (item.thumbnail && !fs.existsSync(item.thumbnail)) throw new Error(`Ролик ${index}/${total}: превью не найдено.`);

  const schedule = resolveItemSchedule(item, title);
  send("youtube", `Пачка ${index}/${total}: «${title.slice(0, 40)}» → ${schedule.date} ${schedule.time}`, {
    percent: Math.min(90, 20 + Math.round((index - 1) / Math.max(1, total) * 70))
  });

  await waitForUploadDialog(page);
  await fillTitle(page, title);
  await setThumbnail(page, item.thumbnail || "");
  await advance(page);
  let url = await finishUploadVisibility(page, schedule.date, schedule.time);
  url = await captureUploadMetaIfNeeded(page, title, url);
  return { url, schedule, packIndex: (item._origIndex != null ? item._origIndex : index - 1) + 1 };
}

async function uploadDraftsBulk(page, pack) {
  assertPageOpen(page);
  await verifyYouTubeStudioReady(page);
  const { toUpload, skipped } = await filterPackForUpload(page, pack, { draftOnly: true });
  for (const s of skipped) {
    const t = cleanUploadTitle(s.item.title);
    send("youtube", `Пропуск «${t.slice(0, 40)}» — ${s.reason}.`, {
      percent: 24,
      packIndex: (s.item._origIndex != null ? s.item._origIndex : 0) + 1
    });
  }
  if (!toUpload.length) {
    send("youtube", "Все ролики уже загружены — новая передача не нужна.", { percent: 95 });
    return [];
  }

  send("youtube", `Открываю загрузку для ${toUpload.length} ролик(ов)…`, { percent: 26 });
  await openUploadAndSetFiles(page, toUpload.map(it => it.video));
  const labels = toUpload.map(it => path.basename(String(it.video || "")));
  send("youtube", `Файлы переданы (${toUpload.length}), жду 100% на YouTube…`, { percent: 28 });
  await waitForUploadsReady(page, toUpload.length, labels);
  await applyDraftThumbnails(page, toUpload);
  await verifyDraftsSaved(page, toUpload.length);

  const results = toUpload.map((it, i) => ({
    url: "",
    schedule: null,
    packIndex: (it._origIndex != null ? it._origIndex : i) + 1
  }));
  send("youtube", "Готово.", { percent: 95 });
  return results;
}

async function uploadPackBulk(page, pack) {
  assertPageOpen(page);
  await verifyYouTubeStudioReady(page);
  const { toUpload, skipped } = await filterPackForUpload(page, pack);
  for (const s of skipped) {
    const t = cleanUploadTitle(s.item.title);
    send("youtube", `Пропуск «${t.slice(0, 40)}» — ${s.reason}.`, {
      percent: 26,
      packIndex: (s.item._origIndex != null ? s.item._origIndex : 0) + 1
    });
  }
  if (!toUpload.length) {
    send("youtube", "Все ролики уже на канале — новая загрузка не нужна.", { percent: 95 });
    return [];
  }

  send("youtube", `Открываю загрузку ${toUpload.length} ролик(ов), затем запланирую…`, { percent: 27 });
  await openUploadAndSetFiles(page, toUpload.map(it => it.video));
  const labels = toUpload.map(it => path.basename(String(it.video || "")));
  send("youtube", `Файлы переданы (${toUpload.length}), жду 100% на YouTube…`, { percent: 29 });
  await waitForUploadsReady(page, toUpload.length, labels);

  const results = [];
  for (let i = 0; i < toUpload.length; i++) {
    if (!(await uploadDialog(page).isVisible().catch(() => false))) {
      await openUploadItemFromProgressPanel(page, i);
    } else if (i > 0) {
      await selectUploadQueueItem(page, i);
    }
    if (!(await uploadDialog(page).isVisible().catch(() => false))) {
      const ok = await ensureUploadDialogOpen(page);
      if (!ok) throw new Error(`Ролик ${i + 1}/${toUpload.length}: не открылось окно редактирования — нажмите ролик в «Загрузка завершена» вручную.`);
    }
    const one = await processUploadItemInDialog(page, toUpload[i], i + 1, toUpload.length);
    results.push(one);
    send("youtube", `Запланировано ${i + 1}/${toUpload.length}.`, {
      percent: Math.min(95, 30 + Math.round((i + 1) / toUpload.length * 65)),
      url: one.url || "",
      packIndex: one.packIndex
    });
    if (i < toUpload.length - 1) await ensureUploadDialogOpen(page);
  }
  await dismissAfterPublish(page);
  return results;
}

async function main() {
  if (!jobPath || !fs.existsSync(jobPath)) throw new Error("Не найдено задание загрузки.");
  job = JSON.parse(fs.readFileSync(jobPath, "utf8"));
  job.token = process.env.VIDEOBATCH_DOLPHIN_TOKEN || job.token;
  if (!job.token || !job.profileId) throw new Error("Укажите токен Dolphin и ID профиля.");
  if (!Number.isInteger(job.localPort) || job.localPort < 1 || job.localPort > 65535) throw new Error("Некорректный порт Dolphin.");

  if (!job.searchOnly && !job.watchMesh && !job.skipQueueDelay) await waitBetweenProfiles();
  let endpoint = await startOrConnectProfile();
  browser = await connectBrowser(endpoint);
  let context = browser.contexts()[0];
  if (!context) throw new Error("Не удалось подключиться к окну профиля Dolphin.");
  let page = await context.newPage();
  attachPageGuards(page);
  await page.bringToFront().catch(() => {});
  // IP не блокируем: прокси уже в Dolphin. Только пишем в лог, если удалось узнать.
  let verifiedIp = "";
  try {
    const ip = await publicIp(page);
    verifiedIp = ip || "";
    if (ip) send("ip", `IP профиля: ${ip}`, { ip, percent: 20 });
  } catch (_) {}

  if (job.checkOnly) {
    await page.close().catch(() => {});
    await browser.close().catch(() => {});
    browser = null;
    await stopProfile();
    finished = true;
    send("done", "Профиль Dolphin доступен.", { success: true, ip: verifiedIp, percent: 100 });
    return;
  }

  if (job.watchMesh) {
    const targets = Array.isArray(job.watchTargets) ? job.watchTargets : [];
    if (!targets.length) throw new Error("Нет каналов для сетки просмотра.");
    youtubeOpened = true;
    const viewerPid = String(job.profileId || "").trim();
    const meshCatalog = loadMeshCatalog();

    const ownTarget = targets.find(t => String((t && t.ownerProfileId) || "").trim().toLowerCase() === viewerPid.toLowerCase());
    if (ownTarget && !normalizeChannelUrl(ownTarget.channelUrl || "") && !loadCachedChannelUrl(viewerPid)) {
      send("youtube", "Определяю URL собственного канала для общей сетки…", { percent: 23 });
      try {
        const ownChannelId = await resolveStudioChannelId(page);
        if (ownChannelId) {
          const ownChannelUrl = "https://www.youtube.com/channel/" + ownChannelId;
          saveCachedChannelUrl(viewerPid, ownChannelUrl);
          ownTarget.channelUrl = ownChannelUrl;
          send("mesh", "URL собственного канала сохранён: " + ownChannelUrl, {
            channelUrl: ownChannelUrl,
            meshOwner: viewerPid,
            percent: 24
          });
        } else {
          send("youtube", "URL собственного канала не определён — использую данные сетки.", { percent: 24 });
        }
      } catch (e) {
        send("youtube", "Не удалось определить URL собственного канала: " + (e instanceof Error ? e.message : String(e)), { percent: 24 });
      }
    }

    send("youtube", "Сетка: " + targets.length + " каналов (свои — лайк, чужие — просмотр)…", { percent: 25 });
    let totalViews = 0;
    let failed = 0;

    const recoverWatchPage = async (reason) => {
      send("youtube", "Восстанавливаю окно просмотра" + (reason ? ": " + reason : "") + "…", { percent: 24 });
      if (page && !page.isClosed()) await page.close().catch(() => {});
      if (!browser || !browser.isConnected()) {
        endpoint = await startOrConnectProfile();
        browser = await connectBrowser(endpoint);
      }
      context = browser.contexts()[0];
      if (!context) {
        await browser.close().catch(() => {});
        browser = null;
        endpoint = await startOrConnectProfile();
        browser = await connectBrowser(endpoint);
        context = browser.contexts()[0];
      }
      if (!context) throw new Error("После восстановления нет контекста браузера.");
      page = await context.newPage();
      attachPageGuards(page);
      await page.bringToFront().catch(() => {});
    };

    for (let i = 0; i < targets.length; i++) {
      const t = enrichWatchTarget(targets[i] || {}, meshCatalog);
      const label = channelDisplayName(t.ownerName || "") || t.ownerName || "?";
      send("youtube", "Канал " + (i + 1) + "/" + targets.length + " · " + label + "…", {
        percent: 25 + Math.round((i / Math.max(1, targets.length)) * 65)
      });
      let channelDone = false;
      let lastChannelError = null;
      for (let attempt = 1; attempt <= 2 && !channelDone; attempt++) {
        try {
          if (!page || page.isClosed() || !browser || !browser.isConnected()) {
            await recoverWatchPage("браузер был закрыт");
          }
          const watched = await watchChannelMeshWithTimeout(page, t, "none", viewerPid);
          totalViews += watched;
          channelDone = true;
          send("youtube", "✓ Канал " + (i + 1) + "/" + targets.length + " · " + label + " — готов (просмотров: " + watched + ")", {
            percent: 25 + Math.round(((i + 1) / Math.max(1, targets.length)) * 65)
          });
        } catch (e) {
          lastChannelError = e;
          const msg = e instanceof Error ? e.message : String(e);
          if (attempt < 2) {
            send("youtube", "Канал «" + label + "»: ошибка — " + msg + ". Восстановление и повтор 2/2…", { percent: 25 });
            await recoverWatchPage(msg.slice(0, 100));
          }
        }
      }
      if (!channelDone) {
        failed++;
        const msg = lastChannelError instanceof Error ? lastChannelError.message : String(lastChannelError || "неизвестная ошибка");
        send("youtube", "Пропуск «" + label + "» после 2 попыток: " + msg, {
          percent: 25 + Math.round(((i + 1) / Math.max(1, targets.length)) * 65)
        });
        try { if (page && !page.isClosed()) await leaveVideoPlayer(page, t.channelUrl || channelUrlFromHandle(parseChannelHandle(t.ownerName))); } catch (_) {}
      }
      if (i < targets.length - 1) {
        const next = enrichWatchTarget(targets[i + 1] || {}, meshCatalog);
        const nextLabel = channelDisplayName(next.ownerName || "") || next.ownerName || "?";
        send("youtube", "→ Канал " + (i + 2) + "/" + targets.length + " · " + nextLabel + "…", {
          percent: 25 + Math.round(((i + 1) / Math.max(1, targets.length)) * 65)
        });
        if (page && !page.isClosed()) await page.waitForTimeout(1200);
      }
    }
    send("youtube", "Аккаунт завершил сетку (" + totalViews + " просмотров). Профиль Dolphin закрывается…", { percent: 99 });
    await browser.close().catch(() => {});
    browser = null;
    await stopProfile();
    await pauseAfterWatch();
    finished = true;
    const summary = "Сетка: " + totalViews + " просмотров, " + (targets.length - failed) + "/" + targets.length + " каналов";
    if (failed) {
      fail(summary + " (" + failed + " пропущено). Сетка завершена не полностью.", {
        keptOpen: false,
        ip: verifiedIp,
        percent: 100
      });
      return;
    }
    send("done", summary + ".", { success: true, keptOpen: false, ip: verifiedIp, percent: 100 });
    return;
  }

  if (job.searchOnly) {
    const keys = parseKeywordList(job.searchKeys, job.title);
    const fullTitle = String(job.searchFullTitle || "").trim();
    if (!keys.length && !fullTitle) throw new Error("Укажите ключи (по одному на строку) или полный заголовок. Ссылка — ориентир по ID.");
    send("youtube", "Ищу видео на YouTube…", { percent: 25 });
    youtubeOpened = true;
    const opened = await openFoundVideo(page, job.title, job.searchUrl, job.searchFilter || "today", {
      searchKeys: job.searchKeys || job.title,
      searchFullTitle: fullTitle
    });
    send("youtube", "Видео открыто. Смотрю (лайк в конце окна)…", { percent: 82 });
    await waitForVideoEnd(page);
    await browser.close().catch(() => {});
    browser = null;
    await stopProfile();
    await pauseAfterWatch();
    finished = true;
    send("done", "Видео досмотрено, лайк поставлен, профиль закрыт.", { success: true, keptOpen: false, url: opened, ip: verifiedIp, percent: 100 });
    return;
  }

  if (job.applyThumbFrame) {
    const titles = (Array.isArray(job.items) ? job.items : [])
      .map(it => String((it && it.title) || "").trim())
      .filter(Boolean);
    const list = titles.length ? titles : (job.title ? [String(job.title).trim()].filter(Boolean) : []);
    if (!list.length) throw new Error("Нет заголовков для «Превью шортс».");
    youtubeOpened = true;
    send("youtube", "Меняю превью шортсов (первый кадр)…", { percent: 25 });
    await applyShortsThumbFrames(page, list);
    await browser.close().catch(() => {});
    browser = null;
    await stopProfile();
    finished = true;
    send("done", "Превью шортсов обновлено (" + list.length + ").", { success: true, ip: verifiedIp, percent: 100 });
    return;
  }

  // Пачка в одном открытом профиле (без закрытия между роликами)
  const pack = Array.isArray(job.items) && job.items.length
    ? job.items
    : [{ video: job.video, title: job.title, thumbnail: job.thumbnail, scheduleDate: job.scheduleDate, scheduleTime: job.scheduleTime }];

  youtubeOpened = true;
  send("youtube", `Загрузчик ${WORKER_BUILD}`, { percent: 24 });
  const draftOnly = job.draftOnly === true;
  send("youtube", draftOnly
    ? (pack.length > 1 ? `Черновики: пачка ${pack.length} роликов.` : "Черновики: один ролик.")
    : (pack.length > 1 ? `Загрузка ${pack.length} роликов с расписанием.` : "Загрузка с расписанием."), { percent: 25 });

  const results = draftOnly ? await uploadDraftsBulk(page, pack) : await uploadPackBulk(page, pack);
  const urls = results.map(r => r.url).filter(Boolean);

  await closeProfileSafely(page);
  markProfileCompleted();
  finished = true;
  const last = urls[urls.length - 1] || "";
  send("done", draftOnly
    ? (pack.length > 1 ? `Черновики: ${pack.length} роликов сохранены.` : "Черновик сохранён.")
    : (pack.length > 1 ? `Запланировано ${pack.length} роликов.` : (last ? `Запланировано: ${last}` : "Запланировано.")), { success: true, url: last, ip: verifiedIp, percent: 100 });
}

if (require.main === module) {
  process.on("SIGINT", async () => { await stopProfile(); process.exit(130); });
  process.on("SIGTERM", async () => { await stopProfile(); process.exit(143); });
  main().catch(async e => {
    // При ошибке после открытия YouTube профиль оставляем открытым: пользователь
    // видит точное место сбоя и может проверить результат без повторной публикации.
    if (!youtubeOpened && browser) await browser.close().catch(() => {});
    if (!youtubeOpened) await stopProfile();
    fail(e, { keptOpen: youtubeOpened && profileStarted });
  });
}

module.exports = { automationEndpoint, advance, automaticSchedule, resolveSchedule, randomDelaySeconds, waitBetweenProfiles, setSchedule, publish, waitEnabled, watchShortOnce, waitForVideoEnd };
