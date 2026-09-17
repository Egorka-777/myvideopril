"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const cs = fs.readFileSync(path.join(root, "src", "TitleCleaner.cs"), "utf8");
const worker = fs.readFileSync(path.join(root, "tools", "uploader", "worker.js"), "utf8");

const LeadingNumber = /^\s*(?:\d{1,3}[\.\)\-_]\s*|\d{1,3}\s+)/;
const PackSuffix = /(\s*[·•]\s*\+\d+\s*|\s+\+\d+\s*)$/;
const PipeNorm = /\s*\|\s*/g;
const DotSeparator = /(?<!\d)\s+\.\s+(?!\d)/g;
const EllipsisSep = /\s*\.\.\.\s*/g;
const MultiPipe = /\|\s*\|+/g;
const MultiSpace = /\s{2,}/g;

function trimPipeEdges(t) {
  while (t.startsWith("|")) t = t.slice(1).trim();
  while (t.endsWith("|")) t = t.slice(0, -1).trim();
  return t;
}

function cleanForUpload(title) {
  if (!title || !String(title).trim()) return "";
  let t = String(title).trim();
  while (true) {
    const m = t.match(LeadingNumber);
    if (!m) break;
    t = t.slice(m[0].length).trim();
  }
  t = t.replace(PackSuffix, "").trim();
  t = t.replace(EllipsisSep, " | ");
  t = t.replace(DotSeparator, " | ");
  t = t.replace(PipeNorm, " | ");
  t = t.replace(MultiPipe, " | ");
  t = t.replace(MultiSpace, " ").trim();
  t = trimPipeEdges(t);
  if (t.length > 100) throw new Error("too long");
  return t;
}

function makeWindowsSafeTitle(title) {
  let t;
  try { t = cleanForUpload(title); } catch { t = String(title || "").trim(); }
  const parts = t.split(" | ").map(p => p.trim()).filter(Boolean);
  const safe = parts.map(p => p.replace(/[<>:"/\\|?*]/g, " ").replace(/\s+/g, " ").trim()).filter(Boolean);
  return safe.length ? safe.join(" . ") : "video";
}

function validateUploadTitle(raw, ctx) {
  const t = String(raw || "").trim();
  if (!t) throw new Error(`${ctx}: empty`);
  if (t.length > 100) throw new Error(`${ctx}: too long`);
  if (/^\s*\d{1,3}[\.\)\-_]\s*/.test(t)) throw new Error(`${ctx}: leading number`);
  if (/\|\s*\|/.test(t) || /^\|/.test(t) || /\|$/.test(t)) throw new Error(`${ctx}: bad pipes`);
  return t;
}

assert.strictEqual(cleanForUpload("01. Pocket Option   .   AI Trading"), "Pocket Option | AI Trading");
assert.strictEqual(cleanForUpload("2) BinoDex  |  Бинарные опционы"), "BinoDex | Бинарные опционы");
assert.strictEqual(cleanForUpload("003_ ИИ трейдинг бот... бинарные опционы стратегия"), "ИИ трейдинг бот | бинарные опционы стратегия");
assert.strictEqual(cleanForUpload("Стратегия 1.5 для трейдинга"), "Стратегия 1.5 для трейдинга");
assert.strictEqual(cleanForUpload("1. Заголовок"), "Заголовок");
assert.strictEqual(cleanForUpload("5 Заголовок"), "Заголовок");
assert.strictEqual(cleanForUpload("Title · +3"), "Title");
assert.doesNotMatch(cleanForUpload("Part A | Part B"), /\|\s*\|/);
assert.throws(() => cleanForUpload("X".repeat(101)), /too long/);

const yt = cleanForUpload("Pocket Option | AI Trading");
const win = makeWindowsSafeTitle("Pocket Option | AI Trading");
assert.match(yt, /\|/);
assert.doesNotMatch(win, /\|/);
assert.match(win, /Pocket Option . AI Trading/);

const sample = cleanForUpload("01. A | B");
validateUploadTitle(sample, "test");
assert.throws(() => validateUploadTitle("01. A | B", "test"), /leading number/);

assert.match(cs, /MakeWindowsSafeTitle/);
assert.match(cs, /throw new Exception\("Заголовок после очистки длиннее/);
assert.match(worker, /validateUploadTitle/);
const titleBlock = worker.slice(worker.indexOf("function validateUploadTitle"), worker.indexOf("function parseLocalDateTime"));
assert.doesNotMatch(titleBlock, /\.slice\(0,\s*100\)/, "title must not be silently truncated in worker");
assert.match(titleBlock, /длиннее 100 символов/);

console.log("title cleaner regression checks passed");
