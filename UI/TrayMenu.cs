using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Island.Core;
using Island.UI;

namespace Launcher.UI;

/// <summary>Что умеет меню в трее — App отдаёт сюда свои действия.</summary>
public sealed class TrayActions
{
    public Action Open = () => { };
    public Action Reindex = () => { };
    public Action OpenSettings = () => { };
    public Action OpenDataFolder = () => { };
    public Action Quit = () => { };
    public Func<bool> GetAutostart = () => false;
    public Action<bool> SetAutostart = _ => { };
    public Func<string> GetHotkey = () => "";
    /// <summary>false — сочетание занято.</summary>
    public Func<string, bool> SetHotkey = _ => false;
    public Func<string> Status = () => "";
    public string Version = "";
}

/// <summary>
/// Своё меню в трее вместо стандартного ContextMenu: тёмная карточка в цветах Island,
/// всплывает над значком, закрывается при клике мимо или по Esc.
/// </summary>
public sealed class TrayMenu : Window
{
    static TrayMenu? _open;
    static readonly string[] HotkeyPresets = { "Alt+Q", "Alt+Space", "Ctrl+Space", "Alt+`" };

    readonly TrayActions _a;
    readonly Border _card;
    readonly TextBlock _hint;
    readonly System.Windows.Controls.Primitives.UniformGrid _keys;
    bool _closing;

    public static void Toggle(TrayActions a)
    {
        if (_open != null) { _open.CloseAnimated(); return; }
        _open = new TrayMenu(a);
        _open.Closed += (_, _) => _open = null;
        _open.Popup();
    }

