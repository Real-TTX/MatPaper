using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// The row of buttons under the content of a form (rules FORM-01, FORM-02): <c>&lt;div class="mat-btn-row"&gt;</c>. The toolkit orders the buttons
/// itself: the primary one, then the secondary and ghost ones, then the gap, then the danger one, whatever order they are written in.
/// <c>plain</c> leaves out the line above (the foot of a dialog), <c>sticky</c> pins the row to the bottom edge of a phone (a long form).
/// Children may be &lt;mp-button&gt; helpers or raw <c>mat-btn</c> markup.
/// </summary>
[HtmlTargetElement("mp-button-row")]
public sealed class ButtonRowTagHelper : TagHelper
{
    [HtmlAttributeName("plain")]
    public bool Plain { get; set; }

    [HtmlAttributeName("sticky")]
    public bool Sticky { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Classes.Merge("mat-btn-row", Plain ? "mat-btn-row--plain" : null, Sticky ? "mat-btn-row--sticky" : null, output.Attributes["class"]?.Value?.ToString()));
    }
}

/// <summary>
/// The gap between the positive buttons and the danger button of a button row (<c>&lt;span class="mat-btn-row__spacer"&gt;</c>).
/// Usage: &lt;mp-button-spacer /&gt;
/// </summary>
[HtmlTargetElement("mp-button-spacer", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ButtonSpacerTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-btn-row__spacer");
    }
}

/// <summary>
/// A button of the toolkit (rule BUTTON-01: primary, secondary, ghost or danger). Renders an &lt;a class="mat-btn mat-btn--{variant}"&gt; when <c>href</c>
/// is set, otherwise a &lt;button&gt; with its <c>type</c>. The text is the child content, wrapped in a span after the optional <c>icon</c> (BUTTON-04).
/// <c>handler</c> posts to a Razor page handler (formaction="?handler=…"); <c>confirm</c> asks first in the toolkit's dialog (data-confirm; a danger
/// button asks with the red button last), <c>confirm-ok</c> names the button of that dialog; <c>icon-only</c> hides the text from the eye (it stays the
/// name of the button) and shows a tooltip.
/// Usage: &lt;mp-button variant="primary" icon="save"&gt;Save&lt;/mp-button&gt;
/// </summary>
[HtmlTargetElement("mp-button")]
public sealed class ButtonTagHelper : TagHelper
{
    /// <summary>primary | secondary | ghost | danger. Default primary.</summary>
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

    /// <summary>Confirmation question; emits data-confirm, handled by the toolkit's dialog.</summary>
    [HtmlAttributeName("confirm")]
    public string? Confirm { get; set; }

    /// <summary>The text of the confirming button of that dialog ("Delete").</summary>
    [HtmlAttributeName("confirm-ok")]
    public string? ConfirmOk { get; set; }

    /// <summary>An icon of the sprite that comes before the text.</summary>
    [HtmlAttributeName("icon")]
    public string? Icon { get; set; }

    /// <summary>Only the icon is drawn; the text is the accessible name and the tooltip.</summary>
    [HtmlAttributeName("icon-only")]
    public bool IconOnly { get; set; }

    /// <summary>sm | lg (default: the usual size).</summary>
    [HtmlAttributeName("size")]
    public string? Size { get; set; }

