"use strict";

const crypto = require("crypto");

const DEEP_WATCH_FRACTION = 0.2;
const HARD_WATCH_CAP_SEC = 40;

function randInt(min, max) {
  const lo = Math.min(min, max);
  const hi = Math.max(min, max);
  return lo + crypto.randomInt(0, hi - lo + 1);
}

function randFloat(min, max) {
  return min + Math.random() * Math.max(0, max - min);
}

function clampWatchBounds(minSec, maxSec) {
  const min = Math.max(1, Number(minSec) || 1);
  const max = Math.max(min, Math.min(HARD_WATCH_CAP_SEC, Number(maxSec) || HARD_WATCH_CAP_SEC));
  return { min, max };
}

/**
 * ~80% quick flicks (1–10 s), ~20% deeper watches up to max (cap 40 s).
 * When duration known, deep watches use partial or near-full percentage.
 */
function pickHumanWatchSeconds(minSec, maxSec, videoDurationSec) {
  const { min, max } = clampWatchBounds(minSec, maxSec);
  const duration = Number(videoDurationSec);
  const hasDuration = duration > 2 && Number.isFinite(duration);

  if (Math.random() >= DEEP_WATCH_FRACTION) {
    if (Math.random() < 0.62) return randInt(min, Math.min(3, max));
    return randInt(Math.min(4, max), Math.min(10, max));
  }

  if (hasDuration) {
    const fullish = Math.random() < 0.45;
    const ratio = fullish
      ? 0.85 + Math.random() * 0.15
      : 0.35 + Math.random() * 0.3;
    let watch = duration * ratio;
    watch = Math.min(watch, max, HARD_WATCH_CAP_SEC);
    watch = Math.max(watch, Math.min(min, 8));
    return Math.max(1, Math.round(watch));
  }

  const deepMin = Math.max(min, 8);
  const deepMax = Math.min(max, HARD_WATCH_CAP_SEC);
  return randInt(deepMin, Math.max(deepMin, deepMax));
}

function describeWatchPlan(seconds, videoDurationSec) {
  const d = Number(videoDurationSec);
  if (!(d > 2)) return "просмотр ~" + seconds + " сек.";
  const pct = Math.round((seconds / d) * 100);
  if (pct >= 92) return "просмотр ~" + seconds + " сек. (почти до конца)";
  if (pct >= 40 && pct <= 70) return "просмотр ~" + seconds + " сек. (~" + pct + "% ролика)";
  return "просмотр ~" + seconds + " сек.";
}

async function readActiveVideoDuration(page) {
  return page.evaluate(() => {
    const active = document.querySelector("ytd-reel-video-renderer[is-active], ytd-reel-video-renderer[active]");
    const root = active || document;
    const v = root.querySelector("video.html5-main-video, video");
    if (!v) return 0;
    const d = Number(v.duration);
    return Number.isFinite(d) && d > 0 ? d : 0;
  }).catch(() => 0);
}

async function resumePlaybackWarmup(page) {
  const safety = require("./youtube-interaction-safety.js");
  await safety.skipAdDuringWarmup(page);
  await page.evaluate(() => {
    const v = document.querySelector("ytd-reel-video-renderer[is-active] video, video.html5-main-video, video");
    if (v && v.paused && !v.ended) {
      const p = v.play();
      if (p && typeof p.catch === "function") p.catch(() => {});
    }
  }).catch(() => {});
}

async function ensureShortsFeed(page, report) {
  const safety = require("./youtube-interaction-safety.js");
  const url = page.url();
  if (!/\/shorts/.test(url)) {
    await page.goto("https://www.youtube.com/shorts", { waitUntil: "domcontentloaded", timeout: 90000 });
  }
  await safety.skipAdDuringWarmup(page, report);
  await page.waitForTimeout(1200 + crypto.randomInt(0, 800));
  const ok = await page.evaluate(() => {
    return !!(
      document.querySelector("ytd-shorts, ytd-reel-video-renderer, [is-shorts], shorts-inner-container") ||
      location.pathname.startsWith("/shorts")
    );
  }).catch(() => false);
  if (!ok) {
    await page.goto("https://www.youtube.com/shorts", { waitUntil: "domcontentloaded", timeout: 90000 });
    await page.waitForTimeout(1500);
  }
}

async function watchCurrentShort(page, seconds, report, label) {
  const safety = require("./youtube-interaction-safety.js");
  await resumePlaybackWarmup(page);
  report("youtube", label || describeWatchPlan(seconds, 0));
  const end = Date.now() + seconds * 1000;
  while (Date.now() < end) {
    await safety.skipAdDuringWarmup(page, report);
    await resumePlaybackWarmup(page);
    await page.waitForTimeout(350 + crypto.randomInt(0, 250));
  }
}

async function maybeLike(page, job, report, watchSec) {
  if (watchSec < 8) return;
  const safety = require("./youtube-interaction-safety.js");
  if (await safety.isAdVisible(page)) return;
  if (!job.warmupLikesEnabled) return;
  const pct = Number(job.warmupLikePercent);
  if (!(pct > 0)) return;
  if (Math.random() * 100 >= pct) {
    report("youtube", "Shorts: лайк пропущен (случайно).");
    return;
  }
  const playback = require("./youtube-playback.js");
  try {
    await playback.likeCurrentVideo(page, report);
  } catch (e) {
    if (/реклама/i.test(String(e.message || e))) {
      report("youtube", "Shorts: лайк пропущен (реклама).");
      return;
    }
    throw e;
  }
}

async function nextShort(page) {
  await page.keyboard.press("ArrowDown").catch(() => {});
  await page.waitForTimeout(500 + crypto.randomInt(0, 900));
}

async function runShortsWarmup(page, job, report) {
  report = report || (() => {});
  const minMin = Math.max(1, Number(job.warmupMinMinutes) || 5);
  const maxMin = Math.max(minMin, Number(job.warmupMaxMinutes) || 15);
  const minSec = Number(job.warmupWatchMinSec);
  const maxSec = Number(job.warmupWatchMaxSec);
  const totalMs = randFloat(minMin, maxMin) * 60 * 1000;
  const deadline = Date.now() + totalMs;

  const boundary = await require("./youtube-interaction-safety.js").installPlaybackBoundary(page, report, { allowAds: true });
  try {
    report("youtube", "Shorts прогрев: ~" + Math.round(totalMs / 60000) + " мин. · имитация листания (80% быстро, 20% дольше).");
    await ensureShortsFeed(page, report);

    let count = 0;
    while (Date.now() < deadline) {
      await boundary.check();
      count++;
      const duration = await readActiveVideoDuration(page);
      let watchSec = pickHumanWatchSeconds(minSec, maxSec, duration);
      const remaining = deadline - Date.now();
      if (remaining <= 0) break;
      watchSec = Math.min(watchSec, Math.max(1, Math.floor(remaining / 1000)));
      const label = "Shorts #" + count + " · " + describeWatchPlan(watchSec, duration);
      await watchCurrentShort(page, watchSec, report, label);
      await maybeLike(page, job, report, watchSec);
      if (Date.now() >= deadline) break;
      await nextShort(page);
    }
    report("youtube", "Shorts прогрев завершён: " + count + " ролик(ов).");
    return { count, minutes: Math.round(totalMs / 60000) };
  } finally {
    await boundary.dispose();
  }
}

module.exports = { runShortsWarmup, pickHumanWatchSeconds, DEEP_WATCH_FRACTION, HARD_WATCH_CAP_SEC, clampWatchBounds };
