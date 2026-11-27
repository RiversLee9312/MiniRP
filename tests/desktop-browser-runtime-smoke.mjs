import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { chromium } from "playwright";

const program = resolve(process.argv[2]);
const existingRuntime = process.env.MINIRP_TEST_CEF_CACHE;
const scenarios = existingRuntime ? ["reuse"] : process.argv.includes("--consent-only") ? ["later", "cancel"]
  : process.argv.includes("--cef") ? ["later", "download", "reuse"] : ["system"];
const fixture = await mkdtemp(join(tmpdir(), "minirp-runtime-test-"));
const workspace = join(fixture, "workspace");
const preset = "4f6145d1-dcad-4477-8c43-7bf8f632cc5b";
const revealed = "Full fixture article revealed.";
const failures = [];
let modelRound, scenario, article, failure, submittedPage;
const server = createServer(async (request, response) => {
  if (request.url === "/favicon.ico") {
    response.writeHead(204).end();
    return;
  }
  if (request.url === "/article") {
    response.setHeader("Content-Type", "text/html");
    response.end(`<!doctype html><title>Fixture article</title>
      <main><button>Reveal article</button><p hidden>${revealed}</p></main>
      <script>document.querySelector('button').onclick =
        () => document.querySelector('p').hidden = false</script>`);
    return;
  }
  try {
    assert.equal(request.url, "/v1/chat/completions");
    let body = "";
    for await (const bytes of request) body += bytes;
    const model = JSON.parse(body);
    if (!model.stream) {
      response.setHeader("Content-Type", "application/json");
      response.end(JSON.stringify({ choices: [{ message: { content: "Fixture chat" } }] }));
      return;
    }
    const tools = (model.tools || []).map((tool) => tool.function.name);
    const message = model.messages.at(-1);
    const round = ++modelRound;
    const call = (name, args = {}) => {
      assert.ok(tools.includes(name), `Round ${round} must offer ${name}.`);
      return { tool_calls: [{ index: 0, id: "fixture_" + round, type: "function",
        function: { name, arguments: JSON.stringify(args) } }] };
    };
    const delta = (() => {
      if (round === 1) return call("open_url", { url: article });
      if (round === 2) return call("real_fetch", { url: article, backend: "local" });
      if (round === 3) return call("browser_navigate", { url: article });
      if (scenario === "later" || scenario === "cancel") {
        if (round === 4) {
          assert.match(message.content, scenario === "later" ? /download deferred/i : /cancelled|canceled/i);
          return call("fail_request", { message: "Browser download stopped." });
        }
        if (tools.includes("fail_request")) return call("fail_request", { message: "Browser download stopped." });
        return { content: "Fixture download stopped." };
      }
      if (round === 4) {
        const { snapshot, error } = JSON.parse(message.content);
        assert.equal(typeof snapshot, "string", "browser_navigate must return a snapshot. " +
          (typeof error === "string" ? error.replaceAll(fixture, "<fixture>") : ""));
        const target = snapshot.match(/button "Reveal article" \[ref=([\w]+)\]/)?.[1];
        assert.ok(target, "The native browser must return an accessibility ref.");
        return call("browser_click", { ref: target });
      }
      if (round === 5) return call("capture_page");
      if (round === 6) {
        assert.match(message.content, /Full fixture article revealed/);
        return call("finish_fetch", { complete: true });
      }
      if (round === 7) {
        const result = JSON.parse(message.content);
        assert.match(result.content, /Full fixture article revealed/);
        return call("submit_page", { ref: result.ref });
      }
      const openedPage = model.messages.find((item) => item.role === "tool" && item.tool_call_id === "fixture_1");
      if (openedPage) {
        const result = JSON.parse(openedPage.content);
        assert.equal(result.url, article);
        assert.ok(result.content?.includes(revealed) || result.asset_url?.startsWith("minirp://assets/"),
          "The chat agent must receive the captured page or its asset URL.");
        submittedPage = true;
      }
      return { content: "Fixture native page captured." };
    })();
    response.setHeader("Content-Type", "text/event-stream");
    response.end("data: " + JSON.stringify({ choices: [{ delta }] }) + "\n\ndata: [DONE]\n\n");
  } catch (error) {
    failures.push(error);
    failure?.resolve(error);
    response.writeHead(500).end("Fixture error");
  }
});
server.listen(0, "127.0.0.1");
await once(server, "listening");
const base = "http://127.0.0.1:" + server.address().port;
article = base + "/article";
await mkdir(join(workspace, "presets"), { recursive: true });
await writeFile(join(workspace, "presets", preset + ".json"), JSON.stringify({
  name: "Browser fixture", chat_mode: "character", agentic_search: true,
}));
await writeFile(join(workspace, ".env"), [
  "DEEPSEEK_BASE_URL=" + base + "/v1", "DEEPSEEK_API_KEY=fixture", "DEEPSEEK_MODEL=fixture",
  "MINIRP_FETCH_BACKEND=local", scenarios[0] !== "system" ? "MINIRP_BROWSER=cef" : "",
].join("\n"));

