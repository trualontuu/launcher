using System;
using System.Collections.Generic;
using System.Text;

namespace Launcher.Search;

/// <summary>Нормализация строк, неправильная раскладка, транслит, начала слов.</summary>
public static class Text
{
    /// <summary>Нижний регистр и ё→е. Длина строки не меняется (важно для подсветки совпадений).</summary>
    public static string Norm(string s)
    {
        var lower = s.ToLowerInvariant();
        return lower.IndexOf('ё') >= 0 ? lower.Replace('ё', 'е') : lower;
    }

    const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./";
    const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбю.";

    /// <summary>Текст, набранный не в той раскладке: «еудупкфь» → «telegram», «lji» → «дом».</summary>
    public static string SwapLayout(string q)
    {
        var sb = new StringBuilder(q.Length);
        foreach (char c in q)
        {
            int i;
            if (c is >= 'а' and <= 'я' or 'ё' && (i = Ru.IndexOf(c)) >= 0) sb.Append(En[i]);
            else if ((i = En.IndexOf(c)) >= 0) sb.Append(Ru[i]);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool HasCyrillic(string s)
    {
        foreach (char c in s) if (c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') return true;
        return false;
    }

    public static bool HasLetters(string s)
    {
        foreach (char c in s) if (char.IsLetter(c)) return true;
        return false;
    }

    static readonly Dictionary<char, string> Tr = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ж'] = "zh", ['з'] = "z",
        ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p",
        ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>Кириллица латиницей: «телеграм» → «telegram», «яндекс» → «yandeks».</summary>
    public static string Translit(string norm)
    {
        var sb = new StringBuilder(norm.Length + 4);
        foreach (char c in norm) sb.Append(Tr.TryGetValue(c, out var t) ? t : c.ToString());
        return sb.ToString();
    }

    static bool IsSep(char c) => c is ' ' or '-' or '_' or '.' or '(' or ')' or '[' or ']' or ',' or '&' or '+' or '/' or '\\' or ':' or '—' or '–' or '«' or '»' or '"' or '\'';

    /// <summary>Где начинаются «слова»: после разделителей, на стыке camelCase и цифр.</summary>
    public static bool[] WordStarts(string original)
    {
        var r = new bool[original.Length];
        for (int i = 0; i < original.Length; i++)
        {
            char c = original[i];
            if (IsSep(c)) continue;
            if (i == 0) { r[i] = true; continue; }
            char p = original[i - 1];
            r[i] = IsSep(p)
                || (char.IsUpper(c) && char.IsLower(p))
                || (char.IsDigit(c) && !char.IsDigit(p))
                || (char.IsLetter(c) && char.IsDigit(p));
        }
        return r;
    }

    public static bool IsWordStartAt(string original, int i)
    {
        if (i <= 0) return true;
        if (i >= original.Length) return false;
        char c = original[i], p = original[i - 1];
        if (IsSep(c)) return false;
        return IsSep(p) || (char.IsUpper(c) && char.IsLower(p)) || (char.IsDigit(c) && !char.IsDigit(p)) || (char.IsLetter(c) && char.IsDigit(p));
    }

    /// <summary>Варианты запроса: как есть, в другой раскладке, транслитом. Второе число — штраф.</summary>
    public static List<(string q, int penalty)> Variants(string q)
    {
        var list = new List<(string, int)> { (q, 0) };
        if (!HasLetters(q)) return list;
        var sw = SwapLayout(q);
        if (sw != q) list.Add((sw, 40));
        if (HasCyrillic(q))
        {
            var tr = Translit(q);
            if (tr != q && tr != sw) list.Add((tr, 25));
        }
        return list;
    }
}
