using System.Collections;
using Hacknet;

namespace HnpfMcpBridge;

/// <summary>
/// 原版终端小能力结构化封装：reboot / CD 托盘 / 清屏 / `addNote` / scan / probe / 成就读取。
/// 这些操作原版只能通过终端文本命令完成，这里给 AI 直接可断言的结构化返回值。
/// </summary>
public static partial class Executor
{
    private static object VanillaReboot(OS os, bool instant)
    {
        var target = os.connectedComp ?? os.thisComputer;
        if (target == null) throw new InvalidOperationException("no target computer");
        Programs.reboot(new[] { "reboot", instant ? "-i" : "" }, os);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["instant"] = instant,
            ["target"] = target.ip,
        };
    }

    private static object VanillaCdTray(OS os, bool open)
    {
        var target = os.connectedComp ?? os.thisComputer;
        if (target == null) throw new InvalidOperationException("no target computer");
        Programs.opCDTray(new[] { open ? "opencdtray" : "closecdtray" }, os, open);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["open"] = open,
            ["target"] = target.ip,
        };
    }

    private static object ClearTerminal(OS os)
    {
        if (os.terminal == null) throw new InvalidOperationException("terminal not available");
        os.terminal.reset();
        return new Dictionary<string, object> { ["ok"] = true };
    }

    private static object AddNote(OS os, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
        // 与终端 addNote 相同：多参数拼接 + 支持 \n 转义
        Programs.addNote(new[] { "addNote", text }, os);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["text"] = text,
        };
    }

    private static object VanillaScan(OS os)
    {
        var target = os.connectedComp ?? os.thisComputer;
        if (target == null) throw new InvalidOperationException("no target computer");
        Programs.scan(new string[0], os);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["target"] = target.ip,
            ["note"] = "scan submitted; run terminal.history to read output",
        };
    }

    private static object VanillaProbe(OS os)
    {
        var target = os.connectedComp ?? os.thisComputer;
        if (target == null) throw new InvalidOperationException("no target computer");
        Programs.probe(new string[0], os);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["target"] = target.ip,
            ["note"] = "probe submitted; run terminal.history to read output",
        };
    }

    /// <summary>
    /// 读成就解锁状态。原版成就经 Steam 解锁，但解锁时会在 ProgressionFlags 里记
    /// `&lt;成就名&gt;_Unlocked`；Steam 侧状态不可直接读，这里返回游戏内可见的解锁记录。
    /// </summary>
    private static object AchievementsList(OS os)
    {
        var unlocked = new List<object>();
        try
        {
            if (ReadMember(os.Flags, "Flags") is IEnumerable flags)
            {
                foreach (var item in flags)
                {
                    var f = item?.ToString();
                    if (string.IsNullOrEmpty(f)) continue;
                    if (f.EndsWith("_Unlocked", StringComparison.Ordinal))
                        unlocked.Add(f.Substring(0, f.Length - "_Unlocked".Length));
                }
            }
        }
        catch { }
        return new Dictionary<string, object>
        {
            ["count"] = unlocked.Count,
            ["unlocked"] = unlocked,
            ["note"] = "游戏本地解锁记录（Steam 成就名）；Steam 侧状态无法直接读取",
        };
    }
}
