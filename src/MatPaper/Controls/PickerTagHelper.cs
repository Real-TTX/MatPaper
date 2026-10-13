using System.ComponentModel.DataAnnotations;
using System.Collections;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;
using MatPaper.Services;

namespace MatPaper.Controls;

/// <summary>
/// A choice from a list in the toolkit's picker (<c>mat-picker[data-picker="select"]</c>, rule FORM-15): a field that opens a panel with a search and the
/// options, full screen on a phone. Renders a <c>mat-field</c> with the label, the picker, the hint and the reserved line for the message of the server.
/// <para>
/// The value posts as ONE input named like the model property, as the toolkit makes it: the key of the chosen option, or the keys joined by a comma
/// (<c>1,4,7</c>) in the multiple mode. The model binds as before: a scalar (<c>long?</c>) takes the key, a collection (<c>long[]</c>) is split by
/// <see cref="CommaSeparatedBinder"/>, so no page model changes. Options come from <c>asp-items</c>.
/// </para>
/// </summary>
[HtmlTargetElement("mp-picker", Attributes = ForAttributeName + "," + ItemsAttributeName)]
public sealed class PickerTagHelper : TagHelper
{
    private const string ForAttributeName = "asp-for";
    private const string ItemsAttributeName = "asp-items";

    private readonly IStringLocalizer<SharedResource> _l;

    public PickerTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    /// <summary>The model property this picker edits. Required.</summary>
    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression For { get; set; } = default!;

    /// <summary>The selectable options (Value + Text). Required.</summary>
    [HtmlAttributeName(ItemsAttributeName)]
    public IEnumerable<SelectListItem> Items { get; set; } = default!;

    /// <summary>Explicit label text. When omitted the [Display] name / property name is used.</summary>
    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    /// <summary>When true, the picker is a multi-select bound to a collection.</summary>
    [HtmlAttributeName("multiple")]
    public bool Multiple { get; set; }

    /// <summary>Placeholder shown when nothing is chosen. Defaults per mode.</summary>
    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    /// <summary>Optional hint rendered as <c>mat-hint</c> under the picker.</summary>
    [HtmlAttributeName("help")]
    public string? Help { get; set; }

    /// <summary>Marks the field as required even without a <c>[Required]</c> on the model.</summary>
    [HtmlAttributeName("required")]
    public bool? Required { get; set; }

    /// <summary>
    /// Explicit chosen key(s) - comma-separated for multiple - overriding the model value. Together with <c>id-suffix</c> this lets the same field appear
    /// once per row (each row in its own form), e.g. in the review inbox.
    /// </summary>
    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    /// <summary>
    /// Offer "Create ..." for names that do not exist yet. Inferred from the field (correspondent, document type, project, tag); set to "false" for
    /// filters, where creating an entry makes no sense.
    /// </summary>
    [HtmlAttributeName("create")]
    public string? Create { get; set; }

    /// <summary>
    /// A document id: the picker then offers the correspondents (or names) that the text of that document suggests, on top of its list. The page script
    /// asks for them (once for all the pickers of a page) and hands them to the toolkit. Only for the correspondent picker; leave unset elsewhere.
    /// </summary>
    [HtmlAttributeName("suggest-doc")]
    public long? SuggestDoc { get; set; }

    /// <summary>Suffix appended to the id so repeated pickers keep unique ids.</summary>
    [HtmlAttributeName("id-suffix")]
    public string? IdSuffix { get; set; }

    /// <summary>The picker has no search field (a list of three).</summary>
    [HtmlAttributeName("no-search")]
    public bool NoSearch { get; set; }

    /// <summary>The colour (<c>#rrggbb</c>) of an option by its key: tags and projects have one; it shows as a dot before the name.</summary>
    [HtmlAttributeName("colours")]
    public IReadOnlyDictionary<string, string?>? Colours { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        string fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        string fieldId = TagBuilder.CreateSanitizedId(fullName, "_");
        string id = string.IsNullOrEmpty(IdSuffix) ? fieldId : $"{fieldId}_{IdSuffix}";
        string placeholder = Placeholder ?? (Multiple ? _l["Add…"].Value : _l["— None —"].Value);
        string labelText = Label ?? For.Metadata.DisplayName ?? For.Metadata.PropertyName ?? For.Name;
        var requiredAttribute = For.Metadata.ValidatorMetadata.OfType<RequiredAttribute>().FirstOrDefault();
        bool required = Required ?? requiredAttribute is not null;

        IReadOnlyList<SelectListItem> items = Items as IReadOnlyList<SelectListItem> ?? Items.ToList();
        var selected = (Value is not null ? SplitKeys(Value) : ResolveSelectedValues()).ToList();
        string? error = ViewContext.ViewData.ModelState.TryGetValue(fullName, out var entry) && entry.Errors.Count > 0
            ? entry.Errors.Select(e => e.ErrorMessage).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "?"
            : null;

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Classes.Merge("mat-field", error is null ? null : "is-invalid", output.Attributes["class"]?.Value?.ToString()));

