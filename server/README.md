# HNPF-MCP（bridge + server）

让 AI（任何 MCP 宿主：Claude Desktop / WorkBuddy / Cursor 等）**查询与控制 Hacknet + Pathfinder 游戏运行态**。

```
AI 宿主 ──stdio──► server (Node.js, MCP SDK)
                        │ NamedPipe \\.\pipe\hnpf-mcp-bridge
                        ▼
Hacknet 游戏进程 ── bridge (C# BepInEx 插件)
   ├─ 主线程泵（OSUpdateEvent 消费请求，杜绝竞态）
   ├─ 58 个分发方法（状态/文件/命令/端口/任务/模组/daemon/原版小操作/主菜单…）+ auth 握手
   └─ 事件推送（node.connected/disconnected、game.loaded/saved 等）
```

## 目录结构

```
bridge/   C# BepInEx 插件（游戏进程内）
  └─ *.cs           HnpfMcpBridgePlugin.cs（入口）、PipeServer.cs（NamedPipe）、
                   Executor*.cs（主线程执行器+DTO 投影，按域拆分）、MenuExecutor.cs、
                   McpModuleScanner.cs、JsonRpc.cs（消息模型）
server/   Node.js MCP 服务器（独立进程）
  └─ src/          index.js（MCP 工具/资源注册）、bridge.js（NamedPipe 客户端）
  └─ test/         smoke.mjs（协议冒烟）、e2e.mjs + fake-bridge.mjs（端到端）、
                   auth-check.mjs（token 握手）、real-all.mjs / real-verify.mjs（真机）
```

## 一、编译并安装 bridge（游戏侧）

