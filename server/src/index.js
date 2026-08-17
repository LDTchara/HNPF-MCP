import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { fileURLToPath } from "node:url";
import path from "node:path";
import fs from "node:fs";
import { spawn } from "node:child_process";
import { BridgeClient } from "./bridge.js";

const GAME_EXE = process.env.HNPF_GAME || "D:\\Game\\Hacknet+DLC+Pathfinder\\Hacknet.exe";

const PIPE_NAME = process.env.HNPF_PIPE || "\\\\.\\pipe\\hnpf-mcp-bridge";
const PIPE_TOKEN = process.env.HNPF_TOKEN || "";

const bridge = new BridgeClient({ pipeName: PIPE_NAME, token: PIPE_TOKEN });
let bridgeReady = false;
let bridgeVersion = null;
let bridgePipe = null;

async function ensureBridge() {
  if (bridge.connected) return;
  await bridge.connect();
  // 协议版本探测（C3）：握手后 ping 一次取 bridge 版本/实际管道名，get_state 里暴露
  if (bridgeVersion == null) {
    try {
      const p = await bridge.call("ping", {});
      bridgeVersion = p?.version || null;
      bridgePipe = p?.pipe || null;
    } catch { /* 探测失败不阻塞 */ }
  }
}

// ---------------- 审计日志（HNPF_AUDIT=off 关闭；写 server/audit.log） ----------------
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const AUDIT_FILE = path.join(__dirname, "..", "audit.log");
const AUDIT_KEYS = ["cmd", "ip", "port", "tool", "exeName", "script", "user", "name", "prefix"];

function audit(method, params) {
  try {
    if (process.env.HNPF_AUDIT === "off") return;
    const pick = {};
    for (const k of AUDIT_KEYS) {
      if (params?.[k] !== undefined) pick[k] = String(params[k]).slice(0, 80);
    }
    const line = `${new Date().toISOString()}  ${method}  ${JSON.stringify(pick)}\n`;
    void import("node:fs").then((fs) => fs.appendFile(AUDIT_FILE, line, () => {}));
  } catch { /* 审计失败不影响调用 */ }
}

/** 调用 bridge；仅连接类错误提示游戏离线，业务错误原样透传 */
async function call(method, params) {
  audit(method, params);
  try {
    await ensureBridge();
    return await bridge.call(method, params);
  } catch (err) {
    const msg = err.message || String(err);
    if (/not connected|ENOENT|ECONNREFUSED|pipe closed|timeout/i.test(msg)) {
      throw new Error(`[game offline] ${msg} — 请先启动 Hacknet (Pathfinder) 并加载 HnpfMcpBridge 插件`);
    }
    throw new Error(msg);
  }
}

const server = new McpServer({
  name: "hnpf-mcp-server",
  version: "0.1.0",
});

// ---------------- 查询类 ----------------

