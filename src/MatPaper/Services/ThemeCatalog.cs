using MatPaper.Configuration;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// The themes MatPaper offers. A theme has three independent parts, each rendered as an
/// attribute on &lt;html&gt; and resolved purely in CSS (wwwroot/css/themes.css):
/// <list type="bullet">
/// <item><b>Mode</b> — system / light / dark. "system" is turned into data-mode="light|dark" by
/// the inline head script, so the page never flashes the wrong brightness.</item>
/// <item><b>Scheme</b> — the surface palette: standard, paper (warm), contrast, oled (pure black,
/// dark mode only).</item>
/// <item><b>Accent</b> — the brand colour used for buttons, links, focus and highlights.</item>
/// </list>
/// The ids here must match the selectors in themes.css. Labels are English resource keys.
/// </summary>
public static class ThemeCatalog
{
    public const string DefaultMode = "system";
    public const string DefaultScheme = "standard";
    public const string DefaultAccent = "green";

    public sealed record Option(string Id, string Label, string? Hint = null);

    public static readonly IReadOnlyList<Option> Modes = new[]
    {
        new Option("system", "System"),
        new Option("light", "Light"),
        new Option("dark", "Dark"),
    };

    public static readonly IReadOnlyList<Option> Schemes = new[]
    {
        new Option("standard", "Standard"),
        new Option("paper", "Paper", "Warm, paper-like tones."),
        new Option("contrast", "High contrast", "Strong borders and maximum text contrast."),
        new Option("oled", "Black (OLED)", "Pure black surfaces; only affects dark mode."),
    };

    public static readonly IReadOnlyList<Option> Accents = new[]
    {
        new Option("green", "Green"),
        new Option("blue", "Blue"),
        new Option("violet", "Violet"),
        new Option("teal", "Petrol"),
        new Option("amber", "Amber"),
        new Option("rose", "Rose"),
        new Option("graphite", "Graphite"),
    };

    public static bool IsMode(string? id) => Modes.Any(o => o.Id == id);
    public static bool IsScheme(string? id) => Schemes.Any(o => o.Id == id);
    public static bool IsAccent(string? id) => Accents.Any(o => o.Id == id);
}

/// <summary>A resolved theme plus whether the user has saved their own mode (for the localStorage migration).</summary>
public sealed record ThemeChoice(string Mode, string Scheme, string Accent, bool UserHasMode);

/// <summary>
/// Resolves the theme for the current request: the signed-in user's own choice per part, falling
/// back to the instance default from the settings page (also used on anonymous pages).
/// </summary>
public sealed class ThemeService(AppConfig config, CurrentUser currentUser, AppDbContext db)
{
    public ThemeChoice InstanceDefault()
    {
        var display = config.Display;
        return new ThemeChoice(
            ThemeCatalog.IsMode(display?.ThemeMode) ? display!.ThemeMode : ThemeCatalog.DefaultMode,
            ThemeCatalog.IsScheme(display?.ThemeScheme) ? display!.ThemeScheme : ThemeCatalog.DefaultScheme,
            ThemeCatalog.IsAccent(display?.ThemeAccent) ? display!.ThemeAccent : ThemeCatalog.DefaultAccent,
            UserHasMode: false);
    }

    public async Task<ThemeChoice> ResolveAsync(CancellationToken ct = default)
    {
        var fallback = InstanceDefault();
        if (currentUser.UserId is not long userId)
        {
            return fallback;
        }

        var own = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.ThemeMode, u.ThemeScheme, u.ThemeAccent })
            .FirstOrDefaultAsync(ct);
        if (own is null)
        {
            return fallback;
        }

        return new ThemeChoice(
            ThemeCatalog.IsMode(own.ThemeMode) ? own.ThemeMode! : fallback.Mode,
            ThemeCatalog.IsScheme(own.ThemeScheme) ? own.ThemeScheme! : fallback.Scheme,
            ThemeCatalog.IsAccent(own.ThemeAccent) ? own.ThemeAccent! : fallback.Accent,
            UserHasMode: ThemeCatalog.IsMode(own.ThemeMode));
    }
}