    TrayMenu(TrayActions a)
    {
        _a = a;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = Brushes.White;
        Opacity = 0;
        Left = -20000; Top = -20000;

        var stack = new StackPanel { Width = 336 };

        // ---- шапка ----
        var logo = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
            Child = new TextBlock
            {
                Text = "", FontFamily = Icons, FontSize = 17, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        logo.SetResourceReference(Border.BackgroundProperty, "Accent");
        var title = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
        name.Inlines.Add("Launcher");
        name.Inlines.Add(new System.Windows.Documents.Run("  " + a.Version) { FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = DimBrush() });
        title.Children.Add(name);
        title.Children.Add(new TextBlock { Text = a.Status(), FontSize = 12, Foreground = DimBrush(), Margin = new Thickness(0, 1, 0, 0) });
        var head = new DockPanel { Margin = new Thickness(14, 14, 14, 10) };
        head.Children.Add(logo);
        head.Children.Add(title);
        stack.Children.Add(head);

        // ---- главное действие ----
        var open = Row("", "Открыть поиск", a.GetHotkey(), () => { CloseAnimated(); a.Open(); });
        stack.Children.Add(Section(open));

        // ---- горячая клавиша ----
        _keys = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(10, 2, 10, 6) };
        _hint = new TextBlock { FontSize = 11.5, Margin = new Thickness(14, 2, 14, 8), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var keyHead = new TextBlock { Text = "ГОРЯЧАЯ КЛАВИША", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 2, 0, 4) };
        keyHead.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var keyPanel = new StackPanel();
        keyPanel.Children.Add(keyHead);
        keyPanel.Children.Add(_keys);
        keyPanel.Children.Add(_hint);
        BuildKeys();
        stack.Children.Add(Section(keyPanel));

        // ---- настройки ----
        var auto = Toggle("", "Запускать вместе с Windows", a.GetAutostart(), on => a.SetAutostart(on));
        var reindex = Row("", "Переиндексировать файлы", "", () => { CloseAnimated(); a.Reindex(); });
        var settings = Row("", "Папки поиска и сокращения", "json", () => { CloseAnimated(); a.OpenSettings(); });
        var data = Row("", "Папка данных", "", () => { CloseAnimated(); a.OpenDataFolder(); });
        stack.Children.Add(Section(auto, reindex, settings, data));

        // ---- выход ----
        var quit = Row("", "Выйти из Launcher", "", () => { CloseAnimated(); a.Quit(); }, danger: true);
        stack.Children.Add(Section(quit, last: true));

        _card = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(18, 14, 18, 22),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            Child = stack, RenderTransform = new TranslateTransform(0, 10),
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        _card.SetResourceReference(Border.BackgroundProperty, "IslandBg");
        Content = _card;

        Deactivated += (_, _) => CloseAnimated();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseAnimated(); e.Handled = true; } };
    }

    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static Brush DimBrush() => (Brush)Application.Current.Resources["Dim"];

    // ---------- показ ----------
    void Popup()
    {
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetCursorPos(out var pt);
        var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var (_, work, scale, _) = Native.MonitorOf(mon);
        int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
        int x = Math.Clamp(pt.X - w / 2, work.Left, work.Left + work.W - w);
        // панель задач снизу — над ней; сверху/сбоку — от курсора
        int y = pt.Y > work.Top + work.H / 2 ? work.Top + work.H - h + (int)(10 * scale) : Math.Max(work.Top, pt.Y - (int)(8 * scale));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        Activate();
        Native.SetForegroundWindow(hwnd);
        Focus();

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Theme.Ms(120)));
        _card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, Theme.Ms(280)) { EasingFunction = Theme.Spring(0.8, 7.5) });
    }

    void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        var a = new DoubleAnimation(Opacity, 0, Theme.Ms(90));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
        _card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6, Theme.Ms(90)));
    }

    // ---------- элементы ----------
    static FrameworkElement Section(params FrameworkElement[] items) => Section(false, items);
    static FrameworkElement Section(FrameworkElement item, bool last) => Section(last, item);

    static FrameworkElement Section(bool last, params FrameworkElement[] items)
    {
        var p = new StackPanel { Margin = new Thickness(6, 4, 6, last ? 6 : 4) };
        foreach (var i in items) p.Children.Add(i);
        var b = new Border { Child = p, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)) };
        return b;
    }

    Border Row(string glyph, string text, string right, Action click, bool danger = false)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15, Width = 30, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        if (danger)
        {
            var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            icon.Foreground = red; label.Foreground = red;
            icon.Text = "";
        }
        else icon.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

        var dock = new DockPanel { LastChildFill = true };
        if (right.Length > 0)
        {
            var chip = new Border
            {
                Padding = new Thickness(7, 1, 7, 2), CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)),
                Child = new TextBlock { Text = right, FontSize = 11.5, Foreground = DimBrush() },
            };
            DockPanel.SetDock(chip, Dock.Right);
            dock.Children.Add(chip);
        }
        dock.Children.Add(icon);
        dock.Children.Add(label);
        return Clickable(dock, click);
    }

    Border Toggle(string glyph, string text, bool on, Action<bool> changed)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15, Width = 30, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var label = new TextBlock { Text = text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };

        var knob = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0), RenderTransform = new TranslateTransform(on ? 16 : 0, 0) };
        var track = new Border { Width = 34, Height = 18, CornerRadius = new CornerRadius(9), Child = knob, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
        void Paint(bool v)
        {
            if (v) { track.SetResourceReference(Border.BackgroundProperty, "Accent"); track.BorderBrush = Brushes.Transparent; knob.Background = Brushes.White; }
            else { track.Background = Brushes.Transparent; track.BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)); knob.Background = new SolidColorBrush(Color.FromArgb(0xC0, 255, 255, 255)); }
        }
        Paint(on);
        DockPanel.SetDock(track, Dock.Right);
        var dock = new DockPanel();
        dock.Children.Add(track);
        dock.Children.Add(icon);
        dock.Children.Add(label);

        bool state = on;
        return Clickable(dock, () =>
        {
            state = !state;
            changed(state);
            Paint(state);
            knob.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(state ? 16 : 0, Theme.Ms(180)) { EasingFunction = Theme.Spring(0.8, 7.5) });
        });
    }

    static Border Clickable(FrameworkElement content, Action click)
    {
        var b = new Border { Child = content, Height = 36, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "CardHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    void BuildKeys()
    {
        _keys.Children.Clear();
        var cur = _a.GetHotkey();
        foreach (var k in HotkeyPresets)
        {
            bool sel = string.Equals(k, cur, StringComparison.OrdinalIgnoreCase);
            var chip = new Border
            {
                Margin = new Thickness(3, 2, 3, 2), Padding = new Thickness(4, 5, 4, 6), CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = k, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
            };
            if (sel) { chip.SetResourceReference(Border.BackgroundProperty, "Accent"); chip.BorderBrush = Brushes.Transparent; }
            else
            {
                chip.Background = new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255));
                chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
                chip.MouseEnter += (_, _) => chip.SetResourceReference(Border.BackgroundProperty, "CardHover");
                chip.MouseLeave += (_, _) => chip.Background = new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255));
            }
            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (sel) return;
                if (_a.SetHotkey(k)) { _hint.Visibility = Visibility.Collapsed; BuildKeys(); }
                else
                {
                    _hint.Text = k.Equals("Alt+Space", StringComparison.OrdinalIgnoreCase)
                        ? "Alt+Space занят полкой Island — поменяйте её в настройках острова."
                        : $"{k} занято другой программой.";
                    _hint.Visibility = Visibility.Visible;
                }
            };
            _keys.Children.Add(chip);
        }
        // текущая клавиша не из списка (задана в json)
        if (Array.FindIndex(HotkeyPresets, p => p.Equals(cur, StringComparison.OrdinalIgnoreCase)) < 0 && cur.Length > 0)
        {
            _hint.Text = $"Сейчас: {cur} (задано в settings.json)";
            _hint.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Для --snap: показать меню за экраном без активации.</summary>
    public static TrayMenu ForSnap(TrayActions a)
    {
        var m = new TrayMenu(a) { ShowActivated = false, Opacity = 1 };
        m.Show();
        m._card.RenderTransform = new TranslateTransform();
        return m;
    }
}
