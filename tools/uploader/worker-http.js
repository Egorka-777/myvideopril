"use strict";

// Production HTTP batch uploader. Does not change worker.js and never
// stops a Dolphin profile by default. One profile start + one YouTube session
// per account batch; items[] uploaded sequentially without artificial delays.

const fs = require("fs");
const crypto = require("crypto");
const path = require("path");

const BUILD = "2026-09-18-http-pool-v2";
const WORKER_EXIT_DELAY_MS = 150;
const CHUNK_SIZE = 8 * 1024 * 1024;
const STUDIO_ORIGIN = "https://studio.youtube.com";
const STUDIO_READY_TIMEOUT_MS = 5 * 60 * 1000;
const DOLPHIN_START_TIMEOUT_MS = 2 * 60 * 1000;

let job = null;
let activePage = null;
let diagnosticFile = "";
const runId = crypto.randomUUID();
const runStartedAt = Date.now();

function safeDiagnosticUrl(value) {
  try {
    const parsed = new URL(String(value || ""));
    return parsed.origin + parsed.pathname;
  } catch (_) { return String(value || "").split(/[?#]/)[0]; }
}

function redactDiagnostic(value) {
  return String(value == null ? "" : value)
    .replace(/\x1b\[[0-9;]*m/g, "")
    .replace(/(Authorization)\s*[:=]\s*(?:Bearer\s+)?[^\s,;]+/gi, "$1=[скрыто]")
    .replace(/(SAPISID|__Secure-3PAPISID|SESSION_TOKEN|sessionToken|Cookie|VIDEOBATCH_DOLPHIN_TOKEN|password|proxyPassword)\s*[:=]\s*[^\s,;]+/gi, "$1=[скрыто]")
    .replace(/Bearer\s+[A-Za-z0-9._~+\/-]+=*/gi, "Bearer [скрыто]")
    .replace(/https?:\/\/[^\s"']+/gi, match => safeDiagnosticUrl(match));
}

function sanitizeExtra(extra) {
  const result = {};
  for (const [key, value] of Object.entries(extra || {})) {
    if (/token|cookie|authorization|password|sapisid|proxy(user|pass)/i.test(key)) {
      result[key] = "[скрыто]";
    } else if (typeof value === "string") {
      result[key] = redactDiagnostic(value);
    } else if (value == null || typeof value === "number" || typeof value === "boolean") {
      result[key] = value;
    }
  }
  return result;
}

function eventData(stage, text, extra) {
  return Object.assign({
    timestamp: new Date().toISOString(),
    elapsedMs: Date.now() - runStartedAt,
    runId,
    build: BUILD,
    stage: String(stage || "log"),
    text: redactDiagnostic(text)
  }, sanitizeExtra(extra));
}

function writeDiagnostic(event) {
  if (!diagnosticFile) return;
  try { fs.appendFileSync(diagnosticFile, JSON.stringify(event) + "\n", "utf8"); } catch (_) {}
}

function send(stage, text, extra = {}) {
  const event = eventData(stage, text, extra);
  writeDiagnostic(event);
  process.stdout.write(JSON.stringify(event) + "\n");
}

function record(stage, text, extra = {}) {
  writeDiagnostic(eventData(stage, text, extra));
}

function setupDiagnostics(jobPath, jobRunId) {
  const root = path.dirname(path.dirname(path.resolve(jobPath)));
  const directory = path.join(root, "diagnostics");
  fs.mkdirSync(directory, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  const rid = String(jobRunId || runId || process.pid).replace(/[^\w-]/g, "").slice(0, 32);
  diagnosticFile = path.join(directory, `http-${stamp}-${rid}-${process.pid}.jsonl`);
  send("diagnostic", `Подробная диагностика: ${diagnosticFile}`, { diagnosticFile });
}

function finishProcess(code) {
  setTimeout(() => process.exit(code), WORKER_EXIT_DELAY_MS);
}

function fail(error, extra = {}) {
  const text = redactDiagnostic(error && error.message || error || "Неизвестная ошибка");
  send("error", text, Object.assign({ success: false, error: text }, extra));
  finishProcess(1);
}

function relevantNetworkUrl(value) {
  try {
    const parsed = new URL(String(value || ""));
    return /(^|\.)(youtube\.com|googlevideo\.com|google\.com)$/i.test(parsed.hostname);
  } catch (_) { return false; }
}

function attachPageDiagnostics(page) {
  page.on("requestfailed", request => {
    if (!relevantNetworkUrl(request.url())) return;
    const failure = request.failure();
    record("network", "Сетевой запрос не выполнен.", {
      method: request.method(), resourceType: request.resourceType(),
      requestUrl: safeDiagnosticUrl(request.url()), reason: failure && failure.errorText || "unknown"
    });
  });
  page.on("response", response => {
    if (response.status() < 400 || !relevantNetworkUrl(response.url())) return;
    const request = response.request();
    record("network", `YouTube/Google вернул HTTP ${response.status()}.`, {
      status: response.status(), method: request.method(), resourceType: request.resourceType(),
      requestUrl: safeDiagnosticUrl(response.url())
    });
  });
  page.on("pageerror", error => record("page", "Ошибка JavaScript страницы: " + redactDiagnostic(error && error.message || error)));
  page.on("crash", () => record("page", "Вкладка браузера аварийно завершилась."));
}

async function captureFailureDiagnostics(error) {
  if (!activePage || activePage.isClosed() || !diagnosticFile) return {};
  const details = {
    pageUrl: safeDiagnosticUrl(activePage.url()),
    failure: redactDiagnostic(error && error.message || error)
  };
  try {
    details.pageTitle = redactDiagnostic(await Promise.race([
      activePage.title(),
      new Promise(resolve => setTimeout(() => resolve("[тайм-аут чтения заголовка]"), 5000))
    ]));
  } catch (_) { details.pageTitle = "[не удалось прочитать]"; }
  const screenshotFile = diagnosticFile.replace(/\.jsonl$/i, "-error.png");
  try {
    await activePage.screenshot({ path: screenshotFile, fullPage: false, timeout: 15000 });
    details.screenshotFile = screenshotFile;
    send("diagnostic", `Снимок окна при ошибке: ${screenshotFile}`, details);
  } catch (screenshotError) {
    record("diagnostic", "Не удалось сделать снимок окна: " + redactDiagnostic(screenshotError && screenshotError.message || screenshotError), details);
  }
  return details;
}

function resolvePublishMode(value) {
  return String(value && value.publishMode || "scheduled").trim().toLowerCase();
}

function validateJob(value) {
  if (!value || typeof value !== "object") throw new Error("Пустое задание.");
  if (!String(value.profileId || "").trim()) throw new Error("Не выбран Profile ID Dolphin.");
  if (!Number.isInteger(Number(value.localPort)) || Number(value.localPort) < 1 || Number(value.localPort) > 65535)
    throw new Error("Некорректный локальный порт Dolphin.");
  if (!String(value.video || "").trim() || !fs.existsSync(String(value.video))) throw new Error("Видео не найдено.");
  const stat = fs.statSync(String(value.video));
  if (!stat.isFile() || stat.size < 1) throw new Error("Видео пустое или это не файл.");
  const title = String(value.title || "").trim();
  if (!title) throw new Error("Заголовок пуст.");
  if (title.length > 100) throw new Error("Заголовок длиннее 100 символов.");
  if (/[<>]/.test(title)) throw new Error("В заголовке нельзя использовать < или >.");
  const publishMode = resolvePublishMode(value);
  let scheduledUnixSeconds = 0;
  if (publishMode === "scheduled") {
    const unix = Number(value.scheduledUnixSeconds);
    if (!Number.isFinite(unix) || unix <= Math.floor(Date.now() / 1000) + 15 * 60)
      throw new Error("Время публикации должно быть минимум через 15 минут.");
    scheduledUnixSeconds = Math.floor(unix);
  }
  if (!String(value.expectedIp || "").trim())
    throw new Error("У аккаунта не сохранён ожидаемый IP. Сначала в основном приложении нажмите «Проверить профили».");
  const thumbnail = String(value.thumbnail || "").trim();
  if (thumbnail && !fs.existsSync(thumbnail)) throw new Error("Файл превью не найден.");
  const contentKind = String(value.contentKind || "long").trim().toLowerCase();
  return Object.assign({}, value, { title, scheduledUnixSeconds, publishMode, thumbnail, contentKind });
}

function validateBatchJob(value) {
  if (!value || typeof value !== "object") throw new Error("Пустое задание.");
  if (!String(value.profileId || "").trim()) throw new Error("Не выбран Profile ID Dolphin.");
  if (!Number.isInteger(Number(value.localPort)) || Number(value.localPort) < 1 || Number(value.localPort) > 65535)
    throw new Error("Некорректный локальный порт Dolphin.");
  if (!String(value.expectedIp || "").trim())
    throw new Error("У аккаунта не сохранён ожидаемый IP.");
  const rawItems = Array.isArray(value.items) ? value.items : [];
  if (rawItems.length === 0) throw new Error("Пустой список items[].");
  const publishMode = resolvePublishMode(value);
  const items = rawItems.map((it, index) => {
    const merged = Object.assign({}, value, it || {}, { publishMode });
    const validated = validateJob(merged);
    return {
      localJobId: String(it.localJobId || validated.localJobId || `item-${index + 1}`),
      video: validated.video,
      title: validated.title,
      scheduledUnixSeconds: validated.scheduledUnixSeconds,
      thumbnail: validated.thumbnail || "",
      contentKind: validated.contentKind || "long",
      thumbnailStatus: "pending"
    };
  });
  return Object.assign({}, value, {
    keepProfileOpen: value.keepProfileOpen !== false,
    publishMode,
    runId: String(value.runId || process.env.VIDEOBATCH_RUN_ID || runId),
    items
  });
}

function isAmbiguousUploadError(error) {
  const text = String(error && error.message || error || "").toLowerCase();
  return /scottyresourceid|не подтвердил создание|не вернул url загрузки|принял байты|неоднозначн|повторно не/.test(text);
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

async function dolphinApi(path, options = {}, timeoutMs = 35000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(`http://127.0.0.1:${job.localPort}${path}`, Object.assign({}, options, { signal: controller.signal }));
    const raw = await response.text();
    let data = {};
    try { data = raw ? JSON.parse(raw) : {}; } catch (_) { data = { raw }; }
    if (!response.ok || data.success === false)
      throw new Error(`Dolphin API: HTTP ${response.status}. ${data.message || data.error || "операция отклонена"}`);
    return data;
  } catch (error) {
    if (error && error.name === "AbortError") throw new Error(`Dolphin не ответил за ${Math.round(timeoutMs / 1000)} секунд.`);
    if (/ECONNREFUSED|fetch failed/i.test(String(error && error.message || error)))
      throw new Error(`Dolphin Anty недоступен на порту ${job.localPort}. Запустите Dolphin.`);
    throw error;
  } finally { clearTimeout(timer); }
}

function dolphinProfileStatus(data) {
  const root = data && (data.data || data.browserProfile || data.profile || data);
  return String(root && (root.status || root.state || root.browserStatus) || "").trim().toLowerCase();
}

function dolphinProfileIsRunning(status) {
  return /(^|[^a-z])(running|started|active)([^a-z]|$)|запущен/i.test(String(status || ""));
}

function retryableDolphinStartError(error) {
  return /initConnectionError/i.test(String(error && error.message || error || ""));
}

async function startDolphinProfile() {
  const startedAt = Date.now();
  const heartbeat = setInterval(() => {
    const seconds = Math.max(1, Math.round((Date.now() - startedAt) / 1000));
    send("dolphin", `Dolphin запускает профиль (${seconds} сек.) — продолжаю ждать ответ API…`, { percent: 4 });
  }, 10000);
  try {
    return await dolphinApi(
      `/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}/start?automation=1`,
      {},
      DOLPHIN_START_TIMEOUT_MS
    );
  } finally { clearInterval(heartbeat); }
}

async function startOrConnectProfile() {
  send("dolphin", "Проверяю локальный API Dolphin…", { percent: 2 });
  await dolphinApi("/v1.0/auth/login-with-token", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token: job.token })
  });
  send("dolphin", "API Dolphin доступен. Запускаю выбранный закрытый профиль в режиме автоматизации…", { percent: 3 });
  const failures = [];
  for (let attempt = 1; attempt <= 2; attempt++) {
    try {
      const started = await startDolphinProfile();
      const endpoint = automationEndpoint(started);
      if (!endpoint) throw new Error("Dolphin не вернул порт автоматизации.");
      if (attempt > 1) send("dolphin", "Повторный запуск профиля подтверждён Dolphin.", { percent: 5 });
      return endpoint;
    } catch (startError) {
      const original = redactDiagnostic(startError && startError.message || startError);
      failures.push(original);
      record("dolphin", `Попытка запуска профиля ${attempt} завершилась ошибкой.`, { attempt, reason: original });
      const info = await dolphinApi(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}`).catch(() => null);
      const endpoint = automationEndpoint(info);
      if (endpoint) {
        send("dolphin", "Dolphin запустил профиль с задержкой; порт автоматизации найден.", { percent: 5 });
        return endpoint;
      }
      const status = dolphinProfileStatus(info);
      if (dolphinProfileIsRunning(status)) {
        throw new Error(`Dolphin действительно сообщает статус «${status}», но не отдал порт автоматизации. Ошибки запуска: ${failures.join("; ")}`);
      }
      if (attempt === 1 && retryableDolphinStartError(startError)) {
        send("dolphin", "Dolphin вернул initConnectionError. Профиль не запущен — через 5 секунд повторю ровно один раз…", { percent: 4 });
        await new Promise(resolve => setTimeout(resolve, 5000));
        continue;
      }
      const statusText = status ? ` Текущий статус по API: «${status}».` : " Статус профиля API не сообщил.";
      throw new Error(`Dolphin не запустил выбранный профиль или не вернул результат запуска.${statusText} Ошибки запуска: ${failures.join("; ")}`);
    }
  }
  throw new Error(`Dolphin не запустил выбранный профиль. Ошибки запуска: ${failures.join("; ")}`);
}

function normalizeIp(value) {
  const ip = String(value || "").trim().toLowerCase();
  return /^[0-9a-f:.]+$/i.test(ip) && (ip.includes(".") || ip.includes(":")) ? ip : "";
}

function parseIpPayload(raw, kind) {
  const text = String(raw || "").trim();
  try {
    if (kind === "json-ip") return normalizeIp(JSON.parse(text).ip);
    if (kind === "json-query") return normalizeIp(JSON.parse(text).query);
  } catch (_) {}
  return normalizeIp(text.split(/\s+/)[0]);
}

const IP_SERVICES = [
  ["https://api.ipify.org?format=json", "json-ip"],
  ["https://api64.ipify.org?format=json", "json-ip"],
  ["https://icanhazip.com", "text"],
  ["https://checkip.amazonaws.com", "text"],
  ["https://ipinfo.io/ip", "text"],
  ["https://api.ip.sb/ip", "text"]
];

async function directIp() {
  for (const [url, kind] of IP_SERVICES) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
      const response = await fetch(url, { signal: controller.signal, cache: "no-store" });
      if (!response.ok) continue;
      const ip = parseIpPayload(await response.text(), kind);
      if (ip) return ip;
    } catch (_) {} finally { clearTimeout(timer); }
  }
  throw new Error("Не удалось определить прямой IP компьютера ни через один сервис.");
}

async function browserIp(context) {
  const page = await context.newPage();
  try {
    for (const [url, kind] of IP_SERVICES) {
      try {
        await page.goto(url, { waitUntil: "domcontentloaded", timeout: 25000 });
        const ip = parseIpPayload(await page.locator("body").innerText({ timeout: 8000 }), kind);
        if (ip) return ip;
      } catch (_) {}
    }
    throw new Error("Не удалось определить IP Dolphin-профиля ни через один сервис.");
  } finally { await page.close().catch(() => {}); }
}

function ipv4Prefix(ip) {
  const parts = normalizeIp(ip).split(".");
  return parts.length === 4 ? parts.slice(0, 2).join(".") : "";
}

function assertProxy(expected, actual, computer) {
  expected = normalizeIp(expected); actual = normalizeIp(actual); computer = normalizeIp(computer);
  if (!actual) throw new Error("Не удалось определить IP Dolphin-профиля.");
  if (!computer) throw new Error("Не удалось определить прямой IP компьютера. Без проверки загрузка запрещена.");
  if (actual === computer) throw new Error(`Прокси не используется: IP профиля совпал с прямым IP компьютера (${actual}). Загрузка отменена.`);
  if (expected === actual) return;
  if (ipv4Prefix(expected) && ipv4Prefix(expected) === ipv4Prefix(actual)) return;
  throw new Error(`IP профиля изменился: ожидался ${expected}, получен ${actual}. Сначала перепроверьте профиль в основном приложении.`);
}

function findBootstrapValue(html, name) {
  const match = String(html || "").match(new RegExp(`"${name}"\\s*:\\s*"([^"\\\\]*(?:\\\\.[^"\\\\]*)*)"`));
  if (!match) return "";
  try { return JSON.parse(`"${match[1]}"`); } catch (_) { return match[1]; }
}

function parseChannelRoleType(html) {
  const match = String(html || "").match(/channelRoleType["\s:]+([A-Z_]+)/);
  return match && match[1] ? match[1] : "CREATOR_CHANNEL_ROLE_TYPE_OWNER";
}

function parseStudioBootstrap(html, url) {
  const channelMatch = String(url || "").match(/studio\.youtube\.com\/channel\/([^/?#]+)/i);
  const result = {
    channelId: channelMatch ? channelMatch[1] : "",
    apiKey: findBootstrapValue(html, "INNERTUBE_API_KEY"),
    authUser: findBootstrapValue(html, "SESSION_INDEX"),
    delegatedSessionId: findBootstrapValue(html, "DELEGATED_SESSION_ID") || null,
    channelRoleType: parseChannelRoleType(html),
    clientVersion: findBootstrapValue(html, "INNERTUBE_CLIENT_VERSION") || "1.20231215.01.00"
  };
  if (!result.channelId || !result.apiKey || result.authUser === "")
    throw new Error("YouTube Studio изменил данные страницы: не найдены channelId/API key/authUser. Загрузка не начата.");
  return result;
}

function studioPageState(urlValue, textValue) {
  const url = String(urlValue || "");
  const text = String(textValue || "");
  if (/accounts\.google\.com|ServiceLogin|signin|oauth/i.test(url))
    return { ready: false, blocker: "Требуется вход в Google." };
  if (/captcha|unusual traffic|challenge|robot/i.test(text + " " + url))
    return { ready: false, blocker: "Google запросил CAPTCHA/проверку безопасности." };
  return { ready: /studio\.youtube\.com\/channel\//i.test(url), blocker: "" };
}

async function waitForStudioChannel(page, timeoutMs = STUDIO_READY_TIMEOUT_MS) {
  const deadline = Date.now() + timeoutMs;
  let nextHeartbeat = 0;
  while (Date.now() < deadline) {
    if (page.isClosed()) throw new Error("Окно Dolphin было закрыто во время загрузки YouTube Studio.");
    const snapshot = await page.evaluate(() => ({
      url: location.href || "",
      text: ((document.body && document.body.innerText) || "").slice(0, 20000)
    })).catch(() => ({ url: page.url(), text: "" }));
    const state = studioPageState(snapshot.url, snapshot.text);
    if (state.blocker) throw new Error(state.blocker);
    if (state.ready) return snapshot.url;
    if (Date.now() >= nextHeartbeat) {
      const shown = String(snapshot.url || page.url() || "").slice(0, 120);
      send("youtube", "YouTube Studio ещё загружается — продолжаю ждать" + (shown ? ` (${shown})` : "") + "…", { percent: 15 });
      nextHeartbeat = Date.now() + 10000;
    }
    await page.waitForTimeout(1000);
  }
  throw new Error("YouTube Studio не открыл канал за 5 минут. Профиль оставлен открытым для проверки.");
}

function assertExpectedChannel(expectedChannelId, actualChannelId) {
  const expected = String(expectedChannelId || "").trim();
  const actual = String(actualChannelId || "").trim();
  if (expected && expected !== actual)
    throw new Error(`Открыт другой YouTube-канал: ожидался ${expected}, открыт ${actual}. Ничего не загружено.`);
}

function channelRoleType(data) {
  return String((data && data.channelRoleType) || "CREATOR_CHANNEL_ROLE_TYPE_OWNER").trim() || "CREATOR_CHANNEL_ROLE_TYPE_OWNER";
}

function makeContext(data, sessionToken) {
  const user = {
    delegationContext: { externalChannelId: data.channelId, roleType: { channelRoleType: channelRoleType(data) } }
  };
  if (data.delegatedSessionId) user.onBehalfOfUser = data.delegatedSessionId;
  return {
    client: {
      clientName: 62, clientVersion: data.clientVersion, experimentsToken: "", gl: "US", hl: "en",
      utcOffsetMinutes: -new Date().getTimezoneOffset(), userInterfaceTheme: "USER_INTERFACE_THEME_DARK",
      screenWidthPoints: 1920, screenHeightPoints: 1080, screenPixelDensity: 1, screenDensityFloat: 1
    },
    request: { internalExperimentFlags: [], returnLogEntry: true, sessionInfo: { token: sessionToken } },
    user
  };
}

function delegationContext(channelId, data) {
  return { externalChannelId: channelId, roleType: { channelRoleType: channelRoleType(data || { channelId }) } };
}

function makeCreateVideoBody(data, sessionToken, frontEndUploadId, scottyResourceId, title) {
  return {
    channelId: data.channelId,
    context: makeContext(data, sessionToken),
    delegationContext: delegationContext(data.channelId, data),
    frontendUploadId: frontEndUploadId,
    initialMetadata: {
      title: { newTitle: title }, description: { newDescription: "", shouldSegment: true },
      privacy: { newPrivacy: "PRIVATE" }, draftState: { isDraft: true }, tags: { newTags: [] }
    },
    presumedShort: false,
    resourceId: { scottyResourceId: { id: scottyResourceId } }
  };
}

function makeMetadataBody(data, sessionToken, videoId, publishMode, scheduledUnixSeconds) {
  const body = {
    context: makeContext(data, sessionToken),
    delegationContext: delegationContext(data.channelId, data),
    encryptedVideoId: videoId,
    madeForKids: { newMfk: "MDE_MADE_FOR_KIDS_TYPE_NOT_MFK", operation: "MDE_MADE_FOR_KIDS_UPDATE_OPERATION_SET" },
    draftState: { operation: "MDE_DRAFT_STATE_UPDATE_OPERATION_REMOVE_DRAFT_STATE" }
  };
  const mode = String(publishMode || "scheduled").toLowerCase();
  if (mode === "immediate") {
    body.privacyState = { newPrivacy: "PUBLIC" };
  } else if (mode === "private") {
    body.privacyState = { newPrivacy: "PRIVATE" };
  } else {
    body.privacyState = { newPrivacy: "PRIVATE" };
    body.scheduledPublishing = { set: { timeSec: String(scheduledUnixSeconds), privacy: "PUBLIC" } };
  }
  return body;
}

function sapiSidHash(sapisid) {
  const timestamp = Math.floor(Date.now() / 1000);
  const digest = crypto.createHash("sha1").update(`${timestamp} ${sapisid} ${STUDIO_ORIGIN}`, "utf8").digest("hex");
  return `${timestamp}_${digest}`;
}

async function studioFetch(page, url, options) {
  const result = await page.evaluate(async ({ url, options }) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), options.timeoutMs || 90000);
    try {
      let body = options.body;
      if (options.base64Body) {
        const binary = atob(options.base64Body);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        body = bytes;
      }
      const response = await fetch(url, {
        method: options.method || "GET", headers: options.headers || {}, body,
        credentials: "include", cache: "no-store", signal: controller.signal
      });
      const headers = {};
      response.headers.forEach((value, key) => { headers[key.toLowerCase()] = value; });
      return { ok: response.ok, status: response.status, text: await response.text(), headers };
    } catch (error) {
      return { ok: false, status: 0, text: String(error && error.message || error), headers: {} };
    } finally { clearTimeout(timer); }
  }, { url, options });
  if (!result.ok) throw new Error(`YouTube HTTP ${result.status || "сеть"}: ${String(result.text || "нет ответа").slice(0, 300)}`);
  return result;
}

function authHeaders(data, sapisid) {
  const headers = {
    "authorization": `SAPISIDHASH ${sapiSidHash(sapisid)}`,
    "x-origin": STUDIO_ORIGIN,
    "x-goog-authuser": String(data.authUser),
    "content-type": "application/json"
  };
  if (data.delegatedSessionId) headers["x-goog-pageid"] = String(data.delegatedSessionId);
  return headers;
}

function isSchedule403(error) {
  const text = String(error && error.message || error || "");
  return /403|PERMISSION_DENIED|permission denied|forbidden/i.test(text);
}

async function applyMetadataSchedule(page, session, sapisid, data, videoId, publishMode, scheduledUnixSeconds) {
  const headers = authHeaders(data, sapisid);
  const metadata = await studioFetch(page, `${STUDIO_ORIGIN}/youtubei/v1/video_manager/metadata_update?key=${encodeURIComponent(data.apiKey)}&alt=json`, {
    method: "POST", timeoutMs: 90000, headers,
    body: JSON.stringify(makeMetadataBody(data, session.sessionToken, videoId, publishMode, scheduledUnixSeconds))
  });
  let metadataJson = null;
  try { metadataJson = metadata.text ? JSON.parse(metadata.text) : null; } catch (_) {}
  if (metadataJson && metadataJson.error) throw new Error("YouTube отклонил метаданные: " + JSON.stringify(metadataJson.error).slice(0, 240));
  return { metadata, metadataJson, applied: true };
}

async function applyScheduleViaEditPage(page, session, sapisid, data, videoId, publishMode, scheduledUnixSeconds) {
  const editUrl = `${STUDIO_ORIGIN}/video/${encodeURIComponent(videoId)}/edit`;
  await page.goto(editUrl, { waitUntil: "domcontentloaded", timeout: 90000 });
  await page.waitForTimeout(2500);
  if (!page.url().includes(`/video/${videoId}/`)) throw new Error("Страница редактирования видео не открылась.");
  let editData = data;
  try {
    editData = parseStudioBootstrap(await page.content(), page.url());
    editData.delegatedSessionId = editData.delegatedSessionId || data.delegatedSessionId;
    if (!editData.channelRoleType) editData.channelRoleType = data.channelRoleType;
  } catch (_) { editData = data; }
  await applyMetadataSchedule(page, session, sapisid, editData, videoId, publishMode, scheduledUnixSeconds);
  return true;
}

async function setThumbnail(page, thumbPath) {
  if (!thumbPath) return;
  const inputs = page.locator("input[type=file]");
  const count = await inputs.count();
  for (let i = 0; i < count; i++) {
    const accept = (await inputs.nth(i).getAttribute("accept").catch(() => "")) || "";
    if (/image|jpeg|png|webp/i.test(accept)) {
      await inputs.nth(i).setInputFiles(thumbPath);
      await page.waitForTimeout(1500);
      return;
    }
  }
  throw new Error("YouTube не показал поле превью.");
}

async function applyItemThumbnail(page, item, videoId, packIndex, packTotal) {
  const base = {
    profileId: job.profileId,
    localJobId: item.localJobId,
    fileName: path.basename(item.video),
    packIndex,
    packTotal,
    videoId
  };
  const thumb = String(item.thumbnail || "").trim();
  if (!thumb) {
    item.thumbnailStatus = "skipped";
    return { status: "skipped" };
  }
  const kind = String(item.contentKind || "long").toLowerCase();
  if (kind === "shorts" || kind === "short") {
    item.thumbnailStatus = "skipped";
    send("thumbnail", "Shorts: произвольное изображение не применяется — используйте кадр из видео.", Object.assign({ thumbnailStatus: "skipped" }, base));
    return { status: "skipped", reason: "Shorts не поддерживают произвольное превью через HTTP" };
  }
  try {
    const editUrl = `${STUDIO_ORIGIN}/video/${encodeURIComponent(videoId)}/edit`;
    await page.goto(editUrl, { waitUntil: "domcontentloaded", timeout: 90000 });
    await page.waitForTimeout(2000);
    await setThumbnail(page, thumb);
    item.thumbnailStatus = "applied";
    send("thumbnail", "Превью применено.", Object.assign({ percent: 93, thumbnailStatus: "applied" }, base));
    return { status: "applied" };
  } catch (error) {
    const reason = String(error && error.message || error);
    item.thumbnailStatus = "error";
    send("thumbnail_warning", "Видео загружено, превью не применено: " + reason, Object.assign({ thumbnailStatus: "error" }, base));
    return { status: "error", reason };
  }
}

async function acquireStudioSession(page) {
  let sessionToken = "";
  let tokenReadError = "";
  const onResponse = async response => {
    if (!/studio\.youtube\.com\/youtubei\/v1\/ars\/grst/i.test(response.url())) return;
    try {
      const body = await response.json();
      if (body && body.sessionToken) sessionToken = String(body.sessionToken);
    } catch (error) { tokenReadError = String(error && error.message || error); }
  };
  page.on("response", onResponse);
  try {
    send("youtube", "Открываю YouTube Studio. Медленный профиль не считается ошибкой — жду готовый канал до 5 минут…", { percent: 14 });
    await page.goto("https://studio.youtube.com", { waitUntil: "domcontentloaded", timeout: 180000 }).catch(error => {
      send("youtube", "Первичная загрузка Studio идёт медленно — продолжаю ждать в открытом профиле…", { percent: 15 });
    });
    await waitForStudioChannel(page);
    send("youtube", "Канал YouTube Studio открылся. Получаю данные сессии…", { percent: 16 });

    const deadline = Date.now() + STUDIO_READY_TIMEOUT_MS;
    let reloaded = false;
    let data = null;
    let nextHeartbeat = 0;
    while (Date.now() < deadline) {
      if (page.isClosed()) throw new Error("Окно Dolphin было закрыто во время чтения сессии YouTube.");
      try { data = parseStudioBootstrap(await page.content(), page.url()); } catch (_) {}
      if (data && sessionToken) return { data, sessionToken };

      const state = studioPageState(page.url(), await page.locator("body").innerText({ timeout: 3000 }).catch(() => ""));
      if (state.blocker) throw new Error(state.blocker);
      if (Date.now() >= nextHeartbeat) {
        send("youtube", "Канал открыт, ожидаю подтверждение сессии YouTube…", { percent: 16 });
        nextHeartbeat = Date.now() + 10000;
      }
      if (!reloaded && Date.now() > deadline - STUDIO_READY_TIMEOUT_MS + 60000) {
        reloaded = true;
        send("youtube", "Сессия ещё не подтверждена — один раз обновляю Studio и продолжаю ждать…", { percent: 16 });
        await page.reload({ waitUntil: "domcontentloaded", timeout: 180000 }).catch(() => {});
        await waitForStudioChannel(page, Math.max(1000, deadline - Date.now()));
      }
      await page.waitForTimeout(1000);
    }
    const detail = tokenReadError ? " Последняя ошибка чтения: " + tokenReadError.slice(0, 120) : "";
    throw new Error("Канал открылся, но YouTube не подтвердил sessionToken за 5 минут. Ничего не загружено; профиль оставлен открытым." + detail);
  } finally {
    page.off("response", onResponse);
  }
}

async function uploadBinary(page, uploadUrl, filePath) {
  const size = fs.statSync(filePath).size;
  const handle = fs.openSync(filePath, "r");
  let offset = 0;
  let finalText = "";
  try {
    while (offset < size) {
      const length = Math.min(CHUNK_SIZE, size - offset);
      const buffer = Buffer.allocUnsafe(length);
      const read = fs.readSync(handle, buffer, 0, length, offset);
      if (read !== length) throw new Error(`Не удалось прочитать видео: ожидалось ${length}, прочитано ${read}.`);
      const final = offset + read >= size;
      const response = await studioFetch(page, uploadUrl, {
        method: "POST", timeoutMs: 5 * 60 * 1000,
        headers: {
          "content-type": "application/octet-stream",
          "x-goog-upload-command": final ? "upload, finalize" : "upload",
          "x-goog-upload-offset": String(offset)
        },
        base64Body: buffer.toString("base64")
      });
      finalText = response.text || finalText;
      offset += read;
      send("upload", `Передано ${Math.round(offset / 1024 / 1024)} из ${Math.round(size / 1024 / 1024)} МБ`, {
        percent: 25 + Math.round(offset / size * 50)
      });
    }
  } finally { fs.closeSync(handle); }
  let parsed;
  try { parsed = JSON.parse(finalText); } catch (_) { parsed = null; }
  const id = parsed && (parsed.scottyResourceId || parsed.resourceId || parsed.id);
  const normalized = typeof id === "string" ? id : id && (id.id || id.scottyResourceId);
  if (!normalized) throw new Error("YouTube принял байты, но не вернул scottyResourceId. Повторно не загружайте — сначала проверьте Studio.");
  return String(normalized);
}

async function uploadOne(page, context, session, sapisid, item, packIndex, packTotal) {
  const data = session.data;
  const headers = authHeaders(data, sapisid);
  const frontEndUploadId = `innertube_studio:${crypto.randomUUID().toUpperCase()}:0`;
  const base = {
    profileId: job.profileId,
    localJobId: item.localJobId,
    fileName: path.basename(item.video),
    packIndex,
    packTotal
  };
  send("upload", `(${packIndex}/${packTotal}) Создаю загрузку…`, Object.assign({ percent: 20 }, base));
  const start = await studioFetch(page, `https://upload.youtube.com/upload/studio?authuser=${encodeURIComponent(data.authUser)}`, {
    method: "POST", timeoutMs: 90000,
    headers: Object.assign({}, headers, { "x-goog-upload-command": "start", "x-goog-upload-protocol": "resumable" }),
    body: JSON.stringify({ frontendUploadId: frontEndUploadId })
  });
  const uploadUrl = start.headers["x-goog-upload-url"];
  if (!uploadUrl || !/^https:\/\//i.test(uploadUrl)) throw new Error("YouTube не вернул URL загрузки. Ничего не загружено.");

  const scottyId = await uploadBinary(page, uploadUrl, item.video);
  send("video_created", "Файл принят. Создаю ролик…", Object.assign({ percent: 78, lastStage: "video_created" }, base));
  const create = await studioFetch(page, `${STUDIO_ORIGIN}/youtubei/v1/upload/createvideo?key=${encodeURIComponent(data.apiKey)}&alt=json`, {
    method: "POST", timeoutMs: 90000, headers,
    body: JSON.stringify(makeCreateVideoBody(data, session.sessionToken, frontEndUploadId, scottyId, item.title))
  });
  let createJson;
  try { createJson = JSON.parse(create.text); } catch (_) { createJson = null; }
  if (createJson && createJson.error) throw new Error("YouTube отклонил создание видео: " + JSON.stringify(createJson.error).slice(0, 240));
  const videoId = createJson && createJson.videoId;
  if (!videoId) throw new Error("Файл загружен, но YouTube не подтвердил создание видео. Повторно не запускайте; профиль оставлен открытым.");

  const publishMode = resolvePublishMode(job);
  const scheduleLabel = publishMode === "immediate" ? "Публикую сразу…"
    : publishMode === "private" ? "Сохраняю как приватное…"
      : "Ставлю отложенную публикацию…";
  send("schedule", scheduleLabel, Object.assign({ percent: 88, videoId, lastStage: "video_created" }, base));
  try {
    await applyMetadataSchedule(page, session, sapisid, data, videoId, publishMode, item.scheduledUnixSeconds);
  } catch (scheduleError) {
    if (!isSchedule403(scheduleError)) throw scheduleError;
    try {
      await applyScheduleViaEditPage(page, session, sapisid, data, videoId, publishMode, item.scheduledUnixSeconds);
    } catch (fallbackError) {
      return {
        videoId,
        url: `https://youtu.be/${videoId}`,
        manualCheck: true,
        scheduleError: String(scheduleError && scheduleError.message || scheduleError),
        lastStage: "video_created"
      };
    }
  }

  let editPageWarning = "";
  try {
    const editUrl = `${STUDIO_ORIGIN}/video/${encodeURIComponent(videoId)}/edit`;
    await page.goto(editUrl, { waitUntil: "domcontentloaded", timeout: 90000 });
    await page.waitForTimeout(2500);
    if (!page.url().includes(`/video/${videoId}/`)) {
      editPageWarning = "Проверочная страница видео не открылась — HTTP-метаданные уже применены.";
      record("warning", editPageWarning, { videoId, pageUrl: safeDiagnosticUrl(page.url()) });
    }
  } catch (editError) {
    editPageWarning = "Страница edit не открылась: " + String(editError && editError.message || editError);
    record("warning", editPageWarning, { videoId });
  }

  const thumb = await applyItemThumbnail(page, item, videoId, packIndex, packTotal);
  return {
    videoId,
    url: `https://youtu.be/${videoId}`,
    editPageWarning,
    thumbnailStatus: thumb.status,
    thumbnailWarning: thumb.reason || ""
  };
}

async function main() {
  const jobPath = process.argv[2];
  if (!jobPath || !fs.existsSync(jobPath)) throw new Error("Файл задания не найден.");
  const rawJob = JSON.parse(fs.readFileSync(jobPath, "utf8"));
  setupDiagnostics(jobPath, rawJob && rawJob.runId);
  job = validateBatchJob(rawJob);
  job.token = String(process.env.VIDEOBATCH_DOLPHIN_TOKEN || "").trim();
  if (!job.token) throw new Error("API-токен Dolphin не передан.");
  const total = job.items.length;
  send("start", `HTTP batch ${BUILD} · ${total} видео`, { profileId: job.profileId, percent: 1, packTotal: total });

  const { chromium } = require("playwright-core");
  const endpoint = await startOrConnectProfile();
  send("dolphin", "Профиль подключён. Профиль не закрывается после пачки.", { profileId: job.profileId, percent: 6 });
  const browser = await chromium.connectOverCDP(endpoint, { timeout: 60000 });
  const context = browser.contexts()[0];
  if (!context) throw new Error("Dolphin не вернул контекст браузера.");

  const [actualIp, computerIp] = await Promise.all([browserIp(context), directIp()]);
  assertProxy(job.expectedIp, actualIp, computerIp);
  send("ip", `Прокси подтверждён: ${actualIp}`, { profileId: job.profileId, ip: actualIp, percent: 12 });

  const page = context.pages()[0] || await context.newPage();
  activePage = page;
  attachPageDiagnostics(page);
  const session = await acquireStudioSession(page);
  assertExpectedChannel(job.expectedChannelId, session.data.channelId);
  const cookies = await context.cookies([STUDIO_ORIGIN, "https://youtube.com"]);
  const sapisidCookie = cookies.find(cookie => cookie.name === "SAPISID") || cookies.find(cookie => cookie.name === "__Secure-3PAPISID");
  if (!sapisidCookie || !sapisidCookie.value) throw new Error("В Dolphin-профиле нет SAPISID. Войдите в YouTube вручную; cookies нигде не сохранялись.");
  send("youtube", `Канал подтверждён: ${session.data.channelId}.`, {
    profileId: job.profileId, percent: 17, hasDelegatedSessionId: !!session.data.delegatedSessionId
  });

  for (let i = 0; i < total; i++) {
    const item = job.items[i];
    const packIndex = i + 1;
    try {
      const result = await uploadOne(page, context, session, sapisidCookie.value, item, packIndex, total);
      if (result.manualCheck) {
        send("manual_check", `(${packIndex}/${total}) Ролик создан: ${result.url}. Расписание не сохранено — проверьте в Studio.`, {
          success: false,
          profileId: job.profileId,
          localJobId: item.localJobId,
          fileName: path.basename(item.video),
          packIndex,
          packTotal: total,
          percent: Math.min(95, Math.floor(18 + (82 * packIndex) / total)),
          url: result.url,
          videoId: result.videoId,
          lastStage: result.lastStage || "video_created",
          error: result.scheduleError || "metadata_update 403",
          diagnosticFile,
          keptOpen: job.keepProfileOpen !== false
        });
        continue;
      }
      const doneText = result.editPageWarning
        ? `(${packIndex}/${total}) Загружено: ${result.url} · ${result.editPageWarning}`
        : result.thumbnailStatus === "error"
          ? `(${packIndex}/${total}) Загружено: ${result.url} · превью не применено`
          : `(${packIndex}/${total}) Готово: ${result.url}`;
      send("done", doneText, {
        success: true,
        profileId: job.profileId,
        localJobId: item.localJobId,
        fileName: path.basename(item.video),
        packIndex,
        packTotal: total,
        percent: Math.min(100, Math.floor(18 + (82 * packIndex) / total)),
        url: result.url,
        videoId: result.videoId,
        ip: actualIp,
        keptOpen: job.keepProfileOpen !== false,
        thumbnailStatus: result.thumbnailStatus || item.thumbnailStatus || "skipped",
        warning: result.editPageWarning || result.thumbnailWarning || ""
      });
    } catch (error) {
      if (isAmbiguousUploadError(error)) {
        send("manual_check", "Требуется проверка — возможна созданная запись. " + (error.message || error), {
          profileId: job.profileId,
          localJobId: item.localJobId,
          fileName: path.basename(item.video),
          lastStage: "upload",
          diagnosticFile,
          keptOpen: true,
          success: false
        });
      }
      throw error;
    }
  }
  send("done", `Пачка из ${total} видео завершена.`, {
    success: true,
    profileId: job.profileId,
    percent: 100,
    ip: actualIp,
    keptOpen: job.keepProfileOpen !== false,
    packTotal: total,
    batchComplete: true
  });
  finishProcess(0);
}

if (require.main === module) {
  main().catch(async error => {
    const details = await captureFailureDiagnostics(error);
    fail(error, Object.assign({ keptOpen: true, diagnosticFile }, details));
  });
}

module.exports = {
  automationEndpoint, normalizeIp, assertProxy, parseStudioBootstrap, parseChannelRoleType,
  studioPageState, waitForStudioChannel, assertExpectedChannel,
  makeContext, makeCreateVideoBody, makeMetadataBody, validateJob, validateBatchJob, resolvePublishMode,
  authHeaders, isSchedule403, isAmbiguousUploadError,
  safeDiagnosticUrl, redactDiagnostic, dolphinProfileStatus, dolphinProfileIsRunning,
  retryableDolphinStartError
};
