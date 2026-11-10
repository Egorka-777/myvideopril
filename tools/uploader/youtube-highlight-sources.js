"use strict";

// Public YouTube pages in the selected Dolphin context only. No search API,
// internal endpoint, cookie export, retry, or alternate-account path.
const { channelVideosUrl } = require('./youtube-open-today');
const ID = /^[A-Za-z0-9_-]{11}$/;
const CARDS = 'ytd-video-renderer, ytd-rich-grid-media, ytd-grid-video-renderer, yt-lockup-view-model';

function parseSource(value) {
  let url;
  try { url = new URL(value); } catch { throw new Error('Нужна ссылка канала или видео YouTube: ' + value); }
  if (url.protocol !== 'https:' || !['youtube.com', 'www.youtube.com', 'm.youtube.com', 'youtu.be'].includes(url.hostname))
    throw new Error('Нужна HTTPS-ссылка YouTube: ' + value);
  const parts = url.pathname.split('/').filter(Boolean);
  const id = url.hostname === 'youtu.be' && parts.length === 1 ? parts[0] :
    url.pathname === '/watch' ? url.searchParams.get('v') :
    ['shorts', 'live'].includes(parts[0]) && parts.length === 2 ? parts[1] : null;
  if (id !== null) {
    if (!ID.test(id)) throw new Error('Некорректный ID видео: ' + value);
    return { kind: ['shorts', 'live'].includes(parts[0]) ? 'excluded' : 'video', videoId: id };
  }
  url.hostname = 'www.youtube.com';
  return { kind: 'channel', url: channelVideosUrl(url.href) };
}

function validateOptions(options) {
  if (!['keywords', 'sources'].includes(options.sourceMode)) throw new Error('Не задан режим источников Highlights.');
  if (!Array.isArray(options.sourceEntries) || !options.sourceEntries.length ||
      options.sourceEntries.some(value => typeof value !== 'string' || !value.trim()))
    throw new Error('Укажите непустые строки источников Highlights.');
  if (!/^\d{4}-\d{2}-\d{2}$/.test(options.todayDate || '') ||
      !Number.isFinite(Date.parse(options.todayDate + 'T00:00:00Z')) ||
      new Date(options.todayDate + 'T00:00:00Z').toISOString().slice(0, 10) !== options.todayDate)
    throw new Error('Не задана календарная дата ПК yyyy-MM-dd.');
  if (!Number.isInteger(options.pcUtcOffsetMinutes)) throw new Error('Не задано смещение времени ПК от UTC в минутах.');
  if (!['today', 'week'].includes(options.sourcePeriod ?? 'today')) throw new Error('Некорректный период источников Highlights.');
  if (!['short', 'medium'].includes(options.sourceDuration ?? 'medium')) throw new Error('Некорректная длительность источников Highlights.');
  if (!Number.isInteger(options.sourceVideoCount ?? 3) || (options.sourceVideoCount ?? 3) < 1)
    throw new Error('Количество исходных видео должно быть положительным целым числом.');
  return [...new Set(options.sourceEntries.map(value => value.trim()))].map(value =>
    options.sourceMode === 'sources' ? parseSource(value) : { kind: 'keyword', query: value });
}

function publicationDate(value, offset) {
  const text = String(value || '');
  if (/^\d{4}-\d{2}-\d{2}$/.test(text)) return text;
  if (!/^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$/.test(text)) return '';
  const ms = Date.parse(text);
  return Number.isFinite(ms) ? new Date(ms + offset * 60000).toISOString().slice(0, 10) : '';
}

function periodCutoff(options) {
  const date = new Date(options.todayDate + 'T00:00:00Z');
  if (options.sourcePeriod === 'week') date.setUTCDate(date.getUTCDate() - 7);
  return date.toISOString().slice(0, 10);
}

