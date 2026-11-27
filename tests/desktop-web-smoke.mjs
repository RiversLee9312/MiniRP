import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, readdir, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { chromium } from "playwright";

const program = resolve(process.argv[2] ?? ("artifacts/desktop/" + (process.platform === "win32" ? "win-x64/minirp.exe" : "linux-x64/minirp")));
const workspace = await mkdtemp(join(tmpdir(), "minirp-desktop-qa-"));
const reply = "A reply from the local test model.";
const mathReply = [
  "Math fixture follows.",
  "Inline $x^2$ and \\(\\sqrt{2}\\). Prices are $5 and $10.",
  "$$\\frac{1}{2}$$",
  "\\[\\sum_{i=1}^{n} i\\]",
  "```" + "math\n\\begin{pmatrix}a&b\\\\c&d\\end{pmatrix}\n```",
  "The literal `$x^2$` stays code.",
].join("\n\n");
const longProvider = "Fixture provider " + "LongProviderName".repeat(18);
const longModel = "org/" + "long-model-without-spaces".repeat(12);
const longModelName = "Fixture catalog model " + "LongDisplayName".repeat(12);
const generatedTitle = "灯塔巡查与夜间航道记录";
const regeneratedTitle = "مراجعة" + "الملاحظاتالمتتابعة".repeat(3);
const longManualTitle = "W".repeat(79) + "🧭" + " 航道与潮汐记录".repeat(9);
const titleRequests = [];

async function verifyTravel(surface, property, edge, rest) {
  const positions = await surface.evaluate((element, { property, edge }) => {
    const slide = element.getAnimations().find((animation) => animation.transitionProperty === property);
    if (!slide) return [];
    const time = slide.currentTime;
    slide.pause();
    try {
      const duration = slide.effect.getTiming().duration;
      return Array.from({ length: 25 }, (_, index) => {
        slide.currentTime = duration * index / 24;
        return element.getBoundingClientRect()[edge];
      });
    } finally {
      slide.currentTime = time;
      slide.play();
    }
  }, { property, edge });
  assert.ok(positions.length, `Expected ${property} transition on ${surface}`);
  const end = positions.at(-1);
  if (rest !== undefined) assert.ok(Math.abs(end - rest) < 1, `Expected ${edge} to settle at ${rest}, got ${end}`);
  const direction = Math.sign(end - positions[0]);
  assert.notEqual(direction, 0, `Expected ${edge} to move during the transition`);
  assert.ok(positions.slice(1).every((position, index) => direction * (position - positions[index]) >= -.01),
    `Unexpected reversal of ${edge} during ${property}: ${positions.map((position) => position.toFixed(2)).join(", ")}`);
}

