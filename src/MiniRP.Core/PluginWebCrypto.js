(function installWebCrypto(entropy) {
    "use strict";

    const Bytes = Uint8Array;
    const Buffer = ArrayBuffer;
    const PromiseType = Promise;
    const stringify = JSON.stringify.bind(JSON);
    const parse = JSON.parse.bind(JSON);
    const define = Object.defineProperty;
    const get = (prototype, field) => Function.call.bind(Object.getOwnPropertyDescriptor(prototype, field).get);
    const typed = Object.getPrototypeOf(Bytes.prototype);
    const typeName = get(typed, Symbol.toStringTag);
    const typedBuffer = get(typed, "buffer");
    const typedOffset = get(typed, "byteOffset");
    const typedLength = get(typed, "byteLength");
    const viewBuffer = get(DataView.prototype, "buffer");
    const viewOffset = get(DataView.prototype, "byteOffset");
    const viewLength = get(DataView.prototype, "byteLength");
    const bufferLength = get(Buffer.prototype, "byteLength");
    const isView = Buffer.isView.bind(Buffer);
    const fill = Function.call.bind(Bytes.prototype.set);
    const hex = Array.from({ length: 256 }, (_, index) => index.toString(16).padStart(2, "0"));
    const integers = /^(?:Int(?:8|16|32)|Uint(?:8|16|32)|Uint8Clamped|BigInt64|BigUint64)Array$/;
    const token = {};
    const pending = new Map();
    const keys = new WeakMap();
    const requests = [];
    const released = [];
    let nextId = 0;
    const finalizers = new FinalizationRegistry(id => released.push(id));

    const DOMError = globalThis.DOMException ?? class DOMException extends Error {
        constructor(message = "", name = "Error") {
            super(String(message));
            define(this, "name", { value: String(name), configurable: true });
        }
        get code() {
            return { IndexSizeError: 1, InvalidCharacterError: 5, NotSupportedError: 9,
                InvalidStateError: 11, SyntaxError: 12, InvalidAccessError: 15, TypeMismatchError: 17,
                SecurityError: 18, AbortError: 20, QuotaExceededError: 22, DataCloneError: 25 }[this.name] ?? 0;
        }
        get [Symbol.toStringTag]() { return "DOMException"; }
    };

    function bytesFromHex(input) {
        const result = new Bytes(input.length / 2);
        for (let i = 0; i < result.length; i++) result[i] = Number.parseInt(input.slice(i * 2, i * 2 + 2), 16);
        return result;
    }

    function bufferBytes(input) {
        if (isView(input)) {
            if (typeName(input)) return new Bytes(typedBuffer(input), typedOffset(input), typedLength(input));
            return new Bytes(viewBuffer(input), viewOffset(input), viewLength(input));
        }
        try { bufferLength(input); }
        catch { return null; }
        return new Bytes(input);
    }

    // Include inherited Web IDL dictionary members as well as own properties.
    const dictionaryFields = ["name", "hash", "length", "iv", "additionalData", "tagLength", "counter", "public",
        "publicExponent", "modulusLength", "namedCurve", "salt", "saltLength", "label", "iterations", "info", "context",
        "kty", "use", "key_ops", "alg", "ext", "crv", "x", "y", "d", "n", "e", "p", "q", "dp", "dq", "qi", "oth", "k"];
    function pack(input, visiting = new Set()) {
        if (input === undefined) return ["undefined"];
        if (input === null || typeof input === "string" || typeof input === "boolean") return input;
        if (typeof input === "number") return Number.isFinite(input) ? input : ["number", String(input)];
        if (typeof input === "bigint") return ["bigint", String(input)];
        if (typeof input !== "object") throw new TypeError("Unsupported Web Crypto argument.");
        const key = keys.get(input);
        if (key) return ["key", key.id];
        const bytes = bufferBytes(input);
        if (bytes) {
            let encoded = "";
            for (const byte of bytes) encoded += hex[byte];
            return ["bytes", encoded];
        }
        if (visiting.has(input)) throw new TypeError("Circular Web Crypto arguments.");
        visiting.add(input);
        const result = Array.isArray(input) ? ["array", Array.from(input, item => pack(item, visiting))]
            : ["object", [...new Set([...Object.keys(input), ...dictionaryFields.filter(field => field in input)])]
                .map(field => [field, pack(input[field], visiting)])];
        visiting.delete(input);
        return result;
    }

    function unpack(input) {
        if (!Array.isArray(input)) return input;
        const [type, value] = input;
        switch (type) {
            case "undefined": return undefined;
            case "bytes": return bytesFromHex(value).buffer;
            case "view": return bytesFromHex(value);
            case "array": return value.map(unpack);
            case "object": return Object.fromEntries(value.map(([field, item]) => [field, unpack(item)]));
            case "key": {
                const meta = unpack(input[2]);
                const key = new CryptoKey(token, { ...meta, id: value });
                finalizers.register(key, value);
                return key;
            }
            default: throw new DOMError("Invalid Web Crypto response.", "OperationError");
        }
    }

    function request(receiver, method, args, minimum) {
        return new PromiseType((resolve, reject) => {
            if (receiver !== subtle) throw new TypeError("Illegal invocation.");
            if (args.length < minimum) throw new TypeError("Missing required Web Crypto arguments.");
            const id = ++nextId;
            requests.push({ id, method, args: Array.from(args, item => pack(item)) });
            pending.set(id, { resolve, reject });
        });
    }

    function keyProperty(key, field) {
        const info = keys.get(key);
        if (!info) throw new TypeError("Illegal invocation.");
        return info[field];
    }
    class CryptoKey {
        constructor(identity, metadata) {
            if (identity !== token) throw new TypeError("Illegal constructor.");
            keys.set(this, metadata);
        }
        get type() { return keyProperty(this, "type"); }
        get extractable() { return keyProperty(this, "extractable"); }
        get algorithm() { return keyProperty(this, "algorithm"); }
        get usages() { return keyProperty(this, "usages"); }
        get [Symbol.toStringTag]() { return "CryptoKey"; }
    }
    class SubtleCrypto {
        constructor(identity) { if (identity !== token) throw new TypeError("Illegal constructor."); }
        encrypt(algorithm, key, data) { return request(this, "encrypt", arguments, 3); }
        decrypt(algorithm, key, data) { return request(this, "decrypt", arguments, 3); }
        sign(algorithm, key, data) { return request(this, "sign", arguments, 3); }
        verify(algorithm, key, signature, data) { return request(this, "verify", arguments, 4); }
        digest(algorithm, data) { return request(this, "digest", arguments, 2); }
        generateKey(algorithm, extractable, usages) { return request(this, "generateKey", arguments, 3); }
        deriveKey(algorithm, baseKey, derivedKeyType, extractable, usages) { return request(this, "deriveKey", arguments, 5); }
        deriveBits(algorithm, baseKey, length) { return request(this, "deriveBits", arguments, 2); }
        importKey(format, data, algorithm, extractable, usages) { return request(this, "importKey", arguments, 5); }
        exportKey(format, key) { return request(this, "exportKey", arguments, 2); }
        wrapKey(format, key, wrappingKey, algorithm) { return request(this, "wrapKey", arguments, 4); }
        unwrapKey(format, data, key, algorithm, unwrappedKeyAlgorithm, extractable, usages) {
            return request(this, "unwrapKey", arguments, 7);
        }
        getPublicKey(key, usages) { return request(this, "getPublicKey", arguments, 2); }
        encapsulateBits(algorithm, key) { return request(this, "encapsulateBits", arguments, 2); }
        encapsulateKey(algorithm, key, sharedKeyAlgorithm, extractable, usages) {
            return request(this, "encapsulateKey", arguments, 5);
        }
        decapsulateBits(algorithm, key, ciphertext) { return request(this, "decapsulateBits", arguments, 3); }
        decapsulateKey(algorithm, key, ciphertext, sharedKeyAlgorithm, extractable, usages) {
            return request(this, "decapsulateKey", arguments, 6);
        }
        get [Symbol.toStringTag]() { return "SubtleCrypto"; }
    }
    const subtle = new SubtleCrypto(token);
    class Crypto {
        constructor(identity) { if (identity !== token) throw new TypeError("Illegal constructor."); }
        get subtle() {
            if (this !== crypto) throw new TypeError("Illegal invocation.");
            return subtle;
        }
        getRandomValues(array) {
            if (this !== crypto || !isView(array)) throw new TypeError("Expected an integer typed array.");
            if (!integers.test(typeName(array))) throw new DOMError("Expected an integer typed array.", "TypeMismatchError");
            const target = new Bytes(typedBuffer(array), typedOffset(array), typedLength(array));
            if (target.length > 65536) throw new DOMError("At most 65,536 bytes can be filled per call.", "QuotaExceededError");
            if (target.length) fill(target, bytesFromHex(entropy(target.length)));
            return array;
        }
        randomUUID() {
            if (this !== crypto) throw new TypeError("Illegal invocation.");
            const bytes = bytesFromHex(entropy(16));
            bytes[6] = (bytes[6] & 0x0f) | 0x40;
            bytes[8] = (bytes[8] & 0x3f) | 0x80;
            const digits = Array.from(bytes, byte => hex[byte]).join("");
            return [digits.slice(0, 8), digits.slice(8, 12), digits.slice(12, 16), digits.slice(16, 20), digits.slice(20)].join("-");
        }
        get [Symbol.toStringTag]() { return "Crypto"; }
    }
    const crypto = new Crypto(token);
    for (const [name, value] of Object.entries({ Crypto, CryptoKey, SubtleCrypto, DOMException: DOMError }))
        if (!(name in globalThis)) define(globalThis, name, { value, writable: true, configurable: true });
    define(globalThis, "crypto", { get: () => crypto, enumerable: true, configurable: true });

    return {
        take() {
            return requests.length || released.length ? stringify({ requests: requests.splice(0), release: released.splice(0) }) : "";
        },
        reply(payload) {
            for (const { id, error, value } of parse(payload)) {
                const callback = pending.get(id);
                if (!callback) continue;
                pending.delete(id);
                if (error) {
                    const failure = error.name === "TypeError" ? new TypeError(error.message)
                        : error.name === "RangeError" ? new RangeError(error.message)
                        : new DOMError(error.message, error.name);
                    callback.reject(failure);
                } else {
                    try { callback.resolve(unpack(value)); }
                    catch (failure) { callback.reject(failure); }
                }
            }
        },
    };
})
