using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// An icon of the toolkit's sprite: <c>&lt;mp-icon name="save" /&gt;</c>. The sprites are inlined once per page by the layout; the icon is a <c>use</c>
/// of its symbol (see <see cref="Ui"/>). Size and colour follow the text (<c>currentColor</c>). An icon next to a label is decoration and hidden from
/// screen readers; an icon alone needs a <c>title</c> (rule ICON-03).
/// </summary>
[HtmlTargetElement("mp-icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper : TagHelper
{
    /// <summary>Icon name (for example "server", "trash").</summary>
    [HtmlAttributeName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional accessible name; when omitted the icon is hidden from assistive tech.</summary>
    [HtmlAttributeName("title")]
    public string? Title { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Classes.Merge("mat-icon", output.Attributes["class"]?.Value?.ToString()));

        if (string.IsNullOrEmpty(Title))
        {
            output.Attributes.SetAttribute("aria-hidden", "true");
        }
        else
        {
            output.Attributes.SetAttribute("role", "img");
            output.Attributes.SetAttribute("aria-label", Title);
        }

        output.Attributes.SetAttribute("focusable", "false");
        output.Content.SetHtmlContent($"<use href=\"#{Ui.SymbolId(Name)}\"></use>");
    }
}
