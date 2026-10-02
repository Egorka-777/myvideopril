"use strict";

// Открытие последнего ролика выбранной даты.
function channelVideosUrl(value) {
  const u = new URL(String(value || "").trim());
  if (u.protocol !== "https:" || !["youtube.com", "www.youtube.com"].includes(u.hostname))
    throw new Error("Укажите https://www.youtube.com/@канал/videos.");
  const parts = u.pathname.split("/").filter(Boolean);
  const base = parts[0] && parts[0].startsWith("@") ? parts.slice(0, 1) :
    ["channel", "c", "user"].includes(parts[0]) && parts[1] ? parts.slice(0, 2) : null;
  if (!base || parts.length > base.length + 1 ||
      (parts.length > base.length && !["videos", "featured", "shorts"].includes(parts.at(-1))))
    throw new Error("Нужна ссылка канала YouTube, а не ссылка ролика.");
  return "https://www.youtube.com/" + base.join("/") + "/videos";
}

function extractAssignedJson(html, name) {
  const match = new RegExp("(?:var\\s+)?" + name + "\\s*=\\s*").exec(html);
  if (!match) return null;
  const start = html.indexOf("{", match.index + match[0].length);
  let depth = 0, quoted = false, escaped = false;
  for (let i = start; i >= 0 && i < html.length; i++) {
    const c = html[i];
    if (quoted) { if (escaped) escaped = false; else if (c === "\\") escaped = true; else if (c === '"') quoted = false; }
    else if (c === '"') quoted = true;
    else if (c === "{") depth++;
    else if (c === "}" && --depth === 0) {
      try { return JSON.parse(html.slice(start, i + 1)); } catch { return null; }
    }
  }
  return null;
}

function classifyVideo(player, id, channelId, today, pcUtcOffsetMinutes) {
  const details = player && player.videoDetails;
  const meta = player && player.microformat && player.microformat.playerMicroformatRenderer;
  if (!details || !meta || details.videoId !== id || !channelId || details.channelId !== channelId)
    return { eligible: false, reason: "metadata_or_owner" };
  if (player.playabilityStatus?.status !== "OK" || details.isLiveContent || meta.liveBroadcastDetails)
    return { eligible: false, reason: "not_public_long_video" };
  const published = String(meta.publishDate || "");
  let date = published.slice(0, 10);
  if (/T.*(?:Z|[+-]\d{2}:\d{2})$/.test(published) && Number.isFinite(pcUtcOffsetMinutes)) {
    const timestamp = Date.parse(published);
    if (!Number.isFinite(timestamp)) return { eligible: false, reason: "date_unknown" };
    date = new Date(timestamp + pcUtcOffsetMinutes * 60000).toISOString().slice(0, 10);
  }
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return { eligible: false, reason: "date_unknown" };
  return { eligible: date === today, reason: date === today ? "today" : "other_date", date, id };
}

function ordinaryVideoIds(data) {
  const tabs = data?.contents?.twoColumnBrowseResultsRenderer?.tabs || [];
  const tab = tabs.map(t => t.tabRenderer).find(t => t?.selected);
  const ids = [];
  function visit(node) {
    if (!node || typeof node !== "object") return;
    if (node.richShelfRenderer || node.reelShelfRenderer || node.shortsLockupViewModel) return;
    const id = node.videoRenderer?.videoId ||
      (node.lockupViewModel?.contentType === "LOCKUP_CONTENT_TYPE_VIDEO" ? node.lockupViewModel.contentId : "");
    if (/^[a-zA-Z0-9_-]{11}$/.test(id || "")) { ids.push(id); return; }
    for (const value of Object.values(node)) visit(value);
  }
  visit(tab?.content);
  return [...new Set(ids)].slice(0, 20);
}

/** Навигация: вкладка «Видео» → сегодняшний ролик. */
async function navigateTodayVideo(page, options, report = () => {}) {
  const url = channelVideosUrl(options.channelUrl);
  const deadline = Date.now() + 90000;
  const timeout = max => {
    const remaining = deadline - Date.now();
    if (remaining <= 0) throw new Error("Не удалось открыть канал и подтвердить видео за 90 секунд.");
    return Math.min(max, remaining);
  };
  if (!/^\d{4}-\d{2}-\d{2}$/.test(options.todayDate || "")) throw new Error("Не задана календарная дата ПК.");
  report("open_channel", url);
  await page.goto(url, { waitUntil: "domcontentloaded", timeout: timeout(60000) });
  await page.waitForFunction(() => document.querySelector("ytd-rich-grid-renderer, ytd-browse") && window.ytInitialData,
    null, { timeout: timeout(30000) });
  const channelId = await page.evaluate(() => window.ytInitialData?.metadata?.channelMetadataRenderer?.externalId || "");
  if (!channelId) throw new Error("YouTube не подтвердил ID открытого канала.");
  await page.locator('a[href*="/watch?v="]').first().waitFor({ state: "attached", timeout: timeout(15000) }).catch(() => {});
  const initialData = await page.evaluate(() => window.ytInitialData);
  let ids = ordinaryVideoIds(initialData);
  if (!ids.length) ids = await page.evaluate(() => [...new Set(Array.from(document.querySelectorAll("ytd-rich-grid-media a[href*='/watch?v='], ytd-grid-video-renderer a[href*='/watch?v=']"))
    .map(a => new URL(a.href).searchParams.get("v")).filter(id => /^[a-zA-Z0-9_-]{11}$/.test(id || "")))].slice(0, 20));
  if (!ids.length) throw new Error("На вкладке «Видео» нет доступных роликов либо изменился интерфейс YouTube.");
  let selected = null;
  for (const id of ids) {
    const html = await page.evaluate(async ({ id, fetchTimeout }) => {
      const controller = new AbortController(), timer = setTimeout(() => controller.abort(), fetchTimeout);
      try {
        const response = await fetch("/watch?v=" + id, { credentials: "include", signal: controller.signal });
        if (!response.ok) throw new Error("HTTP " + response.status);
        return await response.text();
      } finally { clearTimeout(timer); }
    }, { id, fetchTimeout: timeout(15000) });
    const verdict = classifyVideo(extractAssignedJson(html, "ytInitialPlayerResponse"), id, channelId, options.todayDate, options.pcUtcOffsetMinutes);
    report("video_date", id + ": " + verdict.reason + (verdict.date ? " " + verdict.date : ""));
    if (verdict.eligible) { selected = id; break; }
    if (verdict.reason === "date_unknown" || verdict.reason === "metadata_or_owner")
      throw new Error("Не удалось подтвердить дату/принадлежность ролика " + id + ". Старое видео не открывается.");
  }
  if (!selected) throw new Error("Нет подтверждённого видео за " + options.todayDate + " среди последних 20 роликов. Старое видео не открывается.");
  const watchUrl = "https://www.youtube.com/watch?v=" + selected;
  await page.goto(watchUrl, { waitUntil: "domcontentloaded", timeout: timeout(60000) });
  await page.waitForFunction(id => window.ytInitialPlayerResponse?.videoDetails?.videoId === id,
    selected, { timeout: timeout(30000) });
  if (new URL(page.url()).pathname !== "/watch" ||
      await page.evaluate(() => !!document.querySelector("ytd-reel-video-renderer")))
    throw new Error("YouTube открыл Shorts вместо обычного видео.");
  report("opened", watchUrl);
  return watchUrl;
}

