import { cpSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { XMLParser } from "fast-xml-parser";
import { defineConfig } from "vite";

const android = fileURLToPath(new URL("../android/app/src/main/res/", import.meta.url));
const pdfPackage = fileURLToPath(new URL("./node_modules/pdfjs-dist/", import.meta.url));
const parser = new XMLParser({
  ignoreAttributes: false,
  parseTagValue: false,
  processEntities: true,
  isArray: (name) => name === "string",
});

function loadLanguages() {
  const directories = readdirSync(android).filter((name) => name === "values" || name.startsWith("values-b+"));
  return Object.fromEntries(directories.map((name) => {
    const locale = name === "values" ? "en" : name.slice(9).replaceAll("+", "-");
    const source = readFileSync(join(android, name, "strings.xml"), "utf8");
    const entries = parser.parse(source).resources.string;
    return [locale, Object.fromEntries(entries.map((entry) => {
      const message = String(entry["#text"] ?? "").replace(/^"|"$/g, "")
        .replace(/\\u([0-9a-fA-F]{4})|\\([nrt'"\\@?])/g, (_, hex, value) =>
          hex ? String.fromCharCode(Number.parseInt(hex, 16)) : ({ n: "\n", r: "\r", t: "\t" })[value] ?? value);
      return [entry["@_name"], message];
    }))];
  }));
}

export default defineConfig({
  esbuild: { jsx: "automatic" },
  build: {
    emptyOutDir: true,
    // The WebUI's Content Security Policy serves fonts from its own origin.
    assetsInlineLimit: (path) => /\.(?:woff2?|ttf)$/i.test(path) ? false : undefined,
  },
  plugins: [{
    name: "minirp-pdf-preview-resources",
    apply: "build",
    writeBundle({ dir }) {
      for (const folder of ["cmaps", "standard_fonts"]) {
        cpSync(join(pdfPackage, folder), join(dir, "pdf-preview", folder), { recursive: true });
      }
    },
  }, {
    name: "minirp-shared-resources",
    resolveId(id) { return id === "virtual:minirp-languages" ? "\0" + id : null; },
    load(id) {
      if (id !== "\0virtual:minirp-languages") return null;
      const languages = loadLanguages();
      if (Object.keys(languages).length < 50 || !languages["zh-Hans"] || !languages.ur) {
        throw new Error("Desktop WebUI requires the Android language catalog.");
      }
      return "export default " + JSON.stringify(languages);
    },
  }],
});
