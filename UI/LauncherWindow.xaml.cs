using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Island.Core;
using Island.UI;
using Launcher.Core;
using Launcher.Search;
using Launcher.Shell;

namespace Launcher.UI;

public partial class LauncherWindow : Window
{
    readonly Engine _engine;
    readonly Config _cfg;
    List<Result> _results = new();
    readonly List<Border> _rows = new();
    int _sel = -1, _gen;
    CancellationTokenSource? _cts;
    Point _lastMouse = new(double.NaN, double.NaN);
    bool _hiding;
    string? _flash;
    DateTime _flashUntil;

    /// <summary>Режим снимков: окно за экраном и не прячется при потере фокуса.</summary>
    public bool SnapMode;
    public double LastSearchMs;
    public TaskCompletionSource? SearchDone;
    public IReadOnlyList<Result> Results => _results;
    public string? HotkeyError;

    public LauncherWindow(Engine engine, Config cfg)
    {
        _engine = engine;
        _cfg = cfg;
        InitializeComponent();
        Query.TextChanged += (_, _) => { Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RunSearch(); };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (!SnapMode) HideLauncher(); };
        _engine.Changed += () => Dispatcher.BeginInvoke(UpdateStatus);
        Hints.Text = "↵ открыть   Ctrl+↵ в папке   Ctrl+C путь";
        SetEmptyLook();
        UpdateStatus();
    }

    // ---------- показ / скрытие ----------
    public void Toggle()
    {
        if (IsVisible && IsActive) HideLauncher();
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        _hiding = false;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible) PlaceOnCursorMonitor(hwnd);
        Show();
        Activate();
        Native.SetForegroundWindow(hwnd);
        Query.Focus();
        Keyboard.Focus(Query);
        Query.SelectAll();
        _engine.RefreshApps(onlyIfStale: true);
        _ = PlayerLink.RefreshState();
        RunSearch();

        var ms = Theme.Ms(260);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Theme.Ms(110)));
        var ease = Theme.Spring(0.75, 7.5);
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.965, 1, ms) { EasingFunction = ease });
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.965, 1, ms) { EasingFunction = ease });
    }

    public void HideLauncher()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        var a = new DoubleAnimation(Opacity, 0, Theme.Ms(80));
        a.Completed += (_, _) =>
        {
            if (!_hiding) return;
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _hiding = false;
            Native.TrimMemory();
        };
        BeginAnimation(OpacityProperty, a);
    }

    /// <summary>По центру монитора, где курсор, на пятой части высоты.</summary>
    void PlaceOnCursorMonitor(IntPtr hwnd)
    {
        Native.GetCursorPos(out var pt);
        var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var (_, work, scale, _) = Native.MonitorOf(mon);
        int w = (int)Math.Round(Width * scale);
        int x = work.Left + (work.W - w) / 2;
        int y = work.Top + (int)(work.H * 0.2) - (int)(12 * scale);
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public void ShowOffscreen()
    {
        ShowActivated = false;
        Left = -20000; Top = -20000;
        Show();
    }

    // ---------- поиск ----------
    public void SetQuery(string q) { Query.Text = q; Query.CaretIndex = q.Length; }

    async void RunSearch()
    {
        var q = Query.Text;
        int gen = ++_gen;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        List<Result> res;
        try { res = await Task.Run(() => _engine.Search(q, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { res = new(); Flash("Ошибка поиска: " + e.Message); }
        if (gen != _gen) return;
        LastSearchMs = sw.Elapsed.TotalMilliseconds;
        _results = res;
        Render();
        SearchDone?.TrySetResult();
        _ = SearchMusic(q, gen, cts.Token);
    }

    public TaskCompletionSource? MusicDone;

    /// <summary>Музыка из Яндекса через запущенный плеер: через 250 мс тишины в поле, секцией перед «Найти в интернете».</summary>
    async Task SearchMusic(string raw, int gen, CancellationToken ct)
    {
        var q = raw.Trim();
        try
        {
            if (q.Length < 3 || q.Contains('\\') || q.Contains(':') || !PlayerLink.Installed) return;
            await Task.Delay(250, ct);
            var items = await PlayerLink.Search(q, ct);
            if (gen != _gen || items.Count == 0) return;
            var music = new List<Result>();
            foreach (var it in items)
            {
                bool artist = it.Type == "artist";
                music.Add(new Result
                {
                    Kind = Kind.Music, Title = it.Title, Subtitle = it.Sub, Key = "music:" + it.Type + ":" + it.Id, Target = it.Id,
                    Cover = it.Cover, Label = artist ? "Артист" : "Трек",
                    Command = artist ? new { cmd = "artist", id = it.Id } : new { cmd = "play", id = it.Id, q },
                });
            }
            int keep = _sel;
            int at = _results.FindIndex(r => r.Kind == Kind.Web);
            if (at < 0) at = _results.Count;
            _results.InsertRange(at, music);
            Render();
            Select(Math.Max(keep, 0));
        }
        catch (OperationCanceledException) { }
        catch { }
        finally { if (gen == _gen) MusicDone?.TrySetResult(); }
    }

    void SetEmptyLook()
    {
        bool any = _results.Count > 0;
        Divider.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Scroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    void Render()
    {
        List.Children.Clear();
        _rows.Clear();
        if (Query.Text.Trim().Length == 0 && _results.Count > 0)
            List.Children.Add(new TextBlock { Text = "НЕДАВНИЕ", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 6, 0, 4), Foreground = (Brush)FindResource("Dim") });
        for (int i = 0; i < _results.Count; i++)
        {
            if (_results[i].Kind == Kind.Music && (i == 0 || _results[i - 1].Kind != Kind.Music))
                List.Children.Add(new TextBlock { Text = "МУЗЫКА · ЯНДЕКС", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 10, 0, 4), Foreground = (Brush)FindResource("Dim") });
            var row = MakeRow(_results[i], i);
            _rows.Add(row);
            List.Children.Add(row);
        }
        Scroll.ScrollToTop();
        SetEmptyLook();
        _sel = -1;
        Select(_results.Count > 0 ? 0 : -1);
        UpdateStatus();
    }

    Border MakeRow(Result r, int i)
    {
        var img = new Image { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        FrameworkElement icon = img;
        if (r.Kind == Kind.Music)
        {
            bool round = r.Label == "Артист";
            var cover = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.HighQuality);
            if (!string.IsNullOrEmpty(r.Cover))
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(r.Cover);
                    bi.DecodePixelWidth = 72;
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    cover.Source = bi;
                }
                catch { }
            icon = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(round ? 16 : 6), Child = cover,
                Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center,
                Clip = round ? new EllipseGeometry(new Point(16, 16), 16, 16) : new RectangleGeometry(new Rect(0, 0, 32, 32), 6, 6),
            };
        }
        else if (r.Kind == Kind.Player)
        {
            icon = new TextBlock
            {
                Text = r.Glyph ?? "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 19, Foreground = (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else if (r.Kind is Kind.Web or Kind.Url)
        {
            icon = new TextBlock
            {
                Text = r.Kind == Kind.Web ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 20, Foreground = (Brush)FindResource("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            img.Source = Sta.Cached(r);
            if (img.Source == null) Sta.Icon(r, s => { if (s != null) img.Source = s; });
        }

        var title = new TextBlock { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
        AddHighlighted(title.Inlines, r.Title, r.Hits);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        texts.Children.Add(title);
        if (r.Subtitle.Length > 0)
            texts.Children.Add(new TextBlock { Text = r.Subtitle, FontSize = 12, Foreground = (Brush)FindResource("Dim"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = r.KindName, FontSize = 12, Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center });
        if (i < 9)
        {
            var key = new Border
            {
                Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(6, 1, 6, 2), CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)),
                Child = new TextBlock { Text = "Alt+" + (i + 1), FontSize = 11, Foreground = (Brush)FindResource("Dim") },
            };
            right.Children.Add(key);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        grid.Children.Add(right);

        var row = new Border { Height = 52, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 12, 0), Background = Brushes.Transparent, Child = grid, Tag = i };
        row.MouseMove += (_, e) =>
        {
            // список перерисовался под неподвижной мышью — не перехватываем выделение
            var p = e.GetPosition(this);
            if (p == _lastMouse) return;
            _lastMouse = p;
            Select((int)row.Tag);
        };
        row.MouseLeftButtonUp += (_, _) => { Select((int)row.Tag); Activate(Mode.Open); };
        return row;
    }

    void AddHighlighted(InlineCollection inl, string text, int[]? hits)
    {
        if (hits == null || hits.Length == 0) { inl.Add(new Run(text)); return; }
        var set = new HashSet<int>(hits);
        var accent = (Brush)FindResource("Accent");
        int i = 0;
        while (i < text.Length)
        {
            bool on = set.Contains(i);
            int j = i;
            while (j < text.Length && set.Contains(j) == on) j++;
            var run = new Run(text[i..j]);
            if (on) { run.Foreground = accent; run.FontWeight = FontWeights.SemiBold; }
            inl.Add(run);
            i = j;
        }
    }

    void Select(int i)
    {
        if (i == _sel) return;
        if (_sel >= 0 && _sel < _rows.Count) _rows[_sel].Background = Brushes.Transparent;
        _sel = i;
        if (_sel < 0 || _sel >= _rows.Count) return;
        var row = _rows[_sel];
        row.SetResourceReference(Border.BackgroundProperty, "CardHover");
        row.BringIntoView();
    }

    // ---------- клавиатура ----------
    enum Mode { Open, Reveal, Admin }

    void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(6); e.Handled = true; break;
            case Key.PageUp: Move(-6); e.Handled = true; break;
            case Key.Tab: Move((mods & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; break;
            case Key.Enter:
                Activate((mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Shift) != 0 ? Mode.Admin
                       : (mods & ModifierKeys.Control) != 0 ? Mode.Reveal : Mode.Open);
                e.Handled = true; break;
            case Key.Escape:
                if (Query.Text.Length > 0) Query.Clear(); else HideLauncher();
                e.Handled = true; break;
            case Key.C when mods == ModifierKeys.Control && Query.SelectionLength == 0:
                CopyPath(); e.Handled = true; break;
            case >= Key.D1 and <= Key.D9 when mods == ModifierKeys.Alt:
                int n = key - Key.D1;
                if (n < _results.Count) { Select(n); Activate(Mode.Open); }
                e.Handled = true; break;
        }
    }

    void Move(int d)
    {
        if (_results.Count == 0) return;
        int n = _sel + d;
        if (Math.Abs(d) == 1) n = (n + _results.Count) % _results.Count;
        else n = Math.Clamp(n, 0, _results.Count - 1);
        Select(n);
    }

    void Activate(Mode mode)
    {
        if (_sel < 0 || _sel >= _results.Count) return;
        var r = _results[_sel];
        var q = Text.Norm(Query.Text.Trim());
        if (SnapMode) return;
        if (r.Kind is Kind.Music or Kind.Player && r.Command != null)
        {
            HideLauncher();
            var cmd = r.Command;
            _ = Task.Run(async () =>
            {
                var e = await PlayerLink.Command(cmd);
                if (e != null) Dispatcher.Invoke(() => { ShowLauncher(); Flash(e); });
            });
            return;
        }
        string? err = mode switch
        {
            Mode.Reveal => Launch.Reveal(r),
            Mode.Admin => Launch.RunAsAdmin(r),
            _ => Launch.Open(r, _cfg.WebSearch, Query.Text.Trim()),
        };
        if (err != null) { Flash(err); return; }
        _engine.Usage.Record(r, q);
        HideLauncher();
    }

    void CopyPath()
    {
        if (_sel < 0 || _sel >= _results.Count) return;
        var r = _results[_sel];
        var p = r.FsPath ?? (r.Kind == Kind.Url ? r.Target : null);
        if (p == null) { Flash("У этого пункта нет пути"); return; }
        try { Clipboard.SetText(p); Flash("Скопировано: " + Engine.Short(p)); }
        catch { Flash("Буфер обмена занят"); }
    }

    void Flash(string text)
    {
        _flash = text;
        _flashUntil = DateTime.UtcNow.AddSeconds(2.5);
        UpdateStatus();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        t.Tick += (_, _) => { t.Stop(); UpdateStatus(); };
        t.Start();
    }

    void UpdateStatus()
    {
        if (_flash != null && DateTime.UtcNow < _flashUntil) { Status.Text = _flash; return; }
        if (HotkeyError != null) { Status.Text = HotkeyError; return; }
        string apps = _engine.AppsReady ? $"{_engine.Apps.Items.Length} прил." : "приложения…";
        string files = _engine.Files.Ready ? $"{_engine.Files.Count:N0} файлов".Replace(' ', ' ') : $"индексация… {_engine.Files.Scanned:N0}".Replace(' ', ' ');
        Status.Text = $"{apps} · {files}";
    }
}
