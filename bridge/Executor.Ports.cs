using Hacknet;
using System.Collections;
using System.Reflection;

namespace HnpfMcpBridge;

/// <summary>
/// 端口域辅助：Pathfinder 的**每台机器**端口状态。
///
/// <para>
/// ⚠️ 两个坑（2026-10-17 实测）：
/// </para>
/// <list type="number">
/// <item><c>Computer.ports</c>（原版 <c>List&lt;int&gt;</c>）在 PF 接管后可能为空 ——
///   游戏内 <c>probe</c> 有端口，但 <c>get_computer</c> 的 <c>ports</c> 是 <c>[]</c>。</item>
/// <item><b>不能读全局端口注册表</b>：扩展节点可以写 <c>&lt;PFPorts&gt;</c> 把某协议
///   <b>重映射</b>到别的端口号和显示名（例：<c>ssh</c> → 2222「自定义SSH」、<c>web</c> → 8080
///   「我的Web服务器」）。全局注册表里只有默认值（ssh=22「SSH」、web=80「HTTP WebServer」），
///   按协议去全局表取就会显示错。</item>
/// </list>
/// <para>
/// 正确来源：<c>comp.GetPortStateDict()</c> → <c>Dictionary&lt;string, PortState&gt;</c>，
/// 每个 <c>PortState</c> 带**该机器实际生效**的 <c>PortNumber</c> / <c>DisplayName</c> / <c>Cracked</c>，
/// <c>Record</c> 指向全局 <c>PortRecord</c>（默认值，可作 originalPort 对照）。
/// </para>
/// <para>
/// 一律用运行时反射（同本仓库 MailServer 的做法）：libs/ 编译引用与游戏内 PF 版本可能不一致。
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

    /// <summary>该机器上的端口状态字典（协议名 → PortState）；取不到返回 null。</summary>
    private static IDictionary GetPortStateDict(Computer comp)
    {
        if (comp == null) return null;
        try
        {
            var m = ComputerExtType?.GetMethod("GetPortStateDict",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(Computer) }, null);
            return m?.Invoke(null, new object[] { comp }) as IDictionary;
        }
        catch { }
        return null;
    }

    /// <summary>某台机器上的端口状态（**该机器实际生效的**端口号/显示名/是否破解）。</summary>
    private static List<object> PortStatesOf(Computer comp)
    {
        var res = new List<object>();
        var dict = GetPortStateDict(comp);
        if (dict == null) return res;

        foreach (DictionaryEntry e in dict)
        {
            string proto = e.Key as string;
            object st = e.Value;
            if (string.IsNullOrEmpty(proto) || st == null) continue;

            object record = ReadMember(st, "Record");   // 全局默认（PortRecord）

            res.Add(new Dictionary<string, object>
            {
                ["protocol"] = proto,
                ["port"] = ReadMember(st, "PortNumber"),                       // 该机器实际端口（含重映射）
                ["displayName"] = ReadMember(st, "DisplayName"),               // 该机器实际显示名（含重映射）
                ["originalPort"] = ReadMember(record, "DefaultPortNumber"),    // 全局默认端口（对照用）
                ["cracked"] = ReadMember(st, "Cracked") is bool b && b,
                ["open"] = ReadMember(st, "Cracked") is bool b2 && b2,
            });
        }
        return res;
    }

    /// <summary>在这台机器上把端口号解析成协议名（按该机器的重映射值匹配）。</summary>
    private static string ProtocolFromPortNumberOnComputer(Computer comp, int number)
    {
        var dict = GetPortStateDict(comp);
        if (dict == null) return null;
        foreach (DictionaryEntry e in dict)
        {
            object st = e.Value;
            if (st == null) continue;
            if (ReadMember(st, "PortNumber") is int n && n == number) return e.Key as string;
            object record = ReadMember(st, "Record");
            if (ReadMember(record, "DefaultPortNumber") is int dn && dn == number) return e.Key as string;
        }
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
