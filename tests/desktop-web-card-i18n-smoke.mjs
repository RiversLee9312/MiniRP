import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, resolve } from "node:path";
import { chromium } from "playwright";

const assets = resolve(process.argv[2] ?? "apps/desktop-web/dist");
const manifest = JSON.parse(await readFile(new URL("../src/MiniRP.Core/BasePlugin/manifest.json", import.meta.url)));
const example = JSON.parse(await readFile(new URL("../examples/room-status/plugin/manifest.json", import.meta.url)));
const asset = "minirp://assets/minirp.attachments/fixture";
const fixtures = {
  "trace-search-query": [{ query: "lunar soil", limit: 3 }, "lunar soil"],
  "trace-search-results": [{ results: [{ title: "Apollo results", url: "https://example.org/lunar" }] }, "Apollo results"],
  "trace-fetch-request": [{ url: "https://example.org/observations", backend: "local" }, ["https://example.org/observations", "/labels/browser"]],
  "trace-fetch-page": [{ title: "Observations", content: "The station remains open.", completeness: "complete" }, ["The station remains open.", "/labels/complete"]],
  "trace-cache-request": [{ refs: ["first", "second"] }, "2"],
  "trace-cache-result": [{ cached: true }, "/labels/saved"],
  "trace-asset-request": [{ url: asset, page: 2, viewport: { x: 1, y: 2, width: 300, height: 150 } }, "300 × 150"],
  "trace-asset-info": [{ url: asset, name: "Observations.pdf", mime: "application/pdf", metadata: { page_count: 12 } }, "12"],
  "trace-grep-request": [{ url: asset, pattern: "moons", from_line: 3 }, "moons"],
  "trace-grep-result": [{ match_count: 1, lines: [{ offset: 32, text: "50 4B 03 04" }] }, "50 4B 03 04"],
  "trace-edit-request": [{ url: asset, old_string: "open", new_string: "closed", replace_all: true }, "closed"],
  "trace-edit-result": [{ url: asset, changes: 1, size: 500 }, "500 B"],
  "trace-doc-request": [{ topic: "schema", query: "component" }, "schema"],
  "trace-doc-excerpt": [{ topic: "schema", text: "Use a stable tool identifier." }, "Use a stable tool identifier."],
  "trace-worker-request": [{ name: "Schedule", message: "Report the next visit." }, "Report the next visit."],
  "trace-worker-state": [{ id: "fixture", name: "Schedule", status: "finished", result: "The site is ready." }, ["The site is ready.", "/labels/finished"]],
  "trace-workers": [["idle", "running", "finished", "failed", "cancelled", "interrupted", "archived"].map((status, index) =>
    ({ id: `fixture-${index}`, name: `Station planner ${index}`, status })),
  ["Station planner 0", "/labels/idle", "/labels/running", "/labels/finished", "/labels/failed", "/labels/cancelled", "/labels/interrupted", "/labels/archived"]],
  "trace-project-check": [{ query: "SegmentedControl", offset: 10 }, "SegmentedControl"],
  "trace-project-state": [{ available: true, text: "Text and Title components" }, "Text and Title components"],
  "trace-card-preview": [{ rendered: true, width: 400, height: 200 }, "400 × 200"],
  "trace-project-removed": [{ removed: true, id: "draft-fixture" }, "draft-fixture"],
  "trace-current-scope": [{}, "/labels/defaults"],
};
const enumFixtures = {
  "trace-fetch-request": [
    [{ url: "https://example.org/default" }, ["https://example.org/default", "/labels/auto"]],
    [{ url: "https://example.org/api", backend: "api" }, ["https://example.org/api", "/labels/api"]],
    [{ url: "https://example.org/fallback", backend: "api_then_local" }, ["https://example.org/fallback", "/labels/fallback"]],
  ],
  "trace-fetch-page": [
    [{ title: "Observation sample", content: "Read the opening.", completeness: "partial" }, ["Read the opening.", "/labels/partial"]],
    [{ title: "Observation source", content: "Readability varies.", completeness: "unknown" }, ["Readability varies.", "/labels/unknown"]],
  ],
  "trace-worker-state": ["idle", "running", "failed", "cancelled", "interrupted", "archived"].map(status =>
    [{ id: "fixture", name: `Worker ${status.length}`, status }, [`Worker ${status.length}`, "/labels/" + status]]),
};

