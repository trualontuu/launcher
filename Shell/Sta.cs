using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Launcher.Search;

namespace Launcher.Shell;

/// <summary>
/// Один фоновый STA-поток для всего COM оболочки: список приложений и иконки.
/// Интерфейс не ждёт: иконка приходит колбэком, когда готова.
/// </summary>
public static class Sta
{
    static readonly BlockingCollection<Action> _q = new();
    static readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    static Dispatcher? _ui;
    public static int IconPx = 64;

    public static void Init(Dispatcher ui)
    {
        _ui = ui;
        var t = new Thread(() => { foreach (var a in _q.GetConsumingEnumerable()) { try { a(); } catch { } } })
        {
            IsBackground = true, Name = "ShellSta", Priority = ThreadPriority.BelowNormal,
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static void Run(Action a) => _q.Add(a);

    static readonly HashSet<string> PerFileExt = new(StringComparer.OrdinalIgnoreCase)
    { ".exe", ".lnk", ".url", ".ico", ".appref-ms", ".msc", ".scr", ".cpl", "" };
    static readonly HashSet<string> ThumbExt = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".heic", ".tif", ".tiff" };

    /// <summary>Ключ кэша и что спрашивать у оболочки. Файлы одного типа делят одну иконку.</summary>
    static (string key, string parsing, bool thumb) Describe(Result r)
    {
        switch (r.Kind)
        {
            case Kind.App when r.Target.Length > 2 && r.Target[1] == ':': return ("file:" + r.Target, r.Target, false);
            case Kind.App: return ("app:" + r.Target, @"shell:AppsFolder\" + r.Target, false);
            case Kind.Folder: return ("dir:" + r.Target, r.Target, false);
            case Kind.File:
            case Kind.Path:
                if (Directory.Exists(r.Target)) return ("dir:" + r.Target, r.Target, false);
                var ext = System.IO.Path.GetExtension(r.Target);
                if (ThumbExt.Contains(ext)) return ("thumb:" + r.Target, r.Target, true);
                if (PerFileExt.Contains(ext)) return ("file:" + r.Target, r.Target, false);
                return ("ext:" + ext, r.Target, false);
            default:
                return ("web", "", false);
        }
    }

    public static ImageSource? Cached(Result r) => _cache.TryGetValue(Describe(r).key, out var v) ? v : null;

    public static void Icon(Result r, Action<ImageSource?> done)
    {
        var (key, parsing, thumb) = Describe(r);
        if (_cache.TryGetValue(key, out var hit)) { done(hit); return; }
        if (parsing.Length == 0) return;
        _q.Add(() =>
        {
            if (!_cache.TryGetValue(key, out var img))
            {
                img = ShellApi.GetImage(parsing, IconPx, thumb);
                if (_cache.Count > 3000) _cache.Clear();
                _cache[key] = img;
            }
            _ui?.BeginInvoke(() => done(img), DispatcherPriority.Background);
        });
    }
}
