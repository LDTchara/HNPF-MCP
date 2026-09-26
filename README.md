# HNPF-MCP

**Hacknet + Pathfinder 的 MCP 服务器**——让任何 MCP 宿主（Claude / DeepSeek / WorkBuddy / Cursor…）通过标准 MCP 协议直接操控 Hacknet 游戏：读取状态、执行命令、驱动模组（KE 等）、管理存档与生命周期。

```
MCP 宿主（AI） ⇄ server（Node.js MCP Server，45 工具） ⇄ bridge（BepInEx 插件，NamedPipe） ⇄ Hacknet/Pathfinder
                                                              ↕ [McpTool] 反射发现
                                                        connector（L3 模组连接器，ke.*）
```

## 特性

- **45 个 MCP 工具**：状态/文件/命令/端口/Flag/邮件/IRC/论坛/任务板/批量/生命周期全覆盖
- **三层抽象**：L1 游戏通用（bridge）→ L2 Pathfinder 注册表泛化（run_action/registry）→ L3 模组连接器（`[McpTool]` 零注册反射发现）
- **生命周期自动化**：`launch_game` 启动+主菜单自动进扩展（免 -extstart 的插件加载异常）、`exit_to_menu` 游戏内切账号（无需重启）、`menu_load_extension_save` 真读档恢复进度
- **工程化**：事件流、存档快照回退（snapshots/）、token 鉴权、命令白名单、审计日志、多开管道自动改名 + `pipe_probe` 探测
- **G 节增强**（借鉴 DSL-AITOOL）：`run_batch` 批量游戏内指令、`submit_mission` 提交任务答案、`hub_list/accept` 任务板（MissionHubServer/DLCHubServer/DHS）、`shell_drive` shell 驱动、`terminal_type` 交互式输入、`os_memory` DLC 内存读取

## 快速开始

### 1. 部署 bridge 到游戏

```powershell
powershell -ExecutionPolicy Bypass -File deploy.ps1
```

- 编译 bridge + connector → 拷贝 DLL 到游戏目录（`BepInEx/plugins/` 与扩展 `Plugins/`）
- 之后**重启 Hacknet** 使新 bridge 生效

### 2. 配置 MCP 宿主

任意支持 stdio MCP 的宿主，指向：

```
命令: node
参数: <repo>/server/src/index.js
```

环境变量（可选）：

| 变量 | 说明 |
|---|---|
| `HNPF_GAME` | 游戏主程序路径（默认自动探测 `D:\Game\Hacknet+DLC+Pathfinder\Hacknet.exe`） |
| `HNPF_PIPE` | 管道名（多开时各实例自动 `-{pid}` 后缀） |
| `HNPF_TOKEN` | 鉴权 token（游戏侧 AutoToken 开启时必填，与 cfg 一致） |
| `HNPF_AUDIT` | `off` 关闭审计日志（默认写 `server/audit.log`） |

### 3. 首次使用

```
launch_game { ext: "KernelExtensionTEST123123" }   # 启动游戏 + 主菜单自动进扩展
# 或先启动游戏，再 menu_enter_extension / menu_load_extension_save
get_state                                          # 查看游戏状态
execute_command { cmd: "help" }                    # 终端执行命令
```

完整工具/命令参考见 **[docs/使用指南.md](docs/使用指南.md)**；终端指令规范见 **[docs/HN命令速查.md](docs/HN命令速查.md)**。

## 工具概览（45 个）

| 分类 | 工具 |
|---|---|
| 状态 | `ping` `get_state` `get_network_map` `get_computer` `get_flags` `get_mission` `mission_detail` |
| 文件 | `list_files` `read_file` `write_file` `append_file` |
| 命令/连接 | `execute_command` `connect` `disconnect` `terminal_history` |
| 端口/提权 | `open_port` `close_port` `take_admin` |
| 动作/exe | `run_action`（Pathfinder/KE 动作泛化执行）`launch_exe` `run_hack_script` |
| Flag | `set_flag` `clear_flag` |
| 邮件/IRC/论坛 | `mail_list` `mail_read` `irc_read` `board_read` |
| 任务/情报（G 节） | `submit_mission` `hub_list` `hub_accept` `os_memory` |
| 交互（G 节） | `shell_drive` `terminal_type` |
| 批量/生命周期 | `run_batch`（游戏内指令批量）`save_game`（自动快照）`exit_to_menu` `pipe_probe` |
| 模组专属 | `modtool_list` `modtool_call` + 连接器自动注册（如 KE 的 `ke.*` 9 个） |
| 游戏启动/主菜单 | `launch_game`（自动进扩展）`menu_enter_extension` `menu_load_extension_save` |

## 测试

```bash
cd server
node test/e2e.mjs          # 假 bridge 全链路（含 G 节）
node test/smoke.mjs        # 工具清单冒烟
node test/real-all.mjs     # 真机回归（16 项，需游戏在 KE 扩展会话）
```

## 目录结构

```
HNPF-MCP/
├── bridge/         L1 BepInEx 全局插件（PipeServer/Executor/MenuExecutor/Events/...）
├── server/         Node.js MCP Server（45 工具 + Prompts + Resources）
├── connector/      L3 模组连接器示例（零模组引用，[McpTool] 反射发现）
├── docs/           使用文档（使用指南 / HN命令速查）
├── deploy.ps1      一键部署脚本
└── snapshots/      存档快照（save_game 自动生成，保留 20 份）
```

> 开发文档（架构/能力盘点/打磨路线/架构图）位于根目录仓库：`HNPF-MCP设计.md`、`HNPF-MCP能力盘点.md`、`HNPF-MCP打磨路线.md`、`HNPF-MCP架构图.md`。

## 已知要点（详见使用指南与开发文档）

- **`-extstart` 已弃用**：跳主菜单会跳过扩展插件卸载/加载流程，导致插件状态异常——一律走主菜单路径（`launch_game` 已自动处理）
- **`menu_load_extension_save` 的 `userFile` 传纯文件名**（如 `save_1.xml`），传绝对路径会触发假读档（新建空扩展会话、SaveExecutor 不恢复）
- 游戏运行中无读档 API（原版限制）：切换进度 = `exit_to_menu` → `menu_load_extension_save`
- **更新扩展 DLL 无需先关游戏**：`exit_to_menu`（退到主菜单，插件完整卸载）→ 覆盖 `Extensions/<扩展>/Plugins/*.dll` → **重启游戏**加载新版本（.NET 程序集不可卸载）；验证替换务必用 `cp` 覆盖写入判断（`mv` 重命名会掩盖占用）
- `exit_to_menu` 在**帧末**执行（不能在 OSUpdateEvent 派发中卸载插件，会导致卸载残留）；工具会等待退出生效后返回

## License

MIT（见 LICENSE）
