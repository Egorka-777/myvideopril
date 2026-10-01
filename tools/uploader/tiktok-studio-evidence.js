"use strict";

const { parsePostResponse, evaluatePostAcceptance } = require("./worker-tiktok-http.js");

function postEvidence(status, body, requestBody, item) {
  const parsed = parsePostResponse({ status, json: body });
  const verdict = evaluatePostAcceptance(parsed);
  if (!verdict.ok) throw new Error("TikTok отклонил post: " + verdict.reason);
  const feature = requestBody && requestBody.feature_common_info_list;
  const posts = requestBody && requestBody.single_post_req_list;
  // This adapter accepts the schema already used by this repository, only when
  // actually observed on a native Studio request. Unknown schemas fail closed.
  if (!Array.isArray(posts) || posts.length !== 1 || !Array.isArray(feature) || feature.length !== 1)
    throw new Error("Неизвестная структура native post. Нужна сверка Studio; повтор запрещён.");
  const caption = String(posts[0].single_post_feature_info?.text ?? posts[0].caption ?? posts[0].text ?? "").replace(/\r\n/g, "\n").trim();
  if (caption !== String(item.caption).replace(/\r\n/g, "\n").trim())
    throw new Error("TikTok отправил другую подпись. Нужна сверка Studio; повтор запрещён.");
  const sentTime = Number(feature[0].schedule_time || 0);
  if (item.publishMode === "scheduled" && sentTime !== Number(item.scheduledUnixSeconds))
    throw new Error("Время native post не совпадает с заданием. Нужна сверка Studio; повтор запрещён.");
  const list = parsed.body.single_post_resp_list || parsed.body.single_post_resp || [];
  const post = list.find(r => r && (r.item_id || r.aweme_id || r.publish_id));
  const id = post && String(post.item_id || post.aweme_id || post.publish_id);
  return { id: id || "", scheduledUnixSeconds: sentTime, accepted: true };
}

async function setNativeSchedule(page, item) {
  if (item.publishMode !== "scheduled") return;
  const at = Number(item.scheduledUnixSeconds);
  if (!Number.isInteger(at) || at < Math.floor(Date.now() / 1000) + 960 || at > Math.floor(Date.now() / 1000) + 864000)
    throw new Error("Слот устарел либо вне окна 16 минут–10 дней. Post не отправлялся.");
  if (at % 60 !== 0) throw new Error("Этот адаптер Studio поддерживает отложку с точностью до минуты.");
  const label = /^(Schedule|Schedule video|Schedule post|Запланировать|Запланировать видео|Отложенная публикация)$/i;
  let found = false;
  for (const role of ["radio", "switch", "checkbox"]) {
    const control = page.getByRole(role, { name: label }).first();
    if (!(await control.isVisible().catch(() => false))) continue;
    if (role === "switch") { if (await control.getAttribute("aria-checked") !== "true") await control.click(); }
    else await control.check();
    found = true; break;
  }
  if (!found) throw new Error("Studio не показывает доступный переключатель отложки. Post не отправлялся.");
  // Date fields must be exposed by the real page. No coordinates or injected UI.
  const values = await page.evaluate(at => {
    const d = new Date(at * 1000), pad = n => String(n).padStart(2, "0");
    return { date: `${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(d.getDate())}`, time: `${pad(d.getHours())}:${pad(d.getMinutes())}`,
      timezone: Intl.DateTimeFormat().resolvedOptions().timeZone };
  }, at);
  const date = page.locator('input[type="date"]:visible').first();
  const time = page.locator('input[type="time"]:visible').first();
  if (!(await date.isVisible().catch(() => false)) || !(await time.isVisible().catch(() => false)))
    throw new Error("Studio использует другой календарь. Сохранена диагностика; post не отправлялся.");
  await date.fill(values.date); await date.press("Tab");
  await time.fill(values.time); await time.press("Tab");
  if (await date.inputValue() !== values.date || (await time.inputValue()).slice(0,5) !== values.time)
    throw new Error("Studio не подтвердил дату/время. Post не отправлялся.");
  return values;
}

async function confirmStudioRow(page, evidence, item) {
  if (!evidence.id) throw new Error("Post принят без ID публикации. Наличие ролика в Studio не подтверждено; повтор запрещён.");
  const deadline = Date.now() + 120000;
  while (Date.now() < deadline) {
    // ID is the native post ID, never the upload video ID or project draft ID.
    const match = await page.evaluate(({id, caption, scheduled}) => {
      const rows = [...document.querySelectorAll('tr, [role="row"], [data-e2e*="post"], [data-e2e*="video"]')];
      return rows.some(row => {
        const rect = row.getBoundingClientRect();
        if (!rect.width || !rect.height) return false;
        const identity = row.getAttribute("data-id") || row.getAttribute("data-post-id") || "";
        const links = [...row.querySelectorAll("a[href]")].map(a => a.getAttribute("href"));
        const exactId = identity === id || links.some(href => href.split(/[/?&#=]/).includes(id));
        const text = (row.innerText || "").replace(/\s+/g, " ");
        const fullCaption = (row.getAttribute("title") || "") + " " + text;
        return exactId && fullCaption.includes(caption.replace(/\s+/g, " ")) &&
          (!scheduled || /scheduled|запланирован|отложен/i.test(text));
      });
    }, { id:evidence.id, caption:item.caption, scheduled:item.publishMode==="scheduled" });
    if (match) return;
    await page.waitForTimeout(1000);
  }
  throw new Error("Post принят, но точная запись в Studio не найдена за 2 минуты. Повтор запрещён.");
}

module.exports = { postEvidence, setNativeSchedule, confirmStudioRow };
