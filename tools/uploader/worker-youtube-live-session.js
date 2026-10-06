"use strict";

// Uses Studio's authenticated browser and its native HTTPS requests. There are no guessed live API endpoints.
// The control adapter below is deliberately strict: an unfamiliar/ambiguous Studio screen is an error.
const fs = require("fs"), path = require("path"), readline = require("readline");
const dolphin = require("./dolphin-session");
const existing = require("./worker-http");
const ORIGIN = "https://studio.youtube.com";
const VIDEO = /^[A-Za-z0-9_-]{11}$/;
const CHANNEL = /^UC[A-Za-z0-9_-]+$/;
const labels = {
  schedule: /^(Schedule stream|Schedule live stream|Запланировать трансляцию|Запланировать эфир)$/i,
  createNew: /^(Create new|Создать|Создать трансляцию)$/i,
  next: /^(Next|Далее)$/i,
  save: /^(Save|Сохранить)$/i,
  edit: /^(Edit|Изменить|Редактировать)$/i,
  go: /^(Go live|Начать трансляцию|Начать эфир)$/i,
  end: /^(End stream|Завершить трансляцию|Завершить эфир)$/i,
  finished: /stream (?:has )?(?:ended|finished)|трансляция завершена|эфир заверш[её]н/i,
  kidsYes: /^Yes, it.s made for kids|^Да, это видео для детей/i,
  kidsNo: /^No, it.s not made for kids|^Нет, это видео не для детей/i,
  software: /^(Streaming software|Encoder|Видеокодер|Программа для трансляций)$/i,
  public: /^(Public|Открытый доступ)$/i,
  unlisted: /^(Unlisted|Доступ по ссылке)$/i,
  private: /^(Private|Ограниченный доступ)$/i
};

