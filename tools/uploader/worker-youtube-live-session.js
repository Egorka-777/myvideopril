"use strict";

// Uses Studio's authenticated browser and its native HTTPS requests. There are no guessed live API endpoints.
// The control adapter below is deliberately strict: an unfamiliar/ambiguous Studio screen is an error.
const fs = require("fs"), path = require("path"), readline = require("readline");
const dolphin = require("./dolphin-session");
const existing = require("./worker-http");
const ORIGIN = "https://studio.youtube.com";
const WORKER_BUILD="20261125-live-dashboard-v20";
function emitProgress(text) { process.stdout.write(JSON.stringify({type:"progress",text:sanitize(text)})+"\n"); }
const VIDEO = /^[A-Za-z0-9_-]{11}$/;
const CHANNEL = /^UC[A-Za-z0-9_-]+$/;
const labels = {
  schedule: /^(Schedule stream|Schedule live stream|Запланировать трансляцию|Запланировать эфир|Запланировать прямую трансляцию|Создать трансляцию)$/i,
  createNew: /^(Create new|Создать новую|Создать новую трансляцию|Создать трансляцию)$/i,
  finish: /^(Schedule stream|Schedule live stream|Запланировать трансляцию|Запланировать эфир|Done|Готово)$/i,
  next: /^(Next|Далее)$/i,
  save: /^(Save|Сохранить)$/i,
  edit: /^(Edit|Изменить|Редактировать)$/i,
  go: /^(Go live|Начать трансляцию|Начать эфир)$/i,
  end: /^(End stream|Завершить трансляцию|Завершить эфир)$/i,
  finished: /stream (?:has )?(?:ended|finished)|трансляция завершена|эфир заверш[её]н/i,
  kidsYes: /^Yes, it.s made for kids|^Да, это видео для детей/i,
  kidsNo: /^No, it.s not made for kids|^Нет, это видео не для детей/i,
  software: /^(Streaming software|Encoder|Видеокодер|С помощью видеокодера|Программа для трансляций)$/i,
  public: /^(Public|Открытый доступ|Для всех)$/i,
  unlisted: /^(Unlisted|Доступ по ссылке|По ссылке)$/i,
  private: /^(Private|Ограниченный доступ|Только я)$/i
};

