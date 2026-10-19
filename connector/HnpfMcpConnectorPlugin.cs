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
///     某模组缺失时，对应 ke.* 工具返回 "模组未加载/类型缺失"，其余照常工作
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
    public const string Version = "0.4.0";

    public override bool Load()
    {
        // 检测 MCP 桥（IRC 增强同款方式：HacknetChainloader 插件表精确检测，比反射程序集更权威）
        // bridge 是全局插件，游戏启动即加载——要么在插件表里，要么缺失，判定必然准确
        var bridgeLoaded = HacknetChainloader.Instance?.Plugins?.ContainsKey("com.HnpfMcp.Bridge") == true;
        if (!bridgeLoaded)
        {
            // BepInEx 控制台的 GBK/UTF-8 混排会让中文乱码，日志文案统一用英文 + 半角标点
            Log.LogWarning($"[{ModName}] HnpfMcpBridge is not installed! " +
                           "The connector is loaded but the MCP bridge is missing: all MCP tools (including L1/L2) are unavailable " +
                           "(the server will report [game offline]). " +
                           "Put HnpfMcpBridge.dll into BepInEx/plugins/ and restart the game.");
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

        Log.LogInfo($"[{ModName}] loaded. bridge detected -> [McpTool] scan triggered.");
        return true;
    }

    /// <summary>
    /// 卸载时清理本插件留在全局状态里的两处痕迹：
    ///  1. KeReflect 的类型缓存——缓存里的 Type 来自本扩展加载周期的 KE 程序集，
    ///     不清会跨加载周期持有旧 Type（重进扩展后读到上一轮的静态状态，也影响卸载完整性）；
    ///  2. bridge 的 [McpTool] 扫描表——**手动从 UI 退出扩展**时不走 bridge 的退出钩子，
    ///     旧 ke.* 的 MethodInfo 仍留在扫描表里、旧连接器程序集也仍在 AppDomain 中，
    ///     下次 OSLoaded 扫描会把它们重新注册回来（表现为退出扩展后仍看到旧 ke.*，
    ///     且工具实际指向上一轮的程序集）。这里主动通知 bridge 把扩展作用域程序集登记为
    ///     retired 并清空工具表。
    /// </summary>
    public override bool Unload()
    {
        KeReflect.ClearCache();
        RetireBridgeTools();
        return base.Unload();
    }

    /// <summary>
    /// 通知 bridge 退役扩展作用域程序集。用反射而不是硬调用：bridge 版本较旧时没有该方法，
    /// 硬调用会让旧 bridge 下的 Unload 直接抛 MissingMethodException。
    /// </summary>
    private static void RetireBridgeTools()
    {
        try
        {
            typeof(HnpfMcpBridge.McpModuleScanner)
                .GetMethod("RetireExtensionTools", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, null);
        }
        catch { /* 桥缺失或旧版无此方法：卸载本身不受影响 */ }
    }
}

/// <summary>反射辅助：按简单名找类型 + 反射读写字段/属性/方法（模组软依赖的核心）。</summary>
internal static class KeReflect
{
    private static readonly Dictionary<string, Type> TypeCache = new();

    /// <summary>
    /// 上次查找时看到的 KE 程序集。KE 被重载后 AppDomain 里可能同时存在新旧副本
    /// （旧副本不一定真卸载），用它做“世代”标记：一旦换了就整体清缓存。
    /// </summary>
    private static System.Reflection.Assembly _keAsmSnapshot;

    /// <summary>清空类型缓存（插件卸载时调用，避免跨加载周期持有旧 Type）。</summary>
    public static void ClearCache()
    {
        TypeCache.Clear();
        _keAsmSnapshot = null;
    }

