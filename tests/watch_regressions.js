"use strict";
const assert=require('assert');
const fs=require('fs');
const path=require('path');
const read=p=>fs.readFileSync(path.join(__dirname,'..',p),'utf8');
const worker=read('tools/uploader/worker.js'), uploader=read('src/Uploader.cs');
const workspace=read('src/YouTubeWorkspacePanel.cs'), backend=read('src/YouTubeBackend.cs');
const navigation=read('tools/uploader/youtube-open-today.js');
assert.match(worker,/startOrAttach/,'Reuse Dolphin endpoints without a forced restart');
assert.doesNotMatch(worker,/restartAlreadyRunningProfile/);
assert.match(uploader,/result\.Success=msg\.success/,'Respect worker failure');
assert.match(uploader,/ValidateCommon\(false,channelsFilter\)/,'Validate requested workspace selection');
assert.match(uploader,/ValidateExternalChannels/);
assert.match(workspace,/RunLinkWatch/);
assert.match(worker,/job\.watchDirectLinks/);
assert.match(worker,/Профиль оставлен открытым/);
assert.match(uploader,/ParseDirectWatchLinks/);
assert.match(uploader,/FindUniqueFilePath/,'Rename must pick a free filename instead of overwriting');
assert.doesNotMatch(uploader,/MoveFileReplacing/);
assert.match(uploader,/ch\.Kind=packKind/);
assert.match(backend,/RunCrossMeshWatchAsync/);
assert.match(workspace,/RunCrossLinkWatch/);
assert.match(workspace,/RunCrossMeshWatchAsync/);
assert.match(workspace,/CrossWatchViewerMarket/,'Cross button must launch opposite market profiles');
assert.match(workspace,/GetCheckedChannelsForMarket\(viewerMarket\)/,'Cross viewers come from opposite tab');
assert.match(workspace,/var targetChannels = GetCheckedChannels\(\)/,'Cross targets are checked channels on current tab');
const mesh=uploader.slice(uploader.indexOf('async Task CrossWatchMesh('),uploader.indexOf('async Task CrossWatchLinks('));
assert.match(mesh,/const int batchSize=5/,'Old preference four must not reduce the requested batch');
assert.match(mesh,/NormMarket\(ch.Market\)==viewerMarket/,'Keep RU/EN viewer profiles separated');
assert.match(mesh,/targetChannelsFilter/,'Cross-watch must accept separate target channel list');
assert.match(mesh,/targetChannels\.Select\(ch=>BuildMeshTarget/,'Targets may differ from viewers');
assert.match(mesh,/targets.Skip\(offset\).Concat\(targets.Take\(offset\)\)/,'Rotation must retain every channel');
assert.doesNotMatch(mesh,/Where\(ch=>!string.Equals/,'Own channel is also a navigation target');
assert.doesNotMatch(mesh,/PublishedUrl|BuildMeshCatalogVideos|SaveMeshCatalog/,'Upload history must not select today video');
assert.match(mesh,/await Task.WhenAll\(tasks\)/);
assert.doesNotMatch(mesh,/StopProfileRequired|StopProfile|closeErrors/,'Preserve profiles on success, failure and cancellation');
assert.match(mesh,/keepProfileOpen=true/);
assert.match(mesh,/профили Dolphin оставлены открытыми/);
assert.match(worker,/if \(!studioBrowser\.isStudioUpload\(job\) && !job\.openTodayOnly && !job\.watchMesh\) try/,'Navigation and Studio must not wait for external IP services');
assert.match(worker,/openChannelsWatch\(page, targets, navigationJob/);
assert.match(worker,/const WORKER_BUILD = "[^"]+"/,'Worker must expose its deployed build identifier');
assert.match(worker,/Задание остановлено; профиль Dolphin остаётся открытым/);
assert.doesNotMatch(worker,/pauseVideoPage|pauseCurrentVideo/,'Video pause hooks must be removed');
const playback=read('tools/uploader/youtube-playback.js');
assert.match(playback,/waitForVideoEnd/);
assert.match(playback,/resumeIfPaused/);
assert.match(playback,/Лайк поставлен/);
assert.match(playback,/Видео закончилось/);
assert.doesNotMatch(playback,/keyboard\.press\("k"\)/,'Must not toggle play/pause with hotkey');
assert.match(navigation,/openChannelsWatch/);
assert.match(navigation,/navigateTodayVideo/);
assert.doesNotMatch(navigation,/pauseVideoPage|pauseCurrentVideo|openChannelsPaused/,'Navigation must not force pause');
assert.match(playback,/pickLikeAtSec/,'Like timing must be randomized after ~80%');
assert.match(playback,/curRaw >= likeAtSec/,'Like must happen during playback, not only at end');
assert.match(playback,/disableAutoplay/,'Autoplay must be disabled to avoid wrong next video');
assert.match(playback,/assertStillOnVideo/,'Must detect autoplay switching to another video');
assert.match(navigation,/Подтвердите, что это вы/,'Channel navigation must fail clearly on account challenge');

// Exercise the existing failure capture offline; no browser, network, or diagnostic files.
(async()=>{
  const vm=require('vm');
  let saved, screenshot;
  const button={tagName:'BUTTON',id:'',parentElement:{tagName:'YT-LIKE-BUTTON-VIEW-MODEL'},
    getAttribute:name=>({'class':'ytSpecButtonShapeNextHost','aria-pressed':'false'}[name]??null),
    hasAttribute:()=>false,getClientRects:()=>[{}]};
  const video={paused:false,autoplay:false,currentTime:138,duration:155,ended:false,readyState:4};
  const document={querySelector:()=>null,querySelectorAll:selector=>{
    if(selector==='video')return [video];
    if(selector.startsWith('#actions button'))return [button];
    return [];
  }};
  const diagnosticFs={mkdirSync:()=>{},writeFileSync:(file,text)=>{saved={file,data:JSON.parse(text)};}};
  const capture=vm.runInNewContext('('+navigation.slice(navigation.indexOf('async function captureNavigationFailure('),navigation.indexOf('\nasync function openChannelsWatch('))+')',{
    require:name=>name==='fs'?diagnosticFs:require(name),process:{pid:42},URL
  });
  const page={isClosed:()=>false,url:()=> 'https://www.youtube.com/watch?v=LGE1SgZg-8s',
    evaluate:async fn=>vm.runInNewContext('('+fn.toString()+')()',{
      document,window:{ytInitialPlayerResponse:{videoDetails:{videoId:'LGE1SgZg-8s'}}},
      getComputedStyle:()=>({visibility:'visible'})
    }),screenshot:async opts=>{screenshot=opts.path;}};
  await capture(page,{ownerProfileId:'836981176'},new Error('Не найдена кнопка «Нравится».'),'diagnostics',()=>{});
  assert.equal(saved.data.error,'Не найдена кнопка «Нравится».');
  assert.equal(saved.data.videoId,'LGE1SgZg-8s');
  assert.equal(saved.data.state.videos[0].currentTime,138);
  assert.equal(saved.data.state.videos[0].duration,155);
  assert.equal(saved.data.state.videos[0].paused,false);
  assert.equal(saved.data.state.actionButtons[0].parentTag,'yt-like-button-view-model');
  assert.equal(saved.data.state.actionButtons[0].ariaLabel,'');
  assert.equal(saved.data.state.actionButtons[0].ariaPressed,'false');
  assert.equal(saved.data.state.actionButtons[0].visible,true);
  assert.equal(screenshot,saved.file.replace(/\.json$/,'.png'));
  video.duration=Infinity;
  await capture(page,{},new Error('failure'),'diagnostics',()=>{});
  assert.equal(saved.data.state.videos[0].duration,null);
  console.log('OK: watch_regressions.js (navigation, selection, profile preservation, failure diagnostics)');
})().catch(e=>{console.error(e);process.exitCode=1;});
