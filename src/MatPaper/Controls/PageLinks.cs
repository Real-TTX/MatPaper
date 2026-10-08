using System.Text;
using System.Text.Encodings.Web;
using MatPaper.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;

namespace MatPaper.Controls;

/// <summary>
/// The links of a pager and the query strings the page controls of a list build: first and previous page, always
/// three numbered pages (the current one in the middle where there is room), next and last page. A step that leads
/// nowhere (previous on the first page ...) is shown dimmed. Every existing query-string parameter is kept, only the
/// page parameter is swapped.
/// </summary>
internal static class PageLinks
{
    private const string Svg = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">";
    private const string FirstIcon = Svg + "<path d=\"M11 17l-5-5 5-5\"/><path d=\"M18 17l-5-5 5-5\"/></svg>";
    private const string PreviousIcon = Svg + "<path d=\"M15 18l-6-6 6-6\"/></svg>";
    private const string NextIcon = Svg + "<path d=\"M9 18l6-6-6-6\"/></svg>";
    private const string LastIcon = Svg + "<path d=\"M13 17l5-5-5-5\"/><path d=\"M6 17l5-5-5-5\"/></svg>";

    public static string Render(IQueryCollection query, int current, int total, string pageParam, IStringLocalizer<SharedResource> l)
    {
        current = Math.Clamp(current, 1, Math.Max(1, total));

        // Three numbers, the current one in the middle; at either end the window sits against the edge.
        int first = Math.Clamp(current - 1, 1, Math.Max(1, total - 2));
        int last = Math.Min(total, first + 2);

        var sb = new StringBuilder();
        Step(sb, query, pageParam, 1, current > 1, l["First page"].Value, FirstIcon);
        Step(sb, query, pageParam, current - 1, current > 1, l["Previous page"].Value, PreviousIcon);
        for (int page = first; page <= last; page++)
        {
            string href = HtmlEncoder.Default.Encode(With(query, pageParam, page.ToString()));
            if (page == current)
            {
                sb.Append($"<a class=\"pagination__link is-active\" href=\"{href}\" aria-current=\"page\">{page}</a>");
            }
            else
            {
                sb.Append($"<a class=\"pagination__link\" href=\"{href}\">{page}</a>");
            }
        }
        Step(sb, query, pageParam, current + 1, current < total, l["Next page"].Value, NextIcon);
        Step(sb, query, pageParam, total, current < total, l["Last page"].Value, LastIcon);
        return sb.ToString();
    }

    private static void Step(StringBuilder sb, IQueryCollection query, string pageParam, int page, bool enabled, string label, string icon)
    {
        string text = HtmlEncoder.Default.Encode(label);
        if (enabled)
        {
            string href = HtmlEncoder.Default.Encode(With(query, pageParam, page.ToString()));
            sb.Append($"<a class=\"pagination__link pagination__link--step\" href=\"{href}\" aria-label=\"{text}\" title=\"{text}\">{icon}</a>");
        }
        else
        {
            sb.Append($"<span class=\"pagination__link pagination__link--step is-disabled\" aria-hidden=\"true\">{icon}</span>");
        }
    }

    /// <summary>
    /// The current query string with one parameter set to a value. Parameters named in <paramref name="drop"/> are
    /// left out (the page number, when a control changes what the pages are made of).
    /// </summary>
    public static string With(IQueryCollection query, string key, string value, params string[] drop)
    {
        var sb = new StringBuilder("?");
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> kv in query)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)
                || drop.Any(d => string.Equals(kv.Key, d, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (string? v in kv.Value)
            {
                sb.Append(UrlEncoder.Default.Encode(kv.Key))
                  .Append('=')
                  .Append(UrlEncoder.Default.Encode(v ?? string.Empty))
                  .Append('&');
            }
        }

        sb.Append(UrlEncoder.Default.Encode(key)).Append('=').Append(UrlEncoder.Default.Encode(value));
        return sb.ToString();
    }
}
