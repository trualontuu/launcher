using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Threading;

namespace Launcher.Search;

/// <summary>
/// Свой индекс файлов в памяти (Windows Search у пользователя выключен).
/// Строится в фоне при запуске, дальше обновляется FileSystemWatcher'ами без пересканирования.
/// Каждая запись — имя + номер родительской папки, полный путь собирается только для показанных результатов.
/// </summary>
public sealed class FileIndex : IDisposable
{
    struct Entry
    {
        public string Name, Lower;
        public int Parent;
        public byte Depth;
        public bool Dir, Dead;
    }

    readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    Entry[] _e = new Entry[1 << 15];
    int _n, _live;
    List<string> _dirs = new();
    Dictionary<string, int> _dirIds = new(StringComparer.OrdinalIgnoreCase);
    HashSet<string> _exclude = new(StringComparer.OrdinalIgnoreCase);
    List<string> _roots = new();
    readonly List<FileSystemWatcher> _watchers = new();
    readonly BlockingCollection<(WatcherChangeTypes type, string path, string? old)> _events = new();

    public bool Ready { get; private set; }
    public int Count => _live;
    public int Scanned;
    public double BuildMs { get; private set; }
    public event Action? Changed;

    // ---------- построение ----------
    public void Build(IEnumerable<string> roots, IEnumerable<string> exclude)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _exclude = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        _roots = new List<string>(roots);

        var b = new Builder(_exclude, this);
        foreach (var r in _roots) b.AddTree(r, addSelf: true);

        _lock.EnterWriteLock();
        try { _e = b.E; _n = b.N; _live = b.N; _dirs = b.Dirs; _dirIds = b.DirIds; }
        finally { _lock.ExitWriteLock(); }

