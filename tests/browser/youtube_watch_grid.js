"use strict";
const assert = require("assert");
const { chromium } = require("../../tools/uploader/node_modules/playwright-core");
const { openChannelsWatch } = require("../../tools/uploader/youtube-open-today");
const playback = require("../../tools/uploader/youtube-playback");

const channels = {
  en: { id: "ENTODAY0001", owner: "UC-en", date: "2026-10-01", label: "Videos" },
  ru: { id: "RUTODAY0001", owner: "UC-ru", date: "2026-10-01", label: "Видео" },
  old: { id: "OLDVIDEO001", owner: "UC-old", date: "2026-09-30", label: "Videos" }
};

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext();
    let likeClicks = 0;
    await context.exposeBinding("recordLike", () => { likeClicks++; });
    await context.route("**/*", async route => {
      const u = new URL(route.request().url());
      const name = /^\/@(en|ru|old)\/videos$/.exec(u.pathname)?.[1];
      if (name) {
        const ch = channels[name];
        const initialData = {
          metadata: { channelMetadataRenderer: { externalId: ch.owner } },
          contents: {
            twoColumnBrowseResultsRenderer: {
              tabs: [{
                tabRenderer: {
                  selected: true,
                  content: {
                    richGridRenderer: {
                      contents: [
                        { richShelfRenderer: { contents: [{ videoRenderer: { videoId: "SHORTS00001" } }] } },
                        { richItemRenderer: { content: { lockupViewModel: { contentType: "LOCKUP_CONTENT_TYPE_VIDEO", contentId: ch.id } } } }
                      ]
                    }
                  }
                }
              }]
            }
          }
        };
        return route.fulfill({
          contentType: "text/html",
          body: `<script>var ytInitialData=${JSON.stringify(name === "ru" ? initialData : { metadata: initialData.metadata })};</script>` +
            `<ytd-browse>${ch.label}<ytd-rich-grid-renderer>` +
            `<ytd-rich-shelf-renderer><a href="/shorts/SHORTS00001">Shorts</a></ytd-rich-shelf-renderer>` +
            `${name === "ru" ? "<yt-lockup-view-model>" : "<ytd-rich-grid-media>"}` +
            `<a href="/watch?v=${ch.id}">Today</a>` +
            `${name === "ru" ? "</yt-lockup-view-model>" : "</ytd-rich-grid-media>"}` +
            `</ytd-rich-grid-renderer></ytd-browse>`
        });
      }
      if (u.pathname === "/watch") {
        const id = u.searchParams.get("v");
        const ch = Object.values(channels).find(c => c.id === id);
        if (!ch) return route.fulfill({ status: 404, body: "Unexpected Shorts or catalog ID" });
        const player = {
          videoDetails: { videoId: id, channelId: ch.owner, durationSeconds: 2 },
          playabilityStatus: { status: "OK" },
          microformat: { playerMicroformatRenderer: { publishDate: ch.date } }
        };
        return route.fulfill({
          contentType: "text/html",
          body: `<script>var ytInitialPlayerResponse=${JSON.stringify(player)};</script>` +
            `<video id="v"></video>` +
            `<button id="like" aria-label="Like this video" aria-pressed="false">Like</button>` +
            `<script>
              const v=document.getElementById('v');
              const like=document.getElementById('like');
              like.onclick=()=>{
                recordLike();
                like.setAttribute('aria-label','Unlike this video');
                like.setAttribute('aria-pressed','true');
              };
              Object.defineProperty(v,'duration',{value:2,configurable:true});
              v.play=()=>{v.paused=false;return Promise.resolve();};
              let t=0; const iv=setInterval(()=>{
                t+=0.4; v.currentTime=Math.min(t,2); v.paused=false;
                if(t>=2){v.ended=true;clearInterval(iv);}
              },120);
            </script>`
        });
      }
      return route.fulfill({ status: 404, body: "" });
    });

    const target = name => ({
      channelUrl: "https://www.youtube.com/@" + name + "/videos",
      ownerProfileId: name,
      ownerName: name,
      catalogVideos: [{ videoId: "STALE000001" }]
    });

    for (const viewer of ["en", "ru"]) {
      const page = await context.newPage();
      const opened = [];
      const order = viewer === "en" ? ["en", "ru"] : ["ru", "en"];
      const result = await openChannelsWatch(
        page, order.map(target), { profileId: viewer, todayDate: "2026-10-01" },
        (stage, text, extra) => { if (stage === "mesh") opened.push(extra); },
        playback
      );
      assert.equal(result.opened, 2, "Both own and other channel must finish watch");
      assert.deepEqual(opened.map(x => x.meshOwner), order);
      assert(opened.every(x => !x.url.includes("STALE000001")));
      await page.close();
    }
    assert.equal(likeClicks, 4, "Like must happen after each completed watch");

    const page = await context.newPage();
    const opened = [];
    const errors = [];
    await assert.rejects(
      openChannelsWatch(page, ["old", "ru"].map(target), { todayDate: "2026-10-01" },
        (stage, text, extra) => {
          if (stage === "mesh") opened.push(extra.meshOwner);
          if (stage === "channel_error") errors.push(text);
        }, playback),
      /Досмотрено 1\/2/
    );
    assert.deepEqual(opened, ["ru"], "Failure must not skip remaining channels");
    assert.equal(errors.length, 1);
    console.log("OK: watch grid browser (full watch + like during playback, RU/EN, partial failure)");
  } finally {
    await browser.close();
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
