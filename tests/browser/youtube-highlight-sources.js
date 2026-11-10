'use strict';
// Local, routed Chromium pages only: no Dolphin/Google account or external API.
const assert = require('assert');
const fs = require('fs');
const { chromium } = require('../../tools/uploader/node_modules/playwright-core');
const H = require('../../tools/uploader/youtube-highlight-sources');
const base = { sourceMode: 'keywords', sourceEntries: ['news'], todayDate: '2026-11-10', pcUtcOffsetMinutes: 420 };
const ids = { today: 'today000001', old: 'older000001', short: 'short000001', live: 'live0000001', upcoming: 'future00001', shifted: 'shift000001', second: 'today000002', unknown: 'nodate00001', future: 'later000001', boundary: 'bound000001', outside: 'outof000001', third: 'third000001', brief: 'brief000001', long: 'long0000001' };
function player(id, changes = {}) {
  return { playabilityStatus: { status: 'OK' }, videoDetails: { videoId: id, title: 'Title ' + id, author: 'Fixture channel', shortDescription: 'English video description', lengthSeconds: '600', viewCount: '100' },
    microformat: { playerMicroformatRenderer: { publishDate: '2026-11-10' } }, ...changes };
}
function classify({ date = base.todayDate, duration = '600', views = '100', ...options } = {}) {
  const p = player(ids.today);
  p.microformat.playerMicroformatRenderer.publishDate = date;
  p.videoDetails.lengthSeconds = duration;
  p.videoDetails.viewCount = views;
  return H.classifyWatch({ player: p }, ids.today, { ...base, ...options });
}
function card(id) {
  const views = { [ids.today]: 100, [ids.shifted]: 900, [ids.second]: 300, [ids.third]: 1500, [ids.boundary]: 700, [ids.old]: 200 }[id] ?? 50;
  return '<ytd-video-renderer><a id="video-title" href="/watch?v=' + id + '">Video ' + id + '</a><div id="metadata-line">' + views + ' views</div><div class="metadata-snippet-text">English video description</div></ytd-video-renderer>';
}
function shell(body, auth = true) { return '<html><body><ytd-masthead>' + (auth ? '<button id="avatar-btn">Account</button>' : '<a href="https://accounts.google.com/">Sign in</a>') + '</ytd-masthead>' + body + '</body></html>'; }
async function fixture(browser, lang = 'en', options = {}) {
  const context = await browser.newContext(), requests = [], clicks = [], reports = [];
  const labels = lang === 'ru' ? ['Фильтры', options.sourcePeriod === 'week' ? 'За эту неделю' : 'Сегодня', 'Видео', options.sourceDuration === 'short' ? 'Менее 3 минут' : '3–20 минут', 'По популярности'] :
    ['Search filters', options.sourcePeriod === 'week' ? 'This week' : 'Today', 'Video', options.sourceDuration === 'short' ? 'Under 3 minutes' : '3–20 minutes', 'View count'];
  await context.exposeFunction('clicked', text => clicks.push(text));
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()); requests.push(url.href);
    assert.equal(request.method(), 'GET', 'Read-only requests');
    assert(['document', 'fetch'].includes(request.resourceType()), 'Only public pages, no API');
    assert.equal(url.hostname, 'www.youtube.com', 'Only public YouTube pages are navigated');
    assert(['/watch', '/results'].includes(url.pathname) || /^\/@[^/]+\/videos$/.test(url.pathname), 'No upload/studio/internal endpoint');
    let body;
    if (url.pathname === '/watch') {
      const id = url.searchParams.get('v');
      const p = player(id);
      const dates = { [ids.old]: '2026-11-09', [ids.shifted]: '2026-11-09T18:00:00Z', [ids.future]: '2026-11-11', [ids.boundary]: '2026-11-04', [ids.outside]: '2026-11-03' };
      if (dates[id]) p.microformat.playerMicroformatRenderer.publishDate = dates[id];
      if (options.russian) {
        p.videoDetails.title = 'Русское название ролика';
        p.videoDetails.shortDescription = 'Описание видео на русском языке';
        if (id === ids.third) p.videoDetails.shortDescription = 'Original English description of this video';
      }
      p.videoDetails.viewCount = String({ [ids.today]: 100, [ids.shifted]: 900, [ids.second]: 300, [ids.third]: 1500, [ids.boundary]: 700, [ids.old]: 200 }[id] ?? 50);
      if (id === ids.brief) p.videoDetails.lengthSeconds = '179';
      if (id === ids.long) p.videoDetails.lengthSeconds = '1201';
      if (id === ids.live) p.videoDetails.isLiveContent = true;
      if (id === ids.upcoming) p.videoDetails.isUpcoming = true;
      if (id === ids.unknown) delete p.microformat.playerMicroformatRenderer.publishDate;
      body = shell((id === ids.short ? '<link rel="canonical" href="https://www.youtube.com/shorts/' + id + '">' : '') + '<ytd-watch-flexy></ytd-watch-flexy><script>var ytInitialPlayerResponse=' + JSON.stringify(p) + ';window.ytInitialData=' +
        JSON.stringify(id === ids.short ? { endpoint: { reelWatchEndpoint: { videoId: id } } } : {}) + '</script>');
    } else if (url.pathname === '/results') {
      const step = Number(url.searchParams.get('sp') || 0);
      const target = new URL(url.href); target.searchParams.set('sp', String(step + 1));
      const listing = url.searchParams.get('search_query') === 'second query' ? [ids.today, ids.third] :
        [ids.today, ids.today, ids.old, ids.short, ids.live, ids.upcoming, ids.shifted, ids.future, ids.boundary, ids.outside, ids.brief, ids.long];
      let htmlCards = options.empty ? '<ytd-message-renderer>No results</ytd-message-renderer>' : listing.map(card).join('');
      if (options.large) htmlCards = Array.from({ length: 310 }, (_, i) => card('cap' + String(i).padStart(8, '0'))).join('');
      if (options.russian) htmlCards = (htmlCards + card(ids.third)).replace(/Video /g, 'Русское видео ').replace(/English video description/g, 'Русское описание видео');
      body = shell((options.movingContinuation ? '<style>@keyframes shifting {from {transform:translateY(0)} to {transform:translateY(20px)}} ytd-continuation-item-renderer {animation:shifting .05s infinite alternate}</style>' : '') + '<ytd-search><button id="filters">' + labels[0] + '</button><section id="choices" hidden>' +
        (step < 4 ? '<ytd-search-filter-renderer><a href="' + target.href.replace(/&/g, '&amp;') + '">' + '\n<div id="label">\n    <yt-formatted-string>' + labels[step + 1] + '</yt-formatted-string>\n    <yt-icon aria-hidden="true"></yt-icon>\n</div>\n</a>\n</ytd-search-filter-renderer>' : '') + '</section>' + htmlCards +
        (step === 4 && !options.empty ? '<ytd-continuation-item-renderer style="display:block;height:50px;margin-top:1200px">More</ytd-continuation-item-renderer>' : '') +
        '</ytd-search><script>document.querySelector("#filters").onclick=()=>{window.clicked("filters");document.querySelector("#choices").hidden=false;};' +
        'const choice=document.querySelector("ytd-search-filter-renderer a");if(choice)choice.onclick=()=>window.clicked(' + JSON.stringify(labels[step + 1] || '') + ');' +
        'const more=document.querySelector("ytd-continuation-item-renderer");if(more)new IntersectionObserver(rows=>{if(rows.some(r=>r.isIntersecting)){more.insertAdjacentHTML("beforebegin",' + JSON.stringify(card(ids.second)) + ');more.remove();}}).observe(more);' +
        'document.addEventListener("keydown",e=>{if(e.key==="Escape")throw Error("No Escape allowed")});</script>', !options.signedOut);
    } else {
      body = shell('<ytd-browse><yt-chip-cloud-chip-renderer selected>Latest</yt-chip-cloud-chip-renderer>' + [ids.today, ids.old].map(card).join('') +
        '<ytd-continuation-item-renderer style="display:block;height:50px;margin-top:1200px">More</ytd-continuation-item-renderer></ytd-browse>' +
        '<script>const more=document.querySelector("ytd-continuation-item-renderer");new IntersectionObserver(rows=>{if(rows.some(r=>r.isIntersecting)){window.clicked("channel_more");more.insertAdjacentHTML("beforebegin",' + JSON.stringify([ids.boundary, ids.outside].map(card).join('')) + ');more.remove();}}).observe(more);</script>');
    }
    return route.fulfill({ contentType: 'text/html; charset=utf-8', body });
  });
  const page = await context.newPage();
  return { context, page, requests, clicks, labels, reports, report: (stage, text) => reports.push({ stage, text }) };
}
(async () => {
  assert(classify({ sourcePeriod: 'week', sourceNow: '2026-11-10T13:23:00+07:00', date: '2026-11-03T08:04:14-08:00' }).candidate, '91K video inside rolling seven days');
  assert(classify({ sourcePeriod: 'week', sourceNow: '2026-11-10T13:23:00+07:00', date: '2026-11-03T01:22:43-08:00' }).candidate, '42K video inside rolling seven days');
  assert.equal(classify({ sourcePeriod: 'week', sourceNow: '2026-11-10T13:23:00+07:00', date: '2026-11-03T06:22:59Z' }).reason, 'other_date');
  assert.equal(H.queryLanguage('трейдинг'), 'ru');
  assert.equal(H.queryLanguage('trading'), 'en');
  assert(H.matchesLanguage('Описание: трейдинг и инвестиции', 'ru'));
  assert(!H.matchesLanguage('Original English description', 'ru'));
  assert.equal(H.listingViews('6,3 тыс. просмотров'), 6300);
  assert.equal(H.listingViews('1.2M views'), 1200000);
  assert.equal(H.listingViews('12 345 просмотров'), 12345);
  assert.equal(H.listingViews('No views'), 0);
  assert.equal(H.listingViews(''), null);
  assert.equal(H.publicationDate('2026-11-09T18:00:00Z', 420), '2026-11-10');
  assert.equal(H.publicationDate('2026-11-10T01:00:00+03:00', -300), '2026-11-09');
  assert.equal(H.publicationDate('2026-11-10', -300), '2026-11-10', 'Date-only public metadata must not shift');
  assert.equal(H.publicationDate('3 hours ago', 420), '');
  assert.throws(() => H.validateOptions({ ...base, todayDate: '2026-02-30' }), /календарная/);
  assert.throws(() => H.validateOptions({ ...base, pcUtcOffsetMinutes: undefined }), /смещение/);
  for (const sourceVideoCount of [0, -1, 1.5, '2']) assert.throws(() => H.validateOptions({ ...base, sourceVideoCount }), /Количество/);
  assert.throws(() => H.validateOptions({ ...base, sourcePeriod: 'month' }), /период/);
  assert.throws(() => H.validateOptions({ ...base, sourceDuration: 'long' }), /длительность/);
  assert.throws(() => H.parseSource('https://youtube.com.evil.test/watch?v=' + ids.today), /HTTPS/);
  assert.equal(H.parseSource('https://youtu.be/' + ids.today).videoId, ids.today);
  assert.equal(H.parseSource('https://www.youtube.com/@channel/videos').url, 'https://www.youtube.com/@channel/videos');
  assert.equal(H.parseSource('https://youtube.com/shorts/' + ids.short).kind, 'excluded');
  assert.equal(H.parseSource('https://youtube.com/live/' + ids.live).kind, 'excluded');
  assert.equal(H.classifyWatch({ player: player(ids.today, { playabilityStatus: { status: 'ERROR' } }) }, ids.today, base).reason, 'not_public_long_video');
  const privatePlayer = player(ids.today); privatePlayer.videoDetails.isPrivate = true;
  assert.equal(H.classifyWatch({ player: privatePlayer }, ids.today, base).reason, 'not_public_long_video');
  const noDate = player(ids.today); delete noDate.microformat.playerMicroformatRenderer.publishDate;
  assert.throws(() => H.classifyWatch({ player: noDate }, ids.today, base), /дата/);
  assert.throws(() => H.classifyWatch({ player: player(ids.today) }, ids.old, base), /метаданные/);
  for (const sourcePeriod of ['today', 'week']) {
    assert(classify({ sourcePeriod }).candidate);
    assert.equal(classify({ sourcePeriod, date: '2026-11-11' }).reason, 'other_date', 'No future dates');
    assert.equal(!!classify({ sourcePeriod, date: '2026-11-03' }).candidate, sourcePeriod === 'week');
    assert.equal(!!classify({ sourcePeriod, date: '2026-11-04' }).candidate, sourcePeriod === 'week', 'Week cutoff included');
    assert.equal(!!classify({ sourcePeriod, date: '2026-11-09' }).candidate, sourcePeriod === 'week');
  }
  assert(classify({ sourcePeriod: 'week', todayDate: '2026-03-03', date: '2026-02-25' }).candidate, 'Calendar subtraction crosses month');
  assert.equal(classify({ sourcePeriod: 'week', todayDate: '2026-03-03', date: '2026-02-23' }).reason, 'other_date');
  assert(classify({ date: '2026-11-09T18:00:00Z' }).candidate, 'Timestamp converted to PC date');
  assert.equal(classify({ date: '2026-11-10T20:00:00Z' }).reason, 'other_date', 'Future local date rejected');
  for (const duration of [1, 179, 180, 600, 1200, 1201, 1800]) {
    assert.equal(!!classify({ duration: String(duration), sourceDuration: 'short' }).candidate, duration < 180);
    assert.equal(!!classify({ duration: String(duration), sourceDuration: 'medium' }).candidate, duration >= 180 && duration <= 1200);
  }
  for (const duration of [null, '', 'bad', '0', '-1']) assert.throws(() => classify({ duration }), /длительность/);
  const noDuration = player(ids.today); delete noDuration.videoDetails.lengthSeconds;
  assert.throws(() => H.classifyWatch({ player: noDuration }, ids.today, base), /длительность/);
  assert.equal(classify().candidate.durationSeconds, 600, 'Default duration is medium');
  assert.equal(classify({ views: '12345' }).candidate.viewCount, 12345);
  assert.equal(classify({ views: '0' }).candidate.viewCount, 0);
  for (const views of [null, '', '1K', '-1', '1.5']) assert.throws(() => classify({ views }), /просмотров/);
  const noViews = player(ids.today); delete noViews.videoDetails.viewCount;
  assert.throws(() => H.classifyWatch({ player: noViews }, ids.today, base), /просмотров/);
  console.log('PASS options, calendar period/offset/future, duration boundaries, numeric actual player views, metadata guards');
  const worker = fs.readFileSync(require.resolve('../../tools/uploader/worker'), 'utf8');
  assert(worker.includes('function isStudioUploadJob(value) { return !value.highlightsSearch && studioBrowser.isStudioUpload(value); }'));
  const W = require('../../tools/uploader/worker');
  assert.equal(W.isStudioUploadJob({ highlightsSearch: true }), false);
  assert.equal(W.isStudioUploadJob({ highlightsSearch: true, studioProtocolVersion: 2, sourcePeriod: 'week', sourceDuration: 'short' }), false);
  assert.equal(W.isStudioUploadJob({ studioProtocolVersion: 2 }), true);
  assert.equal(W.isStudioUploadJob({ searchOnly: true }), false);
  assert(worker.includes("send('highlight_sources'"));
  assert(worker.includes('!job.highlightsSearch&&!job.checkOnly'));
  console.log('PASS worker non-upload routing / protocol source guards');
  const browser = await chromium.launch({ headless: true });
  try {
    for (const lang of ['en', 'ru']) {
      for (const selection of [{}, { sourcePeriod: 'week', sourceDuration: 'medium' }, { sourcePeriod: 'today', sourceDuration: 'short' }, { sourcePeriod: 'week', sourceDuration: 'short' }]) {
        const f = await fixture(browser, lang, selection);
        const results = await H.discoverSources(f.page, { ...base, ...selection, sourceEntries: ['news', 'news', 'second query'] }, f.report);
        const expected = selection.sourceDuration === 'short' ? [ids.brief] : selection.sourcePeriod === 'week' ?
          [ids.third, ids.shifted, ids.boundary, ids.second, ids.old, ids.today] : [ids.third, ids.shifted, ids.second, ids.today];
        assert.deepEqual(results.map(x => x.videoId), expected.slice(0, 3));
        assert(results.every(x => typeof x.viewCount === 'number' && x.channel === 'Fixture channel'));
        assert(results.every(x => x.durationSeconds === (selection.sourceDuration === 'short' ? 179 : 600)));
        const chain = f.labels.slice(1).flatMap(label => ['filters', label]);
        assert.deepEqual(f.clicks, [...chain, ...chain], 'Native filter order for each distinct phrase');
        assert(f.requests.filter(x => x.includes('/watch?v=' + ids.today)).length <= 1, 'Dedup before watch navigation');
        if (selection.sourceDuration !== 'short') assert.equal(f.requests.filter(x => x.includes('/watch?')).length, selection.sourcePeriod === 'week' ? 3 : 4, 'Only finalists opened, replacing out-of-period candidate');
        const firstWatch = f.requests.findIndex(x => x.includes('/watch?'));
        if (firstWatch >= 0) assert(!f.requests.slice(firstWatch).some(x => x.includes('/results')), 'All queries collected before finalist navigation');
        assert.equal(f.context.pages().length, 1, 'Search never opens watch tabs');
        assert(f.page.url().includes('/results'), 'Search page remains open, no watch navigation');
        assert.equal(f.context.pages().length, 1, 'Task-owned watch tab released');
        await f.context.close();
        console.log('PASS', lang, selection.sourcePeriod || 'today', selection.sourceDuration || 'medium', 'native filter chain, pagination, global view sorting, dedup, exclusions');
      }
    }
    for (const sourcePeriod of ['today', 'week']) {
      const sources = await fixture(browser);
      const results = await H.discoverSources(sources.page, { ...base, sourcePeriod, sourceMode: 'sources', sourceEntries: ['https://www.youtube.com/@channel', 'https://youtu.be/' + ids.today, 'https://www.youtube.com/watch?v=' + ids.shifted, 'https://youtu.be/' + ids.future, 'https://youtu.be/' + ids.brief, 'https://youtu.be/' + ids.long, 'https://www.youtube.com/shorts/' + ids.short] }, sources.report);
      assert.deepEqual(results.map(x => x.videoId), sourcePeriod === 'week' ? [ids.shifted, ids.boundary, ids.old] : [ids.shifted, ids.today]);
      assert.equal(sources.requests.filter(x => x.includes('/watch?v=' + ids.today)).length, 1);
      assert.deepEqual(sources.clicks, sourcePeriod === 'week' ? ['channel_more'] : [], 'Channel stops at period cutoff, not today');
      assert(!sources.requests.some(x => x.includes('/results')));
      await sources.context.close();
    }
    for (const sourceVideoCount of [1, 2, 5, 20]) {
      const configured = await fixture(browser);
      const results = await H.discoverSources(configured.page, { ...base, sourceVideoCount, sourceEntries: ['news', 'second query'] }, configured.report);
      const ordered = [ids.third, ids.shifted, ids.second, ids.today];
      assert.deepEqual(results.map(x => x.videoId), ordered.slice(0, sourceVideoCount), 'Configured source count preserves popularity order');
      assert(configured.reports.some(x => x.text.includes('Выбрано ' + results.length + ' из ' + sourceVideoCount)));
      if (sourceVideoCount > ordered.length) assert(configured.reports.some(x => x.text.includes('меньше запрошенного')));
      await configured.context.close();
    }
    const limitedSources = await fixture(browser);
    const sourceResults = await H.discoverSources(limitedSources.page, { ...base, sourceVideoCount: 1, sourceMode: 'sources',
      sourceEntries: [ids.today, ids.shifted, ids.second].map(id => 'https://youtu.be/' + id) }, limitedSources.report);
    assert.deepEqual(sourceResults.map(x => x.videoId), [ids.shifted], 'Direct sources honor count after global view sorting');
    await limitedSources.context.close();
    console.log('PASS configurable source count, popularity order and explicit shortage');
    const moving = await fixture(browser, 'en', { movingContinuation: true });
    const movingResults = await H.discoverSources(moving.page, base, moving.report);
    assert(movingResults.some(x => x.videoId === ids.second), 'Moving continuation still loads next results');
    await moving.context.close();
    console.log('PASS unstable continuation scroll does not wait for node stability');
    const large = await fixture(browser, 'en', { large: true });
    const top = await H.discoverSources(large.page, base, large.report);
    assert.equal(top.length, 3);
    assert(large.reports.some(x => x.text.includes('300/300')), 'Collection stops at 300 unique cards');
    assert.equal(large.requests.filter(x => x.includes('/watch?')).length, 3);
    await large.context.close();
    console.log('PASS 300-card collection limit, only three watch navigations');
    const russian = await fixture(browser, 'ru', { russian: true });
    const russianResults = await H.discoverSources(russian.page, { ...base, sourceEntries: ['трейдинг', 'second query'] }, russian.report);
    assert(!russianResults.some(x => x.videoId === ids.third), 'Translated title cannot bypass original English description');
    assert(russian.reports.some(x => x.text.includes('other_language')));
    assert(russianResults.length > 0 && russianResults.length <= 3);
    await russian.context.close();
    console.log('PASS Russian query language and original description check');
    const direct = await fixture(browser);
    assert.deepEqual((await H.discoverSources(direct.page, { ...base, sourcePeriod: 'week', sourceDuration: 'short', sourceMode: 'sources', sourceEntries: [ids.today, ids.brief, ids.boundary, ids.future, ids.outside].map(id => 'https://youtu.be/' + id) })).map(x => x.videoId), [ids.brief]);
    await direct.context.close();
    console.log('PASS channel period cutoff/pagination and direct sources share strict dates/duration/views sorting');
    const mixed = await fixture(browser);
    await H.discoverSources(mixed.page, { ...base, sourceMode: 'sources', sourceEntries: ['https://youtu.be/' + ids.old, 'https://www.youtube.com/@channel'] });
    assert.equal(mixed.requests.filter(x => x.includes('/watch?v=' + ids.old)).length, 1);
    assert.deepEqual(mixed.clicks, []);
    await mixed.context.close();
    console.log('PASS cached older-date verdict still terminates channel listing');
    const empty = await fixture(browser, 'en', { empty: true });
    assert.deepEqual(await H.discoverSources(empty.page, base), []);
    await empty.context.close();
    console.log('PASS explicit empty search result');
    const failed = await fixture(browser);
    await assert.rejects(H.discoverSources(failed.page, { ...base, sourceMode: 'sources', sourceEntries: ['https://youtu.be/' + ids.unknown] }), /дата/);
    assert.equal(failed.context.pages().length, 1);
    assert.equal(failed.requests.length, 1, 'Missing date is an error, no retry/fallback');
    await failed.context.close();
    const signedOut = await fixture(browser, 'en', { signedOut: true });
    await assert.rejects(H.discoverSources(signedOut.page, base), /вручную войти/);
    assert.equal(signedOut.requests.length, 1);
    await signedOut.context.close();
    console.log('PASS missing metadata and signed-out account fail visibly without retries or fallback');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
