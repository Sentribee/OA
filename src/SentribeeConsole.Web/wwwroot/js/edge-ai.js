(() => {
  const modal = document.getElementById("create-edge-ai-instance-modal");
  if (!modal) {
    return;
  }

  modal.addEventListener("show.bs.modal", (event) => {
    const button = event.relatedTarget;
    if (!button) {
      return;
    }

    const deviceId = button.getAttribute("data-device-id") || "";
    const deviceName = button.getAttribute("data-device-name") || "";
    const deviceIdInput = modal.querySelector("#edge-ai-device-id");
    const deviceNameInput = modal.querySelector("#edge-ai-device-name");
    const instanceNameInput = modal.querySelector("#Input_InstanceName");

    if (deviceIdInput) {
      deviceIdInput.value = deviceId;
    }
    if (deviceNameInput) {
      deviceNameInput.value = deviceName;
    }
    if (instanceNameInput && !instanceNameInput.value) {
      instanceNameInput.placeholder = `${deviceName} Edge AI instance`;
    }
  });
})();
