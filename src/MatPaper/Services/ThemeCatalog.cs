using MatPaper.Configuration;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>
/// The themes MatPaper offers. A theme has three independent parts, each rendered as an
/// attribute on &lt;html&gt; and resolved purely in the CSS of the MatUi toolkit (wwwroot/matui):
/// <list type="bullet">
/// <item><b>Mode</b> — system / light / dark (<c>data-theme</c>; "system" writes nothing, the page follows the
/// device, and the toolkit's boot script applies a choice that only the browser knows).</item>
/// <item><b>Scheme</b> — the surface palette: standard, paper (warm), contrast, oled (pure black,
/// dark mode only) (<c>data-scheme</c>, absent for the standard).</item>
/// <item><b>Accent</b> — the brand colour used for buttons, links, focus and highlights
/// (<c>data-accent</c>: one of the toolkit's presets; green is the colour of the app itself and writes nothing).</item>
/// </list>
/// The ids are what the accounts and the settings store; <see cref="MatAccent"/> says which preset of the toolkit
/// stands behind an accent. Labels are English resource keys.
/// </summary>
public static class ThemeCatalog
{
    public const string DefaultMode = "system";
    public const string DefaultScheme = "standard";
    public const string DefaultAccent = "green";

    public sealed record Option(string Id, string Label, string? Hint = null);

    /// <summary>The preset of the toolkit (<c>data-accent</c>) that an accent id stands for; null is the colour of the app itself.</summary>
    public static string? MatAccent(string? accent) => accent switch
    {
        "blue" => "ocean",
        "violet" => "purple",
        "teal" => "teal",
        "amber" => "gold",
        "rose" => "berry",
        "graphite" => "graphite",
        _ => null,
    };

    /// <summary>The colour of an accent (for the bar of the browser, <c>theme-color</c>); the values are those of the toolkit's presets.</summary>
    public static string AccentColour(string? accent) => accent switch
    {
        "blue" => "#1a73e8",
        "violet" => "#8300bc",
        "teal" => "#008578",
        "amber" => "#966d07",
        "rose" => "#c2185b",
        "graphite" => "#546e7a",
        _ => "#268538",
    };

    public static readonly IReadOnlyList<Option> Modes = new[]
    {
        new Option("system", "System"),
        new Option("light", "Light"),
        new Option("dark", "Dark"),
    };

    public static readonly IReadOnlyList<Option> Schemes = new[]
    {
        new Option("standard", "Standard"),
        new Option("paper", "Paper"),
        new Option("contrast", "High contrast"),
        new Option("oled", "Black (OLED)", "Dark mode only."),
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

/// <summary>A resolved theme plus whether the user has saved their own mode.</summary>
public sealed record ThemeChoice(string Mode, string Scheme, string Accent, bool UserHasMode)
{
    /// <summary>The colour of the browser's bar.</summary>
    public string ThemeColour => ThemeCatalog.AccentColour(Accent);

    /// <summary>
    /// The attributes of <c>&lt;html&gt;</c> that the toolkit reads, written only when they have a value: an empty
    /// attribute is a present one, and the toolkit's boot script would take it for a choice of the server and never apply what
    /// the browser remembers. The app itself (<c>data-app</c>) gives the accent the toolkit's registry knows.
    /// </summary>
    public string RootAttributes
    {
        get
        {
            var sb = new System.Text.StringBuilder($"data-app=\"{Controls.Ui.App.Id}\"");
            if (Mode is "light" or "dark")
            {
                sb.Append(" data-theme=\"").Append(Mode).Append('"');
            }

            if (ThemeCatalog.MatAccent(Accent) is { } preset)
            {
                sb.Append(" data-accent=\"").Append(preset).Append('"');
            }

            if (Scheme is not (ThemeCatalog.DefaultScheme or ""))
            {
                sb.Append(" data-scheme=\"").Append(Scheme).Append('"');
            }

            return sb.ToString();
        }
    }
}

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

        // The page of an error is drawn with this too: when the database is down the instance default is a fine look.
        (string? ThemeMode, string? ThemeScheme, string? ThemeAccent)? own;
        try
        {
            var row = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.ThemeMode, u.ThemeScheme, u.ThemeAccent })
                .FirstOrDefaultAsync(ct);
            own = row is null ? null : (row.ThemeMode, row.ThemeScheme, row.ThemeAccent);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return fallback;
        }

        if (own is null)
        {
            return fallback;
        }

        var (mode, scheme, accent) = own.Value;
        return new ThemeChoice(
            ThemeCatalog.IsMode(mode) ? mode! : fallback.Mode,
            ThemeCatalog.IsScheme(scheme) ? scheme! : fallback.Scheme,
            ThemeCatalog.IsAccent(accent) ? accent! : fallback.Accent,
            UserHasMode: ThemeCatalog.IsMode(mode));
    }
}
