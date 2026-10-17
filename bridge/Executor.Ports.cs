using Hacknet;
using System.Collections;
using System.Reflection;

namespace HnpfMcpBridge;

/// <summary>
/// 端口域辅助：Pathfinder 的端口状态。
///
/// <para>
/// ⚠️ 不要读 <c>Computer.ports</c>（原版 <c>List&lt;int&gt;</c>）—— Pathfinder 接管端口后，
/// 扩展节点的端口由 <c>Pathfinder.Port.PortManager</c> 管理，<c>comp.ports</c> 可能为空。
/// 2026-10-17 实测：游戏内 <c>probe</c> 能看到 21/22/25/8080，但 <c>get_computer</c> 的
/// <c>ports</c> 是 <c>[]</c>，<c>open_port</c> 也因此一直报"端口不存在"。
/// </para>
/// <para>
/// 一律用运行时类型名 + 反射访问（同本仓库 MailServer 的做法）：libs/ 编译引用与游戏内
/// Pathfinder 版本可能不一致，编译期直接引用容易因签名不同而在运行时炸掉。
/// </para>
/// </summary>
public static partial class Executor
{
    private static Type FindRuntimeType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    private static Type PortManagerType => FindRuntimeType("Pathfinder.Port.PortManager");
    private static Type ComputerExtType => FindRuntimeType("Pathfinder.Port.ComputerExtensions");

    /// <summary>读对象上的属性/字段（公开与非公开都试；Pathfinder 的 Patcher 会把字段改成 public）。</summary>
    private static object ReadMember(object obj, string name)
    {
        if (obj == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var t = obj.GetType();
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var p = t.GetProperty(name, F);
            if (p != null && p.CanRead) return p.GetValue(obj);
            var f = t.GetField(name, F);
            if (f != null) return f.GetValue(obj);
        }
        catch { }
        return null;
    }

    /// <summary>所有已注册端口记录（内置 + 模组注册），每项是 (protocol, port, displayName)。</summary>
    private static List<Dictionary<string, object>> AllPortRecords()
    {
        var list = new List<Dictionary<string, object>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(object record)
        {
            if (record == null) return;
            string proto = ReadMember(record, "Protocol") as string;
            if (string.IsNullOrEmpty(proto) || !seen.Add(proto)) return;
            object num = ReadMember(record, "DefaultPortNumber") ?? ReadMember(record, "PortNumber");
            string disp = ReadMember(record, "DefaultDisplayName") as string
                       ?? ReadMember(record, "DisplayName") as string;
            list.Add(new Dictionary<string, object>
            {
                ["protocol"] = proto,
                ["port"] = num,
                ["displayName"] = disp
            });
        }

        try
        {
            // 1) 内置端口：Pathfinder.Port.ComputerExtensions.OGPorts（Dictionary）
            var ce = ComputerExtType;
            var ogField = ce?.GetField("OGPorts", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (ogField?.GetValue(null) is IDictionary og)
                foreach (DictionaryEntry e in og) Add(e.Value);

            // 2) 模组注册端口：PortManager.CustomPorts（AssemblyAssociatedList<PortRecord>）→ AllItems
            var pm = PortManagerType;
            var customField = pm?.GetField("CustomPorts", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object registry = customField?.GetValue(null);
            if (registry != null && ReadMember(registry, "AllItems") is IEnumerable items)
                foreach (var r in items) Add(r);
        }
        catch { }

        return list;
    }

    /// <summary>某台机器上的端口状态（只在机器上存在的端口）。</summary>
    private static List<object> PortStatesOf(Computer comp)
    {
        var res = new List<object>();
        if (comp == null) return res;

        // comp.GetPortState(protocol)：Pathfinder 的扩展方法，返回 PortState（不存在返回 null）
        var getState = ComputerExtType?
            .GetMethod("GetPortState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Computer), typeof(string) }, null);

        foreach (var rec in AllPortRecords())
        {
            string proto = rec["protocol"] as string;
            if (string.IsNullOrEmpty(proto)) continue;

            object state = null;
            try { state = getState?.Invoke(null, new object[] { comp, proto }); } catch { }
            if (state == null) continue;   // 这台机器上没这个端口

            bool cracked = false;
            try { cracked = ReadMember(state, "Cracked") is bool b && b; } catch { }

            res.Add(new Dictionary<string, object>
            {
                ["protocol"] = proto,
                ["port"] = rec["port"],
                ["displayName"] = rec["displayName"],
                ["cracked"] = cracked,
                ["open"] = cracked
            });
        }
        return res;
    }

    /// <summary>把端口号解析成协议名（PortManager.GetPortRecordFromNumber → .Protocol）。</summary>
    private static string ProtocolFromPortNumber(int number)
    {
        try
        {
            var pm = PortManagerType;
            var m = pm?.GetMethod("GetPortRecordFromNumber",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(int) }, null);
            var rec = m?.Invoke(null, new object[] { number });
            return ReadMember(rec, "Protocol") as string;
        }
        catch { }
        return null;
    }

    /// <summary>把协议名解析成端口号。</summary>
    private static int? PortNumberFromProtocol(string protocol)
    {
        try
        {
            var pm = PortManagerType;
            var m = pm?.GetMethod("GetPortRecordFromProtocol",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);
            var rec = m?.Invoke(null, new object[] { protocol });
            object n = ReadMember(rec, "DefaultPortNumber") ?? ReadMember(rec, "PortNumber");
            return n == null ? (int?)null : Convert.ToInt32(n);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 打开/关闭端口：优先 Pathfinder 的**协议名** API（扩展方法），
    /// 退回原版按**端口号**的实例方法。
    /// </summary>
    private static void SetPortState(Computer comp, string protocol, int number, string sourceIp, bool open)
    {
        string name = open ? "openPort" : "closePort";

        // 1) Pathfinder 扩展方法：openPort(this Computer, string protocol, string sourceIP)
        try
        {
            var m = ComputerExtType?.GetMethod(name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Computer), typeof(string), typeof(string) }, null);
            if (m != null)
            {
                m.Invoke(null, new object[] { comp, protocol, sourceIp });
                return;
            }
        }
        catch { }

        // 2) 原版实例方法：comp.openPort(int port, string sourceIP)
        try
        {
            var m = typeof(Computer).GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(int), typeof(string) }, null);
            if (m != null)
            {
                m.Invoke(comp, new object[] { number, sourceIp });
                return;
            }
        }
        catch { }

        throw new ArgumentException($"neither Pathfinder nor vanilla '{name}' is available on this build");
    }
}