    /// <summary>The button fills the width of its container.</summary>
    [HtmlAttributeName("block")]
    public bool Block { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string variant = Variant is "primary" or "secondary" or "ghost" or "danger" ? Variant : "secondary";
        TagHelperContent body = await output.GetChildContentAsync();
        string text = body.GetContent().Trim();
        output.TagMode = TagMode.StartTagAndEndTag;

        string css = Classes.Merge(
            "mat-btn", $"mat-btn--{variant}",
            IconOnly ? "mat-btn--icon" : null,
            Size is "sm" or "lg" ? $"mat-btn--{Size}" : null,
            Block ? "mat-btn--block" : null,
            output.Attributes["class"]?.Value?.ToString());

        if (Href is not null)
        {
            output.TagName = "a";
            output.Attributes.SetAttribute("href", Href);
        }
        else
        {
            output.TagName = "button";
            output.Attributes.SetAttribute("type", Type);
            if (!string.IsNullOrEmpty(Handler))
            {
                output.Attributes.SetAttribute("formaction", $"?handler={UrlEncoder.Default.Encode(Handler)}");
            }
        }

        output.Attributes.SetAttribute("class", css);

        if (!string.IsNullOrEmpty(Confirm))
        {
            output.Attributes.SetAttribute("data-confirm", Confirm);
            if (!string.IsNullOrEmpty(ConfirmOk))
            {
                output.Attributes.SetAttribute("data-confirm-ok", ConfirmOk);
            }
        }

        if (IconOnly)
        {
            output.Attributes.SetAttribute("aria-label", System.Net.WebUtility.HtmlDecode(text));
            output.Attributes.SetAttribute("data-tip", System.Net.WebUtility.HtmlDecode(text));
            output.Content.SetHtmlContent(Icon is null ? string.Empty : Ui.IconHtml(Icon));
            return;
        }

        output.Content.Clear();
        if (!string.IsNullOrEmpty(Icon))
        {
            output.Content.AppendHtml(Ui.IconHtml(Icon));
        }

        output.Content.AppendHtml("<span>");
        output.Content.AppendHtml(body);
        output.Content.AppendHtml("</span>");
    }
}

/// <summary>
/// A button that is only an icon (rules BUTTON-04, ICON-03): the action of a row, a tool of a toolbar. <c>label</c> is the accessible name and the tooltip.
/// A link when <c>href</c> is set, otherwise a &lt;button&gt; (<c>type</c> button by default, <c>submit</c> to post a form; <c>handler</c> and
/// <c>confirm</c> as for &lt;mp-button&gt;). Ghost and small unless said otherwise - what sits in a table row.
/// Usage: &lt;mp-icon-button icon="edit" label="Edit" href="/Tags/Edit?id=3" /&gt;
/// </summary>
[HtmlTargetElement("mp-icon-button", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconButtonTagHelper : TagHelper
{
    [HtmlAttributeName("icon")]
    public string Icon { get; set; } = "more-horizontal";

    [HtmlAttributeName("label")]
    public string Label { get; set; } = string.Empty;

    [HtmlAttributeName("href")]
    public string? Href { get; set; }

    [HtmlAttributeName("type")]
    public string Type { get; set; } = "button";

    /// <summary>ghost (default) | secondary | primary | danger.</summary>
    [HtmlAttributeName("variant")]
    public string Variant { get; set; } = "ghost";

    /// <summary>sm (default) | md | lg.</summary>
    [HtmlAttributeName("size")]
    public string Size { get; set; } = "sm";

    [HtmlAttributeName("handler")]
    public string? Handler { get; set; }

    [HtmlAttributeName("confirm")]
    public string? Confirm { get; set; }

    [HtmlAttributeName("confirm-ok")]
    public string? ConfirmOk { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        string variant = Variant is "primary" or "secondary" or "ghost" or "danger" ? Variant : "ghost";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Classes.Merge("mat-btn", $"mat-btn--{variant}", "mat-btn--icon", Size is "sm" or "lg" ? $"mat-btn--{Size}" : null, output.Attributes["class"]?.Value?.ToString()));
        output.Attributes.SetAttribute("aria-label", Label);
        output.Attributes.SetAttribute("data-tip", Label);

        if (Href is not null)
        {
            output.TagName = "a";
            output.Attributes.SetAttribute("href", Href);
        }
        else
        {
            output.TagName = "button";
            output.Attributes.SetAttribute("type", Type);
            if (!string.IsNullOrEmpty(Handler))
            {
                output.Attributes.SetAttribute("formaction", $"?handler={UrlEncoder.Default.Encode(Handler)}");
            }
        }

        if (!string.IsNullOrEmpty(Confirm))
        {
            output.Attributes.SetAttribute("data-confirm", Confirm);
            if (!string.IsNullOrEmpty(ConfirmOk))
            {
                output.Attributes.SetAttribute("data-confirm-ok", ConfirmOk);
            }
        }

        output.Content.SetHtmlContent(Ui.IconHtml(Icon));
    }
}

/// <summary>Joins class names, leaving out what is empty.</summary>
internal static class Classes
{
    public static string Merge(params string?[] names)
        => string.Join(' ', names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()));
}
