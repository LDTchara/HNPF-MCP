using Hacknet;

namespace HnpfMcpBridge;

/// <summary>文件系统域：列目录 / 读写文件。</summary>
public static partial class Executor
{
    private static object ListFiles(OS os, string ip, string path)
    {
        var comp = FindComputer(os, ip);
        var folder = ResolveFolder(comp, path) ?? throw new ArgumentException($"folder not found: {path}");
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
        var folder = ResolveFolder(comp, path) ?? throw new ArgumentException($"folder not found: {path}");
        var fe = folder.searchForFile(file);
        if (fe == null)
        {
            fe = new FileEntry { name = file, data = content ?? "" };
            folder.files.Add(fe);
        }
        else
        {
            fe.data = content ?? "";
        }
        return new Dictionary<string, object> { ["ok"] = true, ["file"] = file, ["bytes"] = (content ?? "").Length };
    }

    private static object DeleteFile(OS os, string ip, string path, string file)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("file is required");
        var comp = FindComputer(os, ip);
        var folder = ResolveFolder(comp, path) ?? throw new ArgumentException($"folder not found: {path}");
        var fe = folder.searchForFile(file) ?? throw new ArgumentException($"file not found: {path}/{file}");
        folder.files.Remove(fe);
        return new Dictionary<string, object> { ["ok"] = true, ["file"] = file, ["deleted"] = true };
    }

    private static object MoveFile(OS os, string ip, string path, string file, string toPath, string toFile)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("file is required");
        var comp = FindComputer(os, ip);
        var srcFolder = ResolveFolder(comp, path) ?? throw new ArgumentException($"folder not found: {path}");
        var fe = srcFolder.searchForFile(file) ?? throw new ArgumentException($"file not found: {path}/{file}");
        var dstFolder = ResolveFolder(comp, string.IsNullOrEmpty(toPath) ? path : toPath)
            ?? throw new ArgumentException($"destination folder not found: {toPath}");
        var newName = string.IsNullOrEmpty(toFile) ? file : toFile;
        if (srcFolder != dstFolder) srcFolder.files.Remove(fe);
        // 目标已有同名文件则覆盖（与 fs.write 语义一致）
        var existing = dstFolder.searchForFile(newName);
        if (existing != null && existing != fe) dstFolder.files.Remove(existing);
        fe.name = newName;
        if (srcFolder != dstFolder) dstFolder.files.Add(fe);
        return new Dictionary<string, object> { ["ok"] = true, ["file"] = newName, ["folder"] = string.IsNullOrEmpty(toPath) ? (path ?? "/") : toPath };
    }

    private static object ReplaceInFile(OS os, string ip, string path, string file, string target, string replacement)
    {
        if (string.IsNullOrEmpty(file)) throw new ArgumentException("file is required");
        if (string.IsNullOrEmpty(target)) throw new ArgumentException("target is required");
        var comp = FindComputer(os, ip);
        var folder = ResolveFolder(comp, path) ?? throw new ArgumentException($"folder not found: {path}");
        var fe = folder.searchForFile(file) ?? throw new ArgumentException($"file not found: {path}/{file}");
        int before = fe.data?.Length ?? 0;
        int occurrences = 0;
        if (!string.IsNullOrEmpty(fe.data))
        {
            occurrences = CountOccurrences(fe.data, target);
            fe.data = fe.data.Replace(target, replacement ?? "");
        }
        return new Dictionary<string, object>
        {
            ["ok"] = true, ["file"] = file, ["occurrences"] = occurrences,
            ["bytesBefore"] = before, ["bytesAfter"] = fe.data?.Length ?? 0,
        };
    }

    private static int CountOccurrences(string text, string needle)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }

    private static object AppendFile(OS os, string ip, string path, string file, string content)
    {
        var comp = FindComputer(os, ip);
        var folder = ResolveFolder(comp, path) ?? throw new ArgumentException($"folder not found: {path}");
        var fe = folder.searchForFile(file);
        if (fe == null)
        {
            fe = new FileEntry { name = file, data = content ?? "" };
            folder.files.Add(fe);
        }
        else
        {
            fe.data += content ?? "";
        }
        return new Dictionary<string, object> { ["ok"] = true, ["file"] = file, ["bytes"] = (content ?? "").Length };
    }
}
