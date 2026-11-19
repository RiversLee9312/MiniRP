(() => {
    "use strict";
    const parse = JSON.parse.bind(JSON);
    const stringify = JSON.stringify.bind(JSON);
    const freeze = Object.freeze;
    const Bytes = Uint8Array;
    const Stream = ReadableStream;
    const slice = Function.call.bind(Bytes.prototype.slice);
    const reader = Function.call.bind(Stream.prototype.getReader);
    const chunkSize = 65536;
    for (const name of ["WebAssembly", "SharedArrayBuffer", "Atomics"]) {
        if (Object.prototype.hasOwnProperty.call(globalThis, name))
            Object.defineProperty(globalThis, name, { value: undefined, writable: false, configurable: false });
    }
    function invocation() {
        let nextId = 0;
        const pending = new Map();
        const requests = [];
        let state = { done: false };
        let output;
        let remainder;
        let bytes;
        let closed = false;
        const call = (method, args) => new Promise((resolve, reject) => {
            if (closed) throw new Error("Plugin call is closed.");
            if (pending.size >= 24) throw new Error("Too many outstanding plugin requests.");
            const id = ++nextId;
            pending.set(id, { resolve, reject });
            requests.push({ id, method, args });
        });
        const bind = (group, names) => freeze(Object.fromEntries(names.map(name =>
            [name, (args = {}) => call(group + "." + name, args)])));
        async function openContent(method, args) {
            const { handle, length, mimeType } = await call(method, args);
            const body = new Stream({
                type: "bytes",
                async pull(controller) {
                    const part = await call("assets.pull", { handle });
                    if (part === null || part === undefined) {
                        controller.close();
                        controller.byobRequest?.respond(0);
                    } else controller.enqueue(part);
                },
                cancel() { return call("assets.close", { handle }); },
            });
            return freeze({ body, length, mimeType });
        }
        const openAsset = (url, options = {}) => openContent("assets.open", { url, options });

        async function readAsset(url, options) {
            const opened = await openAsset(url, options);
            const source = reader(opened.body);
            try {
                const parts = [];
                let length = 0;
                for (;;) {
                    const part = await source.read();
                    if (part.done) break;
                    length += part.value.byteLength;
                    if (length > 8 * 1024 * 1024) throw new RangeError("Use assets.open() for assets larger than 8 MiB.");
                    parts.push(part.value);
                }
                const result = new Bytes(length);
                let offset = 0;
                for (const part of parts) {
                    result.set(part, offset);
                    offset += part.byteLength;
                }
                return freeze({ mimeType: opened.mimeType, bytes: result });
            } finally { await source.cancel(); }
        }

        function fail(error) { state = { done: true, error: String(error?.message ?? error) }; }
        return freeze({
            start(payload) {
                const info = parse(payload);
                const api = freeze({
                    environment: freeze(info.environment),
                    harness: bind("harness", ["readSession", "readChat", "listPresets", "listChats", "listRooms", "readRoom", "createChat", "createRoom", "createPreset", "send", "steer", "insertSystemMessage", "createTemporarySession", "runTemporary", "closeTemporary", "renameChat", "renameRoom", "generateTitle", "getPreset", "setSearchMode", "setPrompt", "branchChat", "generate"]),
                    state: bind("state", ["get", "set"]),
                    ...(info.development ? { development: bind("development", ["run"]) } : {}),
                    db: bind("db", ["get", "put", "patch", "delete", "list", "search"]),
                    http: bind("http", ["request"]),
                    picker: bind("picker", ["media", "file"]),
                    attachments: freeze({
                        open: ({ id }) => openContent("attachments.open", { id }),
                        ...(info.legacy ? bind("attachments", ["read"]) : {}),
                    }),
                    settings: bind("settings", ["get"]),
                    actions: bind("actions", ["invoke", "publish"]),
                    assets: freeze({
                        url(id) {
                            if (typeof id !== "string" || !/^[a-z][a-z0-9_-]{0,47}$/.test(id)) throw new TypeError("Invalid asset ID.");
                            return "minirp://assets/" + info.packageName + "/" + id;
                        },
                        open: openAsset, read: readAsset,
                    }),
                    ...(info.legacy ? { url: bind("url", ["open"]) } : {}),
                });
                Promise.resolve().then(() => {
                    const handler = globalThis.handlers?.[info.handler];
                    if (typeof handler !== "function") throw new Error("Missing plugin handler: " + info.handler);
                    return handler(freeze(info.event), api);
                }).then(result => {
                    if (!info.binary) {
                        state = { done: true, result: result ?? null };
                        return;
                    }
                    if (result instanceof Bytes) remainder = result;
                    else if (result instanceof Stream) output = reader(result);
                    else throw new TypeError("An asset handler must return Uint8Array or ReadableStream.");
                    state = { ready: true };
                }).catch(fail);
            },
            reply(payload, chunk) {
                const { id, result, error } = parse(payload);
                const responder = pending.get(id);
                if (!responder) throw new Error("Unknown plugin API request");
                pending.delete(id);
                if (error) responder.reject(new Error(error)); else responder.resolve(chunk ?? result);
            },
            pull() {
                state = {};
                Promise.resolve().then(async () => {
                    while ((!remainder || remainder.byteLength === 0) && output) {
                        const part = await output.read();
                        if (part.done) { output = undefined; break; }
                        if (!(part.value instanceof Bytes)) throw new TypeError("Asset chunks must be Uint8Array.");
                        remainder = part.value;
                    }
                    if (!remainder?.byteLength) { state = { done: true }; return; }
                    bytes = slice(remainder, 0, chunkSize);
                    remainder = remainder.subarray(bytes.byteLength);
                    state = { ready: true, binary: true };
                }).catch(fail);
            },
            finish(abort) {
                closed = true;
                try {
                    if (abort && output) void output.cancel().catch(() => {});
                } finally {
                    for (const responder of pending.values()) responder.reject(new Error("Plugin call is closed."));
                    pending.clear();
                    requests.length = 0;
                    output = remainder = bytes = undefined;
                }
            },
            takeBytes() {
                const chunk = bytes;
                bytes = undefined;
                return chunk;
            },
            poll() { return stringify({ ...state, requests: requests.splice(0) }); }
        });
    }

    const runs = new Map();
    function run(id) {
        const current = runs.get(id);
        if (!current) throw new Error("Plugin call is closed.");
        return current;
    }
    const bridge = freeze({
        start(id, payload) {
            if (runs.has(id) || runs.size >= 32) throw new Error("Too many active calls for this plugin.");
            const current = invocation();
            runs.set(id, current);
            current.start(payload);
        },
        reply(id, payload, bytes) { run(id).reply(payload, bytes); },
        pull(id) { run(id).pull(); },
        takeBytes(id) { return run(id).takeBytes(); },
        poll(id) { return run(id).poll(); },
        finish(id, abort) {
            const current = runs.get(id);
            runs.delete(id);
            current?.finish(abort);
        },
    });
    Object.defineProperty(globalThis, "__minirp", { value: bridge, writable: false, configurable: false });
})();
