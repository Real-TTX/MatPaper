using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace MatPaper.Services;

/// <summary>One active filter shown as a removable chip in a list toolbar.</summary>
public record FilterChip(string Label, string RemoveUrl);

/// <summary>
/// A filter a list page offers: the query-string key, the label to show, the current
/// value (already display-ready — a name, not an id) and the value that counts as
/// "not filtered" (e.g. "all").
/// </summary>
public record FilterSpec(string Key, string Label, string? Value, string? Default = null);

/// <summary>
/// The set of active filters for a list/gallery page. Rendered by the shared
/// <c>_FilterChips</c> partial so every list uses the same model: a chip per
/// active, non-default filter (each removes just itself) plus a "Clear all".
/// </summary>
public record FilterChipBar(IReadOnlyList<FilterChip> Chips, string ClearAllUrl)
{
    public bool Any => Chips.Count > 0;

    public static FilterChipBar Empty { get; } = new(Array.Empty<FilterChip>(), string.Empty);

    /// <summary>
    /// Builds the bar for the simple case most list pages have: a handful of
    /// single-valued filters. Entries whose value is empty or equals the default
    /// ("all", "name_asc", …) are skipped, so only real filters show up.
    /// </summary>
    public static FilterChipBar From(HttpRequest request, params FilterSpec[] filters)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chips = new List<FilterChip>();
        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter.Value)
                || string.Equals(filter.Value, filter.Default, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            chips.Add(new FilterChip($"{filter.Label}: {filter.Value}", FilterUrl.Without(request, filter.Key)));
        }

        return chips.Count == 0 ? Empty : new FilterChipBar(chips, FilterUrl.ClearAll(request));
    }
}

/// <summary>
/// Builds URLs for the current request with a filter removed, so chips can link
/// to "the same page minus this filter". Paging is always reset.
/// </summary>
public static class FilterUrl
{
    private static string Build(HttpRequest req, IEnumerable<KeyValuePair<string, StringValues>> query)
    {
        var qs = QueryString.Empty;
        foreach (var kv in query)
        {
            foreach (var v in kv.Value)
            {
                if (!string.IsNullOrEmpty(v))
                {
                    qs = qs.Add(kv.Key, v);
                }
            }
        }

        return req.PathBase + req.Path + qs.ToString();
    }

    /// <summary>Current URL without the given keys (and without paging).</summary>
    public static string Without(HttpRequest req, params string[] keys)
        => Build(req, req.Query.Where(kv => !keys.Contains(kv.Key) && kv.Key != "PageNumber"));

    /// <summary>Current URL with a single value removed from a multi-valued key.</summary>
    public static string WithoutValue(HttpRequest req, string key, string value)
    {
        var list = new List<KeyValuePair<string, StringValues>>();
        foreach (var kv in req.Query)
        {
            if (kv.Key == "PageNumber")
            {
                continue;
            }

            if (kv.Key == key)
            {
                var remaining = kv.Value.Where(v => v != value).ToArray();
                if (remaining.Length > 0)
                {
                    list.Add(new KeyValuePair<string, StringValues>(key, remaining));
                }
            }
            else
            {
                list.Add(kv);
            }
        }

        return Build(req, list);
    }

    public static string ClearAll(HttpRequest req) => req.PathBase + req.Path;
}
