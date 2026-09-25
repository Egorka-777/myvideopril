"use strict";

/**
 * VideoBatch · TikTok HTTP uploader v5.
 * All TikTok API traffic via page.evaluate(fetch) in Dolphin browser context.
 */

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { chromium } = require("playwright-core");
const {
  awsSigV4Sign,
  buildCanonicalQueryString,
  normalizeCanonicalUri,
  rfc3986Encode,
  EMPTY_PAYLOAD_HASH
} = require("./tiktok-aws-sigv4.js");

const BUILD = "2026-09-24-tiktok-http-v5";
const TRANSPORT = "http";
const CHUNK_SIZE = 2 * 1024 * 1024;
const WORKER_EXIT_DELAY_MS = 150;
const TIKTOK_SCHEDULE_MIN_SEC = 900;
const TIKTOK_SCHEDULE_MAX_SEC = 864000;
const APPLY_UPLOAD_REGION = "ap-singapore-1";
const APPLY_UPLOAD_HOST = "www.tiktok.com";
const APPLY_UPLOAD_PATH = "/top/v1";

const ITEM_STATES = Object.freeze([
  "preflight", "authorized", "apply_confirmed", "project_created", "uploading", "uploaded",
  "committed", "publishing", "confirmed", "manual_check", "failed_before_upload"
]);

const NOTICE_MIT =
  "Portions of the TikTok web upload sequence follow the MIT-licensed TiktokAutoUploader project (makiisthenes/TiktokAutoUploader).";

let job = null;
let browser = null;
let profileStarted = false;
let activePage = null;
let diagnosticFile = "";
let itemState = "preflight";
let bytesSent = 0;
let projectIdSafe = "";
let uploadIdSafe = "";
let videoIdSafe = "";
let routeContext = { browserIp: "", transportIp: "", computerIp: "" };

function canSafeRetry(state) {
  return state === "preflight" || state === "failed_before_upload";
}

function setItemState(next) {
  itemState = String(next || "preflight");
  record("state", itemState, { projectId: projectIdSafe, uploadId: uploadIdSafe, videoId: videoIdSafe });
}

function redactDiagnostic(value) {
  return String(value == null ? "" : value)
    .replace(/\x1b\[[0-9;]*m/g, "")
    .replace(/AKTP[A-Z0-9]{10,}/gi, "AKTP[скрыто]")
    .replace(/AKIA[A-Z0-9]{10,}/gi, "AKIA[скрыто]")
    .replace(/(Cookie|sessionid|msToken|Authorization|access[_ ]?key|secret[_ ]?key|session[_ ]?token|X-Bogus|_signature|proxyPassword|proxyUser)\s*[:=]\s*[^\s,;]+/gi, "$1=[скрыто]")
    .replace(/Bearer\s+[A-Za-z0-9._~+\/-]+=*/gi, "Bearer [скрыто]");
}

function setupDiagnostics(jobPath) {
  const dir = path.join(path.dirname(path.dirname(jobPath)), "diagnostics");
  fs.mkdirSync(dir, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  diagnosticFile = path.join(dir, `tiktok-http-${stamp}-${process.pid}.jsonl`);
}

function record(stage, text, extra = {}) {
  if (!diagnosticFile) return;
  try {
    const safe = {};
    for (const [k, v] of Object.entries(extra)) {
      safe[k] = /token|cookie|secret|password|authorization|msToken|sessionid|accesskey|sessiontoken/i.test(k)
        ? "[скрыто]"
        : (typeof v === "string" ? redactDiagnostic(v) : v);
    }
    fs.appendFileSync(diagnosticFile, JSON.stringify({
      at: new Date().toISOString(), stage, text: redactDiagnostic(text), itemState, ...safe
    }) + "\n", "utf8");
  } catch (_) {}
}

function send(stage, text, extra = {}) {
  record(stage, text, extra);
  process.stdout.write(JSON.stringify(Object.assign({
    stage, text: redactDiagnostic(text), transport: TRANSPORT, itemState, diagnosticFile
  }, extra)) + "\n");
}

function finishProcess(code) {
  setTimeout(() => process.exit(code), WORKER_EXIT_DELAY_MS);
}

function fail(error, extra = {}) {
  const text = redactDiagnostic(error && error.message || error || "Неизвестная ошибка");
  send("error", text, Object.assign({ success: false, error: text, keptOpen: true }, extra));
  finishProcess(1);
}

function manualCheck(message, extra = {}) {
  send("manual_check", message, Object.assign({
    success: false, error: message, keptOpen: true, projectId: projectIdSafe, uploadId: uploadIdSafe
  }, extra));
  finishProcess(2);
}

function generateCreationId(len = 21) {
  const chars = "abcdefghijklmnopqrstuvwxyz0123456789";
  let out = "";
  for (let i = 0; i < len; i++) out += chars[crypto.randomInt(0, chars.length)];
  return out;
}

function crc32Buffer(buf) {
  let crc = 0 ^ -1;
  for (let i = 0; i < buf.length; i++) crc = (crc >>> 8) ^ CRC_TABLE[(crc ^ buf[i]) & 0xff];
  return ((crc ^ -1) >>> 0).toString(16).padStart(8, "0");
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    table[i] = c >>> 0;
  }
  return table;
})();

