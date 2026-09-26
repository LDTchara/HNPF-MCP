using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hacknet;

namespace HnpfMcpBridge;

/// <summary>
/// G 节（借鉴 DSL-AITOOL，2026-08-21）：
///   mission.submit    { sender?, details }  → 提交任务答案（isComplete + sender 验证 + m.finish()）
///   hub.list          { ip? }               → 任务板（MissionHubServer/MissionListingServer/DLCHubServer）
///   hub.accept        { ip?, id }           → 接取任务（contract:/listing:/dhs:）
///   shell.drive       { action }            → shell 驱动（overload/cancel/exit，反射 ShellOverloaderExe）
///   terminal.type     { text }              → 终端模拟输入（不回车，currentLine 字段注入）
///   os.memory         { ip? }               → DLC 内存读取（dataBlocks/commandsRun/fileFragments/images）
/// </summary>
public static partial class Executor
{
    // ---- G2 mission.submit ----

    /// <summary>A6 防火墙直改（对位 DSL-AITOOL firewall_crack）：firewall.solved = true（调试/验收场景）。</summary>
    private static object FirewallCrack(OS os, string ip)
    {
        var comp = MessagingTargetComp(os, ip);
        if (comp?.firewall == null)
            return new Dictionary<string, object> { ["ok"] = true, ["solved"] = false, ["message"] = "no firewall on target" };
        if (comp.firewall.solved)
            return new Dictionary<string, object> { ["ok"] = true, ["solved"] = true, ["already"] = true, ["ip"] = comp.ip };
        comp.firewall.solved = true;
        return new Dictionary<string, object> { ["ok"] = true, ["solved"] = true, ["ip"] = comp.ip };
    }

