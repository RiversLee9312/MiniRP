import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";
import { chromium } from "playwright";

const web = new URL("../apps/desktop-web/", import.meta.url);
const resolveWeb = createRequire(new URL("package.json", web));
const { createServer } = await import(pathToFileURL(resolveWeb.resolve("vite")));
const preset = "eaf5583b-7ae4-4c66-b72a-04b3f411969c";
const room = "ad2b4f9b-b70b-48ab-8fca-42c4d76a7db5";
const shortRoom = "7376f472-16f4-4d16-9941-fbd75dbdf029";
const fitRoom = "04cfb96d-2a24-431e-9faf-65810339fad2";
const pageSize = 80;
const newest = 960;
const firstRecent = newest - pageSize + 1;
const rooms = Array.from({ length: 70 }, (_, index) => ({ id: `fixture-${index}`, name: `Room ${index + 1}` }));
rooms.splice(0, 3, { id: room, name: "Fixture room" }, { id: shortRoom, name: "Short room" }, { id: fitRoom, name: "Fitting room" });
const range = (first, last) => Array.from({ length: last - first + 1 }, (_, index) => {
  const number = first + index;
  return {
    id: `message-${number}`, agent: "ChatAgent", content: `Fixture message ${number} selected word.`,
    ...(number === firstRecent + 2 ? { open_ui: "fixture", open_ui_title: "Fixture open form" } : {}),
    ...(number === firstRecent + 5 ? { tool: "fixture_tool", reasoning: "Trace detail.\n".repeat(75) } : {}),
    ...(number % 8 === 0 ? { kind: "user", version: "fixture-version" } : {}),
    ...(number % 8 === 7 ? { kind: "reply_end", first_block: `message-${number - 7}`, reply: `Complete answer for turn ${number - 7}.` } : {}),
    ...(number === 34 ? { kind: "user", interjection: true, content: "Please include this detail." } : {}),
    ...(number === 105 ? { content: "", open_ui: "fixture", open_ui_title: "Older card, no text message" } : {}),
    ...(number === 210 ? { content: "Long historical message.\n\n".repeat(18) } : {}),
  };
});
const fitting = [{ id: "fit-user", kind: "user", content: "An ordinary user message." },
  { id: "fit-answer", content: "A short answer." }];
const short = [
  { id: "short-user", kind: "user", content: "First question." },
  { id: "short-answer", content: "First answer.\n\n" + "This paragraph gives the conversation some height.\n\n".repeat(19) },
  { id: "short-tool", tool: "configure_date", reasoning: "A trace within the first turn.\n".repeat(75) },
  { id: "short-end", kind: "reply_end", first_block: "short-user", reply: "First completed answer." },
  { id: "short-next", kind: "user", content: "Second question." },
  { id: "short-reply", content: "Second answer." },
];
const snapshot = (id, name, blocks, before = null) => ({ kind: "loaded", preset, config: { name: "Fixture preset" },
  chat: { id, name, blocks }, ensemble_chats: null, history: { chat: before, env: null, preset: null } });

const clients = new Set();
const historyQueries = [];
const firstPage = Promise.withResolvers();
let displayLocale = "en";
const initial = [
  { kind: "ready", preset, config: { name: "Fixture preset" } },
  snapshot(room, "Fixture room", range(firstRecent, newest), "older-1"),
  { kind: "presets", items: [{ id: preset, name: "Fixture preset" }] },
  { kind: "chats", preset, items: rooms },
];
const publish = (events) => {
  for (const client of clients) client.write(`data: ${JSON.stringify(events)}\n\n`);
};
const json = (res, value) => {
  res.writeHead(200, { "Content-Type": "application/json" });
  res.end(JSON.stringify(value));
};

