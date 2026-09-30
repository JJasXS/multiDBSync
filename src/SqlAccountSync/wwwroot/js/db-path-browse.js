async function dbPathLoad(dialog, input, path, useStart) {
  const url = new URL(window.location.href);
  url.searchParams.set("handler", "Browse");
  if (useStart) url.searchParams.delete("path");
  else url.searchParams.set("path", path || "");

  const error = dialog.querySelector("[data-db-path-error]");
  error.hidden = true;
  let data;
  try {
    const res = await fetch(url, { headers: { Accept: "application/json" } });
    if (!res.ok) throw new Error("Could not read that folder.");
    data = await res.json();
  } catch (err) {
    error.hidden = false;
    error.textContent = err.message || "Could not read that folder.";
    return;
  }
  dbPathShow(dialog, input, data);
}

function dbPathShow(dialog, input, data) {
  const current = dialog.querySelector("[data-db-path-current]");
  const error = dialog.querySelector("[data-db-path-error]");
  const up = dialog.querySelector("[data-db-path-up]");
  const list = dialog.querySelector("[data-db-path-list]");
  current.textContent = data.path || "This computer";
  error.hidden = !data.error;
  error.textContent = data.error || "";
  up.hidden = data.parent == null;
  up.onclick = () => dbPathLoad(dialog, input, data.parent || "", false);
  list.replaceChildren();
  const entries = data.entries || [];
  if (entries.length === 0) {
    const empty = document.createElement("li");
    empty.className = "muted db-path-empty";
    empty.textContent = "No folders or database files here.";
    list.append(empty);
    return;
  }
  entries.forEach((entry) => list.append(dbPathItem(dialog, input, entry)));
}

function dbPathItem(dialog, input, entry) {
  const li = document.createElement("li");
  const btn = document.createElement("button");
  btn.type = "button";
  const kind = document.createElement("span");
  kind.className = "db-path-kind";
  kind.textContent = entry.isFile ? "FDB" : "Folder";
  const name = document.createElement("span");
  name.textContent = entry.name || entry.path;
  btn.append(kind, name);
  btn.addEventListener("click", () => dbPathChoose(dialog, input, entry));
  li.append(btn);
  return li;
}

function dbPathChoose(dialog, input, entry) {
  if (entry.isFile) {
    input.value = entry.path;
    dialog.close();
    return;
  }
  dbPathLoad(dialog, input, entry.path, false);
}

function dbPathInit() {
  const input = document.querySelector("[data-db-path]");
  const openBtn = document.querySelector("[data-db-browse]");
  const dialog = document.getElementById("dbPathBrowse");
  if (!input || !openBtn || !dialog) return;
  openBtn.addEventListener("click", () => {
    if (typeof dialog.showModal === "function") dialog.showModal();
    const current = input.value.trim();
    dbPathLoad(dialog, input, current, current.length === 0);
  });
  dialog.querySelector("[data-db-path-close]")?.addEventListener("click", () => dialog.close());
  dialog.addEventListener("cancel", (e) => {
    e.preventDefault();
    dialog.close();
  });
}

dbPathInit();
