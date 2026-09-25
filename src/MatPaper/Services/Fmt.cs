using System.Globalization;
using MatPaper.Configuration;

namespace MatPaper.Services;

/// <summary>
/// Formats the values the UI shows: timestamps are stored in UTC and rendered in the
/// configured time zone (see <see cref="DisplayConfig"/>), dates as dd.MM.yyyy and file
/// sizes in human units. Inject it into a page or view (<c>@inject MatPaper.Services.Fmt Fmt</c>)
/// instead of calling ToString on a DateTime.
/// </summary>
public sealed class Fmt
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("de-DE");

    public Fmt(AppConfig config)
    {
        var id = Environment.GetEnvironmentVariable("MATPAPER_TZ");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = config.Display?.TimeZone;
        }

        TimeZone = Resolve(id);
        ZoneName = TimeZone.Id;
    }

    /// <summary>The zone every displayed time is converted to and schedules run in.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Short zone label for the UI, e.g. "Europe/Berlin".</summary>
    public string ZoneName { get; }

    private static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Converts a stored (UTC) timestamp into the display zone.</summary>
    public System.DateTime ToLocal(System.DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(System.DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);

    /// <summary>25.09.2026 — or the placeholder when there is no value.</summary>
    public string Date(System.DateTime? utc, string empty = "—")
        => utc is null ? empty : ToLocal(utc.Value).ToString("dd.MM.yyyy", Culture);

    /// <summary>25.09.2026 14:32</summary>
    public string DateTime(System.DateTime? utc, string empty = "—")
        => utc is null ? empty : ToLocal(utc.Value).ToString("dd.MM.yyyy HH:mm", Culture);

    /// <summary>25.09.2026 14:32:07 — for logs and task runs.</summary>
    public string DateTimeSeconds(System.DateTime? utc, string empty = "—")
        => utc is null ? empty : ToLocal(utc.Value).ToString("dd.MM.yyyy HH:mm:ss", Culture);

    /// <summary>yyyy-MM-dd in the display zone — the format &lt;input type="date"&gt; expects.</summary>
    public string InputDate(System.DateTime? utc)
        => utc is null ? string.Empty : ToLocal(utc.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>1,4 MB / 812 KB / 512 B</summary>
    public string Size(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return (bytes / (1024d * 1024 * 1024)).ToString("0.#", Culture) + " GB";
        }
        if (bytes >= 1024 * 1024)
        {
            return (bytes / (1024d * 1024)).ToString("0.#", Culture) + " MB";
        }
        if (bytes >= 1024)
        {
            return (bytes / 1024d).ToString("0.#", Culture) + " KB";
        }

        return bytes + " B";
    }

    /// <summary>Counts with a thousands separator: 1.234</summary>
    public string Count(long value) => value.ToString("N0", Culture);
}
