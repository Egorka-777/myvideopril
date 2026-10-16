"use strict";
const assert = require("assert");
const { pickHumanWatchSeconds, DEEP_WATCH_FRACTION, HARD_WATCH_CAP_SEC, clampWatchBounds } = require("../tools/uploader/youtube-shorts-warmup");

let deep = 0, quick = 0, n = 4000;
for (let i = 0; i < n; i++) {
  const s = pickHumanWatchSeconds(1, 40, 55);
  assert(s >= 1 && s <= 40, "in bounds: " + s);
  if (s >= 11) deep++;
  else quick++;
}
const deepRatio = deep / n;
assert(deepRatio > 0.12 && deepRatio < 0.32, "deep ratio ~20% got " + deepRatio.toFixed(3));

const full = pickHumanWatchSeconds(1, 40, 30);
assert(full >= 1 && full <= 40);
const bounds = clampWatchBounds(1, 999);
assert.equal(bounds.max, HARD_WATCH_CAP_SEC);
assert.equal(DEEP_WATCH_FRACTION, 0.2);

console.log("OK: youtube_warmup_human.js");
