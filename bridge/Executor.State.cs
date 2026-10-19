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
        // 修复：ramAvaliable / totalRam 是 OS 的字段（不是 RamModule 的），且 totalRam 不叫 ramTotal
        ["ramFree"] = SafeInt(() => os.ramAvaliable),
        ["ramTotal"] = SafeInt(() => os.totalRam),
        ["media"] = MediaInfo(),
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
                    ["name"] = SafeStr(() => d.name),
                    ["type"] = d.GetType().Name,
                    ["isListed"] = SafeBool(() => d.isListed),
                });
        }

        return new Dictionary<string, object>
        {
            ["ip"] = comp.ip,
            ["type"] = comp.GetType().Name,
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
        var all = ReadTerminalHistory(os);
        if (all == null) return new Dictionary<string, object> { ["lines"] = new List<object>() };
        // 取**末尾** N 行；total 是完整历史行数（原版 GetRecentTerminalHistoryList 只返回最旧 30 行，不能用）
        var start = Math.Max(0, all.Count - lines);
        var trimmed = all.GetRange(start, all.Count - start);
        return new Dictionary<string, object>
        {
            ["lines"] = trimmed,
            ["count"] = trimmed.Count,
            ["total"] = all.Count
        };
    }

    /// <summary>
    /// 读终端完整历史（从旧到新）。原版 <c>Terminal.GetRecentTerminalHistoryList()</c>
    /// 实现是 "for (i=0; i&lt;30 &amp;&amp; i&lt;history.Count; i++)"，返回的是**最旧** 30 行；
    /// 而 <c>history</c> 是 private 字段，这里反射取完整列表。
    /// 失败时退回原版接口（此时只能看到最旧 30 行，属降级，不是正确语义）。
    /// </summary>
    private static List<string> ReadTerminalHistory(OS os)
    {
        if (os?.terminal == null) return null;
        try
        {
            var field = typeof(OS).Assembly.GetType("Hacknet.Terminal")
                ?.GetField("history", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(os.terminal) is List<string> history)
                return history;
        }
        catch { }
        try { return os.terminal.GetRecentTerminalHistoryList(); }
        catch { return null; }
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
        var branches = new List<object>();
        try
        {
            if (os.branchMissions != null)
            {
                foreach (var bm in os.branchMissions)
                {
                    if (bm == null) continue;
                    int bt = 0, bc = 0;
                    try
                    {
                        foreach (var g in bm.goals)
                        {
                            bt++;
                            try { if (g.isComplete()) bc++; } catch { }
                        }
                    }
                    catch { }
                    branches.Add(new Dictionary<string, object>
                    {
                        ["title"] = bm.postingTitle,
                        ["goalCount"] = bt,
                        ["goalComplete"] = bc
                    });
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
            ["branchMissions"] = branches
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
