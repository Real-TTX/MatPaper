using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// The date field of the toolkit (<c>mat-picker[data-picker="date"]</c>, rule FORM-14) for a value that is no property of the page model - one per row of
/// a list, each in its own form: <c>&lt;mp-date name="documentDate" id="documentDate_5" value="row.DocumentDate" label="Date" /&gt;</c>. The real input holds the
/// date as <c>2026-10-12</c> (what a model binder reads), the picker shows it in the language of the user. For a property of the page model
/// <c>&lt;mp-field asp-for="…" kind="date"&gt;</c> does the same.
/// </summary>
[HtmlTargetElement("mp-date", TagStructure = TagStructure.WithoutEndTag)]
public sealed class DateTagHelper : TagHelper
{
    /// <summary>The name the value posts under.</summary>
    [HtmlAttributeName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The id of the input (default: the name); give one per row when a page has many.</summary>
    [HtmlAttributeName("id")]
    public string? Id { get; set; }

    [HtmlAttributeName("value")]
    public DateTime? Value { get; set; }

    [HtmlAttributeName("label")]
    public string Label { get; set; } = string.Empty;

    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    /// <summary>The date can be emptied again (default).</summary>
    [HtmlAttributeName("clearable")]
    public bool Clearable { get; set; } = true;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        string id = string.IsNullOrEmpty(Id) ? Name : Id;
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Classes.Merge("mat-field", output.Attributes["class"]?.Value?.ToString()));

        string value = Value is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
        output.Content.SetHtmlContent(
            $"<label class=\"mat-label\" for=\"{Enc(id)}\">{Enc(Label)}</label>" +
            $"<div class=\"mat-picker\" data-picker=\"date\"{(Clearable ? " data-clearable" : string.Empty)}{(string.IsNullOrEmpty(Placeholder) ? string.Empty : $" data-placeholder=\"{Enc(Placeholder)}\"")}>" +
            $"<input class=\"mat-picker__input\" id=\"{Enc(id)}\" name=\"{Enc(Name)}\" value=\"{Enc(value)}\" /></div>" +
            $"<span class=\"mat-error\" id=\"{Enc(id)}-error\" aria-live=\"polite\"></span>");
    }

    private static string Enc(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
