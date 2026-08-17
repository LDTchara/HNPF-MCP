# HNPF-MCP（bridge + server）

让 AI（任何 MCP 宿主：Claude Desktop / WorkBuddy / Cursor 等）**查询与控制 Hacknet + Pathfinder 游戏运行态**。

```
AI 宿主 ──stdio──► server (Node/TS, MCP SDK)
                        │ NamedPipe \\.\pipe\bridge
                        ▼
Hacknet 游戏进程 ── bridge (C# BepInEx 插件)
   ├─ 主线程泵（OSUpdateEvent 消费请求，杜绝竞态）
   ├─ 16 个 JSON-RPC 方法（状态查询 / 命令执行 / 文件操作 / Flag）
   └─ 事件推送（node.connected / game.loaded / game.saved）
```

## 目录结构

```
bridge/   C# BepInEx 插件（游戏进程内）
  └─ src/          HnpfMcpBridgePlugin.cs（入口）、PipeServer.cs（NamedPipe）、
                   Executor.cs（主线程执行器+DTO 投影）、JsonRpc.cs（消息模型）
server/   Node.js MCP 服务器（独立进程）
  └─ src/          index.js（MCP 工具/资源注册）、bridge.js（NamedPipe 客户端）
  └─ test/         smoke.mjs（协议冒烟）、fake-bridge.mjs + e2e.mjs（端到端链路）
```

## 一、编译并安装 bridge（游戏侧）

