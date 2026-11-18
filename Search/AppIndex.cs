using System;
using System.Collections.Generic;
using System.Text;
using Launcher.Shell;

namespace Launcher.Search;

public sealed class AppItem
{
    public string Name = "", Id = "", Norm = "", Initials = "";
    public string? Path, Alt;
    /// <summary>Имя exe или пакета: «code», «windowsnotepad» — ищем и по нему.</summary>
    public string? Exe, ExeRaw;
    public string Subtitle = "";
    public bool[] Starts = Array.Empty<bool>();
    public int[] StartIdx = Array.Empty<int>();
    public string Key => "app:" + Id;
    public bool IsStore => Id.Contains('!');
    /// <summary>Программа без ярлыка в «Пуске», найденная по exe (портативные: vibranceGUI и т.п.). Id = путь к exe.</summary>
    public bool Loose;
}

/// <summary>Приложения из «Пуска» (обычные и из Store). Список живёт в памяти, обновляется при открытии лаунчера.</summary>
public sealed class AppIndex
{
    public AppItem[] Items { get; private set; } = Array.Empty<AppItem>();
    public DateTime LoadedUtc { get; private set; }
    public double LoadMs { get; private set; }

    /// <summary>Только в STA-потоке (Sta.Run).</summary>
    public void Load()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, id, path) in ShellApi.EnumApps())
        {
            if (IsNoise(name, path)) continue;
            if (!seen.Add(name + "|" + (path ?? id))) continue;
            if (path != null) paths.Add(path);
            list.Add(Make(name, id, path));
        }
        try { ScanExes(list, paths); } catch { }
        Items = list.ToArray();
        LoadedUtc = DateTime.UtcNow;
        LoadMs = sw.Elapsed.TotalMilliseconds;
    }

    // ---------- программы без ярлыков ----------
    static readonly string[] ExeNoise =
    {
        "unins", "uninst", "setup", "install", "update", "crash", "helper", "report", "elevat", "notif", "squirrel",
        "vc_redist", "vcredist", "redist", "dotnet", "webview", "createdump", "7z.sfx", "repair", "diagnos", "telemetry",
        "service", "svc", "host", "agent", "broker", "daemon", "worker", "proxy", "renderer", "gpu", "subprocess",
        "wow_helper", "cefsharp", "dxsetup", "dxwebsetup", "ffprobe", "ffmpeg", "python", "pythonw", "node", "java", "javaw",
        "git-", "sh.exe", "bash", "perl", "tclsh", "wish", "conhost", "msiexec", "regsvr", "rundll",
    };

    static readonly HashSet<string> ExeSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "windowsapps", "packages", "temp", "tmp", "cache", "crashpad", "crashreporter", "logs",
        "common files", "windows defender", "windows nt", "windowspowershell", "reference assemblies", "dotnet",
        "windows kits", "microsoft sdks", "msbuild", "internet explorer", "windows mail", "windows media player",
        "windows photo viewer", "windows sidebar", "modifiablewindowsapps", "node_modules", "locales", "resources",
        "swiftshader", "_internal", "lib", "libs", "plugins", "bin", "tools", "redist", "python", "nvidia", "nvidia corporation",
        "d3dscache", "squirrel", "squirreltemp", "virtualstore", "connecteddevicesplatform", "comms", "publishers",
        "programs", "google", "mozilla", "jetbrains", "steam", "steamapps", "epic games",
    };

    /// <summary>
    /// exe в Program Files и AppData неглубоко (2–3 уровня): портативные программы, у которых нет ярлыка в «Пуске».
    /// Только «главные» exe: без установщиков, апдейтеров, служб и т.п.
    /// </summary>
    static void ScanExes(List<AppItem> list, HashSet<string> known)
    {
        var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new (string dir, int depth)[]
        {
            (la, 2),
            (System.IO.Path.Combine(la, "Programs"), 2),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 2),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), 3),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), 3),
        };
        var byName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in list) byName.Add(a.Name);

        // папки программ, у которых уже есть ярлык в «Пуске», целиком пропускаем: их служебные exe только мешают
        var vendorDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, _) in roots)
            foreach (var p in known)
            {
                var r = root.TrimEnd('\\') + "\\";
                if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
                int slash = p.IndexOf('\\', r.Length);
                if (slash > 0) vendorDirs.Add(p[..slash]);
            }
        var opts = new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System | System.IO.FileAttributes.ReparsePoint };

        foreach (var (root, maxDepth) in roots)
        {
            if (!System.IO.Directory.Exists(root)) continue;
            var stack = new Stack<(string dir, int depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                try
                {
                    if (depth > 0)
                    {
                        foreach (var exe in System.IO.Directory.EnumerateFiles(dir, "*.exe", opts))
                        {
                            var stem = System.IO.Path.GetFileNameWithoutExtension(exe);
                            var lower = stem.ToLowerInvariant();
                            if (known.Contains(exe) || Array.Exists(ExeNoise, n => lower.Contains(n))) continue;
                            if (byName.Contains(stem) || LooksLikeHash(stem)) continue;
                            if (!IsGuiExe(exe)) continue; // консольные утилиты (pg_dump, git, xray) — не программы для запуска
                            known.Add(exe);
                            byName.Add(stem);
                            var item = Make(stem, exe, exe);
                            item.Loose = true;
                            item.Subtitle = Engine.Short(System.IO.Path.GetDirectoryName(exe) ?? "");
                            list.Add(item);
                        }
                    }
                    if (depth < maxDepth)
                        foreach (var sub in System.IO.Directory.EnumerateDirectories(dir, "*", opts))
                        {
                            var n = System.IO.Path.GetFileName(sub);
                            if (n.Length == 0 || n[0] == '.' || n[0] == '$' || ExeSkipDirs.Contains(n)) continue;
                            if (n.Contains("update", StringComparison.OrdinalIgnoreCase) || n.StartsWith("windows ", StringComparison.OrdinalIgnoreCase)) continue;
                            if (depth == 0 && vendorDirs.Contains(sub)) continue;
                            if (n.Length > 0 && char.IsDigit(n[0]) && n.Contains('.')) continue; // папки версий: 1.2.3
                            stack.Push((sub, depth + 1));
                        }
                }
                catch { }
            }
        }
    }

    static bool LooksLikeHash(string s)
    {
        if (s.Length < 16) return false;
        foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>Подсистема из PE-заголовка: 2 — оконная программа, 3 — консольная.</summary>
    static bool IsGuiExe(string path)
    {
        try
        {
            using var f = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, 1, false);
            var buf = new byte[512];
            int n = f.Read(buf, 0, buf.Length);
            if (n < 0x40 || buf[0] != 'M' || buf[1] != 'Z') return false;
            int pe = BitConverter.ToInt32(buf, 0x3C);
            if (pe < 0 || pe + 0x5E > n) { f.Position = pe; buf = new byte[0x60]; if (f.Read(buf, 0, 0x60) < 0x5E) return false; pe = 0; }
            if (buf[pe] != 'P' || buf[pe + 1] != 'E') return false;
            ushort subsystem = BitConverter.ToUInt16(buf, pe + 0x5C); // одинаково для PE32 и PE32+
            return subsystem == 2;
        }
        catch { return false; }
    }

    static bool IsNoise(string name, string? path)
    {
        var l = name.ToLowerInvariant();
        if (l.Contains("uninstall") || l.StartsWith("удал") || l.Contains("деинстал") || l.Contains("readme")) return true;
        if (path != null && path.EndsWith("unins000.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Как ещё называют системные приложения: часть ID/пути → слова для поиска.</summary>
    static readonly (string part, string words)[] Keywords =
    {
        ("immersivecontrolpanel", "settings параметры настройки"),
        ("Microsoft.Windows.Explorer", "explorer файлы мой компьютер"),
        ("taskmgr", "task manager диспетчер"),
        ("WindowsCalculator", "calc калькулятор"),
        ("WindowsTerminal", "terminal cmd powershell консоль"),
        ("ScreenSketch", "screenshot snipping скриншот ножницы"),
        ("Microsoft.Windows.ControlPanel", "control panel"),
        ("RunDialog", "run выполнить"),
    };

    public static AppItem Make(string name, string id, string? path)
    {
        var starts = Text.WordStarts(name);
        var idx = new List<int>();
        for (int i = 0; i < starts.Length; i++) if (starts[i]) idx.Add(i);
        var norm = Text.Norm(name);
        var ini = new StringBuilder(idx.Count);
        foreach (int i in idx) ini.Append(norm[i]);
        string? alt = Text.HasCyrillic(norm) ? Text.Translit(norm) : null;
        string? exe = null;
        if (path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = System.IO.Path.GetFileNameWithoutExtension(path);
        else if (id.Contains('!'))
        {
            // Microsoft.WindowsNotepad_8wekyb3d8bbwe!App → WindowsNotepad
            var pkg = id[..id.IndexOf('!')];
            int us = pkg.IndexOf('_'); if (us > 0) pkg = pkg[..us];
            int dot = pkg.IndexOf('.'); if (dot >= 0 && dot < pkg.Length - 1) pkg = pkg[(dot + 1)..];
            exe = pkg;
        }
        else if (path == null && id.IndexOfAny(new[] { '\\', ':', '/' }) < 0 && id.Contains('.'))
        {
            // Microsoft.Windows.Explorer → Explorer
            exe = id[(id.LastIndexOf('.') + 1)..];
        }
        string? exeNorm = exe != null ? Text.Norm(exe) : null;
        if (exeNorm != null && (norm.Contains(exeNorm) || exeNorm.Length < 3)) exeNorm = null;
        foreach (var (part, words) in Keywords)
        {
            if (id.Contains(part, StringComparison.OrdinalIgnoreCase) || (path?.Contains(part, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                exeNorm = (exeNorm == null ? "" : exeNorm + " ") + words;
                exe = exeNorm;
            }
        }

        string sub = path != null ? Engine.Short(System.IO.Path.GetDirectoryName(path) ?? "")
                   : id.Contains('!') ? "Microsoft Store"
                   : id.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ? "Steam"
                   : id.Contains("://") ? id[..id.IndexOf("://")] : "";
        return new AppItem
        {
            Name = name, Id = id, Path = path, Norm = norm, Starts = starts, StartIdx = idx.ToArray(), Initials = ini.ToString(), Alt = alt,
            Exe = exeNorm, ExeRaw = exeNorm != null ? exe : null, Subtitle = sub,
        };
    }
}
