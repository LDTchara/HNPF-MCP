using Hacknet;

namespace HnpfMcpBridge;

/// <summary>文件系统域：列目录 / 读写文件。</summary>
public static partial class Executor
{
    private static object ListFiles(OS os, string ip, string path)
    {
        var comp = FindComputer(os, ip);
        var folder = ResolveFolder(comp, path);
        var result = new List<object>();
        ProjectFolder(folder, 0, 8, result);
        return new Dictionary<string, object> { ["ip"] = ip, ["path"] = path ?? "/", ["entries"] = result };
    }

    private static object ReadFile(OS os, string ip, string path, string file)
    {
        var comp = FindComputer(os, ip);
        var folder = ResolveFolder(comp, path);
        var fe = folder?.searchForFile(file);
        if (fe == null) throw new ArgumentException($"file not found: {path}/{file}");
        return new Dictionary<string, object>
        {
            ["name"] = fe.name,
            ["size"] = fe.size,
            ["data"] = fe.data
        };
    }

    private static object WriteFile(OS os, string ip, string path, string file, string content)
    {
        var comp = FindComputer(os, ip);
        var folder = ResolveFolder(comp, path);
        var fe = folder?.searchForFile(file);
        if (fe == null)
        {
            fe = new FileEntry { name = file, data = content ?? "" };
            folder?.files.Add(fe);
        }
        else
        {
            fe.data = content ?? "";
        }
        return new Dictionary<string, object> { ["ok"] = true, ["file"] = file, ["bytes"] = (content ?? "").Length };
    }

    private static object AppendFile(OS os, string ip, string path, string file, string content)
    {
        var comp = FindComputer(os, ip);
        var folder = ResolveFolder(comp, path);
        var fe = folder?.searchForFile(file);
        if (fe == null)
        {
            fe = new FileEntry { name = file, data = content ?? "" };
            folder?.files.Add(fe);
        }
        else
        {
            fe.data += content ?? "";
        }
        return new Dictionary<string, object> { ["ok"] = true, ["file"] = file, ["bytes"] = (content ?? "").Length };
    }
}
