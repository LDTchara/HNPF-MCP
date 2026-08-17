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

    public static void Scan()
    {
        Tools.Clear();
        Descriptions.Clear();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // 跳过 bridge 自身与 Pathfinder/BepInEx/系统程序集（它们不会有 McpTool）
            if (asm == typeof(McpModuleScanner).Assembly) continue;
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
