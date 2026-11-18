using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Launcher.Core;

namespace Launcher.Search;

/// <summary>
/// Что и по какому запросу открывали. Даёт прибавку к оценке: частое — выше, а то, что выбрали на «tel», в следующий раз будет первым на «tel» и «te».
/// </summary>
public sealed class Usage
{
    public sealed class Rec
    {
        public int Count { get; set; }
        public DateTime Last { get; set; }
        public string Title { get; set; } = "";
        public Kind Kind { get; set; }
        public string Target { get; set; } = "";
        public string? Path { get; set; }
    }

    public Dictionary<string, Rec> Items { get; set; } = new();
    /// <summary>нормализованный запрос → ключ того, что по нему выбрали последним.</summary>
    public Dictionary<string, string> Queries { get; set; } = new();

    static string FilePath => System.IO.Path.Combine(Config.Dir, "usage.json");
    readonly object _gate = new();
    Timer? _save;

    public static Usage Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Usage>(File.ReadAllText(FilePath)) ?? new Usage();
        }
        catch { }
        return new Usage();
    }

    public void Record(Result r, string normQuery)
    {
        if (r.Kind is Kind.Web or Kind.Music or Kind.Player) return;
        lock (_gate)
        {
            if (!Items.TryGetValue(r.Key, out var rec)) Items[r.Key] = rec = new Rec();
            rec.Count++;
            rec.Last = DateTime.UtcNow;
            rec.Title = r.Title; rec.Kind = r.Kind; rec.Target = r.Target; rec.Path = r.Path;
            if (normQuery.Length > 0) Queries[normQuery] = r.Key;
            if (Queries.Count > 600) foreach (var k in Queries.Keys.Take(100).ToList()) Queries.Remove(k);
            if (Items.Count > 1500)
                foreach (var k in Items.OrderBy(p => p.Value.Last).Take(300).Select(p => p.Key).ToList()) Items.Remove(k);
        }
        _save ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        _save.Change(1500, Timeout.Infinite);
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(this);
            Directory.CreateDirectory(Config.Dir);
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    /// <summary>Прибавки к оценке для текущего запроса: ключ → бонус.</summary>
    public Dictionary<string, int> Boosts(string q)
    {
        var map = new Dictionary<string, int>();
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            foreach (var (key, rec) in Items)
            {
                int b = (int)(40 * Math.Log2(1 + rec.Count));
                if ((now - rec.Last).TotalDays < 1) b += 20;
                map[key] = Math.Min(b, 160);
            }
            if (q.Length > 0)
            {
                if (Queries.TryGetValue(q, out var exact)) Add(map, exact, 250);
                foreach (var (qq, key) in Queries)
                    if (qq.Length > q.Length && qq.StartsWith(q, StringComparison.Ordinal)) Add(map, key, 120);
            }
        }
        return map;

        static void Add(Dictionary<string, int> m, string k, int v)
        {
            m.TryGetValue(k, out int cur);
            m[k] = Math.Max(cur, Math.Min(cur, 160) + v);
        }
    }

    /// <summary>Для пустого запроса: частое и недавнее.</summary>
    public List<Rec> Top(int n)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return Items.Values
                .OrderByDescending(r => r.Count / (1 + (now - r.Last).TotalDays / 3))
                .Take(n).ToList();
        }
    }

    public Rec? Get(string key) { lock (_gate) return Items.TryGetValue(key, out var r) ? r : null; }

    /// <summary>Файлы и папки из истории — чтобы часто открываемое находилось, даже если индекс его не дал в топ.</summary>
    public List<(string key, Rec rec)> FileRecs()
    {
        lock (_gate) return Items.Where(p => p.Value.Kind is Kind.File or Kind.Folder or Kind.Path).Select(p => (p.Key, p.Value)).ToList();
    }
}
