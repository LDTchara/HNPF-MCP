// 真机验证脚本（游戏需在 KE 扩展会话中运行）
// 覆盖：RAM 修复 / get_processes / kill_exe / get_daemons / 文件四件套 / transfer_file /
//       analyze_firewall / solve_firewall / resources 可读 / ReadOnly 热加载生效
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(__dirname, "..");
const CFG = "D:\\Game\\Hacknet+DLC+Pathfinder\\BepInEx\\config\\com.HnpfMcp.Bridge.cfg";

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(root, "src", "index.js")],
});
const client = new Client({ name: "hnpf-real-verify", version: "0.0.1" });
await client.connect(transport);

let pass = 0, fail = 0;

// 若游戏停在主菜单：先新建账号进入 KE 测试扩展（游戏内桥只在 OS 会话里接受请求）
async function ensureSession() {
  let st = await call("get_state");
  if (!st.isError) return;
  console.log("[info] 主菜单/无会话 → menu_enter_extension 进入 KernelExtensionTEST123123 ...");
  const m = await call("menu_enter_extension", { username: "mcpverify2", ext: "KernelExtensionTEST123123" });
  for (let i = 0; i < 60; i++) {
    await new Promise((r) => setTimeout(r, 500));
    st = await call("get_state");
    if (!st.isError) { console.log(`[info] 已进入扩展会话（${(i + 1) * 0.5}s）`); return; }
  }
  throw new Error("进入扩展失败: " + (m.text || "").slice(0, 160));
}
await ensureSession();
async function call(name, args = {}) {
  const r = await client.callTool({ name, arguments: args });
  const text = r.content?.[0]?.text ?? "";
  let j = null;
  try { j = JSON.parse(text); } catch { /* 非 JSON */ }
  return { isError: r.isError === true, text, j };
}
function ok(label, extra = "") { pass++; console.log(`[PASS] ${label}${extra ? " :: " + extra : ""}`); }
function bad(label, extra = "") { fail++; console.log(`[FAIL] ${label}${extra ? " :: " + extra : ""}`); }

// 1) ping / build
{
  const r = await call("ping");
  if (!r.isError && /^\d{4}-\d{2}-\d{2}-\d{4}$/.test(r.j?.build ?? "") && r.j?.version === "0.2.0")
    ok("ping 新 bridge", `build=${r.j.build}`);
  else bad("ping 新 bridge", r.text.slice(0, 160));
}

// 2) RAM 修复
{
  const r = await call("get_state");
  const free = r.j?.ramFree, total = r.j?.ramTotal;
  if (!r.isError && total > 0 && free >= 0 && free <= total) ok("get_state RAM", `${free}/${total}`);
  else bad("get_state RAM", `free=${free} total=${total}`);
  if (r.j?.media?.theme) ok("get_state media", `${r.j.media.theme} / ${r.j.media.song}`);
  else bad("get_state media", JSON.stringify(r.j?.media));
}

// 3) get_processes
let processes = null;
{
  const r = await call("get_processes");
  processes = r.j;
  if (!r.isError && Array.isArray(r.j?.exes) && r.j.ramTotal > 0) {
    ok("get_processes", `${r.j.count} 进程 RAM ${r.j.ramFree}/${r.j.ramTotal}`);
  } else bad("get_processes", r.text.slice(0, 160));
}

// 4) get_daemons（本机 + 网络里挑一个有 daemon 的节点）
{
  const r = await call("get_daemons");
  if (!r.isError && Array.isArray(r.j?.daemons)) {
    ok("get_daemons 本机", `${r.j.count} 个: ${r.j.daemons.map((d) => d.type).join(", ") || "(无)"}`);
  } else bad("get_daemons 本机", r.text.slice(0, 160));

  const nm = await call("get_network_map", { summary: true });
  const nodes = nm.j?.nodes ?? [];
  let found = null;
  for (const n of nodes.slice(0, 15)) {
    const c = await call("get_computer", { ip: n.ip });
    if (!c.isError && c.j?.daemons?.length) { found = c; break; }
  }
  if (found) {
    const d = await call("get_daemons", { ip: found.j.ip });
    ok("get_daemons 远端", `${found.j.ip}: ${d.j?.count} 个 [${d.j?.daemons?.map((x) => x.type).join(", ")}]`);
  } else console.log("[SKIP] get_daemons 远端（网络里没找到带 daemon 的节点）");
}

