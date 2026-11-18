using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core;
using Launcher.Search;
using Launcher.Shell;
using Launcher.UI;

namespace Launcher;

/// <summary>
/// «Launcher.exe --snap &lt;папка&gt; запрос1 запрос2 …» — без кликов по экрану: строит индексы,
/// прогоняет запросы, сохраняет PNG окна и report.txt (время, результаты с оценками, память).
/// </summary>
public static class Snap
{
    public static async void Run(Application app, string dir, string[] queries)
    {
        var log = new StringBuilder();
        try
        {
            Directory.CreateDirectory(dir);
            ThemeSync.Override = Environment.GetEnvironmentVariable("LAUNCHER_THEME");
            ThemeSync.Apply();
            Sta.Init(app.Dispatcher);
            var cfg = Config.Load();
            var sw = Stopwatch.StartNew();
            var engine = new Engine(cfg);
            engine.Start();
            while (!engine.AppsReady || !engine.Files.Ready)
            {
                await Task.Delay(50);
                if (sw.Elapsed.TotalSeconds > 300) { log.AppendLine("ТАЙМАУТ индексации"); break; }
            }
            log.AppendLine($"корни: {string.Join(" | ", cfg.EffectiveRoots())}");
            log.AppendLine($"приложений: {engine.Apps.Items.Length} за {engine.Apps.LoadMs:F0} мс");
            File.WriteAllLines(Path.Combine(dir, "apps.txt"), engine.Apps.Items.Select(a => $"{a.Name}	{a.Id}	{a.Path}	{a.Exe}"));
            log.AppendLine($"файлов: {engine.Files.Count} за {engine.Files.BuildMs:F0} мс");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            log.AppendLine($"память после индексации: managed {GC.GetTotalMemory(false) / 1048576.0:F1} МБ, private {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F1} МБ");

            var win = new LauncherWindow(engine, cfg) { SnapMode = true };
            win.ShowOffscreen();
            if (queries.Length == 0) queries = new[] { "" };
            int n = 0;
            foreach (var q in queries)
            {
                if (q == "@menu")
                {
                    var menu = TrayMenu.ForSnap(new TrayActions
                    {
                        GetHotkey = () => cfg.Hotkey, GetAutostart = () => false, Version = "v0.1.0",
                        Status = () => $"{engine.Apps.Items.Length} приложение · {engine.Files.Count} файлов",
                    });
                    await Task.Delay(300);
                    menu.UpdateLayout();
                    Save(menu, Path.Combine(dir, $"{n++:00}_menu.png"));
                    menu.Close();
                    continue;
                }
                // разогрев и замер: тот же запрос ещё раз
                win.SearchDone = new TaskCompletionSource();
                win.SetQuery(q + " ");
                await win.SearchDone.Task;
                win.SearchDone = new TaskCompletionSource();
                win.MusicDone = new TaskCompletionSource();
                win.SetQuery(q);
                await win.SearchDone.Task;
                double ms = win.LastSearchMs;
                var mt = Stopwatch.StartNew();
                if (await Task.WhenAny(win.MusicDone.Task, Task.Delay(4000)) == win.MusicDone.Task && win.Results.Any(r => r.Kind == Kind.Music))
                {
                    log.AppendLine($"музыка подгрузилась через {mt.ElapsedMilliseconds} мс после локального поиска");
                    await Task.Delay(1200); // обложки
                }
                await Task.Delay(700); // иконки
                win.UpdateLayout();
                var file = Path.Combine(dir, $"{n++:00}_{Safe(q)}.png");
                Save(win, file);
                log.AppendLine();
                log.AppendLine($"«{q}» — {ms:F1} мс, {win.Results.Count} результатов");
                foreach (var r in win.Results.Take(10))
                    log.AppendLine($"   {r.Score,5}  {r.KindName,-10} {r.Title}   [{r.Subtitle}]");
            }
            log.AppendLine();
            log.AppendLine($"память в конце: private {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F1} МБ");
        }
        catch (Exception e) { log.AppendLine("ОШИБКА: " + e); }
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        app.Shutdown();
    }

    static string Safe(string q)
    {
        var s = new string(q.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c).ToArray());
        return s.Length == 0 ? "empty" : s.Length > 30 ? s[..30] : s;
    }

    static void Save(Window w, string file)
    {
        var root = (FrameworkElement)w.Content;
        double scale = 1.5;
        int pw = (int)Math.Ceiling(root.ActualWidth * scale), ph = (int)Math.Ceiling(root.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // подложка как обои, чтобы было видно тень и края
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(58, 64, 92), Color.FromRgb(30, 34, 48), 90), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
