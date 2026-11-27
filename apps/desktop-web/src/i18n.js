import messages from "virtual:minirp-languages";

export const locales = Object.keys(messages).sort((a, b) => a.localeCompare(b));
const rightToLeft = new Set(["ar", "fa", "he", "ur"]);
export function resolveLocale(requested = []) {
  for (const value of requested) {
    const exact = locales.find((locale) => locale.toLowerCase() === value.toLowerCase());
    if (exact) return exact;
    const [language, region] = value.toLowerCase().split("-");
    if (language === "zh") return ["tw", "hk", "mo", "hant"].includes(region) ? "zh-Hant" : "zh-Hans";
    const base = locales.find((locale) => locale.split("-")[0] === language);
    if (base) return base;
  }
  return "en";
}

export function setDocumentLocale(locale) {
  document.documentElement.lang = locale;
  document.documentElement.dir = isRightToLeft(locale) ? "rtl" : "ltr";
}

export function isRightToLeft(locale) {
  return rightToLeft.has(locale.split("-")[0]);
}

export function translator(locale) {
  return (key, ...values) => {
    const message = messages[locale]?.[key] || messages.en[key] || key;
    let next = 0;
    return message.replace(/%(?:(\d+)\$)?[sd]/g, (match, index) => values[index ? Number(index) - 1 : next++] ?? match);
  };
}

export function languageName(locale) {
  return new Intl.DisplayNames([locale], { type: "language" }).of(locale);
}
