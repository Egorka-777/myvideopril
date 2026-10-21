"use strict";
// UI-only Studio startup. No IP-service navigation, request interception or reload loops.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

async function bounded(work, timeout, message) {
  let timer;
  try { return await Promise.race([Promise.resolve().then(work), new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(message)), timeout);
  })]); } finally { clearTimeout(timer); }
}
function isStudioUpload(job) {
  return !['checkOnly','recheckOnly','searchOnly','watchMesh','watchDirectLinks','shortsWarmup','openTodayOnly'].some(k => job[k]);
}
async function handleUploadDialog(dialog,report){
  if(dialog.type()==='beforeunload'){
    try{await dialog.dismiss();}catch{}
    report('studio_dialog','Перезагрузка/выход отменены: открыта незавершённая загрузка. Страница не перезагружалась.');
  }else if(dialog.type()==='alert')await dialog.accept();
  else{
    await dialog.dismiss();
    report('studio_dialog','Неожиданный диалог отменён; подтверждение публикации не выполнялось.');
  }
}
async function connect(chromium, endpoint, report, { timeout = 20000 } = {}) {
  report('dolphin', `Подключаюсь к браузеру · не более ${timeout/1000} секунд.`);
  try { return await chromium.connectOverCDP(endpoint, { timeout }); }
  catch (e) {
    // Playwright releases its CDP transport on its own connection timeout. Do not attach
    // three more times to a browser whose existing tab may be blocking initialization.
    throw new Error(`Dolphin: браузер не подтвердил CDP за ${timeout/1000} секунд. Подключение освобождено; профиль не перезапускался. Проверьте зависшую стартовую вкладку профиля. ` + e.message);
  }
}
async function waitFor(fn, timeout, message) {
  const end = Date.now() + timeout;
  do { const value = await bounded(fn, Math.min(5000, Math.max(1, end - Date.now())), message); if (value) return value; await new Promise(r => setTimeout(r, 150)); } while (Date.now() < end);
  throw new Error(message);
}
async function visible(locator) {
  for (const item of await locator.all()) if (await item.isVisible()) return item;
  return null;
}
async function anyVisible(locator) {
  for (const item of await locator.all()) if (await item.isVisible()) return true;
  return false;
}
const UPLOADS='ytcp-uploads-dialog,ytcp-upload-dialog,ytcp-video-upload-dialog';
const SUCCESS=/^(?:Video published|Video scheduled|Publish complete|Publication scheduled|Видео опубликовано|Видео запланировано|Публикация завершена|Публикация запланирована)[.!]?$/i;
async function controlEnabled(button){
  return button.evaluate(el=>{
    for(let p=el;p;p=p.parentElement||p.getRootNode().host)
      if(p.hasAttribute('disabled')||p.getAttribute('aria-disabled')==='true'||p.inert)return false;
    return true;
  });
}
async function publicationState(page,{scope=null,expectedId=''}={}){
  const surfaces=[];
  const candidates=await (scope||page.locator(UPLOADS)).all();
  for(const root of await page.locator('ytcp-video-share-dialog').all())if(!await root.evaluate(e=>!!e.closest('ytcp-uploads-dialog,ytcp-upload-dialog,ytcp-video-upload-dialog')))candidates.push(root);
  for(const root of candidates){
    let alive=await root.isVisible();
    if(!alive)alive=await anyVisible(root.locator('h1,h2,h3,[role=heading],#close-button,#done-button,[contenteditable=true],[role=dialog]'));
    if(alive)surfaces.push(root);
  }
  for(const root of surfaces){
    const ids=await root.locator('a[href],input').evaluateAll(nodes=>[...new Set(nodes.map(e=>String(e.value||e.getAttribute('href')||'').match(/(?:youtu\.be\/|[?&]v=|\/shorts\/|\/video\/)([A-Za-z0-9_-]{11})(?:\b|\/|$)/)?.[1]).filter(Boolean))]);
    if(expectedId&&ids.length&&(!ids.includes(expectedId)||ids.some(id=>id!==expectedId)))
      throw new Error('Studio показывает другое видео в окне завершения. Окно не закрывалось; повторная загрузка остановлена.');
    const header=await visible(root.getByText(SUCCESS));
    if(header)return {state:/scheduled|запланир/i.test(await header.innerText())?'scheduled':'published',surface:root,ids};
    const error=await visible(root.locator('[role=alert],#error-message,.error-message').filter({hasText:/couldn.t (?:save|publish)|failed to (?:save|publish)|не удалось (?:сохранить|опубликовать)|upload failed|ошибка загрузки/i}));
    if(error)throw new Error('Studio отклонил сохранение: '+(await error.innerText()).slice(0,200));
  }
  return {state:surfaces.length?'pending':'closed',surface:surfaces[0]||null,ids:[]};
}
async function findPublishCloseButton(page,{scope=null}={}){
  if(!scope){const state=await publicationState(page);if(!/^(published|scheduled)$/.test(state.state))return null;scope=state.surface;}
  // Prefer the footer. A disabled footer must become ready; the always-present
  // header X must not short-circuit that wait. Duplicate Copy link buttons do
  // not participate in either success detection or close-button identity.
  for(const id of ['close-button','close-icon-button']){
    const hosts=[];for(const host of await scope.locator('#'+id).all())if(await host.isVisible())hosts.push(host);
    if(hosts.length>1)throw new Error('Studio: несколько кнопок закрытия в одном окне.');
    if(hosts.length){const inner=await visible(hosts[0].getByRole('button'));return inner||hosts[0];}
  }
  const matches=[];for(const btn of await scope.getByRole('button',{name:/^(Close|Закрыть)$/i}).all())if(await btn.isVisible())matches.push(btn);
  if(matches.length>1)throw new Error('Studio: несколько кнопок «Закрыть» в одном окне.');return matches[0]||null;
}
async function isPostPublishScreen(page){return /^(published|scheduled)$/.test((await publicationState(page)).state);}
async function dismissPublishDialog(page,report=()=>{},{scope=null,expectedId='',confirmed=false,timeout=90000}={}){
  const initial=await publicationState(page,{scope,expectedId});
  if(initial.state==='closed')return;
  if(initial.state==='pending'&&!confirmed)return; // Never close an unfinished upload.
  scope=initial.surface;
  const end=Date.now()+timeout;let reported=0;
  const close=await waitFor(async()=>{
    const state=await publicationState(page,{scope,expectedId});if(state.state==='closed')return {closed:true};
    if(state.state==='pending'&&!confirmed)return null;
    const button=await findPublishCloseButton(page,{scope});
    if(button&&await controlEnabled(button))return {button};
    if(Date.now()-reported>5000){report('studio_close','Публикация сохранена. Ожидаю доступную кнопку «Закрыть / Close».');reported=Date.now();}
    return null;
  },timeout,'Studio подтвердил видео, но кнопка «Закрыть» не стала доступна. Видео повторно не отправляйте.');
  if(close.closed)return;
  report('studio_close','Нажимаю «Закрыть / Close» один раз после сохранения.');
  await close.button.click({timeout:Math.min(10000,Math.max(1,end-Date.now()))});
  await waitFor(async()=>!(await publicationState(page,{scope,expectedId})).surface,Math.max(1,end-Date.now()),'Видео сохранено, но Studio не закрыл окно. Следующий файл не передавался.');
}
async function uploadWizardOpen(page) {
  // A custom-element host can have zero bounds while its positioned descendants
  // are visible. A failed inspection is an error, never permission to navigate.
  for(const root of await page.locator('ytcp-uploads-dialog,ytcp-upload-dialog,ytcp-video-upload-dialog').all()){
    if(await root.isVisible())return true;
    for(const child of await root.locator('[contenteditable=true],#next-button,#done-button,#close-button,[role=dialog]').all())if(await child.isVisible())return true;
  }
  return false;
}
const dialogGuards=new WeakMap();
function installUploadDialogGuard(context,report){
  if(dialogGuards.has(context))return dialogGuards.get(context).dispose;
  const seen=new WeakSet();
  const listener=dialog=>{if(seen.has(dialog))return;seen.add(dialog);handleUploadDialog(dialog,report).catch(e=>report('diagnostic','Диалог Studio: '+e.message));};
  context.on('dialog',listener);
  const dispose=()=>{context.off('dialog',listener);dialogGuards.delete(context);};dialogGuards.set(context,{dispose});return dispose;
}
async function disconnect(browser,report=()=>{}){
  if(!browser)return;
  // connectOverCDP: close() disconnects this client. Do not navigate the page,
  // close its default context, or call Dolphin /stop on an incomplete wizard.
  await bounded(()=>browser.close(),2000,'Отключение CDP превысило 2 секунды.').catch(e=>report('diagnostic',e.message));
}
async function openStudio(page, channelUrl, report) {
  const expected = String(channelUrl || '').match(/\/channel\/(UC[A-Za-z0-9_-]+)/)?.[1];
  const url = 'https://studio.youtube.com' + (expected ? '/channel/' + expected : '');
  const onStudio = /^https:\/\/studio\.youtube\.com(?:\/|$)/i.test(page.url() || '');
  const completed=onStudio&&await isPostPublishScreen(page);
  if(completed)await dismissPublishDialog(page,report);
  if (onStudio && await uploadWizardOpen(page)) {
    report('studio_ready', 'YouTube Studio: продолжаю открытый мастер загрузки (без перезагрузки страницы).');
    return;
  }
  report('studio_open', 'Открываю YouTube Studio · без проверки сторонних IP-сервисов.');
  if(!completed)await page.goto(url, { waitUntil: 'commit', timeout: 35000 });
  await waitFor(async () => {
    const current = page.url();
    if (/accounts\.google\.com/.test(current)) throw new Error('Требуется вход в Google в выбранном Dolphin-профиле.');
    if (expected && /studio\.youtube\.com\/channel\/UC/.test(current) && !current.includes('/channel/' + expected))
      throw new Error('Studio открыл другой канал. Файл не передавался.');
    if(await isPostPublishScreen(page)){await dismissPublishDialog(page,report);return null;}
    const button=await visible(page.locator('#create-icon').or(page.getByRole('button',{name:/^(Create|Создать)$/i})));
    return button&&await controlEnabled(button)?button:null;
  }, 90000, 'Studio не показал кнопку «Создать» за 90 секунд. Файл не передавался; проверьте вход и доступ к Studio.');
  report('studio_ready', 'YouTube Studio готов.');
}
async function videoInput(page) {
  const inputs = page.locator('ytcp-uploads-dialog input[type=file], ytcp-upload-dialog input[type=file], ytcp-uploads-file-picker input[type=file], ytcp-video-upload-dialog input[type=file], input[type=file][accept*="video"]');
  let best = null;
  let bestScore = -999;
  for (const input of await inputs.all()) {
    const score = await input.evaluate(el => {
      const accept = (el.getAttribute('accept') || '').toLowerCase();
      if (/image/i.test(accept) && !/video/i.test(accept)) return -100;
      for (let p = el.parentElement || el.getRootNode().host; p; p = p.parentElement || (p.getRootNode && p.getRootNode().host)) {
        if (p.hidden || getComputedStyle(p).display === 'none' || getComputedStyle(p).visibility === 'hidden') return -100;
      }
      let s = 0;
      if (/video/i.test(accept)) s += 40;
      const host = el.getRootNode && el.getRootNode().host;
      const hostTag = (host && host.tagName) || '';
      if (/ytcp-uploads|ytcp-video-upload|ytcp-upload-dialog/i.test(hostTag)) s += 55;
      if (el.closest('ytcp-uploads-dialog, ytcp-uploads-file-picker, ytcp-video-upload-dialog, ytcp-upload-dialog')) s += 60;
      if (location.hostname.includes('studio.youtube.com')) s += 10;
      if (location.pathname.includes('/videos/upload')) s += 15;
      if (el.getAttribute('aria-hidden') === 'true') s -= 5;
      return s;
    }).catch(() => -100);
    if (score > bestScore) { bestScore = score; best = input; }
  }
  return bestScore >= 8 ? best : null;
}
async function ensureUploadPicker(page, channelUrl) {
  let input = await videoInput(page);
  if (input) return input;
  const channelId = String(channelUrl || '').match(/\/channel\/(UC[A-Za-z0-9_-]+)/)?.[1];
  if (!channelId) return null;
  const urls = [
    `https://studio.youtube.com/channel/${channelId}/videos/upload?d=ud`,
    `https://studio.youtube.com/channel/${channelId}/videos/upload`
  ];
  for (const url of urls) {
    if (!page.url().includes('/videos/upload') && !(await uploadWizardOpen(page))) {
      await page.goto(url, { waitUntil: 'commit', timeout: 35000 }).catch(() => {});
    }
    input = await waitFor(() => videoInput(page), 8000, 'Studio не ответил при открытии страницы загрузки.').catch(() => null);
    if (input) return input;
  }
  return null;
}
async function transferFile(page, input, file, report, { forceCdp = false } = {}) {
  const size = fs.statSync(file).size;
  const hidden = await input.evaluate(e => e.getAttribute('aria-hidden') === 'true' || Number(e.tabIndex) < 0).catch(() => true);
  const useCdpFirst = forceCdp || size > 49 * 1024 * 1024 || hidden;
  const viaCdp = async () => {
    const cdp = await prepareCdpFile(page, input);
    try {
      await bounded(() => cdp.send(file), 45000, 'Studio не подтвердил приём файла. Повторная передача заблокирована.');
    } finally {
      await cdp.dispose();
    }
  };
  if (useCdpFirst) {
    await viaCdp();
    return;
  }
  try {
    await input.setInputFiles(file, { timeout: 30000 });
  } catch (e) {
    const msg = String(e && e.message || e);
    if (!/Timeout|not attached|not visible|exceeded|Target closed/i.test(msg)) throw e;
    report('file_select', 'Playwright не передал файл — повтор через CDP…');
    await viaCdp();
  }
}
function findMarkedNode(node, marker) {
  const attrs = node.attributes || [];
  for (let i = 0; i < attrs.length; i += 2)
    if (attrs[i] === 'data-vb-file' && attrs[i + 1] === marker) return node.nodeId;
  for (const child of [...(node.children || []), ...(node.shadowRoots || []), ...(node.contentDocument ? [node.contentDocument] : [])]) {
    const id = findMarkedNode(child, marker); if (id) return id;
  }
  return null;
}
async function prepareCdpFile(page, input) {
  const marker = crypto.randomUUID();
  const client = await bounded(() => page.context().newCDPSession(page), 8000, 'Studio не ответил при подключении к выбору файла. Файл не передавался.');
  const dispose = async () => {
    await Promise.allSettled([
      bounded(() => input.evaluate(e => e.removeAttribute('data-vb-file')), 1000, 'Очистка input превысила время ожидания.'),
      bounded(() => client.detach(), 1000, 'Отключение CDP превысило время ожидания.')
    ]);
  };
  try {
    await bounded(() => input.evaluate((e, value) => e.setAttribute('data-vb-file', value), marker), 8000, 'Studio не ответил при выборе input видео. Файл не передавался.');
    // DOM.querySelector(document) cannot cross the Studio file picker's shadow root.
    const doc = await bounded(() => client.send('DOM.getDocument', { depth: -1, pierce: true }), 8000, 'Studio не ответил при поиске input видео. Файл не передавался.');
    const nodeId = findMarkedNode(doc.root, marker);
    if (!nodeId) throw new Error('CDP не нашёл выбранный input видео, файл не передан.');
    return { send: file => client.send('DOM.setFileInputFiles', { files: [file], nodeId }),
      dispose };
  } catch (e) { await dispose(); throw e; }
}
async function openFile(page, file, beforeSelection, report, { forceCdp = false, channelUrl = '', readyMs=90000 } = {}) {
  file = path.resolve(file);
  if (!fs.existsSync(file)) throw new Error('Не найден файл ' + file);
  if (await isPostPublishScreen(page)) await dismissPublishDialog(page, report);
  for(const root of await page.locator(UPLOADS).all())if(await anyVisible(root.locator('[contenteditable=true],textarea[name=title]')))
    throw new Error('Studio: открыт незавершённый редактор. Новый файл не передавался; проверьте существующий ролик.');
  let input = await ensureUploadPicker(page, channelUrl);
  if (!input) {
    const create=await waitFor(async()=>{
      const button=await visible(page.locator('#create-icon').or(page.getByRole('button',{name:/^(Create|Создать)$/i})));
      return button&&await controlEnabled(button)?button:null;
    },readyMs,'Studio не показал доступную кнопку «Создать». Предыдущий мастер должен закрыться; Файл не передавался.');
    await create.click({ timeout: 8000 });
    const upload = await waitFor(() => visible(page.getByRole('menuitem', { name: /^(Upload videos?|Загрузить видео|Добавить видео)$/i })
      .or(page.locator('tp-yt-paper-item,ytcp-text-menu ytcp-ve').filter({ hasText: /^(Upload videos?|Загрузить видео|Добавить видео)$/i }))),
    12000, 'Studio не открыл пункт «Загрузить видео». Файл не передавался.');
    await upload.click({ timeout: 8000 });
    input = await waitFor(() => videoInput(page), 25000, 'Studio не открыл выбор видео за 25 секунд. Файл не передавался.');
  }
  // Resolve the input BEFORE marking the durable checkpoint. Never retry a possibly
  // accepted file: an uncertain response remains pending for manual inspection.
  await beforeSelection();
  report('file_select', 'Передаю видео в Studio: ' + path.basename(file));
  await transferFile(page, input, file, report, { forceCdp });
  report('file_selected', 'Файл передан. Ожидаю поле названия в редакторе Studio.');
}
module.exports = { uploadWizardOpen,installUploadDialogGuard,disconnect,bounded, isStudioUpload, handleUploadDialog, connect, openStudio, openFile, videoInput, findMarkedNode, ensureUploadPicker, transferFile, dismissPublishDialog, isPostPublishScreen, findPublishCloseButton, publicationState };