function validateJob(job) {
  if (!job || !["verify", "create", "ingest", "configure", "status", "stream-status", "complete"].includes(job.command)) throw new Error("Неизвестная операция Studio.");
  if (!/^[a-f0-9]{32}$/.test(job.operationId || "") || !/^[A-Za-z0-9_-]+$/.test(job.profileId || "")) throw new Error("Некорректные ID операции/профиля.");
  if (!Number.isInteger(job.localPort) || job.localPort < 1 || job.localPort > 65535 || !path.isAbsolute(job.stateDirectory || "")) throw new Error("Некорректные настройки Dolphin.");
  if (job.expectedChannelId && !CHANNEL.test(job.expectedChannelId)) throw new Error("Некорректный ID канала.");
  if (job.command !== "verify" && !CHANNEL.test(job.expectedChannelId || "")) throw new Error("Сначала подтвердите канал в сессии Dolphin.");
  if (job.broadcastId && !VIDEO.test(job.broadcastId)) throw new Error("Некорректный ID эфира.");
  if (["create", "configure"].includes(job.command)) {
    const o = job.options;
    if (!o || !o.title || o.title.length > 100 || /[<>]/.test(o.title) || !labels[o.privacy] || typeof o.madeForKids !== "boolean"
      || !Array.isArray(o.tags) || o.tags.some(t => typeof t !== "string" || /[<>]/.test(t))) throw new Error("Некорректные параметры эфира.");
    if (o.thumbnail && !fs.existsSync(o.thumbnail)) throw new Error("Превью не найдено.");
  }
  return job;
}
function sanitize(error) {
  return existing.redactDiagnostic(String(error && error.message || error)).replace(/rtmps?:\/\/\S+/gi, "[stream]")
    .replace(/(?:stream.?key|ключ трансляции)\s*[:=]\s*[^\s,;]+/gi, "streamKey=[скрыто]").split("\n")[0].slice(0, 450);
}
function rtmpsAddress(value) {
  let u; try { u = new URL(value); } catch (_) { throw new Error("Studio не вернул адрес видеосервера."); }
  if (u.username || u.password || u.search || u.hash || u.pathname !== "/live2") throw new Error("Некорректный адрес видеосервера.");
  if (u.protocol === "rtmp:" && ["a.rtmp.youtube.com", "b.rtmp.youtube.com"].includes(u.hostname))
    return `rtmps://${u.hostname.replace(".rtmp.", ".rtmps.")}/live2`;
  if (u.protocol !== "rtmps:" || !["a.rtmps.youtube.com", "b.rtmps.youtube.com"].includes(u.hostname)) throw new Error("Разрешены только RTMPS-серверы YouTube.");
  return u.href.replace(/\/$/, "");
}
class OperationStore {
  constructor(job) { this.job = job; this.file = path.join(job.stateDirectory, job.operationId + ".json"); }
  read() {
    if (!fs.existsSync(this.file)) return null;
    const saved = JSON.parse(fs.readFileSync(this.file, "utf8"));
    if (saved.profileId !== this.job.profileId || saved.channelId !== this.job.expectedChannelId || saved.operationId !== this.job.operationId)
      throw new Error("Журнал эфира принадлежит другому каналу/профилю.");
    return saved;
  }
  write(saved) {
    fs.mkdirSync(this.job.stateDirectory, { recursive: true });
    const temp = this.file + "." + process.pid + ".tmp";
    fs.writeFileSync(temp, JSON.stringify(saved), { mode: 0o600 }); fs.renameSync(temp, this.file);
  }
  reserve() {
    const directory = path.join(path.dirname(this.job.stateDirectory), "dolphin-sessions");
    const lease = dolphin.liveLeasePath(this.job, directory);
    fs.mkdirSync(path.dirname(lease), { recursive: true });
    try { fs.writeFileSync(lease, JSON.stringify({ operationId: this.job.operationId, profileId: this.job.profileId, channelId: this.job.expectedChannelId }), { flag: "wx", mode: 0o600 }); }
    catch (error) { if (error.code === "EEXIST") throw new Error("Dolphin-профиль уже зарезервирован другим эфиром. Сначала проверьте завершение."); throw error; }
  }
  release() {
    const directory = path.join(path.dirname(this.job.stateDirectory), "dolphin-sessions");
    dolphin.assertLiveLease({ ...this.job, liveOperationId: this.job.operationId }, directory);
    const file = dolphin.liveLeasePath(this.job, directory); if (fs.existsSync(file)) fs.unlinkSync(file);
  }
}
function operationTitle(id) { return "VideoBatch live " + id; }
class SessionEngine {
  constructor(job, ui, store = new OperationStore(job)) { this.job = validateJob(job); this.ui = ui; this.store = store; }
  async execute() {
    const j = this.job, identity = await this.ui.verify();
    if (!CHANNEL.test(identity.channelId || "") || (j.expectedChannelId && j.expectedChannelId !== identity.channelId)) throw new Error("В Dolphin открыт другой канал. Изменения не выполнялись.");
    if (j.command === "verify") return identity;
    let saved = this.store.read();
    if (j.command === "create") {
      if (saved) throw new Error("Создание уже запрашивалось. Сначала проверьте/завершите прежний эфир.");
      this.store.reserve();
      saved = { operationId: j.operationId, profileId: j.profileId, channelId: identity.channelId, createAttempted: true, broadcastId: "", startAttempted: false, configured: false, completed: false };
      this.store.write(saved); // BEFORE any mutating browser action; a lost response is never retried.
      const id = await this.ui.create(operationTitle(j.operationId), j.options);
      if (!VIDEO.test(id || "")) throw new Error("Не подтверждён ID созданного эфира.");
      saved.broadcastId = id; this.store.write(saved); return { broadcastId: id };
    }
    if (!saved) throw new Error("Не найден журнал собственного эфира. Проверьте Studio вручную.");
    if (!saved.broadcastId && j.command === "complete") {
      saved.broadcastId = await this.ui.discover(operationTitle(j.operationId));
      if (!VIDEO.test(saved.broadcastId || "")) throw new Error("Не удалось однозначно найти созданный эфир. Журнал сохранён.");
      this.store.write(saved);
    }
    if (!VIDEO.test(saved.broadcastId || "") || (j.broadcastId && saved.broadcastId !== j.broadcastId)) throw new Error("ID эфира не совпадает с журналом. Чужой эфир не изменён.");
    if (saved.completed) {
      if (j.command === "complete") { this.store.release(); return { completed: true }; }
      return { broadcastState: "complete", streamState: "inactive" };
    }
    await this.ui.open(saved.broadcastId);
    if (j.command === "ingest") {
      if (saved.keyAttempted) throw new Error("Создание ключа уже запрашивалось. Проверьте и завершите эфир.");
      saved.keyAttempted = true; this.store.write(saved);
      const ingest = await this.ui.ingest(operationTitle(j.operationId));
      if (!/^[A-Za-z0-9_-]{8,200}$/.test(ingest.key || "")) throw new Error("Не удалось получить ключ трансляции.");
      return { broadcastId: saved.broadcastId, address: rtmpsAddress(ingest.address), key: ingest.key };
    }
    if (j.command === "configure") {
      await this.ui.configure(j.options);
      saved.configured = true; this.store.write(saved); return { configured: true };
    }
    if (j.command === "complete") {
      // Never delete a possibly live/archived broadcast after an uncertain start.
      await this.ui.complete(saved.startAttempted);
      saved.completed = true; this.store.write(saved); this.store.release(); return { completed: true };
    }
    let state = await this.ui.state();
    if (j.command === "status" && state === "ready" && saved.configured && !saved.startAttempted && await this.ui.canStart()) {
      saved.startAttempted = true; this.store.write(saved);
      await this.ui.start(); state = await this.ui.state();
    }
    return { broadcastState: state === "live" ? "live" : state === "complete" ? "complete" : "ready", streamState: state === "live" ? "active" : "inactive" };
  }
}

