(function () {
  function fileNameFromPath(path) {
    var raw = (path || "").trim().replace(/\\/g, "/");
    if (!raw) return "";
    var parts = raw.split("/");
    return parts[parts.length - 1] || "";
  }

  function syncSdkDatabase(form) {
    var source = form.querySelector("[data-sdk-db-source]");
    var target = form.querySelector("[data-sdk-db-target]");
    if (!source || !target) return;
    if ((target.value || "").trim()) return;
    target.value = fileNameFromPath(source.value);
  }

  function setTab(form, mode) {
    var isSdk = String(mode).toLowerCase() === "sdk";
    var hidden = form.querySelector("[data-write-mode]");
    if (hidden) hidden.value = isSdk ? "Sdk" : "Rest";

    form.querySelectorAll("[data-write-tab]").forEach(function (btn) {
      var on = String(btn.getAttribute("data-write-tab")).toLowerCase() === (isSdk ? "sdk" : "rest");
      btn.classList.toggle("is-active", on);
      btn.setAttribute("aria-selected", on ? "true" : "false");
    });

    var rest = form.querySelector("[data-panel-rest]");
    var sdk = form.querySelector("[data-panel-sdk]");
    if (rest) rest.hidden = isSdk;
    if (sdk) sdk.hidden = !isSdk;
    if (isSdk) syncSdkDatabase(form);
  }

  function bindForm(form) {
    form.querySelectorAll("[data-write-tab]").forEach(function (btn) {
      btn.addEventListener("click", function () {
        setTab(form, btn.getAttribute("data-write-tab"));
      });
    });

    var source = form.querySelector("[data-sdk-db-source]");
    if (source) {
      source.addEventListener("change", function () { syncSdkDatabase(form); });
      source.addEventListener("blur", function () { syncSdkDatabase(form); });
    }

    form.addEventListener("submit", function () {
      var mode = (form.querySelector("[data-write-mode]") || {}).value || "Sdk";
      if (String(mode).toLowerCase() === "sdk") syncSdkDatabase(form);
    });
  }

  document.querySelectorAll("[data-db-write-form]").forEach(bindForm);
})();
