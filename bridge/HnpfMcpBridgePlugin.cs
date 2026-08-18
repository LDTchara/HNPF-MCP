using BepInEx;
using BepInEx.Hacknet;
using Hacknet;
using Pathfinder.Event;
using Pathfinder.Event.Gameplay;
using Pathfinder.Event.Loading;
using Pathfinder.Event.Saving;
using System.IO;

namespace HnpfMcpBridge;

/// <summary>
/// HNPF-MCP-Bridge：把 Hacknet + Pathfinder 游戏进程暴露成本机 NamedPipe 服务，
/// 供 hnpf-mcp-server（MCP 服务器）连接。所有游戏操作在主线程（OSUpdateEvent）执行。
///
/// 安装：把编译出的 HnpfMcpBridge.dll 复制到游戏目录 BepInEx/plugins/ 下。
/// 配置：BepInEx/config/com.HnpfMcp.Bridge.cfg 里 PipeName / PipeToken / ReadOnly。
/// </summary>
[BepInPlugin(ModGUID, ModName, Version)]
public class HnpfMcpBridgePlugin : HacknetPlugin
{
    public const string ModGUID = "com.HnpfMcp.Bridge";
    public const string ModName = "HNPF-MCP-Bridge";
    public const string Version = "0.1.0";
    /// <summary>构建自检版本：每次改动递增，ping/get_state 可见，用于确认游戏里跑的是不是最新 DLL。</summary>
    public const string BuildVersion = "2026-08-18-1754";

    private static HnpfMcpBridgePlugin _instance;

    public static PipeServer Pipe { get; private set; }
    public static string LastConnectedIp { get; set; }
    public static string LastMissionTitle { get; set; }

    private static string _pipeName;
    private static string _token;
    private static bool _readOnly;
    private static HashSet<string> _allowedCommands = new();
    private static HashSet<string> _blockedCommands = new();
    private static FileSystemWatcher _cfgWatcher;

    /// <summary>读取 Safety 段（ReadOnly/AllowedCommands/BlockedCommands）。C4 热加载：cfg 变化时重读。</summary>
    private void LoadSafetyConfig()
    {
        _readOnly = Config.Bind("Safety", "ReadOnly", false,
            "只读模式：拒绝所有写操作（execute_command/connect/fs.write/flags.set 等）").Value;
        _allowedCommands = new HashSet<string>(SplitCsv(Config.Bind("Safety", "AllowedCommands", "",
            "命令白名单（逗号分隔，命令名不区分大小写）。空 = 不限制").Value));
        _blockedCommands = new HashSet<string>(SplitCsv(Config.Bind("Safety", "BlockedCommands", "",
            "命令黑名单（逗号分隔，如 rm,killall）。优先于白名单").Value));
    }

