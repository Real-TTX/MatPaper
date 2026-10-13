using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;
using MatPaper.Services;

namespace MatPaper.Controls;

/// <summary>
/// Tabs that are links (the pages of one area that belong together, such as the tabs of the account): <c>&lt;nav class="mat-tabs"&gt;</c> holding
/// &lt;mp-tab&gt; links; the current one carries <c>aria-current</c>. Tabs that switch panels of one page are the toolkit's <c>data-tabs</c> markup.
/// Usage: &lt;mp-tabbar label="Account"&gt; …&lt;mp-tab&gt;… &lt;/mp-tabbar&gt;
/// </summary>
[HtmlTargetElement("mp-tabbar")]
public sealed class TabBarTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public TabBarTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    /// <summary>Names the tab bar for screen readers.</summary>
    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Classes.Merge("mat-tabs", output.Attributes["class"]?.Value?.ToString()));
        output.Attributes.SetAttribute("aria-label", Label ?? _l["Sections"].Value);
    }
}

/// <summary>
/// A single tab: renders &lt;a class="mat-tab [is-active]" href="…"&gt;. The tab label comes from the <c>title</c> attribute, or from the child
/// content when title is omitted.
/// Usage: &lt;mp-tab title="Details" href="/x" active="true" /&gt;
/// </summary>
[HtmlTargetElement("mp-tab")]
public sealed class TabTagHelper : TagHelper
{
    [HtmlAttributeName("title")]
    public string? Title { get; set; }

    [HtmlAttributeName("href")]
    public string Href { get; set; } = "#";

    [HtmlAttributeName("active")]
    public bool Active { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "a";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Active ? "mat-tab is-active" : "mat-tab");
        output.Attributes.SetAttribute("href", Href);
        if (Active)
        {
            output.Attributes.SetAttribute("aria-current", "page");
        }

        if (!string.IsNullOrEmpty(Title))
        {
            output.Content.SetContent(Title);
        }
        else
        {
            output.Content.SetHtmlContent(await output.GetChildContentAsync());
        }
    }
}
