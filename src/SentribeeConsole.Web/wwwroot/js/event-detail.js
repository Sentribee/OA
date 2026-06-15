(() => {
  const page = document.querySelector(".event-detail-page");
  if (!page) return;

  const eventId = page.dataset.eventId;
  const titleEl = document.getElementById("event-detail-title");
  const subtitleEl = document.getElementById("event-detail-subtitle");
  const statusEl = document.getElementById("event-detail-status");
  const loadingEl = document.getElementById("event-detail-loading");
  const errorEl = document.getElementById("event-detail-error");
  const contentEl = document.getElementById("event-detail-content");
  const sceneCanvas = document.getElementById("event-detail-scene-canvas");
  const sceneLegend = document.getElementById("event-detail-scene-legend");
  const sceneCount = document.getElementById("event-detail-scene-count");
  const sceneActions = document.getElementById("event-detail-scene-actions");
  const statsEl = document.getElementById("event-detail-stats");
  const summaryEl = document.getElementById("event-detail-summary");
  const subjectsEl = document.getElementById("event-detail-subjects");
  const subjectCountEl = document.getElementById("event-detail-subject-count");
  const annotationLogsEl = document.getElementById("event-detail-annotation-logs");
  const zoomModalEl = document.getElementById("event-subject-zoom-modal");
  const zoomTitle = document.getElementById("event-subject-zoom-title");
  const zoomSubtitle = document.getElementById("event-subject-zoom-subtitle");
  const zoomImage = document.getElementById("event-subject-zoom-image");
  const zoomOverlay = document.getElementById("event-subject-zoom-overlay");
  const zoomBoxes = document.getElementById("event-subject-zoom-boxes");
  const zoomModal = zoomModalEl && window.bootstrap
    ? new window.bootstrap.Modal(zoomModalEl)
    : null;
  let currentEventId = eventId;

  const palette = ["#2563eb", "#16a34a", "#f97316", "#7c3aed", "#dc2626", "#0891b2"];
  const asArray = (value) => Array.isArray(value) ? value : [];
  const escapeHtml = (value) => String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
  const formatMetric = (value, suffix = "") => {
    if (value === null || value === undefined || value === "") return "-";
    return `${value}${suffix}`;
  };
  const formatDateTime = (value) => {
    if (!value) return "";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString();
  };
  const annotationActionLabel = (action, targetType) => {
    if (String(targetType || "").toLowerCase().includes("person")) return "Saved PPE boxes";
    if (String(action || "").toLowerCase().includes("pending")) return "Saved event label as pending learning";
    return "Saved event label";
  };
  const proxyArtifactUrl = (value) => {
    if (!value) return "";
    const text = String(value);
    const markers = [
      "/artifacts/",
      "http://ins1.sentribee.ai:8097/artifacts/",
      "http://172.31.4.25:8097/artifacts/",
      "http://127.0.0.1:8097/artifacts/"
    ];
    for (const marker of markers) {
      const index = text.indexOf(marker);
      if (index >= 0) {
        const artifactPath = text.slice(index + marker.length).replace(/^\/+/, "");
        return `/api/edge-analysis-artifacts/${artifactPath}`;
      }
    }

    return text;
  };

  const normalizeBox = (box) => {
    if (!box || typeof box !== "object") return null;
    const x = Number(box.x ?? box.X ?? box.left);
    const y = Number(box.y ?? box.Y ?? box.top);
    const w = Number(box.w ?? box.W ?? box.width);
    const h = Number(box.h ?? box.H ?? box.height);
    if (![x, y, w, h].every(Number.isFinite) || w <= 0 || h <= 0) return null;
    return {
      x,
      y,
      w,
      h,
      classId: Number(box.classId ?? box.ClassId ?? box.class_id ?? -1),
      label: String(box.label ?? box.name ?? box.className ?? "").trim()
    };
  };

  const normalizePpeBox = (item) => {
    const box = normalizeBox(item?.cropBox || item?.crop_box || item?.box || item);
    if (!box) return null;
    return {
      ...box,
      label: String(item?.label || item?.class || item?.name || box.label || "ppe"),
      source: item?.source || ""
    };
  };

  const drawOverlayBoxes = (image, overlay, boxes) => {
    if (!image || !overlay) return;
    const draw = () => {
      const width = image.naturalWidth || image.clientWidth;
      const height = image.naturalHeight || image.clientHeight;
      overlay.innerHTML = "";
      if (!width || !height) return;
      asArray(boxes).map(normalizePpeBox).filter(Boolean).forEach((box) => {
        const item = document.createElement("div");
        item.className = "event-detail-ppe-box";
        item.style.left = `${(box.x / width) * 100}%`;
        item.style.top = `${(box.y / height) * 100}%`;
        item.style.width = `${(box.w / width) * 100}%`;
        item.style.height = `${(box.h / height) * 100}%`;
        const label = document.createElement("span");
        label.textContent = box.label;
        item.appendChild(label);
        overlay.appendChild(item);
      });
    };
    if (image.complete) draw();
    else image.addEventListener("load", draw, { once: true });
  };

  const drawScene = (data) => {
    if (!sceneCanvas) return;
    const ctx = sceneCanvas.getContext("2d");
    const annotation = data?.analysis?.panoramaAnnotation || {};
    const classes = asArray(annotation.classes);
    const boxes = asArray(annotation.boxes).map(normalizeBox).filter(Boolean);
    const className = (classId, fallback) => {
      const found = classes.find((item) => Number(item.id) === Number(classId));
      return found?.name || fallback || "Object";
    };
    const countTarget = document.getElementById("event-detail-scene-count") || sceneCount;
    countTarget.textContent = `${boxes.length} boxes`;
    sceneLegend.innerHTML = boxes.length
      ? ""
      : `<span class="text-body-secondary fs-8">No scene boxes have been reported yet.</span>`;

    const image = new Image();
    image.onload = () => {
      sceneCanvas.width = image.naturalWidth || 960;
      sceneCanvas.height = image.naturalHeight || 540;
      ctx.clearRect(0, 0, sceneCanvas.width, sceneCanvas.height);
      ctx.drawImage(image, 0, 0, sceneCanvas.width, sceneCanvas.height);
      boxes.forEach((box, index) => {
        const color = palette[index % palette.length];
        const label = className(box.classId, box.label);
        ctx.strokeStyle = color;
        ctx.lineWidth = Math.max(3, sceneCanvas.width / 500);
        ctx.strokeRect(box.x, box.y, box.w, box.h);
        ctx.font = `${Math.max(14, sceneCanvas.width / 70)}px Arial`;
        const labelWidth = ctx.measureText(label).width + 16;
        ctx.fillStyle = color;
        ctx.fillRect(box.x, Math.max(0, box.y - 28), labelWidth, 26);
        ctx.fillStyle = "#fff";
        ctx.fillText(label, box.x + 8, Math.max(18, box.y - 9));
      });
      if (boxes.length) {
        const seen = new Set();
        sceneLegend.innerHTML = boxes.map((box, index) => {
          const label = className(box.classId, box.label);
          if (seen.has(label)) return "";
          seen.add(label);
          return `<span class="event-detail-legend-item"><i style="background:${palette[index % palette.length]}"></i>${escapeHtml(label)}</span>`;
        }).join("");
      }
    };
    image.onerror = () => {
      sceneCanvas.width = 960;
      sceneCanvas.height = 240;
      ctx.clearRect(0, 0, sceneCanvas.width, sceneCanvas.height);
      ctx.fillStyle = "#f8fafc";
      ctx.fillRect(0, 0, sceneCanvas.width, sceneCanvas.height);
      ctx.fillStyle = "#64748b";
      ctx.font = "20px Arial";
      ctx.fillText("Event image is not available.", 32, 64);
    };
    image.src = data?.imageUrl || "";
  };

  const statusBadgeClass = (status) => {
    const normalized = String(status || "").toLowerCase();
    if (normalized.includes("severe") || normalized.includes("real")) return "bg-danger-subtle text-danger";
    if (normalized.includes("ordinary")) return "bg-warning-subtle text-warning-emphasis";
    if (normalized.includes("no risk")) return "bg-success-subtle text-success";
    if (normalized.includes("invalid")) return "bg-secondary-subtle text-secondary";
    return "bg-primary-subtle text-primary";
  };

  const learningBadgeClass = (status) => {
    const normalized = String(status || "None").toLowerCase().replace(/\s+/g, "-");
    return `event-learning-status event-learning-status-${normalized}`;
  };

  const renderStats = (analysis) => {
    statsEl.innerHTML = [
      ["People", analysis.peopleCount],
      ["Machinery", analysis.machineryVehicleCount],
      ["Tools", analysis.toolCount],
      ["PPE OK People", analysis.ppeCompliantPeopleCount],
      ["Risk People", analysis.riskPersonCount],
      ["PPE Rate", formatMetric(analysis.ppeComplianceRate, "%")]
    ].map(([label, value]) => `
      <div class="event-detail-stat">
        <span>${escapeHtml(label)}</span>
        <strong>${escapeHtml(formatMetric(value))}</strong>
      </div>`).join("");
  };

  const openSubjectZoom = (subject) => {
    if (!zoomModal || !zoomImage || !zoomOverlay) return;
    const imageUrl = proxyArtifactUrl(subject.cropImageUrl || subject.previewImageUrl);
    zoomTitle.textContent = subject.subjectKey || "Person Slice";
    zoomSubtitle.textContent = `Learning: ${subject.learningStatus || "None"}`;
    zoomImage.removeAttribute("src");
    zoomOverlay.innerHTML = "";
    zoomBoxes.innerHTML = asArray(subject.ppeBoxes).map(normalizePpeBox).filter(Boolean).map((box) => `
      <span class="event-detail-zoom-box-chip">
        <strong>${escapeHtml(box.label)}</strong>
        ${Math.round(box.w)}x${Math.round(box.h)}
      </span>`).join("") || `<span class="text-body-secondary fs-8">No PPE boxes reported.</span>`;
    zoomImage.onload = () => drawOverlayBoxes(zoomImage, zoomOverlay, subject.ppeBoxes);
    zoomImage.src = imageUrl;
    zoomModal.show();
  };

  const renderSubjects = (subjects) => {
    const items = asArray(subjects);
    const canEditEvents = page.dataset.canEditEvents === "true";
    subjectCountEl.textContent = `${items.length} subjects`;
    if (!items.length) {
      subjectsEl.innerHTML = `<div class="rounded border p-4 text-body-secondary">No person slices have been reported yet.</div>`;
      return;
    }

    subjectsEl.innerHTML = items.map((subject, index) => {
      const imageUrl = proxyArtifactUrl(subject.cropImageUrl || subject.previewImageUrl);
      const boxes = asArray(subject.ppeBoxes).map(normalizePpeBox).filter(Boolean);
      const isReadOnly = !canEditEvents || String(subject.learningStatus || "").toLowerCase() === "trained";
      return `
        <article class="event-detail-subject-card">
          <button type="button" class="event-detail-subject-media event-detail-subject-zoom-trigger" data-subject-index="${index}">
            ${imageUrl
              ? `<div class="event-detail-subject-image-wrap">
                  <img src="${escapeHtml(imageUrl)}" alt="">
                  <div class="event-detail-ppe-overlay" data-subject-index="${index}"></div>
                </div>`
              : `<div class="event-detail-subject-empty">No slice image</div>`}
          </button>
          <div class="event-detail-subject-body">
            <div class="d-flex align-items-start justify-content-between gap-3">
              <div>
                <div class="fw-semibold">${escapeHtml(subject.subjectKey || "Person")}</div>
                <div class="text-body-secondary fs-8">${escapeHtml(subject.trackingLabel || subject.subjectType || "")}</div>
              </div>
              <div class="d-flex flex-column align-items-end gap-1">
                <span class="badge ${subject.isRisk ? "bg-danger-subtle text-danger" : "bg-success-subtle text-success"}">${subject.isRisk ? "Risk" : "OK"}</span>
                <span class="badge ${learningBadgeClass(subject.learningStatus)}">${escapeHtml(subject.learningStatus || "None")}</span>
              </div>
            </div>
            ${subject.riskReason ? `<div class="event-detail-risk-note">${escapeHtml(subject.riskReason)}</div>` : ""}
            <div class="d-flex flex-wrap gap-2 mt-3">
              ${boxes.length
                ? boxes.map((box) => `<span class="event-detail-zoom-box-chip"><strong>${escapeHtml(box.label)}</strong>${Math.round(box.w)}x${Math.round(box.h)}</span>`).join("")
                : `<span class="text-body-secondary fs-8">No PPE boxes reported.</span>`}
            </div>
            <button type="button"
                    class="btn btn-outline-primary btn-sm event-annotate-btn mt-3"
                    data-bs-toggle="modal"
                    data-bs-target="#event-annotation-modal"
                    data-annotation-scope="subject"
                    data-subject-id="${escapeHtml(subject.id)}"
                    data-event-id="${escapeHtml(currentEventId)}"
                    data-event-title="${escapeHtml(subject.subjectKey || "Person Slice")}"
                    data-image-url="${escapeHtml(imageUrl)}"
                    data-learning-status="${escapeHtml(subject.learningStatus || "None")}"
                    data-read-only="${isReadOnly ? "true" : "false"}"
                    data-ppe-boxes='${escapeHtml(JSON.stringify(subject.ppeBoxes || []))}'>
              ${isReadOnly ? "View PPE" : "Annotate PPE"}
            </button>
          </div>
        </article>`;
    }).join("");

    subjectsEl.querySelectorAll(".event-detail-subject-image-wrap").forEach((wrap) => {
      const index = Number(wrap.querySelector(".event-detail-ppe-overlay")?.dataset.subjectIndex);
      const subject = items[index];
      drawOverlayBoxes(wrap.querySelector("img"), wrap.querySelector(".event-detail-ppe-overlay"), subject?.ppeBoxes);
    });
    subjectsEl.querySelectorAll(".event-detail-subject-zoom-trigger").forEach((button) => {
      button.addEventListener("click", () => openSubjectZoom(items[Number(button.dataset.subjectIndex)]));
    });
  };

  const renderAnnotationLogs = (logs) => {
    if (!annotationLogsEl) return;
    const items = asArray(logs);
    if (!items.length) {
      annotationLogsEl.innerHTML = `<div class="rounded border p-3 text-body-secondary fs-8">No annotation saves have been logged yet.</div>`;
      return;
    }

    annotationLogsEl.innerHTML = items.map((item) => {
      const actor = [item.adminName, item.adminEmail].filter(Boolean).join(" | ") || `Admin ${item.adminId || ""}`.trim();
      const target = item.targetType === "PersonSlicePpe"
        ? `Person slice ${item.edgeEventSubjectId || item.targetId}`
        : `Event ${item.edgeEventId || item.targetId}`;
      return `
        <div class="annotation-operation-item">
          <div>
            <div class="fw-medium text-body-emphasis">${escapeHtml(annotationActionLabel(item.action, item.targetType))}</div>
            <div class="text-body-secondary fs-8">${escapeHtml(target)} | ${escapeHtml(item.boxCount ?? 0)} boxes${item.saveAsPendingLearning ? " | Pending Learning" : ""}</div>
          </div>
          <div class="text-end">
            <div class="text-body-emphasis fs-8">${escapeHtml(actor)}</div>
            <div class="text-body-secondary fs-8">${escapeHtml(formatDateTime(item.createdAtUtc))}</div>
          </div>
        </div>`;
    }).join("");
  };

  const render = (data) => {
    const analysis = data.analysis || {};
    currentEventId = data.id;
    page.dataset.canEditEvents = data.canEditEvents ? "true" : "false";
    titleEl.textContent = data.title || `Event ${data.id}`;
    subtitleEl.textContent = [
      `Event ID ${data.id}`,
      data.edgeDevice?.name || "Device",
      data.edgeDevice?.code || "",
      data.status || ""
    ].filter(Boolean).join(" | ");
    statusEl.textContent = data.status || "Event";
    statusEl.className = `badge ${statusBadgeClass(data.status)}`;
    if (sceneActions) {
      const annotation = data.analysis?.panoramaAnnotation || {};
      const isReadOnly = !data.canEditEvents || String(data.learningStatus || "").toLowerCase() === "trained";
      sceneActions.innerHTML = `
        <span id="event-detail-scene-count" class="badge bg-primary text-primary-emphasis" style="--bs-bg-opacity:.12">${escapeHtml(sceneCount.textContent || "")}</span>
        <span class="badge ${learningBadgeClass(data.learningStatus)}">${escapeHtml(data.learningStatus || "None")}</span>
        <button type="button"
                class="btn btn-outline-primary btn-sm event-annotate-btn"
                data-bs-toggle="modal"
                data-bs-target="#event-annotation-modal"
                data-event-id="${escapeHtml(data.id)}"
                data-event-title="${escapeHtml(data.title || `Event ${data.id}`)}"
                data-image-url="${escapeHtml(data.imageUrl || "")}"
                data-event-status="${escapeHtml(data.status || "")}"
                data-learning-status="${escapeHtml(data.learningStatus || "None")}"
                data-read-only="${isReadOnly ? "true" : "false"}"
                data-annotation='${escapeHtml(JSON.stringify(annotation))}'>
          ${isReadOnly ? "View Label" : "Annotate"}
        </button>`;
    }
    renderStats(analysis);
    summaryEl.textContent = analysis.summary || `${data.summary?.panoramaObjectCount || 0} scene boxes, ${data.summary?.personSubjectCount || 0} person slices, ${data.summary?.riskSubjectCount || 0} risk subjects.`;
    drawScene(data);
    renderSubjects(data.subjects);
    renderAnnotationLogs(data.annotationLogs);
  };

  const load = async () => {
    loadingEl.classList.remove("d-none");
    errorEl.classList.add("d-none");
    contentEl.classList.add("d-none");
    try {
      const response = await fetch(`/api/events/${eventId}/analysis-detail`, { headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error(`Unable to load event analysis (${response.status}).`);
      render(await response.json());
      contentEl.classList.remove("d-none");
    } catch (error) {
      errorEl.textContent = error.message || "Unable to load event analysis.";
      errorEl.classList.remove("d-none");
    } finally {
      loadingEl.classList.add("d-none");
    }
  };

  load();
})();
