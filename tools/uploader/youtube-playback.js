"use strict";

async function visible(locator, timeoutMs) {
  try { return await locator.first().isVisible({ timeout: timeoutMs || 800 }).catch(() => false); }
  catch (_) { return false; }
}

async function readExpectedVideoId(page) {
  return page.evaluate(() => {
    const fromUrl = new URL(location.href).searchParams.get("v") || "";
    const fromPlayer = window.ytInitialPlayerResponse?.videoDetails?.videoId || "";
    return fromUrl || fromPlayer || "";
  }).catch(() => "");
}

async function assertStillOnVideo(page, expectedId) {
  if (!expectedId) return;
  const current = await readExpectedVideoId(page);
  if (current && current !== expectedId) {
    throw new Error("YouTube переключил ролик (autoplay?): ожидался " + expectedId + ", сейчас " + current + ".");
  }
}

async function disableAutoplay(page) {
  const toggles = [
    page.locator("button.ytp-autonav-toggle-button[aria-checked='true']").first(),
    page.locator("button.ytp-autonav-toggle-button[aria-pressed='true']").first(),
    page.locator(".ytp-autonav-toggle-button[aria-checked='true']").first()
  ];
  for (const toggle of toggles) {
    if (await visible(toggle, 600)) {
      await toggle.click({ timeout: 2000 }).catch(() => {});
      await page.waitForTimeout(300);
      return;
    }
  }
  await page.evaluate(() => {
    const btn = document.querySelector(
      'button.ytp-autonav-toggle-button[aria-checked="true"], button.ytp-autonav-toggle-button[aria-pressed="true"]'
    );
    if (btn) btn.click();
  }).catch(() => {});
}

function pickLikeAtSec(duration) {
  const d = Math.max(1, Number(duration) || 0);
  const ratio = 0.80 + Math.random() * 0.15;
  return Math.min(d - 0.5, Math.max(d * ratio, d * 0.80));
}

async function dismissYouTubeOverlays(page) {
  const labels = [
    /Accept all/i, /Accept/i, /I agree/i, /Agree/i,
    /Принять все/i, /Принять/i, /Согласен/i, /Хорошо/i, /OK/i,
    /Reject all/i, /Отклонить все/i
  ];
  for (const re of labels) {
    try {
      const btn = page.getByRole("button", { name: re }).first();
      if (await btn.isVisible({ timeout: 500 }).catch(() => false)) {
        await btn.click({ timeout: 1500 }).catch(() => {});
        await page.waitForTimeout(300);
      }
    } catch (_) {}
  }
}

async function skipAdIfPossible(page) {
  const skip = page.locator([
    ".ytp-ad-skip-button",
    ".ytp-ad-skip-button-modern",
    ".ytp-skip-ad-button",
    "button.ytp-ad-skip-button-modern",
    "button[id*='skip' i]"
  ].join(", "));
  if (await visible(skip, 400)) await skip.first().click({ timeout: 1000 }).catch(() => {});
}

async function readPlaybackState(page) {
  return page.evaluate(() => {
    const v = document.querySelector("video.html5-main-video") || document.querySelector("#movie_player video") || document.querySelector("video");
    if (!v) return { missing: true };
    return {
      missing: false,
      current: Number(v.currentTime) || 0,
      duration: Number(v.duration) || 0,
      ended: !!v.ended,
      paused: !!v.paused,
      readyState: v.readyState
    };
  }).catch(() => ({ missing: true }));
}

/** Запускает воспроизведение только если ролик на паузе. Не переключает Play/Pause. */
async function resumeIfPaused(page) {
  await dismissYouTubeOverlays(page);
  await skipAdIfPossible(page);
  const state = await readPlaybackState(page);
  if (state.missing || state.ended || !state.paused) return;

  const play = page.locator([
    "button.ytp-large-play-button",
    "button.ytp-play-button[aria-label*='Play' i]",
    "button.ytp-play-button[aria-label*='Смотр' i]",
    "button.ytp-play-button[aria-label*='Воспроиз' i]"
  ].join(", "));
  if (await visible(play, 1200)) {
    await play.first().click({ timeout: 2000 }).catch(() => {});
    return;
  }
  await page.evaluate(() => {
    const v = document.querySelector("video.html5-main-video") || document.querySelector("#movie_player video") || document.querySelector("video");
    if (v && v.paused && !v.ended) {
      const p = v.play();
      if (p && typeof p.catch === "function") p.catch(() => {});
    }
  }).catch(() => {});
}

function formatClock(seconds) {
  const total = Math.max(0, Math.floor(seconds || 0));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return m + ":" + String(s).padStart(2, "0");
}

