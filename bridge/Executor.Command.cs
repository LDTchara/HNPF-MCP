using Hacknet;
using Pathfinder.Executable;
using Pathfinder.Replacements;
using Pathfinder.Util.XML;

namespace HnpfMcpBridge;

/// <summary>命令域：终端命令 / 黑客脚本 / exe / 动作。</summary>
public static partial class Executor
{
    private static object ExecuteCommand(OS os, string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd))
            throw new ArgumentException("cmd is empty");
        var policy = HnpfMcpBridgePlugin.CheckCommandPolicy(cmd);
        if (policy != null) throw new ArgumentException(policy);
        // os.runCommand 与游戏内命令同路径（后台线程安全），输出写在游戏终端
        os.runCommand(cmd);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["cmd"] = cmd,
            ["note"] = "submitted; run terminal.history to read output"
        };
    }

    /// <summary>执行黑客脚本（载体是 .txt：Content/HackerScripts/*.txt 或扩展 HackerScripts/，Content 相对路径）。</summary>
    private static object RunHackScript(OS os, string script)
    {
        if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("script is empty");
        HackerScriptExecuter.runScript(script, os);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["script"] = script,
            ["note"] = "submitted; run terminal.history to read output"
        };
    }

    /// <summary>启动 exe。优先用玩家本机 bin/ 里的文件（原版机制），
    /// Pathfinder 自定义 exe（#NAME#）自动补文件。命令本身经 runCommand 走游戏原路径。</summary>
    private static object LaunchExe(OS os, string exeName, string args)
    {
        if (string.IsNullOrWhiteSpace(exeName)) throw new ArgumentException("exeName is empty");

        var clean = exeName.Trim();
        var lower = clean.Replace(".exe", "").ToLowerInvariant();

        // Pathfinder 自定义 exe：确保本机 bin/ 有对应文件（data = ExeData），否则游戏找不到
        // libs 里的 PathfinderAPI 版本可能没有 IsXmlId/GetCustomExeData，用反射兼容
        string xmlId = clean.StartsWith("#") ? clean : "#" + clean + "#";
        string customData = TryGetCustomExeData(xmlId);
        if (customData != null)
        {
            var bin = os.thisComputer.files?.root?.searchForFolder("bin") ?? os.thisComputer.files?.root;
            var fileName = clean.Trim('#');
            bool exists = false;
            if (bin != null)
                foreach (var f in bin.files)
                    if (string.Equals(f.name, fileName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(f.name, fileName + ".exe", StringComparison.OrdinalIgnoreCase))
                    { exists = true; break; }
            if (!exists && bin != null)
                bin.files.Add(new FileEntry { name = fileName + ".exe", data = customData });
        }

        var fullCmd = lower + (string.IsNullOrWhiteSpace(args) ? "" : " " + args.Trim());
        var policy = HnpfMcpBridgePlugin.CheckCommandPolicy(fullCmd);
        if (policy != null) throw new ArgumentException(policy);
        os.runCommand(fullCmd);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["exe"] = clean,
            ["cmd"] = fullCmd,
            ["note"] = "submitted; run terminal.history to read output"
        };
    }

    /// <summary>泛化执行 Pathfinder 动作（SA XML）。KE 等模组注册的自定义 Action 自动可用。</summary>
    private static object RunAction(OS os, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new ArgumentException("xml is empty");

        var executor = new EventExecutor(xml, false);
        ElementInfo actionInfo = null;
        executor.RegisterExecutor("*", (exec, info) => actionInfo = info, ParseOption.ParseInterior);
        if (!executor.TryParse(out var ex) || actionInfo == null)
            throw new ArgumentException($"bad action xml: {ex?.Message ?? "no action element found"}");

        var action = ActionsLoader.ReadAction(actionInfo);
        action.Trigger(os);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["action"] = actionInfo.Name
        };
    }

    /// <summary>反射调用 Pathfinder ExecutableManager.GetCustomExeData（兼容不同 Pathfinder 版本）。</summary>
    private static string TryGetCustomExeData(string xmlId)
    {
        try
        {
            var m = typeof(ExecutableManager).GetMethod("GetCustomExeData",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return m?.Invoke(null, new object[] { xmlId }) as string;
        }
        catch { return null; }
    }
}
