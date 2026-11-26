const fixture = "fixture-secret-storage-value";
const id = "test-token";
const ok = () => Uint8Array.from("secret-storage-ok", char => char.charCodeAt(0));

globalThis.handlers = {
    async write(_event, { secretStorage }) {
        if (await secretStorage.getSecret(id) !== null) throw new Error("Expected an empty store.");
        await secretStorage.setSecret(id, fixture);
        await secretStorage.setSecret("empty", "");
        if ((await secretStorage.listSecrets()).join(",") !== "empty,test-token") throw new Error("Unexpected IDs.");
        return ok();
    },
    async read(_event, { secretStorage }) {
        if (await secretStorage.getSecret(id) !== fixture || await secretStorage.getSecret("empty") !== "")
            throw new Error("Stored secret was not available.");
        return ok();
    },
    async remove(_event, { secretStorage }) {
        await secretStorage.deleteSecret(id);
        await secretStorage.deleteSecret(id);
        return ok();
    },
    async missing(_event, { secretStorage }) {
        if (await secretStorage.getSecret(id) !== null || (await secretStorage.listSecrets()).includes(id))
            throw new Error("Secret was visible outside its namespace or after deletion.");
        return ok();
    },
};
