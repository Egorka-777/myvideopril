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

function classifyVideo(player, id, channelId, today) {
  const details = player && player.videoDetails;
  const meta = player && player.microformat && player.microformat.playerMicroformatRenderer;
  if (!details || !meta || details.videoId !== id || !channelId || details.channelId !== channelId)
    return { eligible: false, reason: "metadata_or_owner" };
  if (player.playabilityStatus?.status !== "OK" || details.isLiveContent || meta.liveBroadcastDetails)
    return { eligible: false, reason: "not_public_long_video" };
  // Relative labels such as '23 hours ago' do not establish a calendar date.
  const date = String(meta.publishDate || "").slice(0, 10);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return { eligible: false, reason: "date_unknown" };
  return { eligible: date === today, reason: date === today ? "today" : "other_date", date, id };
}

async function pauseVideoPage(page) {
  await page.route("**/*", route => {
    const request = route.request();
    const host = new URL(request.url()).hostname;
    if (request.resourceType() === "media" || /(^|\.)googlevideo\.com$/.test(host)) return route.abort();
    return route.fallback();
  });
  await page.addInitScript(() => {
    const pause = el => { el.autoplay = false; el.muted = true; el.pause(); };
    HTMLMediaElement.prototype.play = function () { pause(this); return Promise.resolve(); };
    document.addEventListener("play", event => { if (event.target instanceof HTMLMediaElement) pause(event.target); }, true);
    const observer = new MutationObserver(() => document.querySelectorAll("video,audio").forEach(pause));
    observer.observe(document, { childList: true, subtree: true });
  });
}

async function openToday(page, options, report = () => {}) {
  const url = channelVideosUrl(options.channelUrl);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(options.todayDate || "")) throw new Error("Не задана календарная дата ПК.");
  if (options.pauseAfterOpen !== false) await pauseVideoPage(page);
  report("open_channel", url);
  await page.goto(url, { waitUntil: "domcontentloaded", timeout: 60000 });
  await page.waitForFunction(() => document.querySelector('ytd-rich-grid-renderer, ytd-browse') && window.ytInitialData,
    null, { timeout: 30000 });
  const channelId = await page.evaluate(() => window.ytInitialData?.metadata?.channelMetadataRenderer?.externalId || "");
  if (!channelId) throw new Error("YouTube не подтвердил ID открытого канала.");
  await page.locator('a[href*="/watch?v="]').first().waitFor({ state: "attached", timeout: 15000 }).catch(() => {});
  const ids = await page.evaluate(() => [...new Set(Array.from(document.querySelectorAll('ytd-rich-grid-renderer a[href*="/watch?v="], ytd-grid-video-renderer a[href*="/watch?v="]'))
    .map(a => new URL(a.href).searchParams.get("v")).filter(id => /^[a-zA-Z0-9_-]{11}$/.test(id || "")))].slice(0, 20));
  if (!ids.length) throw new Error("На вкладке «Видео» нет доступных роликов либо изменился интерфейс YouTube.");
  let selected = null;
  for (const id of ids) {
    const html = await page.evaluate(async id => {
      const controller = new AbortController(), timer = setTimeout(() => controller.abort(), 15000);
      try {
        const response = await fetch("/watch?v=" + id, { credentials: "include", signal: controller.signal });
        if (!response.ok) throw new Error("HTTP " + response.status);
        return await response.text();
      } finally { clearTimeout(timer); }
    }, id);
    const verdict = classifyVideo(extractAssignedJson(html, "ytInitialPlayerResponse"), id, channelId, options.todayDate);
    report("video_date", id + ": " + verdict.reason + (verdict.date ? " " + verdict.date : ""));
    if (verdict.eligible) { selected = id; break; } // Videos tab is newest first.
    if (verdict.reason === "date_unknown" || verdict.reason === "metadata_or_owner")
      throw new Error("Не удалось подтвердить дату/принадлежность ролика " + id + ". Старое видео не открывается.");
  }
  if (!selected) throw new Error("Нет подтверждённого видео за " + options.todayDate + " среди последних 20 роликов. Старое видео не открывается.");
  const watchUrl = "https://www.youtube.com/watch?v=" + selected;
  await page.goto(watchUrl, { waitUntil: "domcontentloaded", timeout: 60000 });
  await page.waitForFunction(id => window.ytInitialPlayerResponse?.videoDetails?.videoId === id,
    selected, { timeout: 30000 });
  report("opened", watchUrl + (options.pauseAfterOpen !== false ? " · пауза" : ""));
  return watchUrl;
}

module.exports = { channelVideosUrl, extractAssignedJson, classifyVideo, pauseVideoPage, openToday };
