// 端到端测试：启动 fake-bridge（NamedPipe），用 MCP Client 调真实 server 的工具
import { spawn } from "node:child_process";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, "..");

const fake = spawn(process.execPath, [path.join(__dirname, "fake-bridge.mjs")], { stdio: "inherit" });
await new Promise((r) => setTimeout(r, 800));

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(root, "src", "index.js")],
  env: { ...process.env, HNPF_PIPE: "\\\\.\\pipe\\hnpf-mcp-bridge-test" },
});

const client = new Client({ name: "hnpf-e2e", version: "0.0.1" });
try {
  await client.connect(transport);

  const state = await client.callTool({ name: "get_state", arguments: {} });
  console.log("[e2e] get_state:", JSON.stringify(JSON.parse(state.content[0].text), null, 1).slice(0, 400));

  const net = await client.callTool({ name: "get_network_map", arguments: {} });
  const netJson = JSON.parse(net.content[0].text);
  console.log(`[e2e] get_network_map: ${netJson.count} nodes, 首节点 ${netJson.nodes[0].name}`);

  const comp = await client.callTool({ name: "get_computer", arguments: { ip: "10.0.0.2" } });
  console.log("[e2e] get_computer 用户:", JSON.parse(comp.content[0].text).users[0].name);

  const f = await client.callTool({ name: "read_file", arguments: { ip: "10.0.0.2", path: "/home", file: "passwd" } });
  console.log("[e2e] read_file:", JSON.parse(f.content[0].text).data);

  const ex = await client.callTool({ name: "execute_command", arguments: { cmd: "ls -la" } });
  console.log("[e2e] execute_command:", JSON.stringify(ex.content[0].text));

  const exSync = await client.callTool({ name: "execute_command", arguments: { cmd: "ls -la", waitMs: 100 } });
  const exSyncJson = JSON.parse(exSync.content[0].text);
  console.log("[e2e] execute_command waitMs: submitted=", "submitted" in exSyncJson, "output.lines=", exSyncJson.output?.lines?.length);

  const hs = await client.callTool({ name: "run_hack_script", arguments: { script: "Missions/Test.hackscript" } });
  console.log("[e2e] run_hack_script:", JSON.stringify(hs.content[0].text).slice(0, 60));

  const ml = await client.callTool({ name: "mail_list", arguments: { ip: "10.0.0.2" } });
  console.log("[e2e] mail_list 首邮件:", JSON.parse(ml.content[0].text).users[0].mailboxes[0].mails[0].subject);

  const mr = await client.callTool({ name: "mail_read", arguments: { ip: "10.0.0.2", user: "root", subject: "Mission Brief" } });
  console.log("[e2e] mail_read body:", JSON.parse(mr.content[0].text).body);

  const sl = await client.callTool({ name: "save_list", arguments: {} });
  const slJson = JSON.parse(sl.content[0].text);
  console.log(`[e2e] save_list 存档数: ${slJson.saves.length} 快照数: ${(slJson.snapshots || []).length}`);

  const ev = await client.callTool({ name: "get_events", arguments: {} });
  console.log("[e2e] get_events:", JSON.parse(ev.content[0].text).events[0].event);

  const reg = await client.callTool({ name: "registry", arguments: {} });
  const regJson = JSON.parse(reg.content[0].text);
  console.log(`[e2e] registry: actions=${regJson.actions.length} executables=${regJson.executables.length}`);

  const prompts = await client.listPrompts();
  console.log(`[e2e] prompts/list: ${prompts.prompts.map((p) => p.name).join(", ")}`);

  const md = await client.callTool({ name: "mission_detail", arguments: {} });
  const mdJson = JSON.parse(md.content[0].text);
  console.log(`[e2e] mission_detail: goals=${mdJson.goals?.length} 首目标=${mdJson.goals?.[0]?.type} xml=${mdJson.xml ? "有" : "无"}`);

  const lg = await client.callTool({ name: "launch_game", arguments: { ext: "KernelExtensionTEST123123", dryRun: true } });
  console.log(`[e2e] launch_game dryRun: ${JSON.parse(lg.content[0].text).cmd}`);

  const me = await client.callTool({ name: "menu_enter_extension", arguments: { username: "e2euser" } });
  console.log(`[e2e] menu_enter_extension: ${JSON.parse(me.content[0].text).mode}`);

  const irc = await client.callTool({ name: "irc_read", arguments: { ip: "1.2.3.4" } });
  console.log(`[e2e] irc_read: ${JSON.parse(irc.content[0].text).count} 条`);
  const board = await client.callTool({ name: "board_read", arguments: { ip: "1.2.3.4" } });
  console.log(`[e2e] board_read: ${JSON.parse(board.content[0].text).threadCount} 线程`);

  console.log("[e2e] 全部通过 ✔");
} finally {
  await client.close();
  fake.kill();
}
