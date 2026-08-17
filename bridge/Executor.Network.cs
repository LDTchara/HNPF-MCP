using Hacknet;

namespace HnpfMcpBridge;

/// <summary>网络域：端口 / 邮件 / 存档列表。</summary>
public static partial class Executor
{
    private static object PortChange(OS os, string ip, int port, bool open)
    {
        var comp = FindComputer(os, ip);
        if (port <= 0) throw new ArgumentException("port must be > 0");
        // 原版语义：只能打开/关闭「已存在」的端口；凭空加端口属于模组行为（Pathfinder AddPort）
        bool existed = comp.ports != null && comp.ports.Contains(port);
        if (!existed)
            throw new ArgumentException(
                $"port {port} not present on {comp.ip} (only existing ports can be opened; ports={Safe(comp.ports)})");
        if (open) comp.openPort(port, os.thisComputer.ip);
        else comp.closePort(port, os.thisComputer.ip);
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["ip"] = comp.ip,
            ["port"] = port,
            ["state"] = open ? "open" : "closed"
        };
    }

    // ---------------- 邮件（MailServer.accounts 反射） ----------------
    // 注意：MailServer 是 internal class，且 libs/ 与游戏运行时的程序集版本可能不一致，
    // 编译期 typeof(MailServer)/getDaemon(typeof) 精确匹配会失败 → 一律用运行时类型名 +
    // 运行时类型反射，与 RamModule 兜底同思路，免疫版本差异。

    private static Folder GetMailAccountsFolder(Computer comp)
    {
        if (comp?.daemons == null) return null;
        foreach (var d in comp.daemons)
        {
            if (d == null || d.GetType().Name != "MailServer") continue;
            // 注意：Pathfinder Patcher 的 MakePublic 把 Hacknet.exe 内所有字段改成 public，
            // 反射必须 Public|NonPublic 都包含（仅 NonPublic 会匹配不到）
            var f = d.GetType().GetField("accounts",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f != null) return f.GetValue(d) as Folder;
        }
        return null;
    }

    private static object MailList(OS os, string ip)
    {
        var comp = FindMailServerComp(os, ip);
        var accounts = GetMailAccountsFolder(comp);
        if (accounts == null)
            throw new ArgumentException(NoMailServerMsg(comp));
        var users = new List<object>();
        foreach (var userFolder in accounts.folders ?? new List<Folder>())
        {
            var mailboxes = new List<object>();
            foreach (var box in userFolder.folders ?? new List<Folder>())
            {
                var mails = new List<object>();
                foreach (var fe in box.files ?? new List<FileEntry>())
                    mails.Add(new Dictionary<string, object> { ["subject"] = fe.name, ["size"] = fe.size });
                mailboxes.Add(new Dictionary<string, object> { ["name"] = box.name, ["mails"] = mails });
            }
            users.Add(new Dictionary<string, object> { ["user"] = userFolder.name, ["mailboxes"] = mailboxes });
        }
        return new Dictionary<string, object> { ["ip"] = comp.ip, ["users"] = users };
    }

    /// <summary>
    /// 定位邮件服务器节点：显式 ip &gt; 原版默认引用 os.netMap.mailServer &gt; 当前连接节点。
    /// 原版单机模式 LoadContent 总会生成默认邮件服务器（generateGameNodes → JMailServer.xml，
    /// netMap.mailServer 引用它）；扩展模式（IsInExtensionMode）网络完全由扩展定义，不会自动生成。
    /// </summary>
    private static Computer FindMailServerComp(OS os, string ip)
    {
        if (!string.IsNullOrEmpty(ip)) return FindComputer(os, ip);
        if (os.netMap?.mailServer != null) return os.netMap.mailServer;
        if (os.connectedComp != null) return os.connectedComp;
        return os.thisComputer;
    }

    /// <summary>诊断信息：列出节点 daemons 的真实类型全名（便于定位 MailServer 匹配问题）。</summary>
    private static string NoMailServerMsg(Computer comp)
    {
        var types = new List<string>();
        foreach (var d in comp?.daemons ?? new List<Daemon>())
            types.Add(d == null ? "null" : d.GetType().FullName);
        return $"no MailServer daemon on {comp?.ip} (daemons: [{string.Join(", ", types)}]；原版单机有默认邮件服务器；扩展模式需扩展定义)";
    }

    private static object MailRead(OS os, string ip, string user, string folder, string subject)
    {
        var comp = FindMailServerComp(os, ip);
        var accounts = GetMailAccountsFolder(comp);
        if (accounts == null) throw new ArgumentException(NoMailServerMsg(comp));
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(subject))
            throw new ArgumentException("user and subject are required");

        Folder userFolder = null;
        foreach (var uf in accounts.folders ?? new List<Folder>())
            if (string.Equals(uf.name, user, StringComparison.OrdinalIgnoreCase)) { userFolder = uf; break; }
        if (userFolder == null) throw new ArgumentException($"user '{user}' not found on {comp.ip}");

        Folder box = null;
        foreach (var b in userFolder.folders ?? new List<Folder>())
            if (string.Equals(b.name, folder ?? "inbox", StringComparison.OrdinalIgnoreCase)) { box = b; break; }
        if (box == null) box = userFolder.folders?.FirstOrDefault();

        FileEntry mail = null;
        foreach (var fe in box?.files ?? new List<FileEntry>())
            if (string.Equals(fe.name, subject, StringComparison.OrdinalIgnoreCase)) { mail = fe; break; }
        if (mail == null) throw new ArgumentException($"mail '{subject}' not found in {user}/{box?.name}");

        // 邮件数据格式：序号@*&^#%@)_!_)*#^@!&*)(#^&\nsender\nsubject\nbody...
        var parts = mail.data.Split(MailSplitDelims, StringSplitOptions.None);
        return new Dictionary<string, object>
        {
            ["user"] = user,
            ["mailbox"] = box?.name,
            ["subject"] = mail.name,
            ["sender"] = parts.Length > 1 ? parts[1].Trim() : "",
            ["body"] = parts.Length > 3 ? string.Join("\n", parts.Skip(3)) : mail.data,
            ["rawSize"] = mail.size
        };
    }

    private static readonly string[] MailSplitDelims = { "@*&^#%@)_!_)*#^@!&*)(#^&\n" };

    // ---------------- 存档列表（load_game 无原生支持，提供列表） ----------------
    // PF 补丁后存档统一在 {文档}\My Games\HacknetPathfinder\Accounts：
    //   主线存档 = Accounts\save_{SaveGameUserName}.xml（根目录）
    //   扩展存档 = Accounts\{扩展名}\save_{SaveGameUserName}.xml（子文件夹，名称=扩展名）
    // 存档按账号分文件（SaveFileManager 按登录用户名定位）。

    private static object SaveList(OS os)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "HacknetPathfinder", "Accounts");
        var saves = new List<object>();

        // 主线存档（Accounts 根目录）
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.GetFiles(root, "*.xml"))
                saves.Add(new Dictionary<string, object> { ["path"] = f, ["name"] = Path.GetFileName(f) });
        }

        // 扩展存档（Accounts\{扩展名}\ 子文件夹，递归一层）
        if (Directory.Exists(root))
        {
            foreach (var sub in Directory.GetDirectories(root))
            {
                var extName = Path.GetFileName(sub);
                foreach (var f in Directory.GetFiles(sub, "*.xml"))
                    saves.Add(new Dictionary<string, object>
                    {
                        ["path"] = f,
                        ["name"] = Path.GetFileName(f),
                        ["extension"] = extName
                    });
            }
        }

        // 标记当前会话存档：SaveGameUserName 可能是完整文件名（save_10.xml）或完整路径
        // （menu_load_extension_save 读档进扩展时 SaveGameUserName = 存档路径），按文件名匹配
        // （可能命中主线+扩展多处；快照端优先带 extension 的）
        string currentName = null;
        try
        {
            currentName = os.SaveGameUserName;
            if (!string.IsNullOrEmpty(currentName) && currentName.IndexOfAny(new[] { '\\', '/' }) >= 0)
                currentName = Path.GetFileName(currentName);
        }
        catch { }
        if (!string.IsNullOrEmpty(currentName))
        {
            foreach (var sObj in saves)
            {
                if (sObj is Dictionary<string, object> s
                    && string.Equals(s["name"]?.ToString(), currentName, StringComparison.OrdinalIgnoreCase))
                    s["current"] = true;
            }
        }

        // 当前活动扩展名（扩展模式下用于优先匹配扩展子文件夹存档）。
        // 注意：扩展名（extensionInfo <Name>）与扩展文件夹名可以不一致——存档子文件夹名 = 扩展名，
        // 因此取 ActiveExtensionInfo.Name（FolderPath 兜底）。
        // ExtensionLoader 是 Hacknet.Extensions 外部程序集类型，用简单名匹配最稳（同 MailServer 思路）。
        // 当前活动扩展名 + 诊断（失败原因不吞，返回 _diag 便于排障）
        string currentExt = null;
        string currentExtDiag = null;
        try
        {
            // os 是运行时对象：其 Assembly 即游戏主程序集（HacknetPathfinder.exe），
            // 直接 GetType 按全名定位 ExtensionLoader（绕开 AppDomain 遍历与 GetTypes 异常）
            var loader = os.GetType().Assembly.GetType("Hacknet.Extensions.ExtensionLoader", false);
            if (loader == null) { currentExtDiag = "ExtensionLoader 类型未找到"; }
            else
            {
                // ActiveExtensionInfo 可能是静态属性也可能是静态字段（定义在外部程序集），两者都试
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                object info = loader.GetProperty("ActiveExtensionInfo", flags)?.GetValue(null);
                if (info == null) info = loader.GetField("ActiveExtensionInfo", flags)?.GetValue(null);
                if (info == null) { currentExtDiag = "ActiveExtensionInfo 为 null（属性/字段都试过，可能当前不在扩展模式）"; }
                else
                {
                    var tInfo = info.GetType();
                    var iFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    // Name/FolderPath 可能是属性也可能是字段，逐个尝试
                    currentExt =
                        tInfo.GetProperty("Name", iFlags)?.GetValue(info)?.ToString()
                        ?? tInfo.GetField("Name", iFlags)?.GetValue(info)?.ToString()
                        ?? tInfo.GetProperty("FolderPath", iFlags)?.GetValue(info)?.ToString()
                        ?? tInfo.GetField("FolderPath", iFlags)?.GetValue(info)?.ToString();
                    if (string.IsNullOrEmpty(currentExt)) currentExtDiag = "Name/FolderPath（属性+字段）均无值";
                }
            }
        }
        catch (Exception ex) { currentExtDiag = ex.GetType().Name + ": " + ex.Message; }

        return new Dictionary<string, object>
        {
            ["note"] = "load_game is not supported at runtime (Hacknet loads saves at startup only)",
            ["saveRoot"] = root,
            ["currentUser"] = os.SaveGameUserName,
            ["currentExtension"] = currentExt,
            ["currentExtensionDiag"] = currentExtDiag,
            ["saves"] = saves
        };
    }
}
