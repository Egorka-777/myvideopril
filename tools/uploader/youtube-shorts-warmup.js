"use strict";

const crypto = require("crypto");

function randInt(min, max) {
  const lo = Math.min(min, max);
  const hi = Math.max(min, max);
  return lo + crypto.randomInt(0, hi - lo + 1);
}

function randFloat(min, max) {
  return min + Math.random() * Math.max(0, max - min);
}

async function dismissOverlays(page) {
  await require("./youtube-interaction-safety.js").assertNoAds(page);
}

async function ensureShortsFeed(page) {
  const url = page.url();
  if (!/\/shorts/.test(url)) {
    await page.goto("https://www.youtube.com/shorts", { waitUntil: "domcontentloaded", timeout: 90000 });
  }
  await dismissOverlays(page);
  await page.waitForTimeout(1500);
  const ok = await page.evaluate(() => {
    return !!(
      document.querySelector("ytd-shorts, ytd-reel-video-renderer, [is-shorts], shorts-inner-container") ||
      location.pathname.startsWith("/shorts")
    );
  }).catch(() => false);
  if (!ok) {
    await page.goto("https://www.youtube.com/shorts", { waitUntil: "domcontentloaded", timeout: 90000 });
    await page.waitForTimeout(2000);
  }
}

async function watchCurrentShort(page, seconds, report) {
  const playback = require("./youtube-playback.js");
  await dismissOverlays(page);
  await playback.resumeIfPaused(page);
  const end = Date.now() + seconds * 1000;
  while (Date.now() < end) {
    await page.waitForTimeout(500);
    await playback.resumeIfPaused(page);
  }
  report("youtube", "Shorts: просмотр ~" + seconds + " сек.");
}

async function maybeLike(page, job, report) {
  if (!job.warmupLikesEnabled) return;
  const pct = Number(job.warmupLikePercent);
  if (!(pct > 0)) return;
  if (Math.random() * 100 >= pct) {
    report("youtube", "Shorts: лайк пропущен (случайно).");
    return;
  }
  const playback = require("./youtube-playback.js");
  await playback.likeCurrentVideo(page, report);
}

async function nextShort(page) {
  await page.keyboard.press("ArrowDown").catch(() => {});
  await page.waitForTimeout(800 + crypto.randomInt(0, 700));
}

async function runShortsWarmup(page, job, report) {
  report = report || (() => {});
  const minMin = Math.max(1, Number(job.warmupMinMinutes) || 5);
  const maxMin = Math.max(minMin, Number(job.warmupMaxMinutes) || 15);
  const minSec = Math.max(3, Number(job.warmupWatchMinSec) || 15);
  const maxSec = Math.max(minSec, Number(job.warmupWatchMaxSec) || 45);
  const totalMs = randFloat(minMin, maxMin) * 60 * 1000;
  const deadline = Date.now() + totalMs;

  const boundary = await require("./youtube-interaction-safety.js").installPlaybackBoundary(page, report);
  try {
  report("youtube", "Shorts прогрев: ~" + Math.round(totalMs / 60000) + " мин.");
  await ensureShortsFeed(page);

  let count = 0;
  while (Date.now() < deadline) {
    await boundary.check();
    count++;
    const watchSec = randInt(minSec, maxSec);
    const remaining = deadline - Date.now();
    if (remaining <= 0) break;
    const slice = Math.min(watchSec, Math.max(3, Math.floor(remaining / 1000)));
    report("youtube", "Shorts #" + count + " · " + slice + " сек.");
    await watchCurrentShort(page, slice, report);
    await maybeLike(page, job, report);
    if (Date.now() >= deadline) break;
    await nextShort(page);
  }
  report("youtube", "Shorts прогрев завершён: " + count + " ролик(ов).");
  return { count, minutes: Math.round(totalMs / 60000) };
  } finally { await boundary.dispose(); }
}

module.exports = { runShortsWarmup };
