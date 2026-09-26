using Hacknet;
using Hacknet.PlatformAPI.Storage;
using HarmonyLib;
using System.IO;
using System.Reflection;

namespace HnpfMcpBridge;

/// <summary>
/// 主菜单阶段执行器：Harmony patch MainMenu.Update，让 bridge 在主菜单也能处理请求，
/// 程序化调用主菜单的"进扩展"逻辑（免 UI 点击，且支持用存档账号恢复进度）。
///
/// 方法：
///   menu.enter_extension     { username?, pass? }     → 新建账号进扩展（CreateNewAccountForExtensionAndStart）
///   menu.load_extension_save { userFile, username }   → 用存档账号进扩展（LoadAccountForExtension_FileAndUsername，恢复进度）
///
/// 进扩展后 MainMenu 退出 → 本 patch 不再触发，Executor.OnUpdate（OS 生命周期）自然接管。
/// </summary>
[HarmonyPatch(typeof(MainMenu), "Update")]
public static class MenuExecutor
{
    public static void Postfix(MainMenu __instance)
    {
        var pipe = HnpfMcpBridgePlugin.Pipe;
        if (pipe == null) return;

        // 主菜单每帧消费少量请求（主菜单无重负载，预算 4 足够）
        int budget = 4;
        while (budget-- > 0 && pipe.TryDequeueRequest(out var req))
        {
            if (req.Method != "menu.enter_extension" && req.Method != "menu.load_extension_save")
            {
                pipe.PushResponse(RpcResponse.Fail(req, 400, "not available in main menu (start a game session first)").ToJsonLine());
                continue;
            }
            try
            {
                var resp = req.Method == "menu.enter_extension"
                    ? EnterExtension(__instance, req.Params)
                    : LoadExtensionSave(__instance, req.Params);
                pipe.PushResponse(RpcResponse.Ok(req, resp).ToJsonLine());
            }
            catch (Exception ex)
            {
                HnpfMcpBridgePlugin.LogWarn($"menu '{req.Method}' failed: {ex}");
                pipe.PushResponse(RpcResponse.Fail(req, 500, ex.ToString()).ToJsonLine());
            }
        }
    }

    /// <summary>新建账号进扩展。完整链路：模拟 PF 插件确认 → ActivateExtensionPage（插件真正加载）→ 创建账户。</summary>
    private static object EnterExtension(MainMenu menu, Dictionary<string, object> p)
    {
        var username = Str(p, "username");
        if (string.IsNullOrEmpty(username)) username = "mcp";
        var pass = Str(p, "pass");
        if (string.IsNullOrEmpty(pass)) pass = Guid.NewGuid().ToString("N").Substring(0, 12);
        var ext = Str(p, "ext");
        if (string.IsNullOrEmpty(ext)) ext = "KernelExtensionTEST123123";

        // 1. 加载扩展 info 并设置 ActiveExtensionInfo（否则 OS.LoadContent → LoadNewExtensionSession(null) NRE）
        var info = LoadExtensionInfo(ext);
        SetActiveExtensionInfo(info);

        // 2. 模拟 PF 插件确认弹窗（approvedInfo=info）→ ActivateExtensionPage 放行 → 插件真正加载 + 账户界面
        ApproveAndOpenExtensionPage(menu, info);

        // 3. 兜底：万一插件加载失败，至少注册 [McpTool]
        TryLoadExtensionPlugins(ext);

