using System;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Island.Core;
using Island.UI;

namespace Launcher.Core;

/// <summary>Тема, прозрачность и характер анимаций — те же, что выбраны в Island. Файл Island только читаем.</summary>
public static class ThemeSync
{
    static FileSystemWatcher? _w;
    static DispatcherTimer? _debounce;

    public static string? Override;

    public static void Apply()
    {
        Settings s;
        try
        {
            var path = Path.Combine(Settings.Dir, "settings.json");
            s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings() : new Settings();
        }
        catch { s = new Settings(); }
        if (Override != null) s.Theme = Override;
        Theme.Apply(s);
    }

    public static void Watch(Dispatcher d)
    {
        try
        {
            if (!Directory.Exists(Settings.Dir)) return;
            _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); Apply(); }, d);
            _debounce.Stop();
            _w = new FileSystemWatcher(Settings.Dir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            _w.Changed += (_, _) => d.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            _w.EnableRaisingEvents = true;
        }
        catch { }
    }
}
