"use strict";

const safety = require("./youtube-interaction-safety.js");
const TOTAL_MS = 10 * 60 * 1000;
const WAIT_MS = 60000;

class WarmupDeadline extends Error {
  constructor() { super("Время прогрева истекло."); }
}

function shuffle(items, random = Math.random) {
  const out = items.slice();
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(random() * (i + 1));
    [out[i], out[j]] = [out[j], out[i]];
  }
  return out;
}

function parseKeywords(value) {
  return [...new Set(String(value || "").split(/\r?\n/).map(s => s.trim()).filter(Boolean))];
}

function journal(report, stage, waitMs, reason) {
  report("warmup_journal", JSON.stringify({ stage, waitMs, reason }), { waitStage: stage, waitMs, reason });
}

function buildResult(keywords, covered, count, reason) {
  const total = keywords.length;
  const coveredCount = covered.size;
  const missing = keywords.filter(key => !covered.has(key));
  const partial = missing.length > 0 || reason === "time_limit";
  return {
    count,
    minutes: 10,
    coveredKeywords: coveredCount,
    totalKeywords: total,
    missing,
    partial,
    reason: reason || (missing.length ? "keywords_remaining" : "completed")
  };
}

function planWatch(duration, remainingSec, shorts, random = Math.random) {
  if (!(duration > 0) || !(remainingSec > 0)) return null;
  const initial = Math.min(5, duration);
  // Do not start a clip if its mandatory initial segment cannot fit.
  if (remainingSec < initial) return null;
  const mode = ["short", "long", "full"][Math.floor(random() * 3)];
  const fraction = mode === "full" ? 1 : mode === "long" ? 0.4 + random() * 0.4 : 0.05 + random() * 0.15;
  const seconds = Math.min(remainingSec, duration, Math.max(initial, duration * fraction));
  const seekTo = !shorts && mode !== "full" && duration > initial + seconds && random() < 0.5
    ? initial + random() * (duration - seconds - initial) : null;
  return { mode, initial, seconds, seekTo };
}

function groupBudget(remainingSec, pendingKeys, searchSec, navigationSec, random = Math.random) {
  // Reserve search, video loading, five seconds of playback and polling
  // granularity for every unseen key.
  const reserve = pendingKeys * (searchSec + navigationSec + 6);
  const available = Math.max(0, remainingSec - reserve);
  const share = remainingSec / (pendingKeys + 1) * (0.6 + random() * 0.8);
  return Math.min(available, Math.max(navigationSec + 5, share));
}

// Bound all shared-helper actions too. Unlike Promise.race alone, Playwright
// navigation/locator calls also receive the remaining native timeout.
function deadlinePage(page, deadline, now = Date.now) {
  const cache = new WeakMap();
  const timed = new Set(["goto", "goBack", "click", "fill", "press", "waitFor", "waitForFunction", "waitForURL", "getAttribute"]);
  const wrap = target => {
    if (cache.has(target)) return cache.get(target);
    const proxy = new Proxy(target, { get(obj, key) {
      const value = obj[key];
      if (typeof value !== "function") return value && typeof value === "object" ? wrap(value) : value;
      return (...input) => {
        // Removing the protection is cleanup, not a new browser interaction.
        if (key === "off" || key === "unroute") return value.apply(obj, input);
        // Removing the protection is cleanup, not a new browser interaction.
        if (key === "off" || key === "unroute") return value.apply(obj, input);
        const remaining = deadline - now();
        if (remaining <= 0) throw new WarmupDeadline();
        const args = input.slice();
        if (key === "waitForTimeout") args[0] = Math.min(args[0], remaining);
        if (timed.has(key)) {
          const index = key === "waitFor" || key === "click" || key === "goBack" ? 0 : key === "waitForFunction" ? 2 : 1;
          const opts = args[index] || {};
          args[index] = { ...opts, timeout: Math.max(1, Math.min(opts.timeout || remaining, remaining)) };
        }
        const result = value.apply(obj, args);
        if (result && typeof result.then === "function") {
          let timer;
          return Promise.race([result, new Promise((_, reject) => {
            timer = setTimeout(() => reject(new WarmupDeadline()), Math.max(1, deadline - now()));
          })]).catch(error => {
            if (now() >= deadline && (error instanceof WarmupDeadline || error.name === "TimeoutError")) throw new WarmupDeadline();
            throw error;
          }).finally(() => clearTimeout(timer));
        }
        return result && typeof result === "object" ? wrap(result) : result;
      };
    } });
    cache.set(target, proxy);
    return proxy;
  };
  return wrap(page);
}

