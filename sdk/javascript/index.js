export function defineHandlers(handlers) {
    return handlers;
}

// Keep tag validation and lookup truncation consistent with manifest localization.
const languageTag = /^(?:(?:[a-z]{2,3}(?:-[a-z]{3}){0,3}|[a-z]{4}|[a-z]{5,8})(?:-[a-z]{4})?(?:-(?:[a-z]{2}|[0-9]{3}))?(?:-(?:[a-z0-9]{5,8}|[0-9][a-z0-9]{3}))*(?:-[0-9a-wy-z](?:-[a-z0-9]{2,8})+)*(?:-x(?:-[a-z0-9]{1,8})+)?|x(?:-[a-z0-9]{1,8})+|en-gb-oed|i-(?:ami|bnn|default|enochian|hak|klingon|lux|mingo|navajo|pwn|tao|tay|tsu)|sgn-(?:be-fr|be-nl|ch-de))$(?![\s\S])/i;

function localeKey(value) {
    if (typeof value !== "string" || value.length > 128 || !languageTag.test(value)) return "";
    const name = value.toLowerCase();
    if (name.startsWith("x-")) return name;
    const variants = new Set();
    const extensions = new Set();
    for (const part of name.split("-").slice(1)) {
        if (part === "x") break;
        if (part.length === 1) {
            if (extensions.has(part)) return "";
            extensions.add(part);
        } else if (!extensions.size && (part.length >= 5 || /^[0-9][a-z0-9]{3}$/.test(part))) {
            if (variants.has(part)) return "";
            variants.add(part);
        }
    }
    return name;
}

export function getLocale(api, fallback = "en") {
    const locale = api?.environment?.locale;
    return localeKey(locale) ? locale : localeKey(fallback) ? fallback : "en";
}

export function localePointer(...segments) {
    if (!segments.length) throw new TypeError("Provide at least one path segment.");
    return "/" + segments.map(segment => String(segment).replaceAll("~", "~0").replaceAll("/", "~1")).join("/");
}

function* localeParents(locale) {
    const parts = locale ? locale.split("-") : [];
    while (parts.length) {
        yield parts.join("-");
        parts.pop();
        if (parts.at(-1)?.length === 1) parts.pop();
    }
}

function platformLocaleAlias(locale) {
    const parts = locale.split("-");
    if (parts[0] === "pt") return "pt-br";
    if (parts[0] !== "zh") return;
    let index = 1;
    while (/^[a-z]{3}$/.test(parts[index] ?? "")) index++;
    return ["hant", "tw", "hk", "mo"].includes(parts[index]) ? "zh-hant" : "zh-hans";
}

function translation(catalog, key) {
    if (!catalog || !Object.hasOwn(catalog, key)) return;
    const text = catalog[key];
    if (typeof text === "string" && text.trim()) return text;
}

export function defineI18n({ defaultLocale, locales, source }) {
    const fallback = localeKey(defaultLocale);
    if (defaultLocale !== undefined && !fallback) throw new TypeError("Expected a BCP 47 default locale.");
    const catalogs = new Map();
    for (const [locale, catalog] of Object.entries(locales)) {
        const name = localeKey(locale);
        if (!name || catalogs.has(name)) throw new TypeError("Expected unique BCP 47 translation locales.");
        catalogs.set(name, catalog);
    }
    const defaults = [...localeParents(fallback)];
    return (locale, key, ...values) => {
        if (!key.trim()) return "";
        const requested = localeKey(locale);
        let message;
        for (const name of new Set([...localeParents(requested), platformLocaleAlias(requested), ...defaults])) {
            message = translation(catalogs.get(name), key);
            if (message !== undefined) break;
        }
        message ??= translation(source, key) ?? key;
        return message.replace(/\{([1-9][0-9]*)\}/g, (token, position) => {
            const value = values[Number(position) - 1];
            return value == null ? token : String(value);
        });
    };
}

export function hookUrl(packageName, id) {
    return "minirp://actions/" + packageName + "/hooks/" + id;
}

export function eventUrl(packageName, id) {
    return "minirp://actions/" + packageName + "/events/" + id;
}

export function assetUrl(packageName, id) {
    return "minirp://assets/" + packageName + "/" + id;
}

export function workspaceId(value) {
    const canonical = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
    if (typeof value !== "string" || value.length !== 36 || !canonical.test(value) ||
        value === "00000000-0000-0000-0000-000000000000") {
        throw new TypeError("Expected a canonical lowercase UUID.");
    }
    return value;
}
