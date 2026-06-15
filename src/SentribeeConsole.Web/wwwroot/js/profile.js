(() => {
  const start = () => {
  const input = document.getElementById("avatar-file");
  const editButton = document.getElementById("avatar-edit-button");
  const chooseButton = document.getElementById("avatar-choose-button");
  const uploadButton = document.getElementById("avatar-upload-button");
  const progress = document.getElementById("avatar-progress");
  const feedback = document.getElementById("profile-feedback");
  const feedbackText = feedback?.querySelector("span");
  const feedbackIcon = feedback?.querySelector("i");
  const antiForgeryToken = document.querySelector("#profile-form input[name='__RequestVerificationToken']");
  const modalElement = document.getElementById("avatar-editor-modal");
  const stage = document.getElementById("avatar-crop-stage");
  const canvas = document.getElementById("avatar-crop-canvas");
  const zoom = document.getElementById("avatar-zoom");
  const editorError = document.getElementById("avatar-editor-error");
  const context = canvas?.getContext("2d");
  const getModal = () => modalElement && window.bootstrap?.Modal
    ? window.bootstrap.Modal.getOrCreateInstance(modalElement)
    : null;

  const outputSize = 512;
  let hideTimer;
  let image;
  let objectUrl;
  let scale = 1;
  let minScale = 1;
  let offsetX = 0;
  let offsetY = 0;
  let dragging = false;
  let dragStartX = 0;
  let dragStartY = 0;
  let dragOffsetX = 0;
  let dragOffsetY = 0;

  const showFeedback = (message, isError = false) => {
    if (!feedback || !feedbackText || !feedbackIcon) {
      return;
    }

    feedbackText.textContent = message;
    feedbackIcon.textContent = isError ? "error" : "check_circle";
    feedback.classList.toggle("is-error", isError);
    feedback.classList.add("is-visible");
    window.clearTimeout(hideTimer);
    hideTimer = window.setTimeout(() => feedback.classList.remove("is-visible"), 4200);
  };

  const setEditorError = (message = "") => {
    if (editorError) {
      editorError.textContent = message;
    }
  };

  const setBusy = (busy) => {
    if (input) input.disabled = busy;
    if (editButton) editButton.disabled = busy;
    if (chooseButton) chooseButton.disabled = busy;
    if (uploadButton) uploadButton.disabled = busy || !image;
    progress?.classList.toggle("is-visible", busy);
  };

  const revokeObjectUrl = () => {
    if (objectUrl) {
      URL.revokeObjectURL(objectUrl);
      objectUrl = undefined;
    }
  };

  const getCanvasSize = () => canvas?.width || outputSize;

  const clampOffsets = () => {
    if (!image || !canvas) return;

    const size = getCanvasSize();
    const drawWidth = image.naturalWidth * scale;
    const drawHeight = image.naturalHeight * scale;
    offsetX = drawWidth <= size ? (size - drawWidth) / 2 : Math.min(0, Math.max(size - drawWidth, offsetX));
    offsetY = drawHeight <= size ? (size - drawHeight) / 2 : Math.min(0, Math.max(size - drawHeight, offsetY));
  };

  const drawCrop = () => {
    if (!context || !canvas) return;

    const size = getCanvasSize();
    context.clearRect(0, 0, size, size);
    context.fillStyle = "#0f172a";
    context.fillRect(0, 0, size, size);

    if (!image) return;

    clampOffsets();
    context.imageSmoothingEnabled = true;
    context.imageSmoothingQuality = "high";
    context.drawImage(
      image,
      offsetX,
      offsetY,
      image.naturalWidth * scale,
      image.naturalHeight * scale);
  };

  const resetCrop = () => {
    if (!image || !canvas || !zoom) return;

    const size = getCanvasSize();
    minScale = Math.max(size / image.naturalWidth, size / image.naturalHeight);
    scale = minScale;
    offsetX = (size - image.naturalWidth * scale) / 2;
    offsetY = (size - image.naturalHeight * scale) / 2;
    zoom.min = "1";
    zoom.max = "4";
    zoom.step = "0.01";
    zoom.value = "1";
    drawCrop();
  };

  const loadImage = async (file) => {
    if (!file || !file.type.startsWith("image/")) {
      throw new Error("Choose a valid image file.");
    }

    revokeObjectUrl();
    objectUrl = URL.createObjectURL(file);
    const nextImage = new Image();
    await new Promise((resolve, reject) => {
      nextImage.onload = resolve;
      nextImage.onerror = reject;
      nextImage.src = objectUrl;
    });

    image = nextImage;
    resetCrop();
    setEditorError();
    if (uploadButton) uploadButton.disabled = false;
  };

  const openFilePicker = () => input?.click();

  const openEditor = () => {
    setEditorError();
  };

  const createAvatarBlob = async (maxBytes = 512 * 1024) => {
    if (!image) {
      throw new Error("Choose an image before uploading.");
    }

    const output = document.createElement("canvas");
    output.width = outputSize;
    output.height = outputSize;
    const outputContext = output.getContext("2d");
    if (!outputContext) {
      throw new Error("Image conversion failed.");
    }

    const ratio = outputSize / getCanvasSize();
    outputContext.fillStyle = "#ffffff";
    outputContext.fillRect(0, 0, outputSize, outputSize);
    outputContext.imageSmoothingEnabled = true;
    outputContext.imageSmoothingQuality = "high";
    outputContext.drawImage(
      image,
      offsetX * ratio,
      offsetY * ratio,
      image.naturalWidth * scale * ratio,
      image.naturalHeight * scale * ratio);

    let quality = 0.86;
    let blob = await new Promise((resolve) => output.toBlob(resolve, "image/jpeg", quality));
    while (blob && blob.size > maxBytes && quality > 0.58) {
      quality -= 0.08;
      blob = await new Promise((resolve) => output.toBlob(resolve, "image/jpeg", quality));
    }

    if (!blob) {
      throw new Error("Image conversion failed.");
    }

    return blob;
  };

  const uploadAvatar = async () => {
    setBusy(true);
    setEditorError();
    try {
      const blob = await createAvatarBlob();
      const body = new FormData();
      body.append("avatar", blob, "avatar.jpg");
      if (antiForgeryToken) {
        body.append("__RequestVerificationToken", antiForgeryToken.value);
      }

      const response = await fetch(`${window.location.pathname}?handler=Avatar`, {
        method: "POST",
        body
      });
      const result = await response.json();
      if (!response.ok || !result.success) {
        throw new Error(result.message || "Avatar upload failed.");
      }

      const avatarUrl = result.displayAvatarUrl || result.avatarUrl;
      document.querySelectorAll("[data-admin-avatar]").forEach((avatarImage) => {
        avatarImage.src = avatarUrl;
      });
      const modal = getModal();
      modal?.hide();
      revokeObjectUrl();
      image = undefined;
      if (input) input.value = "";
      showFeedback(result.message);
    } catch (error) {
      const message = error.message || "Avatar upload failed. Please try again.";
      setEditorError(message);
      showFeedback(message, true);
    } finally {
      setBusy(false);
    }
  };

  if (feedback?.classList.contains("is-visible")) {
    hideTimer = window.setTimeout(() => feedback.classList.remove("is-visible"), 4200);
  }

  if (uploadButton) {
    uploadButton.disabled = true;
  }

  editButton?.addEventListener("click", openEditor);
  chooseButton?.addEventListener("click", openFilePicker);

  input?.addEventListener("change", async () => {
    const file = input.files?.[0];
    if (!file) return;

    try {
      await loadImage(file);
      const modal = getModal();
      if (modal && !modalElement.classList.contains("show")) {
        modal.show();
      }
    } catch (error) {
      setEditorError(error.message || "Choose a valid image file.");
      showFeedback(error.message || "Choose a valid image file.", true);
      input.value = "";
    }
  });

  zoom?.addEventListener("input", () => {
    if (!image) return;

    const previousScale = scale;
    const size = getCanvasSize();
    const centerX = size / 2;
    const centerY = size / 2;
    const imageCenterX = (centerX - offsetX) / previousScale;
    const imageCenterY = (centerY - offsetY) / previousScale;
    scale = minScale * Number(zoom.value);
    offsetX = centerX - imageCenterX * scale;
    offsetY = centerY - imageCenterY * scale;
    drawCrop();
  });

  stage?.addEventListener("pointerdown", (event) => {
    if (!image) return;

    dragging = true;
    dragStartX = event.clientX;
    dragStartY = event.clientY;
    dragOffsetX = offsetX;
    dragOffsetY = offsetY;
    stage.classList.add("is-dragging");
    stage.setPointerCapture(event.pointerId);
  });

  stage?.addEventListener("pointermove", (event) => {
    if (!dragging) return;

    const scaleFactor = getCanvasSize() / stage.clientWidth;
    offsetX = dragOffsetX + (event.clientX - dragStartX) * scaleFactor;
    offsetY = dragOffsetY + (event.clientY - dragStartY) * scaleFactor;
    drawCrop();
  });

  const stopDragging = (event) => {
    if (!dragging) return;

    dragging = false;
    stage?.classList.remove("is-dragging");
    if (event?.pointerId && stage?.hasPointerCapture(event.pointerId)) {
      stage.releasePointerCapture(event.pointerId);
    }
  };

  stage?.addEventListener("pointerup", stopDragging);
  stage?.addEventListener("pointercancel", stopDragging);
  uploadButton?.addEventListener("click", uploadAvatar);

  modalElement?.addEventListener("hidden.bs.modal", () => {
    setEditorError();
    if (input) input.value = "";
  });
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", start, { once: true });
  } else {
    window.setTimeout(start, 0);
  }
})();