// 5) 文件四件套 + transfer（在本机 root 下净零操作）
{
  const NAME = "mcp-verify.txt";
  const w1 = await call("write_file", { file: NAME, content: "hello-verify" });
  const w2 = await call("append_file", { file: NAME, content: "-line2" });
  const rd = await call("read_file", { file: NAME });
  const rp = await call("replace_in_file", { file: NAME, target: "hello", replacement: "HELLO" });
  const mv = await call("move_file", { file: NAME, toFile: "mcp-verify-moved.txt" });
  if (!w1.isError && !w2.isError && rd.j?.data === "hello-verify-line2") ok("文件 write/append/read", rd.j.data);
  else bad("文件 write/append/read", JSON.stringify({ w1: w1.text.slice(0, 80), w2: w2.text.slice(0, 80), rd: rd.text.slice(0, 120) }));
  if (!rp.isError && rp.j?.occurrences === 1) ok("replace_in_file", `occurrences=${rp.j.occurrences}`);
  else bad("replace_in_file", rp.text.slice(0, 120));
  if (!mv.isError && mv.j?.file === "mcp-verify-moved.txt") ok("move_file", mv.j.file);
  else bad("move_file", mv.text.slice(0, 120));

  // transfer 到网络里第一个非本机节点
  const nm = await call("get_network_map", { summary: true });
  const st = await call("get_state");
  const myIp = st.j?.thisComputerIP;
  const other = (nm.j?.nodes ?? []).find((n) => n.ip && n.ip !== myIp);
  if (other) {
    const tf = await call("transfer_file", { file: "mcp-verify-moved.txt", toIp: other.ip, toPath: "/bin", toFile: "mcp-verify-transfer.txt" });
    const chk = await call("read_file", { ip: other.ip, path: "/bin", file: "mcp-verify-transfer.txt" });
    if (!tf.isError && !chk.isError && chk.j?.data?.startsWith("HELLO")) ok("transfer_file", `→ ${other.ip}/bin (${chk.j.size}B)`);
    else bad("transfer_file", JSON.stringify({ tf: tf.text.slice(0, 100), chk: chk.text.slice(0, 100) }));
    // 清理远端文件
    await call("delete_file", { ip: other.ip, path: "/bin", file: "mcp-verify-transfer.txt" });
  } else console.log("[SKIP] transfer_file（没有其他节点）");

  const del = await call("delete_file", { file: "mcp-verify-moved.txt" });
  if (!del.isError && del.j?.deleted) ok("delete_file", "已清理");
  else bad("delete_file", del.text.slice(0, 120));
}

// 6) launch_exe + kill_exe（Notes 开/关）
{
  const le = await call("launch_exe", { exeName: "Notes" });
  await new Promise((r) => setTimeout(r, 600));
  const pr = await call("get_processes");
  const target = (pr.j?.exes ?? []).find((e) => /notes/i.test(e.identifierName + e.type + e.name));
  if (!le.isError && target) {
    const k = await call("kill_exe", { pid: target.pid });
    if (!k.isError && k.j?.ok) ok("kill_exe", `pid=${target.pid} (${target.identifierName})`);
    else bad("kill_exe", k.text.slice(0, 120));
    // 清理：可能还有残留窗口
    for (const e of (await call("get_processes")).j?.exes ?? []) {
      if (/notes/i.test(e.identifierName + e.type)) await call("kill_exe", { pid: e.pid });
    }
  } else bad("launch_exe + kill_exe", JSON.stringify({ le: le.text.slice(0, 80), exes: pr.j?.exes?.map((x) => x.identifierName) }));
}

// 7) analyze_firewall / solve_firewall：网络上找带防火墙的节点
{
  const nm = await call("get_network_map", { summary: true });
  let target = null;
  for (const n of (nm.j?.nodes ?? []).slice(0, 20)) {
    const c = await call("get_computer", { ip: n.ip });
    if (!c.isError && c.j?.firewall && c.j.firewall.solved === false) { target = c.j; break; }
  }
  if (target) {
    const an = await call("analyze_firewall", { ip: target.ip });
    const sol = (await call("get_computer", { ip: target.ip })).j?.firewall?.solution;
    const so = sol ? await call("solve_firewall", { ip: target.ip, solution: sol }) : null;
    if (!an.isError) ok("analyze_firewall", `${target.ip} passes=${an.j?.analysisPasses}`);
    else bad("analyze_firewall", an.text.slice(0, 120));
    if (so && !so.isError && so.j?.solved) ok("solve_firewall", `${target.ip} solved`);
    else if (so) bad("solve_firewall", so.text.slice(0, 160));
  } else console.log("[SKIP] analyze/solve_firewall（网络里没找到未解防火墙节点）");
}

// 8) resources 可读
{
  const list = await client.listResources();
  const uris = (list.resources ?? []).map((x) => x.uri);
  if (uris.includes("hnpf://state") && uris.includes("hnpf://network")) ok("resources/list", uris.join(", "));
  else bad("resources/list", JSON.stringify(uris));
  try {
    const rr = await client.readResource({ uri: "hnpf://state" });
    const j = JSON.parse(rr.contents[0].text);
    if (typeof j.connectedIP === "string" && j.homeNodeID) ok("readResource hnpf://state", `connectedIP="${j.connectedIP}" home=${j.homeNodeID}`);
    else bad("readResource hnpf://state", rr.contents[0].text.slice(0, 120));
  } catch (e) {
    bad("readResource hnpf://state", String(e.message).slice(0, 160));
  }
}

