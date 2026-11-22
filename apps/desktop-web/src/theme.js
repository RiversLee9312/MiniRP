import { Hct, QuantizerCelebi, SchemeTonalSpot, argbFromHex, hexFromArgb } from "@material/material-color-utilities";

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
  return Object.fromEntries(Object.entries(openUiRoles).map(([name, token]) => [name, style.getPropertyValue("--" + token).trim()]));
}

function swatchScore(color, count, population) {
  const channels = [color >>> 16 & 255, color >>> 8 & 255, color & 255].map((channel) => channel / 255);
  const high = Math.max(...channels);
  const low = Math.min(...channels);
  const lightness = (high + low) / 2;
  const range = high - low;
  const saturation = range === 0 ? 0 : range / (1 - Math.abs(2 * lightness - 1));
  // Prefer a vibrant mid-tone like the Android background picker.
  if (lightness < .3 || lightness > .7 || saturation < .35) return -1;
  return .24 * saturation + .52 * (1 - Math.abs(lightness - .5)) + .24 * count / population;
}

export function backgroundAccent(img) {
  const scale = Math.min(1, 96 / Math.max(img.naturalWidth, img.naturalHeight));
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(img.naturalWidth * scale));
  canvas.height = Math.max(1, Math.round(img.naturalHeight * scale));
  try {
    const context = canvas.getContext("2d", { willReadFrequently: true });
    context.drawImage(img, 0, 0, canvas.width, canvas.height);
    const { data } = context.getImageData(0, 0, canvas.width, canvas.height);
    const pixels = [];
    for (let index = 0; index < data.length; index += 4)
      if (data[index + 3] === 255)
        pixels.push((0xff000000 | data[index] << 16 | data[index + 1] << 8 | data[index + 2]) >>> 0);
    if (!pixels.length) return "";
    const swatches = [...QuantizerCelebi.quantize(pixels, 16)].sort((a, b) => b[1] - a[1]);
    const [dominant, population] = swatches[0];
    let best = dominant;
    let score = -1;
    for (const [color, count] of swatches) {
      const candidate = swatchScore(color, count, population);
      if (candidate > score) {
        best = color;
        score = candidate;
      }
    }
    return hexFromArgb(best);
  } catch {
    return "";
  }
}
