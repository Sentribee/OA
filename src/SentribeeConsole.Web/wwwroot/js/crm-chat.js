(() => {
  const app = document.querySelector(".chatgpt-app");
  const form = document.getElementById("chat-form");
  if (!app || !form) {
    return;
  }

  const log = document.getElementById("chat-log");
  const textarea = document.getElementById("chat-message");
  const fileInput = document.getElementById("chat-image");
  const fileButton = document.getElementById("chat-image-button");
  const fileName = document.getElementById("chat-image-name");
  const conversationId = document.getElementById("conversation-id");
  const corpId = app.dataset.corpId;
  if (app.dataset.conversationId) {
    conversationId.value = app.dataset.conversationId;
  }

  const appendMessage = (role, text, imageUrl) => {
    const row = document.createElement("div");
    row.className = `chatgpt-message ${role}`;
    const bubble = document.createElement("div");
    bubble.className = "chatgpt-bubble";

    if (imageUrl) {
      const image = document.createElement("img");
      image.src = imageUrl;
      image.alt = "";
      bubble.appendChild(image);
    }

    if (text) {
      const body = document.createElement("div");
      body.textContent = text;
      bubble.appendChild(body);
    }

    row.appendChild(bubble);
    log.appendChild(row);
    log.scrollTop = log.scrollHeight;
    return row;
  };

  fileButton.addEventListener("click", () => fileInput.click());
  fileInput.addEventListener("change", () => {
    fileName.textContent = fileInput.files.length ? fileInput.files[0].name : "";
  });

  textarea.addEventListener("input", () => {
    textarea.style.height = "auto";
    textarea.style.height = `${Math.min(textarea.scrollHeight, 160)}px`;
  });

  textarea.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      form.requestSubmit();
    }
  });

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    const text = textarea.value.trim();
    const imageFile = fileInput.files[0];
    if (!text && !imageFile) {
      return;
    }

    const previewUrl = imageFile ? URL.createObjectURL(imageFile) : null;
    appendMessage("user", text, previewUrl);
    const pending = appendMessage("assistant", "正在输入…", null);
    textarea.value = "";
    textarea.style.height = "auto";
    fileInput.value = "";
    fileName.textContent = "";

    const formData = new FormData(form);
    formData.set("message", text);
    if (imageFile) {
      formData.set("image", imageFile);
    }

    try {
      const response = await fetch(`/chat/${encodeURIComponent(corpId)}?handler=Send`, {
        method: "POST",
        body: formData,
        headers: {
          "RequestVerificationToken": form.querySelector("input[name='__RequestVerificationToken']").value
        }
      });
      const payload = await response.json();
      pending.remove();
      if (!response.ok || !payload.success) {
        appendMessage("assistant", payload.message || "刚刚没发出去。你稍等一下，再发我一次。", null);
        return;
      }

      conversationId.value = payload.conversationId;
      appendMessage("assistant", payload.reply, null);
    } catch (error) {
      pending.remove();
      appendMessage("assistant", "网络好像断了一下。你再发一次，我继续看。", null);
    } finally {
      if (previewUrl) {
        URL.revokeObjectURL(previewUrl);
      }
    }
  });
})();