    /// <summary>提交任务答案：details 按换行/分号拆分 → m.isComplete(list) → sender 验证 → m.finish()。</summary>
    private static object MissionSubmit(OS os, string sender, string details)
    {
        var m = os.currentMission ?? os.branchMissions?.FirstOrDefault();
        if (m == null) throw new ArgumentException("no current or branch mission to submit");
        var list = (details ?? "").Split(new[] { '\n', ';', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (list.Count == 0) throw new ArgumentException("missing details (reply strings, newline/semicolon separated)");
        bool complete;
        try { complete = m.isComplete(list); } catch { complete = false; }
        if (!complete)
        {
            var bad = new List<string>();
            if (GetFieldOrProp(m, "goals") is IEnumerable goals)
                foreach (var g in goals)
                {
                    try { if (g != null && !(bool)(g.GetType().GetMethod("isComplete", new[] { typeof(List<string>) })?.Invoke(g, new object[] { list }) ?? false)) bad.Add(g.GetType().Name); }
                    catch { bad.Add(g?.GetType().Name ?? "?"); }
                }
            throw new ArgumentException("mission not complete with supplied details; incomplete goals: " +
                (bad.Count > 0 ? string.Join(", ", bad) : "none (sender verification likely failed)"));
        }
        var email = GetFieldOrProp(m, "email");
        var emailSender = GetFieldOrProp(email, "sender")?.ToString();
        if (!m.ShouldIgnoreSenderVerification && !string.IsNullOrWhiteSpace(emailSender)
            && (string.IsNullOrWhiteSpace(sender) || !sender.Trim().Equals(emailSender.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("sender verification failed; pass the mission email sender (see mission_detail)");
        m.finish();
        os.MissionCompleteFlashTime = 3f;
        return new Dictionary<string, object>
        {
            ["ok"] = true, ["completed"] = true,
            ["mission"] = m.reloadGoalsSourceFile ?? m.nextMission ?? ""
        };
    }

    // ---- G4 hub.list / hub.accept ----

    /// <summary>任务板：反射目标机 MissionHubServer（contract:）/MissionListingServer（listing:）/DLCHubServer（dhs:）。</summary>
    private static object HubList(OS os, string ip)
    {
        var comp = MessagingTargetComp(os, ip);
        if (comp?.daemons == null) throw new ArgumentException($"no daemons on {comp?.ip}");
        var hubs = new List<object>();
        foreach (var d in comp.daemons)
        {
            if (d == null) continue;
            var t = d.GetType();
            var missions = new List<object>();
            string kind = null;
            if (t.Name == "MissionHubServer")
            {
                kind = "MissionHubServer";
                if (GetFieldOrProp(d, "listingMissions") is IDictionary lm)
                    foreach (DictionaryEntry e in lm) missions.Add(HubMission("contract:" + e.Key, e.Value));
            }
            else if (t.Name == "MissionListingServer")
            {
                kind = "MissionListingServer";
                if (GetFieldOrProp(d, "missions") is IList ms)
                    for (int i = 0; i < ms.Count; i++) missions.Add(HubMission("listing:" + i, ms[i]));
            }
            else if (t.Name == "DLCHubServer")
            {
                kind = "DLCHubServer";
                if (GetFieldOrProp(d, "ActiveMissions") is IList am)
                    for (int i = 0; i < am.Count; i++)
                    {
                        var mi = GetFieldOrProp(am[i], "Mission");
                        if (mi != null) missions.Add(HubMission("dhs:" + i, mi));
                    }
            }
            if (kind != null)
                hubs.Add(new Dictionary<string, object>
                {
                    ["daemon"] = kind,
                    ["name"] = d.name,
                    ["group"] = GetFieldOrProp(d, "groupName")?.ToString(),
                    ["missions"] = missions
                });
        }
        if (hubs.Count == 0)
            throw new ArgumentException($"no mission hub daemon (MissionHubServer/MissionListingServer/DLCHubServer) on {comp?.ip}");
        return new Dictionary<string, object> { ["ip"] = comp?.ip, ["hubs"] = hubs };
    }

    private static object HubMission(string id, object mission) => new Dictionary<string, object>
    {
        ["id"] = id,
        ["title"] = GetFieldOrProp(mission, "postingTitle")?.ToString()
    };

    /// <summary>接取任务：contract → acceptMission(mission, idx, regId)；listing/dhs → acceptMission(idx)。</summary>
    private static object HubAccept(OS os, string ip, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.Contains(":"))
            throw new ArgumentException("missing/invalid id (from hub.list, e.g. contract:xxx / listing:0 / dhs:0)");
        var comp = MessagingTargetComp(os, ip);
        var prefix = id.Split(':')[0];
        var typeName = prefix == "contract" ? "MissionHubServer" : prefix == "listing" ? "MissionListingServer" : prefix == "dhs" ? "DLCHubServer" : "__none__";
        var d = FindDaemon(comp, typeName);
        if (d == null) throw new ArgumentException($"no hub daemon ({typeName}) for id {id} on {comp?.ip}");
        if (os.currentMission != null) throw new ArgumentException("a contract/mission is already active; abort it before accepting a new one");
        var regId = id.Substring(id.IndexOf(':') + 1);
        try
        {
            var meth = d.GetType().GetMethod("acceptMission", PrivFlags);
            if (prefix == "contract")
            {
                if (GetFieldOrProp(d, "listingMissions") is IDictionary lm && lm[regId] != null)
                {
                    var folder = GetFieldOrProp(d, "listingsFolder") as Folder;
                    int idx = -1;
                    if (folder?.files != null)
                        for (int i = 0; i < folder.files.Count; i++)
                            if (folder.files[i]?.name?.Contains("#" + regId) == true) { idx = i; break; }
                    if (meth != null) meth.Invoke(d, new object[] { lm[regId], idx, regId });
                }
                else throw new ArgumentException($"contract '{regId}' not found on {comp?.ip}");
            }
            else
            {
                if (meth != null) meth.Invoke(d, new object[] { int.Parse(regId) });
                else throw new ArgumentException($"acceptMission not found on {typeName}");
            }
            return new Dictionary<string, object> { ["ok"] = true, ["accepted"] = id };
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex) { throw new ArgumentException($"accept failed: {ex.Message}"); }
    }

    // ---- G3 shell.drive / terminal.type ----

    /// <summary>shell 驱动：反射 Hacknet.ShellOverloaderExe.RunShellOverloaderExe(args, os, target)。</summary>
    private static object ShellDrive(OS os, string action)
    {
        var a = (action ?? "overload").Trim().ToLower();
        var flag = a is "overload" or "o" ? "-o" : a is "cancel" or "c" ? "-c" : a is "exit" or "e" ? "-e" : null;
        if (flag == null) throw new ArgumentException($"unknown shell action '{action}' (use overload / cancel / exit)");
        var meth = typeof(OS).Assembly.GetType("Hacknet.ShellOverloaderExe")
            ?.GetMethod("RunShellOverloaderExe", BindingFlags.Static | BindingFlags.Public);
        if (meth == null) throw new ArgumentException("ShellOverloaderExe.RunShellOverloaderExe not found");
        meth.Invoke(null, new object[] { new[] { flag }, os, os.connectedComp ?? os.thisComputer });
        return new Dictionary<string, object> { ["ok"] = true, ["action"] = a };
    }

    /// <summary>终端模拟输入（不回车）：反射 Terminal.currentLine 字段注入文本。</summary>
    private static object TypeInput(OS os, string text)
    {
        if (text == null) throw new ArgumentException("missing text");
        var terminal = GetFieldOrProp(os, "terminal");
        var f = terminal?.GetType().GetField("currentLine", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) throw new ArgumentException("terminal.currentLine not found");
        f.SetValue(terminal, text);
        return new Dictionary<string, object> { ["ok"] = true, ["typed"] = text };
    }

    // ---- G5 os.memory（DLC） ----

    /// <summary>读目标机内存（DLC RamModule）：dataBlocks/commandsRun/fileFragments/images。</summary>
    private static object OsMemory(OS os, string ip)
    {
        var comp = MessagingTargetComp(os, ip);
        var mem = GetFieldOrProp(comp, "Memory");
        if (mem == null) return new Dictionary<string, object> { ["ip"] = comp?.ip, ["memory"] = null, ["note"] = "no Memory (DLC RamModule) on target" };
        return new Dictionary<string, object>
        {
            ["ip"] = comp?.ip,
            ["memory"] = new Dictionary<string, object>
            {
                ["dataBlocks"] = ToList(GetFieldOrProp(mem, "DataBlocks")),
                ["commandsRun"] = ToList(GetFieldOrProp(mem, "CommandsRun")),
                ["fileFragments"] = ToDict(GetFieldOrProp(mem, "FileFragments")),
                ["images"] = ToList(GetFieldOrProp(mem, "Images"))
            }
        };
    }

    private static List<object> ToList(object v)
    {
        var r = new List<object>();
        if (v is IEnumerable e) foreach (var x in e) r.Add(x?.ToString());
        return r;
    }

    private static Dictionary<string, object> ToDict(object v)
    {
        var r = new Dictionary<string, object>();
        if (v is IDictionary d) foreach (DictionaryEntry e in d) r[e.Key?.ToString() ?? "?"] = e.Value?.ToString();
        return r;
    }
}
