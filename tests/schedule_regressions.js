"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const cs = fs.readFileSync(path.join(root, "src", "ScheduleGenerator.cs"), "utf8");
const worker = fs.readFileSync(path.join(root, "tools", "uploader", "worker.js"), "utf8");

const MinGap = 15;
const MaxGap = 60;

function ceilMinute(dt) {
  if (dt.getSeconds() === 0 && dt.getMilliseconds() === 0) return new Date(dt.getTime());
  return new Date(dt.getFullYear(), dt.getMonth(), dt.getDate(), dt.getHours(), dt.getMinutes() + 1, 0, 0);
}

function parseIso(iso) {
  return new Date(iso);
}

function generateSlots(savedNext, count, now = new Date()) {
  let next = savedNext ? parseIso(savedNext) : new Date(0);
  const minimum = ceilMinute(new Date(now.getTime() + 30 * 60000));
  if (next < minimum) next = minimum;
  const slots = [];
  for (let i = 0; i < count; i++) {
    slots.push(new Date(next.getTime()));
    const gap = MinGap + Math.floor(Math.random() * (MaxGap - MinGap + 1));
    next = new Date(next.getTime() + gap * 60000);
  }
  return { slots, nextFree: next };
}

function randomGapMinutes() {
  return 15 + Math.floor(Math.random() * 46);
}

for (let trial = 0; trial < 50; trial++) {
  const gap = randomGapMinutes();
  assert.ok(gap >= 15 && gap <= 60, `gap ${gap} out of range`);
}

const run1 = generateSlots(null, 5);
const now = new Date();
for (const s of run1.slots) assert.ok(s >= ceilMinute(new Date(now.getTime() + 30 * 60000 - 1000)), "slot in past");
for (let i = 1; i < run1.slots.length; i++) {
  assert.ok(run1.slots[i] > run1.slots[i - 1], "times must increase");
  const diffMin = (run1.slots[i] - run1.slots[i - 1]) / 60000;
  assert.ok(diffMin >= 15 && diffMin <= 60, `interval ${diffMin} out of range`);
}

const run2 = generateSlots(run1.nextFree.toISOString(), 3);
assert.notStrictEqual(run2.slots[0].getTime(), run1.slots[run1.slots.length - 1].getTime(),
  "next run must not reuse last assigned slot");

assert.match(cs, /MinGapMinutes=15/);
assert.match(cs, /MaxGapMinutes=60/);
assert.match(cs, /profiles/);
assert.match(cs, /FirstSlotLeadMinutes=30/);
assert.match(worker, /adjustScheduleIfNeeded/);
assert.match(worker, /refreshRemainingSchedules/);
assert.match(worker, /стало слишком близким/);

console.log("schedule regression checks passed");
