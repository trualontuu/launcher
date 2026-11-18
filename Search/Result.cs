namespace Launcher.Search;

public enum Kind { App, Folder, File, Path, Url, Web, Music, Player }

public sealed class Result
{
    public Kind Kind;
    public string Title = "";
    public string Subtitle = "";
    /// <summary>Уникальный ключ для истории: app:&lt;id&gt; или file:&lt;путь&gt;.</summary>
    public string Key = "";
    /// <summary>Что открывать: путь, ID приложения, адрес.</summary>
    public string Target = "";
    /// <summary>Путь к exe/ярлыку приложения, если известен (для «в папке» и «от администратора»).</summary>
    public string? Path;
    public int Score;
    /// <summary>Индексы символов Title, совпавших с запросом (подсветка).</summary>
    public int[]? Hits;
    /// <summary>Музыка: адрес обложки; команда плеера: глиф иконки.</summary>
    public string? Cover, Glyph;
    /// <summary>Подпись справа вместо стандартной («Трек», «Артист», «Плеер»).</summary>
    public string? Label;
    /// <summary>Команда плееру (JSON-объект) — для Kind.Music и Kind.Player.</summary>
    public object? Command;

    public string KindName => Label ?? Kind switch
    {
        Kind.App => "Приложение",
        Kind.Folder => "Папка",
        Kind.File => "Файл",
        Kind.Path => "Путь",
        Kind.Url => "Сайт",
        _ => "Интернет",
    };

    /// <summary>Путь в файловой системе (для «показать в папке» и «скопировать путь»).</summary>
    public string? FsPath => Kind is Kind.File or Kind.Folder or Kind.Path ? Target : Path;
}
