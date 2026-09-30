async function dbPathPicked(input, picker, hint) {
  const file = picker.files && picker.files[0];
  if (!file) return;
  dbPathHint(hint, "");
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
    dbPathHint(hint, err.message || "Could not look up that file.");
    return;
  }
  const paths = data.paths || [];
  if (paths.length === 1) {
    input.value = paths[0];
    return;
  }
  if (paths.length === 0) {
    dbPathHint(hint, "That file is not on this server. Choose a company .FDB.");
    return;
  }
  dbPathChoose(input, paths);
}

function dbPathChoose(input, paths) {
  const dialog = document.getElementById("dbPathMatches");
  const list = dialog?.querySelector("[data-db-path-matches]");
  if (!dialog || !list) {
    input.value = paths[0];
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
    dialog.close();
  });
  li.append(btn);
  return li;
}

function dbPathHint(hint, message) {
  if (!hint) return;
  hint.textContent = message || "Use the pin to open the file picker and choose the company .FDB. The path is filled in for you.";
}

function dbPathInit() {
  const input = document.querySelector("[data-db-path]");
  const pin = document.querySelector("[data-db-path-pin]");
  const picker = document.querySelector("[data-db-path-file]");
  const hint = document.querySelector("[data-db-path-hint]");
  const dialog = document.getElementById("dbPathMatches");
  if (!input || !pin || !picker) return;
  pin.addEventListener("click", () => {
    picker.value = "";
    picker.click();
  });
  picker.addEventListener("change", () => dbPathPicked(input, picker, hint));
  dialog?.querySelector("[data-db-path-close]")?.addEventListener("click", () => dialog.close());
  dialog?.addEventListener("cancel", (e) => {
    e.preventDefault();
    dialog.close();
  });
}

dbPathInit();