function normalizeIp(value) {
  const ip = String(value || "").trim().toLowerCase();
  return /^[0-9a-f:.]+$/i.test(ip) && (ip.includes(".") || ip.includes(":")) ? ip : "";
}

function parseIpPayload(raw, kind) {
  const text = String(raw || "").trim();
  try {
    if (kind === "json-ip") return normalizeIp(JSON.parse(text).ip);
  } catch (_) {}
  return normalizeIp(text.split(/\s+/)[0]);
}

const IPIFY = "https://api.ipify.org?format=json";

async function dolphinApi(apiPath, options = {}, timeoutMs = 35000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(`http://127.0.0.1:${job.localPort}${apiPath}`, Object.assign({}, options, { signal: controller.signal }));
    const text = await response.text();
    let data = {};
    try { data = text ? JSON.parse(text) : {}; } catch { data = { raw: text }; }
    if (!response.ok || data.success === false) {
      throw new Error(`Dolphin API: HTTP ${response.status}. ${data.message || data.error || "операция отклонена"}`);
    }
    return data;
  } finally { clearTimeout(timer); }
}

function automationEndpoint(data) {
  const root = data && (data.automation || data.data || data);
  if (!root) return null;
  const port = root.port || root.automationPort;
  const ws = root.wsEndpoint || root.ws_endpoint;
  if (ws && /^wss?:\/\//i.test(ws)) return ws;
  if (ws && port) return `ws://127.0.0.1:${port}${ws.startsWith("/") ? "" : "/"}${ws}`;
  return port ? `http://127.0.0.1:${port}` : null;
}

async function startOrConnectProfile() {
  send("dolphin", "Подключаю профиль Dolphin…", { percent: 3 });
  await dolphinApi("/v1.0/auth/login-with-token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ token: job.token })
  });
  const started = await dolphinApi(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}/start?automation=1`);
  profileStarted = true;
  const endpoint = automationEndpoint(started);
  if (!endpoint) throw new Error("Dolphin не вернул порт автоматизации.");
  send("dolphin", "Профиль Dolphin подключён.", { percent: 8 });
  return endpoint;
}

async function fetchComputerIp() {
  try {
    const response = await fetch(IPIFY, { cache: "no-store" });
    if (!response.ok) return "";
    return parseIpPayload(await response.text(), "json-ip");
  } catch (_) {
    return "";
  }
}

async function readBrowserIp(page) {
  const text = await page.evaluate(async (url) => {
    const r = await fetch(url, { cache: "no-store", credentials: "omit" });
    return r.text();
  }, IPIFY);
  return parseIpPayload(text, "json-ip");
}

async function browserFetch(page, method, url, { headers = {}, body = null, bodyBase64 = null } = {}) {
  const payload = {
    method: String(method || "GET").toUpperCase(),
    url,
    headers: headers || {},
    body: typeof body === "string" ? body : null,
    bodyBase64: bodyBase64 || (Buffer.isBuffer(body) ? body.toString("base64") : null)
  };
  const result = await page.evaluate(async (p) => {
    const init = { method: p.method, headers: p.headers, credentials: "include", cache: "no-store" };
    if (p.bodyBase64 != null) {
      const raw = atob(p.bodyBase64);
      const arr = new Uint8Array(raw.length);
      for (let i = 0; i < raw.length; i++) arr[i] = raw.charCodeAt(i);
      init.body = arr;
    } else if (p.body != null) init.body = p.body;
    const response = await fetch(p.url, init);
    const text = await response.text();
    return {
      ok: response.ok,
      status: response.status,
      text,
      serverDate: response.headers.get("date") || ""
    };
  }, payload);
  let json = null;
  try { json = result.text ? JSON.parse(result.text) : null; } catch { json = null; }
  return Object.assign({}, result, { json });
}

async function readTransportIp(page) {
  const res = await browserFetch(page, "GET", IPIFY);
  return parseIpPayload(res.text, "json-ip");
}

function noteClockSkew(serverDateHdr) {
  if (!serverDateHdr) return;
  const server = new Date(serverDateHdr);
  if (isNaN(server.getTime())) return;
  const skewSec = Math.round(Math.abs(Date.now() - server.getTime()) / 1000);
  if (skewSec > 300) {
    send("warning", `Разница часов ПК и сервера: ${skewSec} с`, { clockSkewSec: skewSec, pcTime: new Date().toISOString(), serverDate: serverDateHdr });
  }
}

async function verifyProxyRoute(page) {
  send("ip", "Проверяю browser IP…", { percent: 10 });
  const browserIp = await readBrowserIp(page);
  if (!browserIp) throw new Error("Не удалось определить browser IP через Dolphin.");
  send("ip", `browser IP: ${browserIp}`, { browserIp, percent: 11 });

  send("ip", "Проверяю HTTP transport IP…", { percent: 12 });
  const transportIp = await readTransportIp(page);
  if (!transportIp) throw new Error("Не удалось определить HTTP transport IP.");
  send("ip", `HTTP transport IP: ${transportIp}`, { transportIp, percent: 13 });

  if (browserIp !== transportIp) {
    throw new Error("browser IP и HTTP transport IP не совпадают — остановка до preflight.");
  }
  send("ip", "IP совпадает", { percent: 14 });

  const computerIp = await fetchComputerIp();
  if (computerIp) send("ip", `computer IP (Node): ${computerIp}`, { computerIp, percent: 15 });

  routeContext = { browserIp, transportIp, computerIp };
  return routeContext;
}

async function extractSession(context) {
  const cookies = await context.cookies(["https://www.tiktok.com", "https://tiktok.com"]);
  const session = cookies.find(c => c.name === "sessionid");
  const dc = cookies.find(c => c.name === "tt-target-idc");
  const msToken = cookies.find(c => c.name === "msToken");
  if (!session || !session.value) {
    throw new Error("TikTok-сессия не найдена. Войдите в TikTok в этом профиле Dolphin.");
  }
  send("session", "TikTok session подтверждена.", { percent: 18 });
  return {
    sessionId: session.value,
    dcId: (dc && dc.value) || "useast2a",
    msToken: (msToken && msToken.value) || "",
    cookieHeader: cookies.filter(c => /\.tiktok\.com$/i.test(c.domain)).map(c => `${c.name}=${c.value}`).join("; ")
  };
}

function uploadTokenMeta(token, endpointHost, region) {
  return {
    hasAccessKeyId: !!token.access_key_id,
    hasSecretKey: !!token.secret_acess_key,
    hasSessionToken: !!token.session_token,
    accessKeyPrefix: token.access_key_id ? String(token.access_key_id).slice(0, 4) + "…" : "",
    region: region || APPLY_UPLOAD_REGION,
    endpointHost: endpointHost || APPLY_UPLOAD_HOST,
    expiresIn: token.expires_in || token.expire || null
  };
}

function describeTikTokApiFailure(action, res) {
  const status = res && res.status != null ? res.status : "?";
  const json = res && res.json;
  const err = json && json.ResponseMetadata && json.ResponseMetadata.Error;
  const code = err && err.Code ? err.Code : "";
  const msg = err && err.Message ? err.Message : (json && (json.message || json.status_msg || json.error)) || "";
  const requestId = json && json.ResponseMetadata && json.ResponseMetadata.RequestId ? json.ResponseMetadata.RequestId : "";
  return { action, status, code, message: msg, requestId, snippet: redactDiagnostic(String((res && res.text) || "").slice(0, 180)) };
}

function assertTikTokApiOk(res, action, diagBase = {}) {
  noteClockSkew(res.serverDate);
  const failInfo = describeTikTokApiFailure(action, res);
  const diag = Object.assign({}, diagBase, failInfo, {
    httpStatus: res.status,
    serverDate: res.serverDate || "",
    browserIp: routeContext.browserIp,
    transportIp: routeContext.transportIp
  });
  record("tiktok_api", action, diag);

  const err = res.json && res.json.ResponseMetadata && res.json.ResponseMetadata.Error;
  if (err) {
    if (String(err.Code || "") === "CheckAuthenticationError" || /CheckAuthenticationError/i.test(String(err.Message || ""))) {
      throw Object.assign(new Error("TikTok отклонил временные credentials на ApplyUploadInner."), { diag, authError: true });
    }
    throw Object.assign(new Error(`${action}: ${err.Code || "Error"} — ${redactDiagnostic(err.Message || "")}`), { diag });
  }
  if (res.json && res.json.status_code != null && res.json.status_code !== 0) {
    throw Object.assign(new Error(`${action}: status_code ${res.json.status_code}`), { diag });
  }
  if (!res.ok) {
    throw Object.assign(new Error(`${action} HTTP ${res.status}.`), { diag });
  }
  return diag;
}

function parseApplyUploadResult(res) {
  if (!res || !res.json || !res.json.Result) return null;
  const nodes = res.json.Result.InnerUploadAddress && res.json.Result.InnerUploadAddress.UploadNodes;
  if (!Array.isArray(nodes) || !nodes.length) return null;
  const node = nodes[0];
  const store = node.StoreInfos && node.StoreInfos[0];
  if (!node.Vid || !store || !store.StoreUri || !store.Auth || !node.UploadHost || !node.SessionKey) return null;
  return {
    videoId: String(node.Vid),
    storeUri: store.StoreUri,
    videoAuth: store.Auth,
    uploadHost: node.UploadHost,
    sessionKey: node.SessionKey
  };
}

function buildApplyUploadUrl(fileSize) {
  const q = new URLSearchParams({
    Action: "ApplyUploadInner",
    Version: "2020-11-19",
    SpaceName: "tiktok",
    FileType: "video",
    IsInner: "1",
    FileSize: String(fileSize),
    s: "g158iqx8434"
  });
  return `https://${APPLY_UPLOAD_HOST}${APPLY_UPLOAD_PATH}?${q.toString()}`;
}

