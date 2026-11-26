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
    // Current YouTube markup: enabled yt-button-shape after a disabled structural match.
    await page.setContent(`<div id="actions"><div id="top-level-buttons-computed">
      <like-button-view-model><button aria-disabled="true" aria-label='поставить отметку "Нравится"' aria-pressed="false">Disabled</button></like-button-view-model>
      <yt-button-shape><button id="available-like" aria-label='поставить отметку "Нравится"' aria-pressed="false">Like</button></yt-button-shape>
      <button aria-label='Поставить отметку "Не нравится"'>Dislike</button>
    </div></div>`);
    await page.locator("#available-like").evaluate(btn => {
      btn.onclick = () => { btn.setAttribute("aria-pressed", "true"); };
    });
    await playback.likeCurrentVideo(page);
    assert.equal(await page.locator("#available-like").getAttribute("aria-pressed"), "true");
    assert.equal(await page.locator("[aria-disabled]").getAttribute("aria-pressed"), "false");

    await page.setContent(`<div id="actions"><like-button-view-model>
      <button aria-disabled="true" aria-label='поставить отметку "Нравится"' aria-pressed="false">Like</button>
    </like-button-view-model></div>`);
    await assert.rejects(playback.likeCurrentVideo(page), /YouTube отключил кнопку/);
    const softMessages = [];
    await playback.likeCurrentVideo(page, (_, text) => softMessages.push(text), { soft: true });
    assert(softMessages.some(text => /YouTube отключил кнопку/.test(text)));

    // Signed-out profiles must fail before playback/like, not after watching 80%.
    await page.setContent(`<script>window.ytcfg={get:key=>key==='LOGGED_IN'?false:undefined};</script><video></video><button aria-label="Like this video">Like</button>`);
    await assert.rejects(playback.waitForVideoEnd(page, {}), /нет активного входа в YouTube/);
    await assert.rejects(playback.likeCurrentVideo(page), /нет активного входа в YouTube/);
    await page.evaluate(()=>{ delete window.ytcfg; });
    await page.setContent(`<div>Войдите в аккаунт, чтобы поставить отметку.</div><button aria-label="Like this video">Like</button>`);
    await assert.rejects(playback.likeCurrentVideo(page), /нет активного входа в YouTube/);

    // A non-finite player duration must not replace the known finite duration.
    await page.setContent(`<video></video><button aria-label="Unlike this video" aria-pressed="true">Liked</button>`);
    await page.evaluate(() => {
      const video = document.querySelector("video");
      let reads = 0;
      Object.defineProperties(video, {
        duration: { get: () => ++reads <= 2 ? 2 : Infinity },
        currentTime: { get: () => reads >= 2 ? 2 : 0.1 },
        paused: { get: () => false },
        readyState: { get: () => 4 },
        ended: { get: () => false }
      });
    });
    const playbackMessages = [];
    await playback.waitForVideoEnd(page, {}, (_, text) => playbackMessages.push(text));
    assert(playbackMessages.includes("Видео закончилось."));
    console.log("OK: watch grid browser (full watch, current like markup, disabled likes, non-finite duration, RU/EN, partial failure)");
  } finally {
    await browser.close();
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