const vite = await createServer({
  root: fileURLToPath(web), server: { host: "127.0.0.1", port: 0, ws: false },
  plugins: [{
    name: "transcript-smoke-fixture",
    configureServer(server) {
      server.middlewares.use(async (req, res, next) => {
        const url = new URL(req.url, "http://localhost");
        if (url.pathname === "/api/events") {
          res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" });
          clients.add(res);
          res.on("close", () => clients.delete(res));
          res.write(`data: ${JSON.stringify(initial)}\n\n`);
        } else if (url.pathname === "/api/transcript") {
          const query = Object.fromEntries(url.searchParams);
          historyQueries.push(query);
          const number = Number(query.before?.match(/^older-(\d+)$/)?.[1]);
          if (number === 1) await firstPage.promise;
          const last = newest - pageSize * number;
          if (last >= pageSize && number >= 1)
            json(res, { blocks: range(last - pageSize + 1, last), before: last > pageSize ? `older-${number + 1}` : null });
          else json(res, { blocks: [], before: null });
        } else if (url.pathname === "/api/settings") {
          json(res, { preferences: { locale: displayLocale, theme: "light" }, providers: [], models: [], agents: {} });
        } else if (url.pathname === "/api/command") {
          let body = "";
          for await (const part of req) body += part;
          const command = JSON.parse(body);
          if (command.op === "select_chat") {
            if (command.id === shortRoom) publish([snapshot(shortRoom, "Short room", short)]);
            if (command.id === fitRoom) publish([snapshot(fitRoom, "Fitting room", fitting)]);
            if (command.id === room) publish([initial[1]]);
          }
          json(res, { ok: true });
        } else if (url.pathname === "/openui/index.html") {
          res.writeHead(200, { "Content-Type": "text/html" });
          res.end('<!doctype html><label>Card value <input aria-label="Card value"></label>');
        } else next();
      });
    },
  }],
});