function buildCommitUploadUrl() {
  const q = new URLSearchParams({
    Action: "CommitUploadInner",
    Version: "2020-11-19",
    SpaceName: "tiktok"
  });
  return `https://${APPLY_UPLOAD_HOST}${APPLY_UPLOAD_PATH}?${q.toString()}`;
}

async function fetchUploadToken(page, session) {
  const url = "https://www.tiktok.com/api/v1/video/upload/auth/?aid=1988";
  const res = await browserFetch(page, "GET", url, { headers: { Cookie: session.cookieHeader } });
  assertTikTokApiOk(res, "upload/auth", { endpointHost: "www.tiktok.com" });
  const token = res.json.video_token_v5;
  if (!token || !token.access_key_id || !token.secret_acess_key || !token.session_token) {
    throw new Error("upload/auth: нет обязательных полей video_token_v5.");
  }
  const meta = uploadTokenMeta(token, APPLY_UPLOAD_HOST, APPLY_UPLOAD_REGION);
  send("token", "upload token получен.", Object.assign({ percent: 20 }, meta));
  record("upload_token", "получен", meta);
  return token;
}

async function applyUploadInner(page, session, fileSize, uploadToken) {
  const url = buildApplyUploadUrl(fileSize);
  const signed = awsSigV4Sign({
    method: "GET",
    url,
    body: "",
    accessKeyId: uploadToken.access_key_id,
    secretAccessKey: uploadToken.secret_acess_key,
    sessionToken: uploadToken.session_token,
    region: APPLY_UPLOAD_REGION,
    service: "vod"
  });
  const headers = {
    Accept: "application/json, text/plain, */*",
    Authorization: signed.headers.Authorization,
    "x-amz-date": signed.headers["x-amz-date"],
    "x-amz-content-sha256": signed.headers["x-amz-content-sha256"],
    "x-amz-security-token": signed.headers["x-amz-security-token"],
    Cookie: session.cookieHeader
  };
  const res = await browserFetch(page, "GET", url, { headers });
  assertTikTokApiOk(res, "ApplyUploadInner", {
    endpointHost: APPLY_UPLOAD_HOST,
    region: APPLY_UPLOAD_REGION,
    signedHeaders: signed.signedHeaders
  });
  const parsed = parseApplyUploadResult(res);
  if (!parsed) {
    throw new Error("ApplyUploadInner: HTTP 200 без обязательных upload fields.");
  }
  send("apply", "ApplyUploadInner подтверждён.", {
    percent: 24,
    endpointHost: APPLY_UPLOAD_HOST,
    region: APPLY_UPLOAD_REGION,
    videoId: parsed.videoId
  });
  return parsed;
}