    /// <summary>
    /// 当前**正在运行**的 KE 程序集。返回 null 表示 KE 未加载（或当前扩展不带 KE）。
    ///
    /// <para>
    /// 只认 <c>HacknetChainloader.Instance.Plugins</c> 里的 KE 插件实例所属的程序集——那就是
    /// 正在跑的那一份。不能回退到 <c>AppDomain.GetAssemblies()</c> 里的同名程序集：.NET 程序集
    /// 加载后无法卸载，重进扩展 / 换扩展后 AppDomain 里会堆着多份 KE 副本，枚举顺序不保证，
    /// 读到旧副本就会返回上一轮的静态状态（连接器全局安装、退出扩展后尤其明显）。
    /// </para>
    /// </summary>
    private static System.Reflection.Assembly FindKeAssembly()
    {
        try
        {
            var loader = BepInEx.Hacknet.HacknetChainloader.Instance;
            var plugins = loader?.Plugins;
            if (plugins != null)
            {
                foreach (var kv in plugins)
                {
                    object v = kv.Value;
                    if (v == null) continue;

                    // 插件表的值在不同版本里可能是 PluginInfo（有 Instance 属性）、
                    // 直接是插件实例、甚至 Type。三种都试。
                    object inst = null;
                    try { inst = v.GetType().GetProperty("Instance")?.GetValue(v); } catch { }
                    object owner = inst ?? (v is Type ? null : v);
                    var asm = owner?.GetType().Assembly ?? (v as Type)?.Assembly;
                    if (asm == null) continue;

                    var n = asm.GetName().Name ?? "";
                    if (n.StartsWith("KernelExtensions", StringComparison.OrdinalIgnoreCase)) return asm;
                }
            }
        }
        catch { /* 插件表不可用时按「KE 未加载」处理 */ }

        // 插件表里没有 KE 插件实例 = 当前没有 KE 在跑（全局安装连接器后退出扩展 / 当前扩展不带 KE）。
        // 此时必须报 ModMissing，不能沿用上一次缓存：那属于上一个加载周期，读到的是历史状态。
        TypeCache.Clear();
        _keAsmSnapshot = null;
        return null;
    }

    /// <summary>
    /// 按简单名找类型（缓存）。找不到返回 null（调用方报 ModMissing）。
    ///
    /// <para>
    /// **只接受「当前 KE 程序集」里的类型**：其他模组可能有同名类（如 ConfigLoader），
    /// 按 AppDomain 枚举顺序取第一个会命中别家类型 → 成员读不到或读到别人的状态。
    /// 只有当前 KE 程序集里确实没有时，才在名称以 <c>KernelExtensions</c> 开头的其他程序集里
    /// 兜底（多程序集拆分的兼容路径）；**绝不接受任何其他程序集的同名类型**。
    /// </para>
    /// <para>
    /// **世代校验**：KE 重载后旧副本可能仍在 AppDomain 里，命中旧 Type 会读到上一轮的静态状态。
    /// 因此每次调用先比对当前 KE 程序集是否换了，换了就整体清缓存。
    /// </para>
    /// </summary>
    public static Type FindType(string simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;

        // 世代校验：KE 程序集换代（重载）→ 缓存整体失效
        var keAsm = FindKeAssembly();
        if (!ReferenceEquals(keAsm, _keAsmSnapshot))
        {
            TypeCache.Clear();
            _keAsmSnapshot = keAsm;
        }

        if (TypeCache.TryGetValue(simpleName, out var cached)) return cached;

        Type result = null;
        if (keAsm != null)
        {
            result = FindTypeInAssembly(keAsm, simpleName);

            if (result == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (ReferenceEquals(asm, keAsm)) continue;
                    string n;
                    try { n = asm.GetName().Name ?? ""; } catch { continue; }
                    if (!n.StartsWith("KernelExtensions", StringComparison.OrdinalIgnoreCase)) continue;
                    result = FindTypeInAssembly(asm, simpleName);
                    if (result != null) break;
                }
            }
        }