前置：游戏已装 [Pathfinder](https://github.com/Arkhist/Hacknet-Pathfinder)（`BepInEx/` 目录存在）。

```bash
dotnet build bridge/HnpfMcpBridge.csproj -c Release
# 产物：bridge/bin/Release/net472/HnpfMcpBridge.dll
```

把 `HnpfMcpBridge.dll` 复制到 **游戏目录 `BepInEx/plugins/`** 下，启动游戏。

### L3 连接器（可选，KE 等模组专属状态）

想暴露某模组的独有内存态（如 KE 的 PhaseSwift/CustomTrial/VM/PortHackV2/ProxyKiller），**不需要改模组本体**，做一个连接器插件即可（实现见 `../connector/`，现含 **13 个 `ke.*`**）：

```bash
dotnet build ../connector/HnpfMcpConnector.csproj -c Release
# 产物：../connector/bin/Release/net472/HnpfMcpConnector.dll → 复制到扩展的 Plugins/（推荐）或 BepInEx/plugins/
```

原理：连接器**不声明任何 `[BepInDependency]`**（bridge 是全局插件，扩展模式依赖校验看不到它，声明会误报 missing dependencies）——全部走运行时反射访问模组类型；`[McpTool]` 静态方法由 bridge 在 OSLoaded / 进扩展时反射发现，**无需注册代码**。新模组照此模式加一组 `[McpTool]` 方法即可，MCP 侧用 `modtool_list`/`modtool_call` 调用；最小模板见 `examples/KeMcpAdapter.cs`。

配置（首次启动生成 `BepInEx/config/com.HnpfMcp.Bridge.cfg`）：

```ini
[Pipe]
# 管道名（默认 hnpf-mcp-bridge；多开被占用时自动改为 {Name}-{pid}）
Name = hnpf-mcp-bridge
# 握手 token，留空则不鉴权
Token =
# Token 为空时是否自动生成随机 token（false=保持空值不鉴权）
AutoToken = true

[Safety]
# 只读模式：拒绝所有写方法（fs.write/flags.set/port.*/execute_command/run_action/menu.* 等）
ReadOnly = false
# 命令白名单/黑名单（逗号分隔；仅约束 execute_command / launch_exe / 游戏内 mcp exec）
AllowedCommands =
BlockedCommands =
```

> 注：管道未设置 ACL，`Token` 是本机进程间的唯一鉴权手段；`ReadOnly` 由 bridge 在方法分发入口统一拦截（cfg 热加载生效）。

游戏内验证：终端输入 `mcp ping`，应输出 bridge 版本与管道名。

## 二、配置并运行 MCP 服务器

```bash
cd server
npm install
# 直接跑（stdio，供 MCP 宿主拉起）：
node src/index.js
# 测试：
node test/smoke.mjs        # 协议冒烟（游戏可不运行；断言 isError + resources URI）
node test/e2e.mjs          # 端到端（自动起假 bridge，覆盖全部工具含新增进程/daemon/文件/防火墙）
node test/auth-check.mjs   # token 握手（fake bridge 返回 authenticated）
node test/multiclient-check.mjs  # 多客户端并发（真机，需游戏运行）
node test/token-real-check.mjs   # 真实 token 鉴权（真机，需游戏主菜单 + cfg 设置 Token）
```

环境变量：`HNPF_PIPE`（管道完整路径，默认 `\\.\pipe\hnpf-mcp-bridge`）、`HNPF_TOKEN`（与 bridge cfg `[Pipe] Token` 一致）、`HNPF_GAME`（游戏路径）、`HNPF_AUDIT=off`（关审计）、`HNPF_LOG`（game_log_tail 默认日志路径）。

MCP 宿主配置示例（详见 `../docs/使用指南.md` §1.1）：

```json
{
  "mcpServers": {
    "hnpf": {
      "command": "C:\\...\\node.exe",
      "args": ["C:\\...\\server\\src\\index.js"],
      "env": { "HNPF_PIPE": "\\\\.\\pipe\\hnpf-mcp-bridge" }
    }
  }
}
```

## 三、工具清单（63 个）

| 分类 | 工具 |
|---|---|
| 状态 | `ping` `get_state` `get_network_map(summary?)` `get_computer(ip?)` `get_flags` `get_mission` `mission_detail` |
| 文件 | `list_files(ip?,path?)` `read_file(ip?,path?,file)` `write_file` `append_file` `delete_file` `move_file` `replace_in_file` `transfer_file` |
| 命令 | `execute_command(cmd,waitMs?)` `connect(ip)` `disconnect` `terminal_history(lines?)` |
| 端口/提权/防火墙 | `open_port` `close_port` `take_admin` `firewall_crack`（调试直破）`analyze_firewall` `solve_firewall` |
| 动作/exe | `run_action(xml)`（Pathfinder/KE 动作泛化）`launch_exe(exeName,args?)` `run_hack_script(script)` |
| Flag | `set_flag(name)` `clear_flag(name)` |
| 进程/daemon | `get_processes`（os.exes：PID/类型/ramCost/位置）`kill_exe(pid)` `get_daemons(ip?)`（原版 daemon 通用投影） |
| 存档/事件 | `save_game`（自动快照）`save_list` `get_events(since?)` `registry` |
| 邮件/IRC/论坛 | `mail_list(ip?)` `mail_read(ip?,user,folder?,subject)` `irc_read(ip?)` `board_read(ip?)` |
| 任务/情报 | `submit_mission(details,sender?)` `hub_list(ip?)` `hub_accept(id,ip?)` `os_memory(ip?)` |
| 交互 | `shell_drive(action?)` `terminal_type(text)` |
| 原版小操作 | `reboot_computer(instant?)` `set_cd_tray(open)` `clear_terminal` `add_note(text)` `scan_network` `probe_target` `get_achievements` |
| 批量/生命周期 | `run_batch(steps,continueOnError?)`（34 个动作类工具映射，waitMs/快照/退出等待与单工具一致）`exit_to_menu` `pipe_probe(candidates?)` |
| 模组专属 | `modtool_list` `modtool_call(tool,params?)`（连接器注册的 13 个 `ke.*` 等） |
| 游戏启动/主菜单 | `launch_game(ext?,username?,console?,debug?,dryRun?)` `menu_enter_extension(username?,pass?,ext?)` `menu_load_extension_save(userFile,username,ext?)` |
| 日志 | `game_log_tail(lines?,filter?,offset?,path?)`（读本机 BepInEx LogOutput.log） |

注：`ip` 参数省略时：`computer/fs/port.*` 作用于**当前连接节点**；`mail/irc/board/hub/os.memory/firewall.*/daemon.list` 为**当前连接 → 本机**回退。

## 三·五、提示（Prompts，4 个）

- `pentest-guide {targetIp}` — 目标渗透路径（侦察→连接→读文件→开端口→提权）
- `network-audit` — 全网络弱点审计
- `mission-debug` — 任务调试（目标条件/Flag/动作驱动）
- `hn-command-guide` — **HN 终端指令速查**（scp 方向相反/exe 是列表/analyze→solve 等差异，执行命令前拉取；详见 docs/HN命令速查.md）

资源：`hnpf://state`、`hnpf://network`（MCP 客户端可直接 read；`listResources` 返回正确 URI）。事件（bridge→server）：`node.connected/disconnected`、`game.loaded`、`game.saved` 实时推 stderr；`mission.changed`、`command.executed` 进事件缓冲，用 `get_events` 增量拉取。

## 四、设计要点

- **线程安全**：所有游戏对象访问都在 `OSUpdateEvent`（主线程）回调内执行；请求经 `ConcurrentQueue` 入队，每帧最多消费 8 个防掉帧。
- **序列化**：游戏对象循环引用多，全部手写 DTO 投影（见 `Executor.cs`），绝不对游戏对象直接 `JSON.stringify`。
- **成员兼容**：对 `RamModule` 等有版本差异的成员用反射访问，编译期不绑定成员名。
- **只读模式**：`ReadOnly=true` 时在方法分发入口拒绝全部写方法（`fs.write/append/delete/move/replace`、`flags.*`、`port.*`、`admin.take`、`game.execute_command/connect/disconnect/run_action/run_hack_script/launch_exe/save/exit_to_menu`、`mission.submit`、`hub.accept`、`shell.drive`、`terminal.type`、`process.kill`、`game.reboot`、`game.cd_tray`、`game.add_note`、`firewall.*`、`menu.*`）；读方法不受影响。命令白/黑名单只约束 `execute_command`/`launch_exe`/游戏内 `mcp exec`。
- **扩展**：模组/连接器通过 `[McpTool]` 特性注册专属工具（反射自动装配，已实现；KE 连接器 13 个 `ke.*`）。

## 五、已知坑

- **`mcp` 游戏内命令不用 `CommandManager.RegisterCommand`**：其 `addAutocomplete=true` 内部走 Harmony ReversePatch（`OrigProgramListInit`），在部分 Pathfinder 版本会抛 `NotImplementedException` 导致插件加载失败（表现为 Load 时崩溃、管道已监听但插件未注册成功）。已改为 `EventManager<CommandExecuteEvent>` 纯委托拦截（与 CommandManager 内部实现同源），零 ReversePatch 依赖。代价仅是终端 tab 补全里没有 `mcp`。