async function runHttpPreflight(page, session, fileSize) {
  setItemState("preflight");
  const uploadToken = await fetchUploadToken(page, session);
  setItemState("authorized");
  const applied = await applyUploadInner(page, session, fileSize, uploadToken);
  setItemState("apply_confirmed");
  send("preflight", "HTTP preflight пройден: ApplyUploadInner подтвердил credentials.", { percent: 26 });
  return { uploadToken, applied };
}

async function createProject(page, session) {
  const creationId = generateCreationId(21);
  const url = `https://www.tiktok.com/api/v1/web/project/create/?creation_id=${creationId}&type=1&aid=1988`;
  const res = await browserFetch(page, "POST", url, { headers: { Cookie: session.cookieHeader } });
  assertTikTokApiOk(res, "project/create");
  if (!res.json.project || !res.json.project.project_id) {
    throw new Error("project/create: нет project_id.");
  }
  projectIdSafe = String(res.json.project.project_id);
  setItemState("project_created");
  send("project", `project создан · ${projectIdSafe}.`, { percent: 28, projectId: projectIdSafe });
  return { creationId, projectId: projectIdSafe };
}

async function uploadVideoBytes(page, session, videoPath, uploadToken, applied) {
  const stat = fs.statSync(videoPath);
  const fileSize = stat.size;
  const videoId = applied.videoId;
  const storeUri = applied.storeUri;
  const videoAuth = applied.videoAuth;
  const uploadHost = applied.uploadHost;
  const sessionKey = applied.sessionKey;
  videoIdSafe = String(videoId);
  uploadIdSafe = crypto.randomUUID();

  setItemState("uploading");
  bytesSent = 0;
  const fd = fs.openSync(videoPath, "r");
  const crcs = [];
  try {
    let part = 1;
    while (bytesSent < fileSize) {
      const toRead = Math.min(CHUNK_SIZE, fileSize - bytesSent);
      const buf = Buffer.alloc(toRead);
      fs.readSync(fd, buf, 0, toRead, bytesSent);
      const crc = crc32Buffer(buf);
      crcs.push(crc);
      const partUrl = `https://${uploadHost}/${storeUri}?partNumber=${part}&uploadID=${uploadIdSafe}&phase=transfer`;
      const partRes = await browserFetch(page, "POST", partUrl, {
        headers: {
          Authorization: videoAuth,
          "Content-Type": "application/octet-stream",
          "Content-Disposition": 'attachment; filename="undefined"',
          "Content-Crc32": crc
        },
        body: buf
      });
      if (!partRes.ok) {
        setItemState("manual_check");
        throw new Error(`transfer часть ${part}: HTTP ${partRes.status}.`);
      }
      bytesSent += toRead;
      part += 1;
      send("upload", `Передано ${Math.round(bytesSent / 1024 / 1024)} из ${Math.round(fileSize / 1024 / 1024)} МБ`, {
        percent: Math.min(85, 30 + Math.floor((bytesSent / fileSize) * 50))
      });
    }
  } finally {
    fs.closeSync(fd);
  }
  setItemState("uploaded");
  send("upload", "Файл принят.", { percent: 82 });

  const finishUrl = `https://${uploadHost}/${storeUri}?uploadID=${uploadIdSafe}&phase=finish&uploadmode=part`;
  const finishBody = crcs.map((c, i) => `${i + 1}:${c}`).join(",");
  const finishRes = await browserFetch(page, "POST", finishUrl, {
    headers: { Authorization: videoAuth, "Content-Type": "text/plain;charset=UTF-8" },
    body: finishBody
  });
  if (!finishRes.ok) {
    setItemState("manual_check");
    throw new Error("finish не подтверждён.");
  }
  send("finish", "finish подтверждён.", { percent: 86 });

  const commitUrl = buildCommitUploadUrl();
  const commitBody = JSON.stringify({ SessionKey: sessionKey, Functions: [{ name: "GetMeta" }] });
  const signed = awsSigV4Sign({
    method: "POST",
    url: commitUrl,
    body: commitBody,
    accessKeyId: uploadToken.access_key_id,
    secretAccessKey: uploadToken.secret_acess_key,
    sessionToken: uploadToken.session_token,
    region: APPLY_UPLOAD_REGION,
    service: "vod"
  });
  const commitRes = await browserFetch(page, "POST", commitUrl, {
    headers: {
      Accept: "application/json, text/plain, */*",
      Authorization: signed.headers.Authorization,
      "x-amz-date": signed.headers["x-amz-date"],
      "x-amz-content-sha256": signed.headers["x-amz-content-sha256"],
      "x-amz-security-token": signed.headers["x-amz-security-token"],
      "Content-Type": "application/json",
      Cookie: session.cookieHeader
    },
    body: commitBody
  });
  assertTikTokApiOk(commitRes, "CommitUploadInner", { endpointHost: APPLY_UPLOAD_HOST, region: APPLY_UPLOAD_REGION });
  setItemState("committed");
  send("commit", "commit подтверждён.", { percent: 88, videoId: videoIdSafe });
  return { videoId, sessionKey };
}

