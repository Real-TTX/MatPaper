using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// Renders <c>&lt;div class="btn-row"&gt;</c> for form actions. Put buttons/links inside,
/// with a &lt;mp-button-spacer /&gt; to push trailing danger actions to the right. The
/// standard order is: Save (primary submit), Back (secondary link), spacer, Delete (danger).
/// Children may be &lt;mp-button&gt; helpers or raw <c>.btn</c> markup.
/// </summary>
[HtmlTargetElement("mp-button-row")]
public sealed class ButtonRowTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "btn-row");
    }
}

/// <summary>
/// Pushes following buttons to the right within a <c>.btn-row</c>
/// (renders <c>&lt;span class="btn-row__spacer"&gt;&lt;/span&gt;</c>).
/// Usage: &lt;mp-button-spacer /&gt;
/// </summary>
[HtmlTargetElement("mp-button-spacer", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ButtonSpacerTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "btn-row__spacer");
    }
}

/// <summary>
/// A form-action button. Renders an &lt;a class="btn btn--{variant}"&gt; when <c>href</c> is set,
/// otherwise a &lt;button class="btn btn--{variant}" type="{type}"&gt;. Button text is the child
/// content. Set <c>handler</c> to post to a Razor page handler (formaction="?handler=…"),
/// and <c>confirm</c> to require a JS confirmation dialog before the action.
/// Usage: &lt;mp-button variant="primary" type="submit"&gt;Save&lt;/mp-button&gt;
/// </summary>
[HtmlTargetElement("mp-button")]
public sealed class ButtonTagHelper : TagHelper
{
    /// <summary>primary | secondary | danger. Default primary.</summary>
    [HtmlAttributeName("variant")]
    public string Variant { get; set; } = "primary";

    /// <summary>When set, renders a link (&lt;a&gt;) to this href instead of a &lt;button&gt;.</summary>
    [HtmlAttributeName("href")]
    public string? Href { get; set; }

    /// <summary>button | submit. Default submit. Ignored when href is set.</summary>
    [HtmlAttributeName("type")]
    public string Type { get; set; } = "submit";

    /// <summary>Razor page handler name; emits formaction="?handler=…" on a submit button.</summary>
    [HtmlAttributeName("handler")]
    public string? Handler { get; set; }

    /// <summary>Confirmation prompt; emits onclick="return confirm('…')".</summary>
    [HtmlAttributeName("confirm")]
    public string? Confirm { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string css = $"btn btn--{Variant}";
        TagHelperContent body = await output.GetChildContentAsync();
        output.TagMode = TagMode.StartTagAndEndTag;

        if (Href is not null)
        {
            output.TagName = "a";
            output.Attributes.SetAttribute("class", css);
            output.Attributes.SetAttribute("href", Href);
        }
        else
        {
            output.TagName = "button";
            output.Attributes.SetAttribute("class", css);
            output.Attributes.SetAttribute("type", Type);
            if (!string.IsNullOrEmpty(Handler))
            {
                output.Attributes.SetAttribute("formaction", $"?handler={UrlEncoder.Default.Encode(Handler)}");
            }
        }

        if (!string.IsNullOrEmpty(Confirm))
        {
            string message = Confirm.Replace("\\", "\\\\").Replace("'", "\\'");
            output.Attributes.SetAttribute("onclick", $"return confirm('{message}');");
        }

        output.Content.SetHtmlContent(body);
    }
}
