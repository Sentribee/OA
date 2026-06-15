(() => {
  const form = document.getElementById("login-form");
  if (!form) {
    return;
  }

  const loginId = document.getElementById("Email");
  const password = document.getElementById("Password");
  const feedback = document.getElementById("login-feedback");
  const feedbackText = feedback?.querySelector("span");
  const loading = document.getElementById("global-loading");
  const submit = document.getElementById("login-submit");
  let hideFeedbackTimer;

  const showFeedback = (message) => {
    if (!feedback || !feedbackText) {
      return;
    }

    feedbackText.textContent = message;
    feedback.classList.add("is-visible");
    window.clearTimeout(hideFeedbackTimer);
    hideFeedbackTimer = window.setTimeout(() => {
      feedback.classList.remove("is-visible");
    }, 4200);
  };

  if (feedback?.classList.contains("is-visible")) {
    hideFeedbackTimer = window.setTimeout(() => {
      feedback.classList.remove("is-visible");
    }, 4200);
  }

  form.addEventListener("submit", (event) => {
    if (!loginId?.value.trim() || !password?.value) {
      event.preventDefault();
      showFeedback("Please enter your email and password.");
      (loginId?.value.trim() ? password : loginId)?.focus();
      return;
    }

    loginId.readOnly = true;
    password.readOnly = true;
    submit.disabled = true;
    form.setAttribute("aria-busy", "true");
    loading?.classList.add("is-visible");
    loading?.setAttribute("aria-hidden", "false");
  });
})();

(() => {
  document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll('.oa-auto-modal[data-open="true"]').forEach((modal) => {
      if (!window.bootstrap?.Modal) {
        return;
      }

      window.bootstrap.Modal.getOrCreateInstance(modal).show();
    });
  });
})();
