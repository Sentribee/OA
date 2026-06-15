(() => {
  const defaults = [
    { id: 0, name: "helmet" },
    { id: 2, name: "vest" },
    { id: 11, name: "no_vest" },
    { id: 1, name: "gloves" },
    { id: 3, name: "boots" }
  ];

  const modal = document.getElementById("event-annotation-modal");
  const canvas = document.getElementById("event-annotation-canvas");
  const videoModal = document.getElementById("event-video-modal");
  const videoPlayer = document.getElementById("event-video-player");
  const videoTitle = document.getElementById("event-video-title");
  if (videoModal && videoPlayer) {
    videoModal.addEventListener("show.bs.modal", (event) => {
      const button = event.relatedTarget;
      videoTitle.textContent = button?.dataset.eventTitle || "Event Video";
      videoPlayer.src = button?.dataset.videoUrl || "";
      videoPlayer.load();
    });
    videoModal.addEventListener("hidden.bs.modal", () => {
      videoPlayer.pause();
      videoPlayer.removeAttribute("src");
      videoPlayer.load();
    });
  }

  const detailModal = document.getElementById("event-detail-modal");
  const detailCanvas = document.getElementById("event-detail-scene-canvas");
  const detailLoading = document.getElementById("event-detail-loading");
  const detailContent = document.getElementById("event-detail-content");
  const detailError = document.getElementById("event-detail-error");
  const detailTitle = document.getElementById("event-detail-title");
  const detailSubtitle = document.getElementById("event-detail-subtitle");
  const detailStats = document.getElementById("event-detail-stats");
  const detailSummary = document.getElementById("event-detail-summary");
  const detailLegend = document.getElementById("event-detail-scene-legend");
  const detailSubjects = document.getElementById("event-detail-subjects");
  const detailSubjectCount = document.getElementById("event-detail-subject-count");
  const detailAnnotationLogs = document.getElementById("event-detail-annotation-logs");

  const scenePalette = ["#2563eb", "#16a34a", "#f97316", "#7c3aed", "#dc2626", "#0891b2"];
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

  const normalizeDetailBox = (box) => {
    if (!box || typeof box !== "object") return null;
    const x = Number(box.x ?? box.X ?? box.left);
    const y = Number(box.y ?? box.Y ?? box.top);
    const w = Number(box.w ?? box.W ?? box.width);
    const h = Number(box.h ?? box.H ?? box.height);
    if (![x, y, w, h].every(Number.isFinite) || w <= 0 || h <= 0) return null;
    return {
      classId: Number(box.classId ?? box.ClassId ?? box.class_id ?? -1),
      label: String(box.label ?? box.name ?? box.className ?? "").trim(),
      x,
      y,
      w,
      h
    };
  };

  const drawDetailScene = (data) => {
    if (!detailCanvas) return;
    const ctx = detailCanvas.getContext("2d");
    const annotation = data?.analysis?.panoramaAnnotation || {};
    const classes = asArray(annotation.classes);
    const boxes = asArray(annotation.boxes).map(normalizeDetailBox).filter(Boolean);
    const className = (classId, fallback) => {
      const found = classes.find((item) => Number(item.id) === Number(classId));
      return found?.name || fallback || "Object";
    };
    detailLegend.innerHTML = boxes.length
      ? ""
      : `<span class="text-body-secondary fs-8">No scene boxes have been reported yet.</span>`;

    const image = new Image();
    image.onload = () => {
      detailCanvas.width = image.naturalWidth || 960;
      detailCanvas.height = image.naturalHeight || 540;
      ctx.clearRect(0, 0, detailCanvas.width, detailCanvas.height);
      ctx.drawImage(image, 0, 0, detailCanvas.width, detailCanvas.height);
      boxes.forEach((box, index) => {
        const color = scenePalette[index % scenePalette.length];
        const label = className(box.classId, box.label);
        ctx.strokeStyle = color;
        ctx.lineWidth = Math.max(3, detailCanvas.width / 500);
        ctx.strokeRect(box.x, box.y, box.w, box.h);
        ctx.font = `${Math.max(14, detailCanvas.width / 70)}px Arial`;
        const labelWidth = ctx.measureText(label).width + 16;
        ctx.fillStyle = color;
        ctx.fillRect(box.x, Math.max(0, box.y - 28), labelWidth, 26);
        ctx.fillStyle = "#fff";
        ctx.fillText(label, box.x + 8, Math.max(18, box.y - 9));
      });
      if (boxes.length) {
        const seen = new Set();
        detailLegend.innerHTML = boxes.map((box, index) => {
          const label = className(box.classId, box.label);
          if (seen.has(label)) return "";
          seen.add(label);
          return `<span class="event-detail-legend-item"><i style="background:${scenePalette[index % scenePalette.length]}"></i>${escapeHtml(label)}</span>`;
        }).join("");
      }
    };
    image.onerror = () => {
      detailCanvas.width = 960;
      detailCanvas.height = 240;
      ctx.clearRect(0, 0, detailCanvas.width, detailCanvas.height);
      ctx.fillStyle = "#f8fafc";
      ctx.fillRect(0, 0, detailCanvas.width, detailCanvas.height);
      ctx.fillStyle = "#64748b";
      ctx.font = "20px Arial";
      ctx.fillText("Event image is not available.", 32, 64);
    };
    image.src = data?.imageUrl || "";
  };

  const renderDetailSubjects = (subjects) => {
    const items = asArray(subjects);
    detailSubjectCount.textContent = `${items.length} subjects`;
    if (!items.length) {
      detailSubjects.innerHTML = `<div class="rounded border p-4 text-body-secondary">No person slices have been reported yet.</div>`;
      return;
    }

    detailSubjects.innerHTML = items.map((subject) => {
      const ppeStatus = subject.ppeStatus ? JSON.stringify(subject.ppeStatus, null, 2) : "No PPE status yet";
      const ppeBoxes = subject.ppeBoxes ? JSON.stringify(subject.ppeBoxes, null, 2) : "No PPE boxes yet";
      const imageUrl = proxyArtifactUrl(subject.cropImageUrl || subject.previewImageUrl);
      return `
        <article class="event-detail-subject-card">
          <div class="event-detail-subject-media">
            ${imageUrl
              ? `<div class="event-detail-subject-image-wrap">
                  <img src="${escapeHtml(imageUrl)}" alt="">
                  <div class="event-detail-ppe-overlay" data-ppe-boxes="${escapeHtml(JSON.stringify(subject.ppeBoxes || []))}"></div>
                </div>`
              : `<div class="event-detail-subject-empty">No slice image</div>`}
          </div>
          <div class="event-detail-subject-body">
            <div class="d-flex align-items-start justify-content-between gap-3">
              <div>
                <div class="fw-semibold">${escapeHtml(subject.subjectKey || "Person")}</div>
                <div class="text-body-secondary fs-8">${escapeHtml(subject.trackingLabel || subject.subjectType || "")}</div>
              </div>
              <span class="badge ${subject.isRisk ? "bg-danger-subtle text-danger" : "bg-success-subtle text-success"}">${subject.isRisk ? "Risk" : "OK"}</span>
            </div>
            ${subject.riskReason ? `<div class="event-detail-risk-note">${escapeHtml(subject.riskReason)}</div>` : ""}
            <div class="event-detail-json-grid">
              <div>
                <div class="text-body-secondary fs-8 mb-1">PPE Status</div>
                <pre>${escapeHtml(ppeStatus)}</pre>
              </div>
              <div>
                <div class="text-body-secondary fs-8 mb-1">PPE Boxes</div>
                <pre>${escapeHtml(ppeBoxes)}</pre>
              </div>
            </div>
          </div>
        </article>`;
    }).join("");
    renderSubjectPpeOverlays();
  };

  const renderAnnotationLogs = (logs) => {
    if (!detailAnnotationLogs) return;
    const items = asArray(logs);
    if (!items.length) {
      detailAnnotationLogs.innerHTML = `<div class="rounded border p-3 text-body-secondary fs-8">No annotation saves have been logged yet.</div>`;
      return;
    }

    detailAnnotationLogs.innerHTML = items.map((item) => {
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

  const normalizePpeCropBox = (item) => {
    const box = item?.cropBox || item?.crop_box || item?.box || item;
    if (!box || typeof box !== "object") return null;
    const arrayBox = Array.isArray(box);
    const x = Number(arrayBox ? box[0] : box.x ?? box.X ?? box.left);
    const y = Number(arrayBox ? box[1] : box.y ?? box.Y ?? box.top);
    const w = Number(arrayBox ? box[2] - box[0] : box.w ?? box.W ?? box.width);
    const h = Number(arrayBox ? box[3] - box[1] : box.h ?? box.H ?? box.height);
    if (![x, y, w, h].every(Number.isFinite) || w <= 0 || h <= 0) return null;
    return {
      x,
      y,
      w,
      h,
      label: String(item?.label || item?.class || item?.name || box.label || "ppe"),
      classId: Number(item?.classId ?? item?.class_id ?? box.classId ?? box.class_id ?? -1)
    };
  };

  const renderSubjectPpeOverlays = () => {
    document.querySelectorAll(".event-detail-subject-image-wrap").forEach((wrap) => {
      const image = wrap.querySelector("img");
      const overlay = wrap.querySelector(".event-detail-ppe-overlay");
      if (!image || !overlay) return;
      const draw = () => {
        let boxes = [];
        try {
          boxes = JSON.parse(overlay.dataset.ppeBoxes || "[]");
        } catch {
          boxes = [];
        }
        const width = image.naturalWidth || image.clientWidth;
        const height = image.naturalHeight || image.clientHeight;
        overlay.innerHTML = "";
        if (!width || !height) return;
        boxes.map(normalizePpeCropBox).filter(Boolean).forEach((box) => {
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
      if (image.complete) {
        draw();
      } else {
        image.addEventListener("load", draw, { once: true });
      }
      image.addEventListener("error", () => {
        if (image.dataset.rawSrc && image.src !== image.dataset.rawSrc) {
          image.src = image.dataset.rawSrc;
        }
      }, { once: true });
    });
  };

  const renderEventDetail = (data) => {
    const analysis = data.analysis || {};
    detailTitle.textContent = data.title || "Event Analysis";
    detailSubtitle.textContent = [
      `Event ID ${data.id}`,
      data.edgeDevice?.name || "Device",
      data.edgeDevice?.code || "",
      data.status || ""
    ].filter(Boolean).join(" | ");
    detailStats.innerHTML = [
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
    detailSummary.textContent = analysis.summary || `${data.summary?.panoramaObjectCount || 0} scene boxes, ${data.summary?.personSubjectCount || 0} person slices, ${data.summary?.riskSubjectCount || 0} risk subjects.`;
    drawDetailScene(data);
    renderDetailSubjects(data.subjects);
    renderAnnotationLogs(data.annotationLogs);
  };

  if (detailModal) {
    detailModal.addEventListener("show.bs.modal", async (event) => {
      const button = event.relatedTarget;
      const id = button?.dataset.eventId;
      detailTitle.textContent = button?.dataset.eventTitle || "Event Analysis";
      detailSubtitle.textContent = "";
      detailLoading.classList.remove("d-none");
      detailContent.classList.add("d-none");
      detailError.classList.add("d-none");
      detailError.textContent = "";
      try {
        const response = await fetch(`/api/events/${id}/analysis-detail`, { headers: { "Accept": "application/json" } });
        if (!response.ok) throw new Error(`Unable to load event analysis (${response.status}).`);
        const data = await response.json();
        renderEventDetail(data);
        detailContent.classList.remove("d-none");
      } catch (error) {
        detailError.textContent = error.message || "Unable to load event analysis.";
        detailError.classList.remove("d-none");
      } finally {
        detailLoading.classList.add("d-none");
      }
    });
  }

  if (!modal || !canvas) return;

  const ctx = canvas.getContext("2d");
  const title = document.getElementById("event-annotation-title");
  const subtitle = document.getElementById("event-annotation-subtitle");
  const classesEl = document.getElementById("event-annotation-classes");
  const boxesEl = document.getElementById("event-annotation-boxes");
  const yoloEl = document.getElementById("event-annotation-yolo");
  const canvasWrap = canvas.closest(".annotation-canvas-wrap");
  const saveBtn = document.getElementById("event-annotation-save");
  const saveLearningBtn = document.getElementById("event-annotation-save-learning");
  const clearBtn = document.getElementById("event-annotation-clear");
  const annotationLog = document.getElementById("event-annotation-log");

  let eventId = null;
  let imageUrl = "";
  let image = new Image();
  let classes = [...defaults];
  let boxes = [];
  let selectedBox = -1;
  let drawing = null;
  let dragging = null;
  let readOnly = false;
  let currentStatus = "";
  let activeTrigger = null;
  let annotationScope = "event";
  let subjectId = null;
  let modelClassesPromise = null;
  let pendingLearningLocked = false;

  fetch("/api/model/training-lock")
    .then((response) => response.ok ? response.json() : { locked: false })
    .then((data) => {
      pendingLearningLocked = Boolean(data.locked);
      if (pendingLearningLocked && saveLearningBtn) {
        saveLearningBtn.classList.add("d-none");
        saveLearningBtn.disabled = true;
      }
    })
    .catch(() => {
      pendingLearningLocked = false;
    });

  const normalizeClasses = (items) => (Array.isArray(items) ? items : [])
    .map((item) => ({
      id: Number(item.id),
      name: String(item.name || "").trim()
    }))
    .filter((item) => Number.isFinite(item.id) && item.name);

  const loadModelClasses = async () => {
    if (!modelClassesPromise) {
      modelClassesPromise = fetch("/api/model/classes")
        .then((response) => {
          if (!response.ok) throw new Error("Unable to load model classes.");
          return response.json();
        })
        .then((data) => normalizeClasses(data.classes));
    }

    const loaded = await modelClassesPromise;
    return loaded.length ? loaded : [...defaults];
  };

  const normalizeClassText = (value) => String(value || "")
    .trim()
    .toLowerCase()
    .replace(/[_-]+/g, " ");

  const classIdForLabel = (label, fallbackClassId = -1) => {
    const normalizedLabel = normalizeClassText(label);
    const exact = classes.find((item) => normalizeClassText(item.name) === normalizedLabel);
    if (exact) return exact.id;
    const partial = classes.find((item) => {
      const normalizedName = normalizeClassText(item.name);
      return normalizedName.includes(normalizedLabel) || normalizedLabel.includes(normalizedName);
    });
    if (partial) return partial.id;
    return classes.some((item) => item.id === Number(fallbackClassId))
      ? Number(fallbackClassId)
      : classes[0].id;
  };

  const parseSubjectPpeBoxes = (value) => {
    let parsed = [];
    try {
      parsed = JSON.parse(value || "[]");
    } catch {
      parsed = [];
    }

    return (Array.isArray(parsed) ? parsed : [])
      .map(normalizePpeCropBox)
      .filter(Boolean)
      .map((box) => ({
        classId: classIdForLabel(box.label, box.classId),
        x: box.x,
        y: box.y,
        w: box.w,
        h: box.h
      }));
  };

  const clamp = (value, min, max) => Math.max(min, Math.min(value, max));

  const toCanvasPoint = (event) => {
    const rect = canvas.getBoundingClientRect();
    return {
      x: (event.clientX - rect.left) * (canvas.width / rect.width),
      y: (event.clientY - rect.top) * (canvas.height / rect.height)
    };
  };

  const normalizeBox = (box) => {
    const w = clamp(Math.abs(box.w), 1, canvas.width);
    const h = clamp(Math.abs(box.h), 1, canvas.height);
    return {
      classId: Number(box.classId),
      x: clamp(box.x, 0, Math.max(0, canvas.width - w)),
      y: clamp(box.y, 0, Math.max(0, canvas.height - h)),
      w,
      h
    };
  };

  const hitTestBox = (point) => {
    for (let index = boxes.length - 1; index >= 0; index--) {
      const box = boxes[index];
      if (
        point.x >= box.x &&
        point.x <= box.x + box.w &&
        point.y >= box.y &&
        point.y <= box.y + box.h
      ) {
        return index;
      }
    }

    return -1;
  };

  const toYoloLines = () => boxes.map((box) => {
    const xCenter = (box.x + box.w / 2) / canvas.width;
    const yCenter = (box.y + box.h / 2) / canvas.height;
    return [
      box.classId,
      xCenter.toFixed(6),
      yCenter.toFixed(6),
      (box.w / canvas.width).toFixed(6),
      (box.h / canvas.height).toFixed(6)
    ].join(" ");
  }).join("\n");

  const renderClasses = () => {
    classesEl.innerHTML = "";
    const title = document.createElement("div");
    title.className = "text-body-secondary fs-8 mb-2";
    title.textContent = "Model classes used by boxes:";
    classesEl.appendChild(title);
    const list = document.createElement("div");
    list.className = "d-flex flex-wrap gap-2";
    classes.forEach((item) => {
      const badge = document.createElement("span");
      badge.className = "annotation-class-chip";
      badge.textContent = `${item.id} ${item.name}`;
      list.appendChild(badge);
    });
    classesEl.appendChild(list);
  };

  const renderBoxes = () => {
    boxesEl.innerHTML = "";
    boxes.forEach((box, index) => {
      const row = document.createElement("div");
      row.className = `annotation-box-row rounded border p-2 ${index === selectedBox ? "is-selected" : ""}`;
      const options = classes.map((item) => `<option value="${item.id}" ${item.id === box.classId ? "selected" : ""}>${item.id} ${item.name}</option>`).join("");
      row.innerHTML = `<div class="d-flex gap-2"><select class="form-select form-select-sm">${options}</select><button type="button" class="btn btn-outline-danger btn-sm">Delete</button></div>`;
      const select = row.querySelector("select");
      const deleteButton = row.querySelector("button");
      select.disabled = readOnly;
      deleteButton.disabled = readOnly;
      if (readOnly) deleteButton.classList.add("d-none");
      row.addEventListener("click", () => {
        selectedBox = index;
        renderBoxes();
        draw();
      });
      select.addEventListener("mousedown", (event) => event.stopPropagation());
      select.addEventListener("click", (event) => event.stopPropagation());
      select.addEventListener("focus", () => {
        selectedBox = index;
        draw();
      });
      select.addEventListener("change", (event) => {
        if (readOnly) return;
        box.classId = Number(event.target.value);
        yoloEl.textContent = toYoloLines();
        draw();
      });
      deleteButton.addEventListener("click", (event) => {
        if (readOnly) return;
        event.stopPropagation();
        boxes.splice(index, 1);
        selectedBox = -1;
        renderBoxes();
        draw();
      });
      boxesEl.appendChild(row);
    });
    yoloEl.textContent = toYoloLines();
  };

  const learningClass = (status) => `event-learning-status event-learning-status-${String(status || "None").toLowerCase().replace(/\s+/g, "-")}`;

  const setLearningBadge = (badge, status) => {
    if (!badge) return;
    badge.className = `badge ${learningClass(status)}`;
    badge.textContent = status || "None";
  };

  const updateSavedAnnotationState = (result, saveAsPendingLearning) => {
    const nextLearningStatus = result?.learningStatus || (saveAsPendingLearning ? "Pending Learning" : null);
    const trigger = activeTrigger;
    if (!trigger) return;

    if (result?.ppeBoxJson) {
      trigger.dataset.ppeBoxes = result.ppeBoxJson;
    } else if (annotationScope !== "subject") {
      trigger.dataset.annotation = JSON.stringify({ imageUrl, imageWidth: canvas.width, imageHeight: canvas.height, classes, boxes });
    }

    if (!nextLearningStatus) {
      return;
    }

    trigger.dataset.learningStatus = nextLearningStatus;
    const key = annotationScope === "subject"
      ? `subject-${subjectId}`
      : `event-${eventId}`;
    setLearningBadge(document.querySelector(`[data-learning-badge="${key}"]`), nextLearningStatus);

    const localContainer = trigger.closest("tr, article, #event-detail-scene-actions");
    setLearningBadge(localContainer?.querySelector(".event-learning-status"), nextLearningStatus);

    if (saveLearningBtn) {
      saveLearningBtn.classList.add("d-none");
    }
  };

  const updateAnnotationDialogLog = (result) => {
    if (!annotationLog) return;
    const actor = result?.savedBy
      ? [result.savedBy.name, result.savedBy.email].filter(Boolean).join(" | ")
      : "";
    annotationLog.innerHTML = `
      <div class="text-body-emphasis">Saved by ${escapeHtml(actor || "Unknown user")}</div>
      <div>${escapeHtml(formatDateTime(result?.savedAtUtc || new Date().toISOString()))}</div>`;
  };

  const drawBox = (box, index) => {
    const klass = classes.find((item) => item.id === box.classId);
    ctx.strokeStyle = index === selectedBox ? "#f59e0b" : "#a855f7";
    ctx.lineWidth = index === selectedBox ? 4 : 3;
    ctx.strokeRect(box.x, box.y, box.w, box.h);
    ctx.fillStyle = index === selectedBox ? "#f59e0b" : "#7c3aed";
    const label = `${box.classId} ${klass?.name || "Object"}`;
    ctx.font = "16px Arial";
    const width = ctx.measureText(label).width + 14;
    ctx.fillRect(box.x, Math.max(0, box.y - 25), width, 24);
    ctx.fillStyle = "#fff";
    ctx.fillText(label, box.x + 7, Math.max(17, box.y - 8));
  };

  const draw = () => {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    if (image.complete && image.naturalWidth) {
      ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
    }
    boxes.forEach(drawBox);
    if (drawing) {
      drawBox({ classId: classes[0].id, x: drawing.x, y: drawing.y, w: drawing.w, h: drawing.h }, -1);
    }
    yoloEl.textContent = toYoloLines();
  };

  modal.addEventListener("show.bs.modal", (event) => {
    const button = event.relatedTarget;
    activeTrigger = button || null;
    annotationScope = button?.dataset.annotationScope || "event";
    subjectId = button?.dataset.subjectId || null;
    eventId = button?.dataset.eventId;
    imageUrl = button?.dataset.imageUrl || "";
    currentStatus = button?.dataset.eventStatus || "";
    const learningStatus = button?.dataset.learningStatus || "";
    readOnly = button?.dataset.readOnly === "true" || learningStatus === "Trained";
    if (annotationScope === "subject") {
      readOnly = learningStatus === "Trained" || button?.dataset.readOnly === "true";
      title.textContent = readOnly ? "PPE Annotation (Read Only)" : "PPE Annotation";
    } else {
      title.textContent = readOnly ? "Event Image Annotation (Read Only)" : "Event Image Annotation";
    }
    subtitle.textContent = button?.dataset.eventTitle || "";
    if (annotationLog) {
      annotationLog.textContent = "No save recorded in this dialog yet.";
    }
    const help = document.getElementById("event-annotation-help");
    if (help) {
      help.textContent = annotationScope === "subject"
        ? "Drag on the person slice to draw PPE boxes. Click and drag an existing box to move it. Select a row to change its class or delete it."
        : readOnly
          ? "This event has completed learning. AI labels are locked and can only be viewed."
          : "Drag on the image to draw a box. Click and drag an existing box to move it. Select a row to change its class or delete it.";
    }
    if (saveBtn) {
      saveBtn.classList.toggle("d-none", readOnly);
      saveBtn.textContent = annotationScope === "subject" ? "Save PPE Boxes" : "Save AI Label";
    }
    if (saveLearningBtn) {
      saveLearningBtn.classList.toggle("d-none", pendingLearningLocked || readOnly || learningStatus === "Pending Learning");
      saveLearningBtn.disabled = pendingLearningLocked;
    }
    if (clearBtn) clearBtn.classList.toggle("d-none", readOnly);
    canvas.classList.toggle("is-read-only", readOnly);
    canvasWrap?.classList.toggle("annotation-canvas-wrap-subject", annotationScope === "subject");
    selectedBox = -1;
    boxes = [];
    classes = [...defaults];
    renderClasses();
    renderBoxes();
    if (annotationScope === "subject") {
      classes = [...defaults];
      boxes = parseSubjectPpeBoxes(button?.dataset.ppeBoxes);
    } else {
      loadModelClasses()
        .then((loadedClasses) => {
          classes = loadedClasses;
          boxes = boxes.map((box) => ({
            ...box,
            classId: classes.some((item) => item.id === Number(box.classId))
              ? Number(box.classId)
              : classes[0].id
          }));
          renderClasses();
          renderBoxes();
          draw();
        })
        .catch(() => {
          renderClasses();
          renderBoxes();
          draw();
        });
      try {
        const existing = JSON.parse(button?.dataset.annotation || "null");
        if (existing?.boxes?.length) boxes = existing.boxes;
      } catch {
        boxes = [];
      }
    }
    renderClasses();
    renderBoxes();
    image = new Image();
    image.onload = () => {
      canvas.width = image.naturalWidth || 960;
      canvas.height = image.naturalHeight || 540;
      boxes = boxes.map(normalizeBox);
      renderBoxes();
      draw();
    };
    image.src = imageUrl;
  });

  modal.addEventListener("hidden.bs.modal", () => {
    activeTrigger = null;
  });

  canvas.addEventListener("mousedown", (event) => {
    if (readOnly) return;
    const point = toCanvasPoint(event);
    const hitIndex = hitTestBox(point);
    if (hitIndex >= 0) {
      const box = boxes[hitIndex];
      selectedBox = hitIndex;
      dragging = {
        index: hitIndex,
        offsetX: point.x - box.x,
        offsetY: point.y - box.y
      };
      drawing = null;
      canvas.style.cursor = "move";
      renderBoxes();
      draw();
      event.preventDefault();
      return;
    }

    selectedBox = -1;
    drawing = { x0: point.x, y0: point.y, x: point.x, y: point.y, w: 1, h: 1 };
    renderBoxes();
  });

  canvas.addEventListener("mousemove", (event) => {
    const point = toCanvasPoint(event);
    if (readOnly) {
      canvas.style.cursor = "default";
      return;
    }
    if (dragging) {
      const box = boxes[dragging.index];
      if (!box) return;
      box.x = clamp(point.x - dragging.offsetX, 0, Math.max(0, canvas.width - box.w));
      box.y = clamp(point.y - dragging.offsetY, 0, Math.max(0, canvas.height - box.h));
      boxes[dragging.index] = normalizeBox(box);
      draw();
      return;
    }

    if (!drawing) {
      canvas.style.cursor = hitTestBox(point) >= 0 ? "move" : "crosshair";
      return;
    }

    drawing.x = Math.min(drawing.x0, point.x);
    drawing.y = Math.min(drawing.y0, point.y);
    drawing.w = Math.abs(point.x - drawing.x0);
    drawing.h = Math.abs(point.y - drawing.y0);
    draw();
  });

  window.addEventListener("mouseup", () => {
    if (dragging) {
      const box = boxes[dragging.index];
      if (box) {
        boxes[dragging.index] = normalizeBox(box);
      }
      dragging = null;
      canvas.style.cursor = "crosshair";
      renderBoxes();
      draw();
      return;
    }

    if (!drawing) return;
    if (drawing.w > 8 && drawing.h > 8) {
      boxes.push(normalizeBox({ classId: classes[0].id, x: drawing.x, y: drawing.y, w: drawing.w, h: drawing.h }));
      selectedBox = boxes.length - 1;
    }
    drawing = null;
    renderBoxes();
    draw();
  });

  clearBtn?.addEventListener("click", () => {
    if (readOnly) return;
    boxes = [];
    selectedBox = -1;
    renderBoxes();
    draw();
  });

  const saveAnnotation = async (saveAsPendingLearning) => {
    if (readOnly) return;
    if (saveAsPendingLearning && pendingLearningLocked) {
      alert("Pending Learning saves are disabled while model training is being prepared or running.");
      return;
    }
    if (annotationScope === "subject" && !subjectId) return;
    if (annotationScope !== "subject" && !eventId) return;
    saveBtn.disabled = true;
    if (saveLearningBtn) saveLearningBtn.disabled = true;
    try {
      const payload = {
        imageUrl,
        imageWidth: canvas.width,
        imageHeight: canvas.height,
        classes,
        boxes,
        yoloText: toYoloLines(),
        saveAsPendingLearning
      };
      const endpoint = annotationScope === "subject"
        ? `/api/edge-event-subjects/${subjectId}/ppe-annotations`
        : `/api/events/${eventId}/annotations`;
      const response = await fetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      });
      const result = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(result.message || "Annotation save failed.");
      updateSavedAnnotationState(result, saveAsPendingLearning);
      updateAnnotationDialogLog(result);
      window.bootstrap?.Modal.getInstance(modal)?.hide();
    } catch (error) {
      alert(error.message || "Annotation save failed.");
    } finally {
      saveBtn.disabled = false;
      if (saveLearningBtn) saveLearningBtn.disabled = false;
    }
  };

  saveBtn?.addEventListener("click", () => saveAnnotation(false));
  saveLearningBtn?.addEventListener("click", () => saveAnnotation(true));

  document.querySelectorAll(".event-mark-real-risk-btn").forEach((button) => {
    button.addEventListener("click", async () => {
      const id = button.dataset.eventId;
      if (!id) return;
      button.disabled = true;
      try {
        const response = await fetch(`/api/events/${id}/real-risk`, { method: "POST" });
        if (!response.ok) {
          const data = await response.json().catch(() => ({}));
          throw new Error(data.message || "Unable to mark event as real risk.");
        }
        window.location.reload();
      } catch (error) {
        alert(error.message || "Unable to mark event as real risk.");
        button.disabled = false;
      }
    });
  });
})();
