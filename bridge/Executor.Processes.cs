using Hacknet;

namespace HnpfMcpBridge;

/// <summary>
/// 进程域：os.exes 投影（PID / 类型 / RAM 成本 / 位置 / 退出状态）+ 结束进程。
/// 原版只有 `ps` / `kill` 文本命令；这两个方法给 AI 提供结构化数据（KE 测试常用）。
/// </summary>
public static partial class Executor
{
    private static object GetProcesses(OS os)
    {
        var list = new List<object>();
        var exes = os?.exes;
        if (exes != null)
        {
            foreach (var exe in exes)
            {
                if (exe == null) continue;
                var b = exe.bounds;
                list.Add(new Dictionary<string, object>
                {
                    ["pid"] = exe.PID,
                    ["name"] = SafeStr(() => exe.name),
                    ["identifierName"] = SafeStr(() => exe.IdentifierName),
                    ["type"] = exe.GetType().Name,
                    ["ramCost"] = SafeInt(() => exe.ramCost),
                    ["x"] = b.X,
                    ["y"] = b.Y,
                    ["width"] = b.Width,
                    ["height"] = b.Height,
                    ["targetIP"] = SafeStr(() => exe.targetIP),
                    ["isExiting"] = SafeBool(() => exe.isExiting),
                    ["needsRemoval"] = SafeBool(() => exe.needsRemoval),
                });
            }
        }
        return new Dictionary<string, object>
        {
            ["count"] = list.Count,
            ["ramFree"] = SafeInt(() => os.ramAvaliable),
            ["ramTotal"] = SafeInt(() => os.totalRam),
            ["exes"] = list,
        };
    }

    private static object KillProcess(OS os, int pid)
    {
        if (pid <= 0) throw new ArgumentException("pid must be > 0");
        var exes = os?.exes;
        if (exes == null) throw new InvalidOperationException("no exe list available");
        int idx = -1;
        for (int i = 0; i < exes.Count; i++)
        {
            if (exes[i] != null && exes[i].PID == pid) { idx = i; break; }
        }
        if (idx < 0)
            return new Dictionary<string, object> { ["ok"] = false, ["reason"] = $"PID {pid} not found" };

        var exe = exes[idx];
        var info = new Dictionary<string, object>
        {
            ["pid"] = exe.PID,
            ["identifierName"] = SafeStr(() => exe.IdentifierName),
            ["type"] = exe.GetType().Name,
        };
        exe.Killed();
        exes.RemoveAt(idx);
        return new Dictionary<string, object> { ["ok"] = true, ["killed"] = info };
    }
}
