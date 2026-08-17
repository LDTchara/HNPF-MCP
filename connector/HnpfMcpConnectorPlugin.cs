using BepInEx;
using BepInEx.Hacknet;
using HnpfMcpBridge;

namespace HnpfMcpConnector;

/// <summary>
/// 通用 L3 连接器：把模组（如 KernelExtensions）的运行时独有状态暴露给 HNPF-MCP，模组本体零改动。
///
/// 设计（软依赖，单 DLL 服务任意模组）：
///  1. 【零模组编译引用】不引用任何模组 DLL——适配器全部用运行时反射访问模组类型
///     （简单名匹配 + 反射字段/方法，同 bridge 处理 MailServer 的思路，免疫版本与缺失）
///  2. 【无 [BepInDependency]】不声明任何模组依赖——连接器在任意扩展里都能加载；
///     某模组缺失时，对应 ke.* 工具返回 "模组未加载"，其余照常工作
///  3. bridge（HnpfMcpBridge）是全局插件，游戏启动即加载 → 不能声明 [BepInDependency]：
///     BepInEx 在扩展模式下做依赖校验时看不到全局插件，会误报 missing dependencies。
///     运行时对 HnpfMcpBridge.dll 的引用由 CLR 解析到已加载的全局副本。
///  4. [McpTool] 静态方法由 bridge 的 McpModuleScanner 在 OSLoaded 时反射发现；
///     本插件 Load() 时检测到桥会主动触发一次 Scan() 提前注册
///  5. 连接器自身检测桥：HacknetChainloader 插件表精确检测（IRC 增强同款）；
///     桥缺失 → 明确警告 + 无害等待；桥存在 → 主动 Scan
///
/// 安装：把 HnpfMcpConnector.dll 丢到目标模组所在扩展目录的 Plugins/ 下。
/// 使用：MCP 侧 modtool_list 查看、modtool_call 调用 ke.* 工具。
/// </summary>
[BepInPlugin(ModGUID, ModName, Version)]
public class HnpfMcpConnectorPlugin : HacknetPlugin
{
    public const string ModGUID = "com.HnpfMcp.Connector";
    public const string ModName = "HnpfMcpConnector";
    public const string Version = "0.1.0";

    public override bool Load()
    {
        // 检测 MCP 桥（IRC 增强同款方式：HacknetChainloader 插件表精确检测，比反射程序集更权威）
        // bridge 是全局插件，游戏启动即加载——要么在插件表里，要么缺失，判定必然准确
        var bridgeLoaded = HacknetChainloader.Instance?.Plugins?.ContainsKey("com.HnpfMcp.Bridge") == true;
        if (!bridgeLoaded)
        {
            Log.LogWarning($"[{ModName}] 检测到 HnpfMcpBridge 未安装！" +
                           "连接器已加载但 MCP 桥缺失：所有 MCP 工具（含 L1/L2）不可用（server 会报 [game offline]）。" +
                           "请将 HnpfMcpBridge.dll 放入 BepInEx/plugins/ 并重启游戏。");
            return true;   // 无害等待：桥装上后下次启动自动生效
        }

        // 桥在：主动触发一次 [McpTool] 扫描（不依赖 OSLoaded 时机），保证适配器尽快注册
        try
        {
            typeof(HnpfMcpBridge.McpModuleScanner)
                .GetMethod("Scan", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, null);
        }
        catch { /* 扫描失败不影响加载；OSLoaded 时 bridge 会再扫一次 */ }

        Log.LogInfo($"[{ModName}] loaded. bridge detected → [McpTool] scan triggered.");
        return true;
    }
}

/// <summary>反射辅助：按简单名找类型 + 反射读写字段/属性/方法（模组软依赖的核心）。</summary>
internal static class KeReflect
{
    private static readonly Dictionary<string, Type> TypeCache = new();

