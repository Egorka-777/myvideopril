"use strict";

/**
 * VideoBatch · TikTok uploader через Dolphin Anty + Playwright CDP.
 * Job JSON: caption = полная подпись TikTok. title/description читаются только для совместимости.
 */

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { chromium } = require("playwright-core");
const WORKER_BUILD = "2026-09-21-tiktok-caption-v1";

const jobPath = process.argv[2];
let job = null;
let browser = null;
let profileStarted = false;
let finished = false;
let tiktokOpened = false;
let activePage = null;
let diagnosticFile = "";

function setupDiagnostics() {
  const dir = path.join(path.dirname(path.dirname(jobPath)), "diagnostics");
  fs.mkdirSync(dir, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  diagnosticFile = path.join(dir, `tiktok-${stamp}-${process.pid}.jsonl`);
}

function record(stage, text, extra = {}) {
  if (!diagnosticFile) return;
  try {
    fs.appendFileSync(diagnosticFile, JSON.stringify(Object.assign({
      at: new Date().toISOString(), stage, text: String(text || ""),
      profileId: job && job.profileId ? String(job.profileId) : ""
    }, extra)) + "\n", "utf8");
  } catch (_) {}
}

function localStatePath(name) {
  return path.join(path.dirname(path.dirname(jobPath)), name);
}

function randomDelaySeconds() { return crypto.randomInt(1, 31); }
async function setInputFilesRobust(page, locator, filePath) {
  const abs = path.resolve(String(filePath || ""));
  if (!abs || !fs.existsSync(abs)) throw new Error("Файл не найден: " + abs);
  const size = fs.statSync(abs).size;
  try {
    await locator.setInputFiles(abs);
    return;
  } catch (e) {
    const msg = String((e && e.message) || e);
    if (!/50\s*Mb|50Mb|co-located|larger than/i.test(msg)) throw e;
    send("tiktok", `Файл ${Math.round(size / 1024 / 1024)} МБ — передаю напрямую в браузер Dolphin…`, { percent: 28 });
  }
  const handle = await locator.elementHandle({ timeout: 90000 });
  if (!handle) throw new Error("Не найден input для выбора файла.");
  const client = await page.context().newCDPSession(page);
  try {
    await handle.evaluate((el) => { el.setAttribute("data-vb-file", "1"); });
    const doc = await client.send("DOM.getDocument", { depth: 0 });
    const found = await client.send("DOM.querySelector", { nodeId: doc.root.nodeId, selector: 'input[data-vb-file="1"]' });
    if (!found || !found.nodeId) throw new Error("CDP: не удалось найти input файла.");
    await client.send("DOM.setFileInputFiles", { files: [abs], nodeId: found.nodeId });
    await handle.evaluate((el) => { el.removeAttribute("data-vb-file"); }).catch(() => {});
  } finally {
    await client.detach().catch(() => {});
  }
}


async function waitBetweenProfiles() {
  let lastCompleted = 0;
  try { lastCompleted = Number(JSON.parse(fs.readFileSync(localStatePath("tiktok-queue-delay.json"), "utf8")).lastCompleted || 0); } catch (_) {}
  const ageMs = Date.now() - lastCompleted;
  if (!lastCompleted || ageMs < 0 || ageMs > 60000) return;
  const delay = randomDelaySeconds();
  const remainingMs = Math.max(0, delay * 1000 - ageMs);
  if (remainingMs > 0) {
    send("queue", `Пауза перед следующим профилем: ${Math.ceil(remainingMs / 1000)} сек.`, { percent: 1 });
    await new Promise(resolve => setTimeout(resolve, remainingMs));
  }
}

function markProfileCompleted() {
  const target = localStatePath("tiktok-queue-delay.json");
  const temp = target + "." + process.pid + ".tmp";
  fs.writeFileSync(temp, JSON.stringify({ lastCompleted: Date.now() }), "utf8");
  fs.renameSync(temp, target);
}

function send(stage, text, extra = {}) {
  record(stage, text, extra);
  process.stdout.write(JSON.stringify(Object.assign({ stage, text }, extra)) + "\n");
}

function fail(message, extra = {}) {
  const raw = message instanceof Error ? message.message : String(message);
  const error = raw.replace(/\x1b\[[0-9;]*m/g, "");
  send("error", error, Object.assign({ success: false, error }, extra));
  process.exitCode = 1;
}

async function api(apiPath, options = {}) {
  const url = `http://127.0.0.1:${job.localPort}${apiPath}`;
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
        throw new Error("Не удалось связаться с Dolphin Anty (порт " + job.localPort + "). Запустите Dolphin и проверьте порт.");
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

async function assertProxyAcceptable(page, expectedRaw, actualRaw) {
  const expected = normalizeIp(expectedRaw);
  const actual = normalizeIp(actualRaw);
  if (!actual) throw new Error("Не удалось определить IP профиля.");
  if (!expected) return { ip: actual, refreshed: true };
  if (expected === actual) return { ip: actual, refreshed: false };
  if (sameIpv4Pool(expected, actual)) {
    send("ip", `IP сменился в том же диапазоне (${expected} → ${actual}). Продолжаю.`, { ip: actual, percent: 21 });
    return { ip: actual, refreshed: true };
  }
  const [cExp, cAct] = await Promise.all([lookupCountryCode(page, expected), lookupCountryCode(page, actual)]);
  if (cExp && cAct && cExp === cAct) {
    send("ip", `IP сменился, страна та же (${cAct}): ${expected} → ${actual}.`, { ip: actual, percent: 21 });
    return { ip: actual, refreshed: true };
  }
  if (cExp && cAct && cExp !== cAct) {
    throw new Error(`Прокси другой страны: было ${cExp} (${expected}), стало ${cAct} (${actual}).`);
  }
  throw new Error(`IP сильно изменился: ожидался ${expected}, получен ${actual}. Нажмите «Проверить профили».`);
}

const IP_SERVICES = [
  ["https://api.ipify.org?format=json", "json-ip"],
  ["https://api64.ipify.org?format=json", "json-ip"],
  ["https://icanhazip.com", "text"],
  ["https://checkip.amazonaws.com", "text"],
  ["https://ipinfo.io/ip", "text"],
  ["https://www.cloudflare.com/cdn-cgi/trace", "cf-trace"]
];

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
      send("tiktok", `Сеть/прокси сбросили страницу, повтор ${attempt}/4…`, { percent: Math.min(28, 10 + attempt * 3) });
      await new Promise(r => setTimeout(r, 1200 * attempt));
    }
  }
  throw last || new Error("Не удалось открыть " + url);
}