        TypeCache[simpleName] = result;
        return result;
    }

    /// <summary>在单个程序集里按简单名找类型（部分类型加载失败时仍用能拿到的那部分）。</summary>
    private static Type FindTypeInAssembly(System.Reflection.Assembly asm, string simpleName)
    {
        if (asm == null) return null;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
        catch { return null; }

        if (types == null) return null;
        foreach (var t in types)
            if (t != null && t.Name == simpleName) return t;
        return null;
    }

    /// <summary>诊断用：类型所在程序集名（排查同名类型命错）。</summary>
    public static string AssemblyOf(string simpleName) =>
        FindType(simpleName)?.Assembly?.GetName()?.Name;

    // 读取/调用的统一约定：任何反射异常都收敛成 null（「读不到」），绝不让异常从工具方法逃出去。
    // 模组版本差异（成员改名 / 类型缺失）应表现为字段缺省，而不是 MCP 侧一条 invocation failed。

    public static object Get(object target, string member)
    {
        if (target == null) return null;
        try
        {
            var t = target.GetType();
            return t.GetField(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target)
                ?? t.GetProperty(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target, null);
        }
        catch { return null; }
    }

    public static object GetStatic(Type t, string member)
    {
        if (t == null) return null;
        try
        {
            return t.GetField(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null)
                ?? t.GetProperty(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
        }
        catch { return null; }
    }

    public static object Call(object target, string method)
    {
        if (target == null) return null;
        try
        {
            return target.GetType().GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.Invoke(target, null);
        }
        catch { return null; }
    }

    public static object CallStatic(Type t, string method)
    {
        if (t == null) return null;
        try
        {
            return t.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
        }
        catch { return null; }
    }

    /// <summary>带参数调用静态方法（如 ClockManager.GetPersistentState(os)）。</summary>
    public static object CallStaticArgs(Type t, string method, object[] args)
    {
        if (t == null) return null;
        try
        {
            var ms = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            foreach (var m in ms)
                if (m.Name == method && m.GetParameters().Length == args.Length)
                    return m.Invoke(null, args);
        }
        catch { return null; }
        return null;
    }

    public static string SafeStr(Func<object> getter)
    {
        try { return getter()?.ToString(); } catch { return null; }
    }

    /// <summary>把 IEnumerable 摊成 List&lt;object&gt;（HashSet/Dictionary/List 通用）。</summary>
    public static List<object> ToList(object value)
    {
        var list = new List<object>();
        try
        {
            if (value is System.Collections.IEnumerable en)
                foreach (var item in en) list.Add(item);
        }
        catch { /* 枚举中途异常：返回已收集到的部分 */ }
        return list;
    }

    /// <summary>
    /// 统一的「模组不可用」返回。消息里点名需要的类型，AI 才能判断是「没装模组」
    /// 还是「装了但版本不匹配 / 类型改名」；<c>keLoaded</c> 给出机器可读的区分。
    /// </summary>
    public static Dictionary<string, object> ModMissing(string typeName = null)
    {
        bool keLoaded = FindKeAssembly() != null;
        var result = new Dictionary<string, object>
        {
            ["error"] = typeName == null
                ? "KernelExtensions 未加载或类型缺失"
                : $"KernelExtensions 未加载或类型缺失（需要类型: {typeName}）",
            ["keLoaded"] = keLoaded,
        };
        if (typeName != null) result["missingType"] = typeName;
        return result;
    }
}

/// <summary>模组适配器（[McpTool] 方法由 bridge 自动装配，无需任何注册代码）。
/// 当前适配 KernelExtensions；新增模组 = 新增一组 [McpTool] 静态方法 + KeReflect 反射。</summary>
public static class HnpfMcpAdapters
{
    [McpTool("ke.phaseswift.state", "PhaseSwift 运行时状态（运行中/场景/音乐相位/主题/已发现节点/黑名单）")]
    public static object PhaseSwiftState(Dictionary<string, object> p)
    {
        var m = KeReflect.FindType("PhaseSwiftManager");
        if (m == null) return KeReflect.ModMissing("PhaseSwiftManager");
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
        if (m == null) return KeReflect.ModMissing("PhaseSwiftManager");
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
        if (fd == null) return KeReflect.ModMissing("FlightDaemon");
        var go = KeReflect.FindType("GlobalAircraftOverlayManager");
        if (go == null) return KeReflect.ModMissing("GlobalAircraftOverlayManager");

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

    [McpTool("ke.customtrial.state", "CustomTrial 试炼（运行中/配置名/已删除节点；静态引用与当前 OS 对不上时标 stale）")]
    public static object CustomTrialState(Dictionary<string, object> p)
    {
        var t = KeReflect.FindType("CustomTrialExe");
        if (t == null) return KeReflect.ModMissing("CustomTrialExe");

        var trial = KeReflect.GetStatic(t, "CurrentInstance");
        bool running = trial != null;
        var config = KeReflect.Get(trial, "CurrentConfigName");
        var deletedNodes = KeReflect.ToList(KeReflect.Call(trial, "GetDeletedNodeIndices"));

