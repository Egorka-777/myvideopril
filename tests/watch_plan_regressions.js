"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const worker = require("../tools/uploader/worker.js");

const fiveLong = Array.from({ length: 5 }, (_, i) => ({
  videoId: `longvid000${i}`.slice(-11),
  title: `Long ${i}`,
  kind: "long"
}));
const fiveShorts = Array.from({ length: 5 }, (_, i) => ({
  videoId: `shortvid00${i}`.slice(-11),
  title: `Short ${i}`,
  kind: "shorts"
}));

const plan = worker.buildCatalogPlan(fiveLong.concat(fiveShorts), "", "");
assert.strictEqual(plan.length, 10);
assert.strictEqual(plan.filter(x => x.kind === "long").length, 5);
assert.strictEqual(plan.filter(x => x.kind === "shorts").length, 5);
assert.strictEqual(worker.buildCatalogPlan(plan, fiveLong[0].videoId, "").length, 9);

const meshPlan = worker.buildMeshCatalogPlan(fiveLong, fiveLong[3].videoId, "long", true);
assert.strictEqual(meshPlan.length, 5, "meshSingleLong must keep all catalog videoIds");
assert.strictEqual(meshPlan[0].videoId, fiveLong[3].videoId, "preferVideoId must be first when meshSingleLong");

const meshLongIgnoresKindTag = worker.buildMeshCatalogPlan(
  [{ videoId: fiveShorts[0].videoId, title: "Tagged shorts", kind: "shorts" }],
  "",
  "long",
  true
);
assert.strictEqual(meshLongIgnoresKindTag.length, 1, "meshSingleLong must keep videoIds regardless of kind tag");

const meshShorts = worker.buildMeshCatalogPlan(fiveLong.concat(fiveShorts), "", "shorts", false);
assert.strictEqual(meshShorts.length, 5);
assert.strictEqual(meshShorts[0].kind, "shorts");

const enriched = worker.enrichWatchTarget({
  ownerProfileId: "owner-1",
  meshSingleLong: true,
  videoId: fiveLong[2].videoId,
  catalogVideos: fiveLong
}, null);
assert.strictEqual(enriched.knownVideos.length, 5, "mesh must keep all catalog long videos");
assert.strictEqual(enriched.knownVideos[0].videoId, fiveLong[2].videoId, "preferVideoId first in knownVideos");

const noSteal = worker.enrichWatchTarget({
  ownerProfileId: "owner-1",
  ownerName: "Same Name",
  meshSingleLong: true,
  catalogVideos: []
}, {
  videos: [{
    profileId: "other-profile",
    channel: "Same Name",
    videoId: "abcdefghijk",
    url: "https://youtu.be/abcdefghijk"
  }]
});
assert.strictEqual(noSteal.videoId, "", "mesh must not take videoId from catalog by channel name");

const fromProfile = worker.enrichWatchTarget({
  ownerProfileId: "owner-1",
  meshSingleLong: true,
  catalogVideos: []
}, {
  videos: [{
    profileId: "owner-1",
    channel: "Channel A",
    videoId: "dQw4w9WgXcQ",
    url: "https://youtu.be/dQw4w9WgXcQ"
  }]
});
assert.strictEqual(fromProfile.videoId, "dQw4w9WgXcQ", "mesh may use catalog only by exact profileId");

const titleOnly = worker.buildMeshCatalogPlan([{ title: "No ID", kind: "long", videoId: "" }], "", "long", true);
assert.strictEqual(titleOnly.length, 0, "mesh plan must not use title-only entries");

const root = path.resolve(__dirname, "..");
const source = fs.readFileSync(path.join(root, "tools", "uploader", "worker.js"), "utf8");
const cs = fs.readFileSync(path.join(root, "src", "Uploader.cs"), "utf8");
assert.doesNotMatch(source, /MESH_LONG_MAX|MESH_SHORTS_MAX/);
assert.doesNotMatch(cs, /CollectMeshChannelsForMarket/);
assert.match(cs, /CollectMeshLongChannels/);
assert.match(cs, /meshSingleLong/);
assert.match(source, /buildMeshCatalogPlan/);

console.log("OK: watch_plan_regressions.js");
