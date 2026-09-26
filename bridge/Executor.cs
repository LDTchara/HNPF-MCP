using Hacknet;
using Pathfinder.Event.Gameplay;
using Pathfinder.Event.Loading;
using Pathfinder.Event.Saving;
using Pathfinder.Event;

namespace HnpfMcpBridge;

/// <summary>
/// 主线程执行器（partial：按域拆分见 Executor.State/FileSystem/Command/Network.cs）。
/// 全部在 OSUpdateEvent（游戏主线程）内执行游戏状态读写。
/// 游戏对象有大量循环引用，一律手写 DTO 投影，绝不直接序列化游戏对象。
/// </summary>
public static partial class Executor
{
    /// <summary>
    /// exit_to_menu 待执行标志：请求时置位，由 ExitToMenuFrameHook（Game1.Update Postfix，帧末）执行。
    /// 不能在 OSUpdateEvent 派发中直接 quitGame——卸载插件（UnpatchSelf）会打断事件派发导致卸载残留。
    /// </summary>
    public static bool PendingExitToMenu = false;

    // ---------------- 请求泵 ----------------

    public static void OnUpdate(OSUpdateEvent e)
    {
        var os = e.OS;
        var pipe = HnpfMcpBridgePlugin.Pipe;

        // 每帧最多消费 8 个请求，防掉帧
        int budget = 8;
        while (budget-- > 0 && pipe.TryDequeueRequest(out var req))
        {
            RpcResponse resp;
            try
            {
                resp = RpcResponse.Ok(req, Execute(os, req));
            }
            catch (Exception ex)
            {
                HnpfMcpBridgePlugin.LogWarn($"Execute '{req.Method}' failed: {ex.Message}");
                resp = RpcResponse.Fail(req, 500, ex.Message);
            }
            pipe.PushResponse(resp.ToJsonLine());
        }

        // 事件推送：连接切换
        var ip = os.connectedComp?.ip;
        if (ip != HnpfMcpBridgePlugin.LastConnectedIp)
        {
            HnpfMcpBridgePlugin.LastConnectedIp = ip;
            var data = new Dictionary<string, object> { ["ip"] = ip };
            EventBuffer.Push(ip == null ? "node.disconnected" : "node.connected", data);
            pipe.PushResponse(RpcEvent.ToJsonLine(
                ip == null ? "node.disconnected" : "node.connected", data));
        }

        // 事件推送：任务切换
        var missionTitle = os.currentMission?.postingTitle;
        if (missionTitle != HnpfMcpBridgePlugin.LastMissionTitle)
        {
            HnpfMcpBridgePlugin.LastMissionTitle = missionTitle;
            EventBuffer.Push("mission.changed", new Dictionary<string, object> { ["title"] = missionTitle });
        }
    }

    // ---------------- 方法分发 ----------------

