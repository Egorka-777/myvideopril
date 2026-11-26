(() => {
  if (globalThis.videoBatchInstagramProfileEdit) return;
  const visible = (el) => !!el && !!(el.offsetWidth || el.offsetHeight || el.getClientRects().length) && getComputedStyle(el).visibility !== "hidden";
  const textOf = (el) => [el?.getAttribute?.("aria-label"), el?.getAttribute?.("title"), el?.innerText, el?.textContent].filter(Boolean).join(" ").trim();
  const norm = (s) => (s || "").replace(/\s+/g, " ").trim();
  const pick = (selectors) => {
    for (const selector of selectors) {
      const el = document.querySelector(selector);
      if (visible(el)) return el;
    }
    return null;
  };
  const byLabel = (patterns) => {
    const nodes = Array.from(document.querySelectorAll("label,span,div,h1,h2,h3")).filter(visible);
    for (const node of nodes) {
      const label = norm(textOf(node));
      if (!label) continue;
      for (const pattern of patterns) {
        if (!pattern.test(label)) continue;
        const root = node.closest("section,form,div") || node.parentElement;
        if (!root) continue;
        const input = root.querySelector("input,textarea");
        if (visible(input)) return input;
      }
    }
    return null;
  };
  const readField = (el) => (el && "value" in el ? String(el.value) : "");
  const setField = (el, value) => {
    if (!el) throw new Error("edit_field_missing");
    el.focus();
    el.value = value;
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  };
  const formFields = () => {
    const name = pick(['input[name="full_name"]', 'input[name="first_name"]', 'input[autocomplete="name"]'])
      || byLabel([/^имя$/i, /^name$/i, /^полное имя$/i, /^full name$/i]);
    const bio = pick(['textarea[name="biography"]', 'textarea[autocomplete="off"]'])
      || byLabel([/^биография$/i, /^bio$/i, /^о себе$/i]);
    const file = pick(['input[type="file"]', 'input[accept*="image"]']);
    return { name, bio, file };
  };
  const clickFirst = (patterns) => {
    const buttons = Array.from(document.querySelectorAll("button,a,[role='button']")).filter(visible);
    for (const button of buttons) {
      const label = norm(textOf(button));
      for (const pattern of patterns) {
        if (pattern.test(label)) {
          button.click();
          return true;
        }
      }
    }
    return false;
  };
  const bodyText = () => norm(document.body?.innerText || "").slice(0, 4000);
  const authBlocked = () => /log in|sign in|войти|вход|password|пароль|checkpoint|challenge|captcha|код|code/i.test(bodyText())
    || document.querySelector('input[type="password"],input[autocomplete="one-time-code"]');
  globalThis.videoBatchInstagramProfileEdit = async (action, payload) => {
    if (authBlocked()) return { ok: false, error: "auth_required" };
    if (!/\/accounts\/edit/.test(location.pathname) && action !== "open") {
      return { ok: false, error: "wrong_page" };
    }
    if (action === "read") {
      const fields = formFields();
      if (!fields.name || !fields.bio) return { ok: false, error: "edit_form_missing" };
      return {
        ok: true,
        name: readField(fields.name),
        bio: readField(fields.bio),
        file: !!fields.file,
      };
    }
    if (action === "fill") {
      const fields = formFields();
      if (!fields.name || !fields.bio) return { ok: false, error: "edit_form_missing" };
      setField(fields.name, payload.name || "");
      setField(fields.bio, payload.bio || "");
      return { ok: true, name: readField(fields.name), bio: readField(fields.bio) };
    }
    if (action === "reveal_avatar") {
      clickFirst([/change profile photo/i, /сменить фото профиля/i, /изменить фото профиля/i, /добавить фото профиля/i]);
      await new Promise((resolve) => setTimeout(resolve, 600));
      const fields = formFields();
      return { ok: true, file: !!fields.file };
    }
    if (action === "submit") {
      if (!clickFirst([/^submit$/i, /^отправить$/i, /^сохранить$/i, /^готово$/i, /^done$/i])) {
        return { ok: false, error: "submit_missing" };
      }
      await new Promise((resolve) => setTimeout(resolve, 1200));
      const text = bodyText();
      if (/try again|ошибк|error|too long|слишком/i.test(text)) return { ok: false, error: "submit_failed", detail: text.slice(0, 240) };
      return { ok: true };
    }
    return { ok: false, error: "unknown_action" };
  };
})();