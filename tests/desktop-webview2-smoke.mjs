import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, readFile, realpath, rm, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { isAbsolute, join, relative, resolve } from "node:path";
import { createInterface } from "node:readline";
import { setTimeout as delay } from "node:timers/promises";
import { chromium } from "playwright";

assert.equal(process.platform, "win32", "The Evergreen smoke test runs on Windows.");
const helper = resolve(process.argv[2] ?? "artifacts/desktop/win-x64/browser/minirp-webview2.exe");
const root = await mkdtemp(join(await realpath(tmpdir()), "minirp-webview2-qa-"));
const children = new Set();
const connections = new Set();
let selected;

const service = createServer((request, response) => {
  if (request.url === "/download") {
    response.writeHead(200, { "Content-Disposition": 'attachment; filename="fixture.txt"', "Content-Type": "text/plain" });
    response.end("Browser fixture download");
  } else {
    response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    response.end(`<!doctype html><title>Browser fixture</title>
      <main><h1>Fixture page</h1><button onclick="localStorage.color='blue';document.cookie='test=blue';output.textContent='Saved'">Save state</button>
      <p id=output>Not saved</p><label>Choose file<input type=file onchange="this.files[0].text().then(value=>upload.textContent=value)"></label>
      <p id=upload></p><a href=/download download>Download file</a></main>`);
  }
});
service.listen(0, "127.0.0.1");
await once(service, "listening");
const url = "http://127.0.0.1:" + service.address().port;

function launch(args, visible = false) {
  const env = { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: "--remote-debugging-port=0" };
  if (selected) env.MINIRP_WEBVIEW2_EXECUTABLE = selected;
  const child = spawn(helper, args, { windowsHide: !visible, env });
  const exit = once(child, "exit");
  exit.catch(() => {});
  children.add({ child, exit });
  return { child, exit };
}

async function readMarker(profile, name, { child, exit }) {
  for (let attempt = 0; attempt < 150; attempt++) {
    const text = await readFile(join(profile, name), "utf8").catch(() => "");
    if (text) return text.trim();
    if (child.exitCode !== null) throw new Error("Helper exited before becoming ready with code " + child.exitCode);
    await delay(125);
  }
  child.kill();
  await exit;
  throw new Error("Helper readiness timed out.");
}

async function waitForExit(exit) {
  const timeout = new AbortController();
  try {
    return await Promise.race([
      exit.then(([code]) => code),
      delay(20000, undefined, { signal: timeout.signal }).then(() => { throw new Error("The helper did not close."); })
    ]);
  } finally {
    timeout.abort();
  }
}

async function activateWindow(process, button) {
  const action = button === undefined
    ? "if (!$app.CloseMainWindow()) { exit 7 }"
    : `Add-Type -Namespace MiniRP -Name FixtureDialog -MemberDefinition '
        [DllImport("user32.dll", EntryPoint="SendMessageW")]
        public static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);'
       [MiniRP.FixtureDialog]::SendMessage($app.MainWindowHandle,0x466,[IntPtr]${button},[IntPtr]0) | Out-Null`;
  const command = `
    $app = Get-Process -Id ${process.pid}
    $until = [DateTime]::UtcNow.AddSeconds(12)
    while ($app.MainWindowHandle -eq 0) {
      if ($app.HasExited -or [DateTime]::UtcNow -gt $until) { exit 6 }
      Start-Sleep -Milliseconds 200
      $app.Refresh()
    }
    ${action}`;
  const driver = spawn("pwsh", ["-NoProfile", "-Command", command], { windowsHide: true, stdio: "ignore" });
  assert.equal((await once(driver, "exit"))[0], 0, "The helper window did not become interactive.");
}

async function connect(profile) {
  await mkdir(profile);
  const process = launch(["agent", profile]);
  const endpoint = await readMarker(profile, "minirp-cdp", process);
  assert.ok((await readFile(join(profile, "minirp-runtime"), "utf8")).trim() === selected);
  const browser = await chromium.connectOverCDP(endpoint);
  connections.add(browser);
  return { endpoint, browser, process, profile };
}