async function waitForSearchResults(page, shorts) {
  await page.waitForFunction(onlyShorts => {
    const search = document.querySelector("ytd-search");
    if (!search || search.hasAttribute("is-loading")) return false;
    const links = onlyShorts ? "a[href*='/shorts/']" : "a[href*='/watch?v='], a[href*='/shorts/']";
    if ([...search.querySelectorAll(links)].some(a => a.getClientRects().length)) return true;
    return [...search.querySelectorAll("ytd-message-renderer, ytd-background-promo-renderer")]
      .some(el => el.getClientRects().length && el.innerText.trim());
  }, shorts, { timeout: WAIT_MS });
}

async function searchKeyword(page, keyword, shorts, report = () => {}) {
  // Existing worker search helpers build URLs and dismiss generic overlays;
  // they are deliberately not used for this native-search-only workflow.
  // Reset the previous keyword's native filter, staying in this same tab.
  await page.goto("https://www.youtube.com/", { waitUntil: "domcontentloaded", timeout: 30000 });
  const input = page.locator("input[name='search_query'], input#search, input.ytSearchboxComponentInput").first();
  const searchWaitStarted = Date.now();
  await input.waitFor({ state: "visible", timeout: WAIT_MS });
  journal(report, "search_input", Date.now() - searchWaitStarted, "поле поиска готово");
  await input.fill(keyword);
  await input.press("Enter");
  const resultsWaitStarted = Date.now();
  await page.waitForFunction(query => {
    const u = new URL(location.href);
    return u.pathname === "/results" && u.searchParams.get("search_query") === query && !document.querySelector("ytd-search[is-loading]");
  }, keyword, { timeout: WAIT_MS });
  journal(report, "search_results_url", Date.now() - resultsWaitStarted, "страница результатов");
  // The URL changes before ytd-search and its links have been populated.
  await waitForSearchResults(page, false);
  if (shorts) {
    await page.getByRole("button", { name: /^(Search filters|Filters|Фильтры поиска|Фильтры)$/i }).first().click({ timeout: 10000 });
    const option = page.locator("ytd-search-filter-renderer").filter({ hasText: /^\s*Shorts\s*$/i }).first().locator("a#endpoint");
    const filterUrl = new URL(await option.getAttribute("href"), page.url()).href;
    // Click the native endpoint, not the renderer's non-clickable padding.
    await option.click({ timeout: 10000 });
    // Search is a SPA transition: URL/DOM readiness, not a document load.
    await page.waitForFunction(expected => location.href === expected, filterUrl, { timeout: 10000 });
    await waitForSearchResults(page, true);
  }
  return page.evaluate(onlyShorts => {
    const result = [], seen = new Set();
    const selector = onlyShorts
      ? "ytd-search a[href*='/shorts/']"
      : "ytd-search ytd-video-renderer a[href*='/watch?v='], ytd-search yt-lockup-view-model a[href*='/watch?v=']";
    for (const a of document.querySelectorAll(selector)) {
      if (!a.getClientRects().length) continue;
      if (a.closest("ytd-ad-slot-renderer, ytd-promoted-sparkles-web-renderer")) continue;
      const u = new URL(a.href, location.href);
      if (u.origin !== location.origin) continue;
      const id = u.pathname.startsWith("/shorts/") ? u.pathname.split("/")[2] : u.searchParams.get("v");
      if (!/^[\w-]{11}$/.test(id || "") || seen.has(id)) continue;
      seen.add(id);
      result.push({ id, url: u.href });
    }
    return result;
  }, shorts);
}

// Passed to evaluate so reads, seeks and pauses always target the same
// visible player, not an unloaded prefetch video retained by YouTube.
function currentVideo() {
  return [...document.querySelectorAll("video")].find(video => {
    const rect = video.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.top < innerHeight &&
      rect.right > 0 && rect.left < innerWidth && getComputedStyle(video).visibility !== "hidden";
  });
}

async function withVideo(page, action, argument) {
  const handle = await page.evaluateHandle(currentVideo);
  try { return await handle.evaluate(action, argument); }
  finally { await handle.dispose(); }
}

async function readState(page) {
  return withVideo(page, v => v ? { duration: Number(v.duration), current: Number(v.currentTime), paused: v.paused, ended: v.ended, ready: v.readyState } : null);
}

async function handleAd(page, report) {
  if (!(await safety.isAdVisible(page))) return false;
  // Only native ad Skip is clickable. On ordinary videos ArrowDown would
  // merely scroll, and swiping Shorts would leave the selected search result.
  const skip = page.locator(".ytp-ad-skip-button, .ytp-skip-ad-button, button.ytp-ad-skip-button-modern").first();
  if (await skip.isVisible()) {
    await skip.click({ timeout: 2000 });
    report("youtube", "Прогрев: реклама — пропуск.");
  }
  await page.waitForTimeout(500);
  return true;
}