let browser;
try {
  await vite.listen();
  browser = await chromium.launch(process.env.MINIRP_TEST_BROWSER ? { channel: process.env.MINIRP_TEST_BROWSER } : {});
  const url = vite.resolvedUrls.local[0];
  const page = await browser.newPage({ viewport: { width: 1250, height: 740 }, reducedMotion: "reduce" });
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto(url);
  const transcript = page.locator(".transcript");
  const row = (number) => transcript.locator(`[data-transcript-row=message-${number}]`);
  await row(newest).waitFor({ state: "attached" });
  await page.evaluate(() => document.fonts.ready);
  assert.equal(await transcript.locator("[data-transcript-row]").count(), pageSize);
  const end = (element) => element.scrollHeight - element.clientHeight - element.scrollTop < 3;
  assert.equal(await transcript.evaluate(end), true);
  assert.equal(await row(firstRecent).evaluate((element) => getComputedStyle(element.parentElement).contentVisibility), "auto");
  const ruler = page.getByRole("scrollbar", { name: "Scroll chat history" });
  await ruler.waitFor();
  assert.equal(await transcript.evaluate((element) => getComputedStyle(element).scrollbarWidth), "none");
  assert.equal(await ruler.getAttribute("aria-controls"), await transcript.getAttribute("id"));

  await transcript.evaluate((element) => { element.scrollTop = 95; });
  await page.waitForFunction(() => document.querySelector(".transcript").dataset.following === "false");
  const card = row(firstRecent + 2);
  await card.waitFor();
  const input = card.frameLocator("iframe").getByRole("textbox", { name: "Card value" });
  await input.fill("Keep current card input");
  await transcript.evaluate((element) => {
    element.scrollTop = 112;
    const top = element.getBoundingClientRect().top;
    const anchor = [...element.querySelectorAll("[data-transcript-row]")].find((item) => item.getBoundingClientRect().bottom > top);
    window.transcriptAnchor = { node: anchor, offset: anchor.getBoundingClientRect().top - top };
    const paragraph = element.querySelector("[data-transcript-row=message-882] .assistant-reply p");
    const text = paragraph.firstChild;
    const range = document.createRange();
    const start = text.textContent.indexOf("selected");
    range.setStart(text, start);
    range.setEnd(text, start + "selected".length);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
  });
  assert.equal(await page.evaluate(() => getSelection().toString()), "selected");
  assert.equal(await row(882).locator(".assistant-reply p").evaluate((paragraph) => {
    const sample = document.createElement("span");
    sample.style.backgroundColor = "var(--primary)";
    sample.style.color = "var(--on-primary)";
    document.body.append(sample);
    const accent = getComputedStyle(sample);
    const selection = getComputedStyle(paragraph, "::selection");
    const matches = selection.backgroundColor === accent.backgroundColor && selection.color === accent.color;
    sample.remove();
    return matches;
  }), true);
  firstPage.resolve();
  await row(firstRecent - pageSize).waitFor({ state: "attached" });
  assert.deepEqual(historyQueries, [{ preset, room, agent: "chat", conversation: "", before: "older-1", locale: "en" }]);
  assert.equal(await transcript.locator("[data-transcript-row]").count(), pageSize * 2);
  await page.waitForFunction(() => {
    const container = document.querySelector(".transcript");
    const { node, offset } = window.transcriptAnchor;
    return container.contains(node) && Math.abs(node.getBoundingClientRect().top - container.getBoundingClientRect().top - offset) < 3;
  });
  assert.equal(await page.evaluate(() => getSelection().toString()), "selected");
  assert.equal(await input.inputValue(), "Keep current card input");
  await row(882).locator(".assistant-reply p").dispatchEvent("contextmenu", { bubbles: true, cancelable: true });
  await page.getByRole("menuitem", { name: "Quote", exact: true }).click();
  const quote = page.locator(".composer .quote-attachment");
  await quote.getByRole("button", { name: "Remove Quote" }).waitFor();
  assert.equal(await quote.locator(".quote-preview").textContent(), "selected");
  assert.equal(await page.locator(".composer textarea").inputValue(), "");

  const trace = row(firstRecent + 5);
  await trace.locator("summary").click();
  await trace.evaluate((element) => {
    const scroller = element.closest(".transcript");
    scroller.scrollTop += element.getBoundingClientRect().top - scroller.getBoundingClientRect().top + 120;
  });
  assert.equal(await trace.locator("summary").evaluate((summary) =>
    Math.abs(summary.getBoundingClientRect().top - summary.closest(".transcript").getBoundingClientRect().top) < 2), true);
  publish([{ kind: "delta", agent: "ChatAgent", block: "stream", text: "Live outside the reader's view." }]);
  await transcript.getByText("Live outside the reader's view.", { exact: true }).waitFor({ state: "attached" });
  assert.equal(await trace.locator("details").evaluate((details) => details.open), true);
  assert.equal(await transcript.evaluate(end), false);
  assert.equal(await page.getByRole("heading", { name: "Fixture room" }).evaluate((heading) => heading.getBoundingClientRect().top >= 0), true);
  await page.locator(".jump").click();
  assert.equal(await transcript.evaluate(end), true);
  publish([{ kind: "delta", agent: "ChatAgent", block: "stream", text: "\n\n" + "Long streamed paragraph.\n\n".repeat(35) }]);
  await transcript.getByText("Long streamed paragraph.", { exact: true }).first().waitFor({ state: "attached" });
  await page.waitForFunction(end, await transcript.elementHandle());

  for (let number = 2; number < newest / pageSize; number++) {
    await ruler.press("Home");
    await row(firstRecent - pageSize * number).waitFor({ state: "attached" });
  }
  assert.deepEqual(historyQueries.map((item) => item.before), Array.from({ length: newest / pageSize - 1 }, (_, index) => `older-${index + 1}`));
  assert.equal(await transcript.locator(".transcript-load-older").count(), 0);
  assert.equal(await transcript.locator("[data-transcript-row]").count(), newest + 1);

  assert.equal(await ruler.getAttribute("aria-valuemax"), "122");
  const railViewport = ruler.locator(".transcript-ruler-viewport");
  const rail = await railViewport.boundingBox();
  const xRail = rail.x + rail.width / 2;
  const tick = (id) => ruler.locator(`[data-turn=${id}]`);
  const yTick = async (id) => {
    const target = await tick(id).boundingBox();
    assert.ok(target.y >= rail.y - 1 && target.y < rail.y + rail.height);
    return target.y + target.height / 2;
  };
  const noRailOutline = async () => assert.equal(await ruler.evaluate((element) => {
    const style = getComputedStyle(element);
    return style.outlineStyle === "none" || style.outlineWidth === "0px";
  }), true);
  const preview = ruler.locator(".transcript-ruler-card");
  const fade = async (above, below) => {
    await page.waitForFunction(([above, below]) => {
      const ruler = document.querySelector(".transcript-ruler");
      return ruler.dataset.moreAbove === above && ruler.dataset.moreBelow === below
        && (getComputedStyle(ruler, "::before").content !== "none") === (above === "true")
        && (getComputedStyle(ruler, "::after").content !== "none") === (below === "true");
    }, [String(above), String(below)]);
  };
  const viewportStart = await railViewport.evaluate((element) => {
    element.scrollTop = 0;
    return element.scrollHeight - element.clientHeight;
  });
  assert.ok(viewportStart > 900);
  await fade(false, true);
  const previewOld = async (number, entries) => {
    const previous = await transcript.evaluate((element) => element.scrollTop);
    await page.mouse.move(xRail, await yTick(`message-${number}`));
    for (const [label, content] of entries) {
      await preview.locator("strong").getByText(label, { exact: true }).waitFor();
      await preview.locator("span").getByText(content, { exact: true }).waitFor();
    }
    assert.equal(await transcript.evaluate((element) => element.scrollTop), previous);
  };
  await previewOld(32, [["User message", "Fixture message 32 selected word."], ["Assistant reply", "Complete answer for turn 32."]]);
  await previewOld(34, [["User message", "Please include this detail."]]);
  await preview.locator(".transcript-ruler-interjection").getByText("Interjection", { exact: true }).waitFor();
  assert.equal(await preview.getByText("Assistant reply", { exact: true }).count(), 0);
  await previewOld(104, [["User message", "Fixture message 104 selected word."], ["Assistant reply", "Complete answer for turn 104."]]);
  assert.equal(await tick("message-105").count(), 0);
  await page.mouse.click(xRail, await yTick("message-32"));
  await tick("message-32").and(ruler.locator('[data-current="true"]')).waitFor();
  assert.equal(await transcript.evaluate(end), false);
  await noRailOutline();
  await page.mouse.move(xRail, await yTick("message-32"));
  await page.mouse.down();
  await page.mouse.move(xRail, await yTick("message-104"), { steps: 8 });
  await page.mouse.up();
  await tick("message-104").and(ruler.locator('[data-current="true"]')).waitFor();
  await noRailOutline();
  await page.mouse.move(rail.x + rail.width + 40, rail.y + 100);
  await preview.waitFor({ state: "hidden" });
  assert.equal(await ruler.locator('[data-hover="true"]').count(), 0);
  const transcriptBeforeRailWheel = await transcript.evaluate((element) => element.scrollTop);
  await page.mouse.move(xRail, rail.y + rail.height * .55);
  await page.mouse.wheel(0, 560);
  await fade(true, true);
  assert.equal(await transcript.evaluate((element) => element.scrollTop), transcriptBeforeRailWheel);
  await page.emulateMedia({ forcedColors: "active" });
  assert.equal(await ruler.evaluate((element) =>
    [getComputedStyle(element, "::before"), getComputedStyle(element, "::after")].every((style) =>
      style.forcedColorAdjust === "none" && style.backgroundImage.startsWith("linear-gradient("))), true);
  await page.emulateMedia({ forcedColors: "none" });
  const scrollbar = await railViewport.evaluate((element) => ({
    width: element.offsetWidth - element.clientWidth,
    thumbCenter: (element.scrollTop + element.clientHeight / 2) * element.clientHeight / element.scrollHeight,
    scrollTop: element.scrollTop,
  }));
  if (scrollbar.width > 0) {
    const thumbX = rail.x + rail.width - scrollbar.width / 2;
    const thumbY = rail.y + scrollbar.thumbCenter;
    await page.mouse.move(thumbX, thumbY);
    await page.mouse.down();
    await page.mouse.move(thumbX, thumbY + 45, { steps: 6 });
    await page.mouse.up();
    await page.waitForFunction((top) => document.querySelector(".transcript-ruler-viewport").scrollTop > top, scrollbar.scrollTop);
    assert.equal(await transcript.evaluate((element) => element.scrollTop), transcriptBeforeRailWheel);
  }
  await page.mouse.move(xRail, rail.y + rail.height * .55);
  await page.mouse.wheel(0, 2400);
  await fade(true, false);
  assert.ok(await tick("message-960").isVisible());
  const beforePageUp = await transcript.evaluate((element) => element.scrollTop);
  await ruler.press("PageUp");
  assert.ok(await transcript.evaluate((element) => element.scrollTop) < beforePageUp);
  await ruler.press("End");
  await page.waitForFunction(end, await transcript.elementHandle());
  await tick("message-960").and(ruler.locator('[data-current="true"]')).waitFor();
  await ruler.press("ArrowUp");
  await tick("message-952").and(ruler.locator('[data-current="true"]')).waitFor();
  const library = page.locator(".sidebar .room-list[aria-label]");
  assert.equal(await library.locator(".room-row").count(), 40);
  await page.getByRole("searchbox", { name: "Search chats" }).fill("Room 64");
  assert.equal(await library.getByRole("button", { name: "Room 64", exact: true }).count(), 1);
  await page.getByRole("searchbox", { name: "Search chats" }).fill("");
  await page.locator(".sidebar").getByRole("button", { name: "Load more" }).click();
  assert.equal(await library.locator(".room-row").count(), 70);
  const openRoom = async (name) => {
    await library.getByRole("button", { name, exact: true }).click();
    await page.getByRole("heading", { name, exact: true }).waitFor();
  };
  await openRoom("Fitting room");
  await transcript.getByText("A short answer.").waitFor();
  await ruler.waitFor({ state: "hidden" });
  await openRoom("Short room");
  await ruler.waitFor();
  assert.equal(await ruler.locator("[data-turn]").count(), 2);
  await fade(false, false);
  const shortPosition = await transcript.evaluate((element) => element.scrollTop);
  await ruler.hover();
  await page.mouse.wheel(0, -100);
  await page.waitForFunction((position) => document.querySelector(".transcript").scrollTop < position, shortPosition);
  assert.equal(await railViewport.evaluate((element) => element.scrollTop), 0);
  const shortTrace = transcript.locator(".trace summary");
  const collapsedHeight = await transcript.evaluate((element) => element.scrollHeight);
  await shortTrace.click();
  await page.waitForFunction((height) => document.querySelector(".transcript").scrollHeight > height + 300, collapsedHeight);
  assert.equal(await ruler.locator("[data-turn]").count(), 2);
  await shortTrace.click();
  await page.waitForFunction((height) => document.querySelector(".transcript").scrollHeight === height, collapsedHeight);
  assert.equal(await ruler.locator("[data-turn]").count(), 2);
  await transcript.getByRole("button", { name: "Branch from here", exact: true }).focus();
  await page.keyboard.press("Tab");
  assert.equal(await page.evaluate(() => document.activeElement.getAttribute("role") || document.activeElement.getAttribute("aria-label")), "scrollbar");
  await noRailOutline();
  assert.equal(await ruler.locator('[data-current="true"]').evaluate((element) => getComputedStyle(element).outlineWidth), "2px");
  await openRoom("Fixture room");
  await ruler.waitFor();
  assert.equal(await ruler.getAttribute("aria-valuemax"), "11");
  await openRoom("Short room");
  await ruler.waitFor();
  assert.equal(await ruler.locator("[data-turn]").count(), 2);
  await fade(false, false);
  assert.equal(await ruler.getAttribute("aria-valuenow"), "2");
  assert.equal(await preview.count(), 0);
  assert.deepEqual(errors, []);
  await page.close();

  displayLocale = "ar";
  const touch = await browser.newPage({ viewport: { width: 420, height: 680 }, hasTouch: true, reducedMotion: "reduce" });
  await touch.goto(url);
  await touch.waitForFunction(() => document.documentElement.dir === "rtl" && document.querySelector(".transcript")?.scrollTop > 0);
  await touch.waitForFunction(end, await touch.locator(".transcript").elementHandle());
  const rtlRuler = touch.locator(".transcript-ruler");
  await rtlRuler.waitFor();
  const rtlRail = await rtlRuler.boundingBox();
  const rtlCurrentTick = rtlRuler.locator('[data-current="true"]');
  await rtlCurrentTick.waitFor();
  const rtlTick = await rtlCurrentTick.boundingBox();
  const beforeHover = await touch.locator(".transcript").evaluate((element) => element.scrollTop);
  await touch.mouse.move(rtlRail.x + rtlRail.width / 2, rtlTick.y + rtlTick.height / 2);
  const rtlPreview = touch.locator(".transcript-ruler-card");
  await rtlPreview.waitFor();
  const box = await rtlPreview.boundingBox();
  assert.ok(box.x >= 0 && box.x + box.width <= rtlRail.x + 1 && box.y >= 0 && box.y + box.height <= 680);
  assert.equal(await touch.locator(".transcript-ruler-preview").evaluate((element) => getComputedStyle(element).pointerEvents), "none");
  assert.equal(await touch.locator(".transcript").evaluate((element) => element.scrollTop), beforeHover);
  const viewport = await touch.locator(".transcript").boundingBox();
  const session = await touch.context().newCDPSession(touch);
  const x = Math.round(viewport.x + viewport.width / 2);
  const y = Math.round(viewport.y + viewport.height / 3);
  await session.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x, y }] });
  for (const distance of [50, 110, 180])
    await session.send("Input.dispatchTouchEvent", { type: "touchMove", touchPoints: [{ x, y: y + distance }] });
  await session.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
  await touch.waitForFunction(() => document.querySelector(".transcript").dataset.following === "false");
  assert.equal(await touch.locator(".transcript").evaluate((element) => element.scrollWidth === element.clientWidth), true);
  for (let number = 1; number <= 4; number++) {
    await rtlRuler.press("Home");
    await touch.locator(`[data-transcript-row=message-${firstRecent - pageSize * number}]`).waitFor({ state: "attached" });
  }
  const rtlViewport = touch.locator(".transcript-ruler-viewport");
  await rtlViewport.evaluate((element) => { element.scrollTop = 0; });
  const beforeRailTouch = await touch.locator(".transcript").evaluate((element) => element.scrollTop);
  const xTick = Math.round(rtlRail.x + rtlRail.width / 2);
  const yTickStart = Math.round(rtlRail.y + rtlRail.height * .75);
  await session.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x: xTick, y: yTickStart }] });
  for (const distance of [45, 90, 144])
    await session.send("Input.dispatchTouchEvent", { type: "touchMove", touchPoints: [{ x: xTick, y: yTickStart - distance }] });
  await session.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
  await touch.waitForFunction(() => document.querySelector(".transcript-ruler-viewport").scrollTop > 20);
  assert.equal(await touch.locator(".transcript").evaluate((element) => element.scrollTop), beforeRailTouch);
  await touch.close();

  displayLocale = "zh-Hans";
  const chinese = await browser.newPage({ viewport: { width: 1020, height: 650 }, reducedMotion: "reduce" });
  await chinese.goto(url);
  await chinese.locator("html[lang=zh-Hans] .transcript-ruler").waitFor();
  publish([snapshot(shortRoom, "Short room", [
    ...short.slice(0, 2), { id: "chinese-steer", kind: "user", interjection: true, content: "请补充这个细节。" }, ...short.slice(2),
  ])]);
  const chineseRuler = chinese.getByRole("scrollbar", { name: "滚动聊天记录" });
  const chineseTick = chineseRuler.locator("[data-turn=chinese-steer]");
  await chineseTick.waitFor();
  const chineseRail = await chineseRuler.boundingBox();
  const chineseTickBox = await chineseTick.boundingBox();
  await chinese.mouse.move(chineseRail.x + chineseRail.width / 2, chineseTickBox.y + chineseTickBox.height / 2);
  const chinesePreview = chineseRuler.locator(".transcript-ruler-card");
  await chinesePreview.locator(".transcript-ruler-interjection").getByText("插话", { exact: true }).waitFor();
  await chinesePreview.getByText("请补充这个细节。", { exact: true }).waitFor();
  assert.equal(await chinesePreview.locator("strong").count(), 1);
  await chinese.close();
  console.log("Desktop transcript paging, selection and scroll passed, including turn previews, independently scrolling rail, fade, focus, RTL touch and older rooms.");
} finally {
  firstPage.resolve();
  await browser?.close();
  for (const client of clients) client.end();
  await vite.close();
}
