import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdir, mkdtemp, realpath, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, dirname, join, resolve } from "node:path";
import { setTimeout as wait } from "node:timers/promises";

const temp = await realpath(tmpdir());
const workspace = await mkdtemp(join(temp, "minirp-history-http-"));
const binary = resolve(process.argv[2] ?? ("artifacts/desktop/" + (process.platform === "win32" ? "win-x64/minirp.exe" : "linux-x64/minirp")));
const preset = randomUUID(), room = randomUUID(), another = randomUUID(), file = randomUUID();
const blocks = [];
let oldAsset;
for (let index = 0; index < 205; index++) {
  const block = { id: randomUUID(), agent: "ChatAgent", kind: "user", content: "History " + index };
  if (index === 80) {
    oldAsset = block;
    block.plugin_card = { package: "example.history.card" };
    block.asset_links = { ["minirp://assets/minirp.attachments/" + file]: {
      attachment: { id: file, mime: "text/plain", name: "history.txt" }
    }};
  }
  blocks.push(block);
  if (index < 95) blocks.push({ id: randomUUID(), agent: "EnvAgent", kind: "system", content: "World " + index });
}
let child, closed;
try {
  await mkdir(join(workspace, "presets"));
  await mkdir(join(workspace, "conversations", preset), { recursive:true });
  await mkdir(join(workspace, "attachments"));
  await writeFile(join(workspace, "presets", preset + ".json"), JSON.stringify({ id:preset, name:"Archive fixture" }));
  await writeFile(join(workspace, "conversations", preset, room + ".json"), JSON.stringify({ id:room, name:"Archived room", messages:[], blocks }));
  await writeFile(join(workspace, "conversations", preset, another + ".json"), JSON.stringify({ id:another, name:"Other room", messages:[], blocks:[] }));
  await writeFile(join(workspace, "attachments", file), "Attachment from an older page.");
  child = spawn(binary, ["--web", "--no-open", workspace], {
    env: { ...process.env, MINIRP_CONTAINER_IMAGE: join(workspace,"missing") },
    stdio: ["ignore", "pipe", "pipe"]
  });
  closed = once(child, "exit");
  const connect = await new Promise((resolve, reject) => {
    const deadline = setTimeout(() => reject(new Error("WebUI start timeout")), 40000);
    let output = "";
    child.stderr.resume();
    child.stdout.on("data", data => {
      output += data;
      const match = output.match(/MiniRP WebUI: (\S+)/);
      if (match) { clearTimeout(deadline); resolve(match[1]); }
    });
    child.once("exit", code => { clearTimeout(deadline); reject(new Error("WebUI exited " + code)); });
    child.once("error", error => { clearTimeout(deadline); reject(error); });
  });
  const origin = new URL(connect).origin;
  const auth = await fetch(connect, { redirect:"manual" });
  assert.equal(auth.status, 302);
  const cookie = auth.headers.get("set-cookie").split(";")[0];
  async function command(value) {
    const response = await fetch(origin + "/api/command",{method:"POST",headers:{cookie,"content-type":"application/json"},body:JSON.stringify(value)});
    assert.equal(response.status,200);
  }
  async function findEvents(match, timeout=12000) {
    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(new Error("Event timeout")), timeout);
    try {
      const response = await fetch(origin + "/api/events", { headers:{cookie},signal:abort.signal });
      assert.equal(response.status,200);
      let buffer = "";
      for await (const chunk of response.body.pipeThrough(new TextDecoderStream())) {
        buffer += chunk;
        let index;
        while ((index = buffer.indexOf("\n\n")) >= 0) {
          const line = buffer.slice(0,index);
          buffer = buffer.slice(index+2);
          if (line.startsWith("data: ")) {
            const batch = JSON.parse(line.slice(6));
            assert.ok(batch.every(item => item.kind !== "transcript_checkpoint"));
            const found = match(batch);
            if (found) return found;
          }
        }
      }
      throw new Error("Event stream closed");
    } finally { clearTimeout(timer); abort.abort(); }
  }
  const gotRoom = findEvents(batch => batch.find(item => item.kind==="loaded" && item.chat.id===room));
  await command({op:"select_chat",preset,id:room});
  const initial = await gotRoom;
  assert.equal(initial.chat.blocks.length,160);
  assert.equal(initial.chat.blocks[0].content,"History 125");
  assert.equal(initial.chat.blocks[80].content,"World 15");
  assert.equal(initial.history.preset,null);
  const scope = {preset,room,conversation:"",agent:"chat"};
  const read = (before, override = {}, authenticated=true) => fetch(origin+"/api/transcript?"+new URLSearchParams({...scope,...override,before}), {
    headers: authenticated ? {cookie} : {}
  });
  const readAsset = () => fetch(origin+"/api/asset?"+new URLSearchParams({
    ...scope,mode:"display",block:oldAsset.id,url:Object.keys(oldAsset.asset_links)[0]
  }),{headers:{cookie}});
  async function cardRefreshes() {
    const request = randomUUID();
    let count = 0;
    let refreshing = false;
    const established = findEvents(batch => {
      for (const item of batch) {
        if (item.kind === "plugin_settings" && item.request === request) refreshing = true;
        if (refreshing && item.kind === "open_ui_card_update" && item.block === oldAsset.id) count++;
        if (refreshing && item.kind === "presets") return true;
      }
      return false;
    });
    await command({op:"plugin_locale",locale:"en",request});
    await command({op:"list_presets"});
    await established;
    return count;
  }
  assert.equal(await cardRefreshes(),0);
  assert.equal((await readAsset()).status,400);
  assert.equal((await read(initial.history.chat, {}, false)).status,401);
  assert.equal((await read(initial.history.chat,{room:another})).status,400);
  assert.equal((await read(initial.history.chat,{agent:"env"})).status,400);
  const second = await (await read(initial.history.chat)).json();
  assert.equal(second.blocks.length,80);
  assert.equal(second.blocks[0].content,"History 45");
  assert.equal(second.blocks.at(-1).content,"History 124");
  assert.ok(second.blocks.some(item => item.id===oldAsset.id));
  const oldest = await (await read(second.before)).json();
  assert.equal(oldest.blocks.length,45);
  assert.equal(oldest.before,null);
  assert.equal(new Set([...oldest.blocks,...second.blocks,...initial.chat.blocks.slice(0,80)].map(item=>item.id)).size,205);
  assert.equal(await cardRefreshes(),1);
  const asset = await readAsset();
  assert.equal(asset.status,200);
  assert.equal(await asset.text(),"Attachment from an older page.");

  const live = findEvents(batch => batch.some(item => item.kind==="system" && item.text==="Live 279"), 30000);
  for (let index = 0; index < 280; index++) await command({op:"system_message",preset,chat:room,text:"Live "+index});
  await live;
  let replay;
  const checkpointed = (item) => item.kind === "loaded" && item.chat.blocks.some(block =>
    /^Live \d+$/.test(block.content || "") && Number(block.content.slice(5)) >= 250);
  for (let attempt = 0; attempt < 35; attempt++) {
    replay = await findEvents(batch => batch);
    if (replay.some(checkpointed)) break;
    await wait(70);
  }
  const restored = replay.find(item => item.kind==="loaded");
  assert.ok(checkpointed(restored));
  assert.equal(restored.chat.blocks.length,160);
  assert.ok(replay.filter(item=>item.kind==="system").length <= 30);
  assert.ok(restored.chat.blocks.some(block => block.content === "Live 279") ||
    replay.some(item => item.kind === "system" && item.text === "Live 279"));
  const repeat = await (await read(initial.history.chat)).json();
  assert.deepEqual(repeat.blocks,second.blocks);
  const otherRoom = findEvents(batch => batch.find(item => item.kind==="loaded" && item.chat.id===another));
  await command({op:"select_chat",preset,id:another});
  await otherRoom;
  assert.equal((await read(initial.history.chat)).status,400);

  const activityPreset = randomUUID();
  const run = randomUUID().replaceAll("-", "").slice(0, 12);
  const worker = randomUUID().replaceAll("-", "").slice(0, 12);
  const legacyWorker = randomUUID();
  const activityDirectory = join(workspace, "preset_sessions", activityPreset);
  await mkdir(activityDirectory, { recursive: true });
  await writeFile(join(workspace, "presets", activityPreset + ".json"), JSON.stringify({ id: activityPreset, name: "Activity fixture" }));
  await writeFile(join(activityDirectory, "blocks.json"), JSON.stringify([
    { id: run, agent: "PluginAgent", plugin_agent: worker, plugin_run: run, plugin_status: "finished" },
    { id: run + "-reply", agent: "PluginAgent", plugin_agent: worker, plugin_run: run, content: "Completed run output." },
    { id: legacyWorker, agent: "PluginAgent", plugin_agent: legacyWorker, content: "Legacy run output." },
  ]));
  const selected = findEvents(batch => batch.find(item => item.kind === "home" && item.preset === activityPreset));
  await command({ op: "home", preset: activityPreset });
  await selected;
  const opened = findEvents(batch => batch.find(item => item.kind === "preset_opened" && item.preset === activityPreset));
  await command({ op: "open_preset", preset: activityPreset });
  await opened;
  const activity = (parameters = {}) => fetch(origin + "/api/preset/activity?" + new URLSearchParams({ preset: activityPreset, ...parameters }),
    { headers: { cookie } });
  const list = await activity();
  assert.equal(list.status, 200);
  assert.deepEqual((await list.json()).items.map(item => item.id), [run, legacyWorker]);
  for (const [id, content] of [[run, "Completed run output."], [legacyWorker, "Legacy run output."]]) {
    const detail = await activity({ run: id });
    assert.equal(detail.status, 200);
    assert.deepEqual((await detail.json()).blocks.filter(block => block.content).map(block => block.content), [content]);
  }
  assert.equal((await activity({ run: "unlisted-run" })).status, 400);
  assert.equal((await activity({ run, preset })).status, 400);
  console.log("Packaged WebUI history: pagination, scoped authorization, SSE replay, and plugin run lookup verified.");
} finally {
  if (child && child.exitCode===null) { child.kill(); await closed; }
  const path = await realpath(workspace);
  assert.equal(dirname(path).toLowerCase(),temp.toLowerCase());
  assert.ok(basename(path).startsWith("minirp-history-http-"));
  await rm(path,{recursive:true,force:true});
}
