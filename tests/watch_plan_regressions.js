"use strict";

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const worker = require("../tools/uploader/worker.js");

const fiveLong = Array.from({ length: 5 }, (_, i) => ({
  videoId: `long-${i}`,
  title: `Long ${i}`,
  kind: "long"
}));
const fiveShorts = Array.from({ length: 5 }, (_, i) => ({
  videoId: `short-${i}`,
  title: `Short ${i}`,
  kind: "shorts"
}));

const plan = worker.buildCatalogPlan(fiveLong.concat(fiveShorts), "", "");
assert.strictEqual(plan.length, 10);
assert.strictEqual(plan.filter(x => x.kind === "long").length, 5);
assert.strictEqual(plan.filter(x => x.kind === "shorts").length, 5);
assert.strictEqual(worker.buildCatalogPlan(plan, "long-0", "").length, 9);

const meshPlan = worker.buildMeshCatalogPlan(fiveLong.concat(fiveShorts), "long-3");
assert.strictEqual(meshPlan.length, 1);
assert.strictEqual(meshPlan[0].videoId, "long-3");
assert.strictEqual(meshPlan[0].kind, "long");

const enriched = worker.enrichWatchTarget({
  ownerProfileId: "owner-1",
  meshSingleLong: true,
  videoId: "long-2",
  catalogVideos: fiveLong
}, null);
assert.strictEqual(enriched.knownVideos.length, 1, "mesh must keep one long video");
assert.strictEqual(enriched.knownVideos[0].videoId, "long-2");

const root = path.resolve(__dirname, "..");
const source = fs.readFileSync(path.join(root, "tools", "uploader", "worker.js"), "utf8");
const cs = fs.readFileSync(path.join(root, "src", "Uploader.cs"), "utf8");
assert.doesNotMatch(source, /MESH_LONG_MAX|MESH_SHORTS_MAX/);
assert.doesNotMatch(cs, /CollectMeshChannelsForMarket/);
assert.match(cs, /CollectMeshLongChannels/);
assert.match(cs, /meshSingleLong/);
assert.match(source, /buildMeshCatalogPlan/);

console.log("OK: watch_plan_regressions.js");
