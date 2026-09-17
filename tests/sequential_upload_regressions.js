"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const worker = fs.readFileSync(path.join(root, "tools", "uploader", "worker.js"), "utf8");
const uploader = fs.readFileSync(path.join(root, "src", "Uploader.cs"), "utf8");
const qm = fs.readFileSync(path.join(root, "src", "QueueManager.cs"), "utf8");

assert.match(worker, /async function uploadPackSequentially/);
assert.match(worker, /openUploadAndSetFiles\(page, \[item\.video\]\)/);
assert.match(worker, /2026-09-17-sequential-schedule-v1/);
assert.match(worker, /scheduleAndConfirmUpload/);
assert.match(worker, /scheduleConfirmed: true/);
assert.match(worker, /keptOpen: true/);
assert.match(worker, /uploadPackSequentially\(page, pack\)/);

assert.doesNotMatch(worker, /finishUploadVisibility[\s\S]{0,400}saveAsPrivate/,
  "schedule failure must not fall back to Private");

assert.match(worker, /normalizeUploadState/);
assert.match(worker, /state === "scheduled"/);
assert.match(worker, /state === "unknown"/);

assert.match(uploader, /UploadItemState\.SkipAutoUpload/);
assert.match(uploader, /scheduleConfirmed/);
assert.match(uploader, /Func<UploadJob> buildJob/);
assert.match(uploader, /CountScheduledItems/);
assert.match(uploader, /Отложено .* ✓/);
assert.match(qm, /packIndex=it\.Index/);

const seqBlock = worker.slice(worker.indexOf("async function uploadPackSequentially"), worker.indexOf("async function main"));
const openCalls = (seqBlock.match(/openUploadAndSetFiles\(page, \[item\.video\]\)/g) || []).length;
assert.ok(openCalls >= 1, "sequential path must upload one file at a time");

assert.match(worker, /confirmed < expected/,
  "final success must require all items confirmed");

console.log("sequential upload regression checks passed");