async function isVideoLiked(page) {
  return page.evaluate(() => {
    const unlikeNeedles = [/unlike/i, /больше не нрав/i, /remove.*like/i, /убрать.*нрав/i, /remove from liked/i];
    const buttons = Array.from(document.querySelectorAll("button, [role='button'], ytd-like-button-renderer, like-button-view-model button"));
    for (const btn of buttons) {
      const label = `${btn.getAttribute("aria-label") || ""} ${btn.getAttribute("title") || ""} ${btn.textContent || ""}`.toLowerCase();
      const pressed = (btn.getAttribute("aria-pressed") || "").toLowerCase();
      if (unlikeNeedles.some(r => r.test(label))) return true;
      if (pressed === "true" && /like|нрав/i.test(label) && !/dislike|не нрав/i.test(label)) return true;
    }
    const shortsLiked = document.querySelector(
      "ytd-reel-video-renderer like-button-view-model button[aria-pressed='true'], " +
      "reel-like-button button[aria-pressed='true'], " +
      "like-button-view-model button[aria-pressed='true']"
    );
    return !!shortsLiked;
  }).catch(() => false);
}

async function likeCurrentVideo(page, report) {
  report = report || (() => {});
  report("youtube", "Ставлю лайк…");
  await page.evaluate(() => {
    const actions = document.querySelector("#actions, #actions-inner, ytd-menu-renderer, like-button-view-model");
    if (actions && actions.scrollIntoView) actions.scrollIntoView({ block: "center", inline: "nearest" });
  }).catch(() => {});
  await page.waitForTimeout(800);

  if (await isVideoLiked(page)) {
    report("youtube", "Лайк уже стоит.");
    return;
  }

  const candidates = [
    page.locator("reel-like-button button, ytd-reel-video-renderer like-button-view-model button").first(),
    page.locator("like-button-view-model button").first(),
    page.locator("ytd-segmented-like-dislike-button-renderer like-button-view-model button").first(),
    page.locator("#top-level-buttons-computed like-button-view-model button").first(),
    page.locator("#segmented-like-button button").first(),
    page.locator("button[aria-label*='like this video' i]").first(),
    page.locator("button[aria-label^='Нравится' i]").first(),
    page.locator("button[aria-label*='Нравится' i]:not([aria-label*='Не нравится' i])").first(),
    page.locator("ytd-toggle-button-renderer#like-button button, #like-button button").first()
  ];

  let clicked = false;
  for (const btn of candidates) {
    if (!(await visible(btn, 1200))) continue;
    const label = ((await btn.getAttribute("aria-label").catch(() => "")) || "").toLowerCase();
    if (/dislike|не нрав/.test(label) && !/^нрав|^like/.test(label)) continue;
    await btn.scrollIntoViewIfNeeded().catch(() => {});
    await btn.click({ timeout: 5000 }).catch(async () => {
      await btn.click({ force: true }).catch(() => {});
    });
    clicked = true;
    break;
  }

  if (!clicked) {
    clicked = await page.evaluate(() => {
      const nodes = Array.from(document.querySelectorAll("button, [role='button']"));
      const like = nodes.find(btn => {
        const label = `${btn.getAttribute("aria-label") || ""} ${btn.getAttribute("title") || ""}`.toLowerCase();
        if (!label) return false;
        if (/dislike|не нрав/.test(label)) return false;
        return /(^|\s)like\b|like this|нрав/.test(label);
      });
      if (!like) return false;
      like.click();
      return true;
    }).catch(() => false);
  }

  if (!clicked) throw new Error("Не найдена кнопка «Нравится». Профиль оставлен открытым — поставьте лайк вручную.");

  await page.waitForTimeout(1500);
  if (!(await isVideoLiked(page))) {
    throw new Error("Лайк не подтверждён. Проверьте вход в аккаунт YouTube в профиле Dolphin.");
  }
  report("youtube", "Лайк поставлен.");
}

