using System.Reflection;

namespace HnpfMcpBridge;

/// <summary>
/// 扫描所有已加载插件程序集中的 [McpTool] 静态方法并建立调用表（第三层抽象）。
/// 每次 OSLoaded 都重建一次：扩展专属插件（如 KE 的连接器）可能在进入扩展时才加载，
/// 且扩展插件支持卸载/重载，缓存一次性结果会漏。
/// </summary>
public static class McpModuleScanner
{
    private static readonly Dictionary<string, MethodInfo> Tools = new();
    private static readonly Dictionary<string, string> Descriptions = new();

    /// <summary>
    /// 已退出扩展的程序集：**扫描时必须跳过**。
    ///
    /// <para>
    /// .NET 程序集一旦加载就**永远无法卸载**，扩展退出后它的 Assembly 仍留在
    /// <c>AppDomain.GetAssemblies()</c> 里。若照常扫描，会让"上个扩展的连接器"
    /// 在新扩展里又被注册一遍 —— 表现为退出带连接器的扩展、切到没连接器的扩展后
    /// 仍然打印 `McpTool registered`，且工具实际指向**旧程序集**（读到上一轮的静态状态）。
    /// </para>
    /// <para>
    /// 退出的扩展重新进入时会以**新的 Assembly 实例**加载（字节加载），对象身份不同，
    /// 不会被这个集合误伤。
    /// </para>
    /// </summary>
    private static readonly HashSet<Assembly> Retired = new();

    /// <summary>
    /// 清空缓存。扩展卸载前必须调用：Tools 里的 MethodInfo 强引用扩展程序集，
    /// 会让扩展插件无法被卸载（退出扩展后插件仍占用/文件锁定）。
    /// </summary>
    public static void ClearCache()
    {
        Tools.Clear();
        Descriptions.Clear();
    }

    /// <summary>
    /// 扩展退出时调用：把**所有扩展作用域的程序集**登记为 retired，然后清空缓存。
    ///
    /// <para>
    /// ⚠️ 不能只退役 <see cref="Tools"/> 里那份：实测（2026-10-17）Pathfinder 会把同一个
    /// 扩展插件程序集**加载出多个实例**（退出后日志里仍出现第二份连接器的 McpTool），
    /// 只记当前工具所属的那一个，扫描时还会从另一个实例注册回来。
    /// 因此这里遍历 AppDomain，凡"扩展作用域"的程序集一律退役。
    /// </para>
    ///
    /// <para>
    /// 判据不是"是否在插件表里" —— 退出时机在 quitGame 之前，此时扩展插件**还挂在**
    /// <c>HacknetChainloader.Instance.Plugins</c> 上，按插件表判断会把连接器误判成全局插件。
    /// 可靠判据是**加载方式**：扩展插件走字节加载（<c>Assembly.Location</c> 为空串），
    /// 全局 BepInEx 插件走 LoadFrom（Location 有真实路径）。
    /// </para>
    /// </summary>
    public static void RetireExtensionTools()
    {
        int retired = 0;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == typeof(McpModuleScanner).Assembly) continue;
                if (!IsExtensionScopedAssembly(asm)) continue;
                if (Retired.Add(asm)) retired++;
            }
        }
        catch { /* 枚举失败也要清缓存 */ }

        if (retired > 0)
        {
            try { HnpfMcpBridgePlugin.LogInfo($"McpTool: retired {retired} extension-scoped assembly(ies) (will not be re-scanned after exit)"); }
            catch { }
        }
        ClearCache();
    }

    /// <summary>
    /// 该程序集是否属于**扩展临时加载**的插件。
    ///
    /// <para>
    /// 判据：字节加载的程序集 <c>Location</c> 为**空串**（扩展插件走
    /// <c>Assembly.Load(File.ReadAllBytes(...))</c>，顺便避免文件锁）；全局 BepInEx 插件
    /// 由 <c>LoadFrom</c> 加载，Location 是真实路径。另兼容"路径在 Extensions/ 下"的情形。
    /// </para>
    /// </summary>
    private static bool IsExtensionScopedAssembly(Assembly asm)
    {
        try
        {
            string loc = asm.Location;
            if (string.IsNullOrEmpty(loc)) return true;
            string norm = loc.Replace('\\', '/');
            return norm.IndexOf("/Extensions/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return true; }
    }

    public static void Scan()
    {
        Tools.Clear();
        Descriptions.Clear();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // 跳过 bridge 自身与 Pathfinder/BepInEx/系统程序集（它们不会有 McpTool）
            if (asm == typeof(McpModuleScanner).Assembly) continue;
            // 跳过**已退出扩展**的程序集：它们无法卸载，但绝不该再被注册
            if (Retired.Contains(asm)) continue;
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }

            foreach (var type in types)
            {
                if (type == null || !type.IsClass) continue;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<McpToolAttribute>();
                    if (attr == null) continue;
                    // 只接受：无参 或 单个 Dictionary<string,object> 参数
                    var ps = method.GetParameters();
                    bool ok = ps.Length == 0 ||
                              (ps.Length == 1 && ps[0].ParameterType == typeof(Dictionary<string, object>));
                    if (!ok)
                    {
                        HnpfMcpBridgePlugin.LogWarn($"McpTool '{attr.Name}' ignored: unsupported signature on {method.DeclaringType?.Name}.{method.Name}");
                        continue;
                    }
                    if (!Tools.ContainsKey(attr.Name))
                    {
                        Tools[attr.Name] = method;
                        Descriptions[attr.Name] = attr.Description ?? "";
                        HnpfMcpBridgePlugin.LogInfo($"McpTool registered: {attr.Name} ({method.DeclaringType?.Name}.{method.Name})");
                    }
                }
            }
        }
    }

    public static object List()
    {
        // 惰性自愈：工具为空时重扫一次（扩展插件可能晚于 OSLoaded 加载，如主菜单程序化进扩展）
        if (Tools.Count == 0)
        {
            try { Scan(); } catch { }
        }
        var list = new List<object>();
        foreach (var kv in Tools)
            list.Add(new Dictionary<string, object> { ["name"] = kv.Key, ["description"] = Descriptions[kv.Key] });
        return new Dictionary<string, object> { ["tools"] = list, ["count"] = list.Count };
    }

    public static object Call(string tool, Dictionary<string, object> p)
    {
        if (string.IsNullOrEmpty(tool) || !Tools.TryGetValue(tool, out var method))
            throw new ArgumentException($"McpTool not found: {tool ?? "(null)"} (use modtool.list to see available tools)");

        object result;
        if (method.GetParameters().Length == 0)
            result = method.Invoke(null, null);
        else
            result = method.Invoke(null, new object[] { p ?? new Dictionary<string, object>() });

        // string 直接返回；object 走序列化；null → ok
        if (result is string s) return new Dictionary<string, object> { ["ok"] = true, ["tool"] = tool, ["result"] = s };
        if (result != null) return new Dictionary<string, object> { ["ok"] = true, ["tool"] = tool, ["result"] = result };
        return new Dictionary<string, object> { ["ok"] = true, ["tool"] = tool };
    }
}
