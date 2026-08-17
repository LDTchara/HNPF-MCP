using System.Reflection;

namespace HnpfMcpBridge;

/// <summary>最近事件环形缓冲（P2 事件订阅）。事件带自增 id，AI 可增量拉取。</summary>
public static class EventBuffer
{
    private static readonly List<Dictionary<string, object>> Events = new();
    private static long NextId = 1;
    private const int Max = 100;
    private static readonly object Lock = new();

    public static void Push(string name, object data)
    {
        lock (Lock)
        {
            Events.Add(new Dictionary<string, object>
            {
                ["id"] = NextId++,
                ["event"] = name,
                ["data"] = data,
                ["t"] = DateTime.UtcNow.ToString("HH:mm:ss")
            });
            if (Events.Count > Max) Events.RemoveRange(0, Events.Count - Max);
        }
    }

    public static object Get(long since)
    {
        lock (Lock)
        {
            var list = Events.Where(e => Convert.ToInt64(e["id"]) > since).ToList();
            return new Dictionary<string, object> { ["events"] = list, ["nextId"] = NextId };
        }
    }
}

/// <summary>
/// 反射发现 Pathfinder 统一注册表（第二层抽象的真机兑现）：
/// 列出所有模组注册的 命令 / 动作 / exe / daemon，AI 无需预知即可发现模组能力。
/// 不同 Pathfinder 版本字段名可能不同，全部反射 + try-catch 宽容处理。
/// </summary>
public static class Registry
{
    public static object List()
    {
        var actions = GetActions();
        // 遍历所有 action 类型触发 XmlDoc 加载（按程序集累积 Errors），拿到完整诊断
        try
        {
            foreach (var a in actions)
            {
                if (a is not Dictionary<string, object> d) continue;
                if (d.TryGetValue("_type", out var tv) && tv is Type t) XmlDoc.TypeSummary(t);
            }
        }
        catch { }

        var result = new Dictionary<string, object>
        {
            ["commands"] = GetCommands(),
            ["actions"] = actions
                .Where(a => a is Dictionary<string, object>)
                .Select(a => { var d = new Dictionary<string, object>((Dictionary<string, object>)a); d.Remove("_type"); return d; })
                .ToList(),
            ["executables"] = GetExecutables(),
            ["daemons"] = GetDaemons()
        };
        if (XmlDoc.Errors.Count > 0)
        {
            var diag = new Dictionary<string, object>();
            foreach (var kv in XmlDoc.Errors) diag[kv.Key] = kv.Value;
            result["_diag"] = diag;
        }
        return result;
    }

