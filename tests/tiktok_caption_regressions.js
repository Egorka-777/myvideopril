"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const worker = require("../tools/uploader/worker-tiktok.js");

assert.strictEqual(worker.normalizeCaption("  первая\r\n\r\nстрока  "), "первая\n\nстрока");

const root = path.resolve(__dirname, "..");
const core = fs.readFileSync(path.join(root, "src", "Core.cs"), "utf8");
const tiktok = fs.readFileSync(path.join(root, "src", "TikTok.cs"), "utf8");
const uploader = fs.readFileSync(path.join(root, "tools", "uploader", "worker-tiktok.js"), "utf8");

assert.match(core, /public const string HashTags="#BinoDex #обучениетрейдингу #pocketoption #трейдинг #aitrading"/);
assert.match(core, /TikTokCaptionTemplates\.RussianDefaults\(\)/);
assert.match(tiktok, /TikTokCaptionTemplates\.Allocate/);
assert.doesNotMatch(tiktok, /RemoveRange\(0,count\)/);
assert.match(uploader, /async function fillCaption/);
assert.match(uploader, /publishAndConfirm/);
assert.match(uploader, /dismissTikTokBlockingDialogs/);
assert.doesNotMatch(uploader, /async function fillTitleAndDescription/);

console.log("OK: tiktok_caption_regressions.js");
