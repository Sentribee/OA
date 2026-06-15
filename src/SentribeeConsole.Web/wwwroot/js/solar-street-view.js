(() => {
  const toRad = (degrees) => degrees * Math.PI / 180;
  const toDeg = (radians) => radians * 180 / Math.PI;
  const clamp = (value, min, max) => Math.max(min, Math.min(value, max));

  const dayOfYear = (date) => {
    const start = Date.UTC(date.getUTCFullYear(), 0, 0);
    const current = Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate());
    return Math.floor((current - start) / 86400000);
  };

  const calculateSun = (latitude, longitude, date) => {
    const day = dayOfYear(date);
    const hour = date.getUTCHours() + date.getUTCMinutes() / 60 + date.getUTCSeconds() / 3600;
    const gamma = 2 * Math.PI / 365 * (day - 1 + (hour - 12) / 24);
    const equationOfTime = 229.18 * (
      0.000075 +
      0.001868 * Math.cos(gamma) -
      0.032077 * Math.sin(gamma) -
      0.014615 * Math.cos(2 * gamma) -
      0.040849 * Math.sin(2 * gamma)
    );
    const declination =
      0.006918 -
      0.399912 * Math.cos(gamma) +
      0.070257 * Math.sin(gamma) -
      0.006758 * Math.cos(2 * gamma) +
      0.000907 * Math.sin(2 * gamma) -
      0.002697 * Math.cos(3 * gamma) +
      0.00148 * Math.sin(3 * gamma);

    const timeOffset = equationOfTime + 4 * longitude;
    const trueSolarTime = (hour * 60 + timeOffset + 1440) % 1440;
    const hourAngle = toRad(trueSolarTime / 4 < 0 ? trueSolarTime / 4 + 180 : trueSolarTime / 4 - 180);
    const latRad = toRad(latitude);
    const zenith = Math.acos(
      Math.sin(latRad) * Math.sin(declination) +
      Math.cos(latRad) * Math.cos(declination) * Math.cos(hourAngle)
    );
    const elevation = 90 - toDeg(zenith);
    const azimuth = (toDeg(Math.atan2(
      Math.sin(hourAngle),
      Math.cos(hourAngle) * Math.sin(latRad) - Math.tan(declination) * Math.cos(latRad)
    )) + 180) % 360;

    return { azimuth, elevation };
  };

  const updateStreetViewSun = (element) => {
    const latitude = Number(element.dataset.lat);
    const longitude = Number(element.dataset.lng);
    const heading = Number(element.dataset.heading || 0);
    if (!Number.isFinite(latitude) || !Number.isFinite(longitude)) return;

    const sun = calculateSun(latitude, longitude, new Date());
    const relative = ((sun.azimuth - heading + 540) % 360) - 180;
    const horizontal = 50 + Math.sin(toRad(relative)) * 46;
    const vertical = clamp(82 - sun.elevation * 0.82, 8, 92);
    const opacity = sun.elevation > 0 ? clamp(sun.elevation / 90, 0.1, 0.42) : 0.04;
    const shadeOpacity = sun.elevation > 0 ? clamp(0.34 - sun.elevation / 180, 0.08, 0.28) : 0.36;
    const rayAngle = relative + 90;

    element.style.setProperty("--sun-x", `${horizontal.toFixed(1)}%`);
    element.style.setProperty("--sun-y", `${vertical.toFixed(1)}%`);
    element.style.setProperty("--sun-opacity", opacity.toFixed(2));
    element.style.setProperty("--sun-shade-opacity", shadeOpacity.toFixed(2));
    element.style.setProperty("--sun-ray-angle", `${rayAngle.toFixed(1)}deg`);

    const caption = element.querySelector("[data-sun-caption]");
    if (caption) {
      const direction = relative < -20 ? "left" : relative > 20 ? "right" : "front";
      caption.textContent = sun.elevation > 0
        ? `Sun ${Math.round(sun.elevation)} deg high, light from ${direction}`
        : "Sun below horizon";
    }
  };

  const elements = [...document.querySelectorAll("[data-sun-street-view]")];
  elements.forEach(updateStreetViewSun);
  if (elements.length) {
    window.setInterval(() => elements.forEach(updateStreetViewSun), 60000);
  }
})();
