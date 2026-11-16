"use strict";
// Read-only inspection of an already-open Studio profile. Never selects files or posts.
const {chromium}=require("../tools/uploader/node_modules/playwright-core");
(async()=>{
  const browser=await chromium.connectOverCDP(process.argv[2],{timeout:10000});
  try {
    for(const page of browser.contexts()[0].pages()) {
      if(!page.url().includes("tiktokstudio"))continue;
      console.log(JSON.stringify({url:page.url(),dom:await page.evaluate(()=>({
        text:document.body.innerText.slice(0,18000),
        rows:[...document.querySelectorAll('tbody tr,[role="row"],[data-post-id]')].slice(0,3).map(e=>e.outerHTML),
        controls:[...document.querySelectorAll('button,[role="button"],a')].map(e=>({text:e.innerText,aria:e.getAttribute("aria-label"),cls:e.className,href:e.getAttribute("href"),disabled:e.getAttribute("disabled")}))
      }))}));
    }
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