async function publicIp(page) {
  send("ip", "Проверяю доступ к TikTok через профиль…", { percent: 12 });
  await gotoStable(page, "https://www.tiktok.com/", { waitUntil: "domcontentloaded", timeout: 90000 });
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
  for (const [url, kind] of [["https://api.ipify.org", "text"], ["https://icanhazip.com", "text"]]) {
    try {
      await gotoStable(page, url, { waitUntil: "domcontentloaded", timeout: 25000 });
      const body = (await page.locator("body").innerText({ timeout: 8000 })).trim();
      const ip = normalizeIp(parseIpPayload(body, kind));
      if (ip) return ip;
    } catch (e) { last = e.message; }
  }
  throw new Error("Не удалось узнать внешний IP профиля. " + last);
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
  throw new Error("Не удалось проверить прямой IP компьютера. " + last);
}

async function visible(locator, timeout = 1500) {
  try { await locator.first().waitFor({ state: "visible", timeout }); return true; } catch { return false; }
}

async function dismissOverlays(page) {
  const labels = [
    /Accept all/i, /Accept/i, /Allow all/i, /Got it/i, /I agree/i,
    /Принять все/i, /Принять/i, /Разрешить/i, /Понятно/i, /Хорошо/i, /OK/i,
    /Reject all/i, /Отклонить все/i, /Close/i, /Закрыть/i
  ];
  for (const re of labels) {
    try {
      const btn = page.getByRole("button", { name: re }).first();
      if (await btn.isVisible({ timeout: 400 }).catch(() => false)) {
        await btn.click({ timeout: 1500 }).catch(() => {});
        await page.waitForTimeout(250);
      }
    } catch (_) {}
  }
}