async function maybeLike(page, job, report, watched) {
  if (watched < 5 || !job.warmupLikesEnabled || !(Number(job.warmupLikePercent) > 0) || Math.random() * 100 >= Number(job.warmupLikePercent)) return;
  if (await safety.isAdVisible(page)) return;
  const scope = /\/shorts\//.test(page.url()) ? "ytd-shorts " : "";
  const button = page.locator(scope + "like-button-view-model button:visible").first();
  if (!(await button.isVisible())) {
    report("youtube", "Прогрев: кнопка «Нравится» отсутствует.");
    return;
  }
  if (await safety.isAdVisible(page) || await button.getAttribute("aria-pressed") === "true") return;
  if (await button.isDisabled().catch(() => false)) {
    report("youtube", "Прогрев: лайк недоступен (кнопка неактивна).");
    return;
  }
  await button.click({ timeout: 3000 });
  await page.locator(scope + "like-button-view-model button[aria-pressed='true']:visible").first().waitFor({ state: "visible", timeout: 3000 });
  report("youtube", "Прогрев: лайк поставлен.");
}

async function watchResult(page, shorts, deadline, job, report, checkBoundary, sessionDeadline = deadline) {
  let state;
  const watchBudget = Math.max(0, deadline - Date.now());
  const metadataDeadline = Math.min(sessionDeadline, Date.now() + 60000);
  report("youtube", "Прогрев: ожидаю готовность видео, до 60 секунд в пределах времени сессии.");
  while (Date.now() < metadataDeadline) {
    if (await handleAd(page, report)) continue;
    state = await readState(page);
    if (state && Number.isFinite(state.duration) && state.duration > 0 && state.ready >= 2) break;
    await page.waitForTimeout(250);
  }
  if (!state || !(state.duration > 0) || !Number.isFinite(state.duration) || state.ready < 2) {
    const diagnostic = await page.evaluate(() => ({ url: location.href, videos: [...document.querySelectorAll("video")].map(v => ({
      duration: String(v.duration), ready: v.readyState, paused: v.paused, error: v.error && v.error.code,
      width: v.getBoundingClientRect().width, height: v.getBoundingClientRect().height
    })) }));
    report("diagnostic", "Прогрев: плееры при тайм-ауте готовности: " + JSON.stringify(diagnostic));
    report("youtube", "Прогрев: видео не загрузилось за время ожидания; пропуск без засчитанного просмотра.");
    return false;
  }
  // Loading consumes session time but must not exhaust the random watch slot.
  deadline = Math.min(sessionDeadline, Date.now() + watchBudget);
  // Metadata may arrive after playback has already advanced. Start the
  // mandatory initial segment at zero, before any optional forward seek.
  if (state.current > 0) {
    await withVideo(page, v => { if (v) v.currentTime = 0; });
    state = await readState(page);
  }
  const plan = planWatch(state.duration, (deadline - Date.now()) / 1000, shorts);
  if (!plan) return false;
  report("youtube", "Прогрев: " + (shorts ? "Shorts" : "видео") + " · " + plan.mode + " · до " + Math.round(plan.seconds) + " сек.");
  let watched = 0, previous = 0, sought = false;
  let stalledAt = Date.now();
  while (watched < plan.seconds && Date.now() < deadline) {
    if (await handleAd(page, report)) { previous = null; stalledAt = Date.now(); continue; }
    await checkBoundary();
    state = await readState(page);
    if (!state) throw new Error("Прогрев: видеоплеер исчез.");
    // Count actual playback progress, never buffering, seek jumps or Shorts loops.
    const delta = previous == null ? 0 : state.current - previous;
    if (delta > 0 && delta < 2) { watched += delta; stalledAt = Date.now(); }
    previous = state.current;
    if (watched >= plan.seconds || state.ended && state.duration < 5) break;
    if (state.ended && !shorts) break;
    if (!sought && watched >= plan.initial && plan.seekTo != null) {
      await withVideo(page, (v, position) => {
        if (v) v.currentTime = Math.min(position, v.duration - 0.1);
      }, plan.seekTo);
      previous = null;
      sought = true;
    }
    if (state.paused && !state.ended) {
      await withVideo(page, v => {
        if (v && v.paused && !v.ended) return v.play();
      });
    }
    if (Date.now() - stalledAt > 15000) throw new Error("Прогрев: воспроизведение не продвигается более 15 секунд.");
    await page.waitForTimeout(250);
  }
  await withVideo(page, v => { if (v) v.pause(); });
  report("youtube", "Прогрев: фактически просмотрено " + Math.round(watched) + " сек.");
  if (Date.now() < deadline) await maybeLike(page, job, report, watched);
  return watched >= Math.min(5, state.duration);
}

