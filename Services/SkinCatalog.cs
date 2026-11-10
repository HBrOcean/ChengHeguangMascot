using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ChengHeguangMascot.Services;

/// <summary>一套皮肤的元信息。</summary>
public sealed class SkinEntry
{
    /// <summary>目录名，同时也是 .atlas / .skel 的文件名前缀（即 UTTU 的 garmentId）。</summary>
    public string Id { get; init; } = "";

    /// <summary>界面上显示的名字（一定有值，不会为 null）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>皮肤目录；资源缺失时为 null。</summary>
    public string? Dir { get; init; }

    /// <summary>.atlas 与 .skel 齐全，真的能加载。</summary>
    public bool IsUsable { get; init; }
}

/// <summary>
/// 皮肤目录扫描。列表完全来自文件系统，代码里不再保留任何硬编码的皮肤 id。
///
/// 约定：Assets/characters/&lt;id&gt;/ 下放同名的 &lt;id&gt;.atlas、&lt;id&gt;.skel 和图集，
/// 该目录即为一套皮肤。放一个 display.txt（UTF-8，单行）可以自定义显示名。
///
/// 显示名三级兜底：配置里的 SkinNames → display.txt 第一行 → 目录名。
/// 排序：可用优先；可用内部按 <see cref="PreferredOrder"/> 排（默认皮肤在最前），其余按 id 升序。
/// </summary>
public static class SkinCatalog
{
    /// <summary>
    /// 默认皮肤的优先顺序（只是排序提示，不是「内置列表」——
    /// 这些 id 对应的目录被删掉时它们会自然从列表里消失）。
    /// 程和光三套装扮：默认 → 檐外鹭（洞悉）→ 第三秒（时装）。
    /// </summary>
    private static readonly string[] PreferredOrder = { "313701", "313702", "313703" };

    /// <summary>配置里保存的显示名（id → 名字）。由 ConfigStore.Load 注入。</summary>
    private static Dictionary<string, string> _configuredNames =
        new(StringComparer.OrdinalIgnoreCase);

    private static List<SkinEntry>? _cache;

    /// <summary>
    /// 注入配置里的显示名。
    /// ⚠ 必须在任何人读取 <see cref="All"/> 之前调用（ConfigStore.Load 里已经保证）。
    /// 注入后丢弃缓存，下次访问会按新名字重新生成条目。
    /// </summary>
    public static void SetConfiguredNames(IReadOnlyDictionary<string, string>? names)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (names != null)
        {
            foreach (var kv in names)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                map[kv.Key.Trim()] = kv.Value.Trim();
            }
        }
        _configuredNames = map;
        _cache = null;
    }

    /// <summary>当前可用的全部皮肤（可用优先，其次按默认顺序 / id）。</summary>
    public static IReadOnlyList<SkinEntry> All => _cache ??= Scan();

    /// <summary>默认皮肤：优先顺序里第一个可用的；都不在就用排在最前的可用皮肤。</summary>
    public static string DefaultSkinId
    {
        get
        {
            var all = All;
            foreach (string id in PreferredOrder)
            {
                foreach (var e in all)
                    if (e.IsUsable && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
                        return e.Id;
            }
            foreach (var e in all)
                if (e.IsUsable) return e.Id;
            return "";
        }
    }

    /// <summary>丢弃缓存，下次访问 All 时重新扫描。</summary>
    public static void Invalidate() => _cache = null;

    /// <summary>皮肤显示名：配置名 → display.txt → 目录名。</summary>
    public static string DisplayNameOf(string id)
    {
        var hit = All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit.DisplayName;

        // 不在缓存里（比如配置里存了已删掉的皮肤），现场算一个
        string? dir = DirOf(id);
        return dir != null ? DisplayNameFromDir(id, dir) : id;
    }

    /// <summary>某套皮肤的目录；找不到返回 null。</summary>
    public static string? DirOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (string root in RootCandidates())
        {
            string dir = Path.Combine(root, id);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    /// <summary>characters 根目录的候选路径（程序目录优先，其次是开发时的源码目录）。</summary>
    public static IEnumerable<string> RootCandidates()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Assets", "characters");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Assets", "characters"));
    }

    private static List<SkinEntry> Scan()
    {
        // id → 目录。同名时以先找到的为准（程序目录优先于源码目录）
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in RootCandidates())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string id = Path.GetFileName(dir);
                    if (!found.ContainsKey(id)) found[id] = dir;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Skin] 扫描目录失败 {root}", ex);
            }
        }

        var list = found
            .Select(kv => Make(kv.Key, kv.Value))
            .OrderBy(e => e.IsUsable ? 0 : 1)
            .ThenBy(e => OrderOf(e.Id))
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Logger.Log($"[Skin] 扫描到 {list.Count} 套皮肤（其中可用 {list.Count(e => e.IsUsable)} 套）");
        foreach (var e in list)
            Logger.Log($"[Skin]   {e.Id} → {e.DisplayName}{(e.IsUsable ? "" : "（资源缺失）")}");

        return list;
    }

    /// <summary>PreferredOrder 里的位置；不在列表里的排在后面。</summary>
    private static int OrderOf(string id)
    {
        for (int i = 0; i < PreferredOrder.Length; i++)
            if (string.Equals(PreferredOrder[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return PreferredOrder.Length;
    }

    private static SkinEntry Make(string id, string? dir)
    {
        bool usable = false;
        if (dir != null)
        {
            try
            {
                usable = File.Exists(Path.Combine(dir, id + ".atlas"))
                      && File.Exists(Path.Combine(dir, id + ".skel"));
            }
            catch (Exception ex) { Logger.Error($"[Skin] 检查 {id} 资源失败", ex); }
        }

        string name = dir != null ? DisplayNameFromDir(id, dir) : ConfigNameOr(id);

        return new SkinEntry
        {
            Id = id,
            DisplayName = string.IsNullOrWhiteSpace(name) ? id : name,
            Dir = dir,
            IsUsable = usable,
        };
    }

    /// <summary>
    /// 显示名三级兜底：配置名 → display.txt 第一行 → 目录名。
    /// 读不到或内容为空都不抛异常，一律往下退（显示名必须有值）。
    /// 配置名截断到 24 字，防止有人把整段说明写进 config.json 把卡片撑爆。
    /// </summary>
    private static string DisplayNameFromDir(string id, string dir)
    {
        string configured = ConfigNameOr(id);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        try
        {
            string txt = Path.Combine(dir, "display.txt");
            if (File.Exists(txt))
            {
                string name = File.ReadLines(txt).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
                if (name.Length > 0) return name.Length > 24 ? name[..24] : name;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Skin] 读取 {id}/display.txt 失败", ex);
        }

        return id;
    }

    private static string ConfigNameOr(string id)
    {
        if (_configuredNames.TryGetValue(id, out string? name) && !string.IsNullOrWhiteSpace(name))
            return name.Length > 24 ? name[..24] : name;
        return "";
    }
}