async function pickFileInput(page) {
  const inputs = page.locator('input[type="file"]');
  await inputs.first().waitFor({ state: "attached", timeout: 90000 }).catch(() => {});
  const n = await inputs.count();
  for (let i = 0; i < n; i++) {
    const accept = ((await inputs.nth(i).getAttribute("accept").catch(() => "")) || "").toLowerCase();
    if (!accept || /video|mp4|mov|webm|\*/.test(accept)) return inputs.nth(i);
  }
  return n > 0 ? inputs.first() : null;
}

function normalizeCaption(value) {
  return String(value || "").replace(/\r\n/g, "\n").trim();
}

async function fillCaption(page, caption) {
  const captionText = normalizeCaption(caption);
  if (!captionText) throw new Error("Пустая подпись TikTok.");
  if (captionText.length > 2200) throw new Error("Подпись TikTok длиннее 2200 символов.");
  const selectors = [
    '[contenteditable="true"][data-text="true"]',
    '.public-DraftEditor-content[contenteditable="true"]',
    '[contenteditable="true"].notranslate',
    'div[contenteditable="true"]',
    'textarea[placeholder*="caption" i]',
    'textarea[placeholder*="Describe" i]',
    'textarea[placeholder*="Опишите" i]',
    'textarea[placeholder*="описан" i]',
    'div[role="textbox"]'
  ];
  let found = false;
  for (const selector of selectors) {
    const boxes = page.locator(selector);
    const n = Math.min(3, await boxes.count().catch(() => 0));
    for (let i = 0; i < n; i++) {
      const box = boxes.nth(i);
      if (!(await visible(box, 400))) continue;
      const label = ((await box.getAttribute("aria-label").catch(() => "")) || "") + " " +
        ((await box.getAttribute("placeholder").catch(() => "")) || "");
      if (/\btitle\b|назван/i.test(label)) continue;
      found = true;
      try { await box.fill(captionText, { timeout: 5000 }); }
      catch (_) {
        await box.click({ timeout: 3000 });
        await page.keyboard.press("Control+A");
        await page.keyboard.insertText(captionText);
      }
      await page.waitForTimeout(400);
      const actual = await box.evaluate(el => el.value != null ? el.value : (el.innerText || el.textContent || "")).catch(() => "");
      if (normalizeCaption(actual) === captionText) return;
    }
  }
  throw new Error(found
    ? "TikTok не подтвердил полную подпись в поле ввода. Публикация не начиналась; профиль оставлен открытым."
    : "Не найдено поле подписи TikTok. Проверьте вход в аккаунт. Профиль оставлен открытым.");
}

async function clickPublishConfirmation(page) {
  const dialog = page.locator('[role="dialog"]');
  if (!(await visible(dialog, 1500))) return false;
  const names = [/^Confirm$/i, /^Post now$/i, /^Publish now$/i, /^Подтвердить$/i, /^Опубликовать сейчас$/i];
  for (const name of names) {
    const btn = dialog.getByRole("button", { name });
    if (await visible(btn, 600)) {
      if (!(await btn.first().isDisabled().catch(() => true))) {
        await btn.first().click({ timeout: 3000 });
        return true;
      }
    }
  }
  return false;
}

async function publishAndConfirm(page) {
  let networkEvidence = "";
  const onResponse = async response => {
    try {
      const req = response.request();
      const url = response.url();
      if (req.method() !== "POST" || !/(publish|post|commit|create)/i.test(url) || /upload/i.test(url)) return;
      if (response.status() < 200 || response.status() >= 300) return;
      const body = (await response.text().catch(() => "")).slice(0, 12000);
      if (/(item_id|video_id|post_id|aweme_id)/i.test(body) || /"status_code"\s*:\s*0/.test(body)) {
        networkEvidence = `HTTP ${response.status()} ${new URL(url).pathname}`;
      }
    } catch (_) {}
  };
  page.on("response", onResponse);
  try {
    const posted = await clickPost(page);
    if (!posted) throw new Error("Не нажалась кнопка публикации. Профиль оставлен открытым.");
    await page.waitForTimeout(900);
    await clickPublishConfirmation(page).catch(() => false);
    const deadline = Date.now() + 120000;
    while (Date.now() < deadline) {
      if (networkEvidence) return networkEvidence;
      const state = await page.evaluate(() => ({
        url: location.href,
        text: (document.body && document.body.innerText || "").slice(0, 30000)
      })).catch(() => ({ url: "", text: "" }));
      if (/\/(content|posts?|manage)(\/|\?|$)/i.test(state.url) && !/\/upload/i.test(state.url)) return "страница публикаций";
      if (/(successfully posted|successfully uploaded|post published|video is being processed|успешно опубликован|видео опубликовано|публикация обрабатывается)/i.test(state.text)) return "подтверждение TikTok Studio";
      await page.waitForTimeout(1500);
    }
    throw new Error("Кнопка публикации нажата, но TikTok не подтвердил результат за 2 минуты. Автоповтор запрещён, чтобы не создать дубль; проверьте открытый профиль.");
  } finally {
    page.off("response", onResponse);
  }
}

