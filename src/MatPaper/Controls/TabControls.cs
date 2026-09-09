using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// Renders a horizontal tab bar (<c>&lt;nav class="tabbar"&gt;</c>) containing
/// &lt;mp-tab&gt; links.
/// Usage: &lt;mp-tabbar&gt; …&lt;mp-tab&gt;… &lt;/mp-tabbar&gt;
/// </summary>
[HtmlTargetElement("mp-tabbar")]
public sealed class TabBarTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "nav";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "tabbar");
    }
}

/// <summary>
/// A single tab: renders &lt;a class="tabbar__tab [is-active]" href="…"&gt;. The tab label
/// comes from the <c>title</c> attribute, or from the child content when title is omitted.
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
        output.Attributes.SetAttribute("class", Active ? "tabbar__tab is-active" : "tabbar__tab");
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
