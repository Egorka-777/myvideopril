"use strict";

/**
 * VideoBatch · TikTok HTTP uploader (beta).
 * Session/cookies via Dolphin+Playwright only — no TikTok Studio form, no setInputFiles, no caption DOM, no Post click.
 *
 * Upload flow inspired by MIT TiktokAutoUploader (makiisthenes/TiktokAutoUploader) — see NOTICE below.
 */

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { chromium } = require("playwright-core");

const BUILD = "2026-09-24-tiktok-http-v4-apply-upload";
const TRANSPORT = "http";
const CHUNK_SIZE = 5 * 1024 * 1024;
const WORKER_EXIT_DELAY_MS = 150;
const TIKTOK_SCHEDULE_MIN_SEC = 900;
const TIKTOK_SCHEDULE_MAX_SEC = 864000;

const ITEM_STATES = Object.freeze([
  "preflight", "authorized", "project_created", "uploading", "uploaded",
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

function canSafeRetry(state) {
  return state === "preflight" || state === "authorized" || state === "failed_before_upload";
}

function setItemState(next) {
  itemState = String(next || "preflight");
  record("state", itemState, { projectId: projectIdSafe, uploadId: uploadIdSafe, videoId: videoIdSafe });
}

function redactDiagnostic(value) {
  return String(value == null ? "" : value)
    .replace(/\x1b\[[0-9;]*m/g, "")
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
      safe[k] = /token|cookie|secret|password|authorization|msToken|sessionid/i.test(k)
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
    stage, text: redactDiagnostic(text), transport: TRANSPORT, itemState
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
  for (let i = 0; i < buf.length; i++) {
    crc = (crc >>> 8) ^ CRC_TABLE[(crc ^ buf[i]) & 0xff];
  }
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

function sha256(data) {
  return crypto.createHash("sha256").update(data).digest("hex");
}

function hmacSha256(key, data, encoding) {
  return crypto.createHmac("sha256", key).update(data, encoding).digest();
}

function awsSigV4Headers({ method, url, body, accessKeyId, secretAccessKey, sessionToken, region, service }) {
  const parsed = new URL(url);
  const amzDate = new Date().toISOString().replace(/[:-]|\.\d{3}/g, "");
  const dateStamp = amzDate.slice(0, 8);
  const upperMethod = String(method || "GET").toUpperCase();
  const payloadHash = sha256(body || "");
  const canonicalHeaders = {
    host: parsed.host,
    "x-amz-date": amzDate
  };
  if (sessionToken) canonicalHeaders["x-amz-security-token"] = sessionToken;
  if (upperMethod !== "GET") canonicalHeaders["x-amz-content-sha256"] = payloadHash;
  const signedHeaderKeys = Object.keys(canonicalHeaders).map(k => k.toLowerCase()).sort();
  const canonicalHeaderString = signedHeaderKeys.map(k => `${k}:${String(canonicalHeaders[k]).trim()}\n`).join("");
  const signedHeaders = signedHeaderKeys.join(";");
  const canonicalRequest = [
    upperMethod,
    parsed.pathname + (parsed.search || ""),
    "",
    canonicalHeaderString,
    signedHeaders,
    payloadHash
  ].join("\n");
  const credentialScope = `${dateStamp}/${region}/${service}/aws4_request`;
  const stringToSign = ["AWS4-HMAC-SHA256", amzDate, credentialScope, sha256(canonicalRequest)].join("\n");
  const kDate = hmacSha256("AWS4" + secretAccessKey, dateStamp);
  const kRegion = hmacSha256(kDate, region);
  const kService = hmacSha256(kRegion, service);
  const kSigning = hmacSha256(kService, "aws4_request");
  const signature = hmacSha256(kSigning, stringToSign).toString("hex");
  const authorization = `AWS4-HMAC-SHA256 Credential=${accessKeyId}/${credentialScope}, SignedHeaders=${signedHeaders}, Signature=${signature}`;
  return Object.assign({}, canonicalHeaders, { Authorization: authorization });
}

function vodRegionFromDc(dcId) {
  const dc = String(dcId || "").toLowerCase();
  if (/useast|us-east|maliva/.test(dc)) return "us-east-1";
  if (/eu-?west|gcp/.test(dc)) return "eu-west-1";
  return "ap-singapore-1";
}

function applyUploadQuery(fileSize) {
  return `Action=ApplyUploadInner&Version=2020-11-19&SpaceName=tiktok&FileType=video&IsInner=1&FileSize=${fileSize}&s=g158iqx8434`;
}

function describeTikTokApiFailure(action, res) {
  const status = res && res.status != null ? res.status : "?";
  const json = res && res.json;
  const err = json && (json.ResponseMetadata && json.ResponseMetadata.Error)
    ? `${json.ResponseMetadata.Error.Code || "Error"}: ${json.ResponseMetadata.Error.Message || ""}`
    : (json && (json.message || json.status_msg || json.error)) || "";
  const snippet = redactDiagnostic(String((res && res.text) || "").slice(0, 240)).replace(/\s+/g, " ").trim();
  return `${action} HTTP ${status}${err ? " · " + err : ""}${snippet ? " · " + snippet : ""}`;
}

function parseApplyUploadResult(res) {
  if (!res || !res.ok || !res.json || !res.json.Result) return null;
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

function normalizeIp(value) {
  const ip = String(value || "").trim().toLowerCase();
  return /^[0-9a-f:.]+$/i.test(ip) && (ip.includes(".") || ip.includes(":")) ? ip : "";
}

function parseIpPayload(raw, kind) {
  const text = String(raw || "").trim();
  try {
    if (kind === "json-ip") return normalizeIp(JSON.parse(text).ip);
    if (kind === "cf-trace") {
      const m = text.match(/(?:^|\n)ip=([^\s\r\n]+)/i);
      return m ? normalizeIp(m[1]) : "";
    }
  } catch (_) {}
  return normalizeIp(text.split(/\s+/)[0]);
}

const IP_SERVICES = [
  ["https://api.ipify.org?format=json", "json-ip"],
  ["https://api64.ipify.org?format=json", "json-ip"],
  ["https://icanhazip.com", "text"],
  ["https://checkip.amazonaws.com", "text"],
  ["https://ipinfo.io/ip", "text"],
  ["https://api.ip.sb/ip", "text"],
  ["https://www.cloudflare.com/cdn-cgi/trace", "cf-trace"]
];

async function fetchComputerIp() {
  let last = "";
  for (const [url, kind] of IP_SERVICES) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
      const response = await fetch(url, { signal: controller.signal, cache: "no-store" });
      if (!response.ok) continue;
      const ip = parseIpPayload(await response.text(), kind);
      if (ip) return ip;
    } catch (e) { last = e.message; } finally { clearTimeout(timer); }
  }
  throw new Error("Не удалось проверить прямой IP компьютера. " + last);
}

async function fetchProfileIp(page, request) {
  send("ip", "Проверяю внешний IP профиля…", { percent: 10 });
  let last = "";

  if (request) {
    for (const [url, kind] of IP_SERVICES) {
      try {
        const response = await request.get(url, { timeout: 25000 });
        if (!response.ok()) continue;
        const ip = parseIpPayload(await response.text(), kind);
        if (ip) {
          send("ip", `IP профиля (API): ${ip}`, { ip, percent: 11 });
          return ip;
        }
      } catch (e) { last = e.message; }
    }
  }

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
      const ip = parseIpPayload(text, kind);
      if (ip) {
        send("ip", `IP профиля (fetch): ${ip}`, { ip, percent: 11 });
        return ip;
      }
    } catch (e) { last = e.message; }
  }

  for (const [url, kind] of IP_SERVICES) {
    try {
      await page.goto(url, { waitUntil: "domcontentloaded", timeout: 35000 });
      const body = (await page.locator("body").innerText({ timeout: 8000 })).trim();
      const ip = parseIpPayload(body, kind);
      if (ip) {
        send("ip", `IP профиля (страница): ${ip}`, { ip, percent: 11 });
        return ip;
      }
    } catch (e) { last = e.message; }
  }

  throw new Error("Не удалось узнать внешний IP профиля. " + (last || "сервисы недоступны через прокси Dolphin."));
}

function ipv4Prefix(ip) {
  const parts = normalizeIp(ip).split(".");
  return parts.length === 4 ? parts.slice(0, 2).join(".") : "";
}

async function logProfileIp(page, request, expectedIp) {
  let profileIp = "";
  try {
    profileIp = await fetchProfileIp(page, request);
    send("ip", `IP профиля: ${profileIp}`, { ip: profileIp, percent: 12 });
  } catch (e) {
    send("ip", "IP профиля не определён — продолжаю без проверки (Dolphin контролирует прокси).", { percent: 12 });
    return "";
  }

  const expected = normalizeIp(expectedIp);
  const actual = normalizeIp(profileIp);
  if (expected && actual && expected !== actual && !(ipv4Prefix(expected) && ipv4Prefix(expected) === ipv4Prefix(actual))) {
    send("ip", `IP (${actual}) отличается от сохранённого (${expectedIp}) — без блокировки.`, { ip: profileIp, percent: 13 });
  }
  return profileIp;
}

async function extractSession(context) {
  const cookies = await context.cookies(["https://www.tiktok.com", "https://tiktok.com"]);
  const session = cookies.find(c => c.name === "sessionid");
  const dc = cookies.find(c => c.name === "tt-target-idc");
  const msToken = cookies.find(c => c.name === "msToken");
  if (!session || !session.value) {
    throw new Error("TikTok-сессия не найдена. Войдите в TikTok в этом профиле Dolphin.");
  }
  send("session", "Сессия TikTok подтверждена.", { percent: 18 });
  return {
    sessionId: session.value,
    dcId: (dc && dc.value) || "useast2a",
    msToken: (msToken && msToken.value) || "",
    cookieHeader: cookies.filter(c => /\.tiktok\.com$/i.test(c.domain)).map(c => `${c.name}=${c.value}`).join("; ")
  };
}

async function tiktokRequest(request, method, url, { headers = {}, body = null, aws = null } = {}) {
  const cookieHeader = headers.Cookie;
  const passthrough = Object.assign({ Accept: "application/json, text/plain, */*" }, headers);
  delete passthrough.Cookie;
  let finalHeaders;
  if (aws) {
    finalHeaders = awsSigV4Headers(Object.assign({ method, url, body: body || "" }, aws));
    finalHeaders.Accept = passthrough.Accept || "application/json, text/plain, */*";
    if (cookieHeader) finalHeaders.Cookie = cookieHeader;
  } else {
    finalHeaders = Object.assign({}, passthrough);
    if (cookieHeader) finalHeaders.Cookie = cookieHeader;
  }
  const response = await request.fetch(url, {
    method,
    headers: finalHeaders,
    data: body == null ? undefined : body
  });
  const text = await response.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { json = null; }
  return { ok: response.ok(), status: response.status(), text, json };
}

async function requestApplyUploadInner(request, session, fileSize, uploadToken) {
  const awsBase = {
    accessKeyId: uploadToken.access_key_id,
    secretAccessKey: uploadToken.secret_acess_key,
    sessionToken: uploadToken.session_token,
    service: "vod"
  };
  const query = applyUploadQuery(fileSize);
  const region = vodRegionFromDc(session.dcId);
  const attempts = [
    { url: `https://www.tiktok.com/top/v1?${query}`, region: "ap-singapore-1", cookie: session.cookieHeader },
    { url: `https://vod-${region}.bytevcloudapi.com/?${query}`, region, cookie: null },
    { url: "https://vod-ap-singapore-1.bytevcloudapi.com/?" + query, region: "ap-singapore-1", cookie: null }
  ];
  let lastError = "";
  for (const attempt of attempts) {
    const headers = attempt.cookie ? { Cookie: attempt.cookie } : {};
    const applyRes = await tiktokRequest(request, "GET", attempt.url, {
      headers,
      aws: Object.assign({}, awsBase, { region: attempt.region })
    });
    const parsed = parseApplyUploadResult(applyRes);
    if (parsed) {
      record("apply_upload", "ApplyUploadInner OK", { host: new URL(attempt.url).host, region: attempt.region });
      return parsed;
    }
    lastError = describeTikTokApiFailure("ApplyUploadInner", applyRes);
    send("upload", `ApplyUploadInner (${new URL(attempt.url).host}): повтор…`, { percent: 26 });
  }
  throw new Error(lastError || "ApplyUploadInner: пустой Result.");
}

async function preflightUploadAuth(request, session) {
  setItemState("preflight");
  const url = "https://www.tiktok.com/api/v1/video/upload/auth/?aid=1988";
  const res = await tiktokRequest(request, "GET", url, {
    headers: { Cookie: session.cookieHeader }
  });
  if (!res.ok || !res.json || !res.json.video_token_v5) {
    throw new Error(`HTTP preflight не пройден: upload/auth вернул HTTP ${res.status}.`);
  }
  const token = res.json.video_token_v5;
  if (!token.access_key_id || !token.secret_acess_key || !token.session_token) {
    throw new Error("HTTP preflight не пройден: upload/auth без обязательных полей.");
  }
  setItemState("authorized");
  send("preflight", "HTTP preflight пройден.", { percent: 22 });
  return token;
}

async function createProject(request, session) {
  const creationId = generateCreationId(21);
  const url = `https://www.tiktok.com/api/v1/web/project/create/?creation_id=${creationId}&type=1&aid=1988`;
  const res = await tiktokRequest(request, "POST", url, { headers: { Cookie: session.cookieHeader } });
  if (!res.ok || !res.json || !res.json.project || !res.json.project.project_id) {
    throw new Error(`Не удалось создать TikTok project (HTTP ${res.status}).`);
  }
  projectIdSafe = String(res.json.project.project_id);
  setItemState("project_created");
  send("project", `Создана загрузка · project ${projectIdSafe}.`, { percent: 28, projectId: projectIdSafe });
  return { creationId, projectId: projectIdSafe };
}

async function uploadVideoBytes(request, videoPath, uploadToken, session) {
  const stat = fs.statSync(videoPath);
  const fileSize = stat.size;
  const awsBase = {
    accessKeyId: uploadToken.access_key_id,
    secretAccessKey: uploadToken.secret_acess_key,
    sessionToken: uploadToken.session_token,
    region: vodRegionFromDc(session.dcId),
    service: "vod"
  };
  const applied = await requestApplyUploadInner(request, session, fileSize, uploadToken);
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
      const partRes = await tiktokRequest(request, "POST", partUrl, {
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
        throw new Error(`Файл мог быть принят TikTok. Автоповтор остановлен, чтобы не создать дубль (часть ${part}, HTTP ${partRes.status}).`);
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
  const finishRes = await tiktokRequest(request, "POST", finishUrl, {
    headers: { Authorization: videoAuth, "Content-Type": "text/plain;charset=UTF-8" },
    body: finishBody
  });
  if (!finishRes.ok) {
    setItemState("manual_check");
    throw new Error("Файл мог быть принят TikTok. Автоповтор остановлен, чтобы не создать дубль (finish).");
  }

  const commitUrl = "https://www.tiktok.com/top/v1?Action=CommitUploadInner&Version=2020-11-19&SpaceName=tiktok";
  const commitBody = JSON.stringify({ SessionKey: sessionKey, Functions: [{ name: "GetMeta" }] });
  const commitRes = await tiktokRequest(request, "POST", commitUrl, {
    headers: { "Content-Type": "application/json", Cookie: session.cookieHeader },
    body: commitBody,
    aws: Object.assign({}, awsBase, { region: "ap-singapore-1" })
  });
  if (!commitRes.ok) {
    setItemState("manual_check");
    throw new Error("Файл мог быть принят TikTok. Автоповтор остановлен, чтобы не создать дубль (commit).");
  }
  setItemState("committed");
  send("commit", "Commit подтверждён.", { percent: 88, videoId: videoIdSafe });
  return { videoId, sessionKey, creationId: null };
}

async function publishPost(request, page, session, { creationId, projectId, videoId, caption, publishMode, scheduledUnixSeconds }) {
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
  const baseParams = `app_name=tiktok_web&channel=tiktok_web&device_platform=web&aid=1988&msToken=${encodeURIComponent(msToken)}`;
  const postPath = `/tiktok/web/project/post/v1/?${baseParams}`;

  let postResult = await page.evaluate(async ({ postPath, payload }) => {
    const response = await fetch("https://www.tiktok.com" + postPath, {
      method: "POST",
      headers: { "content-type": "application/json" },
      credentials: "include",
      body: JSON.stringify(payload)
    });
    return { status: response.status, text: await response.text() };
  }, { postPath, payload });

  if (postResult.status === 403 || /signature|bogus|verify/i.test(postResult.text || "")) {
    send("publish", "Пробую подписанный HTTP POST через контекст браузера…", { percent: 92 });
    const signed = await page.evaluate(async ({ payload, msToken }) => {
      const params = new URLSearchParams({
        app_name: "tiktok_web", channel: "tiktok_web", device_platform: "web", aid: "1988", msToken: msToken || ""
      });
      const url = "https://www.tiktok.com/tiktok/web/project/post/v1/?" + params.toString();
      const r = await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, credentials: "include", body: JSON.stringify(payload) });
      return { status: r.status, text: await r.text() };
    }, { payload, msToken: session.msToken });
    postResult = signed;
  }

  let body = null;
  try { body = postResult.text ? JSON.parse(postResult.text) : null; } catch { body = null; }
  if (postResult.status < 200 || postResult.status >= 300 || !body || body.status_code !== 0) {
    setItemState("manual_check");
    manualCheck("Публикация не подтверждена сервером TikTok (HTTP " + postResult.status + "). Файл мог быть принят — проверьте профиль.", {
      projectId, videoId
    });
    return null;
  }
  setItemState("confirmed");
  const msg = scheduleOffset > 0
    ? "Запланировано на " + new Date(scheduledUnixSeconds * 1000).toISOString()
    : "TikTok подтвердил публикацию.";
  send("done_item", msg, { percent: 98, projectId, videoId, url: "https://www.tiktok.com/@" });
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
  send("diagnostic", NOTICE_MIT, { percent: 1 });

  const endpoint = await startOrConnectProfile();
  browser = await chromium.connectOverCDP(endpoint, { timeout: 60000 });
  const context = browser.contexts()[0];
  if (!context) throw new Error("Не удалось подключиться к окну профиля Dolphin.");
  const page = await context.newPage();
  activePage = page;
  const request = context.request;

  try {
    await page.goto("https://www.tiktok.com/", { waitUntil: "domcontentloaded", timeout: 90000 });
    await page.waitForTimeout(1500);
    const verifiedIp = await logProfileIp(page, request, job.expectedIp);
    const session = await extractSession(context);
    const uploadToken = await preflightUploadAuth(request, session);

    let lastUrl = "";
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      send("batch", `Пачка ${i + 1}/${items.length}: HTTP-загрузка…`, { packIndex: i + 1, packTotal: items.length, percent: 25 });
      const { creationId, projectId } = await createProject(request, session);
      await uploadVideoBytes(request, item.video, uploadToken, session);
      send("publish", "Публикация создана…", { percent: 90, packIndex: i + 1, packTotal: items.length });
      const pub = await publishPost(request, page, session, {
        creationId, projectId, videoId: videoIdSafe, caption: item.caption,
        publishMode: item.publishMode, scheduledUnixSeconds: item.scheduledUnixSeconds
      });
      if (pub) lastUrl = pub.url || lastUrl;
      if (i < items.length - 1) setItemState("authorized");
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
      success: true, ip: verifiedIp, url: lastUrl, percent: 100, transport: TRANSPORT
    });
    finishProcess(0);
  } catch (e) {
    if (itemState === "uploading" || itemState === "uploaded" || itemState === "committed") {
      manualCheck((e && e.message) || String(e), { projectId: projectIdSafe, uploadId: uploadIdSafe });
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
    fail(e, { keptOpen: true, itemState });
  });
}

module.exports = {
  TRANSPORT,
  ITEM_STATES,
  canSafeRetry,
  redactDiagnostic,
  crc32Buffer,
  validateItem,
  generateCreationId,
  NOTICE_MIT,
  awsSigV4Headers,
  vodRegionFromDc,
  parseApplyUploadResult,
  describeTikTokApiFailure
};
