import messages from "virtual:minirp-languages";

export const locales = Object.keys(messages).sort((a, b) => a.localeCompare(b));
const rightToLeft = new Set(["ar", "fa", "he"]);
const turnLabels = {
  en: ["User message", "Assistant reply", "Interjection"],
  ar: ["رسالة المستخدم", "رد المساعد", "مداخلة"],
  cs: ["Zpráva uživatele", "Odpověď asistenta", "Vstup do hovoru"],
  da: ["Brugerbesked", "Svar fra assistenten", "Indskud"],
  de: ["Nutzernachricht", "Antwort des Assistenten", "Einwurf"],
  el: ["Μήνυμα χρήστη", "Απάντηση βοηθού", "Παρέμβαση"],
  es: ["Mensaje del usuario", "Respuesta del asistente", "Intervención"],
  fa: ["پیام کاربر", "پاسخ دستیار", "میان‌گفتار"],
  fi: ["Käyttäjän viesti", "Avustajan vastaus", "Välikommentti"],
  fr: ["Message de l’utilisateur", "Réponse de l’assistant", "Intervention"],
  he: ["הודעת המשתמש", "תשובת העוזר", "הערת ביניים"],
  hi: ["उपयोगकर्ता का संदेश", "सहायक का जवाब", "बीच की टिप्पणी"],
  hu: ["Felhasználói üzenet", "Az asszisztens válasza", "Közbeszólás"],
  id: ["Pesan pengguna", "Balasan asisten", "Selaan"],
  it: ["Messaggio dell’utente", "Risposta dell’assistente", "Intervento"],
  ja: ["ユーザーのメッセージ", "アシスタントの返信", "割り込み"],
  ko: ["사용자 메시지", "어시스턴트 답변", "끼어들기"],
  nb: ["Brukermelding", "Svar fra assistenten", "Innspill"],
  nl: ["Gebruikersbericht", "Antwoord van de assistent", "Tussenkomst"],
  pl: ["Wiadomość użytkownika", "Odpowiedź asystenta", "Wtrącenie"],
  "pt-BR": ["Mensagem do usuário", "Resposta do assistente", "Intervenção"],
  ro: ["Mesajul utilizatorului", "Răspunsul asistentului", "Intervenție"],
  ru: ["Сообщение пользователя", "Ответ ассистента", "Вставка"],
  sv: ["Användarmeddelande", "Svar från assistenten", "Inpass"],
  th: ["ข้อความผู้ใช้", "คำตอบของผู้ช่วย", "ข้อความแทรก"],
  tr: ["Kullanıcı mesajı", "Asistanın yanıtı", "Araya girme"],
  uk: ["Повідомлення користувача", "Відповідь асистента", "Репліка"],
  vi: ["Tin nhắn người dùng", "Phản hồi của trợ lý", "Lời chen ngang"],
  "zh-Hans": ["用户消息", "助手回复", "插话"],
  "zh-Hant": ["使用者訊息", "助理回覆", "插話"],
};
const turnLabelKeys = ["transcript_user_message", "transcript_assistant_reply", "transcript_interjection"];
const desktop = {
  en: {
    transcript_position: "Scroll chat history",
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
    transcript_position: "滚动聊天记录",
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
    transcript_position: "捲動聊天記錄",
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
    const turnLabel = (turnLabels[locale] || turnLabels.en)[turnLabelKeys.indexOf(key)];
    const message = turnLabel || desktop[locale]?.[key] || messages[locale]?.[key] || desktop.en[key] || messages.en[key] || key;
    let next = 0;
    return message.replace(/%(?:(\d+)\$)?[sd]/g, (match, index) => values[index ? Number(index) - 1 : next++] ?? match);
  };
}

export function languageName(locale) {
  return new Intl.DisplayNames([locale], { type: "language" }).of(locale);
}
