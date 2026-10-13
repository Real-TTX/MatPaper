using System.Globalization;
using System.Net;
using System.Text;
using MatPaper.Services;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace MatPaper.Controls;

/// <summary>
/// The pagination of a list (rule LIST-06): directly under the table or the gallery, nothing in between. It renders nothing for a single page (CODE-06).
/// The window shows the first and the last page and two on each side of the current one; the line at its left says where the page is in the list
/// ("51 to 75 of 190", or "Page 3 of 8" when the page size is not given). Every other parameter of the query string stays in the links; the page number
/// is read from the query string by the page and never bound as a route value.
/// Usage: &lt;mp-pager current-page="Model.PageNumber" total-pages="Model.TotalPages" total="Model.TotalCount" page-size="50" /&gt;
/// </summary>
[HtmlTargetElement("mp-pager", TagStructure = TagStructure.WithoutEndTag)]
public sealed class PagerTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public PagerTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    [HtmlAttributeName("current-page")]
    public int CurrentPage { get; set; } = 1;

    [HtmlAttributeName("total-pages")]
    public int TotalPages { get; set; } = 1;

    /// <summary>How many entries match, over all pages.</summary>
    [HtmlAttributeName("total")]
    public int? Total { get; set; }

    /// <summary>How many entries a page holds.</summary>
    [HtmlAttributeName("page-size")]
    public int? PageSize { get; set; }

    /// <summary>
    /// How many entries this page shows: enough to say "51 to 75 of 190" without knowing the page size (every page but the last is full, and the last one
    /// ends at the total).
    /// </summary>
    [HtmlAttributeName("row-count")]
    public int? RowCount { get; set; }

    [HtmlAttributeName("page-param")]
    public string PageParam { get; set; } = "PageNumber";

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (TotalPages <= 1)
        {
            output.SuppressOutput();
            return;
        }

        int current = Math.Clamp(CurrentPage, 1, TotalPages);
        var query = ViewContext.HttpContext.Request.Query;
        string Href(int page) => WebUtility.HtmlEncode(PageLinks.With(query, PageParam, page.ToString(CultureInfo.InvariantCulture)));

        string info = _l["Page {0} of {1}", current, TotalPages].Value;
        if (Total is int total)
        {
            int? first = null, last = null;
            if (PageSize is int size && size > 0)
            {
                first = (current - 1) * size + 1;
                last = Math.Min(total, current * size);
            }
            else if (RowCount is int rows && rows > 0)
            {
                // the pages before this one are full; the last page ends at the total
                int before = current == TotalPages ? total - rows : (current - 1) * rows;
                first = before + 1;
                last = before + rows;
            }

            if (first is int f && last is int l)
            {
                info = _l["{0} to {1} of {2}", f.ToString("N0", CultureInfo.CurrentCulture), l.ToString("N0", CultureInfo.CurrentCulture), total.ToString("N0", CultureInfo.CurrentCulture)].Value;
            }
        }

        var sb = new StringBuilder();
        sb.Append($"<span class=\"mat-pager__info\">{WebUtility.HtmlEncode(info)}</span><div class=\"mat-pager__pages\">");
        if (current > 1)
        {
            sb.Append($"<a class=\"mat-pager__btn\" href=\"{Href(current - 1)}\" aria-label=\"{WebUtility.HtmlEncode(_l["Previous page"].Value)}\" rel=\"prev\">{Ui.IconHtml("chevron-left")}</a>");
        }

        foreach (int page in PageLinks.Window(current, TotalPages))
        {
            if (page == 0)
            {
                sb.Append("<span class=\"mat-pager__gap\" aria-hidden=\"true\">…</span>");
            }
            else if (page == current)
            {
                sb.Append($"<span class=\"mat-pager__btn is-current\" aria-current=\"page\">{page}</span>");
            }
            else
            {
                sb.Append($"<a class=\"mat-pager__btn\" href=\"{Href(page)}\" aria-label=\"{WebUtility.HtmlEncode(_l["Page {0}", page].Value)}\">{page}</a>");
            }
        }

        if (current < TotalPages)
        {
            sb.Append($"<a class=\"mat-pager__btn\" href=\"{Href(current + 1)}\" aria-label=\"{WebUtility.HtmlEncode(_l["Next page"].Value)}\" rel=\"next\">{Ui.IconHtml("chevron-right")}</a>");
        }

        sb.Append("</div>");

        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-pager");
        output.Attributes.SetAttribute("aria-label", _l["Pagination"].Value);
        output.Content.SetHtmlContent(sb.ToString());
    }
}