async function clickPost(page) {
  const names = [
    /^Post$/i, /^Publish$/i, /^Post now$/i,
    /^Опубликовать$/i, /^Публикация$/i, /^Разместить$/i
  ];
  for (const re of names) {
    const btn = page.getByRole("button", { name: re });
    if (await visible(btn, 2000)) {
      const el = btn.first();
      const disabled = await el.isDisabled().catch(() => true);
      if (!disabled) {
        await el.click({ timeout: 5000 });
        return true;
      }
    }
  }
  // запасной поиск по тексту
  const clicked = await page.evaluate(() => {
    const buttons = Array.from(document.querySelectorAll("button"));
    const hit = buttons.find(b => {
      const t = (b.innerText || b.textContent || "").trim();
      return /^(Post|Publish|Post now|Опубликовать|Публикация|Разместить)$/i.test(t)
        && !b.disabled
        && b.getAttribute("aria-disabled") !== "true";
    });
    if (!hit) return false;
    hit.click();
    return true;
  }).catch(() => false);
  return clicked;
}

async function waitUploadReady(page) {
  const deadline = Date.now() + 8 * 60 * 1000;
  let lastPct = 0;
  while (Date.now() < deadline) {
    await dismissOverlays(page);
    // кнопка Post активна?
    const canPost = await page.evaluate(() => {
      const buttons = Array.from(document.querySelectorAll("button"));
      return buttons.some(b => {
        const t = (b.innerText || b.textContent || "").trim();
        if (!/^(Post|Publish|Post now|Опубликовать|Публикация|Разместить)$/i.test(t)) return false;
        return !b.disabled && b.getAttribute("aria-disabled") !== "true";
      });
    }).catch(() => false);
    if (canPost) return;

    const progress = await page.evaluate(() => {
      const body = document.body ? document.body.innerText : "";
      const m = body.match(/(\d{1,3})\s*%/);
      return m ? Number(m[1]) : 0;
    }).catch(() => 0);
    if (progress > lastPct) {
      lastPct = progress;
      send("tiktok", `Загрузка файла… ${progress}%`, { percent: Math.min(85, 40 + Math.floor(progress * 0.4)) });
    }
    await new Promise(r => setTimeout(r, 1500));
  }
  throw new Error("Файл долго не догрузился / кнопка публикации не стала активной. Проверьте открытый профиль.");
}

async function uploadOne(page, item, index, total) {
  const caption = normalizeCaption(item.caption || item.description || item.title || "");
  const video = item.video;
  if (!video || !fs.existsSync(video)) throw new Error(`Ролик ${index}/${total}: файл не найден.`);
  if (!caption) throw new Error(`Ролик ${index}/${total}: пустая подпись.`);

  send("tiktok", `Пачка ${index}/${total}: открываю студию…`, {
    percent: Math.min(90, 20 + Math.round((index - 1) / Math.max(1, total) * 70))
  });

  const urls = [
    "https://www.tiktok.com/tiktokstudio/upload",
    "https://www.tiktok.com/upload",
    "https://www.tiktok.com/creator-center/upload"
  ];
  let opened = false;
  for (const u of urls) {
    try {
      await gotoStable(page, u, { waitUntil: "domcontentloaded", timeout: 90000 });
      await dismissOverlays(page);
      const input = await pickFileInput(page);
      if (input) {
        opened = true;
        await setInputFilesRobust(page, input, video);
        break;
      }
    } catch (_) {}
  }
  if (!opened) throw new Error("Не найдена страница загрузки TikTok. Войдите в аккаунт в этом профиле Dolphin и повторите.");

  send("tiktok", `Пачка ${index}/${total}: файл передан, заполняю подпись…`, {
    percent: Math.min(92, 30 + Math.round(index / Math.max(1, total) * 55))
  });
  await page.waitForTimeout(2000);
  await dismissOverlays(page);
  await fillCaption(page, caption);
  await waitUploadReady(page);

  send("tiktok", `Пачка ${index}/${total}: публикую…`, { percent: Math.min(95, 50 + Math.round(index / Math.max(1, total) * 45)) });
  const evidence = await publishAndConfirm(page);
  send("item_done", `TikTok подтвердил публикацию ${index}/${total}: ${evidence}.`, { packIndex: index, packTotal: total });
  send("tiktok", `Готово ${index}/${total}: ${evidence}.`, { percent: Math.min(98, 60 + Math.round(index / Math.max(1, total) * 38)) });
  return page.url();
}

