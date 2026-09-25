"use strict";

const assert = require("assert");

const MIN_LEAD = 900;
const MIN_GAP = 180;
const MAX_GAP = 900;

function generateUnixSlots(count, rng) {
  const slots = [];
  let cursorSec = Math.floor(Date.now() / 1000) + MIN_LEAD + rng.nextInt(0, 59);
  slots.push(cursorSec);
  for (let i = 1; i < count; i++) {
    cursorSec += rng.nextInt(MIN_GAP, MAX_GAP);
    slots.push(cursorSec);
  }
  return slots;
}

function createRng(seed) {
  let s = seed >>> 0;
  return {
    nextInt(min, max) {
      s = (s * 1664525 + 1013904223) >>> 0;
      const frac = s / 0x100000000;
      return min + Math.floor(frac * (max - min + 1));
    }
  };
}

const rng = createRng(42);
const slots = generateUnixSlots(20, rng);
assert.strictEqual(slots.length, 20);

const nowSec = Math.floor(Date.now() / 1000);
assert.ok(slots[0] >= nowSec + MIN_LEAD - 2, "first slot must be at least 15 minutes ahead");

for (let i = 1; i < slots.length; i++) {
  const gap = slots[i] - slots[i - 1];
  assert.ok(gap >= MIN_GAP && gap <= MAX_GAP, `gap ${gap} out of range at index ${i}`);
  assert.ok(slots[i] > slots[i - 1], "slots must strictly increase");
}

const local = new Date(slots[0] * 1000);
const backUnix = Math.floor(local.getTime() / 1000);
assert.ok(Math.abs(backUnix - slots[0]) <= 1, "unix timestamp must match local PC time");

console.log("OK: tiktok_schedule_regressions.js");
