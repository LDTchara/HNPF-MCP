using HarmonyLib;
using Hacknet;

namespace HnpfMcpBridge;

/// <summary>
/// exit_to_menu 的帧末执行钩子——**退出扩展的正确时机**（2026-09-20）。
///
/// 背景（为什么不能在 OSUpdateEvent 里直接 quitGame）：
///   bridge 的 <see cref="Executor.OnUpdate"/> 是 Pathfinder <c>OSUpdateEvent</c> 的处理器之一，
///   运行在事件派发循环中（正在遍历 handler 列表）。而 <c>OS.quitGame</c> 的 Harmony Postfix 是
///   <c>HacknetChainloader.UnloadTemps()</c> → 对每个扩展插件调用 <c>HarmonyInstance.UnpatchSelf()</c>。
///   在"派发中卸载插件"会让卸载打断派发循环与正在执行的 patch，导致：
///     - 日志只有 "Unloading extension plugins..." 而缺 "Finished unloading extension plugins"；
///     - 插件卸载不完整 → 退出扩展后插件仍占用（功能残留 / 组装引用未释放导致扩展 DLL 无法替换）。
///   而手动点顶栏 X → "Exit to Menu" 的 quitGame 发生在 <c>MessageBoxScreen.Update</c>（输入回调），
///   此时 OSUpdateEvent 派发早已结束 → 卸载安全（这就是手动退出正常的原因）。
///
/// 本钩子把 quitGame 挪到 <c>Game1.Update</c> 的 Postfix（整帧屏幕更新 + 事件派发全部结束之后），
/// 与手动路径的执行时机等价；同时先清 bridge 对扩展程序集的强引用
/// （<see cref="McpModuleScanner.ClearCache"/>——缓存的 MethodInfo 会阻止程序集卸载）。
/// </summary>
[HarmonyPatch(typeof(Game1), "Update")]
internal static class ExitToMenuFrameHook
{
    [HarmonyPostfix]
    internal static void Postfix()
    {
        if (!Executor.PendingExitToMenu) return;
        Executor.PendingExitToMenu = false;

        var os = OS.currentInstance;
        if (os == null || os.HasExitedAndEnded) return;

        // 先释放 bridge 持有的扩展程序集引用（McpTool 扫描缓存），再走官方退链卸载插件。
        // 顺序重要：缓存不清 → 扩展程序集无法卸载 → 插件"仍占用"。
        McpModuleScanner.ClearCache();
        os.quitGame(null, null);
    }
}