async function openToday(page, options, report = () => {}) {
  return navigateTodayVideo(page, options, report);
}

async function captureNavigationFailure(page, target, error, directory, report) {
  if (!directory || page.isClosed()) return;
  const fs = require("fs"), path = require("path");
  fs.mkdirSync(directory, { recursive: true });
  const owner = String(target.ownerProfileId || "channel").replace(/[^a-zA-Z0-9_-]/g, "_").slice(0, 40);
  const file = path.join(directory, "youtube-navigation-" + Date.now() + "-" + process.pid + "-" + owner);
  const state = await page.evaluate(() => ({
    channelId: window.ytInitialData?.metadata?.channelMetadataRenderer?.externalId || "",
    playerId: window.ytInitialPlayerResponse?.videoDetails?.videoId || "",
    richGrids: document.querySelectorAll("ytd-rich-grid-renderer").length,
    ordinaryCards: document.querySelectorAll("ytd-rich-grid-media, ytd-grid-video-renderer").length,
    shortsPlayer: !!document.querySelector("ytd-reel-video-renderer"),
    videos: [...document.querySelectorAll("video")].map(v => ({ paused: v.paused, autoplay: v.autoplay })),
    candidateIds: [...new Set([...document.querySelectorAll("ytd-rich-grid-media a[href*='/watch?v='],ytd-grid-video-renderer a[href*='/watch?v=']")].map(a => new URL(a.href).searchParams.get("v")))].slice(0, 20)
  })).catch(() => ({ unavailable: true }));
  const url = new URL(page.url());
  fs.writeFileSync(file + ".json", JSON.stringify({ at: new Date().toISOString(), error: String(error.message),
    page: url.origin + url.pathname, videoId: url.searchParams.get("v") || "", state }, null, 2));
  await page.screenshot({ path: file + ".png", fullPage: false, timeout: 5000 }).catch(() => {});
  report("diagnostic", "Диагностика страницы: " + file + ".json / .png");
}

async function openChannelsWatch(page, targets, options, report = () => {}, playback) {
  if (!Array.isArray(targets) || !targets.length) throw new Error("Нет ссылок каналов.");
  if (!playback || typeof playback.waitForVideoEnd !== "function") throw new Error("Нет модуля просмотра видео.");
  let opened = 0, lastUrl = "";
  const errors = [];
  for (let i = 0; i < targets.length; i++) {
    const target = targets[i];
    const label = String(target.ownerName || target.channelUrl || "Канал");
    report("channel", "Канал " + (i + 1) + "/" + targets.length + " · " + label);
    try {
      const channelUrl = channelVideosUrl(target.channelUrl);
      lastUrl = await navigateTodayVideo(page, { ...options, channelUrl }, (stage, text) => report(stage, label + ": " + text));
      report("youtube", label + ": видео открыто. Смотрю…");
      await playback.waitForVideoEnd(page, {}, (stage, text) => report(stage || "youtube", label + ": " + text));
      opened++;
      report("mesh", "✓ " + label + " · досмотрено", { channelUrl, meshOwner: target.ownerProfileId, url: lastUrl });
    } catch (e) {
      const reason = label + ": " + e.message;
      errors.push(reason); report("channel_error", reason);
      try { await captureNavigationFailure(page, target, e, options.diagnosticsDirectory, report); }
      catch (diagnosticError) { report("diagnostic", "Не сохранена диагностика: " + diagnosticError.message); }
      if (page.isClosed()) break;
    }
  }
  report("summary", "Досмотрено: " + opened + "/" + targets.length + " · ошибок: " + errors.length);
  if (errors.length) throw new Error("Досмотрено " + opened + "/" + targets.length + ". " + errors.join("; "));
  return { opened, lastUrl };
}

module.exports = {
  channelVideosUrl, extractAssignedJson, classifyVideo, ordinaryVideoIds,
  navigateTodayVideo, openToday, openChannelsWatch
};