    /// <summary>按简单名在已加载程序集里找类型（缓存）。找不到返回 null。</summary>
    public static Type FindType(string simpleName)
    {
        if (TypeCache.TryGetValue(simpleName, out var cached)) return cached;
        Type found = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
                if (t.Name == simpleName) { found = t; break; }
            if (found != null) break;
        }
        TypeCache[simpleName] = found;
        return found;
    }

    public static object Get(object target, string member)
    {
        if (target == null) return null;
        var t = target.GetType();
        return t.GetField(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target)
            ?? t.GetProperty(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target, null);
    }

    public static object GetStatic(Type t, string member)
    {
        if (t == null) return null;
        return t.GetField(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null)
            ?? t.GetProperty(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
    }

    public static object Call(object target, string method)
    {
        if (target == null) return null;
        return target.GetType().GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(target, null);
    }

    public static object CallStatic(Type t, string method)
    {
        if (t == null) return null;
        return t.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
    }

    public static string SafeStr(Func<object> getter)
    {
        try { return getter()?.ToString(); } catch { return null; }
    }

    /// <summary>把 IEnumerable 摊成 List&lt;object&gt;（HashSet/Dictionary/List 通用）。</summary>
    public static List<object> ToList(object value)
    {
        var list = new List<object>();
        if (value is System.Collections.IEnumerable en)
            foreach (var item in en) list.Add(item);
        return list;
    }

    public static Dictionary<string, object> ModMissing(string modName) =>
        new() { ["error"] = $"{modName} 未加载（该工具需要对应模组）" };
}

/// <summary>模组适配器（[McpTool] 方法由 bridge 自动装配，无需任何注册代码）。
/// 当前适配 KernelExtensions；新增模组 = 新增一组 [McpTool] 静态方法 + KeReflect 反射。</summary>
public static class HnpfMcpAdapters
{
    [McpTool("ke.phaseswift.state", "PhaseSwift 运行时状态（运行中/场景/音乐相位/主题/已发现节点/黑名单）")]
    public static object PhaseSwiftState(Dictionary<string, object> p)
    {
        var m = KeReflect.FindType("PhaseSwiftManager");
        if (m == null) return KeReflect.ModMissing("KernelExtensions");
        return new Dictionary<string, object>
        {
            ["running"] = KeReflect.GetStatic(m, "IsRunning"),
            ["scene"] = KeReflect.GetStatic(m, "CurrentScene"),
            ["musicPhase"] = KeReflect.GetStatic(m, "CurrentMusicPhase"),
            ["theme"] = KeReflect.GetStatic(m, "DefaultTheme"),
            ["discoveredScenes"] = KeReflect.ToList(KeReflect.CallStatic(m, "GetSceneDiscoveredNodes")),
            ["blocked"] = KeReflect.ToList(KeReflect.CallStatic(m, "GetRuntimeBlockedNodes")),
        };
    }

    [McpTool("ke.phaseswift.detail", "PhaseSwift 深度状态（初始化/双轨/扩展根/配置/受控节点）")]
    public static object PhaseSwiftDetail(Dictionary<string, object> p)
    {
        var m = KeReflect.FindType("PhaseSwiftManager");
        if (m == null) return KeReflect.ModMissing("KernelExtensions");
        var cfg = KeReflect.GetStatic(m, "Config");
        return new Dictionary<string, object>
        {
            ["initialized"] = KeReflect.GetStatic(m, "IsInitialized"),
            ["useDualTrack"] = KeReflect.GetStatic(m, "UseDualTrack"),
            ["extensionRoot"] = KeReflect.GetStatic(m, "ExtensionRoot"),
            ["programName"] = KeReflect.Get(cfg, "ProgramName"),
            ["finishMode"] = KeReflect.Get(cfg, "FinishMode"),
            ["initialScene"] = KeReflect.Get(cfg, "InitialScene"),
            ["changeLayout"] = KeReflect.Get(cfg, "ChangeLayout"),
            ["startButtonText"] = KeReflect.Get(cfg, "StartButtonText"),
            ["controlledNodes"] = KeReflect.ToList(KeReflect.CallStatic(m, "GetControlledNodeIds")),
        };
    }

