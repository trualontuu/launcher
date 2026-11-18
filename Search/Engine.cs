using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Launcher.Core;
using Launcher.Shell;

namespace Launcher.Search;

/// <summary>Сводит вместе приложения, файлы, историю и особые строки (путь, адрес сайта).</summary>
public sealed class Engine : IDisposable
{
    public readonly AppIndex Apps = new();
    public readonly FileIndex Files = new();
    public readonly Usage Usage = Usage.Load();
    readonly Config _cfg;
    int _appsLoading;

    public event Action? Changed;
    public bool AppsReady => Apps.LoadedUtc != default;

    public Engine(Config cfg)
    {
        _cfg = cfg;
        Files.Changed += () => Changed?.Invoke();
    }

    public void Start()
    {
        RefreshApps();
        var t = new Thread(() => Files.Build(_cfg.EffectiveRoots(), _cfg.AllExclude)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "FileIndexBuild" };
        t.Start();
    }

    /// <summary>Перечитать «Пуск» (установили/удалили программу) — в фоне, не чаще раза в несколько минут.</summary>
    public void RefreshApps(bool onlyIfStale = false)
    {
        if (onlyIfStale && (DateTime.UtcNow - Apps.LoadedUtc).TotalMinutes < 5) return;
        if (Interlocked.Exchange(ref _appsLoading, 1) == 1) return;
        Sta.Run(() =>
        {
            try { Apps.Load(); }
            finally { _appsLoading = 0; }
            Changed?.Invoke();
        });
    }

