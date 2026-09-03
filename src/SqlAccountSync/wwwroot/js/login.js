(() => {
  const card = document.querySelector(".login-card");
  if (!card) return;

  const emailStep = document.getElementById("email-step");
  const otpStep = document.getElementById("otp-step");
  const emailForm = document.getElementById("email-form");
  const otpForm = document.getElementById("otp-form");
  const emailInput = document.getElementById("email");
  const emailDisplay = document.getElementById("email-display");
  const emailError = document.getElementById("email-error");
  const otpError = document.getElementById("otp-error");
  const otpBoxes = document.getElementById("otp-boxes");
  const digits = Array.from(document.querySelectorAll(".otp-digit"));
  const countdownEl = document.getElementById("otp-countdown");
  const timerEl = document.getElementById("otp-timer");
  const resendBtn = document.getElementById("btn-resend");
  const backBtn = document.getElementById("btn-back");
  const emailSubmit = document.getElementById("email-submit");
  const verifyBtn = document.getElementById("otp-verify-btn");

  const otpLifetimeDefault = Number(card.dataset.otpSeconds || 120);
  const resendDefault = Number(card.dataset.resendSeconds || 30);

  let currentEmail = "";
  let otpExpiresAt = 0;
  let otpExpired = false;
  let expiryTimerId = null;
  let resendLeft = 0;
  let resendTimerId = null;

  function token() {
    const el = emailForm?.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : "";
  }

  function showError(el, message, ok) {
    if (!el) return;
    el.textContent = message || "";
    el.classList.toggle("show", !!message);
    el.classList.toggle("error-message--success", !!ok);
  }

  function showEmailStep() {
    emailStep?.classList.add("active");
    otpStep?.classList.remove("active");
    clearExpiry();
    showError(otpError, "");
  }

  function showOtpStep(email) {
    currentEmail = email;
    if (emailDisplay) emailDisplay.textContent = email;
    emailStep?.classList.remove("active");
    otpStep?.classList.add("active");
    digits.forEach((d) => {
      d.value = "";
      d.classList.remove("otp-digit--filled");
      d.disabled = false;
    });
    otpBoxes?.classList.remove("is-locked");
    showError(otpError, "");
    startExpiry(otpLifetimeDefault);
    startResendCooldown(resendDefault);
    digits[0]?.focus();
  }

  function clearExpiry() {
    if (expiryTimerId) clearInterval(expiryTimerId);
    expiryTimerId = null;
    otpExpired = false;
    timerEl?.classList.remove("is-expired");
  }

  function startExpiry(seconds) {
    clearExpiry();
    otpExpiresAt = Date.now() + seconds * 1000;
    tickExpiry();
    expiryTimerId = setInterval(tickExpiry, 250);
  }

  function tickExpiry() {
    const left = Math.max(0, Math.ceil((otpExpiresAt - Date.now()) / 1000));
    if (countdownEl) countdownEl.textContent = String(left);
    if (left <= 0) {
      otpExpired = true;
      timerEl?.classList.add("is-expired");
      if (countdownEl) countdownEl.textContent = "0";
      otpBoxes?.classList.add("is-locked");
      digits.forEach((d) => { d.disabled = true; });
      if (expiryTimerId) clearInterval(expiryTimerId);
      expiryTimerId = null;
    }
  }

  function startResendCooldown(seconds) {
    if (resendTimerId) clearInterval(resendTimerId);
    resendLeft = seconds;
    updateResend();
    resendTimerId = setInterval(() => {
      resendLeft -= 1;
      updateResend();
      if (resendLeft <= 0 && resendTimerId) {
        clearInterval(resendTimerId);
        resendTimerId = null;
      }
    }, 1000);
  }

  function updateResend() {
    if (!resendBtn) return;
    if (resendLeft > 0) {
      resendBtn.disabled = true;
      resendBtn.textContent = `Resend (${resendLeft}s)`;
    } else {
      resendBtn.disabled = false;
      resendBtn.textContent = "Resend";
    }
  }

  function readOtp() {
    return digits.map((d) => d.value.replace(/\D/g, "")).join("");
  }

  function fillOtpCode(code) {
    const text = String(code || "").replace(/\D/g, "").slice(0, 6);
    if (text.length !== 6) return false;
    text.split("").forEach((ch, i) => {
      const digit = digits[i];
      if (!digit) return;
      digit.value = ch;
      digit.classList.toggle("otp-digit--filled", !!ch);
      digit.disabled = false;
    });
    otpBoxes?.classList.remove("is-locked");
    otpExpired = false;
    return true;
  }

  function applyDebugOtp(code) {
    if (!fillOtpCode(code)) return;
    showError(otpError, `Development: using OTP ${code}`, true);
    setTimeout(() => otpForm?.requestSubmit(), 150);
  }

  function fillDigit(index, ch) {
    const digit = digits[index];
    if (!digit || otpExpired || digit.disabled) return;
    digit.value = ch;
    digit.classList.toggle("otp-digit--filled", !!ch);
    if (ch && index < digits.length - 1) digits[index + 1].focus();
    if (readOtp().length === 6) otpForm?.requestSubmit();
  }

  function numpadDigit(code) {
    const m = /^Numpad(\d)$/.exec(code || "");
    return m ? m[1] : "";
  }

  async function postHandler(handler, fields) {
    const body = new URLSearchParams();
    body.set("__RequestVerificationToken", token());
    Object.entries(fields).forEach(([k, v]) => body.set(k, v));
    const resp = await fetch(`/Login?handler=${handler}`, {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
        RequestVerificationToken: token()
      },
      body: body.toString(),
      credentials: "same-origin"
    });
    return resp.json();
  }

  async function sendOtp(email) {
    showError(emailError, "");
    if (emailSubmit) emailSubmit.disabled = true;
    try {
      const data = await postHandler("Send", { email });
      if (!data.success) {
        showError(emailError, data.message || "Could not send code.");
        return false;
      }
      showOtpStep(email);
      if (data.debugOtp) {
        applyDebugOtp(data.debugOtp);
      } else {
        showError(otpError, data.message || "Code sent.", true);
        setTimeout(() => showError(otpError, ""), 2500);
      }
      return true;
    } catch {
      showError(emailError, "Network error. Try again.");
      return false;
    } finally {
      if (emailSubmit) emailSubmit.disabled = false;
    }
  }

  emailForm?.addEventListener("submit", async (e) => {
    e.preventDefault();
    const email = (emailInput?.value || "").trim();
    if (!email) {
      showError(emailError, "Enter your email address.");
      return;
    }
    await sendOtp(email);
  });

  otpForm?.addEventListener("submit", async (e) => {
    e.preventDefault();
    showError(otpError, "");
    if (otpExpired) {
      showError(otpError, "OTP expired. Please resend.");
      return;
    }
    const code = readOtp();
    if (code.length !== 6) {
      showError(otpError, "Enter the full 6-digit code.");
      return;
    }
    if (verifyBtn) verifyBtn.disabled = true;
    try {
      const data = await postHandler("Verify", { email: currentEmail, code });
      if (!data.success) {
        showError(otpError, data.message || "Invalid code.");
        return;
      }
      window.location.href = data.redirect || "/";
    } catch {
      showError(otpError, "Network error. Try again.");
    } finally {
      if (verifyBtn) verifyBtn.disabled = false;
    }
  });

  resendBtn?.addEventListener("click", async () => {
    if (resendBtn.disabled || !currentEmail) return;
    const ok = await sendOtp(currentEmail);
    if (ok) startResendCooldown(resendDefault);
  });

  backBtn?.addEventListener("click", () => showEmailStep());

  digits.forEach((digit, index) => {
    digit.addEventListener("input", () => {
      if (otpExpired || digit.disabled) {
        digit.value = "";
        return;
      }
      const v = digit.value.replace(/\D/g, "").slice(-1);
      digit.value = v;
      digit.classList.toggle("otp-digit--filled", !!v);
      if (v && index < digits.length - 1) digits[index + 1].focus();
      if (readOtp().length === 6) otpForm?.requestSubmit();
    });
    digit.addEventListener("keydown", (ev) => {
      if (otpExpired || digit.disabled) {
        ev.preventDefault();
        return;
      }

      const fromNumpad = numpadDigit(ev.code);
      if (fromNumpad) {
        ev.preventDefault();
        fillDigit(index, fromNumpad);
        return;
      }

      if (/^[0-9]$/.test(ev.key)) {
        ev.preventDefault();
        fillDigit(index, ev.key);
        return;
      }

      if (ev.key === "Backspace") {
        if (digit.value) {
          digit.value = "";
          digit.classList.remove("otp-digit--filled");
          ev.preventDefault();
          return;
        }
        if (index > 0) digits[index - 1].focus();
      }
    });
    digit.addEventListener("paste", (ev) => {
      if (otpExpired || digit.disabled) {
        ev.preventDefault();
        return;
      }
      const text = (ev.clipboardData?.getData("text") || "").replace(/\D/g, "").slice(0, 6);
      if (!text) return;
      ev.preventDefault();
      text.split("").forEach((ch, i) => {
        if (!digits[i]) return;
        digits[i].value = ch;
        digits[i].classList.add("otp-digit--filled");
      });
      if (text.length === 6) otpForm?.requestSubmit();
      else digits[Math.min(text.length, digits.length - 1)]?.focus();
    });
  });
})();