function classifyWatch(snapshot, id, options) {
  const player = snapshot.player;
  const details = player?.videoDetails;
  const meta = player?.microformat?.playerMicroformatRenderer;
  if (!details || details.videoId !== id || !meta) throw new Error('Не подтверждены метаданные видео ' + id + '.');
  if (['LOGIN_REQUIRED', 'AGE_CHECK_REQUIRED', 'CONTENT_CHECK_REQUIRED'].includes(player.playabilityStatus?.status))
    throw new Error('YouTube требует ручную проверку доступа к видео ' + id + '.');
  if (snapshot.isShort || details.isShorts || details.isShort || meta.isShortsEligible ||
      details.isLiveContent || details.isLive || details.isUpcoming || meta.liveBroadcastDetails ||
      details.isPrivate || meta.isUnlisted || player.playabilityStatus?.status !== 'OK')
    return { reason: 'not_public_long_video' };
  const publishedDate = publicationDate(meta.publishDate || snapshot.datePublished, options.pcUtcOffsetMinutes);
  if (!publishedDate) throw new Error('Не подтверждена дата публикации видео ' + id + '.');
  const publishedTime = Date.parse(meta.publishDate || snapshot.datePublished);
  const now = Date.parse(options.sourceNow || '');
  const outsideRollingWeek = options.sourcePeriod === 'week' && Number.isFinite(now) && Number.isFinite(publishedTime) &&
    (publishedTime < now - 7 * 86400000 || publishedTime > now);
  if (outsideRollingWeek || publishedDate < periodCutoff(options) || publishedDate > options.todayDate) return { reason: 'other_date', publishedDate };
  const duration = Number(details.lengthSeconds);
  if (!Number.isFinite(duration) || duration <= 0) throw new Error('Не подтверждена длительность видео ' + id + '.');
  // 180 seconds belongs to medium only; 1200 seconds is still medium.
  if (options.sourceDuration === 'short' ? duration >= 180 : duration < 180 || duration > 1200)
    return { reason: 'other_duration', publishedDate };
  if (!details.title || !details.author) throw new Error('Не подтверждены заголовок/канал видео ' + id + '.');
  if (!/^\d+$/.test(String(details.viewCount))) throw new Error('Не подтверждено число просмотров видео ' + id + '.');
  const viewCount = Number(details.viewCount);
  if (!Number.isSafeInteger(viewCount)) throw new Error('Не подтверждено число просмотров видео ' + id + '.');
  const candidate = { videoId: id, title: details.title, url: 'https://www.youtube.com/watch?v=' + id,
    channel: details.author, publishedDate, durationSeconds: duration, viewCount };
  return { reason: 'in_period', publishedDate, candidate };
}

async function readyAccount(page) {
  await page.waitForFunction(() => document.querySelector('ytd-masthead #avatar-btn, ytd-masthead a[href*="accounts.google.com"], form[action*="consent"], #captcha') ||
    /Confirm it's you|Verify it's you|Подтвердите, что это вы|Before you continue|Прежде чем продолжить/.test(document.body?.innerText || ''),
  null, { timeout: 20000 });
  const state = await page.evaluate(() => ({ signedIn: !!document.querySelector('ytd-masthead #avatar-btn'),
    blocked: !!document.querySelector('form[action*="consent"], #captcha') ||
      /Confirm it's you|Verify it's you|Подтвердите, что это вы|Before you continue|Прежде чем продолжить/.test(document.body?.innerText || '') }));
  if (state.blocked) throw new Error('YouTube требует ручную проверку или согласие в выбранном профиле Dolphin.');
  if (!state.signedIn) throw new Error('В выбранном профиле Dolphin нужно вручную войти в YouTube.');
}

async function navigate(page, url) {
  await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });
  await readyAccount(page);
}

async function readyListing(page, kind) {
  await page.locator(kind === 'keyword' ? 'ytd-search' : 'ytd-browse').waitFor({ state: 'visible', timeout: 20000 });
  await page.waitForFunction(selector => !!document.querySelector(selector + ', ytd-message-renderer, ytd-background-promo-renderer'),
    CARDS, { timeout: 20000 });
}

async function selectSearchFilter(page, label) {
  await page.getByRole('button', { name: /^(Search filters|Filters|Фильтры поиска|Фильтры)$/i }).first().click({ timeout: 15000 });
  // Link accessible names normalize Polymer's surrounding whitespace/newlines.
  const choice = page.locator('ytd-search-filter-renderer').getByRole('link', { name: label });
  await choice.waitFor({ state: 'visible', timeout: 10000 });
  const before = page.url();
  // URL changes can precede SPA results. Await YouTube's completed native
  // navigation as well; full document navigation has its own readiness below.
  await page.evaluate(() => {
    window.__highlightNavigationFinished = false;
    document.addEventListener('yt-navigate-finish', () => { window.__highlightNavigationFinished = true; }, { once: true });
  });
  await choice.click();
  await page.waitForURL(url => url.href !== before, { timeout: 15000 });
  await page.waitForFunction(() => window.__highlightNavigationFinished === undefined || window.__highlightNavigationFinished === true,
    null, { timeout: 20000 });
  await choice.waitFor({ state: 'hidden', timeout: 10000 });
  await readyListing(page, 'keyword');
}

