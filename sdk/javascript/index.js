export function defineHandlers(handlers) {
    return handlers;
}

export function hookUrl(packageName, id) {
    return "minichat://actions/" + packageName + "/hooks/" + id;
}

export function eventUrl(packageName, id) {
    return "minichat://actions/" + packageName + "/events/" + id;
}

export function assetUrl(packageName, id) {
    return "minichat://assets/" + packageName + "/" + id;
}
