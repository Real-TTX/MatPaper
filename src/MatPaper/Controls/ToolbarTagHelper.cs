using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// Wraps its children in a GET &lt;form class="toolbar"&gt; and appends an "Apply"
/// submit button (<c>.btn.btn--secondary</c>) inside its own <c>.toolbar__group</c>.
/// Drop &lt;mp-toolbar-search&gt; / &lt;mp-toolbar-select&gt; children inside it, or any
/// raw <c>.toolbar__group</c> markup.
/// Usage: &lt;mp-toolbar&gt; …children… &lt;/mp-toolbar&gt;
/// </summary>
[HtmlTargetElement("mp-toolbar")]
public sealed class ToolbarTagHelper : TagHelper
{
    /// <summary>Form method. Default "get".</summary>
    [HtmlAttributeName("method")]
    public string Method { get; set; } = "get";

    /// <summary>Label of the trailing submit button. Default "Apply". Set empty to omit it.</summary>
    [HtmlAttributeName("apply-text")]
    public string ApplyText { get; set; } = "Apply";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "form";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "toolbar");
        output.Attributes.SetAttribute("method", Method);

        TagHelperContent children = await output.GetChildContentAsync();
        output.Content.SetHtmlContent(children);

        if (!string.IsNullOrEmpty(ApplyText))
        {
            string label = HtmlEncoder.Default.Encode(ApplyText);
            output.Content.AppendHtml(
                $"<div class=\"toolbar__group\"><button class=\"btn btn--secondary\" type=\"submit\">{label}</button></div>");
        }
    }
}

/// <summary>
/// A search box for a toolbar: renders <c>.toolbar__group.toolbar__search</c> with a
/// &lt;label&gt; + text &lt;input class="form-control"&gt;.
/// Usage: &lt;mp-toolbar-search name="Search" value="Model.Search" placeholder="…" /&gt;
/// </summary>
[HtmlTargetElement("mp-toolbar-search", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ToolbarSearchTagHelper : TagHelper
{
    /// <summary>Query-string parameter name. Also used as the element id. Required.</summary>
    [HtmlAttributeName("name")]
    public string Name { get; set; } = default!;

    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    [HtmlAttributeName("label")]
    public string Label { get; set; } = "Search";

    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "toolbar__group toolbar__search");

        string id = HtmlEncoder.Default.Encode(Name);
        string label = HtmlEncoder.Default.Encode(Label);
        string value = HtmlEncoder.Default.Encode(Value ?? string.Empty);
        string placeholder = Placeholder is null
            ? string.Empty
            : $" placeholder=\"{HtmlEncoder.Default.Encode(Placeholder)}\"";

        output.Content.SetHtmlContent(
            $"<label for=\"{id}\">{label}</label>" +
            $"<input class=\"form-control\" type=\"text\" id=\"{id}\" name=\"{id}\" value=\"{value}\"{placeholder} />");
    }
}

/// <summary>
/// A labelled filter/sort select for a toolbar: renders
/// <c>.toolbar__group.toolbar__filter</c> with a &lt;label&gt; + &lt;select class="form-control"&gt;.
/// Provide &lt;option&gt; children; the one whose value equals <c>value</c> is marked selected.
/// Usage: &lt;mp-toolbar-select name="Sort" label="Sort" value="Model.Sort"&gt;&lt;option …&gt;…&lt;/mp-toolbar-select&gt;
/// </summary>
[HtmlTargetElement("mp-toolbar-select")]
public sealed class ToolbarSelectTagHelper : TagHelper
{
    /// <summary>Query-string parameter name. Also used as the element id. Required.</summary>
    [HtmlAttributeName("name")]
    public string Name { get; set; } = default!;

    [HtmlAttributeName("label")]
    public string Label { get; set; } = default!;

    /// <summary>Currently selected value; the matching &lt;option value="…"&gt; is marked selected.</summary>
    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "toolbar__group toolbar__filter");

        string id = HtmlEncoder.Default.Encode(Name);
        string label = HtmlEncoder.Default.Encode(Label ?? string.Empty);
        string optionsHtml = (await output.GetChildContentAsync()).GetContent();
        optionsHtml = HtmlOptionHelper.MarkSelected(optionsHtml, Value);

        output.Content.SetHtmlContent(
            $"<label for=\"{id}\">{label}</label>" +
            $"<select class=\"form-control\" id=\"{id}\" name=\"{id}\">{optionsHtml}</select>");
    }
}