const types = { ".css": "text/css", ".html": "text/html", ".js": "text/javascript" };
const server = createServer(async (request, response) => {
  const path = new URL(request.url, "http://fixture.invalid").pathname;
  if (!/^\/(?:openui|fonts)\/[\w.-]+$/.test(path)) return response.writeHead(404).end();
  try {
    response.setHeader("Content-Type", types[extname(path)] ?? "application/octet-stream");
    response.end(await readFile(join(assets, path)));
  } catch {
    response.writeHead(404).end();
  }
});
await new Promise((done) => server.listen(0, "127.0.0.1", done));
const origin = "http://127.0.0.1:" + server.address().port;
let browser;
try {
  browser = await chromium.launch(process.env.MINIRP_TEST_BROWSER ? { channel: process.env.MINIRP_TEST_BROWSER } : {});
  const page = await browser.newPage({ viewport: { width: 375, height: 760 } });
  const messages = [];
  await page.exposeFunction("record", (type) => messages.push(type));
  await page.addInitScript(() => {
    window.MiniRPSurface = { postMessage: (message) => window.record(JSON.parse(message).type) };
    window.addEventListener("message", ({ data }) => {
      if (data?.minirp?.type) window.record(data.minirp.type);
    });
  });
  async function show(state) {
    messages.length = 0;
    await page.evaluate((input) => window.renderOpenUi(input), state);
  }
  await page.goto(origin + "/openui/tool.html");
  await page.waitForFunction(() => typeof window.renderOpenUi === "function");
  assert.deepEqual(Object.keys(fixtures).sort(), manifest.cards.map(({ id }) => id).sort());
  const mismatches = [];
  for (const [index, card] of manifest.cards.entries()) {
    for (const locale of ["en", ...Object.keys(manifest.i18n.locales)]) {
      const translations = manifest.i18n.locales[locale] ?? {};
      const title = translations[`/cards/${index}/title`] ?? card.title;
      const labels = Object.fromEntries(Object.entries(card.labels).map(([key, value]) => [
        key, translations[`/cards/${index}/labels/${key}`] ?? value,
      ]));
      const cases = [fixtures[card.id], ...(enumFixtures[card.id] ?? [])];
      for (const [data, expected] of cases) {
        const needles = [expected].flat().map(text => text.startsWith("/labels/") ? labels[text.slice(8)] : text);
        assert(needles.every(Boolean), `${card.id} (${locale}) has no translation for an expected label`);
        await show({ program: card.program, locale, rtl: ["ar", "fa", "he", "ur"].includes(locale),
          managedState: { title, labels, data }, managedRevision: index });
        try {
          await page.waitForFunction(({ title, needles }) => {
            const text = document.querySelector("#surface").innerText;
            return text.includes(title) && needles.every(needle => text.includes(needle));
          }, { title, needles }, { timeout: 2000 });
        } catch {
          mismatches.push(`${card.id} (${locale}) expected ${JSON.stringify(needles)}, rendered ${JSON.stringify(await page.locator("#surface").innerText())}`);
        }
        assert(!messages.includes("error"), `${card.id} (${locale}) sent an error`);
      }
    }
  }
  await page.goto(origin + "/openui/index.html");
  await page.waitForFunction(() => typeof window.renderOpenUi === "function");
  const station = example.cards[0];
  const program = station.program.replace("Text($line)", 'Text($line), Button("Change", @Set($note, "Local selection")), Text($note)') + '\n$note = ""';
  await show({ program, locale: "en", managedRevision: 1,
    managedState: { title: station.title, labels: station.labels, line: "Gate closed." } });
  await page.getByRole("button", { name: "Change" }).click();
  await page.getByText("Local selection").waitFor();
  await show({ program, locale: "zh-Hant", managedRevision: 1,
    managedState: { title: "值班狀態", labels: { current: "最新回報" }, line: "Gate closed." } });
  await page.getByText("值班狀態").waitFor();
  await page.getByText("最新回報").waitFor();
  assert(await page.getByText("Gate closed.").isVisible());
  assert(await page.getByText("Local selection").isVisible());
  assert.deepEqual(mismatches, []);
  console.log(`${manifest.cards.length} tool cards and ${Object.values(enumFixtures).flat().length} additional enum variants × ` +
    `${Object.keys(manifest.i18n.locales).length + 1} locales, plus interactive locale refresh, passed.`);
} finally {
  await browser?.close();
  await new Promise((done) => server.close(done));
}
