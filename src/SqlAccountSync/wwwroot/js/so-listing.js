function soCellValue(row, index, type) {
    const text = (row.cells[index]?.textContent || "").trim();
    if (!text) return null;
    if (type === "money") return soMoney(text);
    if (type === "date") return soDay(text);
    return text;
}

function soMoney(text) {
    const value = Number(text.replace(/,/g, ""));
    return Number.isFinite(value) ? value : null;
}

function soDay(text) {
    const match = /^(\d{2})-(\d{2})-(\d{4})$/.exec(text);
    if (!match) return null;
    return Date.UTC(Number(match[3]), Number(match[2]) - 1, Number(match[1]));
}

function soCompareRows(left, right, index, direction, type) {
    const a = soCellValue(left, index, type);
    const b = soCellValue(right, index, type);
    if (a === null && b === null) return 0;
    if (a === null) return 1;
    if (b === null) return -1;
    const result = typeof a === "number"
        ? a - b
        : String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: "base" });
    return direction === "desc" ? -result : result;
}

function soSortBody(table, index, direction, type) {
    const body = table.tBodies[0];
    if (!body) return;
    const rows = [...body.rows].filter((row) => row.cells.length > 1);
    if (rows.length < 2) return;
    rows.sort((left, right) => soCompareRows(left, right, index, direction, type));
    rows.forEach((row) => body.appendChild(row));
}

function soMarkHeaders(table, index, direction) {
    table.querySelectorAll("th[data-sort]").forEach((header) => {
        const active = Number(header.dataset.sort) === index;
        header.classList.toggle("is-sort-asc", active && direction === "asc");
        header.classList.toggle("is-sort-desc", active && direction === "desc");
        header.setAttribute("aria-sort", active ? (direction === "asc" ? "ascending" : "descending") : "none");
    });
}

function soApplySavedSort(root) {
    const table = root.querySelector(".so-matrix");
    const index = Number(root.dataset.sortIndex);
    const direction = root.dataset.sortDir;
    if (!table || !direction || Number.isNaN(index)) return;
    const header = table.querySelector(`th[data-sort="${index}"]`);
    if (!header) return;
    soSortBody(table, index, direction, header.dataset.sortType || "text");
    soMarkHeaders(table, index, direction);
}

function soToggleSort(header) {
    const root = header.closest("[data-so-summary]");
    const table = header.closest("table");
    if (!root || !table) return;
    const index = header.dataset.sort;
    const direction = root.dataset.sortIndex === index && root.dataset.sortDir === "asc" ? "desc" : "asc";
    root.dataset.sortIndex = index;
    root.dataset.sortDir = direction;
    soSortBody(table, Number(index), direction, header.dataset.sortType || "text");
    soMarkHeaders(table, Number(index), direction);
    window.dispatchEvent(new Event("resize"));
}

function soOnHeaderClick(event) {
    const header = event.target.closest("th[data-sort]");
    if (!header) return;
    soToggleSort(header);
}

function soOnHeaderKey(event) {
    if (event.key !== "Enter" && event.key !== " ") return;
    const header = event.target.closest("th[data-sort]");
    if (!header || event.target !== header) return;
    event.preventDefault();
    soToggleSort(header);
}

function soWatchListing(root) {
    if (root.dataset.sortWatch === "1") return;
    const results = root.querySelector("[data-so-results]");
    if (!results) return;
    root.dataset.sortWatch = "1";
    const observer = new MutationObserver(() => soApplySavedSort(root));
    observer.observe(results, { childList: true });
}

function bindSoListingSort() {
    document.querySelectorAll("[data-so-summary]").forEach(soWatchListing);
    if (document.documentElement.dataset.soSortBound === "1") return;
    document.documentElement.dataset.soSortBound = "1";
    document.addEventListener("click", soOnHeaderClick);
    document.addEventListener("keydown", soOnHeaderKey);
}

if (typeof document !== "undefined")
    bindSoListingSort();