    private static object Execute(OS os, RpcRequest req)
    {
        var p = req.Params;
        switch (req.Method)
        {
            case "ping":
                return new Dictionary<string, object>
                {
                    ["pong"] = true,
                    ["version"] = HnpfMcpBridgePlugin.Version,
                    ["build"] = HnpfMcpBridgePlugin.BuildVersion,
                    ["pipe"] = HnpfMcpBridgePlugin.Pipe?.EffectivePipeName,
                    ["os"] = "hacknet+pathfinder"
                };

            case "state.get":
                return GetState(os);

            case "network.map":
                return GetNetworkMap(os, BoolParam(p, "summary"));

            case "computer.get":
                return GetComputer(os, Str(p, "ip"));

            case "fs.list":
                return ListFiles(os, Str(p, "ip"), Str(p, "path"));

            case "fs.read":
                return ReadFile(os, Str(p, "ip"), Str(p, "path"), Str(p, "file"));

            case "fs.write":
                return WriteFile(os, Str(p, "ip"), Str(p, "path"), Str(p, "file"), Str(p, "content"));

            case "fs.append":
                return AppendFile(os, Str(p, "ip"), Str(p, "path"), Str(p, "file"), Str(p, "content"));

            case "game.execute_command":
                return ExecuteCommand(os, Str(p, "cmd"));

            case "game.run_hack_script":
                return RunHackScript(os, Str(p, "script"));

            case "mail.list":
                return MailList(os, Str(p, "ip"));

            case "mail.read":
                return MailRead(os, Str(p, "ip"), Str(p, "user"), Str(p, "folder"), Str(p, "subject"));

            case "irc.read":
                return IrcRead(os, Str(p, "ip"));

            case "board.read":
                return BoardRead(os, Str(p, "ip"));

            case "save.list":
                return SaveList(os);

            case "game.connect":
                return ExecuteCommand(os, "connect " + Str(p, "ip"));

            case "game.disconnect":
                Programs.disconnect(new string[0], os);
                return new Dictionary<string, object> { ["ok"] = true };

            case "game.save":
                os.saveGame();
                return new Dictionary<string, object> { ["ok"] = true, ["message"] = "save requested" };

            case "game.exit_to_menu":
                // 模拟顶栏 X → "Exit to Menu"：os.quitGame（public）→ HasExitedAndEnded=true（触发
                // 扩展插件卸载）→ MainMenu.resetOS + AddScreen(new MainMenu) + SaveFileManager.Init
                // → 之后可 menu_load_extension_save 读其他账号（无需重启游戏）
                //
                // ⚠️ 不在本处直接 quitGame！本方法运行在 OSUpdateEvent 派发中（bridge 的 OnUpdate 是
                //    派发目标之一，此时正在遍历 handler 列表）。quitGame 的 Harmony Postfix 会调
                //    HacknetChainloader.UnloadTemps() → 各扩展插件 HarmonyInstance.UnpatchSelf()——
                //    在事件派发/正在执行的 patch 里卸载插件会打断派发、卸载不完整（日志缺
                //    "Finished unloading extension plugins"），表现为退出后插件仍占用。
                //    手动点 X → Exit to Menu 发生在 MessageBoxScreen.Update（派发早已结束）→ 安全。
                //    故改为只置标志，由 ExitToMenuFrameHook（Game1.Update Postfix，帧末）执行。
                PendingExitToMenu = true;
                return new Dictionary<string, object> { ["ok"] = true, ["queued"] = true, ["message"] = "exit to main menu queued (runs at end of frame)" };

            case "port.open":
                return PortChange(os, Str(p, "ip"), IntParam(p, "port"), true);

            case "firewall.crack":
                return FirewallCrack(os, Str(p, "ip"));

            case "mission.submit":
                return MissionSubmit(os, Str(p, "sender"), Str(p, "details"));

            case "hub.list":
                return HubList(os, Str(p, "ip"));

            case "hub.accept":
                return HubAccept(os, Str(p, "ip"), Str(p, "id"));

            case "shell.drive":
                return ShellDrive(os, Str(p, "action"));

            case "terminal.type":
                return TypeInput(os, Str(p, "text"));

            case "os.memory":
                return OsMemory(os, Str(p, "ip"));

            case "port.close":
                return PortChange(os, Str(p, "ip"), IntParam(p, "port"), false);

            case "admin.take":
            {
                string ip = Str(p, "ip") ?? os.connectedComp?.ip;
                if (string.IsNullOrEmpty(ip)) throw new ArgumentException("no target ip (not connected)");
                os.takeAdmin(ip);
                return new Dictionary<string, object> { ["ok"] = true, ["admin"] = ip };
            }

            case "game.launch_exe":
                return LaunchExe(os, Str(p, "exeName"), Str(p, "args"));

            case "game.run_action":
                return RunAction(os, Str(p, "xml"));

            case "terminal.history":
                return TerminalHistory(os, IntParam(p, "lines") > 0 ? IntParam(p, "lines") : 15);

            case "events.get":
                return EventBuffer.Get(LongParam(p, "since"));

            case "registry.list":
                return Registry.List();

            case "modtool.list":
                return McpModuleScanner.List();

            case "modtool.call":
                return McpModuleScanner.Call(Str(p, "tool"), p);

            case "mission.detail":
                return MissionDetail(os);

            case "flags.get":
                return new Dictionary<string, object> { ["flags"] = Safe(os.Flags?.Flags) };

            case "flags.set":
            {
                string name = Str(p, "name");
                if (!os.Flags.HasFlag(name)) os.Flags.AddFlag(name);
                return new Dictionary<string, object> { ["ok"] = true, ["flag"] = name };
            }

            case "flags.clear":
            {
                string name = Str(p, "name");
                if (os.Flags.HasFlag(name)) os.Flags.RemoveFlag(name);
                return new Dictionary<string, object> { ["ok"] = true, ["flag"] = name };
            }

            case "mission.get":
                return GetMission(os);

            default:
                throw new ArgumentException($"unknown method: {req.Method}");
        }
    }

    // ---------------- 工具（各分文件方法共用） ----------------

    private static Computer FindComputer(OS os, string ip)
    {
        if (string.IsNullOrEmpty(ip)) return os.connectedComp;
        var comp = Programs.getComputer(os, ip);
        if (comp == null) throw new ArgumentException($"computer not found: {ip}");
        return comp;
    }

    private static Folder ResolveFolder(Computer comp, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return comp.files?.root;
        return comp.getFolderFromPath(path);
    }

    private static string Str(Dictionary<string, object> p, string key) =>
        p != null && p.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static int IntParam(Dictionary<string, object> p, string key)
    {
        if (p != null && p.TryGetValue(key, out var v))
        {
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (int.TryParse(v?.ToString(), out var n)) return n;
        }
        return 0;
    }

    private static bool BoolParam(Dictionary<string, object> p, string key)
    {
        if (p != null && p.TryGetValue(key, out var v))
        {
            if (v is bool b) return b;
            if (bool.TryParse(v?.ToString(), out var t)) return t;
        }
        return false;
    }

    private static long LongParam(Dictionary<string, object> p, string key)
    {
        if (p != null && p.TryGetValue(key, out var v))
        {
            if (v is long l) return l;
            if (v is int i) return i;
            if (long.TryParse(v?.ToString(), out var n)) return n;
        }
        return 0;
    }

    private static List<object> Safe(List<int> list) =>
        list == null ? new List<object>() : list.Cast<object>().ToList();

    private static List<object> Safe(List<string> list) =>
        list == null ? new List<object>() : list.Cast<object>().ToList();

    private static List<object> SafeBytes(List<byte> list) =>
        list == null ? new List<object>() : list.Cast<object>().ToList();

    private static object SafeInt(Func<object> getter)
    {
        try { return getter() ?? 0; } catch { return 0; }
    }

    private static object SafeBool(Func<object> getter)
    {
        try { return getter() ?? false; } catch { return false; }
    }

    private static string SafeStr(Func<object> getter)
    {
        try { return getter()?.ToString(); } catch { return null; }
    }

    private static string SafeTerminal(OS os)
    {
        try
        {
            return os.terminal?.currentLine;
        }
        catch { return null; }
    }

    /// <summary>RamModule 字段名有版本差异，用反射取，编译期不绑定成员名。</summary>
    private static object GetRamValue(OS os, string fieldName)
    {
        var ram = os?.ram;
        if (ram == null) return 0;
        var f = ram.GetType().GetField(fieldName);
        if (f == null)
        {
            var prop = ram.GetType().GetProperty(fieldName);
            return prop?.GetValue(ram, null) ?? 0;
        }
        return f.GetValue(ram) ?? 0;
    }
}