        // KE 的 CurrentInstance 从不清空（只在实例初始化时赋值）：试炼结束 / 换 OS 后它仍指向上一次的实例。
        // 因此还要对照当前 OS 的 exe 列表——对不上就说明是历史引用，标 stale，别把它当成当前试炼。
        bool inCurrentOs = running && FindRunningExes(t).Exists(e => ReferenceEquals(e, trial));
        bool stale = running ? !inCurrentOs : (config != null || deletedNodes.Count > 0);

        return new Dictionary<string, object>
        {
            ["running"] = running,
            ["inCurrentOs"] = inCurrentOs,
            ["stale"] = stale,
            ["config"] = config,
            ["deletedNodes"] = deletedNodes,
        };
    }

    [McpTool("ke.vm.state", "VM 恢复模式与检查条件（RecoveryMode：FileDeletion / FileExists / Password）")]
    public static object VmState(Dictionary<string, object> p)
    {
        var t = KeReflect.FindType("VMInfectionManager");
        if (t == null) return KeReflect.ModMissing("VMInfectionManager");
        var cfg = KeReflect.GetStatic(t, "CurrentConfig");
        if (cfg == null) return new Dictionary<string, object> { ["mode"] = "none" };
        // 旧版配置有 ConfigName 字段，现已删除；配置身份改由「相对扩展根的源路径」标识。
        // SourcePath 是运行时字段，未加载 / 旧版本可能为空——回退 null，别让空串冒充路径。
        var sourcePath = KeReflect.Get(cfg, "SourcePath") as string;
        if (string.IsNullOrEmpty(sourcePath)) sourcePath = null;
        return new Dictionary<string, object>
        {
            ["sourcePath"] = sourcePath,
            ["mode"] = KeReflect.SafeStr(() => KeReflect.Get(cfg, "Mode")?.ToString()),
            ["checkFilePath"] = KeReflect.Get(cfg, "CheckFilePath"),
            ["checkFilePattern"] = KeReflect.Get(cfg, "CheckFilePattern"),
        };
    }

    // ================= 运行期 exe 实例定位（PortHackV2 / ProxyKiller / CustomTrial）=================

