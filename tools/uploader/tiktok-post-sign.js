"use strict";

/**
 * TikTok web project/post URL signing.
 * Signing scripts are MIT-licensed (TiktokAutoUploader / makiisthenes).
 */

const fs = require("fs");
const path = require("path");

const SIGN_DIR = path.join(__dirname, "tiktok-sign");
const SIGN_SCRIPTS = ["webmssdk.js", "signer.js", "xbogus.js"];
const POST_URL = "https://www.tiktok.com/tiktok/web/project/post/v1/";
const SIGN_PAGE_URL = "about:blank";

function buildSignLink(msToken) {
  const params = new URLSearchParams({
    app_name: "tiktok_web",
    channel: "tiktok_web",
    device_platform: "web",
    aid: "1988",
    msToken: msToken || ""
  });
  return `${POST_URL}?${params.toString()}`;
}

let cachedSignPage = null;

function generateVerifyFp() {
  const chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".split("");
  const n = Date.now().toString(36);
  const r = [];
  r[8] = r[13] = r[18] = r[23] = "_";
  r[14] = "4";
  for (let o = 0; o < 36; o++) {
    if (!r[o]) {
      const i = 0 | (Math.random() * chars.length);
      r[o] = chars[o === 19 ? (3 & i) | 8 : i];
    }
  }
  return "verify_" + n + "_" + r.join("");
}

async function getPageUserAgent(page) {
  return page.evaluate(() => navigator.userAgent || "");
}

async function signingReady(page) {
  return page.evaluate(() =>
    typeof window.byted_acrawler?.sign === "function" &&
    typeof window.generateBogus === "function"
  );
}

async function ensurePostSignScripts(page) {
  if (await signingReady(page)) return true;
  for (const name of SIGN_SCRIPTS) {
    const filePath = path.join(SIGN_DIR, name);
    if (!fs.existsSync(filePath)) {
      throw new Error(`Signing script missing: ${name} (expected in tools/uploader/tiktok-sign/).`);
    }
    await page.addScriptTag({ path: filePath });
  }
  await page.waitForTimeout(600);
  return signingReady(page);
}

/** TikTok CSP blocks addScriptTag on www.tiktok.com — signing runs on a CSP-free blank page in the same profile. */
async function acquireSignPage(context) {
  if (!context) throw new Error("Browser context required for post signing.");
  if (cachedSignPage && !cachedSignPage.isClosed()) return cachedSignPage;
  const signPage = await context.newPage();
  await signPage.goto(SIGN_PAGE_URL, { waitUntil: "commit", timeout: 15000 });
  cachedSignPage = signPage;
  return signPage;
}

async function signPostQuery(page, msToken, context) {
  const userAgent = await getPageUserAgent(page);
  let signPage = page;
  let signSource = "tiktok_page";
  if (!(await signingReady(page))) {
    signPage = context ? await acquireSignPage(context) : page;
    signSource = "about_blank";
    const ready = await ensurePostSignScripts(signPage);
    if (!ready) throw new Error("TikTok post signing unavailable (byted_acrawler / generateBogus).");
  }

  const signLink = buildSignLink(msToken);

  const signed = await signPage.evaluate(({ signLink, userAgent }) => {
    const signature = window.byted_acrawler.sign({ url: signLink });
    const signedUrl = signLink + "&_signature=" + signature;
    const queryString = new URL(signedUrl).searchParams.toString();
    const xBogus = window.generateBogus(queryString, userAgent);
    return { signature, xBogus };
  }, { signLink, userAgent });

  if (!signed || !signed.signature || !signed.xBogus) {
    throw new Error("TikTok post signing returned empty X-Bogus or _signature.");
  }
  return Object.assign({}, signed, { signSource, signPath: "/tiktok/web/project/post/v1/" });
}

function buildSignedPostUrl(msToken, signed) {
  const params = new URLSearchParams({
    app_name: "tiktok_web",
    channel: "tiktok_web",
    device_platform: "web",
    aid: "1988",
    msToken: msToken || ""
  });
  params.set("_signature", signed.signature);
  params.set("X-Bogus", signed.xBogus);
  return `${POST_URL}?${params.toString()}`;
}

function buildPostRequestHeaders(session, userAgent) {
  return {
    Accept: "application/json, text/plain, */*",
    "Content-Type": "application/json",
    Cookie: String(session && session.cookieHeader || ""),
    Origin: "https://www.tiktok.com",
    Referer: "https://www.tiktok.com/tiktokstudio/upload?from=upload",
    "User-Agent": String(userAgent || "")
  };
}

function computeScheduleOffset(publishMode, scheduledUnixSeconds, minSec, maxSec) {
  if (publishMode !== "scheduled" || scheduledUnixSeconds <= 0) return 0;
  const now = Math.floor(Date.now() / 1000);
  const offset = scheduledUnixSeconds - now;
  return Math.max(minSec, Math.min(maxSec, offset));
}

function buildPostPayload({ creationId, videoId, caption, publishMode, scheduledUnixSeconds, minSec, maxSec }) {
  const payload = {
    post_common_info: { creation_id: creationId, enter_post_page_from: 1, post_type: 3 },
    feature_common_info_list: [{
      geofencing_regions: [],
      playlist_name: "",
      playlist_id: "",
      tcm_params: "{\"commerce_toggle_info\":{}}",
      sound_exemption: 0,
      anchors: [],
      vedit_common_info: { draft: "", video_id: videoId },
      privacy_setting_info: { visibility_type: 0, allow_duet: 1, allow_stitch: 1, allow_comment: 1 }
    }],
    single_post_req_list: [{
      batch_index: 0,
      video_id: videoId,
      is_long_video: 0,
      single_post_feature_info: { text: caption, text_extra: [], markup_text: caption, music_info: {}, poster_delay: 0 }
    }]
  };
  if (publishMode === "scheduled" && scheduledUnixSeconds > 0) {
    payload.feature_common_info_list[0].schedule_time = scheduledUnixSeconds;
  }
  return payload;
}

module.exports = {
  SIGN_DIR,
  SIGN_SCRIPTS,
  POST_URL,
  SIGN_PAGE_URL,
  buildSignLink,
  generateVerifyFp,
  acquireSignPage,
  ensurePostSignScripts,
  signPostQuery,
  buildSignedPostUrl,
  buildPostRequestHeaders,
  computeScheduleOffset,
  buildPostPayload
};
