(function () {
  const el = document.querySelector("[data-auto-push-timer]");
  if (!el) return;

  const statusUrl = el.getAttribute("data-status-url") || "/push?handler=AutoPushStatus";
  let active = el.getAttribute("data-active") === "true";
  let intervalSec = Math.max(15, Number(el.getAttribute("data-interval")) || 60);
  let nextDueMs = parseDue(el.getAttribute("data-next-due"));
  let refreshing = false;

  function parseDue(raw) {
    const n = Number(raw);
    return Number.isFinite(n) && n > 0 ? n : null;
  }

  function ensureDue() {
    if (!active) return;
    if (!nextDueMs || nextDueMs <= Date.now())
      nextDueMs = Date.now() + intervalSec * 1000;
  }

  function format(seconds) {
    const s = Math.max(0, seconds | 0);
    const m = Math.floor(s / 60);
    const r = s % 60;
    return m + ":" + String(r).padStart(2, "0");
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
    if (refreshing) return;
    refreshing = true;
    try {
      const res = await fetch(statusUrl, {
        headers: { Accept: "application/json" },
        credentials: "same-origin",
        cache: "no-store"
      });
      if (!res.ok) return;
      const ct = (res.headers.get("content-type") || "").toLowerCase();
      if (!ct.includes("json")) return;
      const data = await res.json();
      active = !!data.active;
      if (Number(data.intervalSeconds) > 0)
        intervalSec = Math.max(15, Number(data.intervalSeconds));
      const serverDue = parseDue(data.nextDueUnixMs);
      if (active && serverDue && serverDue > Date.now() - 1000)
        nextDueMs = serverDue;
      else if (active)
        ensureDue();
      el.setAttribute("data-active", active ? "true" : "false");
      el.setAttribute("data-interval", String(intervalSec));
    } catch (_) {
      /* keep local countdown */
    } finally {
      refreshing = false;
    }
  }

  function tick() {
    if (!active) {
      paint(0);
      return;
    }
    ensureDue();
    const remaining = Math.ceil((nextDueMs - Date.now()) / 1000);
    if (remaining <= 0) {
      paint(0);
      nextDueMs = Date.now() + intervalSec * 1000;
      refresh();
      return;
    }
    paint(remaining);
  }

  ensureDue();
  tick();
  setInterval(tick, 200);
  setInterval(refresh, 10000);
  refresh();
})();
