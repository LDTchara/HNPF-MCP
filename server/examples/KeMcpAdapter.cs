// ============================================================================
// L3 适配器最小模板：把模组独有状态暴露给 MCP（不引用模组程序集，纯反射软依赖）
// ----------------------------------------------------------------------------
// 用法：
//   1. 在任意 BepInEx 插件程序集（推荐独立"连接器"插件）里加一个静态类；
//   2. 给静态方法加 [McpTool("名字", "描述")]；
//      方法签名只能是「无参」或「单个 Dictionary<string, object> 参数」；
//   3. 编译产物放到扩展的 Plugins/ 或 BepInEx/plugins/；
//   4. bridge 在 OSLoaded / 进扩展时反射扫描注册，MCP 侧用 modtool_list /
//      modtool_call 调用（返回值包在 {ok:true, tool, result} 下）。
//
// 注意：连接器不要声明 [BepInDependency]——bridge 是全局插件，扩展模式依赖校验
// 看不到它，会误报 missing dependencies；模组类型用简单名 + 反射按需解析即可。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using HnpfMcpBridge;   // [McpTool]

namespace MyMod.Mcp;

/// <summary>示例适配器：替换 MyModManager / 字段名为你自己模组的类型与成员。</summary>
internal static class MyModAdapters
{
    private const string ModName = "MyMod";

    [McpTool("mymod.state", "MyMod 运行状态（示例）")]
    public static object State(Dictionary<string, object> p)
    {
        var t = FindType("MyModManager");
        if (t == null) return ModMissing("MyModManager");
        try
        {
            return new Dictionary<string, object>
            {
                ["running"] = t.GetField("IsRunning", BindingFlags.Public | BindingFlags.Static)?.GetValue(null),
                ["scene"] = t.GetField("CurrentScene", BindingFlags.Public | BindingFlags.Static)?.GetValue(null),
                ["config"] = t.GetField("Config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)?.ToString(),
            };
        }
        catch (Exception e)
        {
            return new Dictionary<string, object> { ["error"] = e.Message };
        }
    }

    private static Type FindType(string simpleName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
                if (t.Name == simpleName) return t;
        }
        return null;
    }

    private static object ModMissing(string typeName) => new Dictionary<string, object>
    {
        ["error"] = $"{ModName} 未加载或类型缺失（需要类型: {typeName}）",
    };
}