async function publishPost(page, session, { creationId, projectId, videoId, caption, publishMode, scheduledUnixSeconds }) {
  setItemState("publishing");
  const scheduleOffset = publishMode === "scheduled" && scheduledUnixSeconds > 0
    ? Math.max(TIKTOK_SCHEDULE_MIN_SEC, Math.min(TIKTOK_SCHEDULE_MAX_SEC, scheduledUnixSeconds - Math.floor(Date.now() / 1000)))
    : 0;

  const payload = {
    post_common_info: { creation_id: creationId, enter_post_page_from: 1, post_type: 3 },
    feature_common_info_list: [{
      geofencing_regions: [],
      playlist_name: "",
      playlist_id: "",
      tcm_params: "{\"commerce_toggle_info\":{}}",
      sound_exemption: 0,
      anchors: [],
      vedit_common_info: { draft: "", video_id: videoId },
      privacy_setting_info: { visibility_type: 0, allow_duet: 1, allow_stitch: 1, allow_comment: 1 }
    }],
    single_post_req_list: [{
      batch_index: 0,
      video_id: videoId,
      is_long_video: 0,
      single_post_feature_info: { text: caption, text_extra: [], markup_text: caption, music_info: {}, poster_delay: 0 }
    }]
  };
  if (scheduleOffset > 0) payload.feature_common_info_list[0].schedule_time = scheduleOffset + Math.floor(Date.now() / 1000);

  const msToken = session.msToken || "";
  const postPath = `/tiktok/web/project/post/v1/?app_name=tiktok_web&channel=tiktok_web&device_platform=web&aid=1988&msToken=${encodeURIComponent(msToken)}`;

  const postResult = await page.evaluate(async ({ postPath, payload }) => {
    const response = await fetch("https://www.tiktok.com" + postPath, {
      method: "POST",
      headers: { "content-type": "application/json" },
      credentials: "include",
      body: JSON.stringify(payload)
    });
    return { status: response.status, text: await response.text(), serverDate: response.headers.get("date") || "" };
  }, { postPath, payload });

  noteClockSkew(postResult.serverDate);
  let body = null;
  try { body = postResult.text ? JSON.parse(postResult.text) : null; } catch { body = null; }
  if (postResult.status < 200 || postResult.status >= 300 || !body || body.status_code !== 0) {
    setItemState("manual_check");
    manualCheck("post не подтверждён сервером TikTok (HTTP " + postResult.status + ").", { projectId, videoId });
    return null;
  }
  send("post", "post подтверждён.", { percent: 94, projectId, videoId });

  const listRes = await browserFetch(page, "GET", "https://www.tiktok.com/api/v1/web/project/list/?aid=1988", {
    headers: { Cookie: session.cookieHeader }
  });
  if (listRes.ok && listRes.json && Array.isArray(listRes.json.infos)) {
    send("status", "status подтверждён.", { percent: 96, projectCount: listRes.json.infos.length });
  } else {
    send("status", "status: project list недоступен, post уже подтверждён.", { percent: 96 });
  }

  setItemState("confirmed");
  if (scheduleOffset > 0) {
    const local = new Date(scheduledUnixSeconds * 1000);
    send("schedule", `Запланировано на ${local.toLocaleString()}`, { percent: 98, scheduledUnixSeconds });
  } else {
    send("done_item", "TikTok подтвердил публикацию.", { percent: 98, projectId, videoId });
  }
  return { projectId, videoId, scheduled: scheduleOffset > 0 };
}

