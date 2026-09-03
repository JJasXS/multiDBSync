(() => {
  const shell = document.querySelector(".app-shell");
  if (!shell) return;

  const toggle = shell.querySelector("[data-nav-toggle]");
  const backdrop = shell.querySelector(".nav-backdrop");
  const closers = shell.querySelectorAll("[data-nav-close]");
  const mq = window.matchMedia("(max-width: 900px)");

  const setOpen = (open) => {
    if (!mq.matches) open = false;
    shell.classList.toggle("nav-open", open);
    document.body.classList.toggle("nav-locked", open);
    if (toggle) toggle.setAttribute("aria-expanded", open ? "true" : "false");
    if (toggle) toggle.setAttribute("aria-label", open ? "Close menu" : "Open menu");
    if (backdrop) backdrop.hidden = !open;
  };

  toggle?.addEventListener("click", () => setOpen(!shell.classList.contains("nav-open")));
  closers.forEach((el) => el.addEventListener("click", () => setOpen(false)));
  shell.querySelectorAll(".nav a").forEach((link) => {
    link.addEventListener("click", () => setOpen(false));
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") setOpen(false);
  });
  mq.addEventListener("change", () => setOpen(false));
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

(() => {
  document.querySelectorAll("[data-secret-toggle]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const wrap = btn.closest(".secret-input");
      const input = wrap?.querySelector("input");
      if (!input) return;
      const show = input.type === "password";
      input.type = show ? "text" : "password";
      btn.setAttribute("aria-label", show ? "Hide secret reference" : "Show secret reference");
      const eye = btn.querySelector(".icon-eye");
      const eyeOff = btn.querySelector(".icon-eye-off");
      if (eye) eye.hidden = show;
      if (eyeOff) eyeOff.hidden = !show;
    });
  });
})();

(() => {
  const codeInput = document.querySelector("[data-db-code]");
  const credInput = document.querySelector("[data-api-cred-ref]");
  if (!codeInput || !credInput) return;
  if (codeInput.getAttribute("data-sync-api-cred") !== "true") return;

  const tenant = (codeInput.getAttribute("data-tenant-code") || "").trim();
  if (!tenant) return;

  const prefix = `sm:proacc/tenant/${tenant}/sqlApiCredentials/`;
  const sanitize = (v) => (v || "").trim().toUpperCase().replace(/[^A-Z0-9._@+=-]/g, "");

  const sync = () => {
    const current = (credInput.value || "").trim();
    const managed =
      !current ||
      current.includes("{CODE}") ||
      current.includes("{DBID}") ||
      current.startsWith(prefix);
    if (!managed) return;
    const code = sanitize(codeInput.value);
    credInput.value = code ? prefix + code : prefix + "{CODE}";
  };

  codeInput.addEventListener("input", sync);
  sync();
})();

(() => {
  document.querySelectorAll("[data-check-all]").forEach((master) => {
    master.addEventListener("change", () => {
      const group = master.getAttribute("data-check-all");
      document.querySelectorAll(`[data-check-group="${group}"]`).forEach((cb) => {
        cb.checked = master.checked;
      });
    });
  });
})();

(() => {
  const input = document.getElementById("settingsSearch");
  const list = document.querySelector("[data-settings-list]");
  if (!input || !list) return;

  const items = Array.from(list.querySelectorAll("[data-settings-item]"));
  const empty = list.querySelector("[data-settings-empty]");

  const apply = () => {
    const q = (input.value || "").trim().toLowerCase();
    let visible = 0;
    items.forEach((el) => {
      const hay = (el.getAttribute("data-settings-search") || "").toLowerCase();
      const show = !q || hay.includes(q);
      el.hidden = !show;
      if (show) visible += 1;
    });
    if (empty) empty.hidden = visible > 0;
  };

  input.addEventListener("input", apply);
  apply();
})();

(() => {
  const root = document.getElementById("toast-root");
  if (!root) return;

  document.querySelectorAll("[data-auto-toast]").forEach((source) => {
    const variant = source.getAttribute("data-toast-variant") || "warn";
    const duration = Number(source.getAttribute("data-toast-duration") || 5000);

    const toast = document.createElement("div");
    toast.className = `toast toast--${variant}`;
    toast.setAttribute("role", "status");
    toast.innerHTML = source.innerHTML;
    root.appendChild(toast);

    requestAnimationFrame(() => {
      toast.classList.add("is-visible");
    });

    const dismiss = () => {
      if (toast.classList.contains("is-leaving")) return;
      toast.classList.remove("is-visible");
      toast.classList.add("is-leaving");
      toast.addEventListener("transitionend", () => toast.remove(), { once: true });
    };

    toast.querySelector(".toast-dismiss")?.addEventListener("click", dismiss);
    window.setTimeout(dismiss, duration);
    source.remove();
  });
})();