    [McpTool("ke.aircraft.state", "飞机系统状态（叠加层激活/各飞机海拔与失效状态）")]
    public static object AircraftState(Dictionary<string, object> p)
    {
        var fd = KeReflect.FindType("FlightDaemon");
        var go = KeReflect.FindType("GlobalAircraftOverlayManager");
        if (fd == null || go == null) return KeReflect.ModMissing("KernelExtensions");
        var flights = new List<object>();
        if (KeReflect.GetStatic(fd, "CompToDaemons") is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry kv in dict)
            {
                var daemon = kv.Value;
                if (daemon == null) continue;
                flights.Add(new Dictionary<string, object>
                {
                    ["comp"] = KeReflect.Get(kv.Key, "ip"),
                    ["altitude"] = KeReflect.Get(daemon, "CurrentAltitude"),
                    ["criticalFailure"] = KeReflect.Get(daemon, "IsInCriticalFirmwareFailure"),
                    ["crashImmediately"] = KeReflect.Get(daemon, "AircraftFallStartsImmediatley"),
                    ["height"] = KeReflect.Get(daemon, "H"),
                    ["identifier"] = KeReflect.SafeStr(() => KeReflect.Get(daemon, "Identifier")),
                });
            }
        }
        return new Dictionary<string, object>
        {
            ["overlayActive"] = KeReflect.GetStatic(go, "IsOverlayActive"),
            ["flights"] = flights,
        };
    }

    [McpTool("ke.customtrial.state", "当前 CustomTrial 试炼（配置名/已删除节点）")]
    public static object CustomTrialState(Dictionary<string, object> p)
    {
        var t = KeReflect.FindType("CustomTrialExe");
        if (t == null) return KeReflect.ModMissing("KernelExtensions");
        var trial = KeReflect.GetStatic(t, "CurrentInstance");
        return new Dictionary<string, object>
        {
            ["config"] = KeReflect.Get(trial, "CurrentConfigName"),
            ["deletedNodes"] = KeReflect.ToList(KeReflect.Call(trial, "GetDeletedNodeIndices")),
        };
    }

    [McpTool("ke.vm.state", "VM 感染模式与检查条件")]
    public static object VmState(Dictionary<string, object> p)
    {
        var t = KeReflect.FindType("VMInfectionManager");
        if (t == null) return KeReflect.ModMissing("KernelExtensions");
        var cfg = KeReflect.GetStatic(t, "CurrentConfig");
        if (cfg == null) return new Dictionary<string, object> { ["mode"] = "none" };
        return new Dictionary<string, object>
        {
            ["configName"] = KeReflect.Get(cfg, "ConfigName"),
            ["mode"] = KeReflect.SafeStr(() => KeReflect.Get(cfg, "Mode")?.ToString()),
            ["checkFilePath"] = KeReflect.Get(cfg, "CheckFilePath"),
            ["checkFilePattern"] = KeReflect.Get(cfg, "CheckFilePattern"),
        };
    }

    [McpTool("ke.config.get", "读取 KE-Config.xml 配置")]
    public static object KeConfig(Dictionary<string, object> p)
    {
        var t = KeReflect.FindType("KEConfigLoader");
        if (t == null) return KeReflect.ModMissing("KernelExtensions");
        return new Dictionary<string, object>
        {
            ["debug"] = KeReflect.GetStatic(t, "Debug"),
            ["skipVanillaIRCLogs"] = KeReflect.GetStatic(t, "SkipVanillaIRCLogs"),
            ["customImages"] = KeReflect.ToList(KeReflect.GetStatic(t, "CustomImages") as System.Collections.IEnumerable),
        };
    }

    [McpTool("ke.flag.find", "按前缀查找 Flag（如 PhaseSwift_ / Kernel_VMInfected_）")]
    public static object FlagFind(Dictionary<string, object> p)
    {
        var prefix = p.TryGetValue("prefix", out var v) ? v?.ToString() : "";
        var os = Hacknet.OS.currentInstance;
        if (os?.Flags == null || string.IsNullOrEmpty(prefix))
            return new Dictionary<string, object> { ["found"] = false };
        return new Dictionary<string, object> { ["flag"] = os.Flags.GetFlagStartingWith(prefix) };
    }
}
