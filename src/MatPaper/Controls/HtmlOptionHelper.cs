using System.Text.RegularExpressions;

namespace MatPaper.Controls;

/// <summary>
/// Shared helper that marks the &lt;option&gt; whose <c>value</c> attribute equals a
/// given selected value with a <c>selected</c> attribute. Used by the toolbar select
/// and the select-kind form field when the page supplies raw &lt;option&gt; children.
/// Options that should be selectable this way must carry an explicit value attribute.
/// </summary>
internal static class HtmlOptionHelper
{
    private static readonly Regex OptionTag =
        new("<option\\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ValueAttr =
        new("value\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlreadySelected =
        new("(^|\\s)selected(\\s|=|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string MarkSelected(string optionsHtml, string? selectedValue)
    {
        if (string.IsNullOrEmpty(optionsHtml) || selectedValue is null)
        {
            return optionsHtml;
        }

        return OptionTag.Replace(optionsHtml, match =>
        {
            string attributes = match.Groups[1].Value;
            if (AlreadySelected.IsMatch(attributes))
            {
                return match.Value;
            }

            Match value = ValueAttr.Match(attributes);
            if (value.Success && value.Groups[1].Value == selectedValue)
            {
                return $"<option{attributes} selected>";
            }

            return match.Value;
        });
    }
}
