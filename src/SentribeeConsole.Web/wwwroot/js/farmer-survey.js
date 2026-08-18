(() => {
  const form = document.querySelector("[data-survey-form]");
  const progressBar = document.querySelector("[data-progress-bar]");
  const progressLabel = document.querySelector("[data-progress-label]");

  if (!form) {
    if (progressBar) progressBar.style.width = "100%";
    if (progressLabel) progressLabel.textContent = "Complete / 已完成";
    return;
  }

  const questions = [...form.querySelectorAll("[data-question]")].filter(
    (question) => !question.classList.contains("optional")
  );

  const isAnswered = (question) => {
    const controls = [...question.querySelectorAll("input:not([type=hidden]), textarea, select")].filter(
      (control) => !control.matches("[data-other-input]")
    );
    const grouped = controls.filter((control) => control.type === "radio" || control.type === "checkbox");
    if (grouped.length) return grouped.some((control) => control.checked);
    return controls.some((control) => control.value.trim().length > 0);
  };

  const updateProgress = () => {
    const answered = questions.filter(isAnswered).length;
    const percentage = questions.length ? Math.round((answered / questions.length) * 100) : 0;
    if (progressBar) progressBar.style.width = `${percentage}%`;
    if (progressLabel) progressLabel.textContent = `${percentage}%`;
  };

  const updateOtherInput = (input) => {
    const targetId = input.dataset.otherToggle;
    if (!targetId) return;
    const target = document.getElementById(targetId);
    if (!target) return;
    const groupName = input.name;
    const otherSelected = [...form.querySelectorAll(`[name="${CSS.escape(groupName)}"][data-other-toggle="${CSS.escape(targetId)}"]`)]
      .some((control) => control.checked && control.value === "Other");
    target.classList.toggle("is-visible", otherSelected);
    target.disabled = !otherSelected;
    if (!otherSelected) target.value = "";
  };

  const enforceSelectionLimit = (question) => {
    const maximum = Number(question.dataset.maxSelected || 0);
    if (!maximum) return;
    const boxes = [...question.querySelectorAll('input[type="checkbox"]')];
    const selected = boxes.filter((box) => box.checked).length;
    boxes.forEach((box) => {
      box.disabled = selected >= maximum && !box.checked;
    });
  };

  const enforceExclusive = (changed) => {
    if (changed.type !== "checkbox") return;
    const group = [...form.querySelectorAll(`[name="${CSS.escape(changed.name)}"]`)];
    const exclusive = group.find((input) => input.dataset.exclusive === "true");
    if (!exclusive) return;
    if (changed === exclusive && changed.checked) {
      group.filter((input) => input !== exclusive).forEach((input) => { input.checked = false; });
    } else if (changed !== exclusive && changed.checked) {
      exclusive.checked = false;
    }
  };

  form.addEventListener("change", (event) => {
    const input = event.target;
    if (!(input instanceof HTMLInputElement || input instanceof HTMLTextAreaElement || input instanceof HTMLSelectElement)) return;
    enforceExclusive(input);
    if (input instanceof HTMLInputElement) updateOtherInput(input);
    const question = input.closest("[data-question]");
    if (question) enforceSelectionLimit(question);
    updateProgress();
  });

  form.addEventListener("input", updateProgress);

  form.addEventListener("submit", () => {
    const button = form.querySelector("[data-submit-button]");
    if (button) {
      button.disabled = true;
      button.textContent = "Submitting… / 正在提交…";
    }
  });

  form.querySelectorAll("[data-other-toggle]").forEach(updateOtherInput);
  form.querySelectorAll("[data-max-selected]").forEach(enforceSelectionLimit);
  updateProgress();

  const firstError = form.querySelector(".field-error:not(:empty), .validation-summary-errors");
  if (firstError) firstError.scrollIntoView({ behavior: "smooth", block: "center" });
})();
