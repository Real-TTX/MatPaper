using System.Text.RegularExpressions;

namespace MatPaper.Services;

/// <summary>
/// Proposes a name for a correspondent that is not in the list yet, from the text of the document:
/// a letterhead line with a company form (GmbH, AG ...), the return-address line above the recipient,
/// or the web / mail domain. Pure text heuristics - the user picks one, nothing is created on its own.
/// </summary>
public static class CorrespondentSuggester
{
    private static readonly Regex LegalForm = new(
        @"\b(GmbH(\s*&\s*Co\.?\s*KG)?|mbH|AG|SE|UG(\s*\(haftungsbeschr[aä]nkt\))?|KGaA|KG|OHG|GbR|e\.\s?V\.?|eG|Ltd\.?|LLC|Inc\.?|S\.A\.|B\.V\.|N\.V\.)(?![\p{L}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Separator = new(@"\s[·|•–—]\s|\s-\s|,\s*(?=\p{L}.*\d)|\s{3,}", RegexOptions.Compiled);
    private static readonly Regex Domain = new(@"(?:www\.|@)([a-z0-9][a-z0-9-]{1,40})\.(?:de|com|net|org|eu|at|ch|info|io)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Plz = new(@"\b\d{5}\s+\p{L}", RegexOptions.Compiled);

    private static readonly HashSet<string> MailProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail", "googlemail", "outlook", "hotmail", "web", "gmx", "yahoo", "icloud", "t-online", "freenet", "live", "me", "aol", "proton", "protonmail"
    };

    public static IReadOnlyList<string> Suggest(string? text, int max = 4)
    {
        var result = new List<string>();
        void Add(string? candidate)
        {
            var cleaned = Clean(candidate);
            if (cleaned is not null && !result.Any(r => r.Equals(cleaned, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(cleaned);
            }
        }

        var lines = (text ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 2)
            .Take(40)
            .ToList();

        // 1) A line with a company form, nearest the top of the page first.
        foreach (var line in lines.Take(30))
        {
            var match = LegalForm.Match(line);
            if (!match.Success) { continue; }

            // Keep the name up to and including the form; drop address pieces that follow.
            var head = line[..(match.Index + match.Length)];
            var parts = Separator.Split(head);
            var name = parts.LastOrDefault(p => LegalForm.IsMatch(p)) ?? head;
            Add(name);
        }

        // 2) The sender's return-address line: "Firma · Straße 1 · 12345 Ort".
        foreach (var line in lines.Take(25))
        {
            if (!Plz.IsMatch(line)) { continue; }
            var parts = Separator.Split(line);
            if (parts.Length >= 2) { Add(parts[0]); }
        }

        // 3) Domain of a web or mail address, except the big mail providers.
        foreach (var line in lines)
        {
            foreach (Match m in Domain.Matches(line))
            {
                var host = m.Groups[1].Value;
                if (MailProviders.Contains(host)) { continue; }
                Add(char.ToUpperInvariant(host[0]) + host[1..]);
            }
        }

        return result.Take(max).ToList();
    }

    /// <summary>Lower case, without legal form, punctuation and extra spaces: "Stadtwerke Köln GmbH" and "stadtwerke köln" are the same.</summary>
    public static string Normalize(string? name)
    {
        var s = LegalForm.Replace(name ?? string.Empty, " ");
        s = Regex.Replace(s, @"[^\p{L}\p{N}]+", " ").Trim().ToLowerInvariant();
        return Regex.Replace(s, @"\s+", " ");
    }

    public static bool SameName(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        return x.Length >= 3 && x == y;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var s = Regex.Replace(value, @"\s+", " ").Trim(' ', ',', ';', ':', '.', '-', '|', '·', '•');
        if (s.Length < 3 || s.Length > 60) { return null; }
        if (!s.Any(char.IsLetter)) { return null; }
        // Lines that are mostly digits (invoice numbers, dates) are no names.
        if (s.Count(char.IsDigit) > s.Length / 3) { return null; }
        return s;
    }
}
