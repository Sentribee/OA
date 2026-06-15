(() => {
  const loading = document.getElementById("admin-global-loading");
  const loadingLabel = loading?.querySelector(".loading-label");

  document.querySelectorAll("form[data-global-loading]").forEach((form) => {
    form.addEventListener("submit", (event) => {
      const confirmation = form.dataset.confirm;
      if (confirmation && !window.confirm(confirmation)) {
        event.preventDefault();
        return;
      }

      if (!form.checkValidity()) {
        return;
      }

      form.querySelectorAll("input, select, textarea, button").forEach((field) => {
        field.readOnly = true;
        if (field.tagName === "BUTTON") {
          field.disabled = true;
        }
      });
      if (loadingLabel) {
        loadingLabel.textContent = form.dataset.loadingLabel || "Saving changes...";
      }
      loading?.classList.add("is-visible");
      loading?.setAttribute("aria-hidden", "false");
    });
  });
})();