const createPresetRequest = Promise.withResolvers();
const presetInterview = Promise.withResolvers();
const presetEdit = Promise.withResolvers();
const extendScrollReply = Promise.withResolvers();
const finishScrollReply = Promise.withResolvers();
const configuredChatRequest = Promise.withResolvers();
const quotedModelRequest = Promise.withResolvers();
const readiness = { Ada: Promise.withResolvers(), Bryn: Promise.withResolvers() };
const readinessCalls = [];
const openings = [];
const labOpening = Promise.withResolvers();
const bridgeAnswerStarted = Promise.withResolvers();
const bridgeAnswer = Promise.withResolvers();
const labAnswered = Promise.withResolvers();
const roomWorkerStarted = Promise.withResolvers();
const roomWorkerStopped = Promise.withResolvers();
let labSceneId;
let bridgeSceneId;
const modelFailures = [];
async function expectSignal(signal, message) {
  return Promise.race([signal.promise, new Promise((_, reject) => {
    setTimeout(() => reject(new Error(message)), 10000).unref();
  })]);
}
const service = createServer(async (request, response) => {
  if (request.url.endsWith("/models")) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ data: [{ id: "fixture-model", input_modalities: ["text"] },
      { id: longModel, display_name: longModelName, effort_levels: ["high", "low"], context_length: 32768,
        input_modalities: ["text"] },
      { id: "fixture-vision", input_modalities: ["text", "image"] },
      { id: "fixture-audio", input_modalities: ["text", "audio"] }] }));
    return;
  }
  if (!request.url.endsWith("/chat/completions")) {
    response.writeHead(404).end();
    return;
  }
  let body = "";
  for await (const chunk of request) body += chunk;
  const completion = JSON.parse(body);
  const titlePrompt = completion.messages.some((item) => item.role === "system" &&
    typeof item.content === "string" && item.content.startsWith("Write a concise conversation title"));
  let titleReply = "Fixture chat";
  if (titlePrompt && body.includes("Hello from the desktop test.")) {
    titleReply = titleRequests.length ? regeneratedTitle : generatedTitle;
    titleRequests.push(completion);
  }
  if (!completion.stream) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ choices: [{ message: { content: titleReply }, finish_reason: "stop" }] }));
    return;
  }
  response.writeHead(200, { "content-type": "text/event-stream" });
  const chunk = (delta, finish_reason = null) => response.write("data: " + JSON.stringify({ choices: [{ index: 0, delta, finish_reason }] }) + "\n\n");
  const user = completion.messages.findLast((item) => item.role === "user");
  if (titlePrompt) {
    chunk({ content: titleReply }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  if (["fixture-vision", "fixture-audio"].includes(completion.model)) {
    const image = completion.model === "fixture-vision";
    const source = user.content[1];
    assert.equal(source.type, image ? "image_url" : "input_audio");
    assert.equal(completion.tools?.length ?? 0, 0);
    assert.ok(!body.includes("Hello from the desktop test."));
    chunk({ content: image ? "A small fixture image." : "A short fixture recording." }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  if (JSON.stringify(user?.content || "").includes("Hello from the desktop test."))
    configuredChatRequest.resolve({ model: completion.model, effort: completion.reasoning_effort, content: user.content });
  const prompts = JSON.stringify(completion.messages);
  const tools = completion.tools?.map((tool) => tool.function.name) ?? [];
  const call = (name, args) => {
    chunk({ tool_calls: [{ index: 0, id: "fixture_" + name, type: "function",
      function: { name, arguments: JSON.stringify(args) } }] }, "tool_calls");
    response.end("data: [DONE]\n\n");
  };
  if (tools.includes("finish_preset_setup")) {
    if (!JSON.stringify(user?.content || "").includes("Confirm this fixture preset.")) {
      await presetInterview.promise;
      chunk({ content: "Choose the experience for this preset." }, "stop");
    } else {
      const used = (name) => completion.messages.some((message) => message.tool_calls?.some((tool) => tool.function.name === name));
      if (!used("configure_experience")) return call("configure_experience", { mode: "character", agentic_search: true });
      if (!used("configure_date")) return call("configure_date", { mode: "plugin", initial_date: "2025-09-01" });
      if (!used("set_value")) return call("set_value", { path: "name", value: "Fixture preset" });
      if (!used("finish_preset_setup")) return call("finish_preset_setup", {});
      chunk({ content: "The fixture preset is ready." }, "stop");
    }
    response.end("data: [DONE]\n\n");
    return;
  }
  if (JSON.stringify(user?.content || "").includes("Hold this preset edit.")) {
    await presetEdit.promise;
    chunk({ content: "The preset edit is complete." }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  if (JSON.stringify(user?.content || "").includes("Exercise transcript scrolling.")) {
    const paragraphs = (from, count) => Array.from({ length: count }, (_, index) =>
      "Fixture paragraph " + (from + index) + ".").join("\n\n");
    chunk({ content: paragraphs(1, 45) + "\n\n" });
    await extendScrollReply.promise;
    chunk({ content: paragraphs(46, 20) + "\n\n" });
    await finishScrollReply.promise;
    chunk({ content: "    " + "wide_code_token_".repeat(48) + "\n\nEnd of the scrolling fixture." }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  if (prompts.includes("Open with the observatory bell.")) {
    chunk({ content: "The observatory bell rings." }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  if (JSON.stringify(user?.content || "").includes("The dock crew has finished unloading.")) {
    if (!prompts.includes("Decide whether to advance the story using the current request, conversation, established facts, and stage policy"))
      modelFailures.push("EnvAgent was not given the story advancement decision.");
    if (!tools.includes("set_phase")) modelFailures.push("EnvAgent was not given the world phase tool.");
    if (completion.messages.at(-1)?.role !== "tool") call("set_phase", { phase: "The dock is ready for the next watch." });
    else {
      chunk({ content: "The next watch has begun." }, "stop");
      response.end("data: [DONE]\n\n");
    }
    return;
  }
  if (tools.includes("room_ready")) {
    const name = prompts.includes("Your character is Ada") ? "Ada" : "Bryn";
    if (!tools.includes("room_progress")) modelFailures.push("Room progress tool was unavailable.");
    if (completion.messages.at(-1)?.role !== "tool") {
      readinessCalls.push(name + " progress");
      call("room_progress", { percent: name === "Ada" ? 40 : 60 });
    } else {
      await readiness[name].promise;
      readinessCalls.push(name + " ready");
      call("room_ready", {});
    }
    return;
  }
  if (tools.includes("delete_ensemble_chat") && prompts.includes("Archive the bridge and open a workshop.")) {
    const previous = completion.messages.at(-2)?.tool_calls?.[0]?.function.name;
    if (completion.messages.at(-1)?.role !== "tool") return call("delete_ensemble_chat", { chat: bridgeSceneId });
    if (previous === "delete_ensemble_chat") return call("create_ensemble_chat", { name: "Workshop", members: ["ada"] });
    chunk({ content: "The bridge was archived and the workshop opened." }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  const ensemble = [...prompts.matchAll(/The triggering chat is ([^()]+) \(([0-9a-f-]+)\)/g)].at(-1);
  if (!ensemble && prompts.includes("This is your room opening.") && tools.includes("my_chats")) {
    const name = prompts.includes("Your character is Ada") ? "Ada" : "Bryn";
    const last = completion.messages.at(-1);
    if (last?.role !== "tool") {
      openings.push(name);
      return call("my_chats", {});
    }
    if (completion.messages.at(-2)?.tool_calls?.some(tool => tool.function.name === "my_chats")) {
      const chosen = JSON.parse(last.content).find(chat => chat.name === (name === "Ada" ? "Bridge" : "Lab"));
      if (chosen.name === "Lab") {
        labSceneId = chosen.id;
        await labOpening.promise;
      } else bridgeSceneId = chosen.id;
      return call("chat_send", { chat: chosen.id, text: name + " opened the " + chosen.name.toLowerCase() + "." });
    }
    chunk({ content: "Opening finished." }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  if (ensemble) {
    if (tools.includes("room_progress") || prompts.includes("fixture_room_progress"))
      modelFailures.push("Room loading state leaked into a chat.");
    const [, scene, id] = ensemble;
    const name = prompts.includes("Your character is Ada") ? "Ada" : "Bryn";
    if (name === "Ada" && (scene === "Lab" || prompts.includes("Bryn carried the report to the lab.")))
      modelFailures.push("A character received a message from an unjoined scene.");
    if (completion.messages.at(-1)?.role !== "tool") {
      if (scene === "Bridge" && name === "Ada" && prompts.includes("Hold this room worker.")) {
        response.once("close", roomWorkerStopped.resolve);
        chunk({ content: "Waiting for room departure." });
        roomWorkerStarted.resolve();
        return;
      }
      if (scene === "Bridge" && name === "Ada" && prompts.includes("Report to the bridge.") &&
          !prompts.includes("Ada reported from the bridge.")) {
        bridgeAnswerStarted.resolve();
        await bridgeAnswer.promise;
        return call("chat_send", { chat: id, text: "Ada reported from the bridge." });
      }
      if (scene === "Bridge" && name === "Bryn" && prompts.includes("Ada reported from the bridge.") &&
          !prompts.includes("Bryn carried the report to the lab.")) {
        return call("chat_send", { chat: labSceneId, text: "Bryn carried the report to the lab." });
      }
    }
    const sent = completion.messages.at(-2)?.tool_calls?.some((tool) =>
      tool.function.name === "chat_send" && tool.function.arguments.includes("Bryn carried the report to the lab."));
    if (sent && completion.messages.at(-1)?.role === "tool") labAnswered.resolve();
    chunk({ content: "Private fixture response." }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  if (JSON.stringify(user?.content || "").includes("Cancel this fixture turn.")) {
    chunk({ content: "Waiting for cancellation." });
    return;
  }
  if (JSON.stringify(user?.content || "").includes("Show a fixture card.")) {
    quotedModelRequest.resolve(user.content);
    if (completion.messages.at(-1)?.role !== "tool") {
      chunk({ tool_calls: [{ index: 0, id: "fixture_activate", type: "function",
        function: { name: "activate_skill", arguments: '{"name":"interactive-ui"}' } }] }, "tool_calls");
    } else {
      chunk({ content: "```openui-lang\nroot = Col([title])\ntitle = Title(\"Fixture card\")\n```" }, "stop");
    }
    response.end("data: [DONE]\n\n");
    return;
  }
  chunk({ reasoning_content: "Test trace", content: "A reply " });
  setTimeout(() => {
    chunk({ content: "from the local test model." });
    chunk({}, "stop");
    response.end("data: [DONE]\n\n");
  }, 200);
});
service.listen(0, "127.0.0.1");
await once(service, "listening");
await writeFile(join(workspace, ".env"), [
  "DEEPSEEK_BASE_URL=http://127.0.0.1:" + service.address().port,
  "DEEPSEEK_API_KEY=fixture",
  "DEEPSEEK_MODEL=fixture-model",
  "DEEPSEEK_THINKING=enabled",
].join("\n"));
await writeFile(join(workspace, ".minirp-desktop.json"), JSON.stringify({
  agents: { plugin: { provider: "environment", model: "fixture-model", options: { compact_at_percent: 60 } } },
}));
const child = spawn(program, ["--web", "--no-open", workspace], {
  env: { ...process.env, MINIRP_CONTAINER_IMAGE: join(workspace, "no-container") },
  stdio: ["ignore", "pipe", "pipe"],
});
const exit = once(child, "exit");
let browser;
try {
  const url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("The desktop host did not print its WebUI URL.")), 30000);
    let output = "";
    child.stdout.on("data", (data) => {
      output += data;
      const match = output.match(/MiniRP WebUI: (\S+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
    child.stderr.on("data", (data) => process.stderr.write(data.toString().replace(/\/connect\/[a-f0-9]+/g, "/connect/<redacted>")));
    child.once("exit", (code) => { clearTimeout(timer); reject(new Error("Desktop host exited with code " + code)); });
    child.once("error", (error) => { clearTimeout(timer); reject(error); });
  });
  assert.equal((await fetch(new URL(url).origin + "/api/settings")).status, 401);
  browser = await chromium.launch(process.env.MINIRP_TEST_BROWSER ? { channel: process.env.MINIRP_TEST_BROWSER } : {});
  const page = await browser.newPage({ baseURL: new URL(url).origin, locale: "en-US", acceptDownloads: true });
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.setDefaultTimeout(12000);
  await page.goto(url);
  await page.getByRole("heading", { name: "No presets yet" }).waitFor();
  const appMark = page.locator(".app-mark");
  await appMark.evaluate((image) => image.decode());
  const favicon = await page.locator('link[rel="icon"]').evaluate((link) => link.href);
  assert.equal(await appMark.evaluate((image) => image.currentSrc), favicon);
  assert.equal(await page.locator("html").evaluate((root) => getComputedStyle(root).getPropertyValue("--primary").trim()), "#6750a4");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  const preferences = page.getByRole("dialog", { name: "Settings", exact: true });
  await verifyTravel(preferences, "transform", "top");
  assert.equal(await page.evaluate(async () => {
    const font = '20px "Material Symbols Rounded"';
    const icons = ["add", "check", "cloud", "info", "license", "smart_toy"];
    await document.fonts.load(font, icons.join(" "));
    const context = document.createElement("canvas").getContext("2d");
    context.font = font;
    return icons.every((icon) => Math.abs(context.measureText(icon).width - 20) < 1);
  }), true);
  assert.equal(await preferences.getByLabel("Language").count(), 0);
  await preferences.getByRole("button", { name: "Appearance and behavior" }).click();
  const behavior = page.getByRole("dialog", { name: "Appearance and behavior", exact: true });
  const themeChoice = behavior.getByRole("combobox", { name: "Appearance", exact: true });
  await themeChoice.waitFor();
  assert.equal(await behavior.getAttribute("data-page-direction"), "forward");
  assert.match(await behavior.locator(".settings-page").evaluate((pane) => getComputedStyle(pane).animationName), /page-in/);
  assert.deepEqual(await themeChoice.locator("option").evaluateAll((items) => items.map((node) => node.value)),
    ["system_default", "light", "dark"]);
  if (await page.evaluate(() => CSS.supports("appearance", "base-select"))) {
    await themeChoice.click();
    assert.equal(await themeChoice.evaluate((control) => control.matches(":open")), true);
    const picker = await themeChoice.evaluate((control) => ({
      arrow: getComputedStyle(control, "::picker-icon").content,
      padding: getComputedStyle(control).paddingInlineEnd,
      radius: getComputedStyle(control, "::picker(select)").borderTopLeftRadius,
      transition: getComputedStyle(control, "::picker(select)").transitionProperty,
      gutter: getComputedStyle(control.querySelector(".select-scroll")).scrollbarGutter,
      selected: getComputedStyle(control.selectedOptions[0]).backgroundColor,
      check: getComputedStyle(control.selectedOptions[0], "::checkmark").content,
      checkFont: getComputedStyle(control.selectedOptions[0], "::checkmark").fontFamily,
    }));
    assert.deepEqual([picker.arrow, picker.padding], ['"expand_more"', "10px"]);
    assert.ok(parseFloat(picker.radius) > 0);
    assert.match(picker.transition, /opacity.*display.*overlay/);
    assert.equal(picker.gutter, "stable");
    assert.notEqual(picker.selected, "rgba(0, 0, 0, 0)");
    assert.equal(picker.check, '"check"');
    assert.match(picker.checkFont, /Material Symbols Rounded/);
    await page.keyboard.press("Escape");
    await behavior.waitFor();
  }
  assert.equal(await behavior.getByRole("button", { name: "Background replies" }).count(), 0);
  await behavior.getByRole("button", { name: "Language", exact: true }).click();
  const language = page.getByRole("dialog", { name: "Language", exact: true }).getByRole("combobox", { name: "Language", exact: true });
  await language.waitFor();
  assert.equal(await language.locator("option").count(), 51);
  if (await page.evaluate(() => CSS.supports("appearance", "base-select"))) {
    await language.focus();
    await page.keyboard.press("Space");
    await page.waitForFunction(() => document.querySelector("select:open"));
    await page.keyboard.press("End");
    assert.equal(await language.evaluate((control) => control.matches(":open")), true);
    await page.keyboard.press("Escape");
  }
  await language.selectOption("ar");
  await page.locator("html[lang=ar][dir=rtl]").waitFor({ state: "attached" });
  await page.setViewportSize({ width: 420, height: 780 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  assert.equal(await page.locator(".settings-modal").evaluate((dialog) => {
    const header = getComputedStyle(dialog.querySelector(".dialog-header"));
    const body = getComputedStyle(dialog.querySelector(".dialog-content"));
    return header.paddingInlineStart === body.paddingInlineStart &&
      parseFloat(header.paddingInlineEnd) === parseFloat(body.paddingInlineEnd) + parseFloat(body.marginInlineEnd);
  }), true);
  await page.locator("dialog select").first().selectOption("ur");
  await page.locator("html[lang=ur][dir=rtl]").waitFor({ state: "attached" });
  await page.locator("dialog select").first().selectOption("bn");
  await page.locator("html[lang=bn][dir=ltr]").waitFor({ state: "attached" });
  await page.locator("dialog select").first().selectOption("en");
  await page.locator("html[lang=en][dir=ltr]").waitFor({ state: "attached" });
  await page.keyboard.press("Escape");
  await behavior.waitFor();
  assert.equal(await behavior.getAttribute("data-page-direction"), "back");
  assert.equal(await behavior.getByRole("button", { name: "Language", exact: true }).evaluate((element) => element === document.activeElement), true);
  await page.keyboard.press("Escape");
  await preferences.waitFor();
  const restored = preferences.getByRole("button", { name: "Appearance and behavior" });
  assert.equal(await restored.evaluate((element) => element === document.activeElement), true);
  await preferences.getByRole("button", { name: "Search and fetch APIs" }).click();
  const searchSettings = page.getByRole("dialog", { name: "Search and fetch APIs" });
  await searchSettings.waitFor();
  assert.equal(await searchSettings.locator("input[type=password]").count(), 0);
  const searchProvidersLink = searchSettings.getByRole("button", { name: "Providers", exact: true });
  await searchProvidersLink.click();
  const searchProviders = page.getByRole("dialog", { name: "Providers", exact: true });
  const keyFields = searchProviders.locator("input[type=password]");
  assert.equal(await keyFields.count(), 1);
  assert.equal(await searchProviders.getByRole("button", { name: "Save", exact: true }).isEnabled(), false);
  const choiceWidths = await searchProviders.getByRole("combobox").evaluateAll((controls) =>
    controls.map((element) => element.getBoundingClientRect().width));
  assert.ok(choiceWidths.every((width) => Math.abs(width - choiceWidths[0]) < 1));
  await searchProviders.getByLabel("Web search API").selectOption("tavily");
  assert.equal(await keyFields.count(), 2);
  await searchProviders.getByRole("button", { name: "Save", exact: true }).click();
  await searchSettings.getByLabel("Page fetching").selectOption("local");
  await searchProvidersLink.click();
  assert.equal(await searchProviders.getByLabel("Web search API").inputValue(), "tavily");
  assert.equal(await searchProviders.getByLabel("Page fetch API").count(), 0);
  assert.equal(await keyFields.count(), 1);
  await searchProviders.getByRole("button", { name: "Back", exact: true }).click();
  await searchSettings.getByRole("button", { name: "Back", exact: true }).click();
  await preferences.getByRole("button", { name: "Models and providers" }).click();
  const modelGroup = page.getByRole("dialog", { name: "Models and providers", exact: true });
  await modelGroup.getByRole("button", { name: "Models", exact: true }).click();
  const models = page.getByRole("dialog", { name: "Models", exact: true });
  assert.equal(await models.getByRole("button", { name: "Browser", exact: true }).isVisible(), false);
  const agentModels = models.locator("details").filter({ has: page.getByText("Agent models", { exact: true }) });
  const agentModelsSummary = agentModels.locator(":scope > summary");
  const corner = await agentModels.evaluate((details) => getComputedStyle(details).borderStartStartRadius);
  await agentModelsSummary.click();
  await models.getByRole("button", { name: "Browser", exact: true }).waitFor();
  assert.ok(parseFloat(corner) >= 16);
  assert.equal(await agentModels.evaluate((details) => getComputedStyle(details.querySelector("summary")).borderStartStartRadius), corner);
  await models.getByRole("button", { name: "Back", exact: true }).click();
  await modelGroup.getByRole("button", { name: "Providers", exact: true }).click();
  const providers = page.getByRole("dialog", { name: "Providers" });
  await providers.getByRole("button", { name: "Add provider" }).click();
  const provider = page.getByRole("dialog", { name: "Add provider" });
  assert.equal(await provider.getByRole("button", { name: "Fetch models" }).isVisible(), false);
  await provider.getByLabel("Name", { exact: true }).fill(longProvider);
  await provider.locator("input[type=url]").fill("http://127.0.0.1:" + service.address().port);
  await provider.locator("input[type=password]").fill("fixture");
  await provider.getByText("Manage models", { exact: true }).click();
  await provider.getByRole("button", { name: "Fetch models" }).click();
  await provider.getByText("fixture-audio", { exact: true }).waitFor();
  await provider.getByRole("button", { name: "Save", exact: true }).click();
  await providers.getByRole("button", { name: /^Fixture provider/ }).waitFor();
  await providers.getByRole("button", { name: "Back", exact: true }).click();
  await modelGroup.getByRole("button", { name: "Models", exact: true }).click();
  const catalog = models.getByRole("region", { name: "Available models", exact: true });
  const providerChoice = catalog.getByRole("combobox", { name: "Provider", exact: true });
  await providerChoice.waitFor();
  if (await page.evaluate(() => CSS.supports("appearance", "base-select"))) {
    assert.equal(await providerChoice.evaluate((select) => {
      const label = select.querySelector("selectedcontent .select-label");
      return label && label.scrollWidth > label.clientWidth && getComputedStyle(label).textOverflow === "ellipsis";
    }), true);
    await providerChoice.click();
    const item = providerChoice.locator("option").filter({ hasText: longProvider });
    assert.equal(await item.locator(".select-label").evaluate((label) => {
      const style = getComputedStyle(label);
      return label.scrollWidth <= label.clientWidth + 1 && label.getBoundingClientRect().height > parseFloat(style.lineHeight);
    }), true);
    await page.keyboard.press("Escape");
  }
  await catalog.getByRole("searchbox", { name: "Search models" }).fill(longModel);
  const savedModel = catalog.getByRole("switch", { name: longModelName, exact: true });
  await savedModel.waitFor();
  assert.equal(await savedModel.isChecked(), true);
  await catalog.getByRole("button", { name: "Model settings · " + longModelName, exact: true }).click();
  const modelOptions = page.getByRole("dialog", { name: "Model settings", exact: true });
  await modelOptions.getByText("Model context window (32768 tokens)", { exact: true }).waitFor();
  assert.equal(await modelOptions.locator('input[type="number"]').count(), 2);
  const limit = modelOptions.getByRole("spinbutton", { name: "Context window (%)", exact: true });
  const backgroundLimit = modelOptions.getByRole("spinbutton", { name: /Start in background/ });
  assert.equal(await limit.inputValue(), "80");
  assert.equal(await backgroundLimit.inputValue(), "70");
  await modelOptions.getByText("Enter 1 to 100", { exact: true }).waitFor();
  await modelOptions.getByText("Enter a percentage below the compaction threshold", { exact: true }).waitFor();
  assert.equal(await modelOptions.getByRole("combobox", { name: /Thinking effort/ }).inputValue(), "high");
  await backgroundLimit.fill("80");
  assert.equal(await modelOptions.getByRole("button", { name: "Save", exact: true }).isEnabled(), false);
  await backgroundLimit.fill("65");
  await modelOptions.getByRole("button", { name: "Save", exact: true }).click();
  await agentModelsSummary.click();
  await models.getByRole("button", { name: "Browser", exact: true }).click();
  const assignment = page.getByRole("dialog", { name: "Browser", exact: true });
  const modelSearch = assignment.getByRole("searchbox", { name: "Search models" });
  await modelSearch.fill("not a fixture model");
  await assignment.getByText("No models", { exact: true }).waitFor();
  await modelSearch.fill(longModel);
  const candidate = assignment.getByRole("button", { name: new RegExp("^" + longModelName) });
  await candidate.click();
  await assignment.waitFor({ state: "hidden" });
  assert.equal(await models.getByRole("button", { name: "Browser", exact: true })
    .locator(".model-assignment-value").textContent(), longModelName);
  for (const [label, modelId, otherId] of [
    ["Image description model", "fixture-vision", "fixture-audio"],
    ["Audio description model", "fixture-audio", "fixture-vision"],
  ]) {
    await models.getByRole("button", { name: label, exact: true }).click();
    const dialog = page.getByRole("dialog", { name: label, exact: true });
    await dialog.getByRole("button", { name: "Off", exact: true, pressed: true }).waitFor();
    assert.equal(await dialog.getByRole("button", { name: new RegExp("^" + otherId) }).count(), 0);
    await dialog.getByRole("button", { name: new RegExp("^" + modelId) }).click();
    await dialog.waitFor({ state: "hidden" });
  }
  await models.getByRole("button", { name: "Save", exact: true }).click();
  await modelGroup.waitFor();
  const savedAssignments = await (await page.request.get("/api/settings")).json();
  const savedProvider = savedAssignments.providers.find((item) => item.name === longProvider);
  assert.deepEqual(savedAssignments.agents.plugin.options, { compact_at_percent: 60 });
  assert.deepEqual(savedAssignments.agents.fetch, { provider: savedProvider.id, model: longModel });
  assert.deepEqual(savedAssignments.agents.image_description, { provider: savedProvider.id, model: "fixture-vision" });
  assert.deepEqual(savedAssignments.agents.audio_description, { provider: savedProvider.id, model: "fixture-audio" });
  assert.equal(savedProvider.model_options[longModel].context_window, 32768);
  assert.equal(savedProvider.model_options[longModel].background_compact_at_percent, 65);
  assert.deepEqual(savedProvider.model_options[longModel].effort_levels, ["high", "low"]);
  await modelGroup.getByRole("button", { name: "Models", exact: true }).click();
  await agentModelsSummary.click();
  await models.getByRole("button", { name: "Plugin subagent", exact: true }).click();
  await page.getByRole("dialog", { name: "Plugin subagent", exact: true })
    .getByRole("button", { name: "Use preset model", exact: true }).click();
  await models.getByRole("button", { name: "Browser", exact: true }).click();
  await assignment.getByRole("button", { name: new RegExp("^" + longModelName), pressed: true }).waitFor();
  await assignment.getByRole("button", { name: "Use chat model", exact: true }).click();
  await models.getByRole("button", { name: "Chat", exact: true }).click();
  const chatAssignment = page.getByRole("dialog", { name: "Chat", exact: true });
  await chatAssignment.getByRole("searchbox", { name: "Search models" }).fill(longModel);
  await chatAssignment.getByRole("button", { name: new RegExp("^" + longModelName) }).click();
  await models.getByRole("button", { name: "Title", exact: true }).click();
  await page.getByRole("dialog", { name: "Title", exact: true }).getByRole("button", { name: /^fixture-model/ })
    .filter({ hasText: longProvider }).click();
  await models.getByRole("button", { name: "Save", exact: true }).click();
  await modelGroup.waitFor();
  assert.equal((await (await page.request.get("/api/settings")).json()).agents.fetch, undefined);
  await modelGroup.getByRole("button", { name: "Back", exact: true }).click();
  await preferences.getByRole("button", { name: "Plugins", exact: true }).click();
  const plugins = page.getByRole("dialog", { name: "Plugins", exact: true });
  await plugins.getByRole("button", { name: /Built in/ }).first().click();
  const pluginDetails = page.locator("dialog.workspace-tools:modal").last();
  await pluginDetails.getByText("Built in", { exact: true }).waitFor();
  assert.equal(await pluginDetails.getByRole("button", { name: "Uninstall" }).count(), 0);
  assert.equal(await pluginDetails.getByRole("button", { name: "Providers", exact: true }).count(), 0);
  await pluginDetails.getByRole("button", { name: "Back", exact: true }).click();
  await plugins.getByRole("button", { name: "Back", exact: true }).click();
  await preferences.getByRole("button", { name: "About", exact: true }).click();
  const about = page.getByRole("dialog", { name: "About", exact: true });
  await about.getByRole("button", { name: "Open source licenses" }).click();
  const licenses = page.getByRole("dialog", { name: "Open source licenses", exact: true });
  await licenses.locator(".workspace-plugin-markdown").first().waitFor();
  const licenseBody = licenses.locator(".dialog-content");
  assert.equal(await licenseBody.evaluate((body) => {
    const dialog = body.closest("dialog");
    const bounds = body.getBoundingClientRect();
    const container = dialog.getBoundingClientRect();
    const header = dialog.querySelector(".dialog-header").getBoundingClientRect();
    return body.scrollHeight > body.clientHeight && bounds.top >= header.bottom - 1 &&
      container.bottom - bounds.bottom >= 8;
  }), true);
  if (await page.evaluate(() => CSS.supports("selector(::-webkit-scrollbar)"))) {
    assert.equal(await licenseBody.evaluate((body) => getComputedStyle(body, "::-webkit-scrollbar-button").display), "none");
    await page.emulateMedia({ forcedColors: "active" });
    assert.equal(await licenseBody.evaluate((body) => getComputedStyle(body).scrollbarColor), "auto");
    await page.emulateMedia({ forcedColors: "none" });
  }
  await licenseBody.hover();
  await page.mouse.wheel(0, 350);
  await page.waitForFunction(() => [...document.querySelectorAll("dialog:modal .dialog-content")].some((element) => element.scrollTop > 0));
  await licenses.getByRole("button", { name: "Back", exact: true }).waitFor();
  await licenses.getByRole("button", { name: "Back", exact: true }).click();
  await about.getByRole("button", { name: "Back", exact: true }).click();
  assert.equal(await preferences.getByRole("button", { name: "Close", exact: true }).evaluate(async (button) => {
    const dialog = button.closest("dialog");
    await Promise.allSettled(dialog.getAnimations().map((animation) => animation.finished));
    button.click();
    await new Promise(requestAnimationFrame);
    return dialog.isConnected && !dialog.open && dialog.inert &&
      dialog.getAnimations().some((animation) => animation.transitionProperty === "opacity");
  }), true);
  await page.locator("dialog.settings-modal").waitFor({ state: "detached" });
  const libraryToggle = page.locator(".toolbar button[aria-controls=library]");
  const drawer = page.locator(".sidebar");
  for (const dir of ["ltr", "rtl"]) {
    await page.locator("html").evaluate((element, dir) => { element.dir = dir; }, dir);
    await libraryToggle.click();
    await verifyTravel(drawer, "translate", dir === "rtl" ? "right" : "left",
      dir === "rtl" ? page.viewportSize().width : 0);
    assert.equal(await page.locator(".drawer-close").evaluate((element) => element === document.activeElement), true);
    await page.keyboard.press("Escape");
    await drawer.waitFor({ state: "hidden" });
    assert.equal(await libraryToggle.evaluate((element) => element === document.activeElement), true);
  }
  await page.locator("html").evaluate((element) => { element.dir = "ltr"; });
  await page.emulateMedia({ reducedMotion: "reduce" });
  const home = page.locator(".preset-home");
  assert.equal(await home.evaluate((element) => getComputedStyle(element).animationName), "none");
  assert.equal(await page.locator("main .composer").count(), 0);
  assert.equal(await page.locator("main").getByRole("button", { name: "Choose preset", exact: true }).count(), 0);
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await page.setViewportSize({ width: 1200, height: 800 });

  const send = (op, args = {}) => page.request.post("/api/command", { data: { ...args, op } });
  const createButton = home.getByRole("button", { name: "Create preset", exact: true });
  const presetCreation = Promise.withResolvers();
  await page.route("**/api/command", async (route) => {
    const command = route.request().postDataJSON();
    if (command?.op === "create_preset") {
      presetCreation.resolve(command);
      await createPresetRequest.promise;
    }
    await route.continue();
  });
  await page.setViewportSize({ width: 400, height: 730 });
  await libraryToggle.click();
  await page.locator(".sidebar.visible").getByRole("button", { name: "Create preset", exact: true }).click();
  assert.deepEqual(await presetCreation.promise, { name: "Create preset", locale: "en", open: true, op: "create_preset" });
  await page.locator(".sidebar").waitFor({ state: "hidden" });
  assert.equal(await createButton.isEnabled(), false);
  assert.equal(await page.getByRole("textbox", { name: "Preset name" }).count(), 0);
  createPresetRequest.resolve();
  await page.getByRole("heading", { name: "Edit preset" }).waitFor();
  await page.locator(".sidebar").waitFor({ state: "hidden" });
  await page.unroute("**/api/command");
  await page.setViewportSize({ width: 1200, height: 800 });
  const message = page.getByRole("textbox", { name: "Message", exact: true });
  const presetBack = page.locator(".toolbar-title").getByRole("button", { name: "Back", exact: true });
  const presetStatus = page.locator(".preset-working");
  const stop = page.getByRole("button", { name: "Stop", exact: true });
  await stop.waitFor();
  assert.equal(await presetStatus.count(), 0);
  assert.equal(await presetBack.isEnabled(), false);
  assert.equal(await page.locator("#preset-select").isEnabled(), false);
  await page.reload();
  await stop.waitFor();
  assert.equal(await presetStatus.count(), 0);
  assert.equal(await presetBack.isEnabled(), false);
  presetInterview.resolve();
  await page.locator(".transcript").getByText("Choose the experience for this preset.", { exact: true }).waitFor();
  await stop.waitFor({ state: "hidden" });
  assert.equal(await presetBack.isEnabled(), false);
  assert.equal(await page.locator(".library-footer .create-preset-fab").isEnabled(), false);
  await message.fill("Confirm this fixture preset.");
  await message.press("Enter");
  await page.locator(".transcript").getByText("The fixture preset is ready.", { exact: true }).waitFor();
  const calendarTrace = page.locator(".trace").filter({ has: page.getByText("Configure calendar", { exact: true }) });
  if (await calendarTrace.getAttribute("open") === null) await calendarTrace.locator(":scope > summary").click();
  await calendarTrace.getByLabel("Arguments").scrollIntoViewIfNeeded();
  await calendarTrace.getByLabel("Arguments").frameLocator('iframe[src="/openui/tool.html"]')
    .getByText("Managed by a plugin", { exact: true }).waitFor();
  await calendarTrace.getByLabel("Result").scrollIntoViewIfNeeded();
  await calendarTrace.getByLabel("Result").frameLocator('iframe[src="/openui/tool.html"]')
    .getByText("Changes staged", { exact: true }).waitFor();
  await page.locator(".trace").getByText("Edit preset setting", { exact: true }).waitFor();
  const composerModels = page.locator(".composer .composer-models");
  const presetModel = composerModels.getByRole("button", { name: /^Preset · / });
  const pluginModel = composerModels.getByRole("button", { name: /^Plugin subagent · / });
  assert.equal(await presetModel.locator(".composer-model-label > small").textContent(), "Select model");
  assert.equal(await pluginModel.locator(".composer-model-label > small").textContent(), "Select model");
  await pluginModel.click();
  const pluginPicker = page.getByRole("dialog", { name: "Plugin subagent", exact: true });
  await verifyTravel(pluginPicker, "transform", "bottom", page.viewportSize().height);
  const pluginFixture = (checked) => pluginPicker.getByRole("radio", { checked })
    .filter({ has: page.getByText("fixture-model", { exact: true }) })
    .filter({ has: page.getByText(longProvider, { exact: true }) });
  await pluginPicker.getByRole("radio", { name: "Use preset model", checked: true }).waitFor();
  await pluginPicker.getByRole("searchbox", { name: "Search models" }).fill("fixture-model");
  await pluginFixture(false).click();
  await composerModels.getByRole("button", { name: "Plugin subagent · fixture-model", exact: true }).waitFor();
  await pluginModel.click();
  await pluginFixture(true).waitFor();
  await pluginPicker.getByRole("radio", { name: "Use preset model" }).click();
  await composerModels.getByRole("button", { name: "Plugin subagent · Select model", exact: true }).waitFor();
  await presetBack.click();
  const homeTitle = home.getByRole("button", { name: "Fixture preset", exact: true });
  await homeTitle.click();
  await page.getByRole("heading", { name: "Edit preset" }).waitFor();
  await message.fill("Hold this preset edit.");
  await message.press("Enter");
  await stop.waitFor();
  assert.equal(await presetStatus.count(), 0);
  assert.equal(await presetBack.isEnabled(), false);
  assert.equal(await page.locator("#preset-select").isEnabled(), false);
  presetEdit.resolve();
  await page.locator(".transcript").getByText("The preset edit is complete.", { exact: true }).waitFor();
  await presetBack.click();
  await homeTitle.waitFor();
  await page.setViewportSize({ width: 400, height: 720 });
  const chatModel = composerModels.getByRole("button", { name: /^Chat · / });
  assert.equal(await chatModel.locator(".composer-model-label > small").count(), 0);
  await composerModels.getByRole("button", { name: /^Search · / }).waitFor();
  await chatModel.click();
  const chatPicker = page.getByRole("dialog", { name: "Chat", exact: true });
  await verifyTravel(chatPicker, "transform", "bottom", page.viewportSize().height);
  await chatPicker.getByRole("radio", { name: new RegExp("^" + longModelName), checked: true }).waitFor();
  const fillsMobileWidth = (sheet) => {
    const bounds = sheet.getBoundingClientRect();
    return Math.abs(bounds.bottom - innerHeight) < 1 && Math.abs(bounds.width - innerWidth) < 1;
  };
  await page.waitForFunction(fillsMobileWidth, await chatPicker.elementHandle());
  const pickerLayout = (sheet) => {
    const content = sheet.querySelector(".dialog-content");
    return [getComputedStyle(sheet).flexDirection, content.offsetWidth, content.offsetHeight];
  };
  const openLayout = await chatPicker.evaluate(pickerLayout);
  await page.mouse.click(8, 8);
  const closingPicker = page.locator(".model-sheet:not([open])");
  assert.deepEqual(await closingPicker.evaluate(pickerLayout), openLayout);
  await chatPicker.waitFor({ state: "hidden" });
  await chatModel.click();
  await page.waitForFunction(fillsMobileWidth, await chatPicker.elementHandle());
  const handle = chatPicker.locator(".model-sheet-handle");
  const handleBounds = await handle.boundingBox();
  const handleCenter = [handleBounds.x + handleBounds.width / 2, handleBounds.y + handleBounds.height / 2];
  await page.mouse.move(...handleCenter);
  await page.mouse.down();
  await page.mouse.move(handleCenter[0], handleCenter[1] + 40, { steps: 3 });
  await page.mouse.up();
  await verifyTravel(chatPicker, "transform", "bottom", page.viewportSize().height);
  await page.waitForFunction(fillsMobileWidth, await chatPicker.elementHandle());
  await page.mouse.move(...handleCenter);
  await page.mouse.down();
  await page.mouse.move(handleCenter[0], handleCenter[1] + 80, { steps: 5 });
  await page.mouse.up();
  await chatPicker.waitFor({ state: "hidden" });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  await page.setViewportSize({ width: 1200, height: 800 });
  const staged = page.waitForResponse((response) => new URL(response.url()).pathname === "/api/attachments");
  await page.locator(".composer input[type=file]").setInputFiles([
    { name: "fixture.txt", mimeType: "text/plain", buffer: Buffer.from("Fixture attachment") },
    { name: "fixture.png", mimeType: "image/png",
      buffer: Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZ3sAAAAASUVORK5CYII=", "base64") },
    { name: "fixture.wav", mimeType: "audio/wav",
      buffer: Buffer.from("524946462600000057415645666d74201000000001000100803e0000007d00000200100064617461020000000000", "hex") },
  ]);
  const stagedResponse = await staged;
  assert.ok(stagedResponse.ok(), await stagedResponse.text());
  await page.locator(".composer .attachment").filter({ hasText: "fixture.txt" }).waitFor();
  await page.locator(".composer .attachment").filter({ hasText: "fixture.wav" }).waitFor();
  await message.fill("Hello from the desktop test.");
  await message.press("Enter");
  const transcriptReply = page.locator(".transcript .assistant-reply").getByText(reply, { exact: true });
  await transcriptReply.waitFor();
  await page.locator(".toolbar-rename").getByText(generatedTitle, { exact: true }).waitFor();
  await page.locator(".sidebar").getByRole("button", { name: generatedTitle, exact: true }).waitFor();
  assert.equal(titleRequests.length, 1);
  assert.equal(titleRequests[0].model, "fixture-model");
  assert.equal(titleRequests[0].tools?.length ?? 0, 0);
  const chatRequest = await configuredChatRequest.promise;
  assert.equal(chatRequest.model, longModel);
  assert.equal(chatRequest.effort, "high");
  assert.match(chatRequest.content, /A small fixture image\./);
  assert.match(chatRequest.content, /A short fixture recording\./);
  const trace = page.locator(".trace").filter({ hasText: "Test trace" }).last();
  const traceSummary = trace.locator("summary");
  if (await trace.getAttribute("open") !== null) await traceSummary.click();
  await traceSummary.hover();
  assert.equal(await traceSummary.evaluate((summary) =>
    getComputedStyle(summary).borderTopLeftRadius === getComputedStyle(summary.parentElement).borderTopLeftRadius), true);
  assert.equal(await trace.evaluate((card) => getComputedStyle(card).backgroundColor), "rgba(0, 0, 0, 0)");
  if (await trace.getAttribute("open") === null) await traceSummary.click();
  const thought = trace.locator(".trace-reasoning");
  await thought.waitFor();
  await page.mouse.move(0, 0);
  await page.waitForFunction((summary) => {
    const style = getComputedStyle(summary);
    const probe = document.createElement("span");
    probe.style.background = "var(--primary)";
    probe.style.color = "var(--on-primary)";
    document.body.append(probe);
    const target = getComputedStyle(probe);
    const body = summary.nextElementSibling;
    const bounds = summary.getBoundingClientRect();
    const hitsBody = (x, y) => document.elementFromPoint(x, y)?.closest(".trace-body") === body;
    const correct = style.backgroundColor === target.backgroundColor && style.color === target.color &&
      style.position === "sticky" && style.borderTopLeftRadius !== "0px" &&
      style.borderTopLeftRadius === style.borderBottomRightRadius &&
      getComputedStyle(body).clipPath.startsWith("inset(") &&
      !hitsBody(bounds.left + 2, bounds.top + 2) && !hitsBody(bounds.right - 2, bounds.top + 2) &&
      hitsBody(bounds.left + 2, bounds.bottom - 2) && hitsBody(bounds.right - 2, bounds.bottom - 2) &&
      getComputedStyle(summary, "::before").content === "none";
    probe.remove();
    return correct;
  }, await traceSummary.elementHandle());
  assert.equal(await thought.evaluate((text) =>
    getComputedStyle(text).fontFamily === getComputedStyle(document.body).fontFamily && getComputedStyle(text).whiteSpace === "pre-wrap"), true);
  await message.fill("Existing draft.");
  await transcriptReply.evaluate((paragraph) => {
    const range = document.createRange();
    range.selectNodeContents(paragraph);
    window.getSelection().removeAllRanges();
    window.getSelection().addRange(range);
  });
  await transcriptReply.dispatchEvent("contextmenu", { bubbles: true, cancelable: true });
  await page.getByRole("menu", { name: "Quote", exact: true }).getByRole("menuitem", { name: "Quote", exact: true }).click();
  const draftQuote = page.locator(".composer .quote-attachment");
  await draftQuote.getByRole("button", { name: "Remove Quote" }).waitFor();
  assert.equal(await draftQuote.locator(".quote-preview").textContent(), reply);
  assert.equal(await message.inputValue(), "Existing draft.");
  await draftQuote.locator("summary").click();
  const quoteBody = draftQuote.locator("blockquote");
  await quoteBody.waitFor();
  assert.equal(await quoteBody.textContent(), reply);
  await page.locator(".composer").getByRole("button", { name: "Send", exact: true }).waitFor();
  const quotedMessage = "Show a fixture card.";
  await message.fill(quotedMessage);
  const quoteSent = page.waitForRequest((request) => request.url().endsWith("/api/command") &&
    request.postDataJSON()?.op === "message" && request.postDataJSON().text === quotedMessage);
  await page.locator(".composer").getByRole("button", { name: "Send", exact: true }).click();
  const [quote] = (await quoteSent).postDataJSON().attachments;
  assert.equal(quote.kind, "quote");
  assert.equal(quote.text, reply);
  const modelQuote = await expectSignal(quotedModelRequest, "The reply model did not receive the message with its quote.");
  assert.equal(modelQuote, quotedMessage + "\n\nQuoted passage (reference):\n> " + reply);
  await draftQuote.waitFor({ state: "hidden" });
  const sentQuote = page.locator(".transcript .user-message").filter({ has: page.getByText(quotedMessage, { exact: true }) })
    .locator(".quote-attachment");
  await sentQuote.waitFor();
  assert.equal(await sentQuote.locator(".quote-preview").textContent(), reply);
  await page.frameLocator('iframe.open-ui[src="/openui/index.html"]').getByText("Fixture card", { exact: true }).waitFor();
  const more = page.locator(".toolbar-actions").getByRole("button", { name: "More options", exact: true });
  await more.press("ArrowDown");
  const actions = page.getByRole("menu", { name: "More options", exact: true });
  await actions.getByRole("menuitem", { name: "World", exact: true }).waitFor();
  assert.equal(await actions.getByRole("menuitem", { name: "Return to home", exact: true }).count(), 0);
  assert.equal(await actions.getByRole("menuitem", { name: "World", exact: true }).evaluate((item) => item === document.activeElement), true);
  await page.keyboard.press("End");
  assert.equal(await actions.getByRole("menuitem", { name: "Manage preset" }).evaluate((item) => item === document.activeElement), true);
  await page.keyboard.press("Escape");
  assert.equal(await more.evaluate((item) => item === document.activeElement), true);
  await page.setViewportSize({ width: 1080, height: 220 });
  await more.press("ArrowUp");
  await page.waitForFunction(() => {
    const menu = document.querySelector(".action-menu:popover-open");
    const item = document.activeElement;
    return menu?.contains(item) && menu.scrollTop > 0 &&
      item.getBoundingClientRect().bottom <= menu.getBoundingClientRect().bottom;
  });
  await page.keyboard.press("Escape");
  await page.setViewportSize({ width: 1200, height: 800 });
  await page.locator(".toolbar-rename").click();
  const rename = page.getByRole("dialog", { name: "Rename", exact: true });
  await rename.getByRole("button", { name: "Generate title" }).click();
  await page.locator(".toolbar-rename").getByText(regeneratedTitle, { exact: true }).waitFor();
  assert.equal(titleRequests.length, 2);
  const titledRoom = page.locator(".sidebar").getByRole("button", { name: regeneratedTitle, exact: true });
  const roomMenu = page.locator(".sidebar").getByRole("button", { name: "More options · " + regeneratedTitle, exact: true });
  async function checkLongTitle() {
    assert.equal(await titledRoom.getAttribute("title"), regeneratedTitle);
    assert.equal(await titledRoom.locator(".choice-label").evaluate((label) => {
      const row = label.closest(".room-row");
      const bounds = row.getBoundingClientRect();
      const action = row.querySelector(".icon-button").getBoundingClientRect();
      return getComputedStyle(label).textOverflow === "ellipsis" && getComputedStyle(label).direction === "rtl" &&
        label.scrollWidth > label.clientWidth && action.width >= 39 &&
        action.left >= bounds.left && action.right <= bounds.right && row.scrollWidth <= row.clientWidth + 1;
    }), true);
  }
  await checkLongTitle();
  await page.setViewportSize({ width: 320, height: 720 });
  await page.locator("html").evaluate((element) => { element.dir = "rtl"; });
  await libraryToggle.click();
  await checkLongTitle();
  await roomMenu.click();
  await page.getByRole("menuitem", { name: "Delete chat", exact: true }).waitFor();
  await page.keyboard.press("Escape");
  await page.keyboard.press("Escape");
  await page.locator("html").evaluate((element) => { element.dir = "ltr"; });
  await page.setViewportSize({ width: 1200, height: 800 });
  await more.click();
  await actions.getByRole("menuitem", { name: "Chat settings" }).click();
  const chatSettings = page.getByRole("dialog", { name: "Chat settings" });
  assert.equal(await chatSettings.getByRole("button", { name: "Reset world" }).count(), 0);
  await chatSettings.getByRole("button", { name: "Close" }).click();
  await more.click();
  await actions.getByRole("menuitem", { name: "World", exact: true }).click();
  await page.getByRole("heading", { name: "World", exact: true, level: 1 }).waitFor();
  assert.equal(await page.getByRole("button", { name: "Advance story" }).count(), 0);
  const envRequest = page.waitForRequest((request) => request.url().endsWith("/api/command") && request.postDataJSON()?.agent === "env");
  await message.fill("The dock crew has finished unloading.");
  await message.press("Enter");
  assert.equal(Object.hasOwn((await envRequest).postDataJSON(), "stage"), false);
  await page.locator(".transcript").getByText("The next watch has begun.", { exact: true }).waitFor();
  await more.click();
  await actions.getByRole("menuitem", { name: "Settings", exact: true }).click();
  const worldSettings = page.getByRole("dialog", { name: "Settings · World" });
  await worldSettings.getByRole("button", { name: "Reset world" }).waitFor();
  await worldSettings.getByRole("button", { name: "Close" }).click();
  await page.locator(".toolbar-title").getByRole("button", { name: "Back", exact: true }).click();
  await more.click();
  await actions.getByRole("menuitem", { name: "Memory", exact: true }).click();
  const memory = page.getByRole("dialog", { name: "Memory", exact: true });
  await memory.getByRole("button", { name: "New note" }).click();
  const note = page.getByRole("dialog", { name: "New note", exact: true });
  await note.getByRole("textbox", { name: "Key", exact: true }).fill("fixture-note");
  await note.getByRole("textbox", { name: "Content", exact: true }).fill("Fixture memory content.");
  await note.getByRole("button", { name: "Save", exact: true }).click();
  await memory.getByRole("button", { name: /^fixture-note/ }).click();
  const editNote = page.getByRole("dialog", { name: "Edit entry" });
  assert.equal(await editNote.getByRole("textbox", { name: "Content", exact: true }).inputValue(), "Fixture memory content.");
  await editNote.getByRole("button", { name: "Close", exact: true }).click();
  await memory.getByRole("button", { name: "Close", exact: true }).click();
  await more.click();
  const downloaded = page.waitForEvent("download");
  await actions.getByRole("menuitem", { name: "Export transcript" }).click();
  const file = await downloaded;
  assert.match(file.suggestedFilename(), /\.jsonl$/);
  const lines = (await readFile(await file.path(), "utf8")).trim().split(/\r?\n/).map(JSON.parse);
  assert.ok(lines.some((record) => record.content?.includes(reply)));
  assert.ok(lines.some((record) => record.attachments?.some((file) => file.name === "fixture.txt")));
  const exportedQuote = lines.find((record) => record.content === quotedMessage)?.attachments;
  assert.deepEqual(exportedQuote, [quote]);
  await page.locator(".toolbar-rename").click();
  await rename.getByRole("textbox").fill(longManualTitle);
  const renameRequest = page.waitForRequest((request) => request.url().endsWith("/api/command") &&
    request.postDataJSON()?.op === "rename");
  await rename.getByRole("button", { name: "Save", exact: true }).click();
  const renamedChat = (await renameRequest).postDataJSON();
  await page.locator(".toolbar-rename").getByText(longManualTitle, { exact: true }).waitFor();
  assert.ok((await send("home", { preset: renamedChat.preset })).ok());
  await homeTitle.waitFor();
  const longTitleRoom = page.locator(".sidebar").getByRole("button", { name: longManualTitle, exact: true });
  assert.equal(await longTitleRoom.getAttribute("title"), longManualTitle);
  await longTitleRoom.click();
  await transcriptReply.waitFor();
  await page.reload();
  await transcriptReply.waitFor();
  await page.locator(".toolbar-rename").getByText(longManualTitle, { exact: true }).waitFor();
  assert.equal(await longTitleRoom.getAttribute("title"), longManualTitle);
  await sentQuote.waitFor();
  assert.equal(await sentQuote.locator(".quote-preview").textContent(), reply);
  await message.fill("Cancel this fixture turn.");
  await message.press("Enter");
  await page.locator(".transcript").getByText("Waiting for cancellation.", { exact: true }).waitFor();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await preferences.getByRole("button", { name: "Appearance and behavior" }).click();
  await themeChoice.waitFor();
  await behavior.getByRole("button", { name: "Close", exact: true }).click();
  await stop.click();
  await stop.waitFor({ state: "hidden" });
  const branchUserMessage = page.locator(".user-message[data-branch-message]").filter({ has: page.getByText(quotedMessage, { exact: true }) });
  await branchUserMessage.waitFor();
  assert.equal(await branchUserMessage.getByRole("button", { name: "Edit in a branch" }).count(), 0);
  await branchUserMessage.focus();
  await branchUserMessage.press("Shift+F10");
  const editBranch = page.getByRole("menuitem", { name: "Edit in a branch" });
  await editBranch.waitFor();
  await page.keyboard.press("Escape");
  await editBranch.waitFor({ state: "hidden" });
  await branchUserMessage.click({ button: "right" });
  await editBranch.click();
  await page.waitForFunction((text) => document.querySelector(".composer textarea").value === text, quotedMessage);
  await draftQuote.waitFor();
  assert.equal(await draftQuote.locator(".quote-preview").textContent(), reply);
  async function openPresetManagement() {
    await more.click();
    await actions.getByRole("menuitem", { name: "Manage preset" }).click();
    return page.getByRole("dialog", { name: "Manage preset", exact: true });
  }
  let management = await openPresetManagement();
  const archiveEvent = page.waitForEvent("download");
  await management.getByRole("button", { name: "Export preset", exact: true }).click();
  const archive = await archiveEvent;
  assert.match(archive.suggestedFilename(), /\.minirp-preset$/);
  management = await openPresetManagement();
  const saveEvent = page.waitForEvent("download");
  await management.getByRole("button", { name: "Export save", exact: true }).click();
  const save = await saveEvent;
  assert.match(save.suggestedFilename(), /\.minirp-save$/);
  const sidebar = page.locator(".sidebar");
  async function verifyPresetActions(picker) {
    const [importAction, createAction] = await Promise.all([
      picker.locator(".import-fab").boundingBox(),
      picker.locator(".create-preset-fab").boundingBox(),
    ]);
    assert.ok(importAction && createAction);
    assert.ok(Math.abs(importAction.x + importAction.width - createAction.x - createAction.width) < 1);
    assert.ok(Math.abs(createAction.y - importAction.y - importAction.height - 8) < 1);
  }
  const presetPicker = sidebar.locator("#preset-select");
  const primaryAction = sidebar.locator(".library-footer .create-preset-fab");
  const chatAction = await primaryAction.boundingBox();
  assert.equal((await presetPicker.textContent()).includes("Fixture preset"), true);
  await presetPicker.press("Enter");
  assert.equal(await presetPicker.getAttribute("aria-expanded"), "true");
  assert.equal(await presetPicker.getByText("Choose preset", { exact: true }).count(), 1);
  const importFab = sidebar.getByRole("button", { name: "Import…", exact: true });
  await sidebar.locator(".library-footer").evaluate(async (footer) => {
    await Promise.all(footer.getAnimations({ subtree: true }).map((animation) => animation.finished.catch(() => {})));
  });
  await verifyPresetActions(sidebar);
  const presetAction = await primaryAction.boundingBox();
  assert.ok(Math.abs(chatAction.y - presetAction.y) < 1);
  assert.ok(Math.abs(chatAction.x + chatAction.width - presetAction.x - presetAction.width) < 1);
  const importPreset = sidebar.getByRole("button", { name: "Import preset", exact: true });
  assert.equal(await importPreset.isVisible(), false);
  await importFab.click();
  await importPreset.waitFor();
  const importSave = sidebar.getByRole("button", { name: "Import save", exact: true });
  await importSave.waitFor();
  await sidebar.getByRole("button", { name: "What’s the difference?" }).click();
  const help = page.getByRole("dialog", { name: "What’s the difference?" });
  await help.getByText("A preset contains the setup and its plugins.", { exact: true }).waitFor();
  await help.getByText("A save also includes rooms, conversations, attachments, and progress.", { exact: true }).waitFor();
  await help.getByRole("button", { name: "Close" }).click();
  await importFab.click();
  await importPreset.press("Escape");
  assert.equal(await importFab.getAttribute("aria-expanded"), "false");
  assert.equal(await importFab.evaluate((button) => button === document.activeElement), true);
  async function importArchive(kind, button, file) {
    await importFab.click();
    const chooser = page.waitForEvent("filechooser");
    await button.click();
    const imported = page.waitForResponse((response) => new URL(response.url()).pathname === "/api/import/" + kind);
    await (await chooser).setFiles({
      name: file.suggestedFilename(), mimeType: "application/zip", buffer: await readFile(await file.path()),
    });
    const response = await imported;
    assert.ok(response.ok(), await response.text());
  }
  await importArchive("preset", importPreset, archive);
  management = await openPresetManagement();
  await management.getByRole("button", { name: "Delete preset", exact: true }).click();
  await page.getByRole("dialog", { name: "Delete “Fixture preset”?" }).getByRole("button", { name: "Delete", exact: true }).click();
  await page.getByRole("heading", { name: "No presets yet" }).waitFor();
  await importArchive("save", importSave, save);
  await presetPicker.click();
  await sidebar.locator(".room-list .room-row").first().waitFor();
  await presetPicker.click();
  const ensemblePreset = "7844467a-14ae-48eb-8ef7-9c34d21b484d";
  await mkdir(join(workspace, "presets"), { recursive: true });
  await writeFile(join(workspace, "presets", ensemblePreset + ".json"), JSON.stringify({
    name: "Fixture ensemble", chat_mode: "ensemble", appearance: { theme_color: "#349777" },
    chat_agent: { greeting_system_message: "The watch is starting. Give each scene its opening." },
    ensemble: {
      agents: [{ id: "ada", name: "Ada" }, { id: "bryn", name: "Bryn" }],
      chats: [{ name: "Bridge", members: ["ada", "bryn"] }, { name: "Lab", members: ["bryn"] }],
      loading_messages: ["Navigation lights flicker beyond the glass.", "A watch schedule hangs beside the cargo door."],
    },
  }));
  assert.ok((await send("list_presets")).ok());
  await sidebar.getByRole("button", { name: "Fixture ensemble", exact: true }).click();
  await presetPicker.getByText("Fixture ensemble", { exact: true }).waitFor();
  assert.equal(await page.locator("html").evaluate((root) => root.style.getPropertyValue("--surface")), "");
  assert.match(await page.locator("html").evaluate((root) => root.style.getPropertyValue("--primary")), /^#[0-9a-f]{6}$/);
  assert.equal(await page.locator("main").getByRole("button", { name: "Choose preset", exact: true }).count(), 0);
  await home.getByRole("heading", { name: "Fixture ensemble", exact: true }).waitFor();
  await home.getByRole("button", { name: "New room" }).click();
  const loading = page.getByRole("dialog", { name: "Working" });
  const scenes = page.locator(".ensemble-overview");
  const bridge = scenes.getByRole("button", { name: /^Bridge\b/ });
  await page.waitForFunction(() => document.querySelector(".loading-text[data-typing=true]")?.textContent.length > 0);
  await loading.locator(".loading-text[data-typing=false]").filter({ hasText: "Navigation lights flicker beyond the glass." }).waitFor();
  await loading.locator("progress[value=\"0.25\"]").waitFor();
  assert.equal(await bridge.isVisible(), false);
  await loading.locator(".loading-text[data-typing=false]").filter({ hasText: "A watch schedule hangs beside the cargo door." }).waitFor();
  assert.equal((await loading.innerText()).includes("0/2"), false);
  await page.waitForFunction(() => parseFloat(document.querySelector(".loading-progress")?.style.getPropertyValue("--progress")) > 5);
  readiness.Ada.resolve();
  await loading.locator("progress[value=\"0.4\"]").waitFor();
  assert.equal((await loading.innerText()).includes("1/2"), false);
  assert.equal(await bridge.isVisible(), false);
  readiness.Bryn.resolve();
  await loading.locator("progress[value=\"0.75\"]").waitFor();
  assert.equal(await bridge.isVisible(), false);
  labOpening.resolve();
  await loading.waitFor({ state: "hidden" });
  assert.deepEqual(readinessCalls.slice(0, 2).sort(), ["Ada progress", "Bryn progress"]);
  assert.deepEqual(readinessCalls.slice(2).sort(), ["Ada ready", "Bryn ready"]);
  assert.deepEqual(openings.slice().sort(), ["Ada", "Bryn"]);
  await bridge.click();
  await page.locator(".transcript").getByText("Ada opened the bridge.", { exact: true }).waitFor();
  assert.equal(await page.locator(".transcript").getByText("Bryn opened the bridge.", { exact: true }).count(), 0);
  await message.fill("Report to the bridge.");
  await message.press("Enter");
  await expectSignal(bridgeAnswerStarted, "Ada did not start the bridge reply.");
  await message.fill("The bridge stays open during the report.");
  await message.press("Enter");
  await page.locator(".transcript").getByText("The bridge stays open during the report.", { exact: true }).waitFor();
  await page.getByRole("button", { name: "Back", exact: true }).click();
  const lab = scenes.getByRole("button", { name: /^Lab\b/ });
  await lab.waitFor();
  await message.waitFor({ state: "hidden" });
  const privateMessages = page.locator(".room-private-fab");
  await privateMessages.waitFor();
  bridgeAnswer.resolve();
  await expectSignal(labAnswered, "Bryn did not relay the bridge report while the overview was open.");
  const bounds = await lab.boundingBox();
  const resting = await lab.evaluate((button) => parseFloat(getComputedStyle(button).borderTopLeftRadius));
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
  await page.mouse.down();
  const frames = await lab.evaluate((button) => new Promise((resolve) => {
    const samples = [];
    function frame() {
      const style = getComputedStyle(button);
      samples.push({ radius: parseFloat(style.borderTopLeftRadius), transform: style.transform });
      if (samples.length === 12) resolve(samples);
      else requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
  }));
  await page.mouse.up();
  assert.ok(resting >= 15);
  assert.ok(frames.every((frame) => frame.radius === resting && frame.transform === "none"));
  await message.waitFor();
  assert.equal(await privateMessages.count(), 0);
  await page.locator(".transcript").getByText("Bryn opened the lab.", { exact: true }).waitFor();
  await page.locator(".transcript").getByText("Bryn carried the report to the lab.", { exact: true }).waitFor();
  assert.equal(await page.locator(".transcript").getByText("Ada reported from the bridge.", { exact: true }).count(), 0);
  assert.equal(await page.locator(".transcript").getByText("Private fixture response.", { exact: true }).count(), 0);
  await page.locator(".ensemble-bar").getByRole("button", { name: "Bridge", exact: true }).click();
  await page.locator(".transcript").getByText("Ada reported from the bridge.", { exact: true }).waitFor();
  assert.equal(await page.locator(".transcript").getByText("Bryn carried the report to the lab.", { exact: true }).count(), 0);
  assert.equal(openings.length, 2);
  await message.fill("Hold this room worker.");
  await message.press("Enter");
  await expectSignal(roomWorkerStarted, "Ada did not start waiting for room departure.");
  assert.ok((await send("message", { agent: "env", text: "Archive the bridge and open a workshop." })).ok());
  await expectSignal(roomWorkerStopped, "Archiving the selected chat did not cancel the active model stream.");
  await scenes.getByRole("button", { name: /^Workshop\b/ }).waitFor();
  assert.equal(await bridge.count(), 0);
  await message.waitFor({ state: "hidden" });
  const archiveMenu = page.locator("main").getByRole("button", { name: "More options", exact: true });
  await archiveMenu.click();
  const archiveAction = page.getByRole("menuitem", { name: "Archived chats" });
  const archiveGlyph = await archiveAction.locator(".symbol").first().evaluate(async (icon) => {
    const size = parseFloat(getComputedStyle(icon).fontSize);
    const font = `${size}px "Material Symbols Rounded"`;
    await document.fonts.load(font, icon.textContent);
    const context = document.createElement("canvas").getContext("2d");
    context.font = font;
    return context.measureText(icon.textContent).width / size;
  });
  assert.ok(Math.abs(archiveGlyph - 1) < 0.01, "Archive should render as one Material Symbol");
  await archiveAction.click();
  const archives = page.getByRole("dialog", { name: "Archived chats", exact: true });
  const archivedBridge = archives.getByRole("button", { name: "Bridge", exact: true });
  await archivedBridge.click();
  const archivedHistory = page.getByRole("dialog", { name: "Bridge", exact: true });
  await archivedHistory.getByText("Ada reported from the bridge.", { exact: true }).waitFor();
  assert.equal(await archivedHistory.locator(".composer").count(), 0);
  assert.equal(await archivedHistory.getByRole("button", { name: /restore/i }).count(), 0);
  await archivedHistory.getByRole("button", { name: "Back", exact: true }).click();
  await archivedBridge.waitFor();
  await archivedBridge.click();
  await archivedHistory.getByText("Report to the bridge.", { exact: true }).waitFor();
  await page.keyboard.press("Escape");
  await archivedBridge.waitFor();
  await archives.getByRole("button", { name: "Close", exact: true }).click();
  await scenes.getByRole("button", { name: /^Workshop\b/ }).waitFor();
  await page.waitForFunction(() => document.activeElement?.getAttribute("aria-label") === "More options");
  const longTrace = Array.from({ length: 90 }, (_, index) => "Trace detail " + index).join("\n");
  const stubMemberTrace = async (route) => {
    if (route.request().postDataJSON()?.op !== "ensemble_member_trace") return route.continue();
    await route.fulfill({ json: { blocks: [
      { id: "parent-thoughts", agent: "ChatAgent", reasoning: longTrace },
      { id: "parent-tool", agent: "ChatAgent", tool: "Fixture tool", result: longTrace },
      { id: "search-thoughts", agent: "SearchAgent", reasoning: "Search trace details." },
      { id: "browser-thoughts", agent: "BrowserAgent", parent_agent: "SearchAgent", reasoning: "Browser trace details." },
    ] } });
  };
  await page.route("**/api/command", stubMemberTrace);
  await privateMessages.click();
  await page.getByRole("dialog", { name: "Private messages" }).getByRole("button", { name: "Ada", exact: true }).click();
  await page.getByRole("button", { name: "Activity trace", exact: true }).click();
  const memberTrace = page.getByRole("dialog", { name: "Activity trace", exact: true });
  for (const title of ["Thoughts", "Fixture tool"]) {
    const summary = memberTrace.locator(".trace > summary").filter({ hasText: title });
    if (await summary.evaluate((header) => !header.parentElement.open)) await summary.press("Enter");
    await page.waitForFunction((header) => header.parentElement.open, await summary.elementHandle());
    if (title === "Fixture tool") await memberTrace.locator(".trace-json-field > summary").press("Enter");
    await page.waitForFunction((header) => {
      const body = header.closest(".dialog-content");
      return body.scrollHeight > body.clientHeight + 80;
    }, await summary.elementHandle());
    await summary.evaluate((header) => {
      const body = header.closest(".dialog-content");
      body.scrollTop += header.getBoundingClientRect().top - body.getBoundingClientRect().top + 80;
    });
    await page.waitForFunction((header) => {
      const title = header.closest("dialog").querySelector(".dialog-header");
      const bounds = title.getBoundingClientRect();
      const x = header.getBoundingClientRect().left + 20;
      const y = bounds.bottom - 4;
      return header.closest(".dialog-content").scrollTop > 0 && title.closest("dialog").scrollTop === 0 &&
        header.getBoundingClientRect().top >= bounds.bottom - 1 && title.contains(document.elementFromPoint(x, y));
    }, await summary.elementHandle());
  }
  assert.equal(await memberTrace.getByText("Search trace details.").count(), 0);
  const memberSubagents = memberTrace.getByRole("button", { name: "Subagents", exact: true });
  assert.equal(await memberSubagents.locator(".symbol").textContent(), "smart_toy");
  assert.equal(await memberSubagents.locator(".subagent-count").textContent(), "1");
  await memberSubagents.click();
  const topDialog = () => page.locator("dialog:modal").last();
  assert.equal(await topDialog().getByRole("button", { name: "Browser", exact: true }).count(), 0);
  await topDialog().getByRole("button", { name: "Search", exact: true }).click();
  const searchTrace = page.getByRole("dialog", { name: "Search", exact: true });
  await searchTrace.getByText("Search trace details.").waitFor();
  assert.equal(await searchTrace.getByText("Browser trace details.").count(), 0);
  const searchChildren = searchTrace.getByRole("button", { name: "Subagents", exact: true });
  assert.equal(await searchChildren.locator(".subagent-count").textContent(), "1");
  await searchChildren.click();
  await topDialog().getByRole("button", { name: "Browser", exact: true }).click();
  const browserTrace = page.getByRole("dialog", { name: "Browser", exact: true });
  await browserTrace.getByText("Browser trace details.").waitFor();
  assert.equal(await browserTrace.getByText("Search trace details.").count(), 0);
  assert.equal(await browserTrace.locator(".subagent-count").count(), 0);
  await browserTrace.getByRole("button", { name: "Back", exact: true }).click();
  await topDialog().getByRole("button", { name: "Back", exact: true }).click();
  await searchTrace.getByRole("button", { name: "Close", exact: true }).click();
  await memberTrace.getByRole("button", { name: "Close", exact: true }).click();
  await page.locator(".toolbar").getByRole("button", { name: "Back", exact: true }).click();
  await page.unroute("**/api/command", stubMemberTrace);
  const greetingPreset = "5039dc7a-ba0c-4fd7-b890-e2016365dd4d";
  await writeFile(join(workspace, "presets", greetingPreset + ".json"), JSON.stringify({
    name: "Greeting fixture", chat_mode: "character",
    chat_agent: { greeting_system_message: "Open with the observatory bell." },
  }));
  const oldRun = "542d614b5890";
  const latestRun = "01c71cd811e2";
  const worker = "3e75da20d5a2";
  const runBlock = (id, run, fields) => ({ id, agent: "PluginAgent", plugin_agent: worker,
    plugin_agent_name: "Fixture worker", plugin_run: run, ...fields });
  const greetingSession = join(workspace, "preset_sessions", greetingPreset);
  await mkdir(greetingSession, { recursive: true });
  await writeFile(join(greetingSession, "blocks.json"), JSON.stringify([
    ...Array.from({ length: 99 }, (_, index) => {
      const suffix = String(index + 1).padStart(12, "0");
      const run = "b0000000-0000-4000-8000-" + suffix;
      return { id: run, agent: "PluginAgent", plugin_agent: "a0000000-0000-4000-8000-" + suffix,
        plugin_agent_name: "Earlier worker " + (index + 1), plugin_run: run, plugin_status: "finished" };
    }),
    runBlock(oldRun, oldRun, { plugin_status: "finished" }),
    ...Array.from({ length: 85 }, (_, index) =>
      runBlock("fixture-step-" + index, oldRun, { content: "Old worker step " + index + "." })),
    runBlock(latestRun, latestRun, { plugin_status: "cancelled" }),
    runBlock("fixture-last", latestRun, { content: "Latest worker output." }),
    runBlock("fixture-browser", latestRun, { speaker: "BrowserAgent", tool: "fixture_browser_tool", result: "Browser result." }),
    { id: "fixture-parent", agent: "PresetAgent", content: "Parent preset output." },
  ]));
  assert.ok((await send("list_presets")).ok());
  await presetPicker.click();
  await sidebar.getByRole("button", { name: "Greeting fixture", exact: true }).click();
  await home.getByRole("heading", { name: "Greeting fixture", exact: true }).waitFor();
  assert.equal(await message.count(), 0);
  await home.getByRole("button", { name: "Greeting fixture", exact: true }).click();
  const mainPreset = page.locator("main .transcript");
  await mainPreset.getByText("Parent preset output.").waitFor();
  assert.equal(await mainPreset.getByText(/Latest worker output|fixture_browser_tool/).count(), 0);
  const presetSubagents = page.locator(".toolbar").getByRole("button", { name: "Subagents", exact: true });
  assert.equal(await presetSubagents.locator(".symbol").textContent(), "smart_toy");
  const presetBadge = presetSubagents.locator(".subagent-count");
  assert.equal(await presetBadge.textContent(), "99+");
  assert.equal(await presetBadge.getAttribute("dir"), "ltr");
  for (const [colorScheme, forcedColors] of [["light", "none"], ["dark", "none"], ["dark", "active"]]) {
    await page.emulateMedia({ colorScheme, forcedColors });
    await page.waitForFunction(({ expected, forced }) => {
      const systemPalette = matchMedia("(forced-colors: active)").matches;
      return forced ? systemPalette : !systemPalette && document.documentElement.dataset.theme === expected;
    }, { expected: colorScheme, forced: forcedColors === "active" });
    const colors = await presetBadge.evaluate((badge, forced) => {
      const reference = document.createElement("span");
      reference.style.forcedColorAdjust = "none";
      reference.style.color = forced ? "ButtonText" : "var(--on-secondary)";
      reference.style.backgroundColor = forced ? "ButtonFace" : "var(--secondary)";
      badge.parentElement.append(reference);
      const expected = getComputedStyle(reference);
      const actual = getComputedStyle(badge);
      const matches = [actual.color === expected.color, actual.backgroundColor === expected.backgroundColor];
      reference.remove();
      return matches;
    }, forcedColors === "active");
    assert.deepEqual(colors, [true, true]);
  }
  await page.emulateMedia({ colorScheme: null, forcedColors: "none" });
  await presetSubagents.click();
  const activity = page.getByRole("dialog", { name: "Subagents", exact: true });
  assert.equal(await activity.getByRole("button", { name: "Browser", exact: true }).count(), 0);
  const workerButton = activity.getByRole("button", { name: "Fixture worker Cancelled", exact: true });
  assert.equal(await workerButton.locator(".symbol").first().textContent(), "smart_toy");
  await workerButton.click();
  const workerTrace = page.getByRole("dialog", { name: "Fixture worker", exact: true });
  await workerTrace.getByText("Latest worker output.", { exact: true }).waitFor();
  const invalidRun = await page.request.get("/api/preset/activity?" + new URLSearchParams({ preset: greetingPreset, run: "ffffffffffff" }));
  assert.equal(invalidRun.status(), 400);
  assert.equal(await workerTrace.getByText("fixture_browser_tool", { exact: true }).count(), 0);
  const workerChildren = workerTrace.getByRole("button", { name: "Subagents", exact: true });
  assert.equal(await workerChildren.locator(".subagent-count").textContent(), "1");
  await workerChildren.click();
  await topDialog().getByRole("button", { name: "Browser", exact: true }).click();
  await browserTrace.getByText("fixture_browser_tool", { exact: true }).waitFor();
  assert.equal(await browserTrace.getByText("Latest worker output.", { exact: true }).count(), 0);
  await browserTrace.getByRole("button", { name: "Back", exact: true }).click();
  await topDialog().getByRole("button", { name: "Back", exact: true }).click();
  await workerTrace.locator("select").selectOption(oldRun);
  await workerTrace.getByText("Old worker step 84.", { exact: true }).waitFor();
  assert.equal(await workerTrace.getByText("Latest worker output.", { exact: true }).count(), 0);
  assert.equal(await workerTrace.locator(".subagent-count").count(), 0);
  const workerTimeline = workerTrace.locator(".transcript");
  await workerTimeline.evaluate((element) => { element.scrollTop = 0; });
  await workerTimeline.getByText("Old worker step 0.", { exact: true }).waitFor({ state: "attached" });
  const [exportedRun] = await Promise.all([
    page.waitForEvent("download"),
    workerTrace.getByRole("button", { name: "Export transcript", exact: true }).click(),
  ]);
  assert.match(exportedRun.suggestedFilename(), /\.jsonl$/i);
  const exportedText = await readFile(await exportedRun.path(), "utf8");
  assert.match(exportedText, /Old worker step 0\./);
  assert.doesNotMatch(exportedText, /Latest worker output/);
  await page.keyboard.press("Escape");
  await workerButton.waitFor();
  await activity.getByRole("button", { name: "Close", exact: true }).click();
  await presetBack.click();
  await home.getByRole("heading", { name: "Greeting fixture", exact: true }).waitFor();
  await home.getByRole("button", { name: "New chat", exact: true }).click();
  await page.locator(".transcript").getByText("The observatory bell rings.", { exact: true }).waitFor();
  await message.waitFor();
  await stop.waitFor({ state: "hidden" });
  const scrolling = page.locator(".transcript");
  const atBottom = (element) => element.scrollHeight - element.clientHeight - element.scrollTop < 3;
  await message.fill("Exercise transcript scrolling.");
  await page.locator(".composer").getByRole("button", { name: "Send", exact: true }).click();
  await scrolling.getByText("Fixture paragraph 45.", { exact: true }).waitFor({ state: "attached" });
  assert.equal(await scrolling.evaluate(atBottom), true);
  extendScrollReply.resolve();
  await scrolling.getByText("Fixture paragraph 65.", { exact: true }).waitFor({ state: "attached" });
  assert.equal(await scrolling.evaluate(atBottom), true);
  await scrolling.hover();
  await page.mouse.wheel(0, -20000);
  await page.waitForFunction(() => document.querySelector(".transcript").scrollTop === 0);
  finishScrollReply.resolve();
  await stop.waitFor({ state: "hidden" });
  assert.equal(await scrolling.evaluate((element) => element.scrollTop), 0);
  await page.locator(".jump").click();
  assert.equal(await scrolling.evaluate(atBottom), true);
  const ruler = page.getByRole("scrollbar", { name: "Scroll chat history" });
  await ruler.waitFor();
  assert.equal(await scrolling.evaluate((element) => getComputedStyle(element).scrollbarWidth), "none");
  assert.equal(await ruler.locator("[data-turn]").count(), 2);
  const rail = await ruler.boundingBox();
  const railX = rail.x + rail.width / 2;
  const firstTick = await ruler.locator("[data-turn]").first().boundingBox();
  const lastTick = await ruler.locator("[data-turn]").last().boundingBox();
  const firstTickY = firstTick.y + firstTick.height / 2;
  const lastTickY = lastTick.y + lastTick.height / 2;
  await page.mouse.move(railX, lastTickY);
  const preview = ruler.locator(".transcript-ruler-preview");
  await preview.getByText("Exercise transcript scrolling.", { exact: true }).waitFor();
  await preview.getByText(/Fixture paragraph/).waitFor();
  assert.equal(await scrolling.evaluate(atBottom), true);
  await page.mouse.click(railX, firstTickY);
  await page.waitForFunction(() => document.querySelector(".transcript-ruler").getAttribute("aria-valuenow") === "1" &&
    document.querySelector(".transcript").dataset.following === "false");
  await ruler.press("Home");
  assert.equal(await scrolling.evaluate((element) => element.scrollTop), 0);
  await ruler.press("PageDown");
  assert.ok(await scrolling.evaluate((element) => element.scrollTop > 0));
  await page.mouse.move(railX, firstTickY);
  await page.mouse.down();
  await page.mouse.move(railX, lastTickY);
  await page.mouse.up();
  await page.waitForFunction(() => document.querySelector(".transcript-ruler").getAttribute("aria-valuenow") === "2");
  await ruler.press("End");
  await page.waitForFunction(atBottom, await scrolling.elementHandle());
  const code = scrolling.locator("pre");
  await code.hover();
  await page.mouse.wheel(590, 0);
  await page.waitForFunction(() => document.querySelector(".transcript pre").scrollLeft > 0);
  assert.equal(await scrolling.evaluate((element) => element.scrollWidth === element.clientWidth), true);
  if (await page.locator(".jump").isVisible()) await page.locator(".jump").click();
  await page.setViewportSize({ width: 420, height: 590 });
  await message.fill(Array.from({ length: 25 }, (_, index) => "Draft line " + index).join("\n"));
  assert.equal(await message.evaluate((element) => element.scrollHeight > element.clientHeight && element.clientHeight <= 160), true);
  await page.waitForFunction(atBottom, await scrolling.elementHandle());
  await message.press("Control+Home");
  await message.hover();
  await page.mouse.wheel(0, 185);
  await page.waitForFunction(() => document.querySelector(".composer textarea").scrollTop > 0);
  await message.fill("");
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth && window.scrollY === 0), true);
  await libraryToggle.click();
  await presetPicker.click();
  const library = sidebar.locator(".sidebar-body");
  await page.setViewportSize({ width: 420, height: 260 });
  await library.hover();
  await page.mouse.wheel(0, 300);
  await page.waitForFunction(() => document.querySelector(".sidebar-body").scrollTop > 0);
  await page.setViewportSize({ width: 1200, height: 800 });
  await page.keyboard.press("Escape");
  await more.click();
  await actions.getByRole("menuitem", { name: "World", exact: true }).click();
  const worldPrompt = "The dock crew has finished unloading.";
  await message.fill(worldPrompt);
  await message.press("Enter");
  await page.locator(".transcript").getByText("The next watch has begun.", { exact: true }).waitFor();
  const branchEnv = page.waitForRequest((request) => request.url().endsWith("/api/command") &&
    request.postDataJSON()?.op === "branch" && request.postDataJSON()?.agent === "env");
  await page.locator(".user-message[data-branch-message]").filter({ hasText: worldPrompt }).click({ button: "right" });
  await editBranch.click();
  assert.equal((await branchEnv).postDataJSON().preset, greetingPreset);
  await page.waitForFunction((text) => document.querySelector(".composer textarea")?.value === text, worldPrompt);
  assert.equal(await page.getByRole("heading", { name: "World", exact: true, level: 1 }).isVisible(), true);
  await presetBack.click();
  assert.ok((await send("home", { preset: greetingPreset })).ok());
  await home.getByRole("button", { name: "Greeting fixture", exact: true }).click();
  const presetPrompt = "Hold this preset edit.";
  await message.fill(presetPrompt);
  await message.press("Enter");
  await page.locator(".transcript").getByText("The preset edit is complete.", { exact: true }).waitFor();
  const branchPreset = page.waitForRequest((request) => request.url().endsWith("/api/command") &&
    request.postDataJSON()?.op === "branch" && request.postDataJSON()?.agent === "preset");
  await page.locator(".user-message[data-branch-message]").filter({ hasText: presetPrompt }).click({ button: "right" });
  await editBranch.click();
  const forkCommand = (await branchPreset).postDataJSON();
  assert.equal(forkCommand.preset, greetingPreset);
  await page.waitForFunction((text) => document.querySelector(".composer textarea")?.value === text, presetPrompt);
  assert.equal(await page.locator(".user-message").filter({ hasText: presetPrompt }).count(), 0);
  const presetFiles = await readdir(join(workspace, "presets"));
  const savedPresets = await Promise.all(presetFiles.filter((file) => file.endsWith(".json"))
    .map(async (file) => JSON.parse((await readFile(join(workspace, "presets", file), "utf8")).replace(/^\uFEFF/, ""))));
  assert.ok(savedPresets.some((config) => config.id !== greetingPreset &&
    config.parent_preset === greetingPreset && config.parent_message === forkCommand.block));
  await presetBack.click();
  const rulerChat = "01544d56-4e8a-47ba-8661-7456d056f063";
  const rulerCount = 104;
  const rulerDirectory = join(workspace, "conversations", greetingPreset);
  await mkdir(rulerDirectory, { recursive: true });
  const rulerBlocks = Array.from({ length: rulerCount }, (_, index) => [
    { id: "prompt-" + index, kind: "user", agent: "chat", content: "Ruler question " + (index + 1) + "." },
    { id: "answer-" + index, agent: "ChatAgent",
      content: index === rulerCount - 1 ? mathReply : "Ruler answer " + (index + 1) + "." },
  ]).flat();
  await writeFile(join(rulerDirectory, rulerChat + ".json"), JSON.stringify({
    id: rulerChat, preset: greetingPreset, name: "Paged ruler", updated_at: new Date().toISOString(),
    messages: rulerBlocks.map((block) => ({ role: block.kind === "user" ? "user" : "assistant", content: block.content })),
    blocks: rulerBlocks,
  }));
  assert.ok((await send("select_chat", { preset: greetingPreset, id: rulerChat })).ok());
  await page.getByRole("heading", { name: "Paged ruler", level: 1 }).waitFor();
  await ruler.waitFor();
  await page.waitForFunction((count) => Number(document.querySelector(".transcript-ruler")?.getAttribute("aria-valuemax")) === count, rulerCount);
  const firstQuestion = scrolling.getByText("Ruler question 1.", { exact: true });
  assert.equal(await firstQuestion.count(), 0);
  assert.equal(await ruler.locator("[data-turn]").last().evaluate((tick) =>
    getComputedStyle(tick).transitionTimingFunction), "ease-in-out, ease-in-out, ease-in-out");
  await ruler.press("Home");
  await firstQuestion.waitFor();
  await page.waitForFunction(() => document.querySelector(".transcript-ruler").getAttribute("aria-valuenow") === "1" &&
    document.querySelector(".transcript").scrollTop === 0 &&
    document.querySelectorAll(".transcript-ruler [data-active=true]").length > 1);
  await ruler.press("End");
  await page.waitForFunction((count) => Number(document.querySelector(".transcript-ruler")?.getAttribute("aria-valuenow")) === count, rulerCount);
  const math = page.locator(".assistant-reply").filter({ hasText: "Math fixture follows." });
  await math.locator(".katex").last().waitFor({ state: "attached" });
  assert.equal(await math.locator(".katex").count(), 5);
  assert.equal(await math.locator(".katex-display").count(), 3);
  assert.equal(await math.locator("math").count(), 5);
  assert.equal(await math.locator("code").getByText("$x^2$", { exact: true }).count(), 1);
  assert.match(await math.innerText(), /Prices are \$5 and \$10/);
  const longReplyChat = "ecb5cbb7-5779-4d57-9d3b-daf3af6f6f3a";
  const olderPrompt = "This request is on an older transcript page.";
  await writeFile(join(rulerDirectory, longReplyChat + ".json"), JSON.stringify({
    id: longReplyChat, preset: greetingPreset, name: "Older prompt", title_source: "manual",
    messages: [{ role: "user", content: olderPrompt }, { role: "assistant", content: "Fixture reply" }],
    blocks: [{ id: "older-prompt", kind: "user", agent: "chat", content: olderPrompt },
      ...Array.from({ length: 100 }, (_, index) => ({ id: "reply-" + index, agent: "ChatAgent", content: "Reply " + index }))],
  }));
  assert.ok((await send("select_chat", { preset: greetingPreset, id: longReplyChat })).ok());
  await page.getByRole("heading", { name: "Older prompt", level: 1 }).waitFor();
  assert.equal(await scrolling.getByText(olderPrompt, { exact: true }).count(), 0);
  await page.locator(".toolbar-rename").click();
  await rename.getByRole("button", { name: "Generate title" }).click();
  await page.getByRole("heading", { name: "Fixture chat", level: 1 }).waitFor();
  assert.deepEqual(modelFailures, []);
  assert.deepEqual(errors, []);
  console.log("Desktop WebUI verified, including title generation, long sidebar titles, native pickers, RTL, streaming and paged transcripts, LaTeX and the expanding composer.");
} finally {
  createPresetRequest.resolve();
  presetInterview.resolve();
  presetEdit.resolve();
  extendScrollReply.resolve();
  finishScrollReply.resolve();
  for (const gate of Object.values(readiness)) gate.resolve();
  await browser?.close();
  child.kill();
  await exit.catch(() => {});
  service.closeAllConnections();
  service.close();
  await rm(workspace, { recursive: true, force: true, maxRetries: 3 }).catch(() => {
    console.error("Could not remove the isolated desktop fixture.");
  });
}
