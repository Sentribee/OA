(() => {
  const form = document.querySelector("[data-password-reset-form]");
  if (!form) return;

  const current = form.querySelector("#Password_CurrentPassword");
  const next = form.querySelector("#Password_NewPassword");
  const confirm = form.querySelector("#Password_ConfirmPassword");
  const confirmError = form.querySelector("[data-password-confirm-error]");
  const nextError = form.querySelector("[data-password-new-error]");
  const submit = form.querySelector("button[type='submit']");

  const validate = () => {
    const nextValue = next?.value || "";
    const confirmValue = confirm?.value || "";
    const currentValue = current?.value || "";
    const hasLength = nextValue.length >= 10;
    const hasLettersAndNumbers = /[A-Za-z]/.test(nextValue) && /\d/.test(nextValue);
    const differsFromCurrent = !currentValue || currentValue !== nextValue;
    const matches = !confirmValue || nextValue === confirmValue;

    if (nextError) {
      nextError.textContent = !nextValue || (hasLength && hasLettersAndNumbers && differsFromCurrent)
        ? ""
        : !differsFromCurrent
          ? "New password must be different from the current password."
          : "New password must be at least 10 characters and include letters and numbers.";
    }

    if (confirmError) {
      confirmError.textContent = matches ? "" : "Confirm password must match the new password.";
    }

    if (submit) {
      submit.disabled = Boolean(nextValue || confirmValue) &&
        (!hasLength || !hasLettersAndNumbers || !differsFromCurrent || !matches);
    }
  };

  current?.addEventListener("input", validate);
  next?.addEventListener("input", validate);
  confirm?.addEventListener("input", validate);
  validate();

  const modal = document.querySelector("[data-password-reset-modal]");
  if (modal?.dataset.showPasswordModal === "true" && window.bootstrap?.Modal) {
    window.bootstrap.Modal.getOrCreateInstance(modal).show();
    modal.addEventListener("shown.bs.modal", () => current?.focus(), { once: true });
  }
})();
