"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const tiktok = fs.readFileSync(path.join(root, "src", "TikTok.cs"), "utf8");
const staging = fs.readFileSync(path.join(root, "src", "UploadStaging.cs"), "utf8");

assert.match(tiktok, /PrepareTikTokUploadFiles/,
  "TikTok upload must stage videos before HTTP worker starts.");
assert.match(tiktok, /UploadStaging\.TryResolveVideo/,
  "TikTok must resolve stale Downloads paths before failing.");
assert.match(tiktok, /UploadStaging\.StageVideo/,
  "TikTok must copy videos into stable staging folder.");
assert.match(staging, /TryResolveVideo/,
  "Shared staging must recover renamed duplicate files like clip (85).mp4.");
assert.match(staging, /RunSelfTests/,
  "Upload staging must have self-tests.");

console.log("OK: tiktok_staging_regressions.js");
