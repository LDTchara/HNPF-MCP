using System;
using System.Collections;
using System.Collections.Generic;
using Hacknet;

namespace HnpfMcpBridge;

/// <summary>
/// daemon 域：原版 daemon 种类很多（Mail/IRC/Board/Hub 已分别有专用方法），
/// 其余 flavor daemon（PointClicker / SongChanger / HeartMonitor / UploadServer /
/// WebServer / Database / Logo / Markov 等）没有统一协议。
/// 这里做「通用安全投影」：按类型名 + 标量字段 + 集合计数，容忍版本差异，
/// 让 AI 能发现并读懂任意 daemon 的运行态，而不必为每个 daemon 写专用代码。
/// </summary>
public static partial class Executor
{
    private static object GetDaemons(OS os, string ip)
    {
        var comp = MessagingTargetComp(os, ip);
        if (comp == null) throw new ArgumentException("target computer not found: " + ip);
        var list = new List<object>();
        if (comp.daemons != null)
        {
            for (int i = 0; i < comp.daemons.Count; i++)
            {
                var d = comp.daemons[i];
                if (d == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["name"] = SafeStr(() => d.name),
                    ["type"] = d.GetType().Name,
                    ["isListed"] = SafeBool(() => d.isListed),
                    ["fields"] = ProjectDaemonState(d),
                });
            }
        }
        return new Dictionary<string, object>
        {
            ["ip"] = comp.ip,
            ["count"] = list.Count,
            ["daemons"] = list,
        };
    }

    /// <summary>
    /// 把 daemon 实例的可读状态投影成 JSON 友好字典：
    /// 标量字段/属性（string/bool/数值/enum）+ 集合的计数与前几项。
    /// 跳过 XNA/Unity 对象、委托、索引器等（无法安全序列化的会自动略过）。
    /// </summary>
    private static object ProjectDaemonState(object d)
    {
        var result = new Dictionary<string, object>();
        if (d == null) return result;
        var t = d.GetType();
        const System.Reflection.BindingFlags F =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;
        int count = 0;
        try
        {
            foreach (var f in t.GetFields(F))
            {
                if (count >= 40) break;
                if (!TryProjectValue(f.FieldType, () => f.GetValue(d), f.Name, result)) continue;
                count++;
            }
            foreach (var p in t.GetProperties(F))
            {
                if (count >= 40) break;
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                if (!TryProjectValue(p.PropertyType, () => p.GetValue(d), p.Name, result)) continue;
                count++;
            }
        }
        catch { /* 投影失败返回已收集的部分 */ }
        return result;
    }

    private static bool TryProjectValue(Type type, Func<object> getter, string key, Dictionary<string, object> result)
    {
        try
        {
            if (type == typeof(string) || type == typeof(bool) || type.IsEnum
                || type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double))
            {
                var v = getter();
                if (v == null) return false;
                result[key] = v is Enum ? v.ToString() : v;
                return true;
            }
            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                var v = getter() as IEnumerable;
                if (v == null) return false;
                var items = new List<object>();
                int total = 0;
                foreach (var item in v)
                {
                    total++;
                    if (items.Count < 5) items.Add(DescribeValue(item));
                    if (total > 200) break;
                }
                result[key + ".count"] = total;
                if (items.Count > 0) result[key + ".items"] = items;
                return true;
            }
        }
        catch { }
        return false;
    }

    private static object DescribeValue(object item)
    {
        if (item == null) return null;
        var t = item.GetType();
        if (t.IsPrimitive || t.IsEnum || item is string) return item is Enum ? item.ToString() : item;
        // 复合项只给类型名 + ToString（很多原版结构 ToString 可读）
        try { return item.ToString(); }
        catch { return t.Name; }
    }

    /// <summary>媒体/主题状态（原版 MusicManager / ThemeManager 静态字段）。</summary>
    private static object MediaInfo()
    {
        var result = new Dictionary<string, object>();
        try { result["song"] = MusicManager.currentSongName; } catch { }
        try { result["isPlaying"] = MusicManager.isPlaying; } catch { }
        try { result["isMuted"] = MusicManager.isMuted; } catch { }
        try { result["theme"] = ThemeManager.currentTheme.ToString(); } catch { }
        try { result["customThemePath"] = ThemeManager.LastLoadedCustomThemePath; } catch { }
        return result;
    }
}
