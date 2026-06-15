(() => {
  const reviewModalEl = document.getElementById("pending-learning-review-modal");
  const statsModalEl = document.getElementById("annotation-mistake-stats-modal");
  if (!reviewModalEl && !statsModalEl) return;

  const escapeHtml = (value) => String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
  const asArray = (value) => Array.isArray(value) ? value : [];
  const formatDateTime = (value) => {
    if (!value) return "Unknown time";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString();
  };

  const showModal = (element) => {
    if (!element) return;
    if (window.bootstrap?.Modal) {
      window.bootstrap.Modal.getOrCreateInstance(element).show();
      return;
    }

    element.classList.add("show");
    element.style.display = "block";
    element.removeAttribute("aria-hidden");
    element.setAttribute("aria-modal", "true");
    document.body.classList.add("modal-open");
  };
  const hideModal = (element) => {
    if (!element) return;
    if (window.bootstrap?.Modal) {
      window.bootstrap.Modal.getOrCreateInstance(element).hide();
      return;
    }

    element.classList.remove("show");
    element.style.display = "none";
    element.setAttribute("aria-hidden", "true");
    element.removeAttribute("aria-modal");
    if (!document.querySelector(".modal.show")) {
      document.body.classList.remove("modal-open");
    }
  };
  document.addEventListener("click", (event) => {
    const closeButton = event.target.closest("[data-bs-dismiss='modal']");
    if (!closeButton) return;
    const modal = closeButton.closest(".modal");
    if (modal && !window.bootstrap?.Modal) {
      event.preventDefault();
      hideModal(modal);
    }
  });
  const titleEl = document.getElementById("pending-learning-review-title");
  const subtitleEl = document.getElementById("pending-learning-review-subtitle");
  const loadingEl = document.getElementById("pending-learning-review-loading");
  const emptyEl = document.getElementById("pending-learning-review-empty");
  const contentEl = document.getElementById("pending-learning-review-content");
  const errorEl = document.getElementById("pending-learning-review-error");
  const canvas = document.getElementById("pending-learning-review-canvas");
  const editorEl = document.getElementById("pending-learning-review-editor");
  const boxesEl = document.getElementById("pending-learning-review-boxes");
  const prevBtn = document.getElementById("pending-learning-review-prev");
  const cancelBtn = document.getElementById("pending-learning-review-cancel");
  const nextBtn = document.getElementById("pending-learning-review-next");
  const ctx = canvas?.getContext("2d");

  let modelKind = "panorama";
  let items = [];
  let currentIndex = 0;

  const className = (item, classId, fallback) => {
    const found = asArray(item.classes).find((klass) => Number(klass.id ?? klass.index) === Number(classId));
    return found?.name || fallback || "Object";
  };

  const normalizeBox = (item, raw) => {
    const cropBox = raw?.cropBox || raw?.crop_box || raw?.box || raw;
    const x = Number(cropBox?.x ?? cropBox?.X ?? cropBox?.left);
    const y = Number(cropBox?.y ?? cropBox?.Y ?? cropBox?.top);
    const w = Number(cropBox?.w ?? cropBox?.W ?? cropBox?.width);
    const h = Number(cropBox?.h ?? cropBox?.H ?? cropBox?.height);
    if (![x, y, w, h].every(Number.isFinite) || w <= 0 || h <= 0) return null;
    const classId = Number(raw?.classId ?? raw?.class_id ?? cropBox?.classId ?? cropBox?.class_id ?? -1);
    return {
      x,
      y,
      w,
      h,
      classId,
      label: raw?.label || cropBox?.label || className(item, classId)
    };
  };

  const drawCurrent = () => {
    if (!ctx || !canvas) return;
    const item = items[currentIndex];
    if (!item) return;
    titleEl.textContent = item.title || "Pending Learning Review";
    subtitleEl.textContent = `${currentIndex + 1} / ${items.length} | ${item.subtitle || ""}`;
    const editor = item.lastEditor;
    editorEl.innerHTML = editor
      ? `<div>${escapeHtml([editor.name, editor.email].filter(Boolean).join(" | "))}</div><div class="text-body-secondary fs-8">${escapeHtml(formatDateTime(editor.editedAtUtc))}</div>`
      : `<span class="text-body-secondary">No editor log found.</span>`;
    const boxes = asArray(item.boxes).map((box) => normalizeBox(item, box)).filter(Boolean);
    boxesEl.innerHTML = boxes.length
      ? boxes.map((box) => `
          <div class="pending-review-box-row">
            <div class="fw-medium">${escapeHtml(box.label)}</div>
            <div class="text-body-secondary fs-8">classId ${escapeHtml(box.classId)} | x ${Math.round(box.x)}, y ${Math.round(box.y)}, w ${Math.round(box.w)}, h ${Math.round(box.h)}</div>
          </div>`).join("")
      : `<div class="text-body-secondary fs-8">No boxes were saved for this item.</div>`;
    prevBtn.disabled = currentIndex <= 0;
    nextBtn.disabled = currentIndex >= items.length - 1;
    cancelBtn.disabled = false;

    const image = new Image();
    image.onload = () => {
      canvas.width = image.naturalWidth || 960;
      canvas.height = image.naturalHeight || 540;
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
      boxes.forEach((box, index) => {
        const color = index % 2 === 0 ? "#2563eb" : "#dc2626";
        ctx.strokeStyle = color;
        ctx.lineWidth = Math.max(3, canvas.width / 500);
        ctx.strokeRect(box.x, box.y, box.w, box.h);
        const label = `${box.classId} ${box.label}`;
        ctx.font = `${Math.max(14, canvas.width / 70)}px Arial`;
        const width = ctx.measureText(label).width + 16;
        ctx.fillStyle = color;
        ctx.fillRect(box.x, Math.max(0, box.y - 28), width, 26);
        ctx.fillStyle = "#fff";
        ctx.fillText(label, box.x + 8, Math.max(18, box.y - 9));
      });
    };
    image.onerror = () => {
      canvas.width = 960;
      canvas.height = 240;
      ctx.fillStyle = "#f8fafc";
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.fillStyle = "#64748b";
      ctx.font = "20px Arial";
      ctx.fillText("Image is not available.", 32, 64);
    };
    image.src = item.imageUrl || "";
  };

  const loadReview = async (kind, startId) => {
    modelKind = kind || "panorama";
    items = [];
    currentIndex = 0;
    loadingEl.classList.remove("d-none");
    contentEl.classList.add("d-none");
    emptyEl.classList.add("d-none");
    errorEl.classList.add("d-none");
    showModal(reviewModalEl);
    try {
      const response = await fetch(`/api/model/pending-learning-review?modelKind=${encodeURIComponent(modelKind)}`, {
        headers: { Accept: "application/json" }
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(payload.message || "Unable to load pending learning review items.");
      items = asArray(payload.items);
      if (!items.length) {
        emptyEl.classList.remove("d-none");
        return;
      }
      if (startId) {
        const found = items.findIndex((item) => String(item.id) === String(startId));
        currentIndex = found >= 0 ? found : 0;
      }
      contentEl.classList.remove("d-none");
      drawCurrent();
    } catch (error) {
      errorEl.textContent = error.message || "Unable to load pending learning review items.";
      errorEl.classList.remove("d-none");
    } finally {
      loadingEl.classList.add("d-none");
    }
  };

  document.addEventListener("click", (event) => {
    const button = event.target.closest(".pending-review-open");
    if (!button) return;
    event.preventDefault();
    loadReview(button.dataset.reviewModelKind, button.dataset.reviewStartId);
  });
  prevBtn?.addEventListener("click", () => {
    if (currentIndex > 0) {
      currentIndex--;
      drawCurrent();
    }
  });
  nextBtn?.addEventListener("click", () => {
    if (currentIndex < items.length - 1) {
      currentIndex++;
      drawCurrent();
    }
  });
  cancelBtn?.addEventListener("click", async () => {
    const item = items[currentIndex];
    if (!item || cancelBtn.disabled) return;
    if (!window.confirm("Cancel Pending Learning for this item and record a mistake for the last editor?")) return;
    cancelBtn.disabled = true;
    try {
      const response = await fetch("/api/model/pending-learning-review/cancel", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ modelKind, targetId: item.id })
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(payload.message || "Unable to cancel pending learning.");
      items.splice(currentIndex, 1);
      if (!items.length) {
        contentEl.classList.add("d-none");
        emptyEl.classList.remove("d-none");
        return;
      }
      currentIndex = Math.min(currentIndex, items.length - 1);
      drawCurrent();
    } catch (error) {
      alert(error.message || "Unable to cancel pending learning.");
      cancelBtn.disabled = false;
    }
  });

  const statsDate = document.getElementById("annotation-mistake-stats-date");
  const statsRefresh = document.getElementById("annotation-mistake-stats-refresh");
  const statsBody = document.getElementById("annotation-mistake-stats-body");
  const loadStats = async () => {
    if (!statsBody) return;
    statsBody.innerHTML = `<div class="rounded border p-3 text-body-secondary">Loading stats...</div>`;
    try {
      const query = statsDate?.value ? `?date=${encodeURIComponent(statsDate.value)}` : "";
      const response = await fetch(`/api/model/pending-learning-review/mistakes${query}`, { headers: { Accept: "application/json" } });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(payload.message || "Unable to load mistake stats.");
      const rows = asArray(payload.rows);
      statsBody.innerHTML = rows.length
        ? `<table class="table align-middle mb-0">
            <thead><tr><th>User</th><th>Email</th><th>Saves</th><th>Mistakes</th><th>Mistake Rate</th></tr></thead>
            <tbody>${rows.map((row) => `
              <tr>
                <td>${escapeHtml(row.name)}</td>
                <td>${escapeHtml(row.email || "")}</td>
                <td>${escapeHtml(row.saveCount)}</td>
                <td>${escapeHtml(row.mistakeCount)}</td>
                <td>${escapeHtml(row.mistakeRate)}%</td>
              </tr>`).join("")}</tbody>
          </table>`
        : `<div class="rounded border p-3 text-body-secondary">No annotation saves or mistakes were recorded for this date.</div>`;
    } catch (error) {
      statsBody.innerHTML = `<div class="rounded border border-danger-subtle p-3 text-danger">${escapeHtml(error.message || "Unable to load mistake stats.")}</div>`;
    }
  };
  document.addEventListener("click", (event) => {
    const button = event.target.closest(".annotation-mistake-stats-open");
    if (!button) return;
    event.preventDefault();
    showModal(statsModalEl);
    loadStats();
  });
  statsRefresh?.addEventListener("click", loadStats);
})();
