import messages from "virtual:minirp-languages";

export const locales = Object.keys(messages).sort((a, b) => a.localeCompare(b));
const rightToLeft = new Set(["ar", "fa", "he"]);
const desktop = {
  en: {
    browser_runtime_title: "Browser runtime",
    browser_runtime_explanation: "MiniRP needs CEF for its WebUI window and local page fetching. Download size is %1$s MiB.",
    browser_runtime_continue: "Download and continue",
    browser_runtime_later: "Later",
    browser_runtime_downloading: "Downloading CEF",
    browser_runtime_verifying: "Verifying the download",
    browser_runtime_extracting: "Installing CEF",
    browser_runtime_checking: "Checking browser capabilities",
  },
  "zh-Hans": {
    browser_runtime_title: "浏览器运行时",
    browser_runtime_explanation: "MiniRP 需要 CEF 来打开 WebUI 窗口和抓取网页。下载大小为 %1$s MiB。",
    browser_runtime_continue: "下载并继续",
    browser_runtime_later: "稍后",
    browser_runtime_downloading: "正在下载 CEF",
    browser_runtime_verifying: "正在验证下载",
    browser_runtime_extracting: "正在安装 CEF",
    browser_runtime_checking: "正在检查浏览器能力",
  },
  "zh-Hant": {
    browser_runtime_title: "瀏覽器執行階段",
    browser_runtime_explanation: "MiniRP 需要 CEF 來開啟 WebUI 視窗和擷取網頁。下載大小為 %1$s MiB。",
    browser_runtime_continue: "下載並繼續",
    browser_runtime_later: "稍後",
    browser_runtime_downloading: "正在下載 CEF",
    browser_runtime_verifying: "正在驗證下載",
    browser_runtime_extracting: "正在安裝 CEF",
    browser_runtime_checking: "正在檢查瀏覽器功能",
  },
};

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
  document.documentElement.dir = rightToLeft.has(locale.split("-")[0]) ? "rtl" : "ltr";
}

export function translator(locale) {
  return (key, ...values) => {
    const message = desktop[locale]?.[key] || messages[locale]?.[key] || desktop.en[key] || messages.en[key] || key;
    let next = 0;
    return message.replace(/%(?:(\d+)\$)?[sd]/g, (match, index) => values[index ? Number(index) - 1 : next++] ?? match);
  };
}

export function languageName(locale) {
  return new Intl.DisplayNames([locale], { type: "language" }).of(locale);
}
