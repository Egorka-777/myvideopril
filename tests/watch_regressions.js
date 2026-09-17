"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const worker = fs.readFileSync(path.join(root, "tools", "uploader", "worker.js"), "utf8");
const uploader = fs.readFileSync(path.join(root, "src", "Uploader.cs"), "utf8");

assert.match(worker, /restartAlreadyRunningProfile\(\)/,
  "An already-running Dolphin profile must be restarted when no automation endpoint is available.");
assert.match(worker, /Профиль перезапущен в режиме автоматизации/,
  "Profile recovery must be visible in the log.");

assert.doesNotMatch(worker, /duration\s*\*\s*0\.9/,
  "Shorts must not be declared complete at 90% playback.");
assert.match(worker, /Шортс просмотрен полностью/,
  "A completed Short must have an explicit full-playback log entry.");

assert.match(worker, /лайк не поставлен, просмотр продолжается/,
  "A like failure must not abort playback.");
assert.doesNotMatch(worker, /Лайк уже стоит — видео уже смотрели, пропуск/,
  "A pre-existing like is not proof that the current run watched the video.");

assert.match(worker, /if \(failed\) \{\s*fail\(summary/s,
  "A partially completed mesh must terminate with an error for old and new desktop builds.");
assert.doesNotMatch(worker, /return Math\.max\(extra,\s*1\)/,
  "Zero real views must not be converted into a phantom successful view.");
assert.match(uploader, /result\.Success=msg\.success/,
  "The desktop app must honor the worker's success=false result.");

console.log("watch regression checks passed");
