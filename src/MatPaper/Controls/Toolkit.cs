using System.Text.RegularExpressions;

namespace MatPaper.Controls;

/// <summary>
/// What the pages need of the MatUi toolkit as text: the icon sprites, which every page inlines once (rule ICON-01), and the names in them.
/// The files themselves (<c>wwwroot/matui</c>) are a copy of the toolkit (rule CODE-03); <c>scripts/update-matui.ps1</c> brings a newer one.
/// Two sprites: the toolkit's own (<c>matui-icons.svg</c>, ids <c>mat-i-NAME</c>) and MatPaper's pick of the icon library
/// (<c>matpaper-icons.svg</c>, ids <c>mat-il-NAME</c>).
/// </summary>
public sealed partial class Toolkit
{
    private static IReadOnlySet<string> _library = new HashSet<string>(StringComparer.Ordinal);

    public Toolkit(IWebHostEnvironment env)
    {
        var main = Read(env, "matui/matui-icons.svg");
        var extra = Read(env, "matui/matpaper-icons.svg");
        Sprite = main + extra;
        IconNames = SymbolRegex().Matches(main).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        LibraryNames = LibrarySymbolRegex().Matches(extra).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        _library = LibraryNames;
    }

    /// <summary>Both sprites as markup: hidden <c>svg</c> elements with one <c>symbol</c> per icon.</summary>
    public string Sprite { get; }

    /// <summary>The icons of the toolkit's own sprite.</summary>
    public IReadOnlySet<string> IconNames { get; }

    /// <summary>The icons taken from the library.</summary>
    public IReadOnlySet<string> LibraryNames { get; }

    /// <summary>True when the icon is one of the library's (and not in the toolkit's own sprite).</summary>
    public static bool IsLibraryIcon(string name) => _library.Contains(name);

    private static string Read(IWebHostEnvironment env, string path)
    {
        var file = env.WebRootFileProvider.GetFileInfo(path);
        if (!file.Exists)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(file.CreateReadStream());
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"<symbol id=""mat-i-([a-z0-9-]+)""")]
    private static partial Regex SymbolRegex();

    [GeneratedRegex(@"<symbol id=""mat-il-([a-z0-9-]+)""")]
    private static partial Regex LibrarySymbolRegex();
}
