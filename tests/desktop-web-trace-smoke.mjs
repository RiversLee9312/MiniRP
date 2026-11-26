import assert from "node:assert/strict";
import { once } from "node:events";
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { extname, resolve, sep } from "node:path";
import { chromium } from "playwright";

const webRoot = resolve(process.argv[2] || "artifacts/desktop/win-x64/wwwroot");
const preset = "6821369b-7690-4db7-84c9-dbb20a8dfb0c";
const config = { name: "Trace fixture", appearance: { theme_color: "#166957" } };
const phrase = "A long field can be inspected without covering adjacent values. ".repeat(7);
const json = '{"precision":1234567890123456789012345,"short":"Nearby","long":' + JSON.stringify(phrase) + ',"precision":2}';
const responseText = "Quote this sentence from the assistant.";
const presentation = {
  program: 'root = Col([Title("Presentation fixture", "md"), Text($data.short)])',
  title: "Display-only fixture", state: { data: { short: "Data from the tool" }, tool: "tool_card", phase: "result" },
};
const blocks = [
  { id: "reasoning", reasoning: ("Fixture thought. 文本 · مثال عربي\n").repeat(92) },
  { id: "fallback", tool: "tool_json", arguments: json },
  { id: "presented", tool: "configure_date", tool_title: "Configure the date", skill_title: "Calendar", result: json, tool_result_ui: presentation },
  { id: "restricted", tool: "tool_restricted", result: json, tool_result_ui: {
    ...presentation, program: 'root = Image("/forbidden-image")', title: "Forbidden component fixture",
  } },
  { id: "reply", content: responseText },
];
const events = [
  { kind: "ready", preset, config },
  { kind: "loaded", preset, config, chat: { id: preset, name: "Trace fixture", blocks }, ensemble_chats: null },
];
const commandRequests = [];
const eventClients = new Set();
const publish = (update) => {
  for (const client of eventClients) client.write("data: " + JSON.stringify(update) + "\n\n");
};
let unwantedImageRequest = false;
const types = { ".html": "text/html", ".css": "text/css", ".js": "text/javascript", ".woff2": "font/woff2", ".png": "image/png" };
const server = createServer(async (request, response) => {
  const path = new URL(request.url, "http://localhost").pathname;
  if (path === "/api/events") {
    response.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" });
    response.write("data: " + JSON.stringify(events) + "\n\n");
    eventClients.add(response);
    response.on("close", () => eventClients.delete(response));
    return;
  }
  if (path === "/api/settings") return response.writeHead(200, { "Content-Type": "application/json" }).end("{}");
  if (path === "/api/command") {
    let body = "";
    for await (const part of request) body += part;
    commandRequests.push(JSON.parse(body).op);
    return response.writeHead(200, { "Content-Type": "application/json" }).end('{"ok":true}');
  }
  if (path === "/forbidden-image") unwantedImageRequest = true;
  const file = resolve(webRoot, "." + (path === "/" ? "/index.html" : path));
  if (!file.startsWith(webRoot + sep)) return response.writeHead(404).end();
  try {
    const contents = await readFile(file);
    response.writeHead(200, { "Content-Type": types[extname(file)] || "application/octet-stream" }).end(contents);
  } catch { response.writeHead(404).end(); }
});
server.listen(0, "127.0.0.1");
await once(server, "listening");
let browser;
try {
  browser = await chromium.launch(process.env.MINIRP_TEST_BROWSER ? { channel: process.env.MINIRP_TEST_BROWSER } : {});
  const context = await browser.newContext({ locale: "en-US", viewport: { width: 640, height: 760 },
    colorScheme: process.env.MINIRP_TRACE_DARK ? "dark" : "light",
    permissions: ["clipboard-read", "clipboard-write"] });
  const page = await context.newPage();
  await page.goto("http://127.0.0.1:" + server.address().port);
  const trace = page.locator(".trace").filter({ hasText: "Fixture thought." });
  await trace.locator("summary").click();
  await page.waitForFunction(() => document.querySelector(".trace").clientHeight > 1000);
  assert.equal(await trace.evaluate((details) => {
    const transcript = details.closest(".transcript");
    const viewport = transcript.getBoundingClientRect();
    transcript.scrollTop += details.getBoundingClientRect().top - viewport.top + 180;
    return Math.abs(details.querySelector("summary").getBoundingClientRect().top - viewport.top) < 2;
  }), true);
  assert.equal(await trace.locator(".trace-reasoning").evaluate((text) =>
    getComputedStyle(text).fontFamily === getComputedStyle(document.body).fontFamily), true);

  const fallback = page.locator(".trace").filter({ hasText: "tool_json" });
  await fallback.locator(":scope > summary").click();
  const tool = fallback.getByRole("region", { name: "Arguments" });
  assert.equal(await tool.locator(".trace-json").evaluate((element) => getComputedStyle(element).userSelect), "text");
  await tool.getByText("1234567890123456789012345", { exact: true }).waitFor();
  assert.equal(await tool.locator(".trace-json-key").filter({ hasText: '"precision"' }).count(), 2);
  await tool.getByText('"Nearby"', { exact: true }).waitFor();
  const long = tool.locator(".trace-json-field");
  assert.equal(await long.getAttribute("open"), null);
  await long.locator("summary").click();
  await long.locator(".trace-json-token").filter({ hasText: phrase.trim() }).waitFor();
  await tool.getByRole("button", { name: "Copy" }).click();
  await page.waitForFunction(async (original) => await navigator.clipboard.readText() === original, json);

  const formatted = page.locator('[data-transcript-row="presented"] .trace');
  await formatted.locator(".trace-title").getByText("Configure the date", { exact: true }).waitFor();
  await formatted.locator(".trace-skill").getByText("Calendar", { exact: true }).waitFor();
  assert.equal(await formatted.locator(".trace-caption").getAttribute("title"), "configure_date");
  await formatted.locator(":scope > summary").click();
  await formatted.frameLocator('iframe[src="/openui/tool.html"]').getByText("Data from the tool", { exact: true }).waitFor();
  const raw = formatted.locator(".trace-json-source");
  assert.equal(await raw.getAttribute("open"), null);
  await raw.locator(":scope > summary").click();
  await raw.getByText('"Nearby"', { exact: true }).waitFor();
  if (process.env.MINIRP_TRACE_SCREENSHOT) await page.screenshot({ path: process.env.MINIRP_TRACE_SCREENSHOT });

  const detail = "请保留完整的设定值并验证可选取。".repeat(22) + "完整文本结尾。";
  const toolFacts = {
    program: "root = ToolFacts($rows, $status, $note, $more, $less)", title: "配置日期结果",
    state: { locale: "zh-Hans", status: "", note: "", more: "展开", less: "收起", rows: [
      { label: "日历来源", value: "由插件管理" }, { label: "设置值", value: detail, mono: true },
    ] },
  };
  publish([{ kind: "tool_trace_update", preset, chat: preset, agent: "ChatAgent", block: "presented",
    tool_title: "配置日期", skill_title: "日历", tool_result_ui: toolFacts }]);
  await formatted.locator(".trace-title").getByText("配置日期", { exact: true }).waitFor();
  await formatted.locator(".trace-skill").getByText("日历", { exact: true }).waitFor();
  assert.equal(await formatted.getAttribute("open"), "");
  const toolFrame = formatted.frameLocator('iframe[src="/openui/tool.html"]');
  await toolFrame.getByText("日历来源", { exact: true }).waitFor();
  await toolFrame.getByText("由插件管理", { exact: true }).waitFor();
  const fact = toolFrame.locator(".tool-fact-details");
  const entireValue = fact.locator(".tool-fact-full");
  assert.equal(await fact.getAttribute("open"), null);
  assert.equal(await entireValue.isVisible(), false);
  assert.match(await fact.locator(".tool-fact-preview").textContent(), /…$/);
  await fact.locator("summary").click();
  await entireValue.getByText(detail, { exact: true }).waitFor();
  assert.equal(await entireValue.evaluate((node) => {
    const range = document.createRange();
    range.selectNodeContents(node);
    const selection = getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    return getComputedStyle(node).userSelect === "text" && selection.toString() === node.textContent;
  }), true);
  await fact.locator("summary").click();
  assert.equal(await entireValue.isVisible(), false);
  await formatted.getByRole("button", { name: "Copy", exact: true }).click();
  await page.waitForFunction(async (original) => await navigator.clipboard.readText() === original, json);
  publish([{ kind: "tool_trace_update", preset, chat: preset, agent: "ChatAgent", block: "presented",
    tool_result_ui: { ...toolFacts, state: { ...toolFacts.state,
      rows: [{ ...toolFacts.state.rows[0], value: "由宿主管理" }, toolFacts.state.rows[1]],
    } } }]);
  await toolFrame.getByText("由宿主管理", { exact: true }).waitFor();
  assert.equal(await toolFrame.getByText("由插件管理", { exact: true }).count(), 0);
  publish([{ kind: "tool_trace_update", preset, chat: preset, agent: "ChatAgent", block: "presented",
    tool_title: null, skill_title: null, tool_result_ui: null }]);
  await formatted.locator(".trace-title").getByText("configure_date", { exact: true }).waitFor();
  assert.equal(await formatted.locator(".trace-skill").count(), 0);
  await formatted.locator(".trace-json").getByText('"Nearby"', { exact: true }).waitFor();
  assert.equal(await formatted.locator("iframe").count(), 0);

  const blocked = page.locator(".trace").filter({ hasText: "tool_restricted" });
  await blocked.locator(":scope > summary").click();
  await blocked.getByText('"Nearby"', { exact: true }).waitFor();
  assert.equal(unwantedImageRequest, false);

  const paragraph = page.getByText(responseText, { exact: true });
  await paragraph.scrollIntoViewIfNeeded();
  assert.equal(await paragraph.evaluate((node) => {
    const sample = document.createElement("span");
    sample.style.backgroundColor = "var(--primary)";
    sample.style.color = "var(--on-primary)";
    document.body.append(sample);
    const accent = getComputedStyle(sample);
    const selected = getComputedStyle(node, "::selection");
    const matches = selected.backgroundColor === accent.backgroundColor && selected.color === accent.color;
    sample.remove();
    return matches;
  }), true);
  const draft = page.locator(".composer textarea");
  await draft.fill("Existing draft.");
  await paragraph.evaluate((node) => {
    const range = document.createRange();
    range.selectNodeContents(node);
    document.getSelection().removeAllRanges();
    document.getSelection().addRange(range);
  });
  await paragraph.dispatchEvent("contextmenu", { bubbles: true, cancelable: true });
  await page.getByRole("menuitem", { name: "Quote", exact: true }).click();
  const quote = page.locator(".composer .quote-attachment");
  const removeQuote = quote.getByRole("button", { name: "Remove Quote" });
  await removeQuote.waitFor();
  assert.equal(await quote.locator(".quote-preview").textContent(), responseText);
  assert.equal(await draft.inputValue(), "Existing draft.");
  await removeQuote.click();
  await quote.waitFor({ state: "detached" });
  assert.equal(await draft.inputValue(), "Existing draft.");
  assert.ok(!commandRequests.includes("message"));
  publish([{ kind: "delta", agent: "ChatAgent", block: "following-trace",
    text: "Following reply.\n\n".repeat(60) + "End of following reply." }]);
  await page.getByText("End of following reply.", { exact: true }).waitFor({ state: "attached" });
  await trace.evaluate((details) => { details.open = true; });
  const height = await trace.locator("summary").evaluate((summary) => summary.getBoundingClientRect().height);
  async function placeTraceEnd(distance) {
    return trace.evaluate((details, distance) => {
      const transcript = details.closest(".transcript");
      const edge = transcript.getBoundingClientRect().top;
      transcript.scrollTop += details.getBoundingClientRect().bottom - edge - distance;
      const header = details.querySelector("summary").getBoundingClientRect();
      return { top: header.top - edge, bottom: header.bottom - edge };
    }, distance);
  }
  const pinned = await placeTraceEnd(height * 2);
  assert.ok(Math.abs(pinned.top) < 2);
  for (const distance of [height / 2, height / 4]) {
    const leaving = await placeTraceEnd(distance);
    assert.ok(leaving.top < 0 && Math.abs(leaving.bottom - distance) < 2);
  }
  console.log("Trace pinning and push-out, localized titles and ToolFacts, exact JSON copy and fallback, display-only card restrictions, selection and quoting passed.");
} finally {
  await browser?.close();
  server.closeAllConnections();
  await new Promise((resolve) => server.close(resolve));
}
