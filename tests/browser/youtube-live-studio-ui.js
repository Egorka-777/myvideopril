"use strict";
// Local Studio fixture, NOT a test of real Google accounts or current production Studio markup.
const assert = require("assert"), fs = require("fs"), os = require("os"), path = require("path");
const { chromium } = require("../../tools/uploader/node_modules/playwright-core");
const { NativeStudio } = require("../../tools/uploader/worker-youtube-live-session");
const video = "abcdefghijk", channel = "UCfixture";
const state = { title: "", description: "", tags: [], privacy: "private", kids: false, exists: false, live: false, ended: false, keyName: "", thumbnail: "https://i.ytimg.com/default.jpg" };
function html(url) {
  const mode = url.includes("/manage") ? "manage" : url.endsWith("/edit") ? "edit" : "room";
  return `<!doctype html><html><head><meta charset="utf-8"></head><body><div id="channel-name">Fixture channel</div><main></main><script>
  const state=${JSON.stringify(state)}, mode=${JSON.stringify(mode)};
  window.ytcfg={get:k=>k==='DELEGATED_SESSION_ID'?${JSON.stringify(channel)}:''};
  const main=document.querySelector('main');
  const button=(text,fn,parent=main)=>{const b=document.createElement('button');b.textContent=text;b.onclick=fn;parent.append(b);return b;};
  function dialog(){const d=document.createElement('div');d.setAttribute('role','dialog');document.body.append(d);return d;}
  const save=async()=>{await fetch('/fixture/save',{method:'POST',body:JSON.stringify(state)});};
  function details(d,creation){
    const t=document.createElement('input');t.name='title';t.value=state.title;d.append(t);
    const desc=document.createElement('textarea');desc.name='description';desc.value=state.description;d.append(desc);
    function radio(label,group,value,checked){const r=document.createElement('input');r.type='radio';r.name=group;r.setAttribute('aria-label',label);r.checked=checked;r.onchange=()=>{if(group==='audience')state.kids=value;else state.privacy=value;};d.append(r);}
    radio("Yes, it's made for kids",'audience',true,state.kids);radio("No, it's not made for kids",'audience',false,!state.kids);
    const software=document.createElement('span');software.textContent='Streaming software';d.append(software);
    if(creation){button('Next',()=>{state.title=t.value;state.description=desc.value;d.innerHTML='';radio('Private','privacy','private',true);button('Schedule stream',async()=>{state.exists=true;await save();d.remove();location.href='/channel/${channel}/livestreaming/manage';},d);},d);return;}
    for(const [v,label] of [['private','Private'],['public','Public'],['unlisted','Unlisted']])radio(label,'privacy',v,state.privacy===v);
    const tags=document.createElement('div');tags.id='tags-container';const input=document.createElement('input');input.name='tags';tags.append(input);d.append(tags);
    function drawTags(){for(const chip of tags.querySelectorAll('[role=listitem]'))chip.remove();for(const value of state.tags){const chip=document.createElement('span');chip.setAttribute('role','listitem');chip.append(value);button('Remove',()=>{state.tags=state.tags.filter(x=>x!==value);drawTags();},chip).setAttribute('aria-label','Remove');tags.insertBefore(chip,input);}}
    drawTags();input.onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();state.tags.push(input.value);input.value='';drawTags();}};
    const editor=document.createElement('div');editor.id='thumbnail-editor';const image=document.createElement('img');image.src=state.thumbnail;editor.append(image);d.append(editor);
    const file=document.createElement('input');file.type='file';file.accept='image/png';file.onchange=()=>{state.thumbnail='https://i.ytimg.com/selected.jpg';image.src=state.thumbnail;};d.append(file);
    button('Save',async()=>{state.title=t.value;state.description=desc.value;await save();d.remove();},d);button('Cancel',()=>d.remove(),d);
  }
  function render(){main.innerHTML='';
    if(mode==='manage'){button('Schedule stream',()=>details(dialog(),true));if(state.deleted)main.append('Video deleted');if(state.exists){const row=document.createElement('div');row.setAttribute('role','row');const a=document.createElement('a');a.href='/video/${video}/livestreaming';a.textContent=state.title;row.append(a);main.append(row);}else main.append('No upcoming streams');return;}
    if(mode==='edit'){button('Options',()=>button('Delete forever',()=>{const d=dialog();const box=document.createElement('input');box.type='checkbox';d.append(box);button('Delete forever',async()=>{if(!box.checked)return;state.exists=false;state.deleted=true;await save();location.href='/channel/${channel}/livestreaming/manage';},d);}));return;}
    button('Edit',()=>details(dialog(),false));
    const select=document.createElement('button');select.id='stream-key-select';select.setAttribute('aria-label','Stream key');select.textContent=state.keyName||'Default';select.onclick=()=>button('Create new stream key',()=>{const d=dialog(),i=document.createElement('input');i.name='name';d.append(i);button('Create',async()=>{state.keyName=i.value;await save();select.textContent=state.keyName;d.remove();},d);});main.append(select);
    for(const [label,value] of [['Stream key (paste in encoder)','fixture-key-1234'],['Stream URL','rtmp://a.rtmp.youtube.com/live2']]){const row=document.createElement('div');const l=document.createElement('span');l.textContent=label;row.append(l);button('Copy',()=>navigator.clipboard.writeText(value),row);main.append(row);}
    if(state.ended){main.append('Stream finished');return;}
    if(state.live)button('End stream',()=>{const d=dialog();button('End stream',async()=>{state.live=false;state.ended=true;await save();d.remove();render();},d);});
    else button('Go live',async()=>{state.live=true;await save();render();});
  }render();
  </script></body></html>`;
}
(async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "vb-studio-ui-"));
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage(); page.setDefaultTimeout(3000);
    await page.route("**/*", async route => {
      const u = new URL(route.request().url());
      if (u.hostname !== "studio.youtube.com") return route.fulfill({ status: 200, body: "" });
      if (u.pathname === "/fixture/save") { Object.assign(state, JSON.parse(route.request().postData())); return route.fulfill({ status: 200, body: "{}" }); }
      return route.fulfill({ contentType: "text/html", body: html(u.pathname) });
    });
    const ui = new NativeStudio(page, channel);
    const options = { title: "Эфир — проверка", description: "Описание эфира", tags: ["one", "два"], privacy: "unlisted", madeForKids: false, thumbnail: path.join(root, "thumb.png") };
    fs.writeFileSync(options.thumbnail, Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lX0AAAAASUVORK5CYII=", "base64"));
    assert.equal(await ui.create("VideoBatch live " + "a".repeat(32), options), video);
    assert.equal(state.privacy, "private");
    await ui.open(video); const ingest = await ui.ingest("VideoBatch live unique-key"); assert.equal(ingest.key, "fixture-key-1234");
    assert.equal(await page.evaluate(() => '__vbCopied' in window), false, "secret must be removed from task page");
    await ui.configure(options); assert.equal(state.title, options.title); assert.deepEqual(state.tags, options.tags); assert.equal(state.privacy, "unlisted");
    assert.equal(await ui.state(), "ready"); assert(await ui.canStart());
    await ui.start(); assert.equal(await ui.state(), "live");
    await ui.complete(true); assert.equal(await ui.state(), "complete"); assert(state.exists, "completed archive retained");
    state.ended = false; await ui.open(video); await ui.complete(false); assert.equal(state.exists, false, "only unstarted own event removed");
    await page.goto('https://studio.youtube.com/video/zzzzzzzzzzz/livestreaming'); await assert.rejects(ui.state(), /другую страницу/);
    console.log("OK: Native Studio DOM fixture (create, private staging, unique key, copy, metadata readback, go live, stop, archive, upcoming removal)");
  } finally { await browser.close(); fs.rmSync(root, { recursive: true, force: true }); }
})().catch(e => { console.error(e); process.exitCode = 1; });
