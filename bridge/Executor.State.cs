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
        ["admin"] = os.thisComputer != null && os.thisComputer.currentUser.type >= 2
    };

    private static object GetNetworkMap(OS os)
    {
        var nodes = os.netMap?.nodes;
        var list = new List<object>();
        if (nodes != null)
        {
            foreach (var c in nodes)
            {
                if (c == null) continue;
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
            ["ports"] = Safe(comp.ports),
            ["portsOpen"] = SafeBytes(comp.portsOpen),
            ["currentUser"] = string.IsNullOrEmpty(comp.currentUser.name) ? null : comp.currentUser.name,
            ["currentUserType"] = comp.currentUser.type,
            ["users"] = users,
            ["daemons"] = daemons,
            ["files"] = files,
            ["firewallSolved"] = SafeBool(() => comp.firewall?.solved),
            ["links"] = Safe(comp.links)
        };
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
        var trimmed = list.Take(lines).ToList();
        return new Dictionary<string, object>
        {
            ["lines"] = trimmed,
            ["count"] = trimmed.Count
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
