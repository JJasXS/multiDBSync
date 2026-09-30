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
  document.addEventListener("click", (e) => {
    document.querySelectorAll("details.kebab[open]").forEach((menu) => {
      if (!menu.contains(e.target)) menu.removeAttribute("open");
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
  document.querySelectorAll("[data-copy-field]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const input = btn.closest(".copy-input")?.querySelector("input");
      const text = (input?.value || input?.placeholder || "").trim();
      if (!text) return;
      try {
        await navigator.clipboard.writeText(text);
      } catch {
        input?.focus();
        input?.select();
        document.execCommand("copy");
      }
      btn.setAttribute("title", "Copied");
      btn.setAttribute("aria-label", "Copied");
      window.setTimeout(() => {
        btn.setAttribute("title", "Copy");
        btn.setAttribute("aria-label", "Copy credential reference");
      }, 1200);
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

  const bundle = `proacc/tenant/${tenant}/sqlApiCredentials`;
  const legacy = `proacc/tenant/${tenant}/sqlApiCredentials/`;
  const visible = (v) => (v || "").trim().replace(/^sm:/i, "");

  const sync = () => {
    const current = visible(credInput.value);
    const managed =
      !current ||
      current.includes("{CODE}") ||
      current.includes("{DBID}") ||
      current === bundle ||
      current.startsWith(legacy);
    if (!managed) return;
    credInput.value = bundle;
  };

  codeInput.addEventListener("input", sync);
  sync();
})();

(() => {
  document.querySelectorAll("[data-check-all]").forEach((master) => {
    master.addEventListener("change", () => {
      const group = master.getAttribute("data-check-all");
      document.querySelectorAll(`[data-check-group="${group}"]`).forEach((cb) => {
        const row = cb.closest("tr");
        if (row && row.hidden) return;
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

(() => {
  document.querySelectorAll("[data-master-menu]").forEach((root) => {
    const btn = root.querySelector("[data-master-menu-toggle]");
    const panel = root.querySelector("[data-master-menu-panel]");
    if (!btn || !panel) return;

    const setOpen = (open) => {
      panel.hidden = !open;
      btn.setAttribute("aria-expanded", open ? "true" : "false");
      root.classList.toggle("is-open", open);
    };

    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      setOpen(panel.hidden);
    });

    document.addEventListener("click", (e) => {
      if (!root.contains(e.target)) setOpen(false);
    });

    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape") setOpen(false);
    });
  });
})();

(() => {
  const dialog = document.getElementById("appConfirm");
  if (!dialog) return;

  const titleEl = dialog.querySelector("[data-confirm-title]");
  const messageEl = dialog.querySelector("[data-confirm-message]");
  const okBtn = dialog.querySelector("[data-confirm-ok]");
  const cancelBtn = dialog.querySelector("[data-confirm-cancel]");
  const closeBtn = dialog.querySelector("[data-confirm-close]");
  let settle = null;

  const finish = (ok) => {
    if (dialog.open) dialog.close();
    const done = settle;
    settle = null;
    if (done) done(ok);
  };

  cancelBtn?.addEventListener("click", () => finish(false));
  closeBtn?.addEventListener("click", () => finish(false));
  okBtn?.addEventListener("click", () => finish(true));
  dialog.addEventListener("cancel", (e) => {
    e.preventDefault();
    finish(false);
  });
  dialog.addEventListener("click", (e) => {
    if (e.target === dialog) finish(false);
  });

  const ask = ({ title, message, okLabel, cancelLabel, danger }) =>
    new Promise((resolve) => {
      settle = resolve;
      if (titleEl) titleEl.textContent = title || "Please confirm";
      if (messageEl) messageEl.textContent = message || "";
      if (okBtn) {
        okBtn.textContent = okLabel || "Confirm";
        okBtn.className = danger ? "btn danger" : "btn";
      }
      if (cancelBtn) cancelBtn.textContent = cancelLabel || "Cancel";
      if (typeof dialog.showModal === "function") dialog.showModal();
      else dialog.setAttribute("open", "");
      okBtn?.focus();
    });

  window.appConfirm = ask;

  document.addEventListener(
    "click",
    async (e) => {
      const el = e.target.closest("[data-confirm]");
      if (!el || el.tagName === "FORM") return;
      if (el.dataset.confirmSkip === "1") {
        delete el.dataset.confirmSkip;
        return;
      }

      e.preventDefault();
      e.stopImmediatePropagation();

      const ok = await ask({
        title: el.getAttribute("data-confirm-title") || "Please confirm",
        message: el.getAttribute("data-confirm") || "",
        okLabel: el.getAttribute("data-confirm-ok") || "Confirm",
        cancelLabel: el.getAttribute("data-confirm-cancel") || "Cancel",
        danger: el.hasAttribute("data-confirm-danger"),
      });
      if (!ok) return;

      el.dataset.confirmSkip = "1";
      el.click();
    },
    true
  );

  document.addEventListener(
    "submit",
    async (e) => {
      const form = e.target;
      if (!(form instanceof HTMLFormElement) || !form.hasAttribute("data-confirm")) return;
      if (form.dataset.confirmSkip === "1") {
        delete form.dataset.confirmSkip;
        return;
      }

      e.preventDefault();
      e.stopImmediatePropagation();

      const ok = await ask({
        title: form.getAttribute("data-confirm-title") || "Please confirm",
        message: form.getAttribute("data-confirm") || "",
        okLabel: form.getAttribute("data-confirm-ok") || "Confirm",
        cancelLabel: form.getAttribute("data-confirm-cancel") || "Cancel",
        danger: form.hasAttribute("data-confirm-danger"),
      });
      if (!ok) return;

      form.dataset.confirmSkip = "1";
      if (typeof form.requestSubmit === "function" && e.submitter instanceof HTMLElement)
        form.requestSubmit(e.submitter);
      else form.submit();
    },
    true
  );
})();

function planLinkIds(root) {
  const ids = new Set((root.getAttribute("data-linked-ids") || "").split(",").filter(Boolean));
  const master = root.querySelector("[data-plan-master]");
  if (master && master.value) ids.add(master.value);
  root.querySelectorAll('input[name="SubDbIds"]').forEach((box) => {
    const row = box.closest(".sub-multi-row");
    if (box.checked && !(row && row.hidden)) ids.add(box.value);
  });
  return ids;
}

function trimPlanLinks(root, cap) {
  const boxes = [...root.querySelectorAll('input[name="SubDbIds"]')].filter((box) => box.checked);
  let trimmed = false;
  while (planLinkIds(root).size > cap && boxes.length) {
    boxes.pop().checked = false;
    trimmed = true;
  }
  return trimmed;
}

(() => {
  document.querySelectorAll("[data-plan-cap]").forEach((root) => {
    const cap = Number(root.getAttribute("data-plan-cap"));
    if (!cap) return;
    const hint = root.querySelector("[data-plan-hint]");
    root.addEventListener("change", () => {
      const trimmed = trimPlanLinks(root, cap);
      if (hint) hint.hidden = !trimmed && planLinkIds(root).size <= cap;
    });
  });
})();
