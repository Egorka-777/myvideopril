"use strict";
(function (root) {
  function planWatch(duration, remainingSec, shorts, random = Math.random) {
    if (!(duration > 0) || !(remainingSec > 0)) return null;
    const initial = Math.min(5, duration);
    if (remainingSec < initial) return null;
    const mode = ["short", "long", "full"][Math.floor(random() * 3)];
    const fraction = mode === "full" ? 1 : mode === "long" ? 0.4 + random() * 0.4 : 0.05 + random() * 0.15;
    const seconds = Math.min(remainingSec, duration, Math.max(initial, duration * fraction));
    const seekTo = !shorts && mode !== "full" && duration > initial + seconds && random() < 0.5
      ? initial + random() * (duration - seconds - initial) : null;
    return { mode, initial, seconds, seekTo };
  }
  if (typeof module !== "undefined" && module.exports) module.exports = { planWatch };
  else root.videoBatchWarmupPlan = planWatch;
})(typeof globalThis !== "undefined" ? globalThis : this);
