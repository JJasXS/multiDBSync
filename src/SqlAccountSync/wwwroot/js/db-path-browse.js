async function dbPathPicked(input, picker, hint) {
  const file = picker.files && picker.files[0];
  if (!file) return;
  const kind = (input.getAttribute("data-path-kind") || "fdb").toLowerCase();
  dbPathHint(hint, "", kind);
  const findPage = input.getAttribute("data-find-page");
  const url = findPage ? new URL(findPage, window.location.origin) : new URL(window.location.href);
  url.searchParams.set("handler", "Find");
  url.searchParams.set("name", file.name);
  let data;
  try {
    const res = await fetch(url, { headers: { Accept: "application/json" } });
    if (!res.ok) throw new Error("Could not look up that file.");
    data = await res.json();
  } catch (err) {
    dbPathHint(hint, err.message || "Could not look up that file.", kind);
    return;
  }
  const paths = data.paths || [];
  if (paths.length === 1) {
    input.value = paths[0];
    input.dispatchEvent(new Event("change", { bubbles: true }));
    return;
  }
  if (paths.length === 0) {
    dbPathHint(hint, kind === "dcf"
      ? "That .DCF is not on this server. Choose SQLACC.DCF (or your company DCF)."
      : "That file is not on this server. Choose a company .FDB.", kind);
    return;
  }
  dbPathChoose(input, paths);
}

function dbPathChoose(input, paths) {
  const dialog = document.getElementById("dbPathMatches");
  const list = dialog?.querySelector("[data-db-path-matches]");
  if (!dialog || !list) {
    input.value = paths[0];
    input.dispatchEvent(new Event("change", { bubbles: true }));
    return;
  }
  list.replaceChildren();
  paths.forEach((path) => list.append(dbPathMatch(dialog, input, path)));
  if (typeof dialog.showModal === "function") dialog.showModal();
}

function dbPathMatch(dialog, input, path) {
  const li = document.createElement("li");
  const btn = document.createElement("button");
  btn.type = "button";
  btn.textContent = path;
  btn.addEventListener("click", () => {
    input.value = path;
    input.dispatchEvent(new Event("change", { bubbles: true }));
    dialog.close();
  });
  li.append(btn);
  return li;
}

function dbPathHint(hint, message, kind) {
  if (!hint) return;
  const fallback = kind === "dcf"
    ? "Use the pin to choose the SQL Account .DCF (not the .FDB)."
    : "Use the pin to choose the company .FDB.";
  hint.textContent = message || fallback;
}

function dbPathBindGroup(group) {
  const input = group.querySelector("[data-db-path]");
  const pin = group.querySelector("[data-db-path-pin]");
  const picker = group.querySelector("[data-db-path-file]");
  const hint = group.querySelector("[data-db-path-hint]");
  if (!input || !pin || !picker) return;

  pin.addEventListener("click", () => {
    picker.value = "";
    picker.click();
  });
  picker.addEventListener("change", () => dbPathPicked(input, picker, hint));
}

function dbPathInit() {
  document.querySelectorAll("[data-db-path-group]").forEach(dbPathBindGroup);

  // Legacy single-picker pages (no group wrapper)
  if (!document.querySelector("[data-db-path-group]")) {
    const input = document.querySelector("[data-db-path]");
    const pin = document.querySelector("[data-db-path-pin]");
    const picker = document.querySelector("[data-db-path-file]");
    const hint = document.querySelector("[data-db-path-hint]");
    if (input && pin && picker) {
      pin.addEventListener("click", () => {
        picker.value = "";
        picker.click();
      });
      picker.addEventListener("change", () => dbPathPicked(input, picker, hint));
    }
  }

  const dialog = document.getElementById("dbPathMatches");
  dialog?.querySelector("[data-db-path-close]")?.addEventListener("click", () => dialog.close());
  dialog?.addEventListener("cancel", (e) => {
    e.preventDefault();
    dialog.close();
  });
}

dbPathInit();
