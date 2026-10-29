"use strict";
const assert = require("assert/strict");
const { parseKeywords, shuffle, planWatch, groupBudget, deadlinePage, TOTAL_MS, WarmupDeadline, runShortsWarmup, watchResult } = require("../tools/uploader/youtube-shorts-warmup");

(async () => {
  assert.equal(TOTAL_MS, 600000);
  assert.deepEqual(parseKeywords(" one\r\n two \n\n one\nthree;four"), ["one", "two", "three;four"]);
  const input = [1, 2, 3, 4];
  assert.deepEqual(shuffle(input, () => 0), [2, 3, 4, 1]);
  assert.deepEqual(input, [1, 2, 3, 4]);
  for (const duration of [1, 4.9, 5, 6, 15, 60, 7200]) {
    for (const remaining of [1, 4, 5, 12, 300]) {
      for (const random of [0, 0.25, 0.49, 0.5, 0.75, 0.99]) {
        for (const shorts of [true, false]) {
          const plan = planWatch(duration, remaining, shorts, () => random);
          if (remaining < Math.min(5, duration)) { assert.equal(plan, null); continue; }
          assert(plan.seconds <= duration && plan.seconds <= remaining);
          assert(plan.seconds >= Math.min(5, duration));
          if (shorts || plan.mode === "full") assert.equal(plan.seekTo, null);
          if (plan.seekTo != null) {
            assert(plan.seekTo >= 5);
            assert(plan.seekTo + plan.seconds <= duration);
          }
        }
      }
    }
  }
  assert.equal(planWatch(NaN, 20, false), null);
  assert.equal(planWatch(60, 100, false, () => 0.99).seconds, 60);
  assert.equal(planWatch(60, 20, false, () => 0.99).seconds, 20);
  // Nine keys fit with search/navigation costs, including random-sized groups.
  const groupSizes = new Set();
  for (let seed = 1; seed <= 100; seed++) {
    let state = seed, remaining = 600;
    const random = () => { state = (state * 1664525 + 1013904223) >>> 0; return state / 4294967296; };
    for (let pending = 8; pending >= 0; pending--) {
      remaining -= 25;
      const budget = groupBudget(remaining, pending, 25, 10, random);
      assert(budget <= remaining - pending * 40 + 0.000001);
      const max = Math.min(3, Math.floor(budget / 15));
      assert(max >= 1, "every key retains a load and minimum viewing time");
      const size = 1 + Math.floor(random() * max);
      groupSizes.add(size);
      const perVideo = budget / size;
      for (let i = 0; i < size; i++) {
        const plan = planWatch(7200, perVideo - 10, false, random);
        assert(plan && plan.seconds >= 5);
        remaining -= 10 + plan.seconds;
      }
    }
    assert(remaining >= 0);
  }
  assert.deepEqual([...groupSizes].sort(), [1, 2, 3]);
  assert.equal(groupBudget(100, 8, 25, 10), 0);
  let now = 0, calls = [], cleanup = 0;
  const locator = { click: async options => calls.push(options), waitFor: async options => calls.push(options) };
  const raw = {
    locator: () => locator,
    goto: async (url, options) => calls.push(options),
    waitForTimeout: async ms => { calls.push(ms); now += ms; },
    off: () => cleanup++, unroute: async () => cleanup++
  };
  const page = deadlinePage(raw, 1000, () => now);
  await page.goto("fixture", { timeout: 90000 });
  await page.locator("fixture").click({ timeout: 5000 });
  await page.locator("fixture").waitFor({ timeout: 90000 });
  assert.deepEqual(calls, [{ timeout: 1000 }, { timeout: 1000 }, { timeout: 1000 }]);
  await page.waitForTimeout(5000);
  assert.equal(calls[3], 1000);
  assert.throws(() => page.goto("forbidden"), WarmupDeadline);
  page.off(); await page.unroute(); assert.equal(cleanup, 2);
  await assert.rejects(runShortsWarmup({}, { warmupKeywords: "  " }), /ключевые слова/);
  await assert.rejects(deadlinePage({ evaluate: async () => { throw Error("real browser failure"); } }, Date.now() + 1000).evaluate(() => {}), /real browser failure/);
  await assert.rejects(deadlinePage({ evaluate: () => new Promise(() => {}) }, Date.now() + 20).evaluate(() => {}), WarmupDeadline);
  const realNow = Date.now;
  let clock = 0, readyAfter = 20000, current = 0;
  const messages = [];
  const slowPage = {
    url: () => 'fixture:slow-video',
    waitForTimeout: async ms => { clock += ms; },
    evaluate: async () => ({ url: 'fixture:slow-video', videos: [{ duration: '47', ready: 0 }] }),
    evaluateHandle: async () => ({
      evaluate: async (action, argument) => action({
        duration: 47, readyState: clock >= readyAfter ? 4 : 0,
        get currentTime() { if (clock >= readyAfter) current += 0.25; return current; },
        set currentTime(value) { current = value; },
        paused: false, ended: false, pause() {}
      }, argument),
      dispose: async () => {}
    })
  };
  try {
    Date.now = () => clock;
    // A five-second watch slot must survive twenty seconds of loading.
    assert(await watchResult(slowPage, true, 5000, {}, (s, t) => messages.push(t), async () => {}, 120000));
    assert(clock >= 24000 && clock < 30000);
    assert(messages.some(t => /фактически просмотрено/.test(t)));
    clock = 0; readyAfter = Infinity; current = 0;
    assert.equal(await watchResult(slowPage, true, 5000, {}, (s, t) => messages.push(t), async () => {}, 120000), false);
    assert.equal(clock, 60000, 'unready video is skipped after one minute, not thrown');
    clock = 0;
    assert.equal(await watchResult(slowPage, true, 5000, {}, () => {}, async () => {}, 12000), false);
    assert.equal(clock, 12000, 'session limit bounds loading wait');
    assert(messages.some(t => /пропуск без засчитанного просмотра/.test(t)));
  } finally { Date.now = realNow; }
  console.log("OK: random groups, nine-key budget, delayed playback readiness, 60s skip and session deadline tests");
})().catch(error => { console.error(error); process.exitCode = 1; });
