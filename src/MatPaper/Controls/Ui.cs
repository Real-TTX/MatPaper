using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatPaper.Controls;

/// <summary>
/// Small helpers for the markup of the MatUi toolkit: icons and the logo tile. The icons come from the page's inline sprite
/// (<see cref="Toolkit"/>): the toolkit's own icons are <c>mat-i-NAME</c>, the few more MatPaper uses come from the icon library
/// (<c>scripts/matpaper-icons.txt</c>) and are <c>mat-il-NAME</c>; a caller just says the name.
/// </summary>
public static partial class Ui
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>How the toolkit's registry knows MatPaper (the accent itself comes from <c>data-app</c> in <c>matui.css</c>).</summary>
    public static class App
    {
        public const string Id = "matpaper";
        public const string Name = "MatPaper";

        /// <summary>The accent as the toolkit registry has it; it colours the bar of the browser.</summary>
        public const string Accent = "#268538";
    }

    /// <summary>The id of an icon's symbol in the inline sprite (<c>mat-i-NAME</c>, or <c>mat-il-NAME</c> for an icon of the library).</summary>
    public static string SymbolId(string name)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        return Toolkit.IsLibraryIcon(name) ? $"mat-il-{name}" : $"mat-i-{name}";
    }

    /// <summary>An icon from the page's inline sprite. A name that is in neither sprite is a bug that a test finds (it renders nothing visible).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#{SymbolId(name)}\"></use></svg>");
    }

    /// <summary>The same as markup text, for the places that build HTML in C# (tag helpers).</summary>
    public static string IconHtml(string name, string? extraClass = null)
    {
        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return $"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#{SymbolId(name)}\"></use></svg>";
    }

    /// <summary>
    /// The logo of the app: a sheet with a paper clip on the accent green, drawn in three levels of detail (<c>wwwroot/icons</c>, made into PNGs by
    /// <c>scripts/make-icons.js</c>). The menu takes the logo (<c>logo.svg</c>, size "sm" and "md"), the sign-in page and the offline page the illustration
    /// with its shadow and more lines (<c>icon.svg</c>, size "lg" and "xl"), and the browser tab the plain favicon (<c>favicon.svg</c>, see <c>_Head</c>).
    /// The tile is part of the picture, so it keeps its colours whatever accent a user has chosen.
    /// </summary>
    public static IHtmlContent Logo(string size = "md")
    {
        var src = size is "lg" or "xl" ? "/icons/icon.svg" : "/icons/logo.svg";
        var cls = size is "md" or "" ? "mat-logo mp-logo" : $"mat-logo mp-logo mat-logo--{size}";
        return new HtmlString($"<span class=\"{cls}\"><img src=\"{src}\" alt=\"{App.Name}\"></span>");
    }

    /// <summary>The first letters of a name, for an avatar.</summary>
    public static string Initials(string? name)
    {
        var letters = (name ?? string.Empty).Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(p => char.ToUpperInvariant(p[0]));
        var text = string.Concat(letters);
        return text.Length == 0 ? "?" : text;
    }

    /// <summary>Text with the characters of HTML escaped, for the few places that build markup in C#.</summary>
    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
