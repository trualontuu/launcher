using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Island.Core;
using Launcher.Core;
using Launcher.Search;
using Launcher.Shell;
using Launcher.UI;
using Microsoft.Win32;

namespace Launcher;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _show, _quit;
    SystemHost? _host;
    Engine? _engine;
    LauncherWindow? _win;
    Config _cfg = new();

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int snapAt = Array.IndexOf(e.Args, "--snap");
        if (snapAt >= 0 && snapAt + 1 < e.Args.Length)
        {
            Snap.Run(this, e.Args[snapAt + 1], e.Args[(snapAt + 2)..]);
            return;
        }

        // Повторный запуск exe открывает уже работающий лаунчер; «--quit» закрывает его.
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, "Launcher.Spsp.Show");
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, "Launcher.Spsp.Quit");
        _single = new Mutex(true, "Launcher.Spsp.Singleton", out bool first);
        bool quitArg = Array.IndexOf(e.Args, "--quit") >= 0;
        if (!first || quitArg)
        {
            if (!first) (quitArg ? _quit : _show).Set();
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => Dispatcher.BeginInvoke(() => _win?.ShowLauncher()), null, -1, false);
        ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => Dispatcher.BeginInvoke(Quit), null, -1, true);

        _cfg = Config.Load();
        ThemeSync.Apply();
        ThemeSync.Watch(Dispatcher);
        Sta.Init(Dispatcher);

        _engine = new Engine(_cfg);
        _engine.Start();
        _win = new LauncherWindow(_engine, _cfg);

        _host = new SystemHost();
        if (!_host.RegisterHotkey(_cfg.Hotkey, () => _win.Toggle()))
            _win.HotkeyError = $"Сочетание {_cfg.Hotkey} занято другой программой — поменяйте в настройках";
        _host.TrayLeftClick += () => _win.ShowLauncher();
        _host.TrayRightClick += ShowMenu;
        _host.ShowTray($"Launcher — {_cfg.Hotkey}");

        DispatcherUnhandledException += (_, ex) => { ex.Handled = true; Debug.WriteLine(ex.Exception); };

        if (Array.IndexOf(e.Args, "--background") < 0) _win.ShowLauncher();
    }

    void ShowMenu() => TrayMenu.Toggle(MenuActions());

    public TrayActions MenuActions() => new()
    {
        Open = () => _win?.ShowLauncher(),
        Reindex = Restart,
        OpenSettings = () =>
        {
            _cfg.Save();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Config.FilePath}\"") { UseShellExecute = true })?.Dispose();
        },
        OpenDataFolder = () => Process.Start(new ProcessStartInfo(Config.Dir) { UseShellExecute = true })?.Dispose(),
        Quit = Quit,
        GetAutostart = AutostartOn,
        SetAutostart = SetAutostart,
        GetHotkey = () => _cfg.Hotkey,
        SetHotkey = ChangeHotkey,
        Status = () => _engine == null ? "" : $"{Plural(_engine.Apps.Items.Length, "приложение", "приложения", "приложений")} · " +
            (_engine.Files.Ready ? Plural(_engine.Files.Count, "файл", "файла", "файлов") : "индексация…"),
        Version = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? ""),
    };

    static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        string w = m10 == 1 && m100 != 11 ? one : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        return n.ToString("N0").Replace(' ', ' ') + " " + w;
    }

    /// <summary>Сменить горячую клавишу на лету; если занята — вернуть прежнюю.</summary>
    bool ChangeHotkey(string combo)
    {
        if (_host == null || _win == null) return false;
        _host.ClearHotkeys();
        if (_host.RegisterHotkey(combo, () => _win.Toggle()))
        {
            _cfg.Hotkey = combo;
            _cfg.Save();
            _win.HotkeyError = null;
            return true;
        }
        _host.RegisterHotkey(_cfg.Hotkey, () => _win.Toggle());
        return false;
    }

    static bool AutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("Launcher") is string;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!on) k.DeleteValue("Launcher", false);
        else k.SetValue("Launcher", $"\"{Environment.ProcessPath}\" --background");
    }

    /// <summary>Настройки меняются в json — проще всего перезапуститься.</summary>
    void Restart()
    {
        var exe = Environment.ProcessPath;
        Cleanup();
        if (exe != null) Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false })?.Dispose();
        Shutdown();
    }

    void Quit()
    {
        Cleanup();
        Shutdown();
    }

    void Cleanup()
    {
        _engine?.Dispose(); _engine = null;
        _host?.Dispose(); _host = null;
        _single?.ReleaseMutex();
        _single?.Dispose(); _single = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _engine?.Dispose(); _host?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