async function runShortsWarmup(rawPage, job, report = () => {}) {
  const keywords = parseKeywords(job.warmupKeywords);
  if (!keywords.length) throw new Error("Для прогрева нужны ключевые слова, по одному на строку.");
  const deadline = Date.now() + TOTAL_MS;
  let finishReason = "completed";
  const page = deadlinePage(rawPage, deadline);
  const boundary = await safety.installPlaybackBoundary(page, report, { allowAds: true });
  // Keep the shared navigation/pop-up protection, but guard its ad helper too.
  // check() only runs with no visible ad: handleAd owns the native skip flow.
  let count = 0, searchSec = 25, navigationSec = 10;
  const covered = new Set();
  const check = async () => {
    if (Date.now() >= deadline) throw new WarmupDeadline();
    if (!(await safety.isAdVisible(page))) await boundary.check();
  };
  try {
    report("youtube", "Прогрев по ключевым словам: 10 минут, включая поиск и переходы.");
    while (Date.now() < deadline) {
      let found = 0;
      for (const keyword of shuffle(keywords)) {
        if (deadline - Date.now() < 5000) break;
        await check();
        const shorts = Math.random() < 0.5;
        const searchStarted = Date.now();
        const results = shuffle(await searchKeyword(page, keyword, shorts, report));
        searchSec = Math.max(searchSec, (Date.now() - searchStarted) / 1000);
        const pending = keywords.filter(key => key !== keyword && !covered.has(key)).length;
        const budget = groupBudget((deadline - Date.now()) / 1000, pending, searchSec, navigationSec);
        const maxCount = Math.min(3, results.length, Math.floor(budget / (navigationSec + 5)));
        const selected = results.slice(0, maxCount > 0 ? 1 + Math.floor(Math.random() * maxCount) : 0);
        const groupDeadline = Math.min(deadline, Date.now() + budget * 1000);
        report("youtube", "Прогрев: «" + keyword + "» · " + (shorts ? "Shorts" : "видео") + " · найдено: " + results.length + " · выбрано: " + selected.length);
        if (!results.length) { report("youtube", "Прогрев: пустая выдача, следующий ключ."); continue; }
        if (!selected.length) { report("youtube", "Прогрев: недостаточно времени на просмотр с сохранением запаса для остальных ключей."); continue; }
        found += selected.length;
        for (let i = 0; i < selected.length; i++) {
          const remainingVideos = selected.length - i;
          const remaining = (groupDeadline - Date.now()) / 1000;
          if (remaining < navigationSec + 5) break;
          const videoDeadline = Math.min(groupDeadline, Date.now() + remaining / remainingVideos * 1000);
          await check();
          const navigationStarted = Date.now();
          await page.goto(selected[i].url, { waitUntil: "domcontentloaded", timeout: 30000 });
          navigationSec = Math.max(navigationSec, (Date.now() - navigationStarted) / 1000);
          if (videoDeadline - Date.now() < 5000) continue;
          if (await watchResult(page, shorts, videoDeadline, job, report, check, deadline)) {
            count++;
            covered.add(keyword);
          }
        }
      }
      // Empty results must not become a hot search loop. Stay open for the
      // remainder rather than repeatedly searching the same empty keywords.
      if (!found || deadline - Date.now() < 5000) {
        if (!found) report("youtube", "Прогрев: доступные результаты закончились; ожидание завершения 10 минут.");
        await page.waitForTimeout(Math.max(0, deadline - Date.now()));
      }
    }
  } catch (error) {
    if (error instanceof WarmupDeadline) finishReason = "time_limit";
    else throw error;
  } finally {
    await boundary.dispose();
  }
  const summary = buildResult(keywords, covered, count, finishReason);
  report("youtube", "Прогрев завершён: " + count + " ролик(ов), ключей с просмотром: " + summary.coveredKeywords + "/" + summary.totalKeywords + (summary.partial ? " · частично" : "") + ".");
  if (summary.partial) {
    report("warmup_journal", JSON.stringify({ stage: "finish", waitMs: TOTAL_MS, reason: summary.reason, missing: summary.missing.length }), {
      waitStage: "finish", waitMs: TOTAL_MS, reason: summary.reason + "; осталось ключей: " + summary.missing.length
    });
  }
  return summary;
}

module.exports = { runShortsWarmup, parseKeywords, shuffle, planWatch, groupBudget, deadlinePage, searchKeyword, watchResult, maybeLike, readState, buildResult, TOTAL_MS, WAIT_MS, WarmupDeadline };
