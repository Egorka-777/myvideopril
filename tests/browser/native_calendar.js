"use strict";
const assert=require("assert");
const {chromium}=require("../../tools/uploader/node_modules/playwright-core");
const {setCalendarSchedule}=require("../../tools/uploader/tiktok-studio-evidence");
async function fixture(page, anchor, rejectDate=false) {
  await page.setContent(`<div class="date-picker-input"><input readonly value="${anchor}"></div>
    <div class="calendar-wrapper" hidden><span class="month-title"></span><span class="arrow">Prev</span><span class="arrow">Next</span><div class="days-wrapper"></div></div>
    <div class="time-picker-input"><span>00:00</span></div>
    <div class="tiktok-timepicker-time-picker-container" hidden><div id="hours"></div><div id="minutes"></div></div>`);
  await page.evaluate(({anchor,rejectDate})=>{
    const q=s=>document.querySelector(s),pad=n=>String(n).padStart(2,"0");
    let d=new Date(anchor+"T12:00:00"),hour="00",minute="00";
    const render=()=>{
      q('.month-title').textContent=d.toLocaleString('en',{month:'long'});
      q('.days-wrapper').replaceChildren();
      for(let i=1;i<=new Date(d.getFullYear(),d.getMonth()+1,0).getDate();i++) {
        const el=document.createElement('span'); el.className='day valid';el.textContent=i;el.style.margin='5px';
        el.onclick=()=>{if(!rejectDate)q('.date-picker-input input').value=`${d.getFullYear()}-${pad(d.getMonth()+1)}-${pad(i)}`;q('.calendar-wrapper').hidden=true;};
        q('.days-wrapper').append(el);
      }
    };
    q('.date-picker-input').onclick=()=>{render();q('.calendar-wrapper').hidden=false;};
    document.querySelectorAll('.arrow').forEach((el,i)=>el.onclick=()=>{d.setDate(1);d.setMonth(d.getMonth()+(i?1:-1));render();});
    q('.time-picker-input').onclick=()=>{q('.tiktok-timepicker-time-picker-container').hidden=!q('.tiktok-timepicker-time-picker-container').hidden;};
    for(const [id,limit,step,cls] of [['hours',24,1,'tiktok-timepicker-left'],['minutes',60,5,'tiktok-timepicker-right']]) {
      for(let i=0;i<limit;i+=step){const el=document.createElement('span');el.className=cls;el.textContent=pad(i);el.style.margin='4px';
        el.onclick=()=>{if(id==='hours')hour=pad(i);else minute=pad(i);q('.time-picker-input span').textContent=hour+':'+minute;};q('#'+id).append(el);}
    }
  },{anchor,rejectDate});
}
(async()=>{
  const browser=await chromium.launch({headless:true});
  try {
    const page=await browser.newPage();
    for(const [anchor,date] of [['2026-10-01','2026-10-05'],['2026-10-31','2026-11-01'],['2026-12-31','2027-01-01']]) {
      await fixture(page,anchor);
      await setCalendarSchedule(page,{date,time:'23:55',year:Number(date.slice(0,4)),month:Number(date.slice(5,7)),day:Number(date.slice(8))});
      assert.equal(await page.locator('.date-picker-input input').inputValue(),date);
      assert.equal(await page.locator('.time-picker-input').innerText(),'23:55');
    }
    await fixture(page,'2026-10-01',true);
    await assert.rejects(setCalendarSchedule(page,{date:'2026-10-05',time:'23:55',year:2026,month:10,day:5}),/не подтвердил выбранную дату/);
    console.log('OK: native custom calendar DOM (same month, month/year rollover, rejected readback)');
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