    /// <summary>C4：监视 cfg 文件，变更时延迟重载 Safety 配置（白名单/只读即时生效，无需重启游戏）。</summary>
    private void WatchConfig()
    {
        try
        {
            var cfgPath = Config.ConfigFilePath;
            var dir = Path.GetDirectoryName(cfgPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _cfgWatcher = new FileSystemWatcher(dir, Path.GetFileName(cfgPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            // 文件写入可能多次触发，统一延迟 300ms 合并后重载一次
            var pending = new object();
            var last = 0L;
            _cfgWatcher.Changed += (s, e) =>
            {
                var now = Environment.TickCount;
                lock (pending) { if (now - last < 400) return; last = now; }
                try
                {
                    System.Threading.Thread.Sleep(300);
                    Config.Reload();
                    LoadSafetyConfig();
                    Log.LogInfo($"[{ModName}] cfg 热加载: readOnly={_readOnly} allowed={_allowedCommands.Count} blocked={_blockedCommands.Count}");
                }
                catch (Exception ex) { Log.LogWarning($"[{ModName}] cfg reload failed: {ex.Message}"); }
            };
        }
        catch { /* 热加载不可用不影响启动 */ }
    }

    /// <summary>
    /// 命令白名单/黑名单校验。规则格式：`cmd`（精确命令名）或 `cmd:paramPrefix`（命令+参数前缀，如 `scp:/home/`）。
    /// 黑名单优先，命令名与参数均不区分大小写。
    /// </summary>
    public static string CheckCommandPolicy(string fullCommand)
    {
        if (string.IsNullOrWhiteSpace(fullCommand)) return null;
        var trimmed = fullCommand.Trim();
        var parts = trimmed.Split(' ');
        var name = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? trimmed.Substring(parts[0].Length).Trim() : "";

        if (RuleMatches(_blockedCommands, name, args))
            return $"command '{name}' is blocked by BlockedCommands policy";
        if (_allowedCommands.Count > 0 && !RuleMatches(_allowedCommands, name, args))
            return $"command '{name}' not in AllowedCommands whitelist";
        return null;
    }

    /// <summary>规则匹配：`cmd` 精确 或 `cmd:paramPrefix`（参数以前缀开头）。无前缀规则匹配任意参数。</summary>
    private static bool RuleMatches(HashSet<string> rules, string cmd, string args)
    {
        foreach (var r in rules)
        {
            var idx = r.IndexOf(':');
            var rCmd = idx < 0 ? r : r.Substring(0, idx);
            var rPrefix = idx < 0 ? null : r.Substring(idx + 1);
            if (!string.Equals(rCmd, cmd, StringComparison.OrdinalIgnoreCase)) continue;
            if (rPrefix == null || string.IsNullOrEmpty(args) || args.StartsWith(rPrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> SplitCsv(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) yield break;
        foreach (var item in csv.Split(','))
        {
            var t = item.Trim();
            if (t.Length > 0) yield return t.ToLowerInvariant();
        }
    }

    public override bool Load()
    {
        _instance = this;

        // BepInEx 配置（cfg 文件生成于 BepInEx/config/）
        _pipeName = Config.Bind("Pipe", "Name", PipeServer.DefaultPipeName,
            "NamedPipe 名称，多开游戏时改为 hnpf-mcp-bridge-{pid}").Value;
        var tokenEntry = Config.Bind("Pipe", "Token", "",
            "握手 token，留空则不鉴权（仅本机进程可连管道）");
        _token = tokenEntry.Value;

        // D5：token 强化——仅当 AutoToken=true 且 Token 为空时，生成随机 token 写回 cfg 并提示同步到宿主 env。
        // 想关闭鉴权：AutoToken=false + Token 留空（否则每次启动都会重新生成）。
        var autoToken = Config.Bind("Pipe", "AutoToken", true,
            "Token 为空时是否自动生成随机 token（false = 保持空值不鉴权）").Value;
        if (string.IsNullOrEmpty(_token) && autoToken)
        {
            try
            {
                _token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                tokenEntry.Value = _token;
                Config.Save();
                Log.LogWarning($"[{ModName}] 已自动生成随机 PipeToken（{_token.Substring(0, 8)}...）。" +
                               "请在 MCP 宿主配置里设置环境变量 HNPF_TOKEN 为同一值后重启游戏（或设 Pipe.AutoToken=false 关闭自动生成）。");
            }
            catch { Log.LogWarning($"[{ModName}] 无法写回 Token 到 cfg，管道保持无鉴权"); }
        }

        LoadSafetyConfig();
        Log.LogInfo($"[{ModName}] pipe='{_pipeName}' readOnly={_readOnly} " +
                    $"allowed={_allowedCommands.Count} blocked={_blockedCommands.Count}");

        // C4：cfg 文件变更 → Safety 配置热加载（改白名单/只读不用重启游戏）
        WatchConfig();

        // 1. NamedPipe 服务（收发线程）
        Pipe = new PipeServer(_pipeName, _token);
        Pipe.Start();

        // 2. 主线程泵：每帧消费请求 + 事件检测
        EventManager<OSUpdateEvent>.AddHandler(Executor.OnUpdate);

        // 2.5 主菜单执行器：Harmony patch MainMenu.Update，主菜单阶段也能处理 menu.* 请求（进扩展）
        try
        {
            new HarmonyLib.Harmony(ModGUID).PatchAll(typeof(MenuExecutor).Assembly);
        }
        catch (Exception ex) { Log.LogWarning($"[{ModName}] MenuExecutor patch failed: {ex.Message}"); }

        // 3. 生命周期事件
        EventManager<OSLoadedEvent>.AddHandler(e =>
        {
            // P3：OS 加载完成时扫描所有插件的 [McpTool]（此时各模组已 Load 完毕）
            try { McpModuleScanner.Scan(); } catch (Exception ex) { Log.LogWarning($"[{ModName}] McpTool scan failed: {ex.Message}"); }

            var data = new Dictionary<string, object> { ["ip"] = e.Os?.connectedIP };
            EventBuffer.Push("game.loaded", data);
            Pipe?.PushResponse(RpcEvent.ToJsonLine("game.loaded", data));
        });
        EventManager<SaveEvent>.AddHandler(e =>
        {
            var data = new Dictionary<string, object> { ["file"] = e.Filename };
            EventBuffer.Push("game.saved", data);
            Pipe?.PushResponse(RpcEvent.ToJsonLine("game.saved", data));
        });

        // 3.5 命令执行事件（记入缓冲，供 events.get 增量拉取）
        EventManager<CommandExecuteEvent>.AddHandler(e =>
        {
            if (e.Args == null || e.Args.Length == 0) return;
            EventBuffer.Push("command.executed", new Dictionary<string, object>
            {
                ["cmd"] = string.Join(" ", e.Args),
                ["found"] = e.Found
            });
        });

        // 4. 游戏内命令：mcp ping / mcp state / mcp exec <cmd>
        // 注：不用 CommandManager.RegisterCommand —— 其 addAutocomplete=true 依赖 Harmony ReversePatch
        // (OrigProgramListInit)，在部分 Pathfinder 版本下抛 NotImplementedException 导致插件加载失败。
        // 改走 EventManager<CommandExecuteEvent> 纯委托拦截（与 CommandManager 内部实现同源，零 ReversePatch）。
        EventManager<CommandExecuteEvent>.AddHandler(OnMcpCommandExecute);

        Log.LogInfo($"[{ModName}] loaded. pipe listening.");
        return true;
    }

    /// <summary>在 ProgramRunner 分发前拦截 `mcp` 命令（仿 Pathfinder CommandManager 内部做法）。</summary>
    private static void OnMcpCommandExecute(CommandExecuteEvent e)
    {
        if (e.Args == null || e.Args.Length == 0) return;
        if (!string.Equals(e.Args[0], "mcp", StringComparison.OrdinalIgnoreCase)) return;

        e.Found = true;
        e.Cancelled = true;   // 阻止游戏继续按原命令处理
        McpConsoleCommand(e.Os, e.Args);
    }

    public override bool Unload()
    {
        Pipe?.Stop();
        Pipe = null;
        _instance = null;
        return base.Unload();
    }

    // ---------------- 游戏内 `mcp` 命令 ----------------

    private static void McpConsoleCommand(OS os, string[] args)
    {
        if (args.Length < 2)
        {
            os.write("Usage: mcp ping|state|exec <cmd>");
            return;
        }
        switch (args[1].ToLowerInvariant())
        {
            case "ping":
                os.write($"mcp bridge {Version} | pipe={_pipeName} | readOnly={_readOnly}");
                break;
            case "state":
                os.write("--- mcp state ---");
                os.write($"connected: {os.connectedComp?.ip ?? "(none)"} | home: {os.homeNodeID}");
                os.write($"path: {string.Join("/", os.navigationPath ?? new List<int>())}");
                os.write($"flags: {os.Flags?.Flags.Count ?? 0}");
                os.write($"mission: {os.currentMission?.postingTitle ?? "(none)"}");
                break;
            case "exec":
                if (args.Length < 3)
                {
                    os.write("Usage: mcp exec <command>");
                    return;
                }
                if (_readOnly) { os.write("mcp: read-only mode, command rejected"); return; }
                var cmd = string.Join(" ", args.Skip(2));
                os.runCommand(cmd);
                os.write($"mcp: submitted '{cmd}'");
                break;
            default:
                os.write($"mcp: unknown subcommand '{args[1]}'");
                break;
        }
    }

    // ---------------- 日志（供 PipeServer / Executor 使用） ----------------

    public static void LogInfo(string msg) => _instance?.Log?.LogInfo(msg);
    public static void LogWarn(string msg) => _instance?.Log?.LogWarning(msg);
    public static void LogError(string msg) => _instance?.Log?.LogError(msg);
}