async function readListing(page) {
  return page.evaluate(selector => {
    const scope = document.querySelector('ytd-search, ytd-browse');
    const ids = [];
    for (const card of scope.querySelectorAll(selector)) {
      if (card.closest('ytd-reel-shelf-renderer, ytd-rich-shelf-renderer') || card.querySelector('a[href*="/shorts/"], ytd-thumbnail-overlay-upcoming-event-reminder-renderer')) continue;
      const badges = Array.from(card.querySelectorAll('ytd-badge-supported-renderer, ytd-thumbnail-overlay-time-status-renderer, yt-badge-view-model'));
      if (badges.some(b => /LIVE|UPCOMING|PREMIERE|ПРЯМОЙ ЭФИР|ТРАНСЛЯЦИЯ|ПРЕМЬЕРА|СКОРО НАЧНЕТСЯ/i.test(b.textContent || b.getAttribute('aria-label') || ''))) continue;
      for (const a of card.querySelectorAll('a[href*="/watch?"]')) {
        const id = new URL(a.href).searchParams.get('v');
        if (/^[A-Za-z0-9_-]{11}$/.test(id || '')) { ids.push(id); break; }
      }
    }
    return [...new Set(ids)];
  }, CARDS);
}

function queryLanguage(query) {
  return /[а-яё]/i.test(query) ? 'ru' : 'en';
}

function matchesLanguage(text, language) {
  const ru = (String(text || '').match(/[а-яё]/gi) || []).length;
  const en = (String(text || '').match(/[a-z]/gi) || []).length;
  return language === 'ru' ? ru > en : en > ru;
}

function listingViews(text) {
  const value = String(text || '').replace(/[\u00a0\u202f]/g, ' ').trim();
  if (/No views|Нет просмотров/i.test(value)) return 0;
  const match = value.match(/([\d][\d\s.,]*)\s*(тыс\.?|млн\.?|млрд\.?|[KMB])?/i);
  if (!match) return null;
  const factor = /^(тыс|k)/i.test(match[2] || '') ? 1e3 : /^(млн|m)/i.test(match[2] || '') ? 1e6 : /^(млрд|b)/i.test(match[2] || '') ? 1e9 : 1;
  const number = factor === 1 ? match[1].replace(/\D/g, '') : match[1].replace(/\s/g, '').replace(',', '.');
  return Math.round(Number(number) * factor);
}

async function readSearchCards(page) {
  const cards = await page.evaluate(() => Array.from(document.querySelectorAll('ytd-search ytd-video-renderer')).map(card => {
    const data = card.data || {};
    const text = value => typeof value === 'string' ? value : value?.simpleText || (value?.runs || []).map(run => run.text).join('');
    const link = card.querySelector('a#video-title, a[href*="/watch?"]');
    return {
      videoId: data.videoId || (link ? new URL(link.href).searchParams.get('v') : ''),
      title: text(data.title) || link?.textContent || '',
      description: (data.detailedMetadataSnippets || []).map(x => text(x.snippetText)).join(' ') || text(data.descriptionSnippet) || card.querySelector('.metadata-snippet-text')?.textContent || '',
      views: text(data.viewCountText) || card.querySelector('#metadata-line')?.textContent || '',
      excluded: !!card.closest('ytd-reel-shelf-renderer') || !!card.querySelector('a[href*="/shorts/"]') ||
        /LIVE|UPCOMING|ПРЯМОЙ ЭФИР|ТРАНСЛЯЦИЯ|ПРЕМЬЕРА|СКОРО НАЧНЕТСЯ/i.test(Array.from(card.querySelectorAll('ytd-badge-supported-renderer, ytd-thumbnail-overlay-time-status-renderer')).map(x => x.textContent).join(' '))
    };
  }));
  return cards.filter(card => ID.test(card.videoId || '') && !card.excluded).map(card => ({ ...card, viewCount: listingViews(card.views) }));
}

