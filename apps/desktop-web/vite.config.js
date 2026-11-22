import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { XMLParser } from "fast-xml-parser";
import { defineConfig } from "vite";

const android = fileURLToPath(new URL("../android/app/src/main/res/", import.meta.url));
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
        .replace(/\\([n'"\\])/g, (_, value) => value === "n" ? "\n" : value);
      return [entry["@_name"], message];
    }))];
  }));
}

export default defineConfig({
  esbuild: { jsx: "automatic" },
  build: { emptyOutDir: true },
  plugins: [{
    name: "minirp-shared-resources",
    resolveId(id) { return id === "virtual:minirp-languages" ? "\0" + id : null; },
    load(id) {
      if (id !== "\0virtual:minirp-languages") return null;
      const languages = loadLanguages();
      if (Object.keys(languages).length < 25 || !languages["zh-Hans"] || !languages.ar) {
        throw new Error("Desktop WebUI requires the Android language catalog.");
      }
      return "export default " + JSON.stringify(languages);
    },
  }],
});
