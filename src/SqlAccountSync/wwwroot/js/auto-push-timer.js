(function () {
  const el = document.querySelector("[data-auto-push-timer]");
  if (!el) return;

  const statusUrl = el.getAttribute("data-status-url") || "";
  let active = el.getAttribute("data-active") === "true";
  let nextDueMs = parseLong(el.getAttribute("data-next-due"));
  let refreshing = false;

  function parseLong(raw) {
    const n = Number(raw);
    return Number.isFinite(n) && n > 0 ? n : null;
  }

  function format(seconds) {
    const s = Math.max(0, seconds | 0);
    const m = Math.floor(s / 60);
    const r = s % 60;
    return m > 0 ? m + ":" + String(r).padStart(2, "0") : String(r) + "s";
  }

  function paint(remaining) {
    if (!active) {
      el.hidden = true;
      el.textContent = "";
      return;
    }
    el.hidden = false;
    el.textContent =
      remaining <= 0 ? "Auto push now…" : "Auto push in " + format(remaining);
  }

  async function refresh() {
    if (!statusUrl || refreshing) return;
    refreshing = true;
    try {
      const res = await fetch(statusUrl, {
        headers: { Accept: "application/json" },
        credentials: "same-origin"
      });
      if (!res.ok) return;
      const data = await res.json();
      active = !!data.active;
      nextDueMs = parseLong(data.nextDueUnixMs);
      el.setAttribute("data-active", active ? "true" : "false");
      if (nextDueMs) el.setAttribute("data-next-due", String(nextDueMs));
      else el.removeAttribute("data-next-due");
    } catch (_) {
      /* keep last known countdown */
    } finally {
      refreshing = false;
    }
  }

  function tick() {
    if (!active) {
      paint(0);
      return;
    }
    if (!nextDueMs) {
      paint(0);
      refresh();
      return;
    }
    const remaining = Math.ceil((nextDueMs - Date.now()) / 1000);
    paint(remaining);
    if (remaining <= 0) refresh();
  }

  tick();
  setInterval(tick, 250);
  setInterval(refresh, 15000);
})();
