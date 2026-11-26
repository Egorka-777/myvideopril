"use strict";

// Safety boundary for existing playback automation. Never click ads, accept arbitrary
// overlays or recover from ads by issuing more clicks. An ad requires manual review.
function youtubeUrl(value) {
  try { const u = new URL(value); return u.protocol === "https:" && ["www.youtube.com", "youtube.com", "m.youtube.com"].includes(u.hostname); }
  catch (_) { return false; }
}
async function isAdVisible(page) {
  if (!youtubeUrl(page.url())) return false;
  // Only in-player ads. Sidebar/search slots (ytd-ad-slot-renderer) stay visible
  // on ordinary watch pages and must not block readiness or playback.
  return page.evaluate(() => {
    const visible = el => !!el && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
    const activeShort = document.querySelector("ytd-reel-video-renderer[is-active], ytd-reel-video-renderer[active]");
    const players = activeShort
      ? [activeShort]
      : [...document.querySelectorAll("#movie_player, ytd-player .html5-video-player, .html5-video-player")];
    for (const player of players) {
      if (player.classList?.contains("ad-showing") || player.classList?.contains("ad-interrupting")) return true;
      if (player.querySelector?.(".ad-showing, .ad-interrupting")) return true;
      if ([...player.querySelectorAll(
        ".ytp-ad-player-overlay, .ytp-ad-text, .ytp-ad-skip-button-container, .ytp-skip-ad-button, button.ytp-ad-skip-button-modern"
      )].some(visible)) return true;
    }
    return false;
  }).catch(() => false);
}

async function assertNoAds(page) {
  if (!youtubeUrl(page.url())) throw new Error("Автоматические действия остановлены: открыта страница вне YouTube.");
  if (await isAdVisible(page)) throw new Error("Обнаружена реклама. Автоматические действия остановлены; продолжите вручную в профиле.");
}

/** Shorts warmup: treat ads like another clip — skip or swipe, never stop the job. */
async function skipAdDuringWarmup(page, report = () => {}) {
  if (!youtubeUrl(page.url())) return false;
  const action = await page.evaluate(() => {
    const visible = el => !!el && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
    const skip = document.querySelector('.ytp-ad-skip-button, .ytp-skip-ad-button, button.ytp-ad-skip-button-modern');
    if (visible(skip)) { skip.click(); return "skip"; }
    const player = document.querySelector("#movie_player, ytd-player .html5-video-player, .html5-video-player, ytd-reel-video-renderer[is-active], ytd-reel-video-renderer[active]");
    const ad = !!(player && (
      player.classList?.contains("ad-showing") || player.classList?.contains("ad-interrupting") ||
      player.querySelector?.(".ad-showing, .ad-interrupting, .ytp-ad-player-overlay")
    ));
    return ad ? "ad" : "";
  }).catch(() => "");
  if (action === "skip") {
    report("youtube", "Shorts: реклама — пропуск.");
    await page.waitForTimeout(400 + Math.floor(Math.random() * 400));
    return true;
  }
  if (action === "ad") {
    report("youtube", "Shorts: реклама — листаю дальше.");
    await page.keyboard.press("ArrowDown").catch(() => {});
    await page.waitForTimeout(600 + Math.floor(Math.random() * 500));
    return true;
  }
  return false;
}

async function installPlaybackBoundary(page, report = () => {}, options = {}) {
  const allowAds = !!(options && options.allowAds);
  let blocked = false;
  const popup = async child => { blocked = true; await child.close().catch(() => {}); report("diagnostic", "Заблокирована новая вкладка из окна воспроизведения. Действия остановлены."); };
  const route = async r => {
    const req = r.request();
    if (req.isNavigationRequest() && req.frame() === page.mainFrame() && !youtubeUrl(req.url())) { blocked = true; await r.abort(); return; }
    await r.continue();
  };
  page.on("popup", popup);
  await page.route("**/*", route);
  return {
    async check() {
      if (blocked) throw new Error("Действия остановлены после попытки открыть постороннюю страницу.");
      if (allowAds) await skipAdDuringWarmup(page, report);
      else await assertNoAds(page);
    },
    async dispose() { page.off("popup", popup); await page.unroute("**/*", route).catch(() => {}); }
  };
}
module.exports = { youtubeUrl, isAdVisible, assertNoAds, skipAdDuringWarmup, installPlaybackBoundary };
