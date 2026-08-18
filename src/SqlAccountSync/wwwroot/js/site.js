(() => {
  const shell = document.querySelector(".app-shell");
  if (!shell) return;

  const toggle = shell.querySelector("[data-nav-toggle]");
  const backdrop = shell.querySelector(".nav-backdrop");
  const closers = shell.querySelectorAll("[data-nav-close]");

  const setOpen = (open) => {
    shell.classList.toggle("nav-open", open);
    document.body.classList.toggle("nav-locked", open);
    if (toggle) toggle.setAttribute("aria-expanded", open ? "true" : "false");
    if (toggle) toggle.setAttribute("aria-label", open ? "Close menu" : "Open menu");
    if (backdrop) backdrop.hidden = !open;
  };

  toggle?.addEventListener("click", () => setOpen(!shell.classList.contains("nav-open")));
  closers.forEach((el) => el.addEventListener("click", () => setOpen(false)));
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") setOpen(false);
  });
})();

(() => {
  const setReveal = (target, open) => {
    target.hidden = !open;
    document.querySelectorAll(`[data-reveal="#${target.id}"]`).forEach((btn) => {
      btn.setAttribute("aria-expanded", open ? "true" : "false");
    });
  };

  document.querySelectorAll("[data-reveal]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const target = document.querySelector(btn.getAttribute("data-reveal"));
      if (!target) return;
      setReveal(target, target.hidden);
      if (!target.hidden) target.querySelector("select, input")?.focus();
    });
  });

  document.querySelectorAll("[data-reveal-hide]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const target = document.querySelector(btn.getAttribute("data-reveal-hide"));
      if (target) setReveal(target, false);
    });
  });
})();

(() => {
  const activate = (tabId) => {
    document.querySelectorAll("[data-tabs] [data-tab]").forEach((link) => {
      link.classList.toggle("active", link.getAttribute("data-tab") === tabId);
    });
    document.querySelectorAll("[data-tab-panel]").forEach((panel) => {
      panel.hidden = panel.getAttribute("data-tab-panel") !== tabId;
    });
    const url = new URL(window.location.href);
    url.searchParams.set("tab", tabId);
    history.replaceState(null, "", url);
  };

  document.querySelectorAll("[data-tabs] [data-tab]").forEach((link) => {
    link.addEventListener("click", (e) => {
      e.preventDefault();
      activate(link.getAttribute("data-tab"));
    });
  });
})();
