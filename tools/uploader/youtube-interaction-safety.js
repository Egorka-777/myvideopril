"use strict";

// Safety boundary for existing playback automation. Never click ads, accept arbitrary
// overlays or recover from ads by issuing more clicks. An ad requires manual review.
function youtubeUrl(value) {
  try { const u = new URL(value); return u.protocol === "https:" && ["www.youtube.com", "youtube.com", "m.youtube.com"].includes(u.hostname); }
  catch (_) { return false; }
}
async function assertNoAds(page) {
  if (!youtubeUrl(page.url())) throw new Error("Автоматические действия остановлены: открыта страница вне YouTube.");
  const ad = await page.evaluate(() => {
    const active = document.querySelector('ytd-reel-video-renderer[is-active], ytd-reel-video-renderer[active]');
    const scope = active || document;
    const visible = el => !!el && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
    return [...scope.querySelectorAll('.ad-showing, .ad-interrupting, .ytp-ad-player-overlay, .ytp-ad-text, .ytp-ad-skip-button-container, ytd-ad-slot-renderer, ytd-action-companion-ad-renderer, ad-button-view-model')].some(visible);
  }); // A DOM/read failure stops the action instead of assuming no advertisement.
  if (ad) throw new Error("Обнаружена реклама. Автоматические действия остановлены; продолжите вручную в профиле.");
}
async function installPlaybackBoundary(page, report = () => {}) {
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
    async check() { if (blocked) throw new Error("Действия остановлены после попытки открыть постороннюю страницу."); await assertNoAds(page); },
    async dispose() { page.off("popup", popup); await page.unroute("**/*", route).catch(() => {}); }
  };
}
module.exports = { youtubeUrl, assertNoAds, installPlaybackBoundary };
