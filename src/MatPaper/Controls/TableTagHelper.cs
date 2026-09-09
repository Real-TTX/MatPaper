using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// Renders <c>&lt;div class="data-table-wrap"&gt;&lt;table class="data-table"&gt;…&lt;/table&gt;&lt;/div&gt;</c>
/// around the &lt;thead&gt;/&lt;tbody&gt; provided as child content. When <c>item-count</c> is 0 a
/// <c>.empty-state</c> carrying <c>empty-text</c> is rendered instead of the table.
/// Right-align an actions column by adding class="is-actions" to its &lt;th&gt;/&lt;td&gt;.
/// Usage: &lt;mp-table item-count="Model.Rows.Count" empty-text="No records found."&gt;&lt;thead&gt;…&lt;/mp-table&gt;
/// </summary>
[HtmlTargetElement("mp-table")]
public sealed class TableTagHelper : TagHelper
{
    /// <summary>Number of rows about to be rendered. 0 switches to the empty state.</summary>
    [HtmlAttributeName("item-count")]
    public int ItemCount { get; set; }

    /// <summary>Message shown in the empty state when <c>item-count</c> is 0.</summary>
    [HtmlAttributeName("empty-text")]
    public string EmptyText { get; set; } = "No records found.";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;

        if (ItemCount <= 0)
        {
            output.Attributes.SetAttribute("class", "empty-state");
            output.Content.SetContent(EmptyText);
            return;
        }

        output.Attributes.SetAttribute("class", "data-table-wrap");
        TagHelperContent children = await output.GetChildContentAsync();
        output.Content.SetHtmlContent("<table class=\"data-table\">");
        output.Content.AppendHtml(children);
        output.Content.AppendHtml("</table>");
    }
}
