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

  const rb = await client.callTool({
    name: "run_batch",
    arguments: {
      steps: [
        { tool: "execute_command", args: { cmd: "ls" } },
        { tool: "connect", args: { ip: "10.0.0.2" } },
        { tool: "set_flag", args: { name: "e2e_batch" } },
      ],
    },
  });
  const rbj = JSON.parse(rb.content[0].text);
  console.log(`[e2e] run_batch: ok=${rbj.ok} count=${rbj.count}`);
  const rbErr = await client.callTool({
    name: "run_batch",
    arguments: { steps: [{ tool: "get_state", args: {} }, { tool: "set_flag", args: { name: "x" } }] },
  });
  const rbe = JSON.parse(rbErr.content[0].text);
  console.log(`[e2e] run_batch 失败中断: ok=${rbe.ok} count=${rbe.count}（应 1）`);

  const fw = await client.callTool({ name: "firewall_crack", arguments: { ip: "10.0.0.5" } });
  console.log(`[e2e] firewall_crack: ${JSON.parse(fw.content[0].text).solved}`);
  const sm = await client.callTool({ name: "submit_mission", arguments: { sender: "boss@test", details: "answer1\nanswer2" } });
  console.log(`[e2e] submit_mission: ${JSON.parse(sm.content[0].text).completed}`);
  const hub = await client.callTool({ name: "hub_list", arguments: {} });
  const hj = JSON.parse(hub.content[0].text);
  console.log(`[e2e] hub_list: ${hj.hubs[0].daemon} 任务 ${hj.hubs[0].missions.length} 个`);
  const ha = await client.callTool({ name: "hub_accept", arguments: { id: "contract:001" } });
  console.log(`[e2e] hub_accept: ${JSON.parse(ha.content[0].text).accepted}`);
  const sd = await client.callTool({ name: "shell_drive", arguments: { action: "overload" } });
  console.log(`[e2e] shell_drive: ${JSON.parse(sd.content[0].text).action}`);
  const tt = await client.callTool({ name: "terminal_type", arguments: { text: "hello" } });
  console.log(`[e2e] terminal_type: ${JSON.parse(tt.content[0].text).typed}`);
  const om = await client.callTool({ name: "os_memory", arguments: {} });
  console.log(`[e2e] os_memory fragments: ${JSON.stringify(JSON.parse(om.content[0].text).memory.fileFragments)}`);

  const pr = await client.callTool({ name: "get_processes", arguments: {} });
  const prj = JSON.parse(pr.content[0].text);
  console.log(`[e2e] get_processes: ${prj.count} 进程, RAM ${prj.ramFree}/${prj.ramTotal}`);
  const dk = await client.callTool({ name: "kill_exe", arguments: { pid: 2 } });
  console.log(`[e2e] kill_exe: ok=${JSON.parse(dk.content[0].text).ok}`);
  const da = await client.callTool({ name: "get_daemons", arguments: {} });
  const daj = JSON.parse(da.content[0].text);
  console.log(`[e2e] get_daemons: ${daj.count} 个 [${daj.daemons.map((d) => d.type).join(", ")}]`);
  const fa = await client.callTool({ name: "analyze_firewall", arguments: {} });
  console.log(`[e2e] analyze_firewall: passes=${JSON.parse(fa.content[0].text).analysisPasses}`);
  const fsv = await client.callTool({ name: "solve_firewall", arguments: { solution: "000000" } });
  console.log(`[e2e] solve_firewall: solved=${JSON.parse(fsv.content[0].text).solved}`);
  const df = await client.callTool({ name: "delete_file", arguments: { file: "x.txt" } });
  console.log(`[e2e] delete_file: ${JSON.parse(df.content[0].text).deleted}`);
  const mf = await client.callTool({ name: "move_file", arguments: { file: "a.txt", toFile: "b.txt" } });
  console.log(`[e2e] move_file: ${JSON.parse(mf.content[0].text).file}`);
  const rf = await client.callTool({ name: "replace_in_file", arguments: { file: "a.txt", target: "x", replacement: "y" } });
  console.log(`[e2e] replace_in_file: ${JSON.parse(rf.content[0].text).occurrences} 处`);
  const tf = await client.callTool({ name: "transfer_file", arguments: { file: "passwd", toPath: "/home" } });
  console.log(`[e2e] transfer_file: ${JSON.parse(tf.content[0].text).ok}`);

  // resources 实际可读（之前注册参数错位导致不可读）
  const rr = await client.readResource({ uri: "hnpf://state" });
  const rrj = JSON.parse(rr.contents[0].text);
  console.log(`[e2e] readResource hnpf://state: connectedIP=${rrj.connectedIP}`);

  const rb2 = await client.callTool({ name: "reboot_computer", arguments: { instant: true } });
  console.log(`[e2e] reboot_computer: instant=${JSON.parse(rb2.content[0].text).instant}`);
  const ct = await client.callTool({ name: "set_cd_tray", arguments: { open: true } });
  console.log(`[e2e] set_cd_tray: open=${JSON.parse(ct.content[0].text).open}`);
  const cl = await client.callTool({ name: "clear_terminal", arguments: {} });
  console.log(`[e2e] clear_terminal: ${JSON.parse(cl.content[0].text).ok}`);
  const an = await client.callTool({ name: "add_note", arguments: { text: "e2e note" } });
  console.log(`[e2e] add_note: ${JSON.parse(an.content[0].text).text}`);
  const sn = await client.callTool({ name: "scan_network", arguments: {} });
  console.log(`[e2e] scan_network: ${JSON.parse(sn.content[0].text).target}`);
  const pt = await client.callTool({ name: "probe_target", arguments: {} });
  console.log(`[e2e] probe_target: ${JSON.parse(pt.content[0].text).target}`);
  const ac = await client.callTool({ name: "get_achievements", arguments: {} });
  const acj = JSON.parse(ac.content[0].text);
  console.log(`[e2e] get_achievements: ${acj.count} 个 [${acj.unlocked.join(", ")}]`);

  // run_batch 一致性：execute_command 的 waitMs 生效
  const rbw = await client.callTool({
    name: "run_batch",
    arguments: { steps: [{ tool: "execute_command", args: { cmd: "ls", waitMs: 50 } }] },
  });
  const rbwj = JSON.parse(rbw.content[0].text);
  console.log(`[e2e] run_batch waitMs: output.lines=${rbwj.results?.[0]?.result?.output?.lines?.length}`);

  console.log("[e2e] 全部通过 ✔");
} finally {
  await client.close();
  fake.kill();
}