async function checkMcp({ endpoint, profile }) {
  const cli = resolve("node_modules/@playwright/mcp/cli.js");
  const mcp = spawn(process.execPath, [cli, "--cdp-endpoint", endpoint, "--no-webmcp", "--image-responses", "omit", "--output-dir", profile],
    { windowsHide: true, stdio: ["pipe", "pipe", "inherit"] });
  try {
    const output = createInterface({ input: mcp.stdout });
    const pending = new Map();
    output.on("line", (line) => {
      const value = JSON.parse(line);
      pending.get(value.id)?.(value);
    });
    const send = (value) => mcp.stdin.write(JSON.stringify({ jsonrpc: "2.0", ...value }) + "\n");
    const request = (id, method, params) => new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("Playwright MCP call timed out.")), 12000);
      pending.set(id, (value) => { clearTimeout(timer); resolve(value); });
      send({ id, method, params });
    });
    assert.ok((await request(1, "initialize", {
      protocolVersion: "2025-11-25", capabilities: {}, clientInfo: { name: "minirp-fixture", version: "1" }
    })).result);
    send({ method: "notifications/initialized" });
    const navigation = await request(2, "tools/call", { name: "browser_navigate", arguments: { url } });
    assert.equal(Boolean(navigation.error || navigation.result.isError), false);
    const snapshot = navigation.result.content.map((item) => item.text).join(" ").match(/\[Snapshot\]\(([^)]+)\)/);
    assert.ok(snapshot, "MCP did not return an accessible snapshot.");
    const file = resolve(profile, snapshot[1]);
    const within = relative(profile, file);
    assert.ok(!isAbsolute(within) && !within.startsWith(".."), "The MCP snapshot is outside its fixture.");
    const ref = (await readFile(file, "utf8")).match(/button "Save state" \[ref=(\w+)\]/);
    assert.ok(ref, "MCP did not assign a semantic reference.");
    const click = await request(3, "tools/call", { name: "browser_click", arguments: { target: ref[1], element: "Save state" } });
    const detail = click.result?.content?.find((item) => item.type === "text")?.text ?? click.error?.message;
    assert.equal(Boolean(click.error || click.result.isError), false, detail?.replaceAll(profile, "[fixture]"));
    output.close();
  } finally {
    mcp.stdin.end();
  }
}

try {
  const probe = join(root, "probe");
  await mkdir(probe);
  assert.equal(await waitForExit(launch(["probe", probe]).exit), 0);
  selected = (await readFile(join(probe, "minirp-runtime"), "utf8")).trim();
  assert.match(await readFile(join(probe, "minirp-version"), "utf8"), /^\d+\.\d+\.\d+\.\d+/);

  const profile = join(root, "window");
  await mkdir(profile);
  const window = launch(["window", profile, url], true);
  await readMarker(profile, "minirp-version", window);
  assert.ok((await readFile(join(profile, "minirp-runtime"), "utf8")).trim() === selected);
  await assert.rejects(readFile(join(profile, "EBWebView", "DevToolsActivePort"), "utf8"), { code: "ENOENT" });
  await activateWindow(window.child);
  assert.equal(await waitForExit(window.exit), 0);

  const [one, two] = await Promise.all(["agent-a", "agent-b"].map((id) => connect(join(root, id))));
  assert.notEqual(one.endpoint, two.endpoint);
  const [page, other] = [one, two].map(({ browser }) => browser.contexts()[0].pages()[0]);
  await Promise.all([page.goto(url), other.goto(url)]);
  assert.match(await page.locator("main").ariaSnapshot(), /Fixture page/);
  await page.getByRole("button", { name: "Save state" }).click();
  const file = join(root, "fixture.txt");
  await writeFile(file, "Isolated file");
  await page.getByLabel("Choose file").setInputFiles(file);
  await page.getByText("Isolated file", { exact: true }).waitFor();
  assert.equal(await page.evaluate(() => document.cookie), "test=blue");
  assert.equal(await other.evaluate(() => document.cookie), "");
  assert.equal(await other.evaluate(() => localStorage.color), undefined);
  const download = page.waitForEvent("download", { timeout: 6000 });
  await page.getByRole("link", { name: "Download file" }).click();
  assert.equal((await download).suggestedFilename(), "fixture.txt");
  await checkMcp(two);
  one.process.child.kill();
  await waitForExit(one.process.exit);
  assert.equal(await other.getByRole("heading", { name: "Fixture page" }).count(), 1);

  const later = launch(["confirm", "1048576"], true);
  await activateWindow(later.child);
  assert.equal(await waitForExit(later.exit), 2);
  const consent = launch(["confirm", "1048576"], true);
  await activateWindow(consent.child, 101);
  assert.equal(await waitForExit(consent.exit), 0);

  const control = join(root, "progress");
  await mkdir(control);
  await writeFile(join(control, "minirp-progress"), "45");
  const cancelled = launch(["progress", control], true);
  await activateWindow(cancelled.child);
  assert.equal(await waitForExit(cancelled.exit), 2);
  const finished = launch(["progress", control], true);
  await writeFile(join(control, "minirp-done"), "");
  assert.equal(await waitForExit(finished.exit), 0);
  console.log("Evergreen binary pin, window and Agent profiles, upload/download, MCP references, cancellation and native download prompts verified.");
} finally {
  await Promise.allSettled([...connections].map((browser) => browser.close()));
  for (const { child } of children) child.kill();
  await Promise.allSettled([...children].map(({ exit }) => exit));
  service.closeAllConnections();
  service.close();
  const path = relative(await realpath(tmpdir()), await realpath(root));
  if (/^minirp-webview2-qa-[^\\/]+$/.test(path))
    await rm(root, { recursive: true, force: true, maxRetries: 6, retryDelay: 200 });
}
