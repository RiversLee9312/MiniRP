(function createWebCryptoProvider(crypto) {
    "use strict";
    const keys = new Map();
    let nextKey = 0;
    const operations = new Set(["encrypt", "decrypt", "sign", "verify", "digest", "generateKey", "deriveKey", "deriveBits",
        "importKey", "exportKey", "wrapKey", "unwrapKey", "getPublicKey",
        "encapsulateBits", "encapsulateKey", "decapsulateBits", "decapsulateKey"]);
    const hex = Array.from({ length: 256 }, (_, index) => index.toString(16).padStart(2, "0"));

    function fromHex(value) {
        const bytes = new Uint8Array(value.length / 2);
        for (let i = 0; i < bytes.length; i++) bytes[i] = Number.parseInt(value.slice(2 * i, 2 * i + 2), 16);
        return bytes;
    }
    function toHex(bytes) {
        let value = "";
        for (const byte of bytes) value += hex[byte];
        return value;
    }

    function decode(input) {
        if (!Array.isArray(input)) return input;
        const [type, value] = input;
        switch (type) {
            case "undefined": return undefined;
            case "number": return Number(value);
            case "bigint": return BigInt(value);
            case "bytes": return fromHex(value);
            case "array": return value.map(decode);
            case "object": return Object.fromEntries(value.map(([field, entry]) => [field, decode(entry)]));
            case "key":
                if (keys.has(value)) return keys.get(value);
                throw new DOMException("This CryptoKey is no longer available.", "InvalidAccessError");
            default: throw new TypeError("Invalid Web Crypto input.");
        }
    }

    function encode(input) {
        if (input === undefined) return ["undefined"];
        if (input === null || typeof input !== "object") return input;
        if (input instanceof CryptoKey) {
            const id = ++nextKey;
            keys.set(id, input);
            return ["key", id, encode({ type: input.type, algorithm: input.algorithm, extractable: input.extractable, usages: input.usages })];
        }
        if (input instanceof ArrayBuffer) return ["bytes", toHex(new Uint8Array(input))];
        if (ArrayBuffer.isView(input)) return ["view", toHex(new Uint8Array(input.buffer, input.byteOffset, input.byteLength))];
        if (Array.isArray(input)) return ["array", input.map(encode)];
        return ["object", Object.entries(input).map(([field, entry]) => [field, encode(entry)])];
    }

    return async function execute(payload) {
        const { requests, release } = JSON.parse(payload);
        for (const id of release) keys.delete(id);
        const replies = await Promise.all(requests.map(async ({ id, method, args }) => {
            try {
                if (!operations.has(method) || typeof crypto.subtle[method] !== "function")
                    throw new DOMException("Unsupported Web Crypto operation.", "NotSupportedError");
                return { id, value: encode(await crypto.subtle[method](...args.map(decode))) };
            } catch (error) {
                return { id, error: { name: error.name || "OperationError", message: String(error.message || error) } };
            }
        }));
        return JSON.stringify(replies);
    };
})