async function discoverKeywords(page, options, entries, report) {
  const collected = new Map();
  for (const entry of entries) {
    const language = queryLanguage(entry.query), seen = new Set();
    report('highlight_search', entry.query);
    await navigate(page, 'https://www.youtube.com/results?search_query=' + encodeURIComponent(entry.query));
    await readyListing(page, 'keyword');
    await selectSearchFilter(page, options.sourcePeriod === 'week' ? /^(This week|За эту неделю)$/i : /^(Today|Сегодня)$/i);
    await selectSearchFilter(page, /^(Video|Видео)$/i);
    await selectSearchFilter(page, options.sourceDuration === 'short' ? /^(Under 3 minutes|Менее 3 минут)$/i : /^(3–20 minutes|3–20 минут)$/i);
    await selectSearchFilter(page, /^(View count|По популярности)$/i);
    do {
      for (const card of await readSearchCards(page)) {
        if (seen.size >= 300) break;
        seen.add(card.videoId);
        if (card.viewCount === null || !matchesLanguage(card.title, language) ||
            (card.description && !matchesLanguage(card.description, language))) continue;
        if (!collected.has(card.videoId)) collected.set(card.videoId, { ...card, language });
      }
      report('highlight_search', entry.query + ': собрано ' + seen.size + '/300; подходящих по языку: ' + collected.size);
    } while (seen.size < 300 && await nextListingPage(page));
  }
  const ranked = [...collected.values()].sort((a, b) => b.viewCount - a.viewCount), selected = [];
  if (!ranked.length) {
    report('highlight_search', 'Найдено 0 из ' + (options.sourceVideoCount ?? 3) + ': подходящих видео нет.');
    return selected;
  }
  for (const card of ranked) {
    report('highlight_search', 'Проверяю ссылку без открытия видео: ' + card.title);
    const snapshot = await page.evaluate(async id => {
      const response = await fetch('/watch?v=' + encodeURIComponent(id), { signal: AbortSignal.timeout(20000) });
      if (!response.ok) throw new Error('YouTube HTTP ' + response.status);
      const html = await response.text();
      const match = html.match(/var ytInitialPlayerResponse\s*=\s*(\{.*?\});/s);
      if (!match) throw new Error('Не найдены публичные метаданные видео ' + id);
      return { player: JSON.parse(match[1]), isShort: /<link[^>]+rel="canonical"[^>]+href="[^"]*\/shorts\//i.test(html) };
    }, card.videoId);
    const details = snapshot.player?.videoDetails;
    const verdict = !matchesLanguage(details?.title, card.language) || !matchesLanguage(details?.shortDescription, card.language) ?
      { reason: 'other_language' } : classifyWatch(snapshot, card.videoId, options);
    report('highlight_candidate', card.videoId + ': ' + verdict.reason + '; дата=' + (verdict.publishedDate || snapshot.player?.microformat?.playerMicroformatRenderer?.publishDate || '') + '; просмотры=' + (details?.viewCount || ''));
    if (verdict.candidate) selected.push(verdict.candidate);
    if (selected.length === (options.sourceVideoCount ?? 3)) break;
  }
  report('highlight_search', 'Выбрано ' + selected.length + ' из ' + (options.sourceVideoCount ?? 3) +
    (selected.length < (options.sourceVideoCount ?? 3) ? ': подходящих видео меньше запрошенного.' : '.'));
  return selected.sort((a, b) => b.viewCount - a.viewCount);
}

async function inspectWatch(page, id, options, language) {
  await navigate(page, 'https://www.youtube.com/watch?v=' + id);
  await page.waitForFunction(id => window.ytInitialPlayerResponse?.videoDetails?.videoId === id,
    id, { timeout: 20000 });
  const snapshot = await page.evaluate(id => {
    const player = window.ytInitialPlayerResponse;
    let isShort = location.pathname.startsWith('/shorts/') || !!document.querySelector('ytd-reel-video-renderer') ||
      !!document.querySelector('link[rel="canonical"][href*="/shorts/"], meta[property="og:url"][content*="/shorts/"]');
    // A native endpoint for this exact ID identifies Shorts even on /watch.
    function visit(node) {
      if (!node || typeof node !== 'object' || isShort) return;
      if (node.reelWatchEndpoint?.videoId === id) { isShort = true; return; }
      for (const child of Object.values(node)) visit(child);
    }
    visit(window.ytInitialData);
    if (Array.from(document.querySelectorAll('a[href*="/shorts/"]')).some(a => new URL(a.href).pathname === '/shorts/' + id)) isShort = true;
    return { player, isShort, datePublished: document.querySelector('meta[itemprop="datePublished"]')?.content || '' };
  }, id);
  if (language && (!matchesLanguage(snapshot.player?.videoDetails?.title, language) ||
      !matchesLanguage(snapshot.player?.videoDetails?.shortDescription, language))) return { reason: 'other_language' };
  return classifyWatch(snapshot, id, options);
}

