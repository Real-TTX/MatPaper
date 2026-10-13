using AngleSharp.Html.Parser;
using MatPaper.Services;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace MatPaper.Controls;

/// <summary>
/// The table of a list as the toolkit has it (rules TABLE-01 to TABLE-04): <c>&lt;div class="mat-table-wrap"&gt;&lt;table class="mat-table"&gt;…&lt;/table&gt;&lt;/div&gt;</c>
/// around the &lt;thead&gt;/&lt;tbody&gt; given as child content. Under 720 px a row becomes a card and every <c>td</c> needs <c>data-label</c> with the name of its
/// column (TABLE-02): a cell that has none gets the text of the heading of its column here, so the pages do not repeat it. A cell that must not show a label on a
/// phone (the actions, a thumbnail) says <c>data-label=""</c>; a cell with the class <c>mat-table__cell-actions</c> gets none.
/// When <c>item-count</c> is 0 an empty state (<c>mat-empty</c>) carrying <c>empty-text</c> is rendered instead of the table.
/// Numbers: <c>class="mat-num"</c> on the th and td right-aligns them; IDs and dates use <c>mat-mono</c> (TABLE-01).
/// Usage: &lt;mp-table item-count="Model.Rows.Count" empty-text="No records found." caption="Users"&gt;&lt;thead&gt;…&lt;/mp-table&gt;
/// </summary>
[HtmlTargetElement("mp-table")]
public sealed class TableTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public TableTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    /// <summary>Number of rows about to be rendered. 0 switches to the empty state.</summary>
    [HtmlAttributeName("item-count")]
    public int ItemCount { get; set; }

    /// <summary>Message shown in the empty state when <c>item-count</c> is 0.</summary>
    [HtmlAttributeName("empty-text")]
    public string? EmptyText { get; set; }

    /// <summary>The icon of the empty state.</summary>
    [HtmlAttributeName("empty-icon")]
    public string EmptyIcon { get; set; } = "search";

    /// <summary>Names the table for screen readers (a caption that is not drawn).</summary>
    [HtmlAttributeName("caption")]
    public string? Caption { get; set; }

    /// <summary>Extra classes of the table: <c>mat-table--compact</c>, <c>mat-table--zebra</c>, <c>mat-table--bordered</c>.</summary>
    [HtmlAttributeName("table-class")]
    public string? TableClass { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagMode = TagMode.StartTagAndEndTag;

        if (ItemCount <= 0)
        {
            output.TagName = "div";
            output.Attributes.SetAttribute("class", "mat-empty");
            output.Content.SetHtmlContent(
                $"<span class=\"mat-empty__icon\">{Ui.IconHtml(EmptyIcon)}</span>" +
                $"<div class=\"mat-empty__title\">{Ui.Encode(EmptyText ?? _l["No records found."].Value)}</div>");
            return;
        }

        TagHelperContent children = await output.GetChildContentAsync();
        output.TagName = "div";
        output.Attributes.SetAttribute("class", Classes.Merge("mat-table-wrap", output.Attributes["class"]?.Value?.ToString()));
        output.Content.SetHtmlContent($"<table class=\"{Classes.Merge("mat-table", TableClass)}\">");
        if (!string.IsNullOrEmpty(Caption))
        {
            output.Content.AppendHtml($"<caption class=\"mat-sr-only\">{Ui.Encode(Caption)}</caption>");
        }

        output.Content.AppendHtml(AddLabels(children.GetContent()));
        output.Content.AppendHtml("</table>");
    }

    /// <summary>Gives every cell without a <c>data-label</c> the heading of its column (columns spanned by a colspan count as many).</summary>
    internal static string AddLabels(string rows)
    {
        if (!rows.Contains("<td", StringComparison.OrdinalIgnoreCase))
        {
            return rows;
        }

        var document = new HtmlParser().ParseDocument($"<!DOCTYPE html><html><body><table>{rows}</table></body></html>");
        var table = document.QuerySelector("table");
        if (table is null)
        {
            return rows;
        }

        var labels = new List<string>();
        var head = table.QuerySelector("thead tr");
        if (head is not null)
        {
            foreach (var cell in head.Children.Where(c => c.LocalName is "th" or "td"))
            {
                var text = (cell.TextContent ?? string.Empty).Trim();
                for (var i = 0; i < Span(cell); i++)
                {
                    labels.Add(text);
                }
            }
        }

        foreach (var row in table.QuerySelectorAll("tbody tr"))
        {
            var column = 0;
            foreach (var cell in row.Children.Where(c => c.LocalName is "td" or "th"))
            {
                if (cell.LocalName == "td" && !cell.HasAttribute("data-label") && !cell.ClassList.Contains("mat-table__cell-actions"))
                {
                    cell.SetAttribute("data-label", column < labels.Count ? labels[column] : string.Empty);
                }

                column += Span(cell);
            }
        }

        return table.InnerHtml;
    }

    private static int Span(AngleSharp.Dom.IElement cell)
        => int.TryParse(cell.GetAttribute("colspan"), out var n) && n > 1 ? n : 1;
}