/** Досмотр до конца; лайк ставится один раз после ~80% просмотра (до autoplay). */
async function waitForVideoEnd(page, opts, report) {
  report = report || (() => {});
  if (/\/shorts\//.test(page.url())) {
    throw new Error("Shorts-плеер не поддерживается для полного просмотра длинного ролика.");
  }
  await dismissYouTubeOverlays(page);
  const expectedVideoId = await readExpectedVideoId(page);
  await page.waitForSelector("video.html5-main-video, #movie_player video, video", { timeout: 90000 }).catch(() => {});
  await disableAutoplay(page);
  for (let i = 0; i < 6; i++) {
    const st = await readPlaybackState(page);
    if (!st.missing && !st.paused && (st.current > 0.05 || st.readyState >= 2)) break;
    if (st.paused) await resumeIfPaused(page);
    await new Promise(r => setTimeout(r, 1000));
  }

  let alreadyLiked = await isVideoLiked(page);
  if (alreadyLiked) report("youtube", "Лайк уже стоит — всё равно смотрю видео до конца.");

  let duration = 0;
  let likeAtSec = 0;
  let likePlaced = alreadyLiked;
  const readyDeadline = Date.now() + 120000;
  while (Date.now() < readyDeadline) {
    await skipAdIfPossible(page);
    const state = await readPlaybackState(page);
    if (!state.missing && isFinite(state.duration) && state.duration > 1) {
      duration = state.duration;
      break;
    }
    if (state.paused) await resumeIfPaused(page);
    await new Promise(r => setTimeout(r, 1000));
  }
  if (!(duration > 1)) {
    await page.reload({ waitUntil: "domcontentloaded", timeout: 60000 }).catch(() => {});
    await dismissYouTubeOverlays(page);
    const retryUntil = Date.now() + 45000;
    while (Date.now() < retryUntil) {
      await skipAdIfPossible(page);
      const state = await readPlaybackState(page);
      if (!state.missing && isFinite(state.duration) && state.duration > 1) {
        duration = state.duration;
        break;
      }
      if (state.paused) await resumeIfPaused(page);
      await new Promise(r => setTimeout(r, 900));
    }
  }
  if (!(duration > 1)) throw new Error("Видео открылось, но воспроизведение не стартовало (нет длительности).");

  if (!likePlaced) {
    likeAtSec = pickLikeAtSec(duration);
    report("youtube", "Смотрю до конца (" + formatClock(duration) + ") · лайк после ~" + formatClock(likeAtSec) + "…");
  } else {
    report("youtube", "Смотрю до конца (" + formatClock(duration) + ")…");
  }

  const hardLimitMs = Math.min(4 * 60 * 60 * 1000, Math.max(3 * 60 * 1000, duration * 1000 + 8 * 60 * 1000));
  const startedAt = Date.now();
  let lastReport = 0;
  let stuckAt = -1;
  let stuckSince = Date.now();

  while (true) {
    if (Date.now() - startedAt > hardLimitMs) {
      throw new Error("Видео не завершилось за отведённое время. Проверьте рекламу или остановку в открытом профиле.");
    }
    await skipAdIfPossible(page);
    await assertStillOnVideo(page, expectedVideoId);
    const state = await readPlaybackState(page);
    if (state.missing) {
      await new Promise(r => setTimeout(r, 1500));
      continue;
    }
    const curRaw = Number(state.current) || 0;
    const dur = state.duration > 1 ? state.duration : duration;

    if (!likePlaced && curRaw >= likeAtSec) {
      await assertStillOnVideo(page, expectedVideoId);
      if (!(await isVideoLiked(page))) {
        report("youtube", "Ставлю лайк на ~" + Math.round((curRaw / dur) * 100) + "% просмотра…");
        await likeCurrentVideo(page, report);
      } else {
        report("youtube", "Лайк уже стоит.");
      }
      likePlaced = true;
      await disableAutoplay(page);
    }

    if (state.ended || (dur > 1 && curRaw >= dur - 0.75)) {
      break;
    }
    if (state.paused) {
      await resumeIfPaused(page);
    } else if (curRaw - stuckAt < 0.2) {
      if (Date.now() - stuckSince > 15000) {
        report("youtube", "Просмотр завис — снова запускаю Play…");
        await resumeIfPaused(page);
        stuckSince = Date.now();
      }
    } else {
      stuckAt = curRaw;
      stuckSince = Date.now();
    }

    if (Date.now() - lastReport > 8000) {
      lastReport = Date.now();
      const left = Math.max(0, dur - curRaw);
      report("youtube", "Идёт просмотр… " + formatClock(curRaw) + " / " + formatClock(dur) + " · осталось ~" + formatClock(left));
    }
    await new Promise(r => setTimeout(r, 1500));
  }

  await assertStillOnVideo(page, expectedVideoId);
  report("youtube", "Видео закончилось.");
  if (!likePlaced && !(await isVideoLiked(page))) {
    report("youtube", "Лайк не был поставлен во время просмотра — ставлю перед переходом…");
    await likeCurrentVideo(page, report);
  } else if (likePlaced || await isVideoLiked(page)) {
    report("youtube", "Лайк уже стоит.");
  }
}

module.exports = {
  waitForVideoEnd,
  likeCurrentVideo,
  isVideoLiked,
  resumeIfPaused,
  pickLikeAtSec,
  readExpectedVideoId
};