let browser;
try {
  browser = await chromium.launch(process.env.MINIRP_TEST_BROWSER ? { channel: process.env.MINIRP_TEST_BROWSER } : {});
  for (const phase of scenarios) {
    modelRound = 0;
    scenario = phase;
    submittedPage = false;
    failure = Promise.withResolvers();
    const child = spawn(program, ["--web", "--no-open", workspace], {
      env: { ...process.env, MINIRP_BROWSER_DATA_DIR: existingRuntime || join(fixture, "runtime"),
        MINIRP_CONTAINER_IMAGE: join(fixture, "no-container") },
      stdio: ["ignore", "pipe", "pipe"],
    });
    const exit = once(child, "exit");
    const page = await browser.newPage({ locale: "en-US" });
    let diagnostics = "";
    child.stderr.on("data", (bytes) => {
      diagnostics = (diagnostics + bytes.toString().replace(/\/connect\/[a-f0-9]+/g, "/connect/<redacted>")).slice(-1800);
    });
    try {
      const url = await new Promise((success, fail) => {
        const timeout = setTimeout(() => fail(new Error("The native host did not start.")), 30000);
        let output = "";
        child.stdout.on("data", (bytes) => {
          output += bytes.toString();
          const address = output.match(/MiniRP WebUI: (\S+)/)?.[1];
          if (address) { clearTimeout(timeout); success(address); }
        });
        child.once("error", (error) => { clearTimeout(timeout); fail(error); });
        child.once("exit", (code) => {
          clearTimeout(timeout);
          fail(new Error("The native host exited with code " + code + ". " + diagnostics));
        });
      });
      await page.goto(url);
      await page.getByRole("heading", { name: "Browser fixture" }).waitFor();
      const response = await page.request.post(new URL("/api/command", url).href, { data: { op: "message", agent: "chat",
        preset, text: "Read the fixture article." } });
      assert.ok(response.ok());
      const dialog = page.getByRole("dialog", { name: "Browser runtime" });
      if (phase === "later") await dialog.getByRole("button", { name: "Later", exact: true }).click();
      if (phase === "download" || phase === "cancel") {
        await dialog.getByRole("button", { name: "Download and continue", exact: true }).click();
        await dialog.getByRole("progressbar").waitFor();
        if (phase === "cancel") await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
      }
      const expected = phase === "later" || phase === "cancel" ? "Fixture download stopped." : "Fixture native page captured.";
      const outcome = [
        page.locator(".transcript").getByText(expected, { exact: true }).waitFor({ timeout: phase === "download" ? 900000 : 50000 }),
        failure.promise.then((error) => { throw error; }),
      ];
      if (phase === "system" || phase === "reuse") {
        outcome.push(dialog.waitFor({ timeout: 0 }).then(() => {
          assert.fail("The selected browser is already available. MiniRP must not offer a download.");
        }));
      }
      await Promise.race(outcome);
      assert.deepEqual(failures, []);
      if (phase !== "later" && phase !== "cancel") assert.ok(submittedPage, "The page submission result must reach the model.");
      if (phase === "reuse") assert.equal(await dialog.count(), 0);
      console.log("Desktop browser runtime verified:", phase);
    } catch (error) {
      if (failures.length > 0) throw failures[0];
      throw error;
    } finally {
      await page.close();
      child.kill();
      await exit;
    }
  }
} finally {
  await browser?.close();
  server.closeAllConnections();
  server.close();
  await rm(fixture, { recursive: true, force: true, maxRetries: 6 }).catch((error) => {
    if (error.code !== "EBUSY" && error.code !== "EPERM") throw error;
    console.error("The isolated browser fixture is still in use.");
  });
}
