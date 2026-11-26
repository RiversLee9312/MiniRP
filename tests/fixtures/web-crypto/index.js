const hex = buffer => Array.from(new Uint8Array(buffer), byte => byte.toString(16).padStart(2, "0")).join("");
function check(condition, message) {
    if (!condition) throw new Error(message);
}
async function rejects(name, run) {
    try { await run(); }
    catch (error) {
        check(error.name === name, "Expected " + name + ", got " + error.name);
        return;
    }
    throw new Error("Expected " + name);
}

// Module and classic-script initialization both see the synchronous API.
const initial = crypto.getRandomValues(new BigUint64Array(4));
const initializedKey = crypto.subtle.generateKey({ name: "AES-GCM", length: 256 }, true, ["encrypt", "decrypt"]);
globalThis.handlers = {
    async probe() {
        check(typeof process === "undefined" && typeof require === "undefined" && typeof Buffer === "undefined", "Provider escaped into the plugin.");
        check(initial.length === 4 && initial.some(value => value !== 0n), "Missing initialization entropy.");
        const backing = new Uint8Array(36).fill(255);
        const view = backing.subarray(2, 34);
        check(crypto.getRandomValues(view) === view, "The original view must be returned.");
        check(backing[0] === 255 && backing[1] === 255 && backing[34] === 255 && backing[35] === 255 &&
            view.some(value => value !== 255), "The operation must only fill the supplied view.");
        check(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(crypto.randomUUID()), "Invalid UUID v4.");
        await rejects("TypeMismatchError", () => crypto.getRandomValues(new Float32Array(1)));
        await rejects("QuotaExceededError", () => crypto.getRandomValues(new Uint8Array(65_537)));
        await rejects("TypeError", () => crypto.getRandomValues(new ArrayBuffer(1)));

        const { subtle } = crypto;
        const input = Uint8Array.of(97, 98, 99);
        const digest = subtle.digest(Object.create({ name: "sha-256" }), input);
        check(digest instanceof Promise, "Digest must return a Promise.");
        input.fill(0);
        check(hex(await digest) === "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "Digest or input snapshot differs.");
        await rejects("NotSupportedError", () => subtle.digest("unsupported", input));

        const raw = Uint8Array.of(97, 98, 99);
        const key = await initializedKey;
        check(key instanceof CryptoKey && key.type === "secret" && key.algorithm.length === 256, "Invalid CryptoKey.");
        const gcm = { name: "AES-GCM", iv: crypto.getRandomValues(new Uint8Array(12)) };
        const encrypted = await subtle.encrypt(gcm, key, raw);
        check(hex(await subtle.decrypt(gcm, key, encrypted)) === "616263", "AES-GCM round trip failed.");
        await rejects("TypeError", () => subtle.encrypt(gcm, Object.create(CryptoKey.prototype), raw));
        const jwk = await subtle.exportKey("jwk", key);
        const imported = await subtle.importKey("jwk", jwk, "AES-GCM", false, ["decrypt"]);
        check(hex(await subtle.decrypt(gcm, imported, encrypted)) === "616263", "JWK import failed.");
        await rejects("InvalidAccessError", () => subtle.exportKey("raw", imported));

        const wrapping = await subtle.generateKey({ name: "AES-KW", length: 256 }, false, ["wrapKey", "unwrapKey"]);
        const wrapped = await subtle.wrapKey("raw", key, wrapping, "AES-KW");
        const unwrapped = await subtle.unwrapKey("raw", wrapped, wrapping, "AES-KW", "AES-GCM", false, ["decrypt"]);
        check(hex(await subtle.decrypt(gcm, unwrapped, encrypted)) === "616263", "Key wrap round trip failed.");

        const base = await subtle.importKey("raw", raw, "PBKDF2", false, ["deriveBits", "deriveKey"]);
        const derivation = { name: "PBKDF2", salt: raw, hash: "SHA-256", iterations: 100 };
        const derived = await subtle.deriveBits(derivation, base, 128);
        const hmac = await subtle.deriveKey(derivation, base, { name: "HMAC", hash: "SHA-256", length: 128 }, true, ["sign"]);
        check(hex(await subtle.exportKey("raw", hmac)) === hex(derived), "Derivation output differs.");
        check((await subtle.sign("HMAC", hmac, raw)).byteLength === 32, "HMAC output differs.");

        const rsa = await subtle.generateKey({ name: "RSA-PSS", hash: "SHA-256", modulusLength: 2048,
            publicExponent: Uint8Array.of(1, 0, 1) }, true, ["sign", "verify"]);
        const pss = { name: "RSA-PSS", saltLength: 32 };
        check(rsa.publicKey.algorithm.publicExponent instanceof Uint8Array, "RSA metadata lost its typed array.");
        const signature = await subtle.sign(pss, rsa.privateKey, raw);
        const publicKey = await subtle.importKey("spki", await subtle.exportKey("spki", rsa.publicKey),
            { name: "RSA-PSS", hash: "SHA-256" }, true, ["verify"]);
        check(await subtle.verify(pss, publicKey, signature, raw), "RSA signature verification failed.");
        const privateKey = await subtle.importKey("pkcs8", await subtle.exportKey("pkcs8", rsa.privateKey),
            { name: "RSA-PSS", hash: "SHA-256" }, false, ["sign"]);
        check((await subtle.sign(pss, privateKey, raw)).byteLength === 256, "PKCS8 import failed.");

        const curve = { name: "ECDH", namedCurve: "P-256" };
        const [alice, bob] = await Promise.all([
            subtle.generateKey(curve, false, ["deriveBits"]), subtle.generateKey(curve, false, ["deriveBits"]),
        ]);
        const [ab, ba] = await Promise.all([
            subtle.deriveBits({ name: "ECDH", public: bob.publicKey }, alice.privateKey, 256),
            subtle.deriveBits({ name: "ECDH", public: alice.publicKey }, bob.privateKey, 256),
        ]);
        check(hex(ab) === hex(ba), "ECDH shared secrets differ.");
        return Uint8Array.from("web-crypto-ok", character => character.charCodeAt(0));
    },
};