    static readonly Regex UrlRx = new(@"^(https?://)?([\w-]+\.)+(com|ru|org|net|io|dev|app|me|gg|tv|ai|su|by|kz|ua|info|xyz|co|uk|de|fm|to|cc|so|sh|ly|рф)(:\d+)?(/\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public List<Result> Search(string raw, CancellationToken ct)
    {
        var trimmed = raw.Trim();
        var q = Text.Norm(trimmed);
        if (q.Length == 0) return Recent();

        var variants = Text.Variants(q);
        var boosts = Usage.Boosts(q);
        var byKey = new Dictionary<string, Result>(StringComparer.OrdinalIgnoreCase);
        void Put(Result r)
        {
            if (boosts.TryGetValue(r.Key, out int b)) r.Score += b;
            if (!byKey.TryGetValue(r.Key, out var old) || old.Score < r.Score) byKey[r.Key] = r;
        }

        // приложения: нечёткий поиск, аббревиатуры, транслит названия
        var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // свои сокращения: «тг» → Telegram
        string? alias = null;
        foreach (var (k, v) in _cfg.Aliases)
            if (Text.Norm(k) == q) { alias = Text.Norm(v); break; }
        foreach (var a in Apps.Items)
        {
            appNames.Add(a.Name);
            int best = 0; int[]? bh = null;
            foreach (var (v, pen) in variants)
            {
                // нечёткий поиск только по запросу как есть: по раскладке/транслиту он даёт шум
                int s = Matcher.Score(v, a.Norm, a.Starts, a.Initials, a.StartIdx, pen == 0, out var h);
                if (s == 0 && a.Exe != null && v.Length >= 3)
                {
                    // «notepad» → «Блокнот» (Microsoft.WindowsNotepad), «code» → Code.exe
                    int ei = a.Exe.IndexOf(v, StringComparison.Ordinal);
                    if (ei == 0) s = 870; else if (ei > 0 && Text.IsWordStartAt(a.ExeRaw!, ei)) s = 810;
                    if (s > 0) s -= 30;
                }
                if (s == 0 && a.Alt != null)
                {
                    // «yandex» → «Яндекс Музыка» (yandeks muzyka): совпадения по транслиту без подсветки
                    int ai = a.Alt.IndexOf(v, StringComparison.Ordinal);
                    if (ai == 0) s = 860; else if (ai > 0 && Text.IsWordStartAt(a.Alt, ai)) s = 780;
                    h = null;
                    if (s > 0) s -= 20;
                }
                if (s > 0) s -= pen;
                if (s > best) { best = s; bh = h; }
            }
            if (alias != null && a.Norm.Contains(alias)) { best = 1050; bh = null; }
            if (best <= 0) continue;
            Put(new Result
            {
                Kind = Kind.App, Title = a.Name, Key = a.Key, Target = a.Id, Path = a.Path,
                Subtitle = a.Subtitle,
                Score = best + (a.Loose ? 60 : 100), Hits = bh,
            });
        }
        ct.ThrowIfCancellationRequested();

        // файлы из индекса
        if (Files.Ready)
        {
            // другая раскладка/транслит для 1–2 букв по файлам — почти всегда шум («ям» → «zv…»)
            var fileVariants = variants.Where(v => v.penalty == 0 || v.q.Length >= 3).ToList();
            foreach (var h in Files.Search(fileVariants, 40, ct))
            {
                // ярлык на рабочем столе, который дублирует приложение из «Пуска»
                if (!h.Dir && h.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && appNames.Contains(h.Name[..^4])) continue;
                Put(FileResult(h.Name, h.Path, h.Dir, h.Score, h.Hits));
            }
        }

        // файлы из истории (вдруг не попали в топ индекса или лежат вне индексируемых папок)
        foreach (var (key, rec) in Usage.FileRecs())
        {
            var name = Path.GetFileName(rec.Target.TrimEnd('\\'));
            if (name.Length == 0) name = rec.Target;
            int best = 0; int[]? bh = null;
            foreach (var (v, pen) in variants)
            {
                int s = FileIndex.ScoreName(v, name, Text.Norm(name), rec.Kind == Kind.Folder, out var hh);
                if (s > 0) s -= pen;
                if (s > best) { best = s; bh = hh; }
            }
            if (best <= 0 || byKey.ContainsKey(key)) continue;
            if (!File.Exists(rec.Target) && !Directory.Exists(rec.Target)) continue;
            Put(FileResult(name, rec.Target, rec.Kind == Kind.Folder, best, bh));
        }

        // команды плеера: «пауза», «дальше», «волна кс»…
        foreach (var r in PlayerCommands(variants)) Put(r);

        // путь: C:\…, \\server\…, %APPDATA%, ~\…
        if (LooksLikePath(trimmed, out var full))
            Put(new Result { Kind = Kind.Path, Title = full, Key = "file:" + full, Target = full, Subtitle = Directory.Exists(full) ? "Открыть папку" : "Открыть файл", Score = 2000 });

        // адрес сайта
        if (!trimmed.Contains(' ') && UrlRx.IsMatch(trimmed))
        {
            bool explicitUrl = trimmed.Contains("://");
            Put(new Result { Kind = Kind.Url, Title = trimmed, Key = "url:" + trimmed.ToLowerInvariant(), Target = trimmed, Subtitle = "Открыть в браузере", Score = explicitUrl ? 2000 : 700 });
        }

        var list = byKey.Values
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Kind == Kind.App ? 0 : 1)
            .ThenBy(r => r.Title.Length)
            .Take(_cfg.MaxResults).ToList();

        list.Add(new Result { Kind = Kind.Web, Title = $"Найти в интернете: «{trimmed}»", Key = "web", Target = trimmed, Subtitle = Host(_cfg.WebSearch), Score = -1 });
        return list;
    }

    static readonly (string cmd, string title, string glyph, string[] words)[] Commands =
    {
        ("toggle", "", "\uE768", new[] { "пауза", "играть", "плей", "стоп", "музыка", "pause", "play", "stop", "music" }),
        ("next", "Следующий трек", "\uE893", new[] { "дальше", "следующий", "скип", "next", "skip" }),
        ("prev", "Предыдущий трек", "\uE892", new[] { "предыдущий", "назад трек", "prev", "previous" }),
        ("wave", "Моя волна", "\uE8D6", new[] { "волна", "моя волна", "wave", "my wave" }),
        ("wave_cs2", "Моя волна · CS2", "\uE7FC", new[] { "волна кс", "волна cs", "кс2", "кс 2", "cs2", "cs 2", "wave cs" }),
        ("like", "Мне нравится", "\uEB51", new[] { "лайк", "нравится", "like" }),
    };