// 8.5) 原版小能力 + 成就 + 分支任务
{
  // reboot 要打远程节点（重启本机会把当前 OS 会话重启掉）
  const stNow = await call("get_state");
  const nmNow = await call("get_network_map", { summary: true });
  const otherNode = (nmNow.j?.nodes ?? []).find((n) => n.ip && n.ip !== stNow.j?.thisComputerIP);
  let rebootTarget = null;
  if (otherNode) {
    const c = await call("connect", { ip: otherNode.ip });
    if (!c.isError) rebootTarget = otherNode.ip;
  }
  if (rebootTarget) {
    const rb = await call("reboot_computer", { instant: false });
    if (!rb.isError) ok("reboot_computer", `target=${rb.j?.target}`);
    else bad("reboot_computer", rb.text.slice(0, 140));
    await call("disconnect");
  } else console.log("[SKIP] reboot_computer（没有可连接的远程节点）");
  await new Promise((r) => setTimeout(r, 800));
  const ct = await call("set_cd_tray", { open: true });
  if (!ct.isError) ok("set_cd_tray", `open=${ct.j?.open}`);
  else bad("set_cd_tray", ct.text.slice(0, 140));
  const ct2 = await call("set_cd_tray", { open: false });
  const cl = await call("clear_terminal");
  if (!cl.isError) ok("clear_terminal", "");
  else bad("clear_terminal", cl.text.slice(0, 140));
  const an = await call("add_note", { text: "mcp-verify note" });
  if (!an.isError) ok("add_note", "");
  else bad("add_note", an.text.slice(0, 140));
  const sn = await call("scan_network");
  if (!sn.isError) ok("scan_network", `target=${sn.j?.target}`);
  else bad("scan_network", sn.text.slice(0, 140));
  const pt = await call("probe_target");
  if (!pt.isError) ok("probe_target", `target=${pt.j?.target}`);
  else bad("probe_target", pt.text.slice(0, 140));
  await new Promise((r) => setTimeout(r, 1200));
  const ac = await call("get_achievements");
  if (!ac.isError && Array.isArray(ac.j?.unlocked)) ok("get_achievements", `${ac.j.unlocked.length} 个`);
  else bad("get_achievements", ac.text.slice(0, 140));
  const gm = await call("get_mission");
  if (!gm.isError && Array.isArray(gm.j?.branchMissions)) ok("get_mission branchMissions", `${gm.j.branchMissions.length} 个`);
  else bad("get_mission branchMissions", gm.text.slice(0, 140));
}

// 8.6) L3 connector（KE 测试扩展内应注册 13 个 ke.*）
{
  const ml = await call("modtool_list");
  const tools = ml.j?.tools ?? [];
  const keTools = tools.filter((t) => (t.name ?? "").startsWith("ke."));
  if (keTools.length === 13) ok("modtool_list ke.*", `${keTools.length} 个`);
  else bad("modtool_list ke.*", `期望 13，实际 ${keTools.length}: ${keTools.map((t) => t.name).join(",")}`);

  const keCalls = [
    "ke.phaseswift.state", "ke.phaseswift.detail", "ke.aircraft.state", "ke.customtrial.state",
    "ke.vm.state", "ke.porthackv2.state", "ke.proxykiller.state", "ke.config.get",
    "ke.flag.find", "ke.clock.state", "ke.color.state", "ke.titlebanner.state", "ke.ending.state",
  ];
  for (const name of keCalls) {
    const args = name === "ke.flag.find" ? { tool: name, params: { prefix: "" } } : { tool: name };
    const r = await call("modtool_call", args);
    const result = r.j?.result ?? r.j;
    if (!r.isError) ok(name, JSON.stringify(result).slice(0, 90));
    else bad(name, r.text.slice(0, 140));
  }
}

// 9) ReadOnly 热加载：改 cfg → 尝试写入应被拒 → 改回
{
  const orig = fs.readFileSync(CFG, "utf8");
  const withTop = orig.replace("ReadOnly = false", "ReadOnly = true");
  fs.writeFileSync(CFG, withTop);
  await new Promise((r) => setTimeout(r, 1500)); // FileSystemWatcher 400ms 去抖 + reload
  const w = await call("write_file", { file: "mcp-readonly-test.txt", content: "x" });
  if (w.isError && /read-only/i.test(w.text)) ok("ReadOnly 热加载拒绝写", w.text.slice(0, 80));
  else bad("ReadOnly 热加载拒绝写", w.text.slice(0, 160));
  const rd = await call("read_file", { file: "mcp-readonly-test.txt" });
  if (rd.isError) ok("ReadOnly 下读不受影响（文件未创建，符合预期）", "");
  // 恢复 cfg 并确认写入恢复
  fs.writeFileSync(CFG, orig);
  await new Promise((r) => setTimeout(r, 1500));
  const w2 = await call("write_file", { file: "mcp-readonly-test.txt", content: "y" });
  if (!w2.isError) { ok("ReadOnly 关闭后写入恢复", ""); await call("delete_file", { file: "mcp-readonly-test.txt" }); }
  else bad("ReadOnly 关闭后写入恢复", w2.text.slice(0, 160));
}

await client.close();
console.log(`\n===== 真机验证：${pass} 通过 / ${fail} 失败 =====`);
if (fail > 0) process.exitCode = 1;