function validateItem(item, index) {
  const video = String(item.video || "").trim();
  const caption = String(item.caption || item.description || "").trim();
  if (!video || !fs.existsSync(video)) throw new Error(`Ролик ${index}: файл не найден.`);
  const stat = fs.statSync(video);
  if (!stat.isFile() || stat.size < 1) throw new Error(`Ролик ${index}: пустой файл.`);
  if (!caption) throw new Error(`Ролик ${index}: пустая подпись.`);
  if (caption.length > 2200) throw new Error(`Ролик ${index}: подпись длиннее 2200 символов.`);
  const publishMode = String(item.publishMode || job.publishMode || "immediate").trim().toLowerCase();
  let scheduledUnixSeconds = Number(item.scheduledUnixSeconds || 0);
  if (publishMode === "scheduled") {
    if (!Number.isFinite(scheduledUnixSeconds) || scheduledUnixSeconds <= Math.floor(Date.now() / 1000) + TIKTOK_SCHEDULE_MIN_SEC) {
      throw new Error(`Ролик ${index}: время планирования должно быть от ${TIKTOK_SCHEDULE_MIN_SEC / 60} мин до 10 суток.`);
    }
  } else {
    scheduledUnixSeconds = 0;
  }
  return { video, caption, publishMode, scheduledUnixSeconds, localJobId: String(item.localJobId || `item-${index}`) };
}