        // 4. 创建账户进扩展（官方入口）。对齐原版 -extstart（Game1.LoadInitialScreens：
        //    DeleteUser("test") + CreateNewAccountForExtensionAndStart）：先删同名旧账号，
        //    避免 AddUser 因用户已存在返回 false → 走 else 分支报 "Error auto-loading Extension"
        //    且 EnterExtension 不检查结果仍返回 ok（MCP 报成功但游戏实际没进扩展）
        try { SaveFileManager.DeleteUser(username); } catch { }
        MainMenu.CreateNewAccountForExtensionAndStart(username, pass, menu.ScreenManager, menu, null);

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["mode"] = "new account",
            ["username"] = username,
            ["ext"] = ext
        };
    }

    /// <summary>用存档账号进扩展（恢复进度）。同上链路，最后走账户读档委托。</summary>
    private static object LoadExtensionSave(MainMenu menu, Dictionary<string, object> p)
    {
        var userFile = Str(p, "userFile");
        var username = Str(p, "username");
        if (string.IsNullOrEmpty(userFile) || string.IsNullOrEmpty(username))
            throw new ArgumentException("userFile and username are required (see save_list)");
        // 防"假读档"：LocalDocumentsStorageMethod.FileExists(filename) 会拼账号目录前缀，
        // 传绝对路径会拼成 "KernelExtensionsTEST/D:\\...\\save.xml"（无效）→ FileExists=false
        // → OS.WillLoadSave=false → 走新建扩展（LoadNewExtensionSession），所有自定义 SaveExecutor
        // 不触发（Clock/Flag 等不恢复）。归一化为纯文件名，与 UI 读档一致。
        userFile = System.IO.Path.GetFileName(userFile);
        var ext = Str(p, "ext");
        if (string.IsNullOrEmpty(ext)) ext = "KernelExtensionTEST123123";

        var info = LoadExtensionInfo(ext);
        SetActiveExtensionInfo(info);
        ApproveAndOpenExtensionPage(menu, info);
        TryLoadExtensionPlugins(ext);

        // 账户屏的读档委托（LoadAccountForExtension_FileAndUsername，Action<string,string> 字段）
        var extScreen = typeof(MainMenu)
            .GetField("extensionsScreen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(menu);
        var action = extScreen?.GetType()
            .GetField("LoadAccountForExtension_FileAndUsername", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(extScreen);
        if (action is Action<string, string> fn)
        {
            fn(userFile, username);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["mode"] = "load save",
                ["username"] = username,
                ["userFile"] = userFile,
                ["ext"] = ext
            };
        }
        throw new ArgumentException("cannot resolve LoadAccountForExtension_FileAndUsername");
    }

    /// <summary>ExtensionInfo.ReadExtensionInfo（Pathfinder patch 了它，负责解析 extensionInfo.xml）。</summary>
    private static object LoadExtensionInfo(string extFolderName)
    {
        var infoType = typeof(Hacknet.Extensions.ExtensionInfo);
        var info = infoType?.GetMethod("ReadExtensionInfo",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, new object[] { "Extensions/" + extFolderName });
        if (info == null) throw new ArgumentException($"cannot load extension info: Extensions/{extFolderName}（扩展文件夹名对吗？）");
        return info;
    }

    /// <summary>设置 ExtensionLoader.ActiveExtensionInfo（字段优先，属性兜底）。</summary>
    private static void SetActiveExtensionInfo(object info)
    {
        var loader = typeof(Hacknet.Extensions.ExtensionLoader);
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var field = loader.GetField("ActiveExtensionInfo", flags);
        if (field != null) field.SetValue(null, info);
        else loader.GetProperty("ActiveExtensionInfo", flags)?.SetValue(null, info);
    }

    /// <summary>模拟 PF 插件确认：设 ArbitraryCodeWarning.approvedInfo=info（确认标记），
    /// 再调 ExtensionsMenuScreen.ActivateExtensionPage(info)——HarmonyPrefix 见 approvedInfo==info 放行，
    /// 原方法执行 → 扩展插件（KE/连接器）真正加载 + 进入扩展账户界面。</summary>
    private static void ApproveAndOpenExtensionPage(MainMenu menu, object info)
    {
        // Pathfinder.GUI.ArbitraryCodeWarning 是 internal 类，按简单名反射（PathfinderAPI 程序集已加载）
        var warnType = FindSimpleType("ArbitraryCodeWarning");
        warnType?.GetField("approvedInfo", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, info);

        var extScreen = typeof(MainMenu)
            .GetField("extensionsScreen", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(menu);
        if (extScreen == null) throw new ArgumentException("cannot resolve MainMenu.extensionsScreen");
        extScreen.GetType()
            .GetMethod("ActivateExtensionPage", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.Invoke(extScreen, new object[] { info });
    }

    /// <summary>按简单名在已加载程序集找类型（internal 类型也能找到）。</summary>
    private static Type FindSimpleType(string simpleName)
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

    /// <summary>加载扩展 Plugins/ 目录下的 DLL（连接器/KE 等），随后 McpModuleScanner 重扫注册 [McpTool]。
    ///
    /// ⚠️ 必须用**字节加载**且**先判重**（2026-09-20）：
    ///   原实现 `Assembly.LoadFrom(dll)` 会**永久锁定文件**——.NET Framework 的 LoadFrom 会持有文件句柄
    ///   直到 AppDomain 卸载，而 bridge 是全局插件永不卸载 → 一旦 MCP 走过这里，扩展 DLL 就被锁死，
    ///   **退出扩展后也无法替换/覆盖**（cp 报 "Device or resource busy"）。
    ///   这解释了"用 MCP 进过扩展后 DLL 换不掉，而手动进扩展（不经过 bridge）则正常"。
    ///   另外先判重可避免与 Pathfinder 的加载撞车（同一 DLL 被加载出两套类型 → 类型 identity 不一致）。
    /// </summary>
    private static void TryLoadExtensionPlugins(string extFolderName)
    {
        try
        {
            var pluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Extensions", extFolderName, "Plugins");
            if (!Directory.Exists(pluginsDir)) return;

            // 已加载的程序集名（Pathfinder 正常路径已加载的，一律跳过）
            var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { loadedNames.Add(a.GetName().Name); } catch { }
            }

            int loaded = 0, skipped = 0;
            foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (loadedNames.Contains(name)) { skipped++; continue; }   // 已加载 → 跳过（不重复 LoadFrom）
                try
                {
                    // 字节加载：不持有文件句柄 → 退出扩展后可正常替换 DLL
                    Assembly.Load(File.ReadAllBytes(dll));
                    loaded++;
                }
                catch { }
            }
            HnpfMcpBridgePlugin.LogInfo($"[{HnpfMcpBridgePlugin.ModName}] 扩展插件 DLL 兜底加载 x{loaded}（已加载跳过 x{skipped}，字节加载不锁文件）（{pluginsDir}）");
            try { McpModuleScanner.Scan(); } catch { }
        }
        catch { }
    }

    private static string Str(Dictionary<string, object> p, string key) =>
        p != null && p.TryGetValue(key, out var v) ? v?.ToString() : null;
}