async function main() {
  if (!jobPath || !fs.existsSync(jobPath)) throw new Error("Не найдено задание загрузки.");
  job = JSON.parse(fs.readFileSync(jobPath, "utf8"));
  setupDiagnostics();
  job.token = process.env.VIDEOBATCH_DOLPHIN_TOKEN || job.token;
  if (!job.token || !job.profileId) throw new Error("Укажите токен Dolphin и ID профиля.");
  if (!Number.isInteger(job.localPort) || job.localPort < 1 || job.localPort > 65535) throw new Error("Некорректный порт Dolphin.");

  if (!job.checkOnly && !job.skipQueueDelay) await waitBetweenProfiles();
  send("start", `TikTok worker ${WORKER_BUILD}`, { percent: 1, diagnosticFile });
  send("diagnostic", "Подробная диагностика: " + diagnosticFile, { percent: 1, diagnosticFile });
  send("dolphin", "Подключаюсь к Dolphin…", { percent: 3 });
  await api("/v1.0/auth/login-with-token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token: job.token })
  });
  const started = await api(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}/start?automation=1`);
  profileStarted = true;
  const endpoint = automationEndpoint(started);
  if (!endpoint) throw new Error("Dolphin запустил профиль, но не вернул порт автоматизации.");

  send("dolphin", "Профиль запущен…", { percent: 10 });
  browser = await chromium.connectOverCDP(endpoint, { timeout: 60000 });
  const context = browser.contexts()[0];
  if (!context) throw new Error("Не удалось подключиться к окну профиля Dolphin.");
  const page = await context.newPage();
  activePage = page;
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

  const pack = Array.isArray(job.items) && job.items.length
    ? job.items
    : [{ video: job.video, caption: job.caption || job.description || job.title }];

  tiktokOpened = true;
  send("tiktok", pack.length > 1
    ? `Профиль открыт. Гружу пачку ${pack.length} в TikTok.`
    : "Открываю загрузку TikTok…", { percent: 25 });

  let lastUrl = "";
  for (let i = 0; i < pack.length; i++) {
    lastUrl = await uploadOne(page, pack[i], i + 1, pack.length) || lastUrl;
  }

  await browser.close().catch(() => {});
  browser = null;
  await stopProfile();
  markProfileCompleted();
  finished = true;
  send("done", pack.length > 1
    ? `Пачка ${pack.length} роликов опубликована в TikTok.`
    : "Опубликовано в TikTok.", { success: true, url: lastUrl, ip: verifiedIp, percent: 100 });
}

if (require.main === module) {
  process.on("SIGINT", async () => { await stopProfile(); process.exit(130); });
  process.on("SIGTERM", async () => { await stopProfile(); process.exit(143); });
  main().catch(async e => {
    if (activePage && !activePage.isClosed() && diagnosticFile) {
      const imagePath = diagnosticFile.replace(/\.jsonl$/i, "-error.png");
      await activePage.screenshot({ path: imagePath, fullPage: false }).catch(() => {});
      record("screenshot", "Снимок окна при ошибке", { imagePath, pageUrl: activePage.url().split("?")[0] });
      send("tiktok", "Снимок окна при ошибке: " + imagePath, { diagnosticFile });
    }
    if (!tiktokOpened && browser) await browser.close().catch(() => {});
    if (!tiktokOpened) await stopProfile();
    fail(e, { keptOpen: tiktokOpened && profileStarted });
  });
}

module.exports = { normalizeCaption };
