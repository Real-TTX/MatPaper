using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Services;

/// <summary>How a notice reads: a result, a hint, a warning or a failure.</summary>
public enum NoticeKind
{
    Ok,
    Info,
    Warn,
    Danger
}

/// <summary>
/// One convention for the short "what just happened" message above a page. Page models call
/// <see cref="Notify"/> (survives the redirect) or <see cref="NotifyNow"/> (same request);
/// <c>_Layout</c> renders it through <c>Pages/Shared/_Notice.cshtml</c>. Field validation
/// stays with <c>asp-validation-summary</c> — a notice is never an input error.
/// </summary>
public static class NoticeExtensions
{
    public const string TextKey = "Notice";
    public const string KindKey = "NoticeKind";

    /// <summary>Shows the message after the next redirect.</summary>
    public static void Notify(this PageModel page, string? text, NoticeKind kind = NoticeKind.Ok)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        page.TempData[TextKey] = text;
        page.TempData[KindKey] = kind.ToString();
    }

    /// <summary>Shows the message on the page that is rendered right now (no redirect).</summary>
    public static void NotifyNow(this PageModel page, string? text, NoticeKind kind = NoticeKind.Ok)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        page.ViewData[TextKey] = text;
        page.ViewData[KindKey] = kind.ToString();
    }

    /// <summary>Convenience for "worked / did not work" results.</summary>
    public static void Notify(this PageModel page, bool ok, string? text)
        => page.Notify(text, ok ? NoticeKind.Ok : NoticeKind.Danger);

    public static void NotifyNow(this PageModel page, bool ok, string? text)
        => page.NotifyNow(text, ok ? NoticeKind.Ok : NoticeKind.Danger);
}