async function main() {
  const jobPath = process.argv[2];
  if (!jobPath || !fs.existsSync(jobPath)) throw new Error("Не найдено задание загрузки.");
  job = JSON.parse(fs.readFileSync(jobPath, "utf8"));
  setupDiagnostics(jobPath);
  job.token = process.env.VIDEOBATCH_DOLPHIN_TOKEN || job.token;
  if (!job.token || !job.profileId) throw new Error("Укажите токен Dolphin и ID профиля.");
  if (!Number.isInteger(job.localPort) || job.localPort < 1 || job.localPort > 65535) throw new Error("Некорректный порт Dolphin.");

  const items = (Array.isArray(job.items) && job.items.length ? job.items : [{
    video: job.video, caption: job.caption || job.description || job.title
  }]).map((it, i) => validateItem(it, i + 1));

  send("start", `TikTok HTTP worker ${BUILD}`, { percent: 1, transport: TRANSPORT, diagnosticFile, notice: NOTICE_MIT });

  const endpoint = await startOrConnectProfile();
  browser = await chromium.connectOverCDP(endpoint, { timeout: 60000 });
  const context = browser.contexts()[0];
  if (!context) throw new Error("Не удалось подключиться к окну профиля Dolphin.");
  const page = await context.newPage();
  activePage = page;

  try {
    await page.goto("https://www.tiktok.com/", { waitUntil: "domcontentloaded", timeout: 90000 });
    await page.waitForTimeout(1200);

    await verifyProxyRoute(page);
    const session = await extractSession(context);

    let lastUrl = "";
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      send("batch", `Пачка ${i + 1}/${items.length}: HTTP-загрузка…`, { packIndex: i + 1, packTotal: items.length, percent: 25 });

      const fileSize = fs.statSync(item.video).size;
      const { uploadToken, applied } = await runHttpPreflight(page, session, fileSize);
      const { creationId, projectId } = await createProject(page, session);
      await uploadVideoBytes(page, session, item.video, uploadToken, applied);
      const pub = await publishPost(page, session, {
        creationId, projectId, videoId: videoIdSafe, caption: item.caption,
        publishMode: item.publishMode, scheduledUnixSeconds: item.scheduledUnixSeconds
      });
      if (pub) lastUrl = pub.url || lastUrl;
      if (i < items.length - 1) setItemState("preflight");
    }

    if (job.keepProfileOpen !== false) {
      send("dolphin", "Профиль оставлен открытым после успешной HTTP-загрузки.", { percent: 99 });
    } else {
      await browser.close().catch(() => {});
      browser = null;
      try { await dolphinApi(`/v1.0/browser_profiles/${encodeURIComponent(job.profileId)}/stop`); } catch (_) {}
      profileStarted = false;
    }

    send("done", items.length > 1 ? `Пачка ${items.length} роликов передана через HTTP.` : "TikTok HTTP upload завершён.", {
      success: true,
      ip: routeContext.transportIp,
      browserIp: routeContext.browserIp,
      transportIp: routeContext.transportIp,
      url: lastUrl,
      percent: 100,
      transport: TRANSPORT,
      diagnosticFile
    });
    finishProcess(0);
  } catch (e) {
    if (itemState === "uploading" || itemState === "uploaded" || itemState === "committed") {
      manualCheck((e && e.message) || String(e), { projectId: projectIdSafe, uploadId: uploadIdSafe, diag: e.diag || null });
      return;
    }
    throw e;
  }
}

