using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Launcher.Shell;

/// <summary>
/// Связь с плеером (spspsps/player) через его именованный канал: одна строка JSON туда, одна обратно.
/// Поиск музыки спрашиваем только у уже запущенного плеера; команды (пауза, волна…) при необходимости его запускают.
/// </summary>
public static class PlayerLink
{
    static string PipeName => "Player.Control." + Environment.UserName;
    static string Exe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Player\Player.exe");

    public static bool Installed => File.Exists(Exe);

    /// <summary>Последнее известное состояние плеера — для подписей команд («Пауза · Nemo — Chris Travis»).</summary>
    public static volatile NowPlaying? Now;
    public sealed record NowPlaying(bool Playing, string Title, string Artist);

    public sealed record Item(string Type, string Id, string Title, string Sub, string Cover);

    static async Task<JsonElement?> Send(object req, int connectMs, int totalMs, CancellationToken ct = default)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(totalMs);
            await pipe.ConnectAsync(connectMs, cts.Token);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req) + "\n");
            await pipe.WriteAsync(bytes, cts.Token);
            await pipe.FlushAsync(cts.Token);
            using var r = new StreamReader(pipe, Encoding.UTF8);
            var line = await r.ReadLineAsync(cts.Token);
            if (line == null) return null;
            var e = JsonDocument.Parse(line).RootElement.Clone();
            Remember(e);
            return e;
        }
        catch { return null; }
    }

    static void Remember(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("playing", out var p))
            Now = new NowPlaying(p.ValueKind == JsonValueKind.True, S(e, "title"), S(e, "artist"));
    }

    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Обновить подписи команд; плеер не запускаем.</summary>
    public static Task RefreshState() => Send(new { cmd = "state" }, 80, 400);

    /// <summary>Поиск в Яндекс Музыке через запущенный плеер (у него вход и токен). Плеера нет — пусто.</summary>
    public static async Task<List<Item>> Search(string q, CancellationToken ct)
    {
        var list = new List<Item>();
        var res = await Send(new { cmd = "search", q }, 80, 2500, ct);
        if (res is { } e && e.TryGetProperty("items", out var items))
            foreach (var x in items.EnumerateArray())
                list.Add(new Item(S(x, "type"), S(x, "id"), S(x, "title"), S(x, "sub"), S(x, "cover")));
        return list;
    }

    /// <summary>Команда плееру; если он не запущен — запускаем в фоне и ждём канал до 8 с.</summary>
    /// <returns>текст ошибки или null</returns>
    public static async Task<string?> Command(object req)
    {
        if (await Send(req, 150, 4000) != null) return null;
        if (!Installed) return "Плеер не установлен";
        try { Process.Start(new ProcessStartInfo(Exe, "--background") { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) { return "Не запустился плеер: " + e.Message; }
        for (int i = 0; i < 16; i++)
        {
            await Task.Delay(500);
            if (await Send(req, 300, 6000) != null) return null;
        }
        return "Плеер не ответил";
    }
}
