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
    await dialog.dismiss();
    report('studio_dialog','Перезагрузка/выход отменены: открыта незавершённая загрузка. Сначала остановите задачу.');
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
async function openStudio(page, channelUrl, report) {
  const expected = String(channelUrl || '').match(/\/channel\/(UC[A-Za-z0-9_-]+)/)?.[1];
  const url = 'https://studio.youtube.com' + (expected ? '/channel/' + expected : '');
  report('studio_open', 'Открываю YouTube Studio · без проверки сторонних IP-сервисов.');
  await page.goto(url, { waitUntil: 'commit', timeout: 35000 });
  await waitFor(async () => {
    const current = page.url();
    if (/accounts\.google\.com/.test(current)) throw new Error('Требуется вход в Google в выбранном Dolphin-профиле.');
    if (expected && /studio\.youtube\.com\/channel\/UC/.test(current) && !current.includes('/channel/' + expected))
      throw new Error('Studio открыл другой канал. Файл не передавался.');
    return visible(page.locator('#create-icon').or(page.getByRole('button', { name: /^(Create|Создать)$/i })));
  }, 25000, 'Studio не показал кнопку «Создать» за 25 секунд. Файл не передавался; проверьте вход и доступ к Studio.');
  report('studio_ready', 'YouTube Studio готов.');
}
async function videoInput(page) {
  const inputs = page.locator('ytcp-uploads-dialog input[type=file], ytcp-upload-dialog input[type=file], ytcp-uploads-file-picker input[type=file], input[type=file][accept*="video"]');
  const candidates = [];
  for (const input of await inputs.all()) {
    const accept = await input.getAttribute('accept') || '';
    if (/image/i.test(accept) && !/video/i.test(accept)) continue;
    const active = await input.evaluate(e => {
      for (let p = e.parentElement || e.getRootNode().host; p; p = p.parentElement || p.getRootNode().host)
        if (p.hidden || getComputedStyle(p).display === 'none' || getComputedStyle(p).visibility === 'hidden') return false;
      return true;
    });
    if (!active) continue;
    candidates.push(input);
  }
  if (candidates.length === 1) return candidates[0];
  const videos = [];
  for (const input of candidates) if (/video/i.test(await input.getAttribute('accept') || '')) videos.push(input);
  if (videos.length === 1) return videos[0];
  return null;
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
async function openFile(page, file, beforeSelection, report, { forceCdp = false } = {}) {
  file = path.resolve(file);
  const size = fs.statSync(file).size;
  let input = await bounded(() => videoInput(page), 8000, 'Studio не ответил при поиске выбора видео. Файл не передавался.');
  if (!input) {
    const create = await bounded(() => visible(page.locator('#create-icon').or(page.getByRole('button', { name: /^(Create|Создать)$/i }))), 8000, 'Studio не ответил при поиске кнопки «Создать». Файл не передавался.');
    if (!create) throw new Error('Studio: нет кнопки «Создать». Файл не передавался.');
    await create.click({ timeout: 8000 });
    const upload = await waitFor(() => visible(page.getByRole('menuitem', { name: /^(Upload videos?|Загрузить видео|Добавить видео)$/i })
      .or(page.locator('tp-yt-paper-item,ytcp-text-menu ytcp-ve').filter({ hasText: /^(Upload videos?|Загрузить видео|Добавить видео)$/i }))),
    8000, 'Studio не открыл пункт «Загрузить видео». Файл не передавался.');
    await upload.click({ timeout: 8000 });
    input = await waitFor(() => videoInput(page), 15000, 'Studio не открыл выбор видео за 15 секунд. Файл не передавался.');
  }
  // Resolve the input BEFORE marking the durable checkpoint. Never retry a possibly
  // accepted file: an uncertain response remains pending for manual inspection.
  const cdp = size > 49 * 1024 * 1024 || forceCdp ? await prepareCdpFile(page, input) : null;
  try {
    await beforeSelection();
    report('file_select', 'Передаю видео в Studio: ' + path.basename(file));
    if (cdp) await bounded(() => cdp.send(file), 20000, 'Studio не подтвердил приём файла. Повторная передача заблокирована.');
    else await input.setInputFiles(file, { timeout: 20000 });
    report('file_selected','Файл передан. Ожидаю поле названия в редакторе Studio.');
  } finally { if (cdp) await cdp.dispose(); }
}
module.exports = { bounded, isStudioUpload, handleUploadDialog, connect, openStudio, openFile, videoInput, findMarkedNode };
