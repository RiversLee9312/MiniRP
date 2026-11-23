import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { chromium } from "playwright";

const program = resolve(process.argv[2] ?? ("artifacts/desktop/" + (process.platform === "win32" ? "win-x64/minirp.exe" : "linux-x64/minirp")));
const workspace = await mkdtemp(join(tmpdir(), "minirp-desktop-qa-"));
const reply = "A reply from the local test model.";
const createPresetRequest = Promise.withResolvers();
const presetInterview = Promise.withResolvers();
const presetEdit = Promise.withResolvers();
const extendScrollReply = Promise.withResolvers();
const finishScrollReply = Promise.withResolvers();
const readiness = { Ada: Promise.withResolvers(), Bryn: Promise.withResolvers() };
const readinessCalls = [];
const modelFailures = [];
const service = createServer(async (request, response) => {
  if (request.url.endsWith("/models")) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ data: [{ id: "fixture-model" }] }));
    return;
  }
  if (!request.url.endsWith("/chat/completions")) {
    response.writeHead(404).end();
    return;
  }
  let body = "";
  for await (const chunk of request) body += chunk;
  const completion = JSON.parse(body);
  if (!completion.stream) {
    response.writeHead(200, { "content-type": "application/json" });
    response.end(JSON.stringify({ choices: [{ message: { content: "Fixture chat" }, finish_reason: "stop" }] }));
    return;
  }
  response.writeHead(200, { "content-type": "text/event-stream" });
  const chunk = (delta, finish_reason = null) => response.write("data: " + JSON.stringify({ choices: [{ index: 0, delta, finish_reason }] }) + "\n\n");
  const user = completion.messages.findLast((item) => item.role === "user");
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
  const ensemble = [...prompts.matchAll(/The active chat is [^()]+ \(([0-9a-f-]+)\)/g)].at(-1);
  if (ensemble) {
    if (tools.includes("room_progress") || prompts.includes("fixture_room_progress"))
      modelFailures.push("Room loading state leaked into a chat.");
    if (completion.messages.at(-1)?.role !== "tool") {
      const name = prompts.includes("Your character is Ada") ? "Ada" : "Bryn";
      call("chat_send", { chat: ensemble[1], text: name + " posted to the bridge." });
      return;
    }
    chunk({ content: "Private fixture response." }, "stop");
    response.end("data: [DONE]\n\n");
    return;
  }
  if (JSON.stringify(user?.content || "").includes("Cancel this fixture turn.")) {
    chunk({ content: "Waiting for cancellation." });
    return;
  }
  if (JSON.stringify(user?.content || "").includes("Show a fixture card.")) {
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
  assert.equal(await page.evaluate(async () => {
    const font = '20px "Material Symbols Rounded"';
    await document.fonts.load(font, "add cloud info");
    const context = document.createElement("canvas").getContext("2d");
    context.font = font;
    return ["add", "cloud", "info"].every((icon) => Math.abs(context.measureText(icon).width - 20) < 1);
  }), true);
  assert.equal(await preferences.getByLabel("Language").count(), 0);
  await preferences.getByRole("button", { name: "Appearance and behavior" }).click();
  const behavior = page.getByRole("dialog", { name: "Appearance and behavior", exact: true });
  const themeChoice = behavior.getByRole("combobox", { name: "Appearance", exact: true });
  await themeChoice.waitFor();
  assert.deepEqual(await themeChoice.locator("option").evaluateAll((items) => items.map((node) => node.value)),
    ["system_default", "light", "dark"]);
  if (await page.evaluate(() => CSS.supports("appearance", "base-select"))) {
    await themeChoice.click();
    assert.equal(await themeChoice.evaluate((control) => control.matches(":open")), true);
    const picker = await themeChoice.evaluate((control) => ({
      arrow: getComputedStyle(control, "::picker-icon").content,
      padding: getComputedStyle(control).paddingInlineEnd,
      radius: getComputedStyle(control, "::picker(select)").borderTopLeftRadius,
      selected: getComputedStyle(control.selectedOptions[0]).backgroundColor,
    }));
    assert.deepEqual([picker.arrow, picker.padding], ['"expand_more"', "10px"]);
    assert.ok(parseFloat(picker.radius) > 0);
    assert.notEqual(picker.selected, "rgba(0, 0, 0, 0)");
    await page.keyboard.press("Escape");
    await behavior.waitFor();
  }
  assert.equal(await behavior.getByRole("button", { name: "Background replies" }).count(), 0);
  await behavior.getByRole("button", { name: "Language", exact: true }).click();
  const language = page.getByRole("dialog", { name: "Language", exact: true }).getByRole("combobox", { name: "Language", exact: true });
  await language.waitFor();
  assert.ok(await language.locator("option").count() >= 26);
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
    return header.paddingInlineStart === body.paddingInlineStart && header.paddingInlineEnd === body.paddingInlineEnd;
  }), true);
  await page.locator("dialog select").first().selectOption("en");
  await page.locator("html[lang=en][dir=ltr]").waitFor({ state: "attached" });
  await page.keyboard.press("Escape");
  await behavior.waitFor();
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
  assert.equal(await models.getByRole("combobox", { name: "Browser", exact: true }).isVisible(), false);
  await models.getByText("Agent models", { exact: true }).click();
  await models.getByRole("combobox", { name: "Browser", exact: true }).waitFor();
  await models.getByRole("button", { name: "Back", exact: true }).click();
  await modelGroup.getByRole("button", { name: "Providers", exact: true }).click();
  const providers = page.getByRole("dialog", { name: "Providers" });
  await providers.getByRole("button", { name: "Add provider" }).click();
  const provider = page.getByRole("dialog", { name: "Add provider" });
  assert.equal(await provider.getByRole("button", { name: "Fetch models" }).isVisible(), false);
  await provider.getByLabel("Name", { exact: true }).fill("Fixture provider");
  await provider.locator("input[type=url]").fill("http://127.0.0.1:" + service.address().port);
  await provider.getByText("Manage models", { exact: true }).click();
  await provider.getByRole("button", { name: "Fetch models" }).click();
  await provider.getByText("fixture-model", { exact: true }).waitFor();
  await provider.getByRole("button", { name: "Save", exact: true }).click();
  await providers.getByRole("button", { name: /^Fixture provider/ }).waitFor();
  await providers.getByRole("button", { name: "Back", exact: true }).click();
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
  await licenses.locator(".dialog-content").hover();
  await page.mouse.wheel(0, 350);
  await page.waitForFunction(() => [...document.querySelectorAll("dialog:modal .dialog-content")].some((element) => element.scrollTop > 0));
  await licenses.getByRole("button", { name: "Back", exact: true }).waitFor();
  await licenses.getByRole("button", { name: "Back", exact: true }).click();
  await about.getByRole("button", { name: "Back", exact: true }).click();
  await preferences.getByRole("button", { name: "Close", exact: true }).click();
  const libraryToggle = page.locator(".toolbar button[aria-controls=library]");
  await libraryToggle.click();
  await page.locator(".sidebar.visible").waitFor();
  assert.equal(await page.locator(".drawer-close").evaluate((element) => element === document.activeElement), true);
  await page.keyboard.press("Escape");
  await page.locator(".sidebar").waitFor({ state: "hidden" });
  assert.equal(await libraryToggle.evaluate((element) => element === document.activeElement), true);
  await page.emulateMedia({ reducedMotion: "reduce" });
  const home = page.locator(".preset-home");
  assert.equal(await home.evaluate((element) => getComputedStyle(element).animationName), "none");
  assert.equal(await page.locator("main .composer").count(), 0);
  assert.equal(await page.locator("main").getByRole("button", { name: "Choose preset", exact: true }).count(), 0);
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await page.setViewportSize({ width: 1200, height: 800 });

  const send = (op, args = {}) => page.request.post("/api/command", { data: { ...args, op } });
  const createButton = home.getByRole("button", { name: "Create preset", exact: true });
  await createButton.click();
  const createDialog = page.getByRole("dialog", { name: "Create preset", exact: true });
  await createDialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await page.route("**/api/command", async (route) => {
    if (route.request().postDataJSON()?.op === "create_preset") await createPresetRequest.promise;
    await route.continue();
  });
  await page.setViewportSize({ width: 400, height: 730 });
  await libraryToggle.click();
  await page.locator(".sidebar.visible").getByRole("button", { name: "Create preset", exact: true }).click();
  await createDialog.getByRole("textbox", { name: "Preset name" }).fill("Fixture preset");
  await createDialog.getByRole("button", { name: "Create preset", exact: true }).click();
  await createDialog.getByRole("button", { name: "Working", exact: true }).waitFor();
  assert.equal(await createDialog.getByRole("button", { name: "Close", exact: true }).isEnabled(), false);
  await page.keyboard.press("Escape");
  assert.equal(await createDialog.isVisible(), true);
  createPresetRequest.resolve();
  await page.getByRole("heading", { name: "Edit preset" }).waitFor();
  await page.locator(".sidebar").waitFor({ state: "hidden" });
  await page.unroute("**/api/command");
  await page.setViewportSize({ width: 1200, height: 800 });
  const message = page.getByRole("textbox", { name: "Message", exact: true });
  const presetBack = page.locator(".toolbar-title").getByRole("button", { name: "Back", exact: true });
  const presetStatus = page.locator(".preset-working");
  const stop = page.getByRole("button", { name: "Stop", exact: true });
  await presetStatus.waitFor();
  await stop.waitFor();
  assert.equal(await presetBack.isEnabled(), false);
  assert.equal(await page.locator("#preset-select").isEnabled(), false);
  await page.reload();
  await presetStatus.waitFor();
  assert.equal(await presetBack.isEnabled(), false);
  presetInterview.resolve();
  await page.locator(".transcript").getByText("Choose the experience for this preset.", { exact: true }).waitFor();
  await presetStatus.waitFor({ state: "hidden" });
  assert.equal(await presetBack.isEnabled(), false);
  assert.equal(await page.locator(".library-footer .create-preset-fab").isEnabled(), false);
  await message.fill("Confirm this fixture preset.");
  await message.press("Enter");
  await page.locator(".transcript").getByText("The fixture preset is ready.", { exact: true }).waitFor();
  await presetBack.click();
  const homeTitle = home.getByRole("button", { name: "Fixture preset", exact: true });
  await homeTitle.click();
  await page.getByRole("heading", { name: "Edit preset" }).waitFor();
  await message.fill("Hold this preset edit.");
  await message.press("Enter");
  await presetStatus.waitFor();
  assert.equal(await presetBack.isEnabled(), false);
  assert.equal(await page.locator("#preset-select").isEnabled(), false);
  presetEdit.resolve();
  await page.locator(".transcript").getByText("The preset edit is complete.", { exact: true }).waitFor();
  await presetBack.click();
  await homeTitle.waitFor();
  const staged = page.waitForResponse((response) => new URL(response.url()).pathname === "/api/attachments");
  await page.locator(".composer input[type=file]").setInputFiles({
    name: "fixture.txt", mimeType: "text/plain", buffer: Buffer.from("Fixture attachment"),
  });
  const stagedResponse = await staged;
  assert.ok(stagedResponse.ok(), await stagedResponse.text());
  await page.locator(".composer .attachment").filter({ hasText: "fixture.txt" }).waitFor();
  await message.fill("Hello from the desktop test.");
  await message.press("Enter");
  const transcriptReply = page.locator(".transcript").getByText(reply, { exact: true });
  await transcriptReply.waitFor();
  await page.locator(".composer").getByRole("button", { name: "Send", exact: true }).waitFor();
  await message.fill("Show a fixture card.");
  await page.locator(".composer").getByRole("button", { name: "Send", exact: true }).click();
  await page.frameLocator("iframe.open-ui").getByText("Fixture card", { exact: true }).waitFor();
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
  await page.locator(".toolbar-rename").click();
  const rename = page.getByRole("dialog", { name: "Rename", exact: true });
  await rename.getByRole("button", { name: "Generate title" }).waitFor();
  await rename.getByRole("button", { name: "Cancel" }).click();
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
  await actions.getByRole("menuitem", { name: "Export transcript (JSONL)" }).click();
  const file = await downloaded;
  assert.match(file.suggestedFilename(), /\.jsonl$/);
  const lines = (await readFile(await file.path(), "utf8")).trim().split(/\r?\n/).map(JSON.parse);
  assert.ok(lines.some((record) => record.content?.includes(reply)));
  assert.ok(lines.some((record) => record.attachments?.some((file) => file.name === "fixture.txt")));
  await page.reload();
  await transcriptReply.waitFor();
  await message.fill("Cancel this fixture turn.");
  await message.press("Enter");
  await page.locator(".transcript").getByText("Waiting for cancellation.", { exact: true }).waitFor();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await preferences.getByRole("button", { name: "Appearance and behavior" }).click();
  await themeChoice.waitFor();
  await behavior.getByRole("button", { name: "Close", exact: true }).click();
  await stop.click();
  await stop.waitFor({ state: "hidden" });
  await page.locator(".user-message").filter({ hasText: "Hello from the desktop test." })
    .getByRole("button", { name: "Edit in a branch" }).click();
  await page.waitForFunction(() => document.querySelector(".composer textarea").value === "Hello from the desktop test.");
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
  const bridge = page.getByRole("button", { name: "Bridge", exact: true });
  await page.waitForFunction(() => document.querySelector(".loading-text[data-typing=true]")?.textContent.length > 0);
  await loading.locator(".loading-text[data-typing=false]").filter({ hasText: "Navigation lights flicker beyond the glass." }).waitFor();
  await loading.locator("progress[value=\"0.5\"]").waitFor();
  assert.equal(await bridge.isVisible(), false);
  await loading.locator(".loading-text[data-typing=false]").filter({ hasText: "A watch schedule hangs beside the cargo door." }).waitFor();
  assert.equal((await loading.innerText()).includes("0/2"), false);
  await page.waitForFunction(() => parseFloat(document.querySelector(".loading-progress")?.style.getPropertyValue("--progress")) > 20);
  readiness.Ada.resolve();
  await loading.locator("progress[value=\"0.8\"]").waitFor();
  assert.equal((await loading.innerText()).includes("1/2"), false);
  assert.equal(await bridge.isVisible(), false);
  readiness.Bryn.resolve();
  await loading.waitFor({ state: "hidden" });
  assert.deepEqual(readinessCalls.slice(0, 2).sort(), ["Ada progress", "Bryn progress"]);
  assert.deepEqual(readinessCalls.slice(2).sort(), ["Ada ready", "Bryn ready"]);
  await bridge.click();
  await message.fill("Report to the bridge.");
  await message.press("Enter");
  await page.locator(".transcript").getByText("Ada posted to the bridge.", { exact: true }).waitFor();
  await page.locator(".transcript").getByText("Bryn posted to the bridge.", { exact: true }).waitFor();
  await page.getByRole("button", { name: "Back", exact: true }).click();
  const lab = page.getByRole("button", { name: "Lab", exact: true });
  await lab.waitFor();
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
  assert.ok(frames.every((frame) => frame.radius >= 15 && frame.transform === "none"));
  assert.ok(frames.some((frame) => frame.radius < resting));
  await message.waitFor();
  assert.equal(await page.locator(".transcript").getByText("Ada posted to the bridge.", { exact: true }).count(), 0);
  const greetingPreset = "5039dc7a-ba0c-4fd7-b890-e2016365dd4d";
  await writeFile(join(workspace, "presets", greetingPreset + ".json"), JSON.stringify({
    name: "Greeting fixture", chat_mode: "character",
    chat_agent: { greeting_system_message: "Open with the observatory bell." },
  }));
  assert.ok((await send("list_presets")).ok());
  await presetPicker.click();
  await sidebar.getByRole("button", { name: "Greeting fixture", exact: true }).click();
  await home.getByRole("heading", { name: "Greeting fixture", exact: true }).waitFor();
  assert.equal(await message.count(), 0);
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
  assert.deepEqual(modelFailures, []);
  assert.deepEqual(errors, []);
  console.log("Desktop WebUI verified, including ensemble preparation, native pickers, RTL, scrollable settings and library, streaming transcripts, horizontal code scrolling and the expanding composer.");
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