        BuildMs = sw.Elapsed.TotalMilliseconds;
        Ready = true;
        Changed?.Invoke();
        StartWatchers();
    }

    /// <summary>Сканер без блокировок: пишет в свои массивы, потом они подменяют индекс целиком.</summary>
    sealed class Builder
    {
        public Entry[] E = new Entry[1 << 16];
        public int N;
        public List<string> Dirs = new();
        public Dictionary<string, int> DirIds = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _ex;
        readonly FileIndex _owner;
        public Builder(HashSet<string> ex, FileIndex owner) { _ex = ex; _owner = owner; }

        public int DirId(string path)
        {
            if (DirIds.TryGetValue(path, out int id)) return id;
            id = Dirs.Count;
            Dirs.Add(path);
            DirIds[path] = id;
            return id;
        }

        public void Add(string name, int parent, bool dir)
        {
            if (N == E.Length) Array.Resize(ref E, E.Length * 2);
            E[N++] = new Entry { Name = name, Lower = Text.Norm(name), Parent = parent, Dir = dir, Depth = DepthOf(Dirs[parent]) };
        }

        public void AddTree(string root, bool addSelf)
        {
            if (addSelf)
            {
                var parent = Path.GetDirectoryName(root.TrimEnd('\\'));
                var name = Path.GetFileName(root.TrimEnd('\\'));
                if (parent != null && name.Length > 0) Add(name, DirId(parent), true);
            }
            Walk(root, this.Add, DirId, _ex, ref _owner.Scanned);
        }
    }

    static readonly EnumerationOptions EnumOpts = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false,
    };

    delegate void AddFn(string name, int parent, bool dir);

    static void Walk(string root, AddFn add, Func<string, int> dirId, HashSet<string> ex, ref int counter)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            int pid = dirId(dir);
            try
            {
                var en = new FileSystemEnumerable<(string name, bool dir, FileAttributes attr)>(dir,
                    (ref FileSystemEntry fe) => (fe.FileName.ToString(), fe.IsDirectory, fe.Attributes), EnumOpts);
                foreach (var (name, isDir, attr) in en)
                {
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (isDir)
                    {
                        if ((attr & FileAttributes.ReparsePoint) != 0 || Skip(name, ex)) continue;
                        add(name, pid, true);
                        stack.Push(Path.Join(dir, name));
                    }
                    else add(name, pid, false);
                    counter++;
                }
            }
            catch { }
        }
    }

    /// <summary>Глубина папки в пути — глубоко закопанное ниже в выдаче.</summary>
    static byte DepthOf(string dir)
    {
        int n = 0;
        foreach (char c in dir) if (c == '\\') n++;
        return (byte)Math.Min(n, 255);
    }

    static bool Skip(string dirName, HashSet<string> ex) => dirName.Length == 0 || dirName[0] == '.' || dirName[0] == '$' || ex.Contains(dirName);

    // ---------- поиск ----------
    public readonly struct Hit
    {
        public readonly string Name, Path;
        public readonly bool Dir;
        public readonly int Score;
        public readonly int[]? Hits;
        public Hit(string name, string path, bool dir, int score, int[]? hits) { Name = name; Path = path; Dir = dir; Score = score; Hits = hits; }
    }

    /// <summary>Оценка имени файла: только начало, начало слова и подстрока — нечёткий поиск по 200 тыс. файлов дал бы шум.</summary>
    public static int ScoreName(string q, string name, string lower, bool dir, out int[]? hits)
    {
        hits = null;
        int i = lower.IndexOf(q, StringComparison.Ordinal);
        if (i < 0) return 0;
        int s;
        if (i == 0) s = lower.Length == q.Length ? 880 : 820;
        else if (Text.IsWordStartAt(name, i)) s = 740;
        else if (q.Length <= 2) return 0; // «ям» внутри случайных имён — шум
        else s = 600;
        if (!dir)
        {
            int dot = lower.LastIndexOf('.');
            if (dot > 0 && i >= dot) s = 250;                      // совпало только расширение
            else if (dot > 0 && i == 0 && dot == q.Length) s = 870; // имя без расширения целиком
        }
        s -= Math.Min(60, lower.Length - q.Length);
        if (dir) s += 15;
        hits = Matcher.Range(i, q.Length);
        return s;
    }

    public List<Hit> Search(IReadOnlyList<(string q, int penalty)> variants, int take, CancellationToken ct)
    {
        var top = new List<(int score, int idx, int[]? hits)>(take + 1);
        int min = 0;
        _lock.EnterReadLock();
        try
        {
            var e = _e;
            for (int i = 0; i < _n; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                ref var x = ref e[i];
                if (x.Dead) continue;
                int best = 0; int[]? bh = null;
                foreach (var (q, pen) in variants)
                {
                    int s = ScoreName(q, x.Name, x.Lower, x.Dir, out var h);
                    if (s == 0 && q.IndexOf(' ') > 0) s = MultiWord(q, x, out h);
                    if (s > 0) s -= pen;
                    if (s > best) { best = s; bh = h; }
                }
                if (best > 0 && x.Depth > 5) best -= Math.Min(40, (x.Depth - 5) * 4);
                if (best <= 0 || (top.Count >= take && best <= min)) continue;
                Insert(top, (best, i, bh), take);
                min = top.Count >= take ? top[^1].score : 0;
            }

            var res = new List<Hit>(top.Count);
            foreach (var (score, idx, hits) in top)
            {
                ref var x = ref e[idx];
                res.Add(new Hit(x.Name, Path.Join(_dirs[x.Parent], x.Name), x.Dir, score, hits));
            }
            return res;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>«дом dwg» — все слова в имени; «графика дом» — последнее в имени, остальные в пути.</summary>
    int MultiWord(string q, in Entry x, out int[]? hits)
    {
        hits = null;
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return 0;
        bool allInName = true;
        var h = new List<int>();
        foreach (var t in tokens)
        {
            int i = x.Lower.IndexOf(t, StringComparison.Ordinal);
            if (i < 0) { allInName = false; break; }
            for (int k = 0; k < t.Length; k++) h.Add(i + k);
        }
        if (allInName) { hits = h.ToArray(); return 680; }

        int li = x.Lower.IndexOf(tokens[^1], StringComparison.Ordinal);
        if (li < 0) return 0;
        var dir = _dirs[x.Parent];
        for (int t = 0; t < tokens.Length - 1; t++)
            if (dir.IndexOf(tokens[t], StringComparison.OrdinalIgnoreCase) < 0) return 0;
        hits = Matcher.Range(li, tokens[^1].Length);
        return 560 - Math.Min(60, x.Lower.Length - tokens[^1].Length);
    }

    static void Insert(List<(int score, int idx, int[]? hits)> top, (int score, int idx, int[]? hits) item, int take)
    {
        int pos = top.Count;
        while (pos > 0 && top[pos - 1].score < item.score) pos--;
        top.Insert(pos, item);
        if (top.Count > take) top.RemoveAt(top.Count - 1);
    }

    // ---------- живые изменения ----------
    void StartWatchers()
    {
        foreach (var r in _roots)
        {
            try
            {
                var w = new FileSystemWatcher(r)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => _events.Add((WatcherChangeTypes.Created, e.FullPath, null));
                w.Deleted += (_, e) => _events.Add((WatcherChangeTypes.Deleted, e.FullPath, null));
                w.Renamed += (_, e) => _events.Add((WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath));
                w.Error += (_, _) => _events.Add((WatcherChangeTypes.All, r, null)); // переполнение буфера → пересканировать корень
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { }
        }
        var t = new Thread(EventLoop) { IsBackground = true, Name = "FileIndexEvents", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    void EventLoop()
    {
        foreach (var ev in _events.GetConsumingEnumerable())
        {
            try
            {
                bool changed = ev.type switch
                {
                    WatcherChangeTypes.Created => OnCreated(ev.path),
                    WatcherChangeTypes.Deleted => OnDeleted(ev.path),
                    WatcherChangeTypes.Renamed => OnDeleted(ev.old!) | OnCreated(ev.path),
                    _ => Rescan(ev.path),
                };
                if (changed && _events.Count == 0) Changed?.Invoke();
            }
            catch { }
        }
    }

    bool Excluded(string path)
    {
        foreach (var r in _roots)
        {
            if (!path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = path.AsSpan(r.Length).TrimStart('\\');
            foreach (var seg in rel.ToString().Split('\\'))
                if (Skip(seg, _exclude)) return true;
            return false;
        }
        return true;
    }

    int DirIdLocked(string path)
    {
        if (_dirIds.TryGetValue(path, out int id)) return id;
        id = _dirs.Count;
        _dirs.Add(path);
        _dirIds[path] = id;
        return id;
    }

    /// <summary>Одиночное событие: не добавлять то, что уже есть (Created может прийти после того, как папку уже обошли).</summary>
    void AddUniqueLocked(string name, int parent, bool dir)
    {
        for (int i = 0; i < _n; i++)
            if (_e[i].Parent == parent && !_e[i].Dead && string.Equals(_e[i].Name, name, StringComparison.OrdinalIgnoreCase)) return;
        AddLocked(name, parent, dir);
    }

    void AddLocked(string name, int parent, bool dir)
    {
        if (_n == _e.Length) Array.Resize(ref _e, _e.Length * 2);
        _e[_n++] = new Entry { Name = name, Lower = Text.Norm(name), Parent = parent, Dir = dir, Depth = DepthOf(_dirs[parent]) };
        _live++;
    }

    bool OnCreated(string path)
    {
        if (Excluded(path)) return false;
        FileAttributes attr;
        try { attr = File.GetAttributes(path); } catch { return false; }
        if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
        bool dir = (attr & FileAttributes.Directory) != 0;
        var parent = Path.GetDirectoryName(path);
        if (parent == null) return false;

        _lock.EnterWriteLock();
        try
        {
            AddUniqueLocked(Path.GetFileName(path), DirIdLocked(parent), dir);
            if (dir && (attr & FileAttributes.ReparsePoint) == 0)
            {
                int dummy = 0;
                Walk(path, AddLocked, DirIdLocked, _exclude, ref dummy); // папку могли перенести целиком
            }
        }
        finally { _lock.ExitWriteLock(); }
        return true;
    }

    bool OnDeleted(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent == null) return false;
        var name = Path.GetFileName(path);
        bool any = false;
        _lock.EnterWriteLock();
        try
        {
            if (_dirIds.TryGetValue(parent, out int pid))
            {
                for (int i = 0; i < _n; i++)
                {
                    ref var x = ref _e[i];
                    if (x.Parent == pid && !x.Dead && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) { x.Dead = true; _live--; any = true; }
                }
            }
            // удалили папку — убираем всё внутри
            var prefix = path.TrimEnd('\\') + "\\";
            var gone = new HashSet<int>();
            if (_dirIds.TryGetValue(path, out int self)) gone.Add(self);
            for (int d = 0; d < _dirs.Count; d++)
                if (_dirs[d].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) gone.Add(d);
            if (gone.Count > 0)
            {
                for (int i = 0; i < _n; i++)
                {
                    ref var x = ref _e[i];
                    if (!x.Dead && gone.Contains(x.Parent)) { x.Dead = true; _live--; any = true; }
                }
                foreach (int d in gone) { _dirIds.Remove(_dirs[d]); _dirs[d] = "\0"; }
            }
        }
        finally { _lock.ExitWriteLock(); }
        return any;
    }

    bool Rescan(string root)
    {
        OnDeleted(root);
        _lock.EnterWriteLock();
        try
        {
            int dummy = 0;
            Walk(root, AddLocked, DirIdLocked, _exclude, ref dummy);
        }
        finally { _lock.ExitWriteLock(); }
        return true;
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _events.CompleteAdding();
    }
}
