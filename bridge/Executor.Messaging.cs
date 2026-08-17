using Hacknet;

namespace HnpfMcpBridge;

/// <summary>
/// A4：IRC / 论坛读取（partial Executor）。
///   irc.read    { ip? }  → 读目标节点 IRC 消息日志（IRCDaemon.System.GetLogsFromFile）
///   board.read  { ip? }  → 读目标节点论坛线程（MessageBoardDaemon threads 文件夹）
/// </summary>
public static partial class Executor
{
    private static readonly System.Reflection.BindingFlags PrivFlags =
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

    // ---------------- IRC ----------------

    /// <summary>读目标节点 IRC 日志（消息历史）。ip 省略=当前连接/本机。</summary>
    private static object IrcRead(OS os, string ip)
    {
        var comp = MessagingTargetComp(os, ip);
        var daemon = FindDaemon(comp, "IRCDaemon");
        if (daemon == null)
            throw new ArgumentException($"no IRCDaemon on {comp?.ip}");

        // IRCDaemon.System → IRCSystem（外部类型，反射）
        var sys = GetFieldOrProp(daemon, "System");
        if (sys == null) return new Dictionary<string, object> { ["ip"] = comp?.ip, ["messages"] = new List<object>(), ["note"] = "IRCSystem 为 null（未初始化）" };

        // IRCSystem.GetLogsFromFile() → List<IRCSystem.IRCLogEntry>（Name/Message/Time）
        var method = sys.GetType().GetMethod("GetLogsFromFile", PrivFlags | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        object result = null;
        try { result = method?.Invoke(sys, null); } catch { }
        var messages = new List<object>();
        if (result is System.Collections.IEnumerable entries)
        {
            foreach (var e in entries)
            {
                var entry = new Dictionary<string, object>
                {
                    ["name"] = GetAny(e, "Author", "Name", "User")?.ToString(),
                    ["message"] = GetAny(e, "Message", "Text", "Content", "Body")?.ToString(),
                    ["time"] = GetAny(e, "Time", "Timestamp", "DateTime", "Date")?.ToString()
                };
                messages.Add(entry);
            }
        }
        return new Dictionary<string, object>
        {
            ["ip"] = comp?.ip,
            ["count"] = messages.Count,
            ["messages"] = messages
        };
    }

    // ---------------- 论坛 ----------------

    /// <summary>读目标节点论坛线程列表与内容。ip 省略=当前连接/本机。</summary>
    private static object BoardRead(OS os, string ip)
    {
        var comp = MessagingTargetComp(os, ip);
        var daemon = FindDaemon(comp, "MessageBoardDaemon");
        if (daemon == null)
            throw new ArgumentException($"no MessageBoardDaemon on {comp?.ip}");

        var boardName = GetFieldOrProp(daemon, "BoardName")?.ToString();
        var root = GetFieldOrProp(daemon, "rootFolder");
        Folder threads = null;
        try
        {
            // rootFolder.searchForFolder("Threads")（loadInit 同款）
            threads = root?.GetType()
                .GetMethod("searchForFolder", PrivFlags | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                ?.Invoke(root, new object[] { "Threads" }) as Folder;
        }
        catch { }
        threads ??= GetFieldOrProp(daemon, "threadsFolder") as Folder;

        var threadsList = new List<object>();
        if (threads?.files != null)
        {
            foreach (var f in threads.files)
            {
                var data = f?.data;
                threadsList.Add(new Dictionary<string, object>
                {
                    ["name"] = f?.name,
                    ["content"] = data
                });
            }
        }
        return new Dictionary<string, object>
        {
            ["ip"] = comp?.ip,
            ["board"] = boardName,
            ["threadCount"] = threadsList.Count,
            ["threads"] = threadsList
        };
    }

    // ---------------- helpers ----------------

    /// <summary>定位消息类 daemon 所在计算机：指定 ip → 当前连接 → 本机。</summary>
    private static Computer MessagingTargetComp(OS os, string ip)
    {
        if (!string.IsNullOrEmpty(ip)) return FindComputer(os, ip);
        return os.connectedComp ?? os.thisComputer;
    }

    /// <summary>按类型简单名找 comp 上的 daemon（运行时类型名匹配，绕开 libs/runtime identity 差异）。</summary>
    private static object FindDaemon(Computer comp, string simpleTypeName)
    {
        if (comp?.daemons == null) return null;
        foreach (var d in comp.daemons)
        {
            if (d == null) continue;
            var t = d.GetType();
            if (t.Name == simpleTypeName || t.FullName?.Contains(simpleTypeName) == true) return d;
        }
        return null;
    }

    /// <summary>属性优先、字段兜底取值（Patcher 公开化后字段可能是 public 或 nonpublic）。</summary>
    private static object GetFieldOrProp(object obj, string name)
    {
        if (obj == null) return null;
        var t = obj.GetType();
        try
        {
            var p = t.GetProperty(name, PrivFlags | System.Reflection.BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
        }
        catch { }
        try
        {
            var f = t.GetField(name, PrivFlags | System.Reflection.BindingFlags.Instance);
            if (f != null) return f.GetValue(obj);
        }
        catch { }
        return null;
    }

    /// <summary>按候选名依次取值，返回第一个非 null。</summary>
    private static object GetAny(object obj, params string[] names)
    {
        if (obj == null) return null;
        foreach (var n in names)
        {
            var v = GetFieldOrProp(obj, n);
            if (v != null) return v;
        }
        return null;
    }
}
