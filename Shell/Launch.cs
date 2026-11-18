using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Launcher.Search;

namespace Launcher.Shell;

public static class Launch
{
    /// <returns>текст ошибки или null</returns>
    public static string? Open(Result r, string webSearch, string query)
    {
        try
        {
            switch (r.Kind)
            {
                case Kind.App when !IsFsPath(r.Target):
                    Start(@"shell:AppsFolder\" + r.Target);
                    break;
                case Kind.Web:
                    Start(string.Format(webSearch, Uri.EscapeDataString(query)));
                    break;
                case Kind.Url:
                    Start(r.Target.Contains("://") ? r.Target : "https://" + r.Target);
                    break;
                default:
                    var psi = new ProcessStartInfo(r.Target) { UseShellExecute = true };
                    var dir = File.Exists(r.Target) ? System.IO.Path.GetDirectoryName(r.Target) : null;
                    if (dir != null) psi.WorkingDirectory = dir;
                    Process.Start(psi)?.Dispose();
                    break;
            }
            return null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return null; } // отмена UAC
        catch (Exception e) { return e.Message; }
    }

    public static string? Reveal(Result r)
    {
        var p = r.FsPath;
        if (p == null) return "Путь неизвестен";
        try
        {
            if (File.Exists(p) || Directory.Exists(p)) Process.Start("explorer.exe", $"/select,\"{p}\"")?.Dispose();
            else return "Файл не найден";
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    public static string? RunAsAdmin(Result r)
    {
        string target = r.Kind == Kind.App ? (r.Path ?? @"shell:AppsFolder\" + r.Target) : r.Target;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            return null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return null; }
        catch (Exception e) { return e.Message; }
    }

    static bool IsFsPath(string s) => s.Length > 2 && s[1] == ':' && s[2] == '\\';

    static void Start(string s) => Process.Start(new ProcessStartInfo(s) { UseShellExecute = true })?.Dispose();
}
