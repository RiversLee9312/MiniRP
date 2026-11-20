import { build } from "esbuild";
import { copyFile, mkdir, writeFile } from "node:fs/promises";
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
await copyFile(join(here, "index.html"), join(output, "index.html"));
await writeFile(join(output, "prompt.txt"), prompt);
await writeFile(join(output, "card-prompt.txt"), cardPrompt);