async function uniqueVisible(locator, required = true) {
  const visible = [];
  for (let i = 0, n = await locator.count(); i < n; i++) if (await locator.nth(i).isVisible()) visible.push(locator.nth(i));
  if (visible.length > 1) throw new Error("Studio показал несколько одинаковых элементов. Операция остановлена.");
  if (!visible.length && required) throw new Error("Studio изменил интерфейс: нужный элемент не найден.");
  return visible[0] || null;
}
async function waitUntil(fn, timeout = 15000) {
  const deadline = Date.now() + timeout;
  do { const value = await fn(); if (value) return value; await new Promise(r => setTimeout(r, 250)); } while (Date.now() < deadline);
  throw new Error("Studio не подтвердил изменение состояния.");
}
class NativeStudio {
  constructor(page, channelId = "") { this.page = page; this.channelId = channelId; this.broadcastId = ""; }
  async button(name, scope = this.page, required = true) { return uniqueVisible(scope.getByRole("button", { name }), required); }
  async click(name, scope = this.page) { const b = await waitUntil(() => this.button(name, scope, false)); await b.click({ timeout: 10000 }); }
  async dialog() {
    return waitUntil(async () => await uniqueVisible(this.page.getByRole("dialog"), false)
      || await uniqueVisible(this.page.locator('ytcp-dialog[opened], tp-yt-paper-dialog[opened]').filter({ hasNot: this.page.getByRole("dialog") }), false));
  }
  async verify() {
    await this.page.goto(ORIGIN, { waitUntil: "domcontentloaded", timeout: 90000 });
    await existing.waitForStudioChannel(this.page, 90000);
    const data = existing.parseStudioBootstrap(await this.page.content(), this.page.url());
    this.channelId = data.channelId;
    // Name is informational; channel ID is the binding authority.
    const name = await this.page.locator("#channel-name").first().innerText({ timeout: 1000 }).catch(() => "");
    return { channelId: data.channelId, channelName: name };
  }
  async open(id) {
    if (!VIDEO.test(id)) throw new Error("Некорректный эфир.");
    this.broadcastId = id;
    await this.page.goto(`${ORIGIN}/video/${id}/livestreaming`, { waitUntil: "domcontentloaded", timeout: 60000 });
    await this.assertTarget();
    await waitUntil(async () => await this.button(labels.go, this.page, false) || await this.button(labels.end, this.page, false)
      || labels.finished.test(await this.page.locator("body").innerText()), 30000);
  }
  async assertTarget() {
    const url = new URL(this.page.url());
    if (url.origin !== ORIGIN || !url.pathname.startsWith(`/video/${this.broadcastId}/`)) throw new Error("Studio открыл другую страницу. Эфир не изменён.");
    const actual = await this.page.evaluate(() => {
      const c = window.ytcfg && window.ytcfg.get && window.ytcfg.get("DELEGATED_SESSION_ID");
      // A delegated channel ID is not always exposed; the initial Studio bootstrap was checked before navigation.
      return typeof c === "string" && /^UC/.test(c) ? c : "";
    });
    if (actual && actual !== this.channelId) throw new Error("Канал сессии изменился. Эфир не изменён.");
  }
  async manage() {
    await this.page.goto(`${ORIGIN}/channel/${this.channelId}/livestreaming/manage`, { waitUntil: "domcontentloaded", timeout: 60000 });
    if (!this.page.url().includes(`/channel/${this.channelId}/`)) throw new Error("Studio не открыл управление нужным каналом.");
  }
  async discover(title) {
    await this.manage();
    const row = await waitUntil(() => uniqueVisible(this.page.locator("ytcp-video-row, tr, [role='row']").filter({ has: this.page.getByText(title, { exact: true }) }), false));
    const links = await row.locator("a[href]").evaluateAll(nodes => nodes.map(n => n.href));
    const ids = [...new Set(links.map(s => s.match(/\/video\/([A-Za-z0-9_-]{11})(?:\/|$)/)?.[1]).filter(Boolean))];
    if (ids.length !== 1) throw new Error("Studio не подтвердил единственный ID созданного эфира.");
    return ids[0];
  }
  async field(kind, scope = this.page) {
    const selectors = kind === "title" ? 'ytcp-social-suggestions-textbox#title-textarea #textbox, #title-textarea [contenteditable], input[name="title"]'
      : 'ytcp-social-suggestions-textbox#description-textarea #textbox, #description-textarea [contenteditable], textarea[name="description"]';
    return waitUntil(() => uniqueVisible(scope.locator(selectors), false));
  }
  async details(scope, title, options, extras) {
    await (await this.field("title", scope)).fill(title, { timeout: 10000 });
    await (await this.field("description", scope)).fill(options.description || "", { timeout: 10000 });
    const radio = await uniqueVisible(scope.getByRole("radio", { name: options.madeForKids ? labels.kidsYes : labels.kidsNo }), false);
    if (!radio) throw new Error("Не найдена настройка аудитории эфира.");
    await radio.click({ timeout: 10000 });
    if (!extras) return;
    if (options.thumbnail) {
      const before = await scope.locator('img[src*="ytimg"], #thumbnail-editor img, ytcp-thumbnails-compact-editor img').evaluateAll(nodes => nodes.map(n => n.src));
      const input = scope.locator('input[type="file"][accept*="image"], input[type="file"][accept*="png"], input[type="file"][accept*="jpeg"]');
      const count = await input.count();
      if (count !== 1) throw new Error("Не найдено однозначное поле загрузки превью.");
      await input.setInputFiles(options.thumbnail);
      this.thumbnailEvidence = await waitUntil(async () => {
        const after = await scope.locator('img[src*="ytimg"], #thumbnail-editor img, ytcp-thumbnails-compact-editor img').evaluateAll(nodes => nodes.map(n => n.src));
        return after.find(src => /^https:\/\//.test(src) && !before.includes(src));
      }, 30000);
    }
    let tag = await uniqueVisible(scope.locator('ytcp-free-text-chip-bar#tags-container input, #tags-container input, input[name="tags"]'), false);
    if (!tag && options.tags.length) {
      const more = await this.button(/^(Show more|Показать больше|Развернуть)$/i, scope, false);
      if (more) await more.click();
      tag = await uniqueVisible(scope.locator('ytcp-free-text-chip-bar#tags-container input, #tags-container input, input[name="tags"]'), false);
    }
    if (tag) {
      // Clear any defaults copied by Studio, using actual chip controls, not deleting unrelated fields.
      const chips = scope.locator("#tags-container ytcp-chip, #tags-container [role='listitem']");
      for (let n = await chips.count(); n > 0; n--) {
        const remove = chips.nth(n - 1).locator('[aria-label="Remove"], [aria-label="Удалить"], #delete-icon');
        if (await remove.count() !== 1) throw new Error("Не удалось очистить прежние теги.");
        await remove.click();
      }
      await tag.fill("");
      for (const value of options.tags) { await tag.fill(value); await tag.press("Enter"); }
    } else if (options.tags.length) throw new Error("Studio не показал поле тегов.");
  }
  async create(title, options) {
    await this.manage();
    const liveRows = this.page.locator("ytcp-video-row, [role='row']").filter({ hasText: /\bLIVE\b|Трансляция ид[её]т|В эфире/ });
    if (await liveRows.count()) throw new Error("На канале уже есть эфир. Существующая передача не изменена.");
    await this.click(labels.schedule);
    const newButton = await this.button(labels.createNew, this.page, false);
    if (newButton) await newButton.click();
    let dialog = await this.dialog();
    const software = await uniqueVisible(dialog.getByRole("radio", { name: labels.software }), false);
    if (software) await software.click();
    else if (!labels.software.test((await dialog.getByText(labels.software, { exact: true }).innerText()).trim()))
      throw new Error("Studio не подтвердил тип трансляции «Видеокодер».");
    await this.details(dialog, title, { ...options, description: "[VideoBatch:" + title.split(" ").pop() + "]", thumbnail: "", tags: [] }, false);
    // Native scheduling wizard. Create as PRIVATE until final metadata has been verified.
    for (let i = 0; i < 4; i++) {
      const visibility = await uniqueVisible(dialog.getByRole("radio", { name: labels.private }), false);
      if (visibility) { await visibility.click(); break; }
      const next = await this.button(labels.next, dialog, false);
      if (!next) throw new Error("Studio не показал шаг доступа к новому эфиру.");
      await next.click(); await this.page.waitForTimeout(500);
    }
    if (!await uniqueVisible(dialog.getByRole("radio", { name: labels.private }), false)) throw new Error("Не подтверждён приватный доступ к создаваемому эфиру.");
    await this.click(labels.schedule, dialog);
    await waitUntil(async () => !await dialog.isVisible());
    const openRoom = await this.button(/^(View in Live Control Room|Open Live Control Room|Открыть панель управления трансляцией|Перейти в панель управления)$/i, this.page, false);
    if (openRoom) await openRoom.click();
    // Find ONLY the title reserved for this operation, never the first/current stream.
    return await this.discover(title);
  }
  async copySetting(pattern) {
    // Find the smallest visible setting container with exactly one Copy control.
    const label = await uniqueVisible(this.page.getByText(pattern, { exact: true }));
    let container = label;
    for (let i = 0; i < 6; i++) {
      container = container.locator("..");
      const copy = await this.button(/^(Copy|Копировать|Скопировать)$/i, container, false);
      if (!copy) continue;
      // Capture a native copy action in this task tab. Do not put the secret in the system clipboard.
      await this.page.evaluate(() => {
        window.__vbCopied = ""; window.__vbOriginalCopy = navigator.clipboard.writeText;
        navigator.clipboard.writeText = async text => { window.__vbCopied = String(text); };
        window.__vbCopyListener = e => { const text = e.clipboardData && e.clipboardData.getData("text/plain"); if (text) { window.__vbCopied = text; e.preventDefault(); } };
        document.addEventListener("copy", window.__vbCopyListener);
      });
      try {
        await copy.click();
        return await waitUntil(() => this.page.evaluate(() => window.__vbCopied || ""), 5000);
      } finally {
        await this.page.evaluate(() => {
          navigator.clipboard.writeText = window.__vbOriginalCopy; document.removeEventListener("copy", window.__vbCopyListener);
          delete window.__vbCopied; delete window.__vbOriginalCopy; delete window.__vbCopyListener;
        }).catch(() => {});
      }
    }
    throw new Error("Не найдено управление копированием параметра трансляции.");
  }
  async ingest(name) {
    await this.assertTarget();
    // A separate key avoids sharing a previous stream's ingest destination.
    const drop = await uniqueVisible(this.page.locator('#stream-key-select ytcp-dropdown-trigger, #stream-key-select [role="combobox"], [aria-label="Stream key"], [aria-label="Ключ трансляции"]'));
    await drop.click();
    await this.click(/^(Create new stream key|Создать ключ трансляции|Создать новый ключ трансляции)$/i);
    const d = await this.dialog();
    const input = await uniqueVisible(d.locator('input[aria-label="Name"], input[aria-label="Название"], #name input, input[name="name"]'));
    await input.fill(name); await this.click(/^(Create|Создать)$/i, d);
    await waitUntil(async () => (await drop.innerText()).includes(name));
    const key = await this.copySetting(/^(Stream key(?: \(paste in encoder\))?|Ключ трансляции(?: \(вставьте в видеокодер\))?)$/i);
    const address = await this.copySetting(/^(Stream URL|URL трансляции|URL сервера)$/i);
    return { key: key.trim(), address: address.trim() };
  }
  async configure(options) {
    await this.assertTarget(); await this.click(labels.edit);
    const d = await this.dialog();
    await this.details(d, options.title, options, true);
    const privacy = await uniqueVisible(d.getByRole("radio", { name: labels[options.privacy] }), false);
    if (!privacy) {
      const drop = await uniqueVisible(d.locator('#privacy-select ytcp-dropdown-trigger, [aria-label="Visibility"], [aria-label="Доступ"]'));
      await drop.click(); await this.click(labels[options.privacy], d);
    } else await privacy.click();
    await this.click(labels.save, d);
    await waitUntil(async () => !await d.isVisible());
    // Read back saved fields from a fresh edit dialog before allowing an encoder to start.
    await this.click(labels.edit); const verify = await this.dialog();
    const text = async control => control.evaluate(n => n.isContentEditable ? n.innerText : n.value);
    if (await text(await this.field("title", verify)) !== options.title || await text(await this.field("description", verify)) !== (options.description || ""))
      throw new Error("Studio не сохранил название/описание эфира.");
    const audience = await uniqueVisible(verify.getByRole("radio", { name: options.madeForKids ? labels.kidsYes : labels.kidsNo }));
    if (await audience.getAttribute("aria-checked") !== "true" && !await audience.isChecked().catch(() => false)) throw new Error("Studio не подтвердил аудиторию.");
    const savedTags = await verify.locator("#tags-container ytcp-chip, #tags-container [role='listitem']").evaluateAll(nodes => nodes.map(n =>
      (n.getAttribute("text") || n.querySelector("#text, #label, .chip-text")?.textContent || Array.from(n.childNodes).filter(c => c.nodeType === Node.TEXT_NODE).map(c => c.textContent).join("")).trim()));
    if (JSON.stringify(savedTags.slice().sort()) !== JSON.stringify(options.tags.slice().sort())) throw new Error("Studio не подтвердил сохранение точного списка тегов.");
    if (options.thumbnail && !(await verify.locator('img[src*="ytimg"], #thumbnail-editor img, ytcp-thumbnails-compact-editor img').evaluateAll(nodes => nodes.map(n => n.src))).includes(this.thumbnailEvidence))
      throw new Error("Studio не подтвердил сохранение выбранного превью.");
    const checkedPrivacy = await uniqueVisible(verify.getByRole("radio", { name: labels[options.privacy] }), false);
    if (checkedPrivacy && await checkedPrivacy.getAttribute("aria-checked") !== "true" && !await checkedPrivacy.isChecked().catch(() => false)) throw new Error("Studio не подтвердил доступ.");
    if (!checkedPrivacy && !labels[options.privacy].test((await verify.locator('#privacy-select, [aria-label="Visibility"], [aria-label="Доступ"]').innerText()).trim()))
      throw new Error("Studio не подтвердил доступ.");
    await this.click(/^(Cancel|Отмена|Close|Закрыть)$/i, verify);
  }
  async state() {
    await this.assertTarget();
    const text = await this.page.locator("body").innerText();
    const blocker = existing.studioPageState(this.page.url(), text).blocker;
    if (blocker) throw new Error(blocker);
    // Studio only offers End stream for a live broadcast. The mere existence of an FFmpeg process is insufficient.
    const end = await this.button(labels.end, this.page, false);
    if (end && await end.isEnabled()) return "live";
    if (labels.finished.test(text)) return "complete";
    const go = await this.button(labels.go, this.page, false);
    if (go) return "ready";
    throw new Error("Studio не подтвердил состояние эфира.");
  }
  async canStart() { const b = await this.button(labels.go, this.page, false); return !!b && await b.isEnabled(); }
  async start() {
    await this.assertTarget(); await this.click(labels.go);
    // Some accounts show a confirmation dialog. Only act inside that dialog.
    const d = await uniqueVisible(this.page.locator('[role="dialog"], ytcp-dialog[opened]'), false);
    if (d && await this.button(labels.go, d, false)) { await this.click(labels.go, d); await waitUntil(async () => !await d.isVisible()); }
    await waitUntil(async () => await this.state() === "live", 20000);
  }
  async complete(startAttempted) {
    let state = await this.state();
    if (state === "complete") return;
    if (state === "live") {
      await this.click(labels.end);
      const dialog = await this.dialog();
      await this.click(labels.end, dialog);
      await waitUntil(async () => !await dialog.isVisible());
      await waitUntil(async () => await this.state() === "complete", 30000); return;
    }
    if (startAttempted) throw new Error("Запуск запрашивался, но завершение не подтверждено. Проверьте Studio вручную.");
    // Cancel ONLY the known upcoming video, via its own edit page. Archive/live video deletion is forbidden.
    await this.page.goto(`${ORIGIN}/video/${this.broadcastId}/edit`, { waitUntil: "domcontentloaded", timeout: 60000 });
    await this.assertTarget();
    await this.click(/^(Options|More options|Параметры|Дополнительные действия)$/i);
    const item = await uniqueVisible(this.page.getByText(/^(Delete forever|Удалить навсегда)$/i, { exact: true })); await item.click();
    const dialog = await this.dialog();
    const box = await uniqueVisible(dialog.getByRole("checkbox")); await box.check();
    await this.click(/^(Delete forever|Удалить навсегда)$/i, dialog);
    await waitUntil(async () => /video (?:successfully )?deleted|видео (?:успешно )?удалено/i.test(await this.page.locator("body").innerText()), 30000);
    await waitUntil(async () => !this.page.url().includes(`/video/${this.broadcastId}/`));
    // A navigation alone is not deletion confirmation: prove the exact video is absent from Manage.
    await this.manage();
    await waitUntil(async () => await this.page.locator('ytcp-video-row, [role="row"]').count() > 0 || await this.page.getByText(/No upcoming streams|Нет запланированных трансляций/i).count() > 0);
    if (await this.page.locator(`a[href*="/video/${this.broadcastId}/"]`).count()) throw new Error("Удаление не стартовавшего эфира не подтверждено.");
  }
}

async function main() {
  const lines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
  let input = ""; for await (const line of lines) { input = line; break; } lines.close();
  const job = validateJob(JSON.parse(input)); job.liveOperationId = job.operationId;
  const token = process.env.VIDEOBATCH_DOLPHIN_TOKEN || "";
  if (!token) throw new Error("Не передан API-токен Dolphin.");
  const request = (p, options = {}, timeout) => dolphin.requestLocal(job, p, options, timeout);
  const auth = await request("/v1.0/auth/login-with-token", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token }) });
  if (!auth.ok || auth.data.success === false) throw new Error("Dolphin отклонил авторизацию локального API.");
  const endpoint = await dolphin.startOrAttach(job, path.join(path.dirname(job.stateDirectory), "dolphin-sessions"), request);
  const { chromium } = require("playwright-core");
  const browser = await chromium.connectOverCDP(endpoint, { timeout: 60000 });
  let page;
  const events = [];
  try {
    const context = browser.contexts()[0]; if (!context) throw new Error("Dolphin не вернул браузерную сессию.");
    page = await context.newPage();
    page.setDefaultTimeout(12000);
    await page.evaluate(id => { window.name = "videobatch-live-" + id; }, job.operationId);
    page.on("response", response => {
      const u = new URL(response.url());
      if (u.origin !== ORIGIN) return;
      events.push({ method: response.request().method(), path: u.pathname, status: response.status() });
      if (events.length > 25) events.shift();
    });
    const ui = new NativeStudio(page);
    const data = await new SessionEngine(job, ui).execute();
    process.stdout.write(JSON.stringify({ type: "result", ok: true, data }) + "\n");
  } catch (error) {
    fs.mkdirSync(job.stateDirectory, { recursive: true });
    fs.writeFileSync(path.join(job.stateDirectory, job.operationId + ".diagnostic.json"), JSON.stringify({ command: job.command, error: sanitize(error), requests: events }), { mode: 0o600 });
    throw error;
  } finally {
    if (page) await dolphin.releaseTaskPage(page).catch(() => {});
    // connectOverCDP: close disconnects this client; never close the existing context or call Dolphin /stop.
    await browser.close().catch(() => {});
  }
}
if (require.main === module) main().catch(error => {
  process.stdout.write(JSON.stringify({ type: "result", ok: false, error: sanitize(error) }) + "\n"); process.exitCode = 1;
});
module.exports = { validateJob, sanitize, rtmpsAddress, OperationStore, operationTitle, SessionEngine, NativeStudio, uniqueVisible, labels };
