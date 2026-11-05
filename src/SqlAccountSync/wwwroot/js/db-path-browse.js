async function dbPathBrowseFolder(input, hint) {
  const kind = (input.getAttribute("data-path-kind") || "fdb").toLowerCase();
  dbPathHint(hint, "", kind);
  const findPage = input.getAttribute("data-find-page");
  const url = findPage ? new URL(findPage, window.location.origin) : new URL(window.location.href);
  url.searchParams.set("handler", "Browse");
  url.searchParams.set("kind", kind);
  const current = (input.value || "").trim();
  if (current) url.searchParams.set("dir", current);

  let data;
  try {
    const res = await fetch(url, { headers: { Accept: "application/json" } });
    if (!res.ok) throw new Error("Could not open that folder.");
    data = await res.json();
  } catch (err) {
    dbPathHint(hint, err.message || "Could not open that folder.", kind);
    return;
  }

  const directory = data.directory || "";
  const paths = data.paths || [];
  if (!directory) {
    dbPathHint(hint, kind === "dcf"
      ? "No SQL Account Share folder found for .DCF files."
      : "No allowed folder found for database files.", kind);
    return;
  }

  const note = paths.length === 0
    ? `No matching files in ${directory}.`
    : `Files in ${directory}`;
  dbPathChoose(input, paths, note, directory);
}

function dbPathChoose(input, paths, note, directory) {
  const dialog = document.getElementById("dbPathMatches");
  const list = dialog?.querySelector("[data-db-path-matches]");
  const hint = dialog?.querySelector("[data-db-path-folder]");
  const title = dialog?.querySelector("#dbPathMatchesTitle");
  if (!dialog || !list) {
    if (paths[0]) {
      input.value = paths[0];
      input.dispatchEvent(new Event("change", { bubbles: true }));
    }
    return;
  }

  if (title) title.textContent = directory ? "Choose a file" : "Choose a path";
  if (hint) hint.textContent = note || "Pick the path to use.";
  list.replaceChildren();
  if (paths.length === 0) {
    const empty = document.createElement("li");
    empty.className = "db-path-empty";
    empty.textContent = "No files to show in this folder.";
    list.append(empty);
  } else {
    paths.forEach((path) => list.append(dbPathMatch(dialog, input, path)));
  }
  dialog.dataset.activeKind = (input.getAttribute("data-path-kind") || "fdb").toLowerCase();
  dialog._activeInput = input;
  dialog._activeHint = input.closest("[data-db-path-group]")?.querySelector("[data-db-path-hint]")
    || document.querySelector("[data-db-path-hint]");
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
    ? "Use the pin to open the Share folder and pick SQLACC.DCF (or Default.DCF)."
    : "Use the pin to choose the company .FDB.";
  hint.textContent = message || fallback;
}

function dbPathBindGroup(group) {
  const input = group.querySelector("[data-db-path]");
  const pin = group.querySelector("[data-db-path-pin]");
  const hint = group.querySelector("[data-db-path-hint]");
  if (!input || !pin) return;

  pin.addEventListener("click", () => {
    dbPathBrowseFolder(input, hint);
  });
}

function dbPathInit() {
  document.querySelectorAll("[data-db-path-group]").forEach(dbPathBindGroup);

  // Legacy single-picker pages (no group wrapper)
  if (!document.querySelector("[data-db-path-group]")) {
    const input = document.querySelector("[data-db-path]");
    const pin = document.querySelector("[data-db-path-pin]");
    const hint = document.querySelector("[data-db-path-hint]");
    if (input && pin) {
      pin.addEventListener("click", () => dbPathBrowseFolder(input, hint));
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
