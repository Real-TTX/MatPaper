using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;

namespace MatPaper.Controls;

/// <summary>The query strings the page controls of a list build, and which page numbers a pager shows.</summary>
internal static class PageLinks
{
    /// <summary>
    /// The page numbers of a pager: the first and the last page and two on each side of the current one; <c>0</c> stands for a gap
    /// (<c>1 … 4 5 [6] 7 8 … 20</c>).
    /// </summary>
    public static IReadOnlyList<int> Window(int current, int total)
    {
        current = Math.Clamp(current, 1, Math.Max(1, total));
        var shown = new SortedSet<int> { 1, Math.Max(1, total) };
        for (int p = current - 2; p <= current + 2; p++)
        {
            if (p >= 1 && p <= total)
            {
                shown.Add(p);
            }
        }

        var result = new List<int>();
        int last = 0;
        foreach (int p in shown)
        {
            if (last != 0 && p - last > 1)
            {
                result.Add(0);
            }

            result.Add(p);
            last = p;
        }

        return result;
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
