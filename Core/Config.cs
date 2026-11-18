using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

/// <summary>Настройки лаунчера: %APPDATA%\Launcher\settings.json (правится руками или из меню трея).</summary>
public sealed class Config
{
    /// <summary>Alt+Space занят полкой Island, поэтому по умолчанию Alt+Q.</summary>
    public string Hotkey { get; set; } = "Alt+Q";

    /// <summary>Где искать файлы. Пусто — папки пользователя и все несистемные диски целиком.</summary>
    public List<string> Roots { get; set; } = new();
    /// <summary>Добавить к папкам по умолчанию.</summary>
    public List<string> ExtraRoots { get; set; } = new();
    /// <summary>Свои имена папок, в которые индекс не заходит (вдобавок к встроенному списку BaseExclude).</summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>Встроенный список: сборки, кэши, игры (тысячи файлов ассетов только мешают). Папки на «.» и «$» пропускаются всегда.</summary>
    [JsonIgnore] public static readonly string[] BaseExclude =
    {
        "node_modules", "bin", "obj", "__pycache__", "venv", "env", "site-packages", "packages",
        "system volume information", "windowsapps", "appdata",
        "cache", "caches", "webcache", "shadercache", "gpucache", "code cache", "temp", "tmp", "logs",
        "steamapps", "userdata", "my games", "epic games", "riot games", "workshopcache", "streamingassets",
    };

    [JsonIgnore] public IEnumerable<string> AllExclude => BaseExclude.Concat(Exclude);

    /// <summary>Поиск в интернете, {0} — запрос.</summary>
    public string WebSearch { get; set; } = "https://www.google.com/search?q={0}";
    public int MaxResults { get; set; } = 30;

    /// <summary>Свои сокращения: запрос → часть названия приложения.</summary>
    public Dictionary<string, string> Aliases { get; set; } = new()
    {
        ["тг"] = "Telegram", ["tg"] = "Telegram",
        ["кс"] = "Counter-Strike 2", ["дс"] = "Discord", ["ds"] = "Discord",
        ["ям"] = "Яндекс Музыка", ["вскод"] = "Visual Studio Code",
    };

    [JsonIgnore] public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Launcher");
    [JsonIgnore] public static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Opts) ?? new Config();
        }
        catch { }
        var c = new Config();
        c.Save();
        return c;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { }
    }

    public List<string> EffectiveRoots()
    {
        var list = new List<string>();
        if (Roots.Count > 0) list.AddRange(Roots);
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            list.Add(Path.Combine(home, "Downloads"));
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            if (Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } od) list.Add(od);
            string sys = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType == DriveType.Fixed && d.IsReady && !string.Equals(d.RootDirectory.FullName, sys, StringComparison.OrdinalIgnoreCase))
                        list.Add(d.RootDirectory.FullName);
                }
                catch { }
            }
        }
        list.AddRange(ExtraRoots);

        // без дублей и без вложенных друг в друга корней
        var norm = list.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                       .Select(p => Path.GetFullPath(p).TrimEnd('\\') + "\\")
                       .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return norm.Where(p => !norm.Any(o => o.Length < p.Length && p.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
                   .Select(p => p.Length > 3 ? p.TrimEnd('\\') : p).ToList();
    }
}