    /// <summary>Pathfinder.Command.CommandManager.CustomCommands (AssemblyAssociatedList&lt;CustomCommand&gt;, item.Name)</summary>
    private static List<object> GetCommands()
    {
        var result = new List<object>();
        try
        {
            var field = typeof(Pathfinder.Command.CommandManager)
                .GetField("CustomCommands", BindingFlags.NonPublic | BindingFlags.Static);
            if (field != null)
            {
                var list = field.GetValue(null);
                var allItems = list?.GetType().GetProperty("AllItems")?.GetValue(list) as System.Collections.IEnumerable;
                if (allItems != null)
                    foreach (var item in allItems)
                    {
                        var name = item.GetType().GetField("Name")?.GetValue(item)?.ToString();
                        if (!string.IsNullOrEmpty(name)) result.Add(name);
                    }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Pathfinder.Action.ActionManager.CustomActions (Dictionary&lt;string, Type&gt;, key=xml 名)
    /// 反射 Action 类型的 public 字段作为 XML 参数提示（KE 等模组的 Action 字段都是 public）。
    /// </summary>
    private static List<object> GetActions()
    {
        var result = new List<object>();
        try
        {
            var field = typeof(Pathfinder.Action.ActionManager)
                .GetField("CustomActions", BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    var name = e.Key?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    var actionType = e.Value as Type;
                    var entry = new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["_type"] = actionType,
                        ["params"] = actionType != null ? GetPublicFields(actionType) : new List<object>()
                    };
                    // 类注释（T: 前缀）作为 Action 的整体用法说明（KE 的语义写在类注释里，含用法示例）
                    var desc = XmlDoc.TypeSummary(actionType);
                    if (!string.IsNullOrEmpty(desc)) entry["description"] = desc;
                    result.Add(entry);
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>Action 类型的 public 字段（含类型名与 XML 文档描述），作为 XML 属性提示。</summary>
    private static List<object> GetPublicFields(Type t)
    {
        var list = new List<object>();
        try
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.IsInitOnly || f.IsLiteral) continue;   // 跳过 const/只读
                var entry = new Dictionary<string, object>
                {
                    ["name"] = f.Name,
                    ["type"] = f.FieldType.Name
                };
                // 模组开 GenerateDocumentationFile 时，字段的 /// 注释自动可见（如 FlashScreen.Color 的格式说明）
                var desc = XmlDoc.FieldSummary(t, f.Name);
                if (!string.IsNullOrEmpty(desc)) entry["description"] = desc;
                list.Add(entry);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Pathfinder.Executable.ExecutableManager._customExes (List&lt;CustomExeInfo&gt;, .XmlId)。
    /// A5：附带公开字段（构造参数提示），如 #PROGNAME# 的自定义 exe 参数。</summary>
    private static List<object> GetExecutables()
    {
        var result = new List<object>();
        try
        {
            var field = typeof(Pathfinder.Executable.ExecutableManager)
                .GetField("_customExes", BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is System.Collections.IEnumerable list)
                foreach (var item in list)
                {
                    var xmlId = item.GetType().GetField("XmlId")?.GetValue(item)?.ToString();
                    if (string.IsNullOrEmpty(xmlId)) continue;
                    var entry = new Dictionary<string, object> { ["name"] = xmlId };
                    var fields = GetPublicFields(item.GetType());
                    if (fields.Count > 0) entry["params"] = fields;
                    result.Add(entry);
                }
        }
        catch { }
        return result;
    }

    /// <summary>Pathfinder.Daemon.DaemonManager.CustomDaemons (List&lt;Type&gt;, .Name)。
    /// A5：附带 daemon 类型公开实例字段（运行时状态提示）。</summary>
    private static List<object> GetDaemons()
    {
        var result = new List<object>();
        try
        {
            var field = typeof(Pathfinder.Daemon.DaemonManager)
                .GetField("CustomDaemons", BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is System.Collections.IEnumerable list)
                foreach (var t in list)
                {
                    if (t is not Type ty) continue;
                    if (string.IsNullOrEmpty(ty.Name)) continue;
                    var entry = new Dictionary<string, object> { ["name"] = ty.Name };
                    var fields = GetPublicFields(ty);
                    if (fields.Count > 0) entry["params"] = fields;
                    result.Add(entry);
                }
        }
        catch { }
        return result;
    }


/// <summary>读取程序集 XML 文档注释（{程序集}.xml，需模组开 GenerateDocumentationFile）。
/// 让 registry 的 Action 参数带上 /// 语义描述（如 FlashScreen.Color 的合法格式），
/// AI 不再只能看到裸类型名。缓存按程序集。LLM：语义最后手段仍靠试错，但文档注释解决大多数。</summary>
internal static class XmlDoc
{
    private static readonly Dictionary<string, Dictionary<string, string>> Cache = new();
    /// <summary>按程序集累积诊断信息（找不到 xml / xml 无成员等），供 registry._diag 返回排障。</summary>
    public static readonly Dictionary<string, string> Errors = new();

    /// <summary>取 member 的 summary 文本（member 形如 F:Namespace.Type.Field / M:... / T:...）。</summary>
    public static string Summary(Type t, string member)
    {
        try
        {
            if (t?.Assembly == null) return null;
            var asm = t.Assembly;
            var asmKey = asm.GetName().Name ?? "";
            if (!Cache.TryGetValue(asmKey, out var members))
            {
                members = new Dictionary<string, string>();
                try
                {
                    // 程序集名可能带 BepInEx 后缀（如 KernelExtensions-639225119085033995）→ xml 用去后缀名匹配
                    var xmlBase = System.Text.RegularExpressions.Regex.Replace(asmKey, @"-\d+$", "");
                    // BepInEx 从字节加载插件 → asm.Location 常为空；baseDir 也可能为空 → 用 exe 目录兜底
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    if (string.IsNullOrEmpty(baseDir))
                    {
                        try { baseDir = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName); } catch { }
                    }
                    if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.CurrentDirectory;

                    var xmlPath = (string)null;
                    var locDir = string.IsNullOrEmpty(asm.Location) ? null : Path.GetDirectoryName(asm.Location);
                    if (!string.IsNullOrEmpty(locDir)) xmlPath = Path.Combine(locDir, xmlBase + ".xml");
                    if (xmlPath == null || !File.Exists(xmlPath))
                    {
                        var candidate = Path.Combine(baseDir, xmlBase + ".xml");
                        xmlPath = File.Exists(candidate) ? candidate : null;
                    }
                    if (xmlPath == null || !File.Exists(xmlPath))
                    {
                        foreach (var root in new[] { Path.Combine(baseDir, "Extensions"), Path.Combine(baseDir, "BepInEx", "plugins") })
                        {
                            xmlPath = FindXmlRecursive(root, xmlBase + ".xml");
                            if (xmlPath != null) break;
                        }
                    }
                    if (xmlPath != null && File.Exists(xmlPath))
                    {
                        var doc = System.Xml.Linq.XDocument.Load(xmlPath);
                        foreach (var m in doc.Descendants("member"))
                        {
                            var name = m.Attribute("name")?.Value;
                            var summary = m.Element("summary")?.Value;
                            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(summary))
                                members[name] = string.Join(" ", summary.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                        }
                        if (members.Count == 0) Errors[asmKey] = $"xml 存在({xmlPath})但无带 summary 的成员";
                    }
                    else
                    {
                        Errors[asmKey] = $"找不到 {xmlBase}.xml（asm.Location={(asm.Location ?? "空")}，baseDir={baseDir}）";
                    }
                }
                catch (Exception ex) { Errors[asmKey] = ex.GetType().Name + ": " + ex.Message; }
                Cache[asmKey] = members;
            }
            return members.TryGetValue(member, out var s) ? s : null;
        }
        catch { return null; }
    }

    public static string FieldSummary(Type t, string fieldName) =>
        Summary(t, $"F:{t.FullName}.{fieldName}");

    public static string TypeSummary(Type t) =>
        Summary(t, $"T:{t.FullName}");

    /// <summary>容错递归查找文件（遇无权限/异常目录直接跳过，避免整个遍历失败）。</summary>
    private static string FindXmlRecursive(string root, string fileName)
    {
        try
        {
            if (Directory.Exists(root))
            {
                var direct = Path.Combine(root, fileName);
                if (File.Exists(direct)) return direct;
                foreach (var d in Directory.GetDirectories(root))
                {
                    var f = FindXmlRecursive(d, fileName);
                    if (f != null) return f;
                }
            }
        }
        catch { }
        return null;
    }
}

}
