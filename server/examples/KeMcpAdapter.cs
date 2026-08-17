// ============================================================================
// KE 适配器示例（第三层抽象：模组专属内存态暴露给 AI）
// ----------------------------------------------------------------------------
// 使用方式：
//   1. KE 项目添加对 hnpf-mcp-bridge 程序集（HnpfMcpBridge.dll）的引用
//   2. 本文件加入 KE 项目（或放到任意被 BepInEx 加载的程序集里）
//   3. 编译后，bridge 在 OSLoaded 时自动扫描 [McpTool] 并注册，
//      MCP 侧用 modtool_list / modtool_call 即可调用 —— 无需改 MCP server
// ----------------------------------------------------------------------------
// 支持的方法签名：无参 或 单个 Dictionary<string, object> 参数。
// 返回值：object（bridge 自动 JSON 序列化）或 string。
// ============================================================================

using HnpfMcpBridge;                       // McpToolAttribute
using KernelExtensions.Executables;        // CustomTrialExe
using KernelExtensions.Modules;            // PhaseSwiftManager / VMInfectionManager
using KernelExtensions.Utility;            // KEConfigLoader

namespace KernelExtensions.Mcp;

public static class KeMcpAdapter
{
    [McpTool("ke.phaseswift.state", "PhaseSwift 运行时状态（运行中/场景/音乐相位/主题/已发现节点/黑名单）")]
    public static object PhaseSwiftState(Dictionary<string, object> p) => new Dictionary<string, object>
    {
        ["running"] = PhaseSwiftManager.IsRunning,
        ["scene"] = PhaseSwiftManager.CurrentScene,
        ["musicPhase"] = PhaseSwiftManager.CurrentMusicPhase,
        ["theme"] = PhaseSwiftManager.DefaultTheme,
        ["discovered"] = PhaseSwiftManager.GetSceneDiscoveredNodes(),
        ["blocked"] = PhaseSwiftManager.GetRuntimeBlockedNodes(),
    };

    [McpTool("ke.customtrial.state", "当前 CustomTrial 试炼（配置名/已删除节点）")]
    public static object CustomTrialState(Dictionary<string, object> p)
    {
        var trial = CustomTrialExe.CurrentInstance;
        return new Dictionary<string, object>
        {
            ["config"] = trial?.CurrentConfigName,
            ["deletedNodes"] = trial?.GetDeletedNodeIndices(),
        };
    }

    [McpTool("ke.vm.state", "VM 感染模式与检查条件")]
    public static object VmState(Dictionary<string, object> p) =>
        VMInfectionManager.CurrentConfig != null
            ? (object)new Dictionary<string, object>
            {
                ["mode"] = VMInfectionManager.CurrentConfig.Mode.ToString(),
                ["checkFilePath"] = VMInfectionManager.CurrentConfig.CheckFilePath,
            }
            : new Dictionary<string, object> { ["mode"] = "none" };

    [McpTool("ke.config.get", "读取 KE-Config.xml 配置")]
    public static object KeConfig(Dictionary<string, object> p) => new Dictionary<string, object>
    {
        ["debug"] = KEConfigLoader.Debug,
        ["skipVanillaIRCLogs"] = KEConfigLoader.SkipVanillaIRCLogs,
        ["customImages"] = KEConfigLoader.CustomImages,
    };

    // 带参数示例（如按名字查 Flag）：
    [McpTool("ke.flag.find", "按前缀查找 Flag（如 PhaseSwift_）")]
    public static object FlagFind(Dictionary<string, object> p)
    {
        var prefix = p.TryGetValue("prefix", out var v) ? v?.ToString() : "";
        var os = Hacknet.OS.currentInstance;
        if (os?.Flags == null || string.IsNullOrEmpty(prefix)) return new Dictionary<string, object> { ["found"] = false };
        return new Dictionary<string, object> { ["flag"] = os.Flags.GetFlagStartingWith(prefix) };
    }
}
