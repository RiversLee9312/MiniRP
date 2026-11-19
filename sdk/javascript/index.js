export function defineHandlers(handlers) {
    return handlers;
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
