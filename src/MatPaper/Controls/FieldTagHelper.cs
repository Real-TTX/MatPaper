using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.TagHelpers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// A complete form field built from an <c>asp-for</c> model expression. Renders a
/// <c>.form-row</c> (or <c>.form-check</c> host for checkboxes) containing a &lt;label&gt;,
/// the input, an inline <c>.field-error</c> validation message and optional
/// <c>.form-help</c> text. Names and ids are derived from the model expression, so any
/// number of fields can coexist on one page without colliding.
/// Supports kinds: text (default), email, password, number, color, checkbox, textarea, select.
/// For select, pass <c>asp-items</c> (a SelectList) or nested &lt;option&gt; children.
/// Optional dependent visibility: <c>show-when-field</c> + <c>show-when-value</c> emit
/// data-show-when-field / data-show-when-value on the wrapper (handled by app.js).
/// </summary>
[HtmlTargetElement("mp-field", Attributes = ForAttributeName)]
public sealed class FieldTagHelper : TagHelper
{
    private const string ForAttributeName = "asp-for";

    private readonly IHtmlGenerator _generator;

    public FieldTagHelper(IHtmlGenerator generator) => _generator = generator;

    /// <summary>The model property this field edits. Required.</summary>
    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression For { get; set; } = default!;

    /// <summary>Input kind: text, email, password, number, color, checkbox, textarea, select.</summary>
    [HtmlAttributeName("kind")]
    public string Kind { get; set; } = "text";

    /// <summary>Explicit label text. When omitted the [Display] name / property name is used.</summary>
    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    /// <summary>Optional help text rendered as <c>.form-help</c> under the input.</summary>
    [HtmlAttributeName("help")]
    public string? Help { get; set; }

    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    [HtmlAttributeName("autocomplete")]
    public string? Autocomplete { get; set; }

    /// <summary>Row count for the textarea kind. Default 4.</summary>
    [HtmlAttributeName("rows")]
    public int Rows { get; set; } = 4;

    /// <summary>SelectList for the select kind (alternative to nested &lt;option&gt; children).</summary>
    [HtmlAttributeName("asp-items")]
    public IEnumerable<SelectListItem>? Items { get; set; }

    /// <summary>Name of the controlling input for dependent visibility (see app.js).</summary>
    [HtmlAttributeName("show-when-field")]
    public string? ShowWhenField { get; set; }

    /// <summary>Comma-separated controller values that reveal this field.</summary>
    [HtmlAttributeName("show-when-value")]
    public string? ShowWhenValue { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string kind = (Kind ?? "text").Trim().ToLowerInvariant();
        bool isCheckbox = kind == "checkbox";

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", isCheckbox ? "form-row form-row--check" : "form-row");

        if (!string.IsNullOrEmpty(ShowWhenField))
        {
            output.Attributes.SetAttribute("data-show-when-field", ShowWhenField);
            output.Attributes.SetAttribute("data-show-when-value", ShowWhenValue ?? string.Empty);
        }

        TagBuilder label = _generator.GenerateLabel(ViewContext, For.ModelExplorer, For.Name, Label, htmlAttributes: null);
        TagBuilder validation = _generator.GenerateValidationMessage(
            ViewContext, For.ModelExplorer, For.Name, message: null, tag: "span",
            htmlAttributes: new { @class = "field-error" });

        if (isCheckbox)
        {
            TagBuilder checkbox = _generator.GenerateCheckBox(ViewContext, For.ModelExplorer, For.Name, isChecked: null, htmlAttributes: null);
            TagBuilder hidden = _generator.GenerateHiddenForCheckbox(ViewContext, For.ModelExplorer, For.Name);

            output.Content.SetHtmlContent("<div class=\"form-check\">");
            output.Content.AppendHtml(checkbox);
            output.Content.AppendHtml(label);
            output.Content.AppendHtml("</div>");
            output.Content.AppendHtml(hidden);
            output.Content.AppendHtml(validation);
            AppendHelp(output);
            return;
        }

        output.Content.SetHtmlContent(label);

        switch (kind)
        {
            case "textarea":
                var textareaAttrs = BaseAttributes();
                output.Content.AppendHtml(
                    _generator.GenerateTextArea(ViewContext, For.ModelExplorer, For.Name, Rows, 0, textareaAttrs));
                break;

            case "select":
                await AppendSelectAsync(output);
                break;

            case "password":
                output.Content.AppendHtml(
                    _generator.GeneratePassword(ViewContext, For.ModelExplorer, For.Name, value: null, BaseAttributes()));
                break;

            default:
                var inputAttrs = BaseAttributes();
                inputAttrs["type"] = MapInputType(kind);
                // Pass the model value explicitly: GenerateTextBox does NOT fall back to
                // ModelExplorer.Model when value is null, so edit forms would render empty.
                output.Content.AppendHtml(
                    _generator.GenerateTextBox(ViewContext, For.ModelExplorer, For.Name, value: For.Model, format: null, inputAttrs));
                break;
        }

        output.Content.AppendHtml(validation);
        AppendHelp(output);
    }

    private async Task AppendSelectAsync(TagHelperOutput output)
    {
        if (Items is not null)
        {
            output.Content.AppendHtml(
                _generator.GenerateSelect(ViewContext, For.ModelExplorer, optionLabel: null, For.Name, Items, allowMultiple: false, BaseAttributes()));
            return;
        }

        // Nested <option> children: render the select manually and mark the option
        // whose value matches the current model value.
        string fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        string id = TagBuilder.CreateSanitizedId(fullName, "_");
        string optionsHtml = (await output.GetChildContentAsync()).GetContent();
        optionsHtml = HtmlOptionHelper.MarkSelected(optionsHtml, For.Model?.ToString());

        output.Content.AppendHtml(
            $"<select class=\"form-control\" id=\"{HtmlEncoder.Default.Encode(id)}\" name=\"{HtmlEncoder.Default.Encode(fullName)}\">");
        output.Content.AppendHtml(optionsHtml);
        output.Content.AppendHtml("</select>");
    }

    private Dictionary<string, object> BaseAttributes()
    {
        var attrs = new Dictionary<string, object> { ["class"] = "form-control" };
        if (!string.IsNullOrEmpty(Placeholder))
        {
            attrs["placeholder"] = Placeholder;
        }

        if (!string.IsNullOrEmpty(Autocomplete))
        {
            attrs["autocomplete"] = Autocomplete;
        }

        return attrs;
    }

    private void AppendHelp(TagHelperOutput output)
    {
        if (!string.IsNullOrEmpty(Help))
        {
            output.Content.AppendHtml($"<p class=\"form-help\">{HtmlEncoder.Default.Encode(Help)}</p>");
        }
    }

    private static string MapInputType(string kind) => kind switch
    {
        "email" => "email",
        "number" => "number",
        "color" => "color",
        _ => "text",
    };
}
