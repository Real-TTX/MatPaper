using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// Renders a <c>.pagination</c> bar with a <c>.pagination__link</c> for every page
/// 1..total-pages, marking the current page <c>.is-active</c>. Every existing
/// query-string parameter is preserved; only the page parameter is swapped.
/// Renders nothing when there is one page or fewer.
/// Usage: &lt;mp-pagination current-page="Model.PageNumber" total-pages="Model.TotalPages" /&gt;
/// </summary>
[HtmlTargetElement("mp-pagination", TagStructure = TagStructure.WithoutEndTag)]
public sealed class PaginationTagHelper : TagHelper
{
    [HtmlAttributeName("current-page")]
    public int CurrentPage { get; set; } = 1;

    [HtmlAttributeName("total-pages")]
    public int TotalPages { get; set; }

    /// <summary>Query-string parameter that carries the page number. Default "PageNumber".</summary>
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

        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "pagination");

        IQueryCollection query = ViewContext.HttpContext.Request.Query;
        var content = new StringBuilder();
        for (int page = 1; page <= TotalPages; page++)
        {
            string active = page == CurrentPage ? " is-active" : string.Empty;
            string href = BuildHref(query, PageParam, page);
            content.Append($"<a class=\"pagination__link{active}\" href=\"{href}\">{page}</a>");
        }

        output.Content.SetHtmlContent(content.ToString());
    }

    private static string BuildHref(IQueryCollection query, string pageParam, int page)
    {
        var sb = new StringBuilder("?");
        bool first = true;
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> kv in query)
        {
            if (string.Equals(kv.Key, pageParam, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string? value in kv.Value)
            {
                if (!first)
                {
                    sb.Append('&');
                }

                sb.Append(UrlEncoder.Default.Encode(kv.Key))
                  .Append('=')
                  .Append(UrlEncoder.Default.Encode(value ?? string.Empty));
                first = false;
            }
        }

        if (!first)
        {
            sb.Append('&');
        }

        sb.Append(UrlEncoder.Default.Encode(pageParam)).Append('=').Append(page);
        return sb.ToString();
    }
}
