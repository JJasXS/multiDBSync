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
