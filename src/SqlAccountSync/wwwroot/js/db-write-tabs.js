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

  function setFieldsEnabled(root, enabled) {
    if (!root) return;
    root.querySelectorAll("input, select, textarea").forEach(function (el) {
      el.disabled = !enabled;
    });
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
    if (rest) {
      rest.classList.toggle("is-hidden", isSdk);
      rest.hidden = isSdk;
      setFieldsEnabled(rest, !isSdk);
    }
    if (sdk) {
      sdk.classList.toggle("is-hidden", !isSdk);
      sdk.hidden = !isSdk;
      setFieldsEnabled(sdk, isSdk);
    }
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
      var modeEl = form.querySelector("[data-write-mode]");
      var mode = (modeEl && modeEl.value) || "Sdk";
      setTab(form, mode);
      if (String(mode).toLowerCase() === "sdk") syncSdkDatabase(form);
    });

    var modeEl = form.querySelector("[data-write-mode]");
    setTab(form, (modeEl && modeEl.value) || "Sdk");
  }

  document.querySelectorAll("[data-db-write-form]").forEach(bindForm);
})();
