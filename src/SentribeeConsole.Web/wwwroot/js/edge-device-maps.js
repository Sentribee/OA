window.initEdgeDeviceAddressAutocomplete = function () {
  const input = document.getElementById("edge-address-input");
  if (!input || !window.google?.maps?.places) {
    return;
  }

  if (input.dataset.googleAutocompleteInitialized === "true") {
    return;
  }
  input.dataset.googleAutocompleteInitialized = "true";

  const latitude = document.getElementById("edge-latitude");
  const longitude = document.getElementById("edge-longitude");
  const placeId = document.getElementById("edge-place-id");
  const streetViewUrl = document.getElementById("edge-street-view-url");
  const preview = document.getElementById("edge-address-preview");
  const locationPreview = document.getElementById("edge-location-preview");

  const autocomplete = new google.maps.places.Autocomplete(input, {
    fields: ["formatted_address", "geometry", "place_id"],
    types: ["address"]
  });

  input.addEventListener("input", () => {
    latitude.value = "";
    longitude.value = "";
    placeId.value = "";
    streetViewUrl.value = "";
    preview?.classList.add("d-none");
  });

  autocomplete.addListener("place_changed", () => {
    const place = autocomplete.getPlace();
    if (!place.geometry?.location) {
      return;
    }

    const lat = place.geometry.location.lat();
    const lng = place.geometry.location.lng();
    const address = place.formatted_address || input.value;
    const staticStreetViewUrl = buildStreetViewUrl(lat, lng);

    input.value = address;
    latitude.value = lat.toFixed(7);
    longitude.value = lng.toFixed(7);
    placeId.value = place.place_id || "";
    streetViewUrl.value = staticStreetViewUrl;

    if (locationPreview) {
      locationPreview.textContent = `${latitude.value}, ${longitude.value}`;
    }
    preview?.classList.remove("d-none");
  });
};

window.initEdgeDeviceDetailsMap = function () {
  const mapElement = document.getElementById("edge-device-map");
  if (!mapElement || !window.google?.maps) {
    return;
  }

  const lat = Number.parseFloat(mapElement.dataset.lat || "");
  const lng = Number.parseFloat(mapElement.dataset.lng || "");
  if (!Number.isFinite(lat) || !Number.isFinite(lng)) {
    return;
  }

  const position = { lat, lng };
  const map = new google.maps.Map(mapElement, {
    center: position,
    zoom: 16,
    mapTypeControl: false,
    streetViewControl: true,
    fullscreenControl: true
  });

  new google.maps.Marker({
    position,
    map,
    title: mapElement.dataset.title || "Edge device"
  });
};

function buildStreetViewUrl(lat, lng) {
  return `https://maps.googleapis.com/maps/api/streetview?size=640x360&location=${lat.toFixed(7)},${lng.toFixed(7)}`;
}

document.addEventListener("DOMContentLoaded", () => {
  const versionSelect = document.getElementById("edge-ai-version-select");
  const requiredText = document.getElementById("edge-ai-required-devices");
  const emptyState = document.getElementById("edge-no-required-devices");
  const rows = Array.from(document.querySelectorAll(".edge-endpoint-row[data-catalog-name]"));

  const applyRequiredDevices = () => {
    const selected = versionSelect?.selectedOptions?.[0];
    const required = (selected?.dataset.requiredDevices || "")
      .split("|")
      .map((value) => value.trim().toLowerCase())
      .filter(Boolean);

    rows.forEach((row) => {
      const catalogName = (row.dataset.catalogName || "").trim().toLowerCase();
      const isRequired = required.includes(catalogName);
      row.classList.toggle("edge-endpoint-row-required", isRequired);
      row.querySelector(".edge-endpoint-suggested")?.classList.toggle("d-none", !isRequired);
      const selectedInput = row.querySelector(".edge-endpoint-selected");
      if (selectedInput) {
        selectedInput.value = "true";
      }
    });

    if (requiredText) {
      requiredText.textContent = required.length
        ? `Suggested external devices from this version: ${required.join(", ")}`
        : "No external device requirements were detected from this code version. Configure at least one device below.";
    }
    emptyState?.classList.add("d-none");
  };

  versionSelect?.addEventListener("change", applyRequiredDevices);
  applyRequiredDevices();
});
