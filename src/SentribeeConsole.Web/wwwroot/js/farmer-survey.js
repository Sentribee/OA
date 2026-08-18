(() => {
  const root = document.documentElement;
  const body = document.body;
  const form = document.querySelector("[data-survey-form]");
  const progressBar = document.querySelector("[data-progress-bar]");
  const progressLabel = document.querySelector("[data-progress-label]");
  const languageButtons = [...document.querySelectorAll("[data-language-option]")];
  const languageStorageKey = "farmer-survey-language";
  let currentLanguage = "en";

  const copy = {
    en: {
      title: "New Zealand Livestock Farm Operations Survey",
      description: "Sentribee New Zealand Livestock Farm Operations Survey",
      languageLabel: "Language",
      complete: "Complete",
      submitting: "Submitting…"
    },
    zh: {
      title: "新西兰畜牧农场运营调查",
      description: "Sentribee 新西兰畜牧农场运营调查",
      languageLabel: "语言",
      complete: "已完成",
      submitting: "正在提交…"
    }
  };

  const wrapEnglishText = (parent) => {
    if (parent.dataset.languagePairReady === "true") return;
    parent.dataset.languagePairReady = "true";
    [...parent.childNodes]
      .filter((node) => node.nodeType === Node.TEXT_NODE && node.textContent.trim().length > 0)
      .forEach((node) => {
        const wrapper = document.createElement("span");
        wrapper.dataset.lang = "en";
        wrapper.textContent = node.textContent;
        parent.replaceChild(wrapper, node);
      });
  };

  document.querySelectorAll(".hero-zh, .body-zh, .question-zh, .submit-zh, .legend-copy small").forEach((element) => {
    element.dataset.lang = "zh";
    wrapEnglishText(element.parentElement);
  });

  const choiceLabels = [...document.querySelectorAll(".choice span[data-zh]")];
  choiceLabels.forEach((label) => {
    label.dataset.en = label.textContent.trim();
  });

  document.querySelectorAll("input[placeholder], textarea[placeholder]").forEach((control) => {
    const placeholder = control.getAttribute("placeholder");
    const divider = placeholder.indexOf(" / ");
    if (divider < 0) return;
    control.dataset.placeholderEn = placeholder.slice(0, divider);
    control.dataset.placeholderZh = placeholder.slice(divider + 3);
  });

  const prepareValidationMessage = (element) => {
    if (element.dataset.messageEn) return;
    const message = element.textContent.trim();
    const divider = message.indexOf(" / ");
    if (divider < 0) return;
    element.dataset.messageEn = message.slice(0, divider);
    element.dataset.messageZh = message.slice(divider + 3);
  };

  document.querySelectorAll(".field-error, .validation-summary li").forEach(prepareValidationMessage);

  const localizeValidationMessages = (language) => {
    document.querySelectorAll(".field-error, .validation-summary li").forEach((element) => {
      prepareValidationMessage(element);
      const message = language === "zh" ? element.dataset.messageZh : element.dataset.messageEn;
      if (message) element.textContent = message;
    });
  };

  const applyLanguage = (language, persist = false) => {
    currentLanguage = language === "zh" ? "zh" : "en";
    const isChinese = currentLanguage === "zh";
    body.classList.toggle("lang-en", !isChinese);
    body.classList.toggle("lang-zh", isChinese);
    root.lang = isChinese ? "zh-CN" : "en-NZ";
    document.title = copy[currentLanguage].title;

    const description = document.querySelector('meta[name="description"]');
    if (description) description.content = copy[currentLanguage].description;

    document.querySelectorAll("[data-i18n-en]").forEach((element) => {
      element.textContent = isChinese ? element.dataset.i18nZh : element.dataset.i18nEn;
    });

    document.querySelectorAll("[data-aria-label-en]").forEach((element) => {
      element.setAttribute("aria-label", isChinese ? element.dataset.ariaLabelZh : element.dataset.ariaLabelEn);
    });

    const switcher = document.querySelector(".language-switch");
    if (switcher) switcher.setAttribute("aria-label", copy[currentLanguage].languageLabel);

    languageButtons.forEach((button) => {
      button.setAttribute("aria-pressed", String(button.dataset.languageOption === currentLanguage));
    });

    document.querySelectorAll("[data-placeholder-en]").forEach((control) => {
      control.setAttribute("placeholder", isChinese ? control.dataset.placeholderZh : control.dataset.placeholderEn);
    });

    choiceLabels.forEach((label) => {
      label.textContent = isChinese ? label.dataset.zh : label.dataset.en;
    });

    localizeValidationMessages(currentLanguage);

    const submitLabel = document.querySelector("[data-submit-label]");
    if (submitLabel && submitLabel.closest("[data-submit-button]")?.dataset.submitting === "true") {
      submitLabel.textContent = copy[currentLanguage].submitting;
    }

    if (!form && progressLabel) progressLabel.textContent = copy[currentLanguage].complete;

    if (persist) {
      try {
        localStorage.setItem(languageStorageKey, currentLanguage);
      } catch {
        // The survey still switches normally when storage is unavailable.
      }
    }
  };

  const preferredLanguage = (() => {
    try {
      const savedLanguage = localStorage.getItem(languageStorageKey);
      if (savedLanguage === "en" || savedLanguage === "zh") return savedLanguage;
    } catch {
      // Fall back to the browser language.
    }
    return navigator.language.toLowerCase().startsWith("zh") ? "zh" : "en";
  })();

  languageButtons.forEach((button) => {
    button.addEventListener("click", () => applyLanguage(button.dataset.languageOption, true));
  });

  applyLanguage(preferredLanguage);

  if (!form) {
    if (progressBar) progressBar.style.width = "100%";
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
    const label = button?.querySelector("[data-submit-label]");
    if (button && label) {
      button.disabled = true;
      button.dataset.submitting = "true";
      label.textContent = copy[currentLanguage].submitting;
    }
  });

  form.querySelectorAll("[data-other-toggle]").forEach(updateOtherInput);
  form.querySelectorAll("[data-max-selected]").forEach(enforceSelectionLimit);
  updateProgress();

  const firstError = form.querySelector(".field-error:not(:empty), .validation-summary-errors");
  if (firstError) firstError.scrollIntoView({ behavior: "smooth", block: "center" });
})();
