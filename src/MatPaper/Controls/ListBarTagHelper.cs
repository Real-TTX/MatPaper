using System.Globalization;
using System.Text.Encodings.Web;
using MatPaper.Services;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace MatPaper.Controls;

/// <summary>
/// The pager and the count of a list. Placed twice on a page:
/// <list type="bullet">
/// <item>Above the entries (the default) it is the line between the toolbar and the first entry: the pager on the
/// left, what the list holds on the right ("12 documents"), and anything inside the tag - a view switch - after that.
/// A phone does not show it.</item>
/// <item><c>position="bottom"</c> closes the list. It repeats what the top one was given, so it takes no attributes. A
/// wide screen shows the pager only; a phone shows the count above the pager, because that is where the top
/// line is missing.</item>
/// </list>
/// Both render nothing for an empty list; the page shows its empty state instead.
/// <c>singular</c> and <c>plural</c> are resource keys with the number as {0} ("{0} document").
/// Usage: &lt;mp-list-bar count="Model.TotalCount" singular="{0} user" plural="{0} users"
/// current-page="Model.PageNumber" total-pages="Model.TotalPages" /&gt;, then &lt;mp-list-bar position="bottom" /&gt;
/// </summary>
[HtmlTargetElement("mp-list-bar")]
public sealed class ListBarTagHelper : TagHelper
{
    private const string StateKey = "mp:list-bar";

    /// <summary>What the top bar was given, kept for the bottom one.</summary>
    private sealed record State(int Count, string Text, int CurrentPage, int TotalPages, string PageParam);

    private readonly IStringLocalizer<SharedResource> _l;

    public ListBarTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    /// <summary>"top" (default) or "bottom".</summary>
    [HtmlAttributeName("position")]
    public string Position { get; set; } = "top";

    /// <summary>How many entries match, over all pages.</summary>
    [HtmlAttributeName("count")]
    public int Count { get; set; }

    [HtmlAttributeName("singular")]
    public string Singular { get; set; } = "{0} result";

    [HtmlAttributeName("plural")]
    public string Plural { get; set; } = "{0} results";

    [HtmlAttributeName("current-page")]
    public int CurrentPage { get; set; } = 1;

    /// <summary>Omit for a list that is not paged.</summary>
    [HtmlAttributeName("total-pages")]
    public int TotalPages { get; set; } = 1;

    [HtmlAttributeName("page-param")]
    public string PageParam { get; set; } = "PageNumber";

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (string.Equals(Position, "bottom", StringComparison.OrdinalIgnoreCase))
        {
            RenderBottom(output);
            return;
        }

        TagHelperContent extra = await output.GetChildContentAsync();
        string number = Count.ToString("N0", CultureInfo.CurrentCulture);
        string text = _l[Count == 1 ? Singular : Plural, number].Value;
        ViewContext.ViewData[StateKey] = new State(Count, text, CurrentPage, TotalPages, PageParam);

        if (Count <= 0 && extra.IsEmptyOrWhiteSpace)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "list-bar");
        output.Content.Clear();

        // An empty list keeps only what belongs to the view (the switch): the page shows its empty state.
        if (Count <= 0)
        {
            output.Content.AppendHtml(extra);
            return;
        }

        AppendPager(output, CurrentPage, TotalPages, PageParam);
        output.Content.AppendHtml($"<span class=\"list-bar__count\">{HtmlEncoder.Default.Encode(text)}</span>");
        output.Content.AppendHtml(extra);
    }

    private void RenderBottom(TagHelperOutput output)
    {
        if (ViewContext.ViewData[StateKey] is not State state || state.Count <= 0)
        {
            output.SuppressOutput();
            return;
        }

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        // Without a pager there is only the count, and a wide screen has that at the top.
        output.Attributes.SetAttribute("class", state.TotalPages > 1 ? "list-bar list-bar--end" : "list-bar list-bar--end list-bar--solo");
        output.Content.Clear();
        AppendPager(output, state.CurrentPage, state.TotalPages, state.PageParam);
        output.Content.AppendHtml($"<span class=\"list-bar__count\">{HtmlEncoder.Default.Encode(state.Text)}</span>");
    }

    private void AppendPager(TagHelperOutput output, int current, int total, string pageParam)
    {
        if (total <= 1)
        {
            return;
        }

        string label = HtmlEncoder.Default.Encode(_l["Pages"].Value);
        output.Content.AppendHtml($"<nav class=\"pagination\" aria-label=\"{label}\">");
        output.Content.AppendHtml(PageLinks.Render(ViewContext.HttpContext.Request.Query, current, total, pageParam, _l));
        output.Content.AppendHtml("</nav>");
    }
}

