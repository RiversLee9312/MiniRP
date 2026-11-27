import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { chromium } from "playwright";

const program = resolve(process.argv[2] ?? "artifacts/desktop/win-x64/minirp.exe");
const screenshots = process.argv[3] && resolve(process.argv[3]);
const workspace = await mkdtemp(join(tmpdir(), "minirp-attachment-fixture-"));
const preset = "e6b38588-48ee-4404-833e-4255b7aedd97";
const chat = "38cdeb50-c833-4a7d-b928-5167b4031765";
const doc = "314159260001";
const photo = "314159260002";
const pdf = "314159260003";
const wav = "314159260004";
const report = Array.from({ length: 280 }, (_, i) => "Field note " + (i + 1)).join("\n") + "\nEnd of report.";
const root = new URL("../", import.meta.url);
const png = await readFile(new URL("assets/branding/launcher.png", root));

function documentFixture() {
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 210 300] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 210 300] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ...["First page", "Second page"].map((title) => {
      const commands = "BT /F1 16 Tf 20 250 Td (" + title + ") Tj ET";
      return "<< /Length " + commands.length + " >>\nstream\n" + commands + "\nendstream";
    }),
  ];
  let source = "%PDF-1.4\n";
  const offsets = objects.map((object, i) => {
    const offset = source.length;
    source += (i + 1) + " 0 obj\n" + object + "\nendobj\n";
    return offset;
  });
  const xref = source.length;
  return source + "xref\n0 8\n0000000000 65535 f \n" +
    offsets.map((offset) => String(offset).padStart(10, "0") + " 00000 n \n").join("") +
    "trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n";
}

function audioFixture() {
  const bytes = Buffer.alloc(64044, 128);
  bytes.write("RIFF", 0); bytes.writeUInt32LE(bytes.length - 8, 4);
  bytes.write("WAVEfmt ", 8); bytes.writeUInt32LE(16, 16);
  bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22);
  bytes.writeUInt32LE(8000, 24); bytes.writeUInt32LE(8000, 28);
  bytes.writeUInt16LE(1, 32); bytes.writeUInt16LE(8, 34);
  bytes.write("data", 36); bytes.writeUInt32LE(64000, 40);
  return bytes;
}

const conversationPath = join(workspace, "conversations", preset);
for (const directory of [join(workspace, "presets"), join(workspace, "attachments"), conversationPath])
  await mkdir(directory, { recursive: true });
await writeFile(join(workspace, "presets", preset + ".json"), JSON.stringify({ id: preset, name: "Attachment fixture" }));
await Promise.all([[doc, report], [photo, png], [pdf, documentFixture()], [wav, audioFixture()]].map(([id, data]) =>
  writeFile(join(workspace, "attachments", id), data)));
const attachments = [
  { id: photo, name: "Reference.png", mime: "image/png" },
  { id: doc, name: "Field notes.txt", mime: "text/plain" },
  { id: pdf, name: "Two pages.pdf", mime: "application/pdf" },
  { id: wav, name: "Audio sample.wav", mime: "audio/wav" },
];
const prompt = "Check these attachments.";
await writeFile(join(conversationPath, chat + ".json"), JSON.stringify({
  id: chat, name: "Attachment preview fixture", preset, updated_at: new Date().toISOString(),
  messages: [{ role: "user", content: prompt, attachments }],
  blocks: [{ id: "start", agent: "chat", kind: "user", content: prompt, attachments },
    { id: "answer", agent: "ChatAgent", content: "\x60\x60\x60\n" + "wide code ".repeat(130) + "\n\x60\x60\x60" }],
}));

