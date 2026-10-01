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
assert.match(fs.readFileSync(path.join(root, "src", "YouTubeWorkspacePanel.cs"), "utf8"), /RunLinkWatch/,
  "Direct link watch must start from YouTube workspace panel.");
assert.match(worker, /job\.watchDirectLinks/,
  "Worker must support direct link watch mode.");
assert.match(worker, /Профиль оставлен открытым/,
  "Direct link watch must keep Dolphin profile open when requested.");
assert.match(uploader, /ParseDirectWatchLinks/,
  "Uploader must parse pasted YouTube links into watch targets.");
assert.match(uploader, /CrossWatchLinks/,
  "Uploader must run link-batch watch across checked accounts.");

assert.doesNotMatch(worker, /openFoundVideoMesh\(page, target\.title/,
  "Mesh must not search by title when videoId is missing.");
assert.match(worker, /assertMeshVideoOpen/,
  "Mesh must verify the opened video is accessible.");
assert.match(worker, /openMeshVideoViaChannel/,
  "Mesh must open the channel page before playing a video.");
assert.match(worker, /findWatchLinkOnVideosTab/,
  "Mesh long must locate catalog video on Videos tab with scroll.");
assert.match(worker, /openMeshLongFromVideosTab/,
  "Mesh long must open catalog video by clicking Videos tab, not direct URL.");
assert.match(worker, /assertMeshLongVideoOpen/,
  "Mesh long must reject Shorts player and videos shorter than minimum.");
assert.match(worker, /if \(meshLong\) \{[\s\S]*?openMeshLongFromVideosTab/,
  "Mesh long branch must open videos via Videos tab click.");
assert.match(worker, /только клик с вкладки/,
  "Mesh long must log Videos-tab click path.");
assert.doesNotMatch(worker, /запасное длинное/,
  "Mesh long must not substitute random channel video when catalog is scheduled.");
assert.match(worker, /verifyMeshChannelPage/,
  "Mesh must confirm channel by @handle when channelUrl is set.");
assert.match(uploader, /IsValidYouTubeVideoId/,
  "Mesh anchor must require a valid 11-char videoId.");
assert.match(uploader, /contentKind="long"/,
  "Mesh target must always use long content for meshSingleLong.");
assert.match(uploader, /BuildMeshCatalogVideos/,
  "Mesh must send all published videoIds, not only the last one.");
assert.match(uploader, /MoveFileReplacing/,
  "Video assign must rename files on disk instead of silently skipping failed moves.");
assert.match(uploader, /ch\.Kind=packKind/,
  "Assigning videos must sync channel kind with Long/Shorts workspace toggle.");
assert.match(uploader, /WatchMaxParallelProfiles/,
  "Mesh batch size must come from WatchMaxParallelProfiles setting.");
assert.match(uploader, /meshBatchSize=Math\.Max\(1,Math\.Min\(5,settings\.WatchMaxParallelProfiles\)\)/,
  "Mesh batch size must be clamped 1–5 from settings.");
assert.match(worker, /findWatchLinkOnVideosTab/,
  "Mesh long watch must scan Videos tab for catalog videoId.");
assert.doesNotMatch(worker, /meshLong \? \[\"\/videos\", \"\/shorts\"\]/,
  "Mesh long must not scan Shorts tab (opens /shorts/ and breaks full watch).");
assert.match(worker, /открываю кликом с вкладки/,
  "Mesh long must open catalog videos by clicking Videos tab card.");
assert.match(worker, /MESH_MIN_LONG_SECONDS/,
  "Mesh long must reject videos shorter than minimum duration.");
assert.match(uploader, /MeshBatchItems/,
  "Mesh must use last published batch even when local video files still exist.");
assert.match(uploader, /meshBatchTotal/,
  "Mesh watch must process sequential batches.");
assert.match(uploader, /Сетка: пачка/,
  "Mesh log must announce each batch.");

console.log("watch regression checks passed");
