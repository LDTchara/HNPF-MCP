using Hacknet;

namespace HnpfMcpBridge;

/// <summary>状态投影域：OS 状态 / 网络地图 / 节点详情 / 任务 / 终端历史。</summary>
public static partial class Executor
{
    private static object GetState(OS os) => new Dictionary<string, object>
    {
        ["connectedIP"] = os.connectedIP,
        ["connectedComp"] = os.connectedComp?.ip,
        ["connectedName"] = os.connectedComp?.name,
        ["homeNodeID"] = os.homeNodeID,
        ["navigationPath"] = Safe(os.navigationPath),
        ["thisComputerIP"] = os.thisComputer?.ip,
        ["ramFree"] = SafeInt(() => GetRamValue(os, "ramAvaliable")),
        ["ramTotal"] = SafeInt(() => GetRamValue(os, "ramTotal")),
        ["flags"] = Safe(os.Flags?.Flags),
        ["hasMission"] = os.currentMission != null,
        ["missionTitle"] = os.currentMission?.postingTitle,
        ["terminalText"] = SafeTerminal(os),
        ["admin"] = os.thisComputer != null && os.thisComputer.currentUser.type >= 2,
        ["trace"] = TraceInfo(os)
    };

    /// <summary>
    /// 追踪（trace）状态。
    ///
    /// <para>
    /// 实测背景：原版 <c>Computer.hostileActionTaken()</c> 只在
    /// **目标就是当前所连节点**且 <c>traceTime &gt; 0</c> 时才会 <c>traceTracker.start(traceTime)</c>。
    /// 所以"破解自己机器"会起追踪（玩家机 trace=300），而打过 `<trace time="-1"/>` 的节点永不追踪。
    /// 之前 MCP 看不到这个状态，测试时只能靠"等 300 秒看会不会蓝屏"——把 active/剩余时间暴露出来即可断言。
    /// </para>
    /// </summary>
    private static object TraceInfo(OS os)
    {
        try
        {
            object tt = ReadMember(os, "traceTracker");
            if (tt == null) return null;
            object activeObj = ReadMember(tt, "active");
            bool active = activeObj is bool b && b;
            object target = ReadMember(tt, "target");
            return new Dictionary<string, object>
            {
                ["active"] = active,
                ["remaining"] = ReadMember(tt, "timer"),
                ["startingTimer"] = ReadMember(tt, "startingTimer"),
                ["targetIp"] = ReadMember(target, "ip"),
                ["connectedTraceTime"] = ReadMember(os.connectedComp, "traceTime"),
            };
        }
        catch { return null; }
    }

