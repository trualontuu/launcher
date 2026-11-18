using System;
using System.Collections.Generic;

namespace Launcher.Search;

/// <summary>
/// Оценка совпадения запроса с названием. Чем выше, тем лучше; 0 — не подходит.
/// 1000 точное · 900 начало · 820 начало слова · 780 аббревиатура · 760 все слова · 640 подстрока · 300–580 нечётко.
/// </summary>
public static class Matcher
{
    /// <param name="loose">разрешить нечёткий поиск и подстроку из 1–2 букв (только для запроса как есть)</param>
    public static int Score(string q, string norm, bool[] starts, string initials, int[] startIdx, bool loose, out int[]? hits)
    {
        hits = null;
        if (q.Length == 0 || norm.Length == 0) return 0;

        if (norm == q) { hits = Range(0, q.Length); return 1000; }
        if (norm.StartsWith(q, StringComparison.Ordinal)) { hits = Range(0, q.Length); return 900 - Math.Min(50, norm.Length - q.Length); }

        // начало любого слова: «code» → Visual Studio Code
        foreach (int s in startIdx)
        {
            if (s == 0 || s + q.Length > norm.Length) continue;
            if (string.CompareOrdinal(norm, s, q, 0, q.Length) == 0) { hits = Range(s, q.Length); return 820 - Math.Min(40, s); }
        }

        // аббревиатура: «vsc» → Visual Studio Code, «ям» → Яндекс Музыка
        if (q.Length >= 2 && initials.StartsWith(q, StringComparison.Ordinal))
        {
            hits = new int[q.Length];
            for (int i = 0; i < q.Length; i++) hits[i] = startIdx[i];
            return 780;
        }

        // несколько слов, каждое — начало какого-то слова в названии: «studio vis» → Visual Studio
        if (q.IndexOf(' ') > 0)
        {
            var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var h = new List<int>();
            bool all = true;
            foreach (var t in tokens)
            {
                bool found = false;
                foreach (int s in startIdx)
                {
                    if (s + t.Length <= norm.Length && string.CompareOrdinal(norm, s, t, 0, t.Length) == 0)
                    {
                        for (int k = 0; k < t.Length; k++) h.Add(s + k);
                        found = true; break;
                    }
                }
                if (!found) { all = false; break; }
            }
            if (all) { hits = h.ToArray(); return 760; }
        }

        int idx = (loose || q.Length >= 3) ? norm.IndexOf(q, StringComparison.Ordinal) : -1;
        if (idx >= 0) { hits = Range(idx, q.Length); return 640 - Math.Min(40, idx); }

        if (loose && q.Length >= 2) return Fuzzy(q, norm, starts, out hits);
        return 0;
    }

    /// <summary>Буквы запроса по порядку, первая — с начала слова. Бонусы за начала слов и подряд идущие буквы.</summary>
    static int Fuzzy(string q, string t, bool[] starts, out int[]? hits)
    {
        hits = null;
        var h = new int[q.Length];
        int qi = 0, raw = 0, last = -2;
        for (int ti = 0; ti < t.Length && qi < q.Length; ti++)
        {
            if (t[ti] != q[qi]) continue;
            bool ws = starts[ti];
            if (qi == 0 && !ws) continue;
            int s = 10;
            if (ws) s += 15;
            if (ti == last + 1) s += 12;
            raw += s;
            h[qi++] = ti;
            last = ti;
        }
        if (qi < q.Length) return 0;
        int span = h[^1] - h[0] + 1 - q.Length;
        raw -= Math.Min(span, 25);
        hits = h;
        int score = 300 + 280 * Math.Max(0, raw) / (q.Length * 37);
        return Math.Clamp(score, 300, 580);
    }

    public static int[] Range(int start, int len)
    {
        var r = new int[len];
        for (int i = 0; i < len; i++) r[i] = start + i;
        return r;
    }
}
