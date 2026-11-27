import { Hct, SchemeTonalSpot, argbFromHex, hexFromArgb } from "@material/material-color-utilities";

const hueBins = 24;

const accents = {
  "primary": "primary", "on-primary": "onPrimary",
  "primary-container": "primaryContainer", "on-primary-container": "onPrimaryContainer",
  "secondary": "secondary", "on-secondary": "onSecondary",
  "secondary-container": "secondaryContainer", "on-secondary-container": "onSecondaryContainer",
  "tertiary": "tertiary", "on-tertiary": "onTertiary",
  "tertiary-container": "tertiaryContainer", "on-tertiary-container": "onTertiaryContainer",
};
const openUiRoles = {
  foreground: "surface", textNeutralPrimary: "on-surface", textNeutralSecondary: "variant",
  intelligentSurface: "surface-container", intelligentSurfaceStrong: "surface-high",
  intelligentBorder: "outline-variant", intelligentFocus: "primary", intelligentRangeThumb: "primary",
  intelligentChart1: "primary", intelligentChart2: "secondary", intelligentChart3: "tertiary",
};

export function applyAccent(seed, dark) {
  const root = document.documentElement;
  root.dataset.theme = dark ? "dark" : "light";
  // Android uses MaterialKolor 5.x Tonal Spot for preset accents over the base surfaces.
  const scheme = /^#[\da-f]{6}$/i.test(seed || "")
    ? new SchemeTonalSpot(Hct.fromInt(argbFromHex(seed)), dark, 0, "2021", "phone") : null;
  for (const [token, role] of Object.entries(accents)) {
    if (scheme) root.style.setProperty("--" + token, hexFromArgb(scheme[role]));
    else root.style.removeProperty("--" + token);
  }
  const style = getComputedStyle(root);
  return Object.fromEntries(Object.entries({
    ...openUiRoles, selectionBackground: "primary", selectionForeground: "on-primary",
  }).map(([name, token]) => [name, style.getPropertyValue("--" + token).trim()]));
}

function accentFromPixels(data) {
  const bins = Array.from({ length: hueBins }, () => [0, 0, 0, 0]);
  let visible = 0;
  for (let index = 0; index < data.length; index += 4) {
    const alpha = data[index + 3] / 255;
    visible += alpha;
    const red = data[index];
    const green = data[index + 1];
    const blue = data[index + 2];
    const high = Math.max(red, green, blue);
    const range = high - Math.min(red, green, blue);
    if (range < 12 || alpha === 0) continue;
    const hue = high === red ? (green - blue) / range
      : high === green ? 2 + (blue - red) / range : 4 + (red - green) / range;
    const bin = bins[(Math.round(hue * hueBins / 6) + hueBins) % hueBins];
    const weight = alpha * Math.min(1, range / 48);
    bin[0] += weight;
    bin[1] += red * weight;
    bin[2] += green * weight;
    bin[3] += blue * weight;
  }
  // A color family needs visible coverage. A vivid detail in a mostly neutral or opposite-hued photo
  // should not set the theme, and adjacent bins must agree across the red wraparound.
  let best = 0;
  let score = 0;
  for (let index = 0; index < hueBins; index++) {
    if (bins[index][0] === 0) continue;
    const support = bins[index][0] * 2 + bins[(index + hueBins - 1) % hueBins][0] + bins[(index + 1) % hueBins][0];
    if (support > score) { best = index; score = support; }
  }
  const [weight, red, green, blue] = bins[best];
  if (score < visible * .08 || weight === 0) return "";
  return hexFromArgb(0xff000000 | Math.round(red / weight) << 16 | Math.round(green / weight) << 8 | Math.round(blue / weight));
}

export function backgroundAccent(media) {
  const width = media.naturalWidth ?? media.videoWidth ?? media.width;
  const height = media.naturalHeight ?? media.videoHeight ?? media.height;
  const scale = Math.min(1, 96 / Math.max(width, height));
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(width * scale));
  canvas.height = Math.max(1, Math.round(height * scale));
  try {
    const context = canvas.getContext("2d", { willReadFrequently: true });
    context.drawImage(media, 0, 0, canvas.width, canvas.height);
    return accentFromPixels(context.getImageData(0, 0, canvas.width, canvas.height).data);
  } catch {
    return "";
  }
}
