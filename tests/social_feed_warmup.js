"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
(async () => {
  let scrollTop = 0, paused = false, current = 0, blocked = false;
  const scroller = { clientHeight: 700, scrollHeight: 5000, get scrollTop() { return scrollTop; }, scrollBy({ top }) { if (!blocked) scrollTop += top; } };
  const video = { parentElement: scroller, readyState: 4, duration: 45, ended: false,
    get currentTime() { return current; }, get paused() { return paused; },
    pause() { paused = true; }, async play() { paused = false; },
    getBoundingClientRect() { return { width: 300, height: 650, top: 0, bottom: 650, left: 0, right: 300 }; }
  };
  const article = { parentElement: scroller, getBoundingClientRect: video.getBoundingClientRect };
  let videos = [video], articles = [];
  const context = vm.createContext({ innerHeight: 700, innerWidth: 390, getComputedStyle: () => ({ visibility: "visible", overflowY: "auto" }),
    document: { scrollingElement: scroller, querySelectorAll(selector) { if (selector === "video") return videos; if (selector === "article,[role='article']") return articles; throw Error("Unexpected DOM access: " + selector); } } });
  for (const script of ["warmup-watch-plan.js", "social-feed-warmup.js"]) vm.runInContext(fs.readFileSync(path.join(__dirname, "../tools/uploader", script), "utf8"), context);
  const call = context.videoBatchFeedWarmup;
  assert.equal((await call("read")).watched, 0);
  current += .5; assert.equal((await call("read")).watched, .5);
  current += 10; assert.equal((await call("read")).watched, .5, "seek jumps are not viewing");
  await call("pause"); current += .5; assert.equal((await call("read")).watched, .5, "paused time is not viewing");
  await call("play"); current += .5; assert.equal((await call("read")).watched, 1);
  const plan = await call("plan", 20); assert(plan.seconds >= 5 && plan.seconds <= 20 && plan.seekTo === null);
  await call("scroll"); assert(scrollTop >= 560 && scrollTop <= 805); assert(paused);
  await call("read"); blocked = true; await assert.rejects(call("scroll"), /не прокручивается/);
  blocked = false; videos = []; articles = [article]; assert((await call("read")).article); await call("scroll");
  articles = []; await assert.rejects(call("scroll"), /не показала/);
  console.log("PASS: visible media playback accounting, shared random watch plans, pause/play, nested feed scroll, Threads articles, blocked/missing feed errors; no platform integration claimed.");
})().catch(error => { console.error(error); process.exitCode = 1; });
