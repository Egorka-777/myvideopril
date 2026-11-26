"use strict";
// Generic rendered media/semantic articles only. Platform UI still requires live verification.
(function (root) {
  const visible = element => {
    const r = element.getBoundingClientRect();
    return r.width > 0 && r.height > 0 && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth && getComputedStyle(element).visibility !== "hidden";
  };
  const media = () => [...document.querySelectorAll("video")].filter(visible).sort((a, b) => {
    const area = e => { const r = e.getBoundingClientRect(); return Math.min(r.bottom, innerHeight) - Math.max(0, r.top); };
    return area(b) - area(a);
  })[0];
  let active = null, previous = null, watched = 0;
  function read() {
    const video = media();
    if (video !== active) { active = video; previous = null; watched = 0; }
    if (!video) return { media: false, article: [...document.querySelectorAll("article,[role='article']")].some(visible) };
    const current = Number(video.currentTime);
    const delta = previous == null ? 0 : current - previous;
    if (!video.paused && delta > 0 && delta < 2) watched += delta;
    previous = current;
    return { media: true, ready: video.readyState >= 2, duration: Number.isFinite(video.duration) ? video.duration : 0, watched, paused: video.paused, ended: video.ended };
  }
  async function command(action, remaining) {
    if (action === "read") return read();
    if (action === "plan") {
      const state = read();
      if (state.media && state.ready && state.duration > 0) return root.videoBatchWarmupPlan(state.duration, Math.min(60, remaining), true);
      return null;
    }
    if (action === "play") { if (active && active.paused && !active.ended) await active.play(); return true; }
    if (action === "pause") { if (active) active.pause(); previous = null; return true; }
    if (action === "scroll") {
      const target = active || [...document.querySelectorAll("article,[role='article']")].find(visible);
      if (!target) throw Error("Лента не показала видимого видео или публикации.");
      if (active) active.pause();
      let scroll = target.parentElement;
      while (scroll && !(scroll.scrollHeight > scroll.clientHeight + 20 && /auto|scroll/.test(getComputedStyle(scroll).overflowY))) scroll = scroll.parentElement;
      scroll = scroll || document.scrollingElement;
      const before = scroll.scrollTop;
      scroll.scrollBy({ top: Math.max(240, Math.min(innerHeight, scroll.clientHeight) * (0.8 + Math.random() * 0.35)), behavior: "instant" });
      if (scroll.scrollTop === before) throw Error("Лента не прокручивается; прогрев остановлен.");
      active = null; previous = null; watched = 0;
      return true;
    }
    throw Error("Неизвестное действие прогрева.");
  }
  root.videoBatchFeedWarmup = command;
})(globalThis);