if (require.main === module) {
  process.on("SIGINT", () => finishProcess(130));
  process.on("SIGTERM", () => finishProcess(143));
  main().catch(async e => {
    if (activePage && !activePage.isClosed() && diagnosticFile) {
      const imagePath = diagnosticFile.replace(/\.jsonl$/i, "-error.png");
      await activePage.screenshot({ path: imagePath, fullPage: false }).catch(() => {});
      record("screenshot", "Снимок при ошибке", { imagePath });
    }
    if (browser) await browser.close().catch(() => {});
    fail(e, { keptOpen: true, itemState, diag: e.diag || null, diagnosticFile });
  });
}

module.exports = {
  TRANSPORT,
  BUILD,
  ITEM_STATES,
  canSafeRetry,
  redactDiagnostic,
  crc32Buffer,
  validateItem,
  generateCreationId,
  NOTICE_MIT,
  awsSigV4Sign,
  buildCanonicalQueryString,
  normalizeCanonicalUri,
  rfc3986Encode,
  EMPTY_PAYLOAD_HASH,
  parseApplyUploadResult,
  describeTikTokApiFailure,
  assertTikTokApiOk,
  buildApplyUploadUrl,
  uploadTokenMeta,
  verifyProxyRoute,
  browserFetch,
  readBrowserIp,
  readTransportIp
};