const child = spawn(program, ["--web", "--no-open", workspace], {
  env: { ...process.env, MINIRP_CONTAINER_IMAGE: join(workspace, "no-container") },
  stdio: ["ignore", "pipe", "pipe"],
});
const exited = once(child, "exit");
let browser;
try {
  const url = await new Promise((resolveUrl, reject) => {
    const timer = setTimeout(() => reject(new Error("Fixture WebUI did not start.")), 20_000);
    let output = "";
    child.stdout.on("data", (data) => {
      output += data;
      const match = output.match(/MiniRP WebUI: (\S+)/);
      if (match) { clearTimeout(timer); resolveUrl(match[1]); }
    });
    child.once("exit", (code) => { clearTimeout(timer); reject(new Error("Fixture host exited " + code)); });
    child.once("error", (error) => { clearTimeout(timer); reject(error); });
  });
  browser = await chromium.launch({
    channel: process.env.MINIRP_TEST_BROWSER || undefined,
    ignoreDefaultArgs: ["--hide-scrollbars"],
  });
  const page = await browser.newPage({ baseURL: new URL(url).origin, locale: "en-US", viewport: { width: 1100, height: 720 } });
  page.setDefaultTimeout(12_000);
  const photograph = page.getByRole("button", { name: "Preview Reference.png", exact: true });
  async function shot(name, target = page) {
    if (!screenshots) return;
    await mkdir(screenshots, { recursive: true });
    await target.screenshot({ path: join(screenshots, name), animations: "disabled" });
  }
  async function scrollbar(port, axis = "vertical") {
    const geometry = await port.evaluate((element, direction) => {
      const vertical = direction === "vertical";
      const range = vertical ? element.scrollHeight - element.clientHeight : element.scrollWidth - element.clientWidth;
      if (vertical) element.scrollTop = range;
      else element.scrollLeft = range;
      const button = getComputedStyle(element, "::-webkit-scrollbar-button");
      const native = getComputedStyle(element);
      return { range, end: vertical ? element.scrollTop : element.scrollLeft,
        thumb: native.scrollbarWidth, buttons: button.display };
    }, axis);
    assert.ok(geometry.range > 0);
    assert.ok(geometry.end >= geometry.range - 2);
    assert.notEqual(geometry.thumb, "none");
    if (await page.evaluate(() => CSS.supports("selector(::-webkit-scrollbar)"))) assert.equal(geometry.buttons, "none");
  }
  await page.goto(url);
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await page.getByRole("button", { name: "About", exact: true }).click();
  await page.getByRole("button", { name: "Open source licenses", exact: true }).click();
  const license = page.getByRole("dialog", { name: "Open source licenses" });
  const licenseBody = license.locator(".dialog-content");
  await licenseBody.locator(".workspace-plugin-markdown").first().waitFor();
  await scrollbar(licenseBody);
  await shot("settings-scrollbar.png", license);
  const licenseBounds = await licenseBody.boundingBox();
  const beforeDrag = await licenseBody.evaluate((body) => body.scrollTop);
  await page.mouse.move(licenseBounds.x + licenseBounds.width - 6, licenseBounds.y + licenseBounds.height - 15);
  await page.mouse.down();
  await page.mouse.move(licenseBounds.x + licenseBounds.width - 6, licenseBounds.y + licenseBounds.height - 90, { steps: 8 });
  await page.mouse.up();
  assert.ok(await licenseBody.evaluate((body) => body.scrollTop) < beforeDrag);
  await license.getByRole("button", { name: "Back", exact: true }).click();
  await page.getByRole("dialog", { name: "About", exact: true }).getByRole("button", { name: "Back", exact: true }).click();
  await page.getByRole("button", { name: "Appearance and behavior" }).click();
  await page.getByRole("button", { name: "Language", exact: true }).click();
  const language = page.getByRole("combobox", { name: "Language", exact: true });
  if (await page.evaluate(() => CSS.supports("appearance", "base-select"))) {
    await language.click();
    await page.waitForFunction(() => document.querySelector("select:open"));
    await page.keyboard.press("End");
    await scrollbar(language.locator(".select-scroll"));
    await page.waitForFunction(() => getComputedStyle(document.querySelector("select:open"), "::picker(select)").opacity === "1");
    await shot("native-picker-scrollbar.png");
    await page.keyboard.press("Escape");
  }
  const response = await page.request.post("/api/command", { data: { op: "select_chat", preset, id: chat } });
  assert.ok(response.ok());
  await page.getByRole("dialog", { name: "Language", exact: true }).getByRole("button", { name: "Close", exact: true }).click();
  await photograph.click();
  const viewer = page.getByRole("dialog", { name: "Reference.png" });
  await viewer.getByRole("img", { name: "Reference.png" }).evaluate((image) => image.decode());
  await viewer.getByRole("button", { name: "Zoom in" }).click();
  await page.waitForFunction(() => getComputedStyle(document.querySelector(".react-transform-component")).transform !== "none" &&
    Number(new DOMMatrix(getComputedStyle(document.querySelector(".react-transform-component")).transform).a) > 1);
  await viewer.getByRole("button", { name: "Reset zoom" }).click();
  await shot("web-attachment-image.png", viewer);
  await viewer.getByRole("button", { name: "Next", exact: true }).click();
  const docViewer = page.getByRole("dialog", { name: "Field notes.txt" });
  const textPort = docViewer.locator(".attachment-preview-text");
  await textPort.getByText(/End of report\./).waitFor();
  await scrollbar(textPort);
  await shot("text-preview-scrollbar.png", docViewer);
  await docViewer.getByRole("button", { name: "Next", exact: true }).click();
  const pdfViewer = page.getByRole("dialog", { name: "Two pages.pdf" });
  const canvas = pdfViewer.getByRole("img", { name: "Page 1 of 2" });
  await canvas.waitFor();
  await page.waitForFunction((element) => {
    const rgba = element.getContext("2d").getImageData(0, 0, element.width, element.height).data;
    return rgba.some((value, i) => i % 4 === 0 && value < 30 && rgba[i + 3] === 255);
  }, await canvas.elementHandle());
  await pdfViewer.locator(".attachment-preview-stage").getByRole("button", { name: "Next", exact: true }).click();
  await pdfViewer.getByRole("img", { name: "Page 2 of 2" }).waitFor();
  await pdfViewer.locator(":scope > .dialog-content > .attachment-preview-pager").getByRole("button", { name: "Next", exact: true }).click();
  const audioViewer = page.getByRole("dialog", { name: "Audio sample.wav" });
  const audio = audioViewer.locator("audio");
  await page.waitForFunction((element) => element.readyState >= 1 && element.duration === 8, await audio.elementHandle());
  await audioViewer.getByRole("slider", { name: "Playback position" }).fill("3");
  await page.waitForFunction((element) => element.currentTime === 3, await audio.elementHandle());
  await audioViewer.getByRole("button", { name: "Play", exact: true }).click();
  await page.waitForFunction((element) => !element.paused && element.currentTime > 3.1, await audio.elementHandle());
  await audioViewer.getByRole("button", { name: "Pause", exact: true }).click();
  await page.waitForFunction((element) => element.paused, await audio.elementHandle());
  const audioBounds = await audioViewer.boundingBox();
  assert.ok(audioBounds.width <= 532 && audioBounds.height < 280, "Audio playback uses a compact dialog.");
  await shot("web-attachment-audio.png", audioViewer);
  await page.keyboard.press("Escape");
  await page.getByRole("dialog", { name: "Audio sample.wav" }).waitFor({ state: "hidden" });
  const wideCode = page.locator(".assistant-reply pre");
  await scrollbar(wideCode, "horizontal");
  await shot("horizontal-code-scrollbar.png", wideCode);
  const composer = page.locator(".composer");
  await composer.locator("input[type=file]").setInputFiles({ name: "Draft.png", mimeType: "image/png", buffer: png });
  await composer.getByRole("button", { name: "Preview Draft.png" }).click();
  await page.getByRole("dialog", { name: "Draft.png" }).getByRole("img", { name: "Draft.png" }).evaluate((image) => image.decode());
  await page.keyboard.press("Escape");
  await composer.getByRole("button", { name: "Remove Draft.png" }).click();
  await composer.getByRole("button", { name: "Preview Draft.png" }).waitFor({ state: "hidden" });
  await page.emulateMedia({ colorScheme: "dark" });
  await page.setViewportSize({ width: 960, height: 230 });
  const toolbarMenu = page.locator(".toolbar-actions").getByRole("button", { name: "More options" });
  await toolbarMenu.press("ArrowUp");
  const actions = page.getByRole("menu", { name: "More options" });
  await scrollbar(actions);
  await shot("action-menu-scrollbar.png");
  await page.keyboard.press("Escape");
  console.log("Attachment previews and overflow scrollbars verified in WebUI.");
} finally {
  await browser?.close();
  child.kill();
  await exited.catch(() => {});
  await rm(workspace, { recursive: true, force: true, maxRetries: 3 });
}