function validateJob(job) {
  if (!job || !["verify", "prepare", "create", "ingest", "configure", "status", "stream-status", "complete"].includes(job.command)) throw new Error("Неизвестная операция Studio.");
  if (!/^[a-f0-9]{32}$/.test(job.operationId || "") || !/^[A-Za-z0-9_-]+$/.test(job.profileId || "")) throw new Error("Некорректные ID операции/профиля.");
  if (!Number.isInteger(job.localPort) || job.localPort < 1 || job.localPort > 65535 || !path.isAbsolute(job.stateDirectory || "")) throw new Error("Некорректные настройки Dolphin.");
  if (job.expectedChannelId && !CHANNEL.test(job.expectedChannelId)) throw new Error("Некорректный ID канала.");
  if (job.command !== "verify" && !CHANNEL.test(job.expectedChannelId || "")) throw new Error("Сначала подтвердите канал в сессии Dolphin.");
  if (job.broadcastId && !VIDEO.test(job.broadcastId)) throw new Error("Некорректный ID эфира.");
  if (["create", "configure"].includes(job.command)) {
    const o = job.options;
    if (!o || !o.title || o.title.length > 100 || /[<>]/.test(o.title) || !labels[o.privacy] || typeof o.madeForKids !== "boolean"
      || !Array.isArray(o.tags) || o.tags.some(t => typeof t !== "string" || /[<>]/.test(t))) throw new Error("Некорректные параметры эфира.");
    const tagLength=o.tags.reduce((n,t)=>n+t.length+(t.includes(" ")?2:0),Math.max(0,o.tags.length-1));
    if(tagLength>500 || Buffer.byteLength(o.description || "","utf8")>5000 || /[<>]/.test(o.description || "")) throw new Error("Описание или теги эфира превышают лимит YouTube.");
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
  if (u.username || u.password || u.hash || u.pathname !== "/live2") throw new Error("Некорректный адрес видеосервера.");
  if (u.protocol === "rtmp:" && ["a.rtmp.youtube.com", "b.rtmp.youtube.com"].includes(u.hostname))
    return `rtmp://${u.hostname}/live2` + (u.search || "");
  if (u.protocol === "rtmps:" && ["a.rtmps.youtube.com", "b.rtmps.youtube.com"].includes(u.hostname))
    return `rtmp://${u.hostname.replace(".rtmps.", ".rtmp.")}/live2` + (u.search || "");
  throw new Error("Разрешены только RTMP(S)-серверы YouTube.");
  return u.href.replace(/\/$/, "");
}
class OperationStore {
  constructor(job) { this.job = job; this.file = path.join(job.stateDirectory, job.operationId + ".json"); }
  read() {
    if (!fs.existsSync(this.file)) return null;
    const saved = JSON.parse(fs.readFileSync(this.file, "utf8").replace(/^\uFEFF/, ""));
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
    const j = this.job, identity = await this.ui.verify(this.job.expectedChannelUrl || "");
    if (!CHANNEL.test(identity.channelId || "") || (j.expectedChannelId && j.expectedChannelId !== identity.channelId)) throw new Error("В Dolphin открыт другой канал. Изменения не выполнялись.");
    if (j.command === "verify") return identity;
    if(j.command==="prepare"){await this.ui.prepare();return {ready:true};}
    let saved = this.store.read();
    if (j.command === "create") {
      if (saved) {
        if (saved.operationId !== j.operationId) throw new Error("Создание уже запрашивалось другой операцией. Завершите прежний эфир.");
        if (VIDEO.test(saved.broadcastId || "")) return { broadcastId: saved.broadcastId };
        await this.ui.findBroadcastRoom();
        const openId = await this.ui.activeBroadcastId();
        if (!VIDEO.test(openId || "")) throw new Error("Studio не подтвердил ID эфира на открытой панели.");
        saved.broadcastId = openId; this.store.write(saved); return { broadcastId: openId };
      }
      this.store.reserve();
      saved = { operationId: j.operationId, profileId: j.profileId, channelId: identity.channelId, createAttempted: true, broadcastId: "", startAttempted: false, configured: false, completed: false };
      this.store.write(saved); // BEFORE any mutating browser action; a lost response is never retried.
      const title = (j.options && j.options.title) || operationTitle(j.operationId);
      const id = await this.ui.create(title, { ...j.options, operationMarker: j.operationId,
        description: /\[VideoBatch:[a-f0-9]{32}\]/.test(j.options?.description || "") ? j.options.description : (j.options.description || "") });
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
      const ingest = await this.ui.ingest(operationTitle(j.operationId));
      if (!/^[A-Za-z0-9_-]{8,200}$/.test(ingest.key || "")) throw new Error("Не удалось получить ключ трансляции.");
      saved.keyAttempted = true; this.store.write(saved);
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
  if (visible.length > 1) {
    if (!required) return visible[0];
    throw new Error("Studio показал несколько одинаковых элементов. Операция остановлена.");
  }
  if (!visible.length && required) throw new Error("Studio изменил интерфейс: нужный элемент не найден.");
  return visible[0] || null;
}
async function waitUntil(fn, timeout = 15000) {
  const deadline = Date.now() + timeout;
  do { const value = await fn(); if (value) return value; await new Promise(r => setTimeout(r, 250)); } while (Date.now() < deadline);
  throw new Error("Studio не подтвердил изменение состояния.");
}
class NativeStudio {
  constructor(page, channelId = "",progress=()=>{}) { this.page = page; this.channelId = channelId; this.broadcastId = "";this.progress=text=>{this.lastStep=text;progress(text);}; }
  async button(name, scope = this.page, required = true) { return uniqueVisible(scope.getByRole("button", { name }), required); }
  async click(name, scope = this.page) { this.lastAction=name.source;
    const hint=name===labels.schedule?"Открываю мастер нового эфира":name===labels.finish?"Подтверждаю создание эфира":name===labels.edit?"Открываю настройки эфира":name===labels.save?"Сохраняю настройки":name===labels.go?"Запускаю эфир в Studio":name===labels.end?"Завершаю эфир в Studio":"Ожидаю действие YouTube Studio";
    this.progress(hint);const b = await waitUntil(() => this.button(name, scope, false)); await b.click({ timeout: 10000 }); }
  async dialog() {
    return waitUntil(async () => await uniqueVisible(this.page.getByRole("dialog"), false)
      || await uniqueVisible(this.page.locator('ytcp-dialog[opened], tp-yt-paper-dialog[opened]').filter({ hasNot: this.page.getByRole("dialog") }), false));
  }
  async verify(expectedUrl = "") {
    this.progress("Открываю YouTube Studio и проверяю канал");
    const liveUrl = this.page.url().match(new RegExp(`${ORIGIN.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}/video/([A-Za-z0-9_-]{11})/livestreaming`));
    if (liveUrl) {
      this.broadcastId = liveUrl[1];
      let channelId = await this.page.evaluate(() => {
        const c = window.ytcfg && window.ytcfg.get && window.ytcfg.get("DELEGATED_SESSION_ID");
        return typeof c === "string" && /^UC/.test(c) ? c : "";
      }).catch(() => "");
      if (!CHANNEL.test(channelId) && CHANNEL.test(this.channelId)) channelId = this.channelId;
      if (CHANNEL.test(channelId)) {
        this.channelId = channelId;
        const name = await this.page.locator("#channel-name, ytcp-entity-name").first().innerText({ timeout: 2000 }).catch(() => "");
        return { channelId, channelName: name };
      }
    }
    await this.page.goto(ORIGIN, { waitUntil: "domcontentloaded", timeout: 90000 });
    await existing.waitForStudioChannel(this.page, 90000);
    // Read identity and URL in one document context. Serializing HTML after the
    // channel redirect races Studio's navigation and is not needed for UI control.
    const data = await waitUntil(async () => {
      let snapshot;
      try {
        snapshot = await this.page.evaluate(() => ({ url: location.href, ready: document.readyState,
          text: (document.body?.innerText || "").slice(0, 20000) }));
      } catch (error) {
        if (/Execution context was destroyed|Cannot find context with specified id/i.test(error.message)) return null;
        throw error;
      }
      const state = existing.studioPageState(snapshot.url, snapshot.text);
      if (state.blocker) throw new Error(state.blocker);
      const match = snapshot.url.match(/^https:\/\/studio\.youtube\.com\/channel\/(UC[A-Za-z0-9_-]+)(?:\/|$)/);
      return match && snapshot.ready !== "loading" ? { channelId: match[1] } : null;
    }, 90000);
    if(expectedUrl) {
      const u=new URL(expectedUrl);
      if(!/^(www\.)?youtube\.com$/.test(u.hostname) || !/^\/(?:@[^/]+|channel\/UC[A-Za-z0-9_-]+)(?:\/|$)/.test(u.pathname))throw new Error("Нужна ссылка на YouTube-канал, а не видео.");
      let expected="";const direct=u.pathname.match(/^\/channel\/(UC[A-Za-z0-9_-]+)/);
      if(direct)expected=direct[1];
      else {
        const tab=await this.page.context().newPage();
        try { await tab.goto(u.href,{waitUntil:"domcontentloaded",timeout:90000});
          expected=await waitUntil(()=>tab.evaluate(()=>window.ytInitialData?.metadata?.channelMetadataRenderer?.externalId || document.querySelector('meta[itemprop="channelId"]')?.content || ""),30000);
        }finally{await tab.close();}
      }
      if(expected!==data.channelId)throw new Error("В Dolphin открыт другой YouTube-канал, чем в выделенной строке.");
    }
    this.channelId = data.channelId;
    // Name is informational; channel ID is the binding authority.
    const name = await this.page.locator("#channel-name").first().innerText({ timeout: 1000 }).catch(() => "");
    return { channelId: data.channelId, channelName: name };
  }
  async activeBroadcastId() {
    const fromUrl = this.page.url().match(/\/video\/([A-Za-z0-9_-]{11})(?:\/|$)/);
    if (fromUrl) return fromUrl[1];
  if (!/\/video\//i.test(this.page.url())) return "";
    return await this.page.evaluate(() => {
      const cfg = window.ytcfg && window.ytcfg.get && window.ytcfg.get.bind(window.ytcfg);
      for (const key of ["VIDEO_ID", "LIVE_STREAM_ID", "DELEGATED_VIDEO_ID"]) {
        const id = cfg(key);
        if (typeof id === "string" && /^[A-Za-z0-9_-]{11}$/.test(id)) return id;
      }
      return "";
    }).catch(() => "");
  }
  async encoderDashboardReady() {
    const text = await this.page.locator("body").innerText().catch(() => "");
    return /URL трансляции|Stream URL|подключите видеокодер|Чтобы начать трансляцию|Ключ трансляции|Default stream key/i.test(text);
  }
  async findBroadcastRoom() {
    const id = await this.activeBroadcastId();
    if (!VIDEO.test(id)) return false;
    const open = `${ORIGIN}/video/${id}/livestreaming`;
    if (!this.page.url().includes(`/video/${id}/`)) {
      await this.page.goto(open, { waitUntil: "domcontentloaded", timeout: 90000 });
    }
    for (let attempt = 0; attempt < 3; attempt++) {
      const text = await this.page.locator("body").innerText({ timeout: 10000 }).catch(() => "");
      if ((await this.roomReady()) || (await this.encoderDashboardReady()) || /URL трансляции|подключите видеокодер|Изменить/i.test(text)) break;
      this.progress("Панель эфира пустая — обновляю страницу");
      await this.page.goto(open, { waitUntil: "domcontentloaded", timeout: 90000 });
      await this.page.waitForTimeout(2500);
    }
    const ready = await waitUntil(() => this.roomReady() || this.encoderDashboardReady(), 20000).catch(() => null);
    if (!ready) {
      this.progress("Панель эфира не загрузилась — перейду к «Запланировать трансляцию»");
      return false;
    }
    this.broadcastId = id;
    return true;
  }
  async open(id) {
    if (!VIDEO.test(id)) throw new Error("Некорректный эфир.");
    this.broadcastId = id;
    await this.page.goto(`${ORIGIN}/video/${id}/livestreaming`, { waitUntil: "domcontentloaded", timeout: 60000 });
    await this.assertTarget();
    // The Go live control can be absent until the encoder sends its first frames.
    await waitUntil(()=>this.roomReady(),30000);
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
  async roomReady() {
    return await this.button(labels.edit,this.page,false) || await this.button(labels.go,this.page,false) || await this.button(labels.end,this.page,false)
      || await uniqueVisible(this.page.getByText(/^(Stream URL|URL трансляции|URL сервера)$/i,{exact:true}),false)
      || labels.finished.test(await this.page.locator("body").innerText());
  }
  async liveSetupPanel() {
    return await uniqueVisible(
      this.page.locator("ytcp-dialog, ytd-flow-dialog, ytcp-flow, [role='dialog']").filter({
        hasText: /Панель управления трансляциями|Когда вы хотите выйти в эфир/i
      }),
      false
    );
  }
  async pickSetupChoice(namePattern, scope = this.page) {
    const roleBtn = scope.getByRole("button", { name: namePattern });
    let choice = await uniqueVisible(roleBtn, false);
    if (!choice) {
      choice = await uniqueVisible(
        scope.locator("ytcp-selectable-button, ytcp-button, tp-yt-paper-button").filter({ hasText: namePattern }),
        false
      );
    }
    return choice;
  }
  async liveWhenStepOpen() {
    const panel = (await this.liveSetupPanel()) || this.page;
    const nowBtn = await this.pickSetupChoice(/^(Right now|Now|Сейчас)$/i, panel);
    const laterBtn = await this.pickSetupChoice(/^(Later|Позже)$/i, panel);
    return !!(nowBtn && laterBtn);
  }
  async confirmLiveSetupWizard() {
    const done = await uniqueVisible(this.page.getByRole("button", { name: /^(Done|Готово|Save|Сохранить|Next|Далее|Got it|Понятно|OK|ОК)$/i }), false);
    if (done) { this.progress("Первый запуск Studio: подтверждаю мастер настройки"); await done.click(); await this.page.waitForTimeout(600); }
  }
  async streamSetupModalOpen() {
    const text = await this.page.locator("body").innerText().catch(() => "");
    return /Настройка трансляции|Скопируйте ключ трансляции|Stream setup|Copy the stream key/i.test(text);
  }
  async dismissStreamSetupModal() {
    if (!(await this.streamSetupModalOpen())) return false;
    const ok = await this.pickSetupChoice(/^(OK|ОК)$/i) || await uniqueVisible(this.page.getByRole("button", { name: /^(OK|ОК)$/i }), false);
    if (!ok) return false;
    this.progress("Первый запуск Studio: закрываю «Настройка трансляции»");
    await ok.click({ timeout: 15000 });
    await this.page.waitForTimeout(800);
    return true;
  }
  async liveWizardBlocksManage() {
    if (await this.liveWhenStepOpen()) return true;
    if (await this.streamSetupModalOpen()) return true;
    const panel = await this.liveSetupPanel();
    if (panel) return true;
    const text = await this.page.locator("body").innerText().catch(() => "");
    return /способ.*трансляц|how do you want to stream|выберите способ|через что|веб-камера|видеокодер/i.test(text)
      && !/Запланировать трансляцию|Schedule stream/i.test(text);
  }
  async manageReady() {
    if (await this.liveWizardBlocksManage()) return false;
    return !!(await this.button(labels.schedule, this.page, false) || await this.button(labels.createNew, this.page, false));
  }
  async liveSetup() {
    if (!(await this.liveWizardBlocksManage()) && await this.manageReady()) return false;
    const encoder = /видеокодер|streaming software|encoder|программ.*трансляц|друг.*программ/i;
    let changed = false;
    const bodyText = await this.page.locator("body").innerText().catch(() => "");
    if (/Выберите способ выхода в эфир|how do you want to stream/i.test(bodyText)) {
      const viaEncoder = await this.pickSetupChoice(/^С помощью видеокодера$/i, this.page)
        || await this.pickSetupChoice(/видеокодер/i, this.page);
      if (viaEncoder) {
        this.progress("Первый запуск Studio: выбираю «С помощью видеокодера»");
        await viaEncoder.click({ timeout: 15000 });
        await this.page.waitForTimeout(2000);
        await this.dismissStreamSetupModal();
        return true;
      }
    }
    if (await this.liveWhenStepOpen()) {
      const panel = (await this.liveSetupPanel()) || this.page;
      const nowBtn = await this.pickSetupChoice(/^(Right now|Now|Сейчас)$/i, panel);
      this.progress("Первый запуск Studio: выбираю «Сейчас»");
      await nowBtn.click({ timeout: 15000 });
      await this.page.waitForTimeout(1500);
      return true;
    }
    const scope = (await this.liveSetupPanel()) || this.page;
    const viaEncoder = await this.pickSetupChoice(/^С помощью видеокодера$/i, scope);
    const viaCamera = await this.pickSetupChoice(/^Через встроенную веб-камеру$/i, scope);
    if (viaEncoder && viaCamera) {
      this.progress("Первый запуск Studio: выбираю «С помощью видеокодера»");
      await viaEncoder.click({ timeout: 15000 });
      changed = true;
      await this.page.waitForTimeout(1500);
      if (await this.dismissStreamSetupModal()) return true;
    }
    const enc = await this.pickSetupChoice(encoder, scope)
      || await uniqueVisible(scope.getByRole("radio").filter({ hasText: encoder }), false)
      || await uniqueVisible(scope.locator("ytcp-selectable-button, [role='button']").filter({ hasText: encoder }), false);
    if (enc) {
      this.progress("Первый запуск Studio: выбираю «С помощью видеокодера»");
      await enc.click({ timeout: 15000 });
      changed = true;
      await this.confirmLiveSetupWizard();
      await this.page.waitForTimeout(1200);
    }
    if (await this.dismissStreamSetupModal()) changed = true;
    return changed;
  }
  async manage() {
    const target=`${ORIGIN}/channel/${this.channelId}/livestreaming/manage`;
    this.progress("Открываю управление трансляциями нужного канала");
    if (!/\/livestreaming\/manage(?:\?|$|\/)/.test(this.page.url())) {
      await this.page.goto(target,{waitUntil:"domcontentloaded",timeout:120000});
    }
    await waitUntil(async()=>{
      const text=await this.page.locator("body").innerText();
      const blocker=existing.studioPageState(this.page.url(),text).blocker;if(blocker)throw new Error(blocker);
      if(/Verify it.?s you|Подтвердите.*(личность|вход)|войдите в аккаунт|повторн.*вход/i.test(text))
        throw new Error("Google запрашивает подтверждение входа. Заверши проверку в открытой вкладке Dolphin и дождись следующей попытки.");
      if(/24\s*(?:hours|час)|activate live streaming|включить.*прям.*трансляц|live streaming isn.t available|трансляции недоступны/i.test(text))
        throw new Error("YouTube требует активации/проверки доступа к эфирам. Заверши этот шаг в Dolphin; первая активация может занять до 24 часов.");
      if (await this.manageReady()) return true;
      if (await this.button(labels.schedule, this.page, false)) return true;
      if(!/\/livestreaming\/manage(?:\?|$|\/)/.test(this.page.url())) {
        await this.page.goto(target,{waitUntil:"domcontentloaded",timeout:60000});return false;
      }
      return await this.manageReady();
    },180000);
    this.progress("Страница управления трансляциями готова");
    const url=new URL(this.page.url());
    if(url.origin!==ORIGIN || !url.pathname.startsWith(`/channel/${this.channelId}/`))throw new Error("Studio не открыл управление нужным каналом.");
  }
  async prepare() {
    await this.manage();
    const liveRows=this.page.locator("ytcp-video-row, [role='row']").filter({hasText:/\bLIVE\b|Трансляция ид[её]т|В эфире/});
    if(await liveRows.count())throw new Error("На канале уже есть эфир. Существующая передача не изменена.");
    this.progress("YouTube готов к созданию отдельного эфира через видеокодер");
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
      this.thumbnailEvidence = await require("./youtube-thumbnail").uploadThumbnail(this.page, options.thumbnail, {scope});
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
    await this.prepare();
    if (VIDEO.test(this.broadcastId)) {
      this.progress("Использую уже открытую панель управления эфиром");
      return this.broadcastId;
    }
    const liveRows = this.page.locator("ytcp-video-row, [role='row']").filter({ hasText: /\bLIVE\b|Трансляция ид[её]т|В эфире/ });
    if (await liveRows.count()) throw new Error("На канале уже есть эфир. Существующая передача не изменена.");
    await this.manage();
    const scheduleBtn = await waitUntil(async () => await this.button(labels.schedule, this.page, false)
      || await uniqueVisible(this.page.locator("ytcp-button, ytcp-button-shape").filter({ hasText: labels.schedule }), false), 45000);
    this.progress("Творческая студия: открываю мастер «Запланировать трансляцию»");
    await scheduleBtn.click({ timeout: 20000, force: true });
    const newButton = await this.button(labels.createNew, this.page, false);
    if (newButton) await newButton.click();
    let dialog = await waitUntil(() => this.dialog());
    const software = await uniqueVisible(dialog.getByRole("radio", { name: labels.software }), false);
    if (software) await software.click();
  const encoderCombo = await uniqueVisible(dialog.locator('[role="combobox"], ytcp-dropdown-trigger, ytcp-dropdown').filter({ hasText: /видеокодер|encoder/i }), false);
    if (encoderCombo && !(await dialog.innerText()).match(/видеокодер|encoder/i)) {
      await encoderCombo.click();
      await this.click(labels.software, dialog);
    }
    const marker = /\[VideoBatch:[a-f0-9]{32}\]/.test(options.description || "") ? options.description
      : ((options.description || "") + (options.description ? "\n" : "") + "[VideoBatch:" + (options.operationMarker || "live") + "]");
    await this.details(dialog, title, { ...options, description: marker, thumbnail: "", tags: options.tags || [] }, true);
    for (let i = 0; i < 4; i++) {
      const next = await this.button(labels.next, dialog, false);
      if (next) { await next.click(); await this.page.waitForTimeout(500); continue; }
      const privacy = await uniqueVisible(dialog.getByRole("radio", { name: labels[options.privacy] }), false);
      if (privacy) { await privacy.click(); break; }
    }
    const done = await this.button(labels.finish, dialog, false) || await this.button(/^(Создать|Create)$/i, dialog, false);
    if (done) await done.click();
    else await this.click(labels.next, dialog);
    await waitUntil(async () => !await dialog.isVisible(), 30000);
    const openRoom = await this.button(/^(View in Live Control Room|Open Live Control Room|Открыть панель|панель управления|Перейти в панель)/i, this.page, false);
    if (openRoom) await openRoom.click();
    const row = await uniqueVisible(this.page.locator("ytcp-video-row, tr, [role='row']").filter({ hasText: title }), false);
    if (row) await row.click({ timeout: 10000 }).catch(() => {});
    const id = await waitUntil(async () => {
      const open = await this.activeBroadcastId();
      if (VIDEO.test(open || "")) return open;
      return await this.discover(title).catch(() => "");
    }, 45000);
    if (!VIDEO.test(id || "")) throw new Error("Studio не подтвердил панель созданного эфира.");
    await this.findBroadcastRoom();
    return id;
  }
  async readClipboard() {
    await this.page.context().grantPermissions(["clipboard-read", "clipboard-write"]).catch(() => {});
    return (await this.page.evaluate(async () => await navigator.clipboard.readText()).catch(() => "")).trim();
  }
  async copyIngestFromButtons() {
    let key = "", address = "";
    const btns = await this.page.getByRole("button", { name: /^(Copy|Копировать|Скопировать)$/i }).all();
    for (const btn of btns) {
      if (!(await btn.isVisible().catch(() => false))) continue;
      await btn.click({ timeout: 5000 }).catch(() => {});
      await this.page.waitForTimeout(350);
      const v = await this.readClipboard();
      if (!v) continue;
      if (/^rtmps?:\/\//i.test(v) && !address) address = v.split(/\s+/)[0];
      else if (/^[A-Za-z0-9_-]{8,200}$/.test(v) && !key) key = v;
      if (key && address) break;
    }
    return { key, address };
  }
  async captureCopyClick(copy) {
    await this.page.evaluate(() => {
      window.__vbCopied = ""; window.__vbOriginalCopy = navigator.clipboard.writeText; window.__vbOriginalWrite = navigator.clipboard.write;
      navigator.clipboard.writeText = async text => { window.__vbCopied = String(text); };
      navigator.clipboard.write = async items => { for (const item of items) if (item.types.includes("text/plain")) { window.__vbCopied = await (await item.getType("text/plain")).text(); break; } };
      window.__vbCopyListener = e => {
        const active = document.activeElement;
        const selected = window.getSelection()?.toString() || (active && typeof active.selectionStart === "number" ? String(active.value || "").slice(active.selectionStart, active.selectionEnd) : "");
        const text = e.clipboardData?.getData("text/plain") || selected;
        if (text) { window.__vbCopied = text; e.preventDefault(); }
      };
      document.addEventListener("copy", window.__vbCopyListener, true);
    });
    try {
      await copy.click();
      return await waitUntil(() => this.page.evaluate(() => window.__vbCopied || ""), 5000);
    } finally {
      await this.page.evaluate(() => {
        navigator.clipboard.writeText = window.__vbOriginalCopy; navigator.clipboard.write = window.__vbOriginalWrite;
        document.removeEventListener("copy", window.__vbCopyListener, true);
        delete window.__vbCopied; delete window.__vbOriginalCopy; delete window.__vbCopyListener; delete window.__vbOriginalWrite;
      }).catch(() => {});
    }
  }
  async ensureIngestionSettings() {
    const tab = await uniqueVisible(this.page.getByRole("tab", { name: /^(Настройки трансляции|Stream settings)$/i }), false)
      || await uniqueVisible(this.page.locator("tp-yt-paper-tab, ytcp-tab-group *").filter({ hasText: /^Настройки трансляции$/i }), false);
    if (tab) await tab.click();
    await this.page.mouse.wheel(0, 1200);
    await this.page.waitForTimeout(400);
  }
  async copySetting(pattern) {
    await this.ensureIngestionSettings();
  // Find the smallest visible setting container with exactly one Copy control.
    let label = await uniqueVisible(this.page.getByText(pattern, { exact: true }), false)
      || await uniqueVisible(this.page.getByText(pattern), false);
    if (!label) throw new Error("Не найдена подпись параметра трансляции: " + pattern.source);
    let container = label;
    for (let i = 0; i < 6; i++) {
      container = container.locator("..");
      const copy = await this.button(/^(Copy|Копировать|Скопировать)$/i, container, false);
      if (!copy) continue;
      return await this.captureCopyClick(copy);
    }
    throw new Error("Не найдено управление копированием параметра трансляции: "+pattern.source);
  }
  async copyIngestViaAria(kind) {
    const name = kind === "url"
      ? /^(Copy stream URL|Скопировать URL трансляции|Копировать URL трансляции|Copy URL)/i
      : /^(Copy stream key|Скопировать ключ трансляции|Копировать ключ трансляции|Copy key)/i;
    const btn = await uniqueVisible(this.page.getByRole("button", { name }), false);
    return btn ? await this.captureCopyClick(btn) : "";
  }
  async visibleIngestAddress() {
    const text = await this.page.locator("body").innerText();
    const match = text.match(/rtmps?:\/\/a\.rtmp\.youtube\.com\/live2[^\s]*/i) || text.match(/rtmps?:\/\/a\.rtmps\.youtube\.com\/live2[^\s]*/i);
    return match ? match[0].trim() : "";
  }
  async ingest(name) {
    await this.assertTarget();
    await this.ensureIngestionSettings();
    this.progress("Получаю URL сервера и ключ трансляции");
    let { key: clipKey, address: clipAddress } = await this.copyIngestFromButtons();
    let address = clipAddress;
    let key = clipKey;
    if (!address) address = await this.copyIngestViaAria("url").catch(() => "");
    if (!address) address = await this.copySetting(/^(Stream URL|URL трансляции|URL сервера)$/i).catch(() => "");
    if (!address) address = await this.visibleIngestAddress();
    if (!key) key = await this.copyIngestViaAria("key").catch(() => "");
    if (!key) key = await this.copySetting(/^(Stream key|Ключ трансляции|Ключ:)/i).catch(() => "");
    if (!key) {
      const header = await uniqueVisible(this.page.locator("*").filter({ hasText: /^(Default stream key|Ключ трансляции|Ключ:)/i }), false);
      if (header) {
        let node = header;
        for (let i = 0; i < 8 && !key; i++) {
          node = node.locator("..");
          const copy = await this.button(/^(Copy|Копировать|Скопировать)$/i, node, false);
          if (copy) key = await this.captureCopyClick(copy).catch(() => "");
        }
      }
    }
    if (/^[A-Za-z0-9_-]{8,200}$/.test((key || "").trim()) && (address || "").trim()) {
      this.progress("Ключ и URL скопированы с панели эфира");
      return { key: key.trim(), address: address.trim().split(/\s+/)[0] };
    }
    // A separate key avoids sharing a previous stream's ingest destination.
    this.progress("Создаю отдельный ключ трансляции для выбранного канала");
    const drop = await uniqueVisible(this.page.locator('#stream-key-select ytcp-dropdown-trigger, #stream-key-select [role="combobox"], [aria-label="Stream key"], [aria-label="Ключ трансляции"]'));
    await drop.click();
    await this.click(/^(Create new stream key|Создать ключ трансляции|Создать новый ключ трансляции)$/i);
    const d = await this.dialog();
    const input = await uniqueVisible(d.locator('input[aria-label="Name"], input[aria-label="Название"], #name input, input[name="name"]'));
    await input.fill(name); await this.click(/^(Create|Создать)$/i, d);
    await waitUntil(async () => (await drop.innerText()).includes(name));
    const streamKey = await this.copySetting(/^(Stream key(?: \(paste in encoder\))?|Ключ трансляции(?: \(вставьте в видеокодер\))?)$/i);
    const streamAddress = await this.copySetting(/^(Stream URL|URL трансляции|URL сервера)$/i);
    this.progress("URL сервера и ключ получены; ключ в лог не записывается");
    return { key: streamKey.trim(), address: streamAddress.trim() };
  }
  async configure(options) {
    this.progress("Сохраняю название, описание, теги и превью эфира");
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
    if(options.thumbnail)await require("./youtube-thumbnail").verifyThumbnail(this.page,options.thumbnail,{scope:verify});
    const checkedPrivacy = await uniqueVisible(verify.getByRole("radio", { name: labels[options.privacy] }), false);
    if (checkedPrivacy && await checkedPrivacy.getAttribute("aria-checked") !== "true" && !await checkedPrivacy.isChecked().catch(() => false)) throw new Error("Studio не подтвердил доступ.");
    if (!checkedPrivacy && !labels[options.privacy].test((await verify.locator('#privacy-select, [aria-label="Visibility"], [aria-label="Доступ"]').innerText()).trim()))
      throw new Error("Studio не подтвердил доступ.");
    await this.click(/^(Cancel|Отмена|Close|Закрыть)$/i, verify);
  }
  async readState() {
    await this.assertTarget();
    const text = await this.page.locator("body").innerText();
    const blocker = existing.studioPageState(this.page.url(), text).blocker;
    if (blocker) throw new Error(blocker);
    // Studio only offers End stream for a live broadcast. The mere existence of an FFmpeg process is insufficient.
    const end = await this.button(labels.end, this.page, false);
    if (end && await end.isEnabled()) return "live";
    if (labels.finished.test(text)) return "complete";
    const go = await this.button(labels.go, this.page, false);
    if (go || await this.roomReady()) return "ready";
    return null; // Native controls can briefly disappear while Studio renders the new state.
  }
  async state() { return waitUntil(()=>this.readState(),15000); }
  async canStart() { const b = await this.button(labels.go, this.page, false); return !!b && await b.isEnabled(); }
  async start() {
    await this.assertTarget(); await this.click(labels.go);
    // The native confirmation can appear after the click has returned.
    let confirmed=false;
    await waitUntil(async () => {
      const d=await uniqueVisible(this.page.locator('[role="dialog"], ytcp-dialog[opened]'),false);
      if(d && await this.button(labels.go,d,false)) {
        if(!confirmed){await this.click(labels.go,d);confirmed=true;}
        return false;
      }
      return await this.readState()==="live";
    },20000);
  }
  async complete(startAttempted) {
    let state = await this.state();
    if (state === "complete") return;
    if (state === "live") {
      await this.click(labels.end);
      const dialog = await this.dialog();
      await this.click(labels.end, dialog);
      await waitUntil(async () => !await dialog.isVisible());
      await waitUntil(async () => await this.readState() === "complete", 30000); return;
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

async function livePageUsable(page) {
  const text = await page.locator("body").innerText({ timeout: 4000 }).catch(() => "");
  return /URL трансляции|Stream URL|подключите видеокодер|Чтобы начать трансляцию|Изменить/i.test(text);
}
async function pickStudioTaskPage(context) {
  const manage = context.pages().find(p => !p.isClosed() && /studio\.youtube\.com\/channel\/UC[A-Za-z0-9_-]+\/livestreaming\/manage/i.test(p.url()));
  if (manage) return { page: manage, owned: false };
  const videoLive = context.pages().filter(p => !p.isClosed() && /studio\.youtube\.com\/video\/[A-Za-z0-9_-]{11}\/livestreaming/i.test(p.url()));
  for (const page of videoLive) {
    if (await livePageUsable(page)) return { page, owned: false };
  }
  for (const page of videoLive) await page.close({ runBeforeUnload: false }).catch(() => {});
  const hub = context.pages().find(p => !p.isClosed() && /studio\.youtube\.com\/channel\/UC[A-Za-z0-9_-]+\/livestreaming(?:\/|$)/i.test(p.url()));
  if (hub) return { page: hub, owned: false };
  return null;
}
async function main() {
  const lines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
  let input = ""; for await (const line of lines) { input = line; break; } lines.close();
  emitProgress("Studio worker "+WORKER_BUILD);
  const job = validateJob(JSON.parse(input));
  const sessionsDir = path.join(path.dirname(job.stateDirectory), "dolphin-sessions");
  const leasePath = dolphin.liveLeasePath(job, sessionsDir);
  if (fs.existsSync(leasePath)) {
    const lease = JSON.parse(fs.readFileSync(leasePath, "utf8"));
    const journal = path.join(job.stateDirectory, lease.operationId + ".json");
    if (fs.existsSync(journal) && lease.operationId !== job.operationId) {
      job.operationId = lease.operationId;
      emitProgress("Продолжаю незавершённый эфир (" + job.operationId.slice(0, 8) + "…)");
    }
  } else {
    const resumes = [];
    for (const name of fs.readdirSync(job.stateDirectory)) {
      if (!name.endsWith(".json") || name.includes("diagnostic")) continue;
      const saved = JSON.parse(fs.readFileSync(path.join(job.stateDirectory, name), "utf8").replace(/^\uFEFF/, ""));
      if (saved.profileId === job.profileId && saved.channelId === job.expectedChannelId && saved.broadcastId && !saved.completed) resumes.push(saved);
    }
    if (resumes.length === 1 && resumes[0].operationId !== job.operationId) {
      job.operationId = resumes[0].operationId;
      emitProgress("Продолжаю журнал эфира (" + job.operationId.slice(0, 8) + "…)");
    }
  }
  job.liveOperationId = job.operationId;
  const token = process.env.VIDEOBATCH_DOLPHIN_TOKEN || "";
  if (!token) throw new Error("Не передан API-токен Dolphin.");
  const request = (p, options = {}, timeout) => dolphin.requestLocal(job, p, options, timeout);
  emitProgress("Подключаюсь к локальному Dolphin");
  const auth = await request("/v1.0/auth/login-with-token", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token }) });
  if (!auth.ok || auth.data.success === false) throw new Error("Dolphin отклонил авторизацию локального API.");
  emitProgress("Открываю или подключаю профиль Dolphin "+job.profileId);
  const endpoint = await dolphin.startOrAttach(job, path.join(path.dirname(job.stateDirectory), "dolphin-sessions"), request);
  const { chromium } = require("playwright-core");
  const browser = await chromium.connectOverCDP(endpoint, { timeout: 60000 });
  let page,ui,succeeded=false,taskOwned=true;
  const events = [];
  try {
    const context = browser.contexts()[0]; if (!context) throw new Error("Dolphin не вернул браузерную сессию.");
    const picked = await pickStudioTaskPage(context);
    if (picked) { page = picked.page; taskOwned = picked.owned; await page.bringToFront().catch(() => {}); }
    else {
      page = await context.newPage();
      await page.evaluate(id => { window.name = "videobatch-live-" + id; }, job.operationId);
    }
    page.setDefaultTimeout(12000);
    page.on("response", response => {
      const u = new URL(response.url());
      if (u.origin !== ORIGIN) return;
      events.push({ method: response.request().method(), path: u.pathname, status: response.status() });
      if (events.length > 25) events.shift();
    });
    ui = new NativeStudio(page, job.expectedChannelId || "", emitProgress);
    const data = await new SessionEngine(job, ui).execute();
    succeeded=true;
    process.stdout.write(JSON.stringify({ type: "result", ok: true, data }) + "\n");
  } catch (error) {
    try {
      const leaseDir = path.join(path.dirname(job.stateDirectory), "dolphin-sessions");
      const lease = dolphin.liveLeasePath(job, leaseDir);
      if (fs.existsSync(lease)) fs.unlinkSync(lease);
    } catch (_) {}
    fs.mkdirSync(job.stateDirectory, { recursive: true });
    const diagnostic=path.join(job.stateDirectory,job.operationId+".diagnostic.json");
    fs.writeFileSync(diagnostic, JSON.stringify({ command: job.command, error: sanitize(error), step:ui?.lastStep || "Подключение Dolphin", action:ui?.lastAction || "", taskTabLeftOpen:!!page, requests: events }), { mode: 0o600 });
    emitProgress("Ошибка Studio: "+sanitize(error)+" · диагностика: "+diagnostic+" · проблемная вкладка оставлена открытой");
    throw error;
  } finally {
    if (page && succeeded && taskOwned) await dolphin.releaseTaskPage(page).catch(() => {});
    // connectOverCDP: close disconnects this client; never close the existing context or call Dolphin /stop.
    await browser.close().catch(() => {});
  }
}
if (require.main === module) main().catch(error => {
  process.stdout.write(JSON.stringify({ type: "result", ok: false, error: sanitize(error) }) + "\n"); process.exitCode = 1;
});
module.exports = { WORKER_BUILD,validateJob, sanitize, rtmpsAddress, OperationStore, operationTitle, SessionEngine, NativeStudio, uniqueVisible, labels };
