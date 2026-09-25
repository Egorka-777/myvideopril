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

assert.match(uploader, /ValidateCommon\(false,channelsFilter\)/,
  "Mesh watch must validate passed channels, not hidden grid selection.");
assert.match(uploader, /ValidateExternalChannels/,
  "External channel list must bypass grid checkbox validation for mesh/check.");
assert.match(uploader, /BuildMeshViewers/,
  "Mesh must build viewer list from checked YouTube workspace channels.");
assert.match(fs.readFileSync(path.join(root, "src", "YouTubeWorkspacePanel.cs"), "utf8"), /RunMesh/,
  "Mesh must start from YouTube workspace panel.");

assert.doesNotMatch(worker, /openFoundVideoMesh\(page, target\.title/,
  "Mesh must not search by title when videoId is missing.");
assert.match(worker, /assertMeshVideoOpen/,
  "Mesh must verify the opened video is accessible.");
assert.match(worker, /openMeshVideoViaChannel/,
  "Mesh must open the channel page before playing a video.");
assert.match(worker, /каталог недоступен \(отложено\?\)/,
  "Mesh must fall back to any public video on the channel when catalog ids are scheduled.");
assert.match(worker, /verifyMeshChannelPage/,
  "Mesh must confirm channel by @handle when channelUrl is set.");
assert.match(uploader, /IsValidYouTubeVideoId/,
  "Mesh anchor must require a valid 11-char videoId.");
assert.match(uploader, /contentKind="long"/,
  "Mesh target must always use long content for meshSingleLong.");
assert.match(uploader, /BuildMeshCatalogVideos/,
  "Mesh must send all published videoIds, not only the last one.");

console.log("watch regression checks passed");