前置：游戏已装 [Pathfinder](https://github.com/Arkhist/Hacknet-Pathfinder)（`BepInEx/` 目录存在）。

```bash
dotnet build bridge/HnpfMcpBridge.csproj -c Release
# 产物：bridge/bin/Release/net472/HnpfMcpBridge.dll
```

把 `HnpfMcpBridge.dll` 复制到 **游戏目录 `BepInEx/plugins/`** 下，启动游戏。

### L3 连接器（可选，KE 等模组专属状态）

想暴露某模组的独有内存态（如 KE 的 PhaseSwift/CustomTrial/VM），**不需要改模组本体**，做一个硬依赖该模组的独立连接器插件即可（示例见 `../connector/`，已含 KE 适配器：`ke.phaseswift.state` / `ke.customtrial.state` / `ke.vm.state` / `ke.config.get` / `ke.flag.find`）：

```bash
dotnet build ../connector/KeMcpConnector.csproj -c Release
# 产物：../connector/bin/Release/net472/KeMcpConnector.dll → 复制到 BepInEx/plugins/
```

原理：连接器用 `[BepInDependency]` 声明依赖 KE 与 bridge（BepInEx 保证先加载）；`[McpTool]` 静态方法由 bridge 在 OSLoaded 时反射发现，**无需任何注册代码**。新模组照此模式加一个连接器即可，MCP 侧工具自动出现。

配置（首次启动生成 `BepInEx/config/com.HnpfMcp.Bridge.cfg`）：

```ini
[Pipe]
# 多开游戏时改为 bridge-{pid}
Name = bridge
# 握手 token，留空则不鉴权（仅本机进程可连管道）
Token =

[Safety]
# 只读模式：拒绝所有写操作
ReadOnly = false
```

游戏内验证：终端输入 `mcp ping`，应输出 bridge 版本与管道名。

## 二、配置并运行 MCP 服务器

```bash
cd server
npm install
# 直接跑（stdio，供 MCP 宿主拉起）：
node src/index.js
# 测试：
node test/smoke.mjs        # 协议冒烟（游戏可不运行）
node test/e2e.mjs          # 端到端（自动起假 bridge 模拟游戏侧）
```

环境变量：`HNPF_PIPE`（管道名，默认 `\\.\pipe\bridge`）、`HNPF_TOKEN`（与 bridge cfg 一致）。

MCP 宿主配置示例（Claude Desktop `claude_desktop_config.json` / WorkBuddy `~/.workbuddy/mcp.json`，详见 `HNPF-MCP使用指南.md` §1.1）：

```json
{
  "mcpServers": {
    "hnpf": {
      "command": "C:\\...\\node.exe",
      "args": ["C:\\...\\server\\src\\index.js"],
      "env": { "HNPF_PIPE": "\\\\.\\pipe\\bridge" }
    }
  }
}
```

## 三、工具清单（35 个）

| 分类 | 工具 |
|---|---|
| 状态 | `ping` `get_state` `get_network_map` `get_computer(ip?)` `get_flags` `get_mission` `mission_detail` |
| 文件 | `list_files(ip?,path?)` `read_file(ip?,path?,file)` `write_file` `append_file` |
| 命令 | `execute_command(cmd)` `connect(ip)` `disconnect` `terminal_history(lines?)` |
| 端口/提权 | `open_port(ip?,port)` `close_port(ip?,port)` `take_admin(ip?)` |
| 动作/exe | `run_action(xml)`（泛化执行 Pathfinder/KE 动作）`launch_exe(exeName,args?)` |
| Flag | `set_flag(name)` `clear_flag(name)` |
| 存档 | `save_game`（自动快照到 HNPF-MCP/snapshots/） |
| 事件/发现 | `get_events(since?)`（增量事件）`registry`（自动列出模组注册的命令/动作/exe/daemon） |
| 模组专属 | `modtool_list` `modtool_call`（调用模组 `[McpTool]` 工具，见 examples/KeMcpAdapter.cs） |
| 游戏启动/主菜单 | `launch_game(ext?,username?,dryRun?)`（正常启动；`ext` 后台自动经主菜单进扩展，`-extstart` 已弃用）`menu_enter_extension(username?,pass?)`（主菜单新建账号进扩展）`menu_load_extension_save(userFile,username,ext?)`（主菜单恢复存档进扩展） |
| 多开 | `pipe_probe(candidates?)`（探测在线 bridge 管道）；`launch_game` 含进程检测防多开 |

注：`ip` 参数省略时作用于当前连接节点。

## 三·五、提示（Prompts，4 个）

- `pentest-guide {targetIp}` — 目标渗透路径（侦察→连接→读文件→开端口→提权）
- `network-audit` — 全网络弱点审计
- `mission-debug` — 任务调试（目标条件/Flag/动作驱动）
- `hn-command-guide` — **HN 终端指令速查**（scp 方向相反/exe 是列表/analyze→solve 等差异，执行命令前拉取；详见 docs/HN命令速查.md）

资源：`hnpf://state`、`hnpf://network`。事件（bridge→server）：`node.connected/disconnected`、`game.loaded`、`game.saved`（当前输出到 stderr 日志，订阅工具在 P2 提供）。

## 四、设计要点

- **线程安全**：所有游戏对象访问都在 `OSUpdateEvent`（主线程）回调内执行；请求经 `ConcurrentQueue` 入队，每帧最多消费 8 个防掉帧。
- **序列化**：游戏对象循环引用多，全部手写 DTO 投影（见 `Executor.cs`），绝不对游戏对象直接 `JSON.stringify`。
- **成员兼容**：对 `RamModule` 等有版本差异的成员用反射访问，编译期不绑定成员名。
- **只读模式**：`ReadOnly=true` 时拒绝 `execute_command/connect/fs.write/flags.set` 等写操作。
- **扩展**：模组可通过 `[McpTool]` 特性注册专属工具（反射自动装配，P3 规划）。

## 五、已知坑

- **`mcp` 游戏内命令不用 `CommandManager.RegisterCommand`**：其 `addAutocomplete=true` 内部走 Harmony ReversePatch（`OrigProgramListInit`），在部分 Pathfinder 版本会抛 `NotImplementedException` 导致插件加载失败（表现为 Load 时崩溃、管道已监听但插件未注册成功）。已改为 `EventManager<CommandExecuteEvent>` 纯委托拦截（与 CommandManager 内部实现同源），零 ReversePatch 依赖。代价仅是终端 tab 补全里没有 `mcp`。

