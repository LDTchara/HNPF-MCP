# Changelog

## 0.2.0（2026-10-19）

### bridge（HnpfMcpBridge 0.2.0）

- **多客户端**：NamedPipe 最多 8 条并发连接，每连接独立鉴权与发送队列；响应按来源连接回送，事件广播到所有连接（此前仅支持单客户端，`pipe_probe`/第二个 AI 宿主会抢连接）。
- **管道 ACL**：仅当前用户 + SYSTEM 可连接（默认 NamedPipe DACL 允许任意本机进程）。
- **token 鉴权修复**：改为连接级认证——每条连接首次请求必须 `auth`，通过后本连接不再逐条校验；失败只断开本连接并继续监听（此前通过 auth 后的下一个请求会被 401 并直接退出监听循环）。
- **只读模式真正生效**：`Safety.ReadOnly=true` 在方法分发入口拦截全部写方法（files/flags/ports/提权/命令/动作/任务/模组/菜单/进程/防火墙/reboot/cd_tray/add_note），cfg 热加载即时生效。
- **修复**：`hub.accept` 反射缺 `BindingFlags.Instance`（contract 静默假成功 / listing 永远失败）；`get_state` RAM 恒 0（改读 `os.ramAvaliable`/`os.totalRam`）；`fs.*` 未连接时 `ip` 省略回退本机（此前 NRE）；`fs.write/append` 目录不存在改为抛错（此前静默 ok）；`run_hack_script` 路径穿越校验；`run_action` 动作缺失明确报错；`mission.submit` 未完成目标诊断不再误报；事件新增 `ts` 毫秒时间戳、`mission.changed` 也推送、OSLoaded 复位事件去重状态；`McpModuleScanner.Call` 透传内层异常；`BuildVersion` 改为读取 DLL 构建时间（自动更新）。
- **新增方法**：`process.list` / `process.kill`、`daemon.list`（原版 flavor daemon 通用投影）、`fs.delete` / `fs.move` / `fs.replace`、`firewall.analyze` / `firewall.solve`、`game.reboot` / `game.cd_tray` / `game.clear_terminal` / `game.add_note` / `game.scan` / `game.probe`、`achievements.list`；`state.get` 增加 `media`（歌曲/主题/静音）；`get_computer` 增加 `type` 与 daemon `type/isListed`；`get_mission` 增加 `branchMissions`。
- 游戏内 `mcp exec` 现在也走命令白/黑名单。
- 配置注释更新：`[Pipe] Name/Token/AutoToken`、`[Safety] ReadOnly/AllowedCommands/BlockedCommands`。

### server（hnpf-mcp-server 0.2.0）

- **Resources 修复**：注册参数错位（URI 被当描述）导致资源不可读 → 正确签名 `resource(name, uri, config, cb)`，`hnpf://state`/`hnpf://network` 可正常 read。
- **新增工具（7）**：`reboot_computer` / `set_cd_tray` / `clear_terminal` / `add_note` / `scan_network` / `probe_target` / `get_achievements`；工具总数 47 → 63。
- **`run_batch` 与单工具行为一致**：批量内 `execute_command.waitMs`、`save_game` 快照、`exit_to_menu` 等待均生效；`menu_*` 在 OS 会话内明确报错；必要的必填参数校验（不再静默写空内容）；动作类映射 28 → 34。
- **`launch_game`**：返回真实游戏 PID（`launcherPid` 另列）；启动前校验 `ext` 目录存在；回退启动前复查进程，降低双开风险；后台自动进扩展改走统一 `call()`（审计 + 离线错误翻译）。
- **`game_log_tail`**：默认只允许读游戏目录 `BepInEx/` 下日志（`HNPF_LOG_ALLOW_ANY=1` 可放开），修复文件尾空行导致的 `truncated`/行数误报。
- **审计**：同步写盘不丢行；参数白名单扩充（file/path/target/pid/sender/subject/text/id 等）；记录失败调用的 error。
- **健壮性**：连接超时 3s；token 握手必须收到 `authenticated:true`；并发连接去重；`close()` 清理 buffer；`pipe_probe` 探测后关闭连接。
- 数字参数统一 `.int()`；`takeSnapshot total` 修正；`modtool_call` 参数不能覆盖工具名；`save_game` 快照前等待落盘。
- 测试：新增 `auth-check.mjs`（token 握手）、`real-verify.mjs`（真机 27 项）、`multiclient-check.mjs`（多客户端并发）；e2e 覆盖全部新工具；smoke 修正离线断言（`isError`）并校验资源 URI；移除遗留的 p1/p2/real-check 脚本。

### connector（HnpfMcpConnector 0.4.0）

- 修复：`ke.vm.state.configName` 改读 `SourcePath`（原字段已被 KE 删除）；`ke.customtrial.state` 增加 `running`；`FindType` 只从当前 KE 程序集取类型（不再命中其他模组同名类型）；`FindKeAssembly` 在插件表无 KE 时返回 null（全局安装退出扩展后不再读旧静态状态）；`ModMissing` 区分未加载/类型缺失；版本元数据统一为 0.4.0。
- 新增：`ke.titlebanner.state`、`ke.ending.state`、`ke.color.state` 增加临时主题覆盖（ThemeOverride）状态；`ke.*` 总数 11 → **13**。
- 日志文案全部改英文（BepInEx 控制台 GBK/UTF-8 混排下中文会乱码）；版本常量/csproj 统一 0.4.0；删除过时的 `libs/KernelExtensions.dll`。

## 0.1.0

- 初版：bridge named pipe + 主线程执行器、server 工具集（状态/文件/命令/端口/邮件/IRC/论坛/任务/批量/主菜单）、L3 `[McpTool]` 连接器与 KE 适配器。