    /// <summary>
    /// 找当前 OS 里正在运行的指定类型 exe 实例（Hacknet 的 os.exes），返回全部匹配。
    ///
    /// <para>
    /// 按 exe 自身注入的 <c>os</c> 字段校验归属：<c>OS.currentInstance</c> 是全局，切换 / 退出
    /// 过程中可能指向另一个 OS，不校验会把别的实例（或上一轮的遗留）当成「当前正在运行」。
    /// 同一程序允许并存多个实例（如多开 PortHackV2），所以返回列表而不是第一个。
    /// </para>
    /// </summary>
    private static List<object> FindRunningExes(Type exeType)
    {
        var result = new List<object>();
        try
        {
            var os = Hacknet.OS.currentInstance;
            if (os == null || exeType == null) return result;
            var f = typeof(Hacknet.OS).GetField("exes",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            if (f?.GetValue(os) is System.Collections.IList list)
                foreach (var e in list)
                {
                    if (e == null) continue;
                    var et = e.GetType();
                    if (et != exeType && et.Name != exeType.Name) continue;
                    // 归属校验：exe 由自己的 os 实例驱动（Module.os），必须就是当前 OS
                    if (!ReferenceEquals(KeReflect.Get(e, "os"), os)) continue;
                    result.Add(e);
                }
        }
        catch { }
        return result;
    }

    private static float ToFloat(object v)
    {
        try { return v == null ? 0f : Convert.ToSingle(v); } catch { return 0f; }
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    [McpTool("ke.porthackv2.state", "PortHackV2 运行期状态：状态机 / 目标 / 根级时长与进度 / 各协议进度 / 前置是否满足（多实例时 instances 列出全部）")]
    public static object PortHackV2State(Dictionary<string, object> p)
    {
        var t = KeReflect.FindType("PortHackV2Exe");
        if (t == null) return KeReflect.ModMissing("PortHackV2Exe");

        var instances = FindRunningExes(t);
        if (instances.Count == 0) return new Dictionary<string, object> { ["running"] = false };

        var all = new List<object>();
        foreach (var inst in instances) all.Add(BuildPortHackV2State(inst));

        // 多实例：顶层字段取第一个运行中的实例，完整列表放 instances（单实例时不加，保持原 schema）
        var top = new Dictionary<string, object>((Dictionary<string, object>)all[0]);
        if (all.Count > 1) top["instances"] = all;
        return top;
    }

    private static Dictionary<string, object> BuildPortHackV2State(object inst)
    {
        var cfg = KeReflect.Get(inst, "CurrentConfig");
        object state = KeReflect.Get(inst, "CurrentState");
        float crackTime = ToFloat(KeReflect.Get(inst, "_crackTime"));
        float rootTime = ToFloat(KeReflect.Get(cfg, "Time"));
        string targetIp = KeReflect.Get(inst, "_targetIp") as string;

        var protos = new List<object>();
        foreach (var rt in KeReflect.ToList(KeReflect.Get(inst, "_runtimes")))
        {
            var def = KeReflect.Get(rt, "Def");
            protos.Add(new Dictionary<string, object>
            {
                ["name"] = KeReflect.Get(def, "Name"),
                ["startDelay"] = ToFloat(KeReflect.Get(def, "StartDelay")),
                ["rngPercent"] = Math.Round(ToFloat(KeReflect.Get(def, "SuccessChance")) * 100f, 1),
                ["timeIfSuccess"] = ToFloat(KeReflect.Get(def, "TimeIfSuccess")),
                ["timeIfFail"] = ToFloat(KeReflect.Get(def, "TimeIfFail")),
                ["delayElapsed"] = Math.Round(ToFloat(KeReflect.Get(rt, "DelayElapsed")), 2),
                ["elapsed"] = Math.Round(ToFloat(KeReflect.Get(rt, "Elapsed")), 2),
                ["started"] = KeReflect.Get(rt, "Started"),
                ["finished"] = KeReflect.Get(rt, "Finished"),
                ["succeeded"] = KeReflect.Get(rt, "Succeeded"),
                ["isDone"] = KeReflect.Get(rt, "IsDone"),
            });
        }

        bool prereq = false;
        try { prereq = KeReflect.Call(inst, "AllPrerequisitesMet") is bool b && b; } catch { }

        return new Dictionary<string, object>
        {
            ["running"] = true,
            ["state"] = state?.ToString(),
            ["configName"] = KeReflect.Get(cfg, "SourceName"),
            ["programName"] = KeReflect.Get(cfg, "ProgramName"),
            ["targetIp"] = targetIp,
            ["rootTime"] = rootTime,
            ["crackElapsed"] = Math.Round(crackTime, 3),
            ["progress"] = rootTime > 0f ? Math.Round(Clamp01(crackTime / rootTime), 3) : (object)null,
            ["showEstimate"] = KeReflect.Get(cfg, "ShowEstimate"),
            ["prerequisitesMet"] = prereq,
            ["protocols"] = protos,
        };
    }

    [McpTool("ke.proxykiller.state", "ProxyKiller 运行期状态：RAM 爬升 / 当前倍速 / 自过载档位 / 所盯节点（多实例时 instances 列出全部）")]
    public static object ProxyKillerState(Dictionary<string, object> p)
    {
        var t = KeReflect.FindType("ProxyKillerExe");
        if (t == null) return KeReflect.ModMissing("ProxyKillerExe");

        var instances = FindRunningExes(t);
        if (instances.Count == 0) return new Dictionary<string, object> { ["running"] = false };

        var all = new List<object>();
        foreach (var inst in instances) all.Add(BuildProxyKillerState(inst));

        // 多实例：顶层字段取第一个运行中的实例，完整列表放 instances（单实例时不加，保持原 schema）
        var top = new Dictionary<string, object>((Dictionary<string, object>)all[0]);
        if (all.Count > 1) top["instances"] = all;
        return top;
    }

    private static Dictionary<string, object> BuildProxyKillerState(object inst)
    {
        var cfg = KeReflect.Get(inst, "_cfg");
        float ram = ToFloat(KeReflect.Get(inst, "_ram"));
        float ramStart = ToFloat(KeReflect.Get(inst, "_ramStart"));
        float target = ToFloat(KeReflect.Get(inst, "_target"));
        float mulTarget = ToFloat(KeReflect.Get(inst, "_mulTarget"));

        // 倍速由 RAM 爬升**线性**驱动：1 → ShellOverloadTarget。
        // 注意 BurstTarget 只影响"爬满快慢"，不影响最终倍速（归一化后）。
        float progress = target > ramStart ? Clamp01((ram - ramStart) / (target - ramStart)) : 1f;
        var watched = KeReflect.Get(inst, "_watched");

        return new Dictionary<string, object>
        {
            ["running"] = true,
            ["configName"] = KeReflect.Get(cfg, "SourceName"),
            ["selfOverload"] = KeReflect.Get(cfg, "SelfOverload"),
            ["selfOverloadRate"] = KeReflect.Get(inst, "_selfOverloadRate"),
            ["ram"] = Math.Round(ram, 1),
            ["ramStart"] = ramStart,
            ["ramTarget"] = target,
            ["ramProgress"] = Math.Round(progress, 3),
            ["multiplier"] = Math.Round(1f + (mulTarget - 1f) * progress, 2),
            ["multiplierMax"] = mulTarget,
            ["followConnection"] = KeReflect.Get(cfg, "ResolvedFollowConnection"),
            ["watchedNode"] = KeReflect.SafeStr(() => KeReflect.Get(watched, "ip") ?? KeReflect.Get(watched, "idName")),
            ["watchedProxyActive"] = KeReflect.Get(watched, "proxyActive"),
            ["sawProxyActive"] = KeReflect.Get(inst, "_sawProxyActive"),
        };
    }

    [McpTool("ke.config.get", "读取 KE-Config.xml 配置（ConfigLoader）")]
    public static object KeConfig(Dictionary<string, object> p)
    {
        // KE 重构后配置加载类由 KEConfigLoader 改名为 ConfigLoader（成员名不变）
        var t = KeReflect.FindType("ConfigLoader") ?? KeReflect.FindType("KEConfigLoader");
        if (t == null) return KeReflect.ModMissing("ConfigLoader");
        return new Dictionary<string, object>
        {
            ["debug"] = KeReflect.GetStatic(t, "Debug"),
            ["skipVanillaIRCLogs"] = KeReflect.GetStatic(t, "SkipVanillaIRCLogs"),
            ["customImages"] = KeReflect.ToList(KeReflect.GetStatic(t, "CustomImages") as System.Collections.IEnumerable),
            ["_source"] = t.FullName + " @" + t.Assembly.GetName().Name,
            ["connectorVersion"] = HnpfMcpConnectorPlugin.Version,
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

    [McpTool("ke.clock.state", "Clock 定时器状态：运行中的 Clock 列表（id/来源/已触发次数/已过时间）+ 待恢复数")]
    public static object ClockState(Dictionary<string, object> p)
    {
        var m = KeReflect.FindType("ClockManager");
        if (m == null) return KeReflect.ModMissing("ClockManager");
        var os = Hacknet.OS.currentInstance;
        if (os == null) return new Dictionary<string, object> { ["running"] = false, ["note"] = "no active OS" };
        var raw = KeReflect.CallStaticArgs(m, "GetPersistentState", new object[] { os });
        var clocks = new List<object>();
        if (raw is System.Collections.IEnumerable en)
            foreach (var st in en)
            {
                if (st == null) continue;
                clocks.Add(new Dictionary<string, object>
                {
                    ["id"] = KeReflect.Get(st, "Id"),
                    ["sourcePath"] = KeReflect.Get(st, "SourcePath"),
                    ["extensionRoot"] = KeReflect.Get(st, "ExtensionRoot"),
                    ["timesElapsed"] = KeReflect.Get(st, "TimesElapsed"),
                    ["elapsed"] = KeReflect.Get(st, "Elapsed"),
                    ["timer"] = KeReflect.Get(st, "Timer"),
                });
            }
        var pending = KeReflect.GetStatic(m, "PendingRestore") as System.Collections.ICollection;
        return new Dictionary<string, object>
        {
            ["count"] = clocks.Count,
            ["clocks"] = clocks,
            ["pendingRestore"] = pending?.Count ?? 0
        };
    }

    [McpTool("ke.titlebanner.state", "TitleBanner 状态：是否激活 / 标题 / 正文 / 时长 / 已过时间 / 强调色（含 CustomColor 关键字）/ 图标")]
    public static object TitleBannerState(Dictionary<string, object> p)
    {
        // 单例挂在 Harmony 补丁类上（OS.LoadContent 时创建），横幅对象本身不是静态单例
        var patches = KeReflect.FindType("TitleBannerPatches");
        if (patches == null) return KeReflect.ModMissing("TitleBannerPatches");

        var banner = KeReflect.GetStatic(patches, "Instance");
        if (banner == null) return new Dictionary<string, object> { ["active"] = false };

        return new Dictionary<string, object>
        {
            ["active"] = KeReflect.Get(banner, "IsActive"),
            ["title"] = KeReflect.Get(banner, "TitleText"),
            ["body"] = KeReflect.Get(banner, "BodyText"),
            ["duration"] = Math.Round(ToFloat(KeReflect.Get(banner, "Duration")), 2),
            ["elapsed"] = Math.Round(ToFloat(KeReflect.Get(banner, "timeElapsed")), 2),
            ["accentColor"] = KeReflect.SafeStr(() => KeReflect.Get(banner, "AccentColor")),
            ["accentColorKey"] = KeReflect.Get(banner, "AccentColorKey"),
            ["iconPath"] = KeReflect.Get(banner, "CurrentIconPath"),
        };
    }

    [McpTool("ke.ending.state", "自定义结局（CustomEndingModule）状态：运行中 / 标题 / 结尾文字 / 报幕音乐 / 资源路径")]
    public static object EndingState(Dictionary<string, object> p)
    {
        var t = KeReflect.FindType("CustomEndingModule");
        if (t == null) return KeReflect.ModMissing("CustomEndingModule");

        var os = Hacknet.OS.currentInstance;
        if (os == null)
            return new Dictionary<string, object> { ["available"] = false, ["running"] = false, ["note"] = "no active OS" };

        // OS 原生驱动的结局模块挂在 os.endingSequence（原版也在构造时占位一个基类实例）
        var ending = KeReflect.Get(os, "endingSequence");
        if (ending == null || !t.IsInstanceOfType(ending))
            return new Dictionary<string, object>
            {
                ["available"] = false,
                ["running"] = false,
                ["note"] = "当前 OS 没有正在使用的自定义结局模块",
            };

        return new Dictionary<string, object>
        {
            ["available"] = true,
            ["running"] = KeReflect.Get(ending, "IsActive"),
            ["title"] = KeReflect.Get(ending, "Titletext"),
            ["endingText"] = KeReflect.Get(ending, "endingText"),
            ["onCreditMusic"] = KeReflect.Get(ending, "onCreditMusic"),
            ["afterMusic"] = KeReflect.Get(ending, "afterMusic"),
            ["speechFile"] = KeReflect.Get(ending, "SpeechFile"),
            ["creditsFile"] = KeReflect.Get(ending, "CreditsFile"),
        };
    }

    [McpTool("ke.color.parse", "解析颜色字符串（XNA 命名色 / #RGB / #RRGGBB / #AARRGGBB / R,G,B[,A] / 动态色 LDTchara/RGB/Gradient/预设名）；返回解析结果，不依赖画面")]
    public static object ColorParse(Dictionary<string, object> p)
    {
        var raw = p.TryGetValue("value", out var v) ? v?.ToString() : null;
        var colorHelper = KeReflect.FindType("ColorHelper");
        if (colorHelper == null) return KeReflect.ModMissing("ColorHelper");

        var result = new Dictionary<string, object> { ["raw"] = raw };
        try
        {
            var m = colorHelper.GetMethod("ParseStaticColor",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (m == null)
            {
                result["error"] = "ColorHelper.ParseStaticColor not found";
                return result;
            }

            var colorType = m.GetParameters()[1].ParameterType;
            // 哨兵 alpha=0：ParseStaticColor 所有合法路径都会把 alpha 置 255，用它判断"没认出来"
            var sentinel = Activator.CreateInstance(colorType, (byte)0, (byte)0, (byte)0, (byte)0);
            var parsed = m.Invoke(null, new object[] { raw, sentinel });
            result["static"] = parsed?.ToString();
            result["recognized"] = parsed != null && !parsed.Equals(sentinel);

            var customColorManager = KeReflect.FindType("CustomColorManager");
            var dyn = customColorManager == null
                ? null
                : KeReflect.CallStaticArgs(customColorManager, "ParseColorString", new object[] { raw });
            result["isDynamic"] = dyn != null;
            if (dyn != null)
            {
                result["dynamicType"] = KeReflect.Get(dyn, "ColorType")?.ToString();
                result["speed"] = KeReflect.Get(dyn, "Speed");
                result["alpha"] = KeReflect.Get(dyn, "Alpha");
                result["segments"] = KeReflect.ToList(KeReflect.Get(dyn, "Segments")).Count;
            }
        }
        catch (Exception e)
        {
            result["error"] = e.InnerException?.Message ?? e.Message;
        }
        return result;
    }

    [McpTool("ke.color.state", "动态颜色状态：已注册的动态颜色字段与各自当前颜色值 + ChangeThemeColor 临时主题覆盖")]
    public static object ColorState(Dictionary<string, object> p)
    {
        var m = KeReflect.FindType("CustomColorManager");
        if (m == null) return KeReflect.ModMissing("CustomColorManager");
        var fields = KeReflect.GetStatic(m, "_dynamicFields") as System.Collections.IDictionary;
        var result = new Dictionary<string, object>();
        if (fields != null)
            foreach (System.Collections.DictionaryEntry e in fields)
            {
                var name = e.Key?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                result[name] = KeReflect.CallStaticArgs(m, "GetCurrentColor", new object[] { name })?.ToString();
            }
        return new Dictionary<string, object>
        {
            ["count"] = result.Count,
            ["fields"] = result,
            ["override"] = ThemeOverrideState(),
        };
    }

    /// <summary>
    /// 读取 ChangeThemeColor 的临时主题覆盖：<c>ChangeThemeColorAction</c> 按 OS 把运行中的
    /// 覆盖实例存在一个弱引用表里（动态字段逐帧重算，Duration 到期后还原）。
    /// 纯只读，且读不到（旧版 KE / 没有覆盖）只返回 <c>{active:false}</c>，不影响动态颜色字段本身。
    /// </summary>
    private static Dictionary<string, object> ThemeOverrideState()
    {
        var inactive = new Dictionary<string, object> { ["active"] = false };
        try
        {
            var os = Hacknet.OS.currentInstance;
            if (os == null) return inactive;

            var action = KeReflect.FindType("ChangeThemeColorAction");
            if (action == null) return inactive;
            var table = KeReflect.GetStatic(action, "ActiveOverrides");
            if (table == null) return inactive;

            // ConditionalWeakTable&lt;OS, ThemeOverride&gt;：用反射调 TryGetValue 拿到 out 值
            var tryGet = table.GetType().GetMethod("TryGetValue",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (tryGet == null) return inactive;
            var args = new object[] { os, null };
            if (!(tryGet.Invoke(table, args) is bool found) || !found) return inactive;

            var ov = args[1];
            if (ov == null) return inactive;

            bool unlimited = KeReflect.Get(ov, "Unlimited") is bool b && b;
            float duration = ToFloat(KeReflect.Get(ov, "Duration"));
            float remaining = ToFloat(KeReflect.Get(ov, "remaining"));
            var targets = KeReflect.ToList(KeReflect.Get(ov, "targets"));
            var targetFields = new List<object>();
            foreach (var target in targets) targetFields.Add(KeReflect.Get(target, "Key"));

            return new Dictionary<string, object>
            {
                ["active"] = true,
                ["unlimited"] = unlimited,
                ["durationSeconds"] = unlimited ? (object)null : Math.Round(duration, 2),
                ["remainingSeconds"] = unlimited ? (object)null : Math.Round(Math.Max(0f, remaining), 2),
                ["elapsedSeconds"] = unlimited ? (object)null : Math.Round(Math.Max(0f, duration - remaining), 2),
                ["targetCount"] = targets.Count,
                ["targets"] = targetFields,
            };
        }
        catch { return inactive; }
    }
}
