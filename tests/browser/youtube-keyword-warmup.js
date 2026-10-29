"use strict";
const assert = require("assert/strict");
const { chromium } = require("../../tools/uploader/node_modules/playwright-core");
const { deadlinePage, searchKeyword, watchResult, maybeLike, readState } = require("../../tools/uploader/youtube-shorts-warmup");
const safety = require("../../tools/uploader/youtube-interaction-safety");

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    let requests = [];
    const fulfillFixture = async route => {
      requests.push(route.request().url());
      assert(route.request().url().startsWith("https://www.youtube.com/"), "fixture never contacts a real site");
      if (new URL(route.request().url()).searchParams.get("search_query") === "empty fixture") {
        await route.fulfill({ contentType: "text/html", body: '<form action="/results"><input name="search_query"></form><ytd-search><ytd-message-renderer>No results</ytd-message-renderer></ytd-search>' });
        return;
      }
      await route.fulfill({ contentType: "text/html", body: `<form action="/results"><input name="search_query"></form>
        <ytd-search><button aria-label="Search filters" onclick="document.querySelector('ytd-search-filter-renderer').hidden=false">Filters</button>
        <ytd-search-filter-renderer hidden><a id="endpoint" href="/results?search_query=native+shorts&fixture_filter=shorts" onclick="event.preventDefault();window.nativeShorts=true;history.replaceState({},'',this.href);setTimeout(() => { document.querySelector('ytd-reel-item-renderer').hidden=false; }, 400)">Shorts</a></ytd-search-filter-renderer>
        <ytd-video-renderer><a href="/watch?v=abcdefghijk">Ordinary</a><a href="/watch?v=abcdefghijk">Duplicate</a></ytd-video-renderer>
        <ytd-reel-item-renderer hidden><a href="/shorts/lmnopqrstuv">Short</a></ytd-reel-item-renderer>
        <yt-lockup-view-model hidden id="stale-ordinary"><a href="/watch?v=ordinary123">Old ordinary result</a></yt-lockup-view-model>
        <ytd-ad-slot-renderer><ytd-video-renderer><a href="/watch?v=advert12345">Ad</a></ytd-video-renderer></ytd-ad-slot-renderer>
        <ytd-message-renderer hidden>Stale no results</ytd-message-renderer></ytd-search>
        <script>
          const results = [...document.querySelectorAll('ytd-video-renderer, ytd-reel-item-renderer')];
          const content = results.map(el => el.innerHTML);
          results.forEach(el => { el.innerHTML = ''; });
          setTimeout(() => results.forEach((el, i) => { el.innerHTML = content[i]; }), 300);
        </script>` });
    };
    // The safety boundary installs a page route. Install this fixture route
    // afterwards (page routes precede context routes) so no network can escape.
    const raw = await context.newPage();
    const page = deadlinePage(raw, Date.now() + 30000);
    const boundary = await safety.installPlaybackBoundary(page, () => {}, { allowAds: true });
    await raw.route("**/*", fulfillFixture);
    try {
      const ordinary = await searchKeyword(page, "native fixture", false);
      assert.deepEqual(ordinary.map(x => x.id), ["abcdefghijk"]);
      const shorts = await searchKeyword(page, "native shorts", true);
      assert.deepEqual(shorts.map(x => x.id), ["lmnopqrstuv"]);
      assert(await raw.evaluate(() => window.nativeShorts));
      assert(requests.some(url => url.includes("search_query=native+shorts")));
      assert(!requests.some(url => url.includes("sp=")), "no guessed filter URL");
      const empty = await searchKeyword(page, "empty fixture", false);
      assert.deepEqual(empty, []);
      await raw.setContent(`<video></video><ytd-segmented-like-dislike-button-renderer><like-button-view-model><button aria-label="Like" aria-pressed="false" onclick="this.setAttribute('aria-pressed','true');this.setAttribute('aria-label','Unlike');window.likes=(window.likes||0)+1">Like</button></like-button-view-model></ytd-segmented-like-dislike-button-renderer>`);
      await raw.evaluate(() => {
        const v = document.querySelector("video"); let current = -0.5;
        Object.defineProperties(v, {
          duration: { get: () => 5 }, readyState: { get: () => 4 },
          currentTime: { get: () => { current = Math.min(5, current + 0.5); return current; } },
          paused: { get: () => false }, ended: { get: () => current >= 5 }
        });
      });
      const oldRandom = Math.random; Math.random = () => 0.99;
      try {
        assert(await watchResult(page, false, Date.now() + 15000, { warmupLikesEnabled: true, warmupLikePercent: 100 }, () => {}, () => boundary.check()));
      } finally { Math.random = oldRandom; }
      assert.equal(await raw.evaluate(() => window.likes || 0), 1);
      // Already liked fixture must not be toggled on a second five-second watch.
      await raw.evaluate(() => {
        const v = document.querySelector("video");
        // A fresh element clears the first fixture's non-configurable properties.
        const next = document.createElement("video"); v.replaceWith(next); let current = -0.5;
        Object.defineProperties(next, { duration: { get: () => 5 }, readyState: { get: () => 4 }, currentTime: { get: () => { current = Math.min(5, current + 0.5); return current; } }, paused: { get: () => false }, ended: { get: () => current >= 5 } });
      });
      assert(await watchResult(page, false, Date.now() + 15000, { warmupLikesEnabled: true, warmupLikePercent: 100 }, () => {}, () => boundary.check()));
      assert.equal(await raw.evaluate(() => window.likes), 1);
      // Current Shorts markup has no [is-active]; hidden prefetched players
      // and a liked offscreen Short must not mask the visible player's state.
      await raw.setContent(`<video hidden></video><ytd-shorts>
        <ytd-reel-video-renderer style="display:none"><video></video><like-button-view-model><button aria-pressed="true">Old like</button></like-button-view-model></ytd-reel-video-renderer>
        <ytd-reel-video-renderer><video></video><like-button-view-model><toggle-button-view-model><button-view-model><label>
        <button aria-label='поставить отметку "Нравится"' aria-pressed="false" onclick="this.setAttribute('aria-pressed','true');window.shortsLikes=(window.shortsLikes||0)+1">Нравится</button>
        </label></button-view-model></toggle-button-view-model></like-button-view-model></ytd-reel-video-renderer></ytd-shorts>`);
      await raw.evaluate(() => {
        history.replaceState({}, '', '/shorts/lmnopqrstuv');
        const v = document.querySelector('ytd-reel-video-renderer:not([style]) video');
        Object.defineProperties(v, { duration: { get: () => 85 }, readyState: { get: () => 4 }, currentTime: { get: () => 12 } });
      });
      assert.equal((await readState(page)).duration, 85);
      assert.equal((await readState(page)).current, 12);
      await maybeLike(page, { warmupLikesEnabled: true, warmupLikePercent: 100 }, () => {}, 10);
      assert.equal(await raw.evaluate(() => window.shortsLikes), 1);
      await maybeLike(page, { warmupLikesEnabled: true, warmupLikePercent: 100 }, () => {}, 10);
      assert.equal(await raw.evaluate(() => window.shortsLikes), 1, 'existing Shorts like is never toggled off');
    } finally { await boundary.dispose(); }
    assert.equal(context.pages().length, 1);
    assert(!raw.isClosed(), "profile page remains open");
    console.log("OK: isolated native search/filter, distinct results, playback and non-toggle like fixture (not live integration)");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
