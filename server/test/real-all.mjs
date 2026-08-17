// ============================================================================
// P0-P3 完整真机回归测试
// 只读 + 无害操作；写操作（open_port 成功路径/take_admin/set_flag/save_game）
// 需显式设环境变量 ENABLE_WRITE=1 才会执行（会改变游戏状态）。
// 自动探测 bridge 版本：modtool.list 报 unknown method = P2 及以下。
// ============================================================================
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ENABLE_WRITE = process.env.ENABLE_WRITE === "1";

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(__dirname, "..", "src", "index.js")],
});
const client = new Client({ name: "hnpf-full", version: "0.0.1" });
await client.connect(transport);

const results = [];
async function t(name, fn) {
  try {
    const r = await fn();
    const text = typeof r === "string" ? r : JSON.stringify(r, null, 1);
    results.push({ name, pass: true, text });
    console.log(`[PASS] ${name}`);
  } catch (e) {
    results.push({ name, pass: false, text: e.message });
    console.log(`[FAIL] ${name} :: ${e.message}`);
  }
}

async function call(name, args = {}) {
  const r = await client.callTool({ name, arguments: args });
  return r.content?.[0]?.text ?? JSON.stringify(r);
}

// 安全解析：非 JSON 响应（如 SDK 错误文本）显示原文前 120 字符
function safeParse(text, label) {
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${label} 非 JSON 响应: ${String(text).slice(0, 120)}`);
  }
}

console.log(`========== HNPF-MCP P0-P3 完整回归（${ENABLE_WRITE ? "含写操作" : "只读模式"}）==========\n`);

// ---------- 版本探测 ----------
// bridge 的 unknown method 错误会以文本内容返回（非异常），需按内容判断
let bridgeP3 = false;
try {
  const txt = await call("modtool_list", {});
  bridgeP3 = !txt.includes("unknown method: modtool.list");
} catch { bridgeP3 = false; }
console.log(`bridge 版本探测: ${bridgeP3 ? "P3+（modtool_list 可用）" : "≤P2（modtool_list 返回 unknown method）"}\n`);

// 补漏版本探测（run_hack_script 等新方法）
let bridgeFull = false;
try {
  const txt = await call("save_list", {});
  bridgeFull = !txt.includes("unknown method: save.list");
} catch { bridgeFull = false; }
if (!bridgeFull) {
  console.log("[提示] bridge 为旧版（无 run_hack_script/mail_list/save.list）——请用最新的 HnpfMcpBridge.dll 替换 BepInEx/plugins/ 并重启游戏\n");
}

// ---------- P0 基础 ----------
console.log("---- P0 基础 ----");
await t("ping", async () => JSON.parse(await call("ping")));
await t("get_state", async () => {
  const s = JSON.parse(await call("get_state"));
  return { ip: s.connectedIP, comp: s.connectedName, flags: s.flags.length };
});
await t("get_network_map", async () => {
  const m = JSON.parse(await call("get_network_map"));
  return `${m.count} 节点`;
});
await t("get_computer(当前)", async () => {
  const c = JSON.parse(await call("get_computer", {}));
  return `${c.name} 用户=${c.users.length} 端口=${c.ports.length}`;
});
await t("list_files(当前,/bin)", async () => {
  const f = JSON.parse(await call("list_files", { path: "/bin" }));
  return `${f.entries.length} 项`;
});
await t("get_flags", async () => {
  const f = JSON.parse(await call("get_flags"));
  return `${f.flags.length} 个`;
});
await t("get_mission", async () => {
  const m = JSON.parse(await call("get_mission"));
  return m.active ? `active:${m.title || "(空标题)"}` : "无任务";
});

// ---------- P1 渗透工具（无害部分） ----------
console.log("\n---- P1 渗透工具（无害部分）----");
await t("terminal_history", async () => {
  const h = JSON.parse(await call("terminal_history", { lines: 5 }));
  return `${h.count} 行`;
});
await t("run_action <TerminalWrite>", async () => {
  const r = JSON.parse(await call("run_action", { xml: "<TerminalWrite Content='[full-regression] hello'/>" }));
  return `action=${r.action}`;
});

// ---------- P1.5 补漏（run_hack_script / 邮件 / 存档列表） ----------
console.log("\n---- P1.5 补漏 ----");
if (!bridgeFull) {
  console.log("[SKIP] bridge 为旧版，跳过 P1.5（更新 bridge DLL 后重跑）");
} else {
  // run_hack_script：真机脚本有破坏性（ThemeHack 含 forkbomb/delete），用报错路径验证机制与路径解析
  await t("run_hack_script(报错路径验证)", async () => {
    const text = await call("run_hack_script", { script: "HackerScripts/__definitely_missing__.txt" });
    if (!text.includes("Could not find file")) throw new Error("未得到预期文件错误: " + text.slice(0, 120));
    return "预期文件错误 ✓（机制与路径解析正常）";
  });
  // mail_list：先走 netMap.mailServer 权威引用（无参调用），再遍历全部节点；找不到验证报错路径
  await t("mail_list(自动探测或报错路径)", async () => {
    try {
      const ml0 = safeParse(await call("mail_list", {}), "mail_list");
      if (!ml0.users) throw new Error("mail_list 无 users");
      return `${ml0.ip} MailServer：${ml0.users.length} 账户（成功路径 ✓）`;
    } catch { /* 无默认邮件服务器，继续遍历 */ }
    const net = safeParse(await call("get_network_map", {}), "get_network_map");
    for (const node of net.nodes) {
      try {
        const ml = safeParse(await call("mail_list", { ip: node.ip }), "mail_list");
        if (!ml.users) throw new Error("mail_list 无 users");
        return `${node.ip} MailServer：${ml.users.length} 账户（成功路径 ✓）`;
      } catch { /* 无 MailServer，继续 */ }
    }
    const text = await call("mail_list", {});
    if (!text.includes("no MailServer daemon")) throw new Error("未得到预期错误: " + text.slice(0, 120));
    return "全网无 MailServer，报错路径 ✓";
  });
  await t("save_list", async () => {
    const r = safeParse(await call("save_list", {}), "save_list");
    return `${r.saves?.length ?? 0} 个存档`;
  });
}

if (ENABLE_WRITE) {
  console.log("\n---- P1 写操作（ENABLE_WRITE=1）----");
  await t("open_port 报错路径(玩家本机无端口)", async () => {
    const text = await call("open_port", { port: 9999 });
    if (!text.includes("only existing ports")) throw new Error("未得到预期报错: " + text.slice(0, 120));
    return "预期报错 ✓";
  });
  await t("set_flag(临时回归测试flag)", async () => {
    const r = JSON.parse(await call("set_flag", { name: "__hnpf_regression_test__" }));
    return r;
  });
  await t("clear_flag(清理)", async () => {
    const r = JSON.parse(await call("clear_flag", { name: "__hnpf_regression_test__" }));
    return r;
  });
}

// ---------- P2 事件/注册表 ----------
console.log("\n---- P2 事件/注册表 ----");
await t("get_events(增量)", async () => {
  const e = JSON.parse(await call("get_events", { since: 0 }));
  return `${e.events.length} 条, nextId=${e.nextId}`;
});
await t("registry(模组能力发现)", async () => {
  const r = JSON.parse(await call("registry"));
  const withDesc = r.actions.filter((a) => a.description).length;
  const descNote = withDesc > 0 ? `；${withDesc} 个 action 带 XML 文档描述 ✓` : "（无 description：模组未开 XML 文档或注释缺失，不影响功能）";
  return `actions=${r.actions.length} executables=${r.executables.length} daemons=${r.daemons.length} commands=${r.commands.length}${descNote}`;
});

// ---------- P3 McpTool（P3 DLL 时） ----------
console.log("\n---- P3 McpTool ----");
if (bridgeP3) {
  await t("modtool_list", async () => {
    const r = JSON.parse(await call("modtool_list"));
    return `${r.count} 个模组工具: ${r.tools.map((x) => x.name).join(", ")}`;
  });
  const list = JSON.parse(await call("modtool_list"));
  if (list.tools?.length > 0) {
    await t("modtool_call(第一个工具)", async () => {
      const r = JSON.parse(await call("modtool_call", { tool: list.tools[0].name }));
      return r;
    });
  }
} else {
  console.log("[SKIP] 需要 P3 DLL（modtool.list 不可用），请先替换 BepInEx/plugins/HnpfMcpBridge.dll");
}

// ---------- 汇总 ----------
const pass = results.filter((r) => r.pass).length;
const fail = results.filter((r) => !r.pass).length;
console.log(`\n========== 汇总: ${pass} 通过 / ${fail} 失败 ==========`);
if (fail > 0) {
  console.log("\n失败详情:");
  for (const r of results.filter((x) => !x.pass)) console.log(`  ✗ ${r.name}: ${r.text}`);
}

await client.close();
process.exit(fail > 0 ? 1 : 0);