    /// <summary>网络地图。summary=true 只返回核心字段（ip/idName/name/visible/connected）——大响应裁剪（B4）。</summary>
    private static object GetNetworkMap(OS os, bool summary = false)
    {
        var nodes = os.netMap?.nodes;
        var list = new List<object>();
        if (nodes != null)
        {
            foreach (var c in nodes)
            {
                if (c == null) continue;
                if (summary)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["ip"] = c.ip,
                        ["idName"] = c.idName,
                        ["name"] = c.name,
                        ["visible"] = os.netMap.visibleNodes?.Contains(os.netMap.nodes.IndexOf(c)) ?? false,
                        ["connected"] = c == os.connectedComp
                    });
                }
                else
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["ip"] = c.ip,
                        ["idName"] = c.idName,
                        ["name"] = c.name,
                        ["adminIP"] = c.adminIP,
                        ["links"] = Safe(c.links),
                        ["ports"] = Safe(c.ports),
                        ["portsOpen"] = SafeBytes(c.portsOpen),
                        ["users"] = c.users?.Count ?? 0,
                        ["hasFiles"] = c.files != null,
                        ["visible"] = os.netMap.visibleNodes?.Contains(os.netMap.nodes.IndexOf(c)) ?? false
                    });
                }
            }
        }
        return new Dictionary<string, object> { ["count"] = list.Count, ["nodes"] = list };
    }

    private static object GetComputer(OS os, string ip)
    {
        var comp = FindComputer(os, ip);
        if (comp == null) throw new ArgumentException($"computer not found: {ip}");

        var files = new List<object>();
        if (comp.files?.root != null)
            ProjectFolder(comp.files.root, 0, 8, files);   // 最多 8 层防爆栈

        var users = new List<object>();
        if (comp.users != null)
        {
            foreach (var u in comp.users)
                users.Add(new Dictionary<string, object>
                {
                    ["name"] = u.name,
                    ["pass"] = u.pass,
                    ["type"] = u.type
                });
        }

        var daemons = new List<object>();
        if (comp.daemons != null)
        {
            foreach (var d in comp.daemons)
                daemons.Add(new Dictionary<string, object>
                {
                    ["name"] = SafeStr(() => d.name)
                });
        }

        return new Dictionary<string, object>
        {
            ["ip"] = comp.ip,
            ["idName"] = comp.idName,
            ["name"] = comp.name,
            ["adminIP"] = comp.adminIP,
            ["adminPass"] = comp.adminPass,
            ["ports"] = PortStatesOf(comp),
            ["portsOpen"] = PortStatesOf(comp).Where(o => o is Dictionary<string, object> d && d.TryGetValue("cracked", out var c) && c is bool b && b).ToList(),
            ["currentUser"] = string.IsNullOrEmpty(comp.currentUser.name) ? null : comp.currentUser.name,
            ["currentUserType"] = comp.currentUser.type,
            ["users"] = users,
            ["daemons"] = daemons,
            ["files"] = files,
            ["firewallSolved"] = SafeBool(() => comp.firewall?.solved),
            ["firewall"] = FirewallInfo(comp),
            ["proxy"] = ProxyInfo(comp),
            ["links"] = Safe(comp.links)
        };
    }

    /// <summary>
    /// 防火墙详情。
    ///
    /// <para>
    /// 之前只暴露 <c>firewallSolved</c>，导致测试时**无法验证** <c>Solution="@random" Length="N"</c>
    /// 到底生成了几位、内容是不是字面量 "<c>@random</c>"（那个 bug 只能靠翻日志的长度猜）。
    /// 这里把 level / solutionLength / solution 原文一并给出 —— 这是调试桥，读这些是合理的。
    /// </para>
    /// </summary>
    private static object FirewallInfo(Computer comp)
    {
        try
        {
            var f = comp?.firewall;
            if (f == null) return null;
            return new Dictionary<string, object>
            {
                ["level"] = ReadMember(f, "level"),
                ["solved"] = ReadMember(f, "solved"),
                ["solutionLength"] = ReadMember(f, "solutionLength"),
                ["solution"] = ReadMember(f, "solution"),
                ["additionalDelay"] = ReadMember(f, "additionalDelay"),
                ["complexity"] = ReadMember(f, "complexity"),
            };
        }
        catch { return null; }
    }

    /// <summary>代理详情：是否装了代理、是否生效中、过载剩余 tick。用于验证 addProxy / 过载类 Action。</summary>
    private static object ProxyInfo(Computer comp)
    {
        try
        {
            if (comp == null || !comp.hasProxy) return null;
            return new Dictionary<string, object>
            {
                ["hasProxy"] = comp.hasProxy,
                ["proxyActive"] = comp.proxyActive,
                ["proxyTime"] = ReadMember(comp, "proxyTime"),
                ["proxyOverloadRemaining"] = ReadMember(comp, "proxyOverloadRemainingTime"),
            };
        }
        catch { return null; }
    }

    private static void ProjectFolder(Folder folder, int depth, int maxDepth, List<object> outList)
    {
        if (folder == null || depth > maxDepth) return;
        foreach (var f in folder.folders ?? new List<Folder>())
        {
            if (f == null) continue;
            var entry = new Dictionary<string, object>
            {
                ["type"] = "folder",
                ["name"] = f.name
            };
            var children = new List<object>();
            ProjectFolder(f, depth + 1, maxDepth, children);
            if (children.Count > 0) entry["children"] = children;
            outList.Add(entry);
        }
        foreach (var fe in folder.files ?? new List<FileEntry>())
        {
            if (fe == null) continue;
            outList.Add(new Dictionary<string, object>
            {
                ["type"] = "file",
                ["name"] = fe.name,
                ["size"] = fe.size
            });
        }
    }

    private static object TerminalHistory(OS os, int lines)
    {
        var list = os.terminal?.GetRecentTerminalHistoryList();
        if (list == null) return new Dictionary<string, object> { ["lines"] = new List<object>() };
        // ⚠️ 取**末尾** N 行，不是开头：这个列表是"从旧到新"的，用 Take() 会拿到最早的内容，
        // 排查时看到的是过期输出（2026-10-17 实测踩到：lines=12 时看不到最新的命令结果）。
        var trimmed = list.Skip(Math.Max(0, list.Count - lines)).ToList();
        return new Dictionary<string, object>
        {
            ["lines"] = trimmed,
            ["count"] = trimmed.Count,
            ["total"] = list.Count
        };
    }

    private static object GetMission(OS os)
    {
        var m = os.currentMission;
        if (m == null) return new Dictionary<string, object> { ["active"] = false };
        var goals = new List<object>();
        int complete = 0;
        try
        {
            foreach (var g in m.goals)
            {
                bool done = false;
                try { done = g.isComplete(); } catch { }
                if (done) complete++;
                goals.Add(new Dictionary<string, object> { ["complete"] = done });
            }
        }
        catch { }
        return new Dictionary<string, object>
        {
            ["active"] = true,
            ["title"] = m.postingTitle,
            ["goalCount"] = goals.Count,
            ["goalComplete"] = complete,
            ["goals"] = goals
        };
    }

    /// <summary>任务详情：标题 / 各目标类型与完成状态 / 任务 XML 全文（reloadGoalsSourceFile 指向的文件）。</summary>
    private static object MissionDetail(OS os)
    {
        var m = os.currentMission;
        if (m == null) return new Dictionary<string, object> { ["active"] = false };

        var goals = new List<object>();
        int complete = 0;
        try
        {
            foreach (var g in m.goals)
            {
                bool done = false;
                try { done = g.isComplete(); } catch { }
                if (done) complete++;
                goals.Add(new Dictionary<string, object>
                {
                    ["type"] = g.GetType().Name,
                    ["complete"] = done
                });
            }
        }
        catch { }

        string xmlPath = null;
        string xml = null;
        try
        {
            var f = m.GetType().GetField("reloadGoalsSourceFile");
            xmlPath = f?.GetValue(m) as string;
            if (!string.IsNullOrEmpty(xmlPath))
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var candidate in new[]
                {
                    Path.Combine(baseDir, xmlPath),
                    Path.Combine(baseDir, "Content", xmlPath),
                    Path.Combine(baseDir, "Content", "Missions", Path.GetFileName(xmlPath))
                })
                {
                    if (!File.Exists(candidate)) continue;
                    try { xml = File.ReadAllText(candidate); } catch { }
                    break;
                }
            }
        }
        catch { }

        return new Dictionary<string, object>
        {
            ["active"] = true,
            ["title"] = m.postingTitle,
            ["goalCount"] = goals.Count,
            ["goalComplete"] = complete,
            ["goals"] = goals,
            ["xmlPath"] = xmlPath,
            ["xml"] = xml
        };
    }
}
