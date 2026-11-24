import { build } from "esbuild";
import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { cardPrompt, prompt } from "./catalog.mjs";

const here = fileURLToPath(new URL(".", import.meta.url));
const output = join(resolve(process.argv[2] ?? join(here, "dist")), "openui");
await mkdir(output, { recursive: true });
const options = {
    absWorkingDir: here,
    bundle: true,
    format: "esm",
    platform: "browser",
    jsx: "automatic",
    target: "chrome90",
    minify: true,
    legalComments: "linked",
    loader: { ".woff": "file", ".woff2": "file", ".ttf": "file" },
    define: { "process.env.NODE_ENV": '"production"' },
};
// Keep the vector map lazy, and consolidate the library's thousands of icon and
// syntax-highlighting entry points into one APK asset.
await Promise.all([
    build({
        ...options,
        entryPoints: ["surface.jsx"],
        outdir: output,
        plugins: [{
            name: "external-map",
            setup(builder) {
                builder.onResolve({ filter: /^maplibre-gl$/ }, () => ({
                    path: "./maplibre.js",
                    external: true,
                }));
            },
        }],
    }),
    build({
        ...options,
        stdin: {
            contents: 'export { Map, Marker, LngLatBounds, NavigationControl } from "maplibre-gl";',
            resolveDir: here,
        },
        outfile: join(output, "maplibre.js"),
    }),
]);
const index = await readFile(join(here, "index.html"), "utf8");
function toolPage(source) {
    return source.replace(/<html([^>]*)>/, '<html$1 data-surface="tool-presentation">')
        .replace("img-src 'self' https: data: blob:", "img-src 'none'")
        .replace("connect-src 'self' https://tiles.openfreemap.org", "connect-src 'none'")
        .replace("worker-src 'self' blob:", "worker-src 'none'");
}
if (process.argv.includes("--desktop")) {
    const desktop = index.replace("<html>", '<html data-host="desktop">')
        .replace('    <link rel="stylesheet"', '    <link rel="stylesheet" href="/fonts/typography.css">\n    <link rel="stylesheet"')
        .replace('    <script type="module"', '    <script src="desktop-bridge.js"></script>\n    <script type="module"');
    await writeFile(join(output, "index.html"), desktop);
    await writeFile(join(output, "tool.html"), toolPage(desktop));
    await writeFile(join(output, "preview.html"), desktop.replace("img-src 'self' https: data: blob:", "img-src 'self' data: blob:")
        .replace("connect-src 'self' https://tiles.openfreemap.org", "connect-src 'none'"));
    await copyFile(join(here, "desktop-bridge.js"), join(output, "desktop-bridge.js"));
} else {
    await writeFile(join(output, "index.html"), index);
    await writeFile(join(output, "tool.html"), toolPage(index));
}
await writeFile(join(output, "prompt.txt"), prompt);
await writeFile(join(output, "card-prompt.txt"), cardPrompt);