    /// <summary>Команды плееру. Только с 2+ букв и только по началу слова — иначе «п» всё время предлагала бы паузу.</summary>
    static IEnumerable<Result> PlayerCommands(List<(string q, int penalty)> variants)
    {
        if (!PlayerLink.Installed) yield break;
        var now = PlayerLink.Now;
        string track = now is { Title.Length: > 0 } ? $"{now.Title} — {now.Artist}" : "";
        foreach (var (cmd, title0, glyph, words) in Commands)
        {
            int best = 0;
            foreach (var (v, pen) in variants)
            {
                if (v.Length < 2) continue;
                foreach (var w in words)
                {
                    int s = w == v ? 1000 : w.StartsWith(v, StringComparison.Ordinal) ? 900 + Math.Min(v.Length * 10, 80) : 0;
                    if (s > 0) best = Math.Max(best, s - pen);
                }
            }
            if (best <= 0) continue;
            var title = cmd == "toggle" ? (now?.Playing == true ? "Пауза" : "Играть") : title0;
            var g = cmd == "toggle" && now?.Playing == true ? "\uE769" : glyph;
            var sub = cmd switch
            {
                "toggle" or "like" or "next" or "prev" when track.Length > 0 => track,
                "wave" => "Бесконечный поток под ваш вкус",
                "wave_cs2" => "Хайп-трэп под игру, без шумного рейджа",
                _ => "Плеер",
            };
            yield return new Result
            {
                Kind = Kind.Player, Title = title, Subtitle = sub, Key = "player:" + cmd, Target = cmd,
                Glyph = g, Label = "Плеер", Score = best, Command = new { cmd },
            };
        }
    }

    List<Result> Recent()
    {
        var list = new List<Result>();
        var apps = Apps.Items.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var rec in Usage.Top(12))
        {
            if (list.Count >= 8) break;
            if (rec.Kind == Kind.App)
            {
                if (!apps.TryGetValue("app:" + rec.Target, out var a)) continue; // удалили
                list.Add(new Result { Kind = Kind.App, Title = a.Name, Key = a.Key, Target = a.Id, Path = a.Path, Subtitle = a.Subtitle });
            }
            else if (rec.Kind is Kind.File or Kind.Folder or Kind.Path)
            {
                if (!File.Exists(rec.Target) && !Directory.Exists(rec.Target)) continue;
                var r = FileResult(Path.GetFileName(rec.Target.TrimEnd('\\')), rec.Target, Directory.Exists(rec.Target), 0, null);
                list.Add(r);
            }
            else if (rec.Kind == Kind.Url)
                list.Add(new Result { Kind = Kind.Url, Title = rec.Target, Key = "url:" + rec.Target.ToLowerInvariant(), Target = rec.Target, Subtitle = "Открыть в браузере" });
        }
        return list;
    }

    static Result FileResult(string name, string path, bool dir, int score, int[]? hits)
    {
        string title = name;
        if (!dir && (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
        {
            title = name[..^4];
            if (hits != null) hits = hits.Where(i => i < title.Length).ToArray();
        }
        return new Result
        {
            Kind = dir ? Kind.Folder : Kind.File, Title = title, Key = "file:" + path, Target = path,
            Subtitle = Short(Path.GetDirectoryName(path) ?? ""), Score = score, Hits = hits,
        };
    }

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>C:\Users\имя\Desktop\demo → ~\Desktop\demo</summary>
    public static string Short(string p) =>
        p.StartsWith(Home, StringComparison.OrdinalIgnoreCase) ? "~" + p[Home.Length..] : p;

    static bool LooksLikePath(string s, out string full)
    {
        full = "";
        if (s.Length < 2) return false;
        string p = s;
        if (p.StartsWith("~")) p = Home + p[1..];
        p = Environment.ExpandEnvironmentVariables(p).Trim('"');
        bool pathy = (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':') || p.StartsWith(@"\\");
        if (!pathy) return false;
        if (p.Length == 2) p += "\\";
        try
        {
            if (File.Exists(p) || Directory.Exists(p)) { full = p; return true; }
        }
        catch { }
        return false;
    }

    static string Host(string url)
    {
        try { return new Uri(url.Replace("{0}", "x")).Host.Replace("www.", ""); } catch { return ""; }
    }

    public void Dispose()
    {
        Files.Dispose();
        Usage.Save();
    }
}