/// <summary>
/// Two buttons that switch a list between its gallery and its table. They are links: the page reads the choice from
/// the query string (and remembers it), so the switch works without a script. The page number is dropped, because
/// the two views do not hold the same number of entries per page. A phone has no list bar; there the choice sits in
/// the filter dialog (<see cref="ToolbarViewTagHelper"/>).
/// Usage: &lt;mp-view-switch current="@Model.View" /&gt;
/// </summary>
[HtmlTargetElement("mp-view-switch", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ViewSwitchTagHelper : TagHelper
{
    private const string Svg = "<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.9\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">";
    internal const string GalleryIcon = Svg + "<rect x=\"3\" y=\"3\" width=\"7\" height=\"7\" rx=\"1.5\"/><rect x=\"14\" y=\"3\" width=\"7\" height=\"7\" rx=\"1.5\"/><rect x=\"3\" y=\"14\" width=\"7\" height=\"7\" rx=\"1.5\"/><rect x=\"14\" y=\"14\" width=\"7\" height=\"7\" rx=\"1.5\"/></svg>";
    internal const string ListIcon = Svg + "<path d=\"M8 6h13M8 12h13M8 18h13\"/><path d=\"M3 6h.01M3 12h.01M3 18h.01\"/></svg>";

    private readonly IStringLocalizer<SharedResource> _l;

    public ViewSwitchTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    /// <summary>"gallery" or "list".</summary>
    [HtmlAttributeName("current")]
    public string Current { get; set; } = "gallery";

    [HtmlAttributeName("param")]
    public string Param { get; set; } = "View";

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "view-switch");
        output.Attributes.SetAttribute("role", "group");
        output.Attributes.SetAttribute("aria-label", _l["Display as"].Value);

        output.Content.SetHtmlContent(
            Button("gallery", _l["Gallery"].Value, GalleryIcon) +
            Button("list", _l["List"].Value, ListIcon));
    }

    private string Button(string value, string label, string icon)
    {
        bool active = string.Equals(Current, value, StringComparison.OrdinalIgnoreCase);
        string href = HtmlEncoder.Default.Encode(PageLinks.With(ViewContext.HttpContext.Request.Query, Param, value, "PageNumber"));
        string text = HtmlEncoder.Default.Encode(label);
        return $"<a class=\"view-switch__btn{(active ? " is-active" : string.Empty)}\" href=\"{href}\" aria-label=\"{text}\" title=\"{text}\" aria-pressed=\"{(active ? "true" : "false")}\">{icon}</a>";
    }
}

/// <summary>
/// The gallery / list choice as a field of a toolbar: two segments in the filter dialog, applied with the filters
/// (the page keeps the choice in a cookie). A wide screen has the switch in the list bar and does not show the field;
/// it is no filter, so the count on the funnel button leaves it out.
/// Usage: &lt;mp-toolbar-view name="View" value="@Model.View" /&gt;
/// </summary>
[HtmlTargetElement("mp-toolbar-view", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ToolbarViewTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public ToolbarViewTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    /// <summary>Query-string parameter name.</summary>
    [HtmlAttributeName("name")]
    public string Name { get; set; } = "View";

    /// <summary>"gallery" or "list".</summary>
    [HtmlAttributeName("value")]
    public string Value { get; set; } = "gallery";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "toolbar__group toolbar__filter toolbar__view");
        output.Attributes.SetAttribute("data-badge", "off");

        string name = HtmlEncoder.Default.Encode(Name);
        string title = HtmlEncoder.Default.Encode(_l["Display as"].Value);
        output.Content.SetHtmlContent(
            $"<span class=\"toolbar__view-title\" id=\"{name}_title\">{title}</span>" +
            $"<div class=\"view-choice\" role=\"radiogroup\" aria-labelledby=\"{name}_title\">" +
            Option(name, "gallery", _l["Gallery"].Value, ViewSwitchTagHelper.GalleryIcon) +
            Option(name, "list", _l["List"].Value, ViewSwitchTagHelper.ListIcon) +
            "</div>");
    }

    private string Option(string name, string value, string label, string icon)
    {
        string checkedAttr = string.Equals(Value, value, StringComparison.OrdinalIgnoreCase) ? " checked" : string.Empty;
        return $"<label class=\"view-choice__opt\"><input type=\"radio\" name=\"{name}\" value=\"{value}\"{checkedAttr} />{icon}<span>{HtmlEncoder.Default.Encode(label)}</span></label>";
    }
}