server.tool(
  "ping",
  "检测游戏侧 bridge 是否在线",
  {},
  async () => {
    const r = await call("ping");
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "get_state",
  "玩家当前整体状态：连接节点、路径、RAM、Flag、任务、管理员权限（含 bridge 协议版本）",
  {},
  async () => {
    const r = await call("state.get");
    return { content: [{ type: "text", text: JSON.stringify({ ...r, bridgeVersion, bridgePipe, serverVersion: "0.1.0" }, null, 2) }] };
  }
);

server.tool(
  "get_network_map",
  "获取全网络节点列表（IP/名称/链接/端口/用户数）",
  {},
  async () => {
    const r = await call("network.map");
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "get_computer",
  "查询单个节点详情（用户、端口、守护进程、文件树）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点") },
  async ({ ip }) => {
    const r = await call("computer.get", { ip });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "list_files",
  "列出指定节点指定目录的文件树",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点"), path: z.string().optional().describe("目录路径，默认 /") },
  async ({ ip, path }) => {
    const r = await call("fs.list", { ip, path: path ?? "/" });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "read_file",
  "读取文件内容",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点"), path: z.string().optional().describe("所在目录路径，默认 /"), file: z.string().describe("文件名") },
  async ({ ip, path, file }) => {
    const r = await call("fs.read", { ip, path, file });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "get_flags",
  "获取游戏进度 Flag 列表",
  {},
  async () => {
    const r = await call("flags.get");
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "get_mission",
  "当前任务信息（标题、目标完成进度）",
  {},
  async () => {
    const r = await call("mission.get");
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "mission_detail",
  "任务详情：各目标类型与完成状态 + 任务 XML 全文（含目标参数/函数，便于调试任务卡点）",
  {},
  async () => {
    const r = await call("mission.detail");
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

// ---------------- 操作类 ----------------

server.tool(
  "execute_command",
  "在游戏内终端执行命令（connect/cd/ls/cat/scp/sudo 等）。输出显示在游戏内终端；传 waitMs>0 时等待并连同 terminal_history 一起返回（同步输出）。",
  {
    cmd: z.string().describe("要执行的终端命令，如 'connect 10.0.0.2'"),
    waitMs: z.number().optional().describe("等待毫秒数后抓取命令输出一并返回（默认 0=异步，建议 400-800；长命令如 scp 大文件可加大）"),
  },
  async ({ cmd, waitMs }) => {
    const r = await call("game.execute_command", { cmd });
    if (waitMs > 0) {
      await new Promise((res) => setTimeout(res, waitMs));
      const h = await call("terminal.history", { lines: 10 });
      return {
        content: [{
          type: "text",
          text: JSON.stringify({ submitted: r, output: h }, null, 2),
        }],
      };
    }
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "connect",
  "连接指定节点",
  { ip: z.string().describe("目标 IP") },
  async ({ ip }) => {
    const r = await call("game.connect", { ip });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "disconnect",
  "断开当前连接，回到本机",
  {},
  async () => {
    const r = await call("game.disconnect");
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "open_port",
  "打开目标节点端口（渗透提权关键步骤）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点"), port: z.number().describe("端口号，如 22") },
  async ({ ip, port }) => {
    const r = await call("port.open", { ip, port });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "close_port",
  "关闭目标节点端口",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点"), port: z.number() },
  async ({ ip, port }) => {
    const r = await call("port.close", { ip, port });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "take_admin",
  "获取目标节点管理员权限（需先满足端口/防火墙条件）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点") },
  async ({ ip }) => {
    const r = await call("admin.take", { ip });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "launch_exe",
  "在游戏内启动 exe（内置 porthack/forkbomb/shell 或 Pathfinder 自定义 #NAME#）",
  { exeName: z.string().describe("exe 名称，如 porthack / #CUSTOMTRIAL#"), args: z.string().optional().describe("附加参数") },
  async ({ exeName, args }) => {
    const r = await call("game.launch_exe", { exeName, args });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "run_action",
  "泛化执行 Pathfinder 动作 XML（SA 动作；KE 等模组注册的自定义 Action 自动可用）。例：<PlaySound Sound='x'/>",
  { xml: z.string().describe("动作 XML，如 <PlaySound Sound='file.ogg'/> 或 <TerminalWrite Content='hi'/>") },
  async ({ xml }) => {
    const r = await call("game.run_action", { xml });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "terminal_history",
  "读取游戏终端最近输出（命令结果在这里看）",
  { lines: z.number().optional().describe("行数，默认 15，最多 30") },
  async ({ lines }) => {
    const r = await call("terminal.history", { lines: lines ?? 15 });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "run_hack_script",
  "执行黑客脚本（载体是 .txt：Content/HackerScripts/*.txt 或扩展 HackerScripts/，如 HackerScripts/ThemeHack.txt）",
  { script: z.string().describe("脚本路径，相对游戏 Content 目录，如 HackerScripts/ThemeHack.txt") },
  async ({ script }) => {
    const r = await call("game.run_hack_script", { script });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "mail_list",
  "列出指定节点邮件服务器的账户与邮件（主题/大小）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点") },
  async ({ ip }) => {
    const r = await call("mail.list", { ip });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "mail_read",
  "读取一封邮件的完整内容（发件人/正文）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点"), user: z.string().describe("账户名"), folder: z.string().optional().describe("邮箱夹，默认 inbox"), subject: z.string().describe("主题（即邮件文件名）") },
  async ({ ip, user, folder, subject }) => {
    const r = await call("mail.read", { ip, user, folder, subject });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "save_list",
  "列出可用存档 + 项目内快照（注意：load_game 在游戏运行时无原生支持，存档只在启动时加载）",
  {},
  async () => {
    const r = await call("save.list");
    const snapshots = listSnapshots();
    return { content: [{ type: "text", text: JSON.stringify({ ...r, snapshots }, null, 2) }] };
  }
);

server.tool(
  "get_events",
  "增量拉取游戏事件（node.connected/disconnected、command.executed、mission.changed、game.loaded/saved）。传上次返回的 nextId 作为 since。",
  { since: z.number().optional().describe("上次 nextId，省略则返回全部缓冲") },
  async ({ since }) => {
    const r = await call("events.get", { since: since ?? 0 });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "registry",
  "列出所有模组通过 Pathfinder 注册表注册的能力（命令/动作/exe/daemon）——模组无需写 MCP 代码，能力自动可发现",
  {},
  async () => {
    const r = await call("registry.list");
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "modtool_list",
  "列出模组通过 [McpTool] 特性注册的专属工具（第三层抽象，如 KE 的 PhaseSwift/CustomTrial/VM 状态）",
  {},
  async () => {
    const r = await call("modtool.list");
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "modtool_call",
  "调用模组 [McpTool] 专属工具",
  { tool: z.string().describe("工具名，先 modtool_list 查看"), params: z.record(z.string(), z.any()).optional().describe("参数") },
  async ({ tool, params }) => {
    const r = await call("modtool.call", { tool, ...(params ?? {}) });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "write_file",
  "写入/覆盖文件（不存在则创建）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点"), path: z.string().optional().describe("目录路径，默认 /"), file: z.string(), content: z.string() },
  async ({ ip, path, file, content }) => {
    const r = await call("fs.write", { ip, path, file, content });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "append_file",
  "追加内容到文件（不存在则创建）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接节点"), path: z.string().optional(), file: z.string(), content: z.string() },
  async ({ ip, path, file, content }) => {
    const r = await call("fs.append", { ip, path, file, content });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "set_flag",
  "设置进度 Flag（如驱动 PhaseSwift/KE 场景）",
  { name: z.string() },
  async ({ name }) => {
    const r = await call("flags.set", { name });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "clear_flag",
  "清除进度 Flag",
  { name: z.string() },
  async ({ name }) => {
    const r = await call("flags.clear", { name });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "irc_read",
  "读取目标节点 IRC 消息日志（IRCDaemon 历史消息，情报源；ip 省略=当前连接/本机）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接/本机") },
  async ({ ip }) => {
    const r = await call("irc.read", { ip });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "board_read",
  "读取目标节点论坛（MessageBoard）线程列表与内容（情报源；ip 省略=当前连接/本机）",
  { ip: z.string().optional().describe("目标 IP；省略则当前连接/本机") },
  async ({ ip }) => {
    const r = await call("board.read", { ip });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "save_game",
  "请求游戏存档，并自动在 HNPF-MCP/snapshots/ 生成时间戳快照（保留最近 20 份，不进原版存档路径）",
  {},
  async () => {
    const r = await call("game.save");
    const snapshot = await takeSnapshot();
    return { content: [{ type: "text", text: JSON.stringify({ ...r, snapshot }, null, 2) }] };
  }
);

// ---------------- 存档快照（HNPF-MCP/snapshots/，项目内，不碰原版存档路径） ----------------
const SNAPSHOT_DIR = path.join(__dirname, "..", "..", "snapshots");
const SNAPSHOT_KEEP = 20;

function listSnapshots() {
  try {
    if (!fs.existsSync(SNAPSHOT_DIR)) return [];
    return fs.readdirSync(SNAPSHOT_DIR)
      .filter((f) => f.endsWith(".xml"))
      .sort()
      .reverse()
      .map((f) => ({ name: f, path: path.join(SNAPSHOT_DIR, f), snapshot: true }));
  } catch { return []; }
}

async function takeSnapshot() {
  try {
    fs.mkdirSync(SNAPSHOT_DIR, { recursive: true });
    const list = await call("save.list");
    const saves = list.saves || [];
    if (saves.length === 0) return { ok: false, reason: "no save files found" };
    // 优先当前会话存档：current 标记且所在扩展 == 当前扩展 → 其次任意 current（优先带扩展的）→ 兜底最新修改
    const current = saves.filter((s) => s.current);
    let pick = null;
    if (list.currentExtension) {
      pick = current.find((s) => s.extension === list.currentExtension) || null;
    }
    if (!pick) {
      pick = current.find((s) => s.extension) || current.find((s) => !s.extension) || null;
    }
    if (!pick) {
      let best = null;
      for (const s of saves) {
        try {
          const st = fs.statSync(s.path);
          if (!best || st.mtimeMs > best.mtimeMs) best = { ...s, mtimeMs: st.mtimeMs };
        } catch { /* 存档文件不可读，跳过 */ }
      }
      pick = best;
    }
    if (!pick) return { ok: false, reason: "no readable save files" };
    const ts = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
    const extTag = pick.extension ? `-${pick.extension}` : "";
    const dest = path.join(SNAPSHOT_DIR, `save-${ts}${extTag}.xml`);
    fs.copyFileSync(pick.path, dest);
    // 清理：保留最近 SNAPSHOT_KEEP 份
    const snaps = fs.readdirSync(SNAPSHOT_DIR).filter((f) => f.endsWith(".xml")).sort();
    while (snaps.length > SNAPSHOT_KEEP) {
      fs.unlinkSync(path.join(SNAPSHOT_DIR, snaps.shift()));
    }
    return { ok: true, file: dest, source: pick.name, extension: pick.extension || null, current: !!pick.current, total: Math.min(snaps.length + 1, SNAPSHOT_KEEP) };
  } catch (e) {
    return { ok: false, reason: String(e.message || e).slice(0, 120) };
  }
}

// ---------------- 主菜单进扩展（需游戏处于主菜单） ----------------

server.tool(
  "menu_enter_extension",
  "在主菜单新建账号并进入扩展（程序化调用主菜单进扩展逻辑，免 UI 点击；需游戏处于主菜单）。username 省略默认 mcp。",
  {
    username: z.string().optional().describe("新建账号名（SaveFileManager 会创建）"),
    pass: z.string().optional().describe("账号密码（省略自动生成）"),
  },
  async ({ username, pass }) => {
    const r = await call("menu.enter_extension", { username, pass });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

server.tool(
  "menu_load_extension_save",
  "在主菜单用存档账号进入扩展（恢复进度，对应 ExtensionsMenuScreen 的读档进扩展；需游戏处于主菜单）。userFile 取 save_list 的存档路径。",
  {
    userFile: z.string().describe("存档 XML 完整路径（save_list 返回的 path）"),
    username: z.string().describe("存档用户名（save_list 返回的 name 去掉 save_ 前缀和 .xml）"),
  },
  async ({ userFile, username }) => {
    const r = await call("menu.load_extension_save", { userFile, username });
    return { content: [{ type: "text", text: JSON.stringify(r, null, 2) }] };
  }
);

// ---------------- 游戏启动（自动化） ----------------

/** 进程检测 Hacknet 是否在运行：管道探测优先（bridge 在监听=真在跑），
 * 其次 tasklist 进程+内存过滤（正常 Hacknet 占几百 MB；僵尸/空壳进程 20K 不算）。 */
function isGameRunning() {
  return new Promise((resolve) => {
    (async () => {
      try { await ensureBridge(); if (bridge.connected) return resolve(true); } catch { /* 管道不可连 */ }
      try {
        const p = spawn("tasklist", ["/FI", "IMAGENAME eq Hacknet.exe"], { windowsHide: true });
        let out = "";
        p.stdout.on("data", (d) => (out += d.toString()));
        p.on("close", () => {
          const m = out.match(/Hacknet\.exe\s+\d+\s+\S+\s+\d+\s+([\d,]+)\s*K/gi);
          if (!m) return resolve(false);
          for (const line of m) {
            const kb = parseInt(line.match(/([\d,]+)\s*K/)[1].replace(/,/g, ""), 10) || 0;
            if (kb > 51200) return resolve(true);   // > 50MB 才视为真运行
          }
          resolve(false);   // 全部是僵尸/空壳进程
        });
        p.on("error", () => resolve(false));
      } catch { resolve(false); }
    })();
  });
}

/**
 * 后台自动进扩展（替代 -extstart 直进）：正常启动到主菜单 → 等 bridge 管道就绪 →
 * 调 menu.enter_extension（MenuExecutor 在主菜单阶段执行，插件真正加载）。
 * -extstart 会跳过主菜单流程导致扩展插件（KE 等）加载异常/退出不卸载——已弃用。
 */
function scheduleAutoEnterExtension(ext, { username, pass } = {}) {
  (async () => {
    let attempts = 0;
    while (attempts++ < 30) {   // 最长 ~150s
      await new Promise((r) => setTimeout(r, 5000));
      try {
        await ensureBridge();
        // 主菜单阶段 MenuExecutor 处理 menu.enter_extension；进扩展后 OS 会话接管，
        // 若已进扩展（响应非 ok）则停止
        const r = await bridge.call("menu.enter_extension", { ext, username, pass }, 60000);
        if (r?.ok) {
          process.stderr.write(`[hnpf] 已自动进扩展 ${ext}（主菜单路径，插件正常加载）\n`);
          return;
        }
        // 有响应说明 MenuExecutor 已处理（成功或失败）——已过主菜单阶段，停止，避免重复建账号
        if (r != null) return;
      } catch {
        // 管道未就绪/超时（主菜单未到）→ 继续等
      }
    }
    process.stderr.write("[hnpf] 自动进扩展超时（150s）——游戏可能未启动成功，请手动 menu_enter_extension\n");
  })();
}

server.tool(
  "launch_game",
  "启动 Hacknet 游戏（正常启动到主菜单，默认带 -enabledebug -enablefc 调试参数）。可选 ext 指定扩展文件夹名：启动后后台自动经主菜单进扩展（menu.enter_extension，插件真正加载；-extstart 直进已弃用——会跳过主菜单导致扩展插件加载异常）；username 可选（进扩展用新账号）；console=true 用 Start-Process 带控制台启动（CEF 不冒空窗口、显示游戏日志）；debug=false 去掉调试参数。dryRun=true 只返回将执行的命令不启动。",
  {
    ext: z.string().optional().describe("扩展文件夹名（如 KernelExtensionTEST123123）：启动后自动经主菜单进扩展"),
    username: z.string().optional().describe("进扩展用的新账号名（配合 ext；省略默认 mcp）"),
    console: z.boolean().optional().describe("true=带控制台启动（Start-Process，CEF 不再冒空窗口，控制台显示游戏日志；默认 false 直接启动）"),
    debug: z.boolean().optional().describe("默认 true 带 -enabledebug -enablefc（游戏调试模式）；false 则不带"),
    dryRun: z.boolean().optional().describe("true=只返回命令不实际启动"),
  },
  async ({ ext, username, console, debug, dryRun }) => {
    // 防多开：进程检测（tasklist），游戏已在跑则拒绝
    if (!dryRun && (await isGameRunning())) {
      return {
        content: [{ type: "text", text: JSON.stringify({ ok: false, reason: "游戏已在运行（Hacknet.exe 进程存在），请勿重复启动" }, null, 2) }],
      };
    }
    // 注意：不再用 -extstart（跳主菜单 → 插件加载异常）；进扩展走主菜单 menu.enter_extension
    // 默认带调试参数（-enabledebug -enablefc，与 debugtest.bat 一致），debug=false 可关
    const args = debug === false ? [] : ["-enabledebug", "-enablefc"];
    const exe = GAME_EXE;
    const exists = fs.existsSync(exe);
    if (!exists && !dryRun) {
      return { content: [{ type: "text", text: JSON.stringify({ ok: false, reason: `找不到游戏主程序: ${exe}（可用环境变量 HNPF_GAME 指定）` }, null, 2) }] };
    }
    if (dryRun) {
      return { content: [{ type: "text", text: JSON.stringify({ ok: true, dryRun: true, exe, args, cmd: `"${exe}" ${args.join(" ")}` }, null, 2) }] };
    }
    // Hacknet.exe 与 cefprocess.exe 都是 Console 子系统：
    // - 默认（console=false）：直接 spawn（detached + stdio 管道）——最稳、MCP 退出游戏独立存活；
    //   副作用：游戏 spawn 的 CEF 子进程因无控制台继承会各自弹空控制台窗口（无害）
    // - console=true：PowerShell Start-Process（console 程序默认创建新控制台）——游戏有控制台 →
    //   CEF 子进程继承 → 不再冒空窗口，且控制台显示 KE banner/游戏日志（类似 debugtest.bat 体验）；
    //   Start-Process 异步，PowerShell 立即退出，游戏独立存活
    let child, modeNote;
    if (console) {
      const argList = args.length > 0
        ? `@(${args.map((a) => `"${a}"`).join(", ")})`
        : "@()";
      const ps = `Start-Process -FilePath "${exe}" -ArgumentList ${argList} -WorkingDirectory "${path.dirname(exe)}" -PassThru | Out-Null`;
      child = spawn("powershell.exe", ["-NoProfile", "-Command", ps], {
        detached: true, stdio: "ignore", windowsHide: true,
      });
      modeNote = "带控制台启动（Start-Process，CEF 不再冒空窗口，控制台显示游戏日志）";
    } else {
      child = spawn(exe, args, { detached: true, stdio: ["ignore", "pipe", "pipe"], cwd: path.dirname(exe) });
      child.stdout?.on("data", () => { /* 丢弃，防缓冲区满 */ });
      child.stderr?.on("data", () => { });
      modeNote = "直接启动（若见 cefprocess 空控制台窗口属正常，可忽略或改用 console:true）";
    }
    child.stdout?.on("data", () => { /* 丢弃，防缓冲区满 */ });
    child.stderr?.on("data", () => { });
    child.unref();
    const note = ext
      ? `游戏正常启动；后台自动进扩展 ${ext}（主菜单路径，约 30-60s，插件正常加载）。${modeNote}`
      : `游戏启动中，稍候约 20-30s 后 bridge 会自动连上。${modeNote}`;
    if (ext) scheduleAutoEnterExtension(ext, { username });
    return { content: [{ type: "text", text: JSON.stringify({ ok: true, pid: child.pid, exe, args, ext: ext || null, note }, null, 2) }] };
  }
);

// ---------------- 多开管道探测 ----------------

server.tool(
  "pipe_probe",
  "探测可用的 bridge 管道（多开时每个游戏实例各有管道，默认名被占用时自动变 hnpf-mcp-bridge-{pid}）。candidates 逗号分隔（省略则只探测默认管道），返回每个管道在线状态。",
  {
    candidates: z.string().optional().describe("逗号分隔的管道名，如 hnpf-mcp-bridge,hnpf-mcp-bridge-1234"),
  },
  async ({ candidates }) => {
    const list = (candidates || "hnpf-mcp-bridge").split(",").map((s) => s.trim()).filter(Boolean);
    const results = [];
    for (const name of list) {
      const pipe = name.startsWith("\\\\") || name.startsWith("\\.") ? name : `\\\\.\\pipe\\${name}`;
      const probe = new BridgeClient({ pipeName: pipe, timeoutMs: 1500 });
      try {
        await probe.connect();
        results.push({ pipe: name, online: true });
        probe.close();
      } catch {
        results.push({ pipe: name, online: false });
      }
    }
    return { content: [{ type: "text", text: JSON.stringify({ results }, null, 2) }] };
  }
);

// ---------------- 资源 ----------------

server.resource("hnpf://state", "玩家当前状态快照", async () => {
  const r = await call("state.get");
  return { contents: [{ uri: "hnpf://state", text: JSON.stringify(r, null, 2) }] };
});

server.resource("hnpf://network", "全网络快照", async () => {
  const r = await call("network.map");
  return { contents: [{ uri: "hnpf://network", text: JSON.stringify(r, null, 2) }] };
});

// ---------------- 提示（Prompts） ----------------

server.prompt(
  "pentest-guide",
  "针对目标节点给出渗透路径建议：侦察 → 连接 → 读文件 → 开端口 → 提权",
  { targetIp: z.string().describe("目标 IP") },
  ({ targetIp }) => ({
    messages: [{
      role: "user",
      content: {
        type: "text",
        text: `请对 Hacknet 网络中的目标节点 ${targetIp} 执行一次渗透：
1) 用 get_network_map 查看全网络，get_computer 查 ${targetIp} 的端口/用户/防火墙
2) 用 connect 连接目标（如不可达，先在本机执行 connect 到可达节点再跳转）
3) 用 list_files/read_file 侦察敏感文件
4) 对目标已存在但关闭的端口用 open_port 破解（端口号取自 get_computer 的 ports 列表）
5) 满足条件后用 take_admin 提权
6) 每步用 terminal_history 确认结果，用 get_events 观察游戏变化
请逐步执行并说明每一步的依据。`
      }
    }]
  })
);

server.prompt(
  "network-audit",
  "对全网络做弱点审计（开放端口、可达性、管理员）",
  {},
  () => ({
    messages: [{
      role: "user",
      content: {
        type: "text",
        text: `请对当前 Hacknet 网络做一次弱点审计：
1) get_network_map 获取全部节点
2) 对每个可疑节点 get_computer 检查端口开放情况、用户、防火墙
3) 汇总一份弱点清单：哪些节点最容易被入侵、需要破解哪些端口、提权路径是什么
输出结构化的审计报告。`
      }
    }]
  })
);

server.prompt(
  "mission-debug",
  "调试当前任务：检查目标完成条件与 Flag",
  {},
  () => ({
    messages: [{
      role: "user",
      content: {
        type: "text",
        text: `请调试当前游戏任务：
1) get_mission 查看任务标题与目标进度
2) get_flags 检查相关 Flag
3) get_state 确认玩家位置与状态
4) 判断任务卡在哪一步，并给出下一步操作建议（必要时用 run_action 驱动场景动作）`
      }
    }]
  })
);

server.prompt(
  "hn-command-guide",
  "HN 终端指令速查：内置命令语法、与 Unix 的关键差异（scp 方向相反/exe 是列表不是执行/防火墙 analyze→solve 流程等）、危险命令提醒。执行 execute_command 前建议拉取。",
  {},
  () => ({
    messages: [{
      role: "user",
      content: {
        type: "text",
        text: `以下是 Hacknet 终端指令规范速查（执行 execute_command 前先读）：

【内置命令】
- ls / cd [文件夹]（无参不回 home）/ cat [文件] / mv [文件] [目标] / rm [文件|*]（无确认！）
- connect [ip] → probe（端口/安防）→ scan（链接）：连接三连，缺一不可
- ps / kill [PID] / dc（=disconnect）
- scp [文件名] [可选目标] = 从远程【下载】到本地（⚠️方向与 Unix 相反）；upload [本地路径] = 上传
- exe = 列出 /bin 可用程序（⚠️不是执行）；运行程序写 xxx.exe（porthack.exe/forkbomb.exe/shell.exe 等）
- analyze → 得方案 → solve [方案]（防火墙流程，顺序错会失败）
- login（交互式）/ reboot [-i]（-i 立即重启目标）/ openCDTray/closeCDTray / addNote / append [文件] [数据] / replace [文件] "目标" "替换" / clear / help [页码]

【内置可执行程序】porthack.exe（破解端口，需目标有开放端口）、forkbomb.exe（消耗内存，可致崩溃）、shell.exe（远程 shell）、securitytracer.exe（追踪状态）

【操作建议】写/读文件优先用 write_file/read_file 工具；登录密码从 get_computer 找；危险命令（rm */forkbomb/reboot -i）先想清影响。`
      }
    }]
  })
);

// ---------------- 启动 ----------------

bridge.on("event", (ev) => {
  process.stderr.write(`[bridge-event] ${JSON.stringify(ev)}\n`);
});

const transport = new StdioServerTransport();
await server.connect(transport);
process.stderr.write(`[hnpf-mcp-server] ready, pipe=${PIPE_NAME} token=${PIPE_TOKEN ? "***" : "(none)"}\n`);