async function nextListingPage(page) {
  // YouTube replaces continuation nodes while appending results. Read the
  // current node and scroll synchronously, without waiting for its stability.
  const before = await page.evaluate(selector => {
    const nodes = document.querySelectorAll('ytd-search ytd-continuation-item-renderer, ytd-browse ytd-continuation-item-renderer');
    const continuation = nodes[nodes.length - 1];
    if (!continuation) return null;
    const count = document.querySelectorAll(selector).length;
    continuation.scrollIntoView({ block: 'end', behavior: 'instant' });
    return count;
  }, CARDS);
  if (before === null) return false;
  await page.waitForFunction(({ selector, before }) => document.querySelectorAll(selector).length > before ||
    !document.querySelector('ytd-search ytd-continuation-item-renderer, ytd-browse ytd-continuation-item-renderer'),
  { selector: CARDS, before }, { timeout: 20000 });
  return true;
}

async function discoverSources(page, options, report = () => {}) {
  const entries = validateOptions(options);
  if (options.sourceMode === 'keywords') return discoverKeywords(page, options, entries, report);
  const checked = new Map(), candidates = [];
  // Separate task-owned watch tab preserves listing state and continuation UI.
  const watch = await page.context().newPage();
  try {
    for (const entry of entries) {
      if (entry.kind === 'excluded') { report('highlight_candidate', entry.videoId + ': shorts/live source excluded'); continue; }
      if (entry.kind !== 'video') {
        report('highlight_search', entry.query || entry.url);
        await navigate(page, entry.kind === 'keyword' ? 'https://www.youtube.com/results?search_query=' + encodeURIComponent(entry.query) : entry.url);
        await readyListing(page, entry.kind);
        if (entry.kind === 'keyword') {
          await selectSearchFilter(page, options.sourcePeriod === 'week' ? /^(This week|За эту неделю)$/i : /^(Today|Сегодня)$/i);
          await selectSearchFilter(page, /^(Video|Видео)$/i);
          await selectSearchFilter(page, options.sourceDuration === 'short' ? /^(Under 3 minutes|Менее 3 минут)$/i : /^(3–20 minutes|3–20 минут)$/i);
          await selectSearchFilter(page, /^(View count|По популярности)$/i);
        } else {
          const latest = page.locator('yt-chip-cloud-chip-renderer').filter({ hasText: /^(Latest|Новые|Недавние)$/i });
          await latest.waitFor({ state: 'visible', timeout: 10000 });
          if (await latest.getAttribute('selected') === null && await latest.getAttribute('aria-selected') !== 'true') {
            await latest.click();
            await page.waitForFunction(() => Array.from(document.querySelectorAll('yt-chip-cloud-chip-renderer')).some(chip =>
              /^(Latest|Новые|Недавние)$/i.test(chip.textContent.trim()) && (chip.hasAttribute('selected') || chip.getAttribute('aria-selected') === 'true')),
            null, { timeout: 15000 });
            await readyListing(page, entry.kind);
          }
        }
      }
      let older = false;
      do {
        const ids = entry.kind === 'video' ? [entry.videoId] : await readListing(page);
        for (const id of ids) {
          let verdict = checked.get(id);
          if (!verdict) {
            verdict = await inspectWatch(watch, id, options);
            checked.set(id, verdict);
            report('highlight_candidate', id + ': ' + verdict.reason + (verdict.publishedDate ? ' ' + verdict.publishedDate : ''));
            if (verdict.candidate) candidates.push(verdict.candidate);
          }
          if (entry.kind === 'channel' && verdict.publishedDate && verdict.publishedDate < periodCutoff(options)) older = true;
        }
      } while (entry.kind !== 'video' && !older && await nextListingPage(page));
    }
    const selected = candidates.sort((a, b) => b.viewCount - a.viewCount).slice(0, options.sourceVideoCount ?? 3);
    report('highlight_search', 'Выбрано ' + selected.length + ' из ' + (options.sourceVideoCount ?? 3) +
      (selected.length < (options.sourceVideoCount ?? 3) ? ': подходящих видео меньше запрошенного.' : '.'));
    return selected;
  } finally { await watch.close(); }
}

module.exports = { validateOptions, parseSource, publicationDate, classifyWatch, selectSearchFilter, discoverSources, queryLanguage, matchesLanguage, listingViews };
