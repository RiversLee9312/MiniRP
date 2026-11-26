export function makeSample(value = 42) {
    let remaining = 1_000_003;
    return new ReadableStream({
        pull(controller) {
            if (remaining === 0) {
                controller.close();
                return;
            }
            const chunk = new Uint8Array(Math.min(remaining, 65536));
            chunk.fill(value);
            remaining -= chunk.length;
            controller.enqueue(chunk);
        },
    });
}

export async function countBytes(body) {
    let bytes = 0;
    for await (const chunk of body) bytes += chunk.byteLength;
    return bytes;
}
