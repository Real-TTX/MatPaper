using Cronos;

namespace MatPaper.Services;

/// <summary>Human-friendly view of a cron schedule for the task UI.</summary>
public sealed record CronInfo(bool IsValid, string Text, DateTime? NextUtc);

/// <summary>
/// Turns the 5-field cron expressions stored on import/export tasks into a readable
/// description plus the next run time, and validates them. Times are interpreted in the
/// configured display time zone — the same zone <c>TaskSchedulerService</c> evaluates in —
/// so "daily at 08:00" means 08:00 wall-clock time, not 08:00 UTC.
/// </summary>
public static class CronSchedule
{
    /// <summary>A blank expression means "manual only" and is considered valid.</summary>
    public static bool IsValid(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return true;
        }

        try
        {
            CronExpression.Parse(cron.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static CronInfo Describe(string? cron, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (string.IsNullOrWhiteSpace(cron))
        {
            return new CronInfo(true, "Manual only", null);
        }

        var trimmed = cron.Trim();
        CronExpression expr;
        try
        {
            expr = CronExpression.Parse(trimmed);
        }
        catch
        {
            return new CronInfo(false, "Invalid schedule", null);
        }

        var next = expr.GetNextOccurrence(DateTime.UtcNow, zone);
        return new CronInfo(true, Humanize(trimmed), next);
    }

    private static string Humanize(string cron)
    {
        var f = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5)
        {
            return $"Cron: {cron}";
        }

        string min = f[0], hour = f[1], dom = f[2], mon = f[3], dow = f[4];
        var mOk = int.TryParse(min, out var m);
        var hOk = int.TryParse(hour, out var h);
        var domOk = int.TryParse(dom, out var dm);
        var dowOk = int.TryParse(dow, out var d);

        if (mon != "*")
        {
            return $"Cron: {cron}";
        }

        if (mOk && hour == "*" && dom == "*" && dow == "*")
        {
            return $"Hourly at minute {m}";
        }
        if (mOk && hOk && dom == "*" && dow == "*")
        {
            return $"Daily at {h:00}:{m:00}";
        }
        if (mOk && hOk && dom == "*" && dowOk)
        {
            return $"Weekly on {DayName(d)} at {h:00}:{m:00}";
        }
        if (mOk && hOk && domOk && dow == "*")
        {
            return $"Monthly on day {dm} at {h:00}:{m:00}";
        }

        return $"Cron: {cron}";
    }

    private static string DayName(int d) => (d % 7) switch
    {
        0 => "Sunday",
        1 => "Monday",
        2 => "Tuesday",
        3 => "Wednesday",
        4 => "Thursday",
        5 => "Friday",
        6 => "Saturday",
        _ => d.ToString()
    };
}