        var html = new StringBuilder();
        html.Append($"<label class=\"mat-label\" for=\"{Enc(id)}\">{Enc(labelText)}");
        if (required)
        {
            html.Append(" <span class=\"mat-req\" aria-hidden=\"true\">*</span>");
        }

        html.Append("</label>");

        string? createKind = CreateKind(fullName);
        html.Append("<div class=\"mat-picker\" data-picker=\"select\"");
        if (Multiple) { html.Append(" data-multiple data-sep=\",\""); }
        if (!NoSearch && (items.Count > 6 || createKind is not null)) { html.Append(" data-search"); }
        if (!Multiple && !required) { html.Append(" data-clearable"); }
        html.Append($" data-placeholder=\"{Enc(placeholder)}\"");
        if (createKind is not null) { html.Append($" data-create-url=\"/QuickCreate?kind={Enc(createKind)}\" data-create-label=\"{Enc(_l["Create \"{0}\""].Value)}\""); }
        if (SuggestDoc is long doc && !Multiple && selected.Count == 0) { html.Append($" data-mp-suggest-doc=\"{doc}\""); }
        html.Append('>');

        html.Append($"<input class=\"mat-picker__input\" id=\"{Enc(id)}\" name=\"{Enc(fullName)}\" value=\"{Enc(string.Join(",", selected))}\"");
        if (required)
        {
            html.Append(" required");
            html.Append($" data-msg-required=\"{Enc(_l[requiredAttribute?.ErrorMessage ?? "The {0} field is required.", labelText].Value)}\"");
        }

        if (error is not null) { html.Append($" aria-invalid=\"true\" aria-describedby=\"{Enc(id)}-error\""); }
        html.Append(" />");

        html.Append("<div class=\"mat-picker__options\" hidden>");
        foreach (SelectListItem item in items)
        {
            html.Append($"<button type=\"button\" class=\"mat-picker__option\" data-value=\"{Enc(item.Value)}\"");
            if (item.Disabled) { html.Append(" disabled"); }
            html.Append('>');
            if (Colours is not null && item.Value is not null && Colours.TryGetValue(item.Value, out var colour) && !string.IsNullOrEmpty(colour))
            {
                html.Append($"<span class=\"mat-dot\" data-colour=\"{Enc(colour)}\" aria-hidden=\"true\"></span>");
            }

            html.Append(Enc(item.Text)).Append("</button>");
        }

        html.Append("</div></div>");

        if (!string.IsNullOrEmpty(Help))
        {
            html.Append($"<span class=\"mat-hint\" id=\"{Enc(id)}-hint\">{Enc(Help)}</span>");
        }

        html.Append($"<span class=\"mat-error\" id=\"{Enc(id)}-error\" aria-live=\"polite\">{Enc(error)}</span>");
        output.Content.SetHtmlContent(html.ToString());
    }

    private string? CreateKind(string fullName)
    {
        if (string.Equals(Create, "false", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(Create))
        {
            return Create;
        }

        var field = fullName.Contains('.') ? fullName[(fullName.LastIndexOf('.') + 1)..] : fullName;
        if (field.Contains("Correspondent", StringComparison.OrdinalIgnoreCase)) { return "correspondent"; }
        // Document types belong to the administrators (System): only they get the "Create" for them.
        if (field.Contains("DocumentType", StringComparison.OrdinalIgnoreCase)) { return ViewContext.HttpContext.User.IsInRole("Admin") ? "document-type" : null; }
        if (field.Contains("Project", StringComparison.OrdinalIgnoreCase)) { return "project"; }
        if (field.Contains("Tag", StringComparison.OrdinalIgnoreCase)) { return "tag"; }
        return null;
    }

    private static IEnumerable<string> SplitKeys(string keys)
        => keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private IEnumerable<string> ResolveSelectedValues()
    {
        object? model = For.Model;
        if (model is null)
        {
            yield break;
        }

        if (Multiple && model is IEnumerable enumerable && model is not string)
        {
            foreach (object? entry in enumerable)
            {
                if (entry is not null)
                {
                    yield return Convert.ToString(entry, CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }

            yield break;
        }

        string? scalar = Convert.ToString(model, CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(scalar))
        {
            yield return scalar;
        }
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
