using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// A complete form field built from an <c>asp-for</c> model expression, as the toolkit has it (rules FORM-03, FORM-06, FORM-08): <c>&lt;div class="mat-field"&gt;</c>
/// with the label above, the control, the hint below and the reserved line (<c>mat-error</c>) for the message of the server, which the toolkit's script
/// writes its own into. Names and ids come from the model expression, so any number of fields can coexist on one page.
/// Kinds: text (default), email, password (with the eye), number, tel, url, search, textarea, select, checkbox, switch, and the pickers date, time and color.
/// An explicit <c>[Required]</c> puts the star in the label and <c>required</c> plus the message (<c>data-msg-required</c>) on the control; the toolkit
/// checks the form without the browser's bubbles (<c>novalidate</c>). Length, range, pattern and e-mail checks of the model become the matching
/// attributes with their messages in the same way. A checkbox is never required (FORM-05).
/// Dependent visibility: <c>show-when-field</c> + <c>show-when-value</c> write the toolkit's <c>data-show-if</c> / <c>data-show-value</c> on the field; a
/// field that is hidden is disabled and posts nothing (FORM-04).
/// Folder picker (text kind): <c>browse</c> names the scope and <c>browse-label</c> the text of the button that opens the shared dialog for this input
/// (browse-dialog.js). Buttons written as the content of the tag (text, number kinds) stand at the end of the input as one group with it.
/// </summary>
[HtmlTargetElement("mp-field", Attributes = ForAttributeName)]
public sealed class FieldTagHelper : TagHelper
{
    private const string ForAttributeName = "asp-for";

    private readonly IHtmlGenerator _generator;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<MatPaper.SharedResource> _l;

    public FieldTagHelper(IHtmlGenerator generator, Microsoft.Extensions.Localization.IStringLocalizer<MatPaper.SharedResource> l)
    {
        _generator = generator;
        _l = l;
    }

    /// <summary>The model property this field edits. Required.</summary>
    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression For { get; set; } = default!;

    /// <summary>Input kind: text, email, password, number, tel, url, search, textarea, select, checkbox, switch, date, time, color.</summary>
    [HtmlAttributeName("kind")]
    public string Kind { get; set; } = "text";

    /// <summary>Explicit label text. When omitted the [Display] name / property name is used.</summary>
    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    /// <summary>Optional hint rendered as <c>mat-hint</c> under the control (for a checkbox or switch: under its label).</summary>
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

    /// <summary>Marks the field as required even without a <c>[Required]</c> on the model (<c>false</c> takes the star away).</summary>
    [HtmlAttributeName("required")]
    public bool? Required { get; set; }

    /// <summary>An id of the control of your own, when the same property appears twice on a page (one of two blocks that show one at a time).</summary>
    [HtmlAttributeName("control-id")]
    public string? ControlId { get; set; }

    /// <summary>Draw the asterisk of a required field (default); <c>false</c> for a form in which everything is required (the sign-in).</summary>
    [HtmlAttributeName("star")]
    public bool Star { get; set; } = true;

    /// <summary>The field spans both columns of a two-column form (FORM-07): descriptions, long values.</summary>
    [HtmlAttributeName("full")]
    public bool Full { get; set; }

    /// <summary>Set in the mono font (IDs, paths, technical values - TYPE-03).</summary>
    [HtmlAttributeName("mono")]
    public bool Mono { get; set; }

    [HtmlAttributeName("readonly")]
    public bool ReadOnly { get; set; }

    [HtmlAttributeName("disabled")]
    public bool Disabled { get; set; }

    [HtmlAttributeName("autofocus")]
    public bool AutoFocus { get; set; }

    [HtmlAttributeName("inputmode")]
    public string? InputMode { get; set; }

    [HtmlAttributeName("min")]
    public string? Min { get; set; }

    [HtmlAttributeName("max")]
    public string? Max { get; set; }

    [HtmlAttributeName("step")]
    public string? Step { get; set; }

    [HtmlAttributeName("maxlength")]
    public int? MaxLength { get; set; }

    [HtmlAttributeName("pattern")]
    public string? Pattern { get; set; }

    /// <summary>A picker can be emptied again (date, time, color).</summary>
    [HtmlAttributeName("clearable")]
    public bool Clearable { get; set; }

    /// <summary>Name of the controlling input for dependent visibility.</summary>
    [HtmlAttributeName("show-when-field")]
    public string? ShowWhenField { get; set; }

    /// <summary>Comma-separated controller values that reveal this field (<c>true</c> = a checkbox that is checked).</summary>
    [HtmlAttributeName("show-when-value")]
    public string? ShowWhenValue { get; set; }

    /// <summary>Picker scope sent to the form's data-browse-url handler; adds a browse button (text kind only).</summary>
    [HtmlAttributeName("browse")]
    public string? Browse { get; set; }

    /// <summary>Text of the browse button.</summary>
    [HtmlAttributeName("browse-label")]
    public string? BrowseLabel { get; set; }

    /// <summary>Optional top of the picker tree, e.g. "/" for container folders.</summary>
    [HtmlAttributeName("browse-root")]
    public string? BrowseRoot { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string kind = (Kind ?? "text").Trim().ToLowerInvariant();
        bool isCheck = kind is "checkbox" or "switch";
        string fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        string id = ControlId ?? TagBuilder.CreateSanitizedId(fullName, "_");
        string? error = ErrorOf(fullName);
        bool required = !isCheck && (Required ?? For.Metadata.ValidatorMetadata.Any(v => v is RequiredAttribute));
        string labelText = Label ?? For.Metadata.DisplayName ?? For.Metadata.PropertyName ?? For.Name;

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Classes.Merge("mat-field", Full ? "mat-field--full" : null, error is null ? null : "is-invalid", output.Attributes["class"]?.Value?.ToString()));
        if (!string.IsNullOrEmpty(ShowWhenField))
        {
            output.Attributes.SetAttribute("data-show-if", ShowWhenField);
            output.Attributes.SetAttribute("data-show-value", ShowWhenValue ?? string.Empty);
        }

        var html = new StringBuilder();
        if (!isCheck)
        {
            html.Append($"<label class=\"mat-label\" for=\"{Enc(id)}\">{Enc(labelText)}");
            if (required && Star)
            {
                html.Append(" <span class=\"mat-req\" aria-hidden=\"true\">*</span>");
            }

            html.Append("</label>");
        }

        switch (kind)
        {
            case "checkbox":
            case "switch":
                AppendCheck(html, kind, id, labelText);
                break;
            case "textarea":
                html.Append(Render(_generator.GenerateTextArea(ViewContext, For.ModelExplorer, For.Name, Rows, 0, null), "mat-textarea", required, id, error));
                break;
            case "select":
                html.Append(await SelectAsync(output, id, required, error));
                break;
            case "date":
            case "time":
                html.Append(PickerHtml(kind, id, fullName, error, required));
                break;
            case "color":
                html.Append(PickerHtml("colour", id, fullName, error, required));
                break;
            case "password":
                html.Append("<div class=\"mat-input-icon mat-input-icon--end\">");
                html.Append(Render(_generator.GeneratePassword(ViewContext, For.ModelExplorer, For.Name, value: null, null), "mat-input", required, id, error));
                html.Append($"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm mat-input-icon__btn\" data-password-toggle=\"{Enc(id)}\" aria-pressed=\"false\" aria-label=\"{Enc(PasswordLabel())}\" data-tip=\"{Enc(PasswordLabel())}\">{Ui.IconHtml("eye", "mat-toggle-on")}{Ui.IconHtml("eye-off", "mat-toggle-off")}</button>");
                html.Append("</div>");
                break;
            default:
                // The model value is passed explicitly: GenerateTextBox does NOT fall back to ModelExplorer.Model when value is null, so edit forms would render empty.
                var textBox = _generator.GenerateTextBox(ViewContext, For.ModelExplorer, For.Name, value: For.Model, format: null, new Dictionary<string, object> { ["type"] = InputType(kind) });
                string box = Render(textBox, "mat-input", required, id, error);

                // Buttons written inside the tag stand at the end of the input, as one group with it (the camera that reads a number from a label).
                string addon = (await output.GetChildContentAsync()).GetContent().Trim();
                if (string.IsNullOrEmpty(Browse) && addon.Length == 0)
                {
                    html.Append(box);
                }
                else
                {
                    html.Append("<div class=\"mat-input-group\">").Append(box);
                    if (!string.IsNullOrEmpty(Browse))
                    {
                        html.Append($"<button type=\"button\" class=\"mat-btn mat-btn--secondary\" data-browse=\"{Enc(Browse)}\" data-browse-target=\"{Enc(id)}\"");
                        if (!string.IsNullOrEmpty(BrowseRoot))
                        {
                            html.Append($" data-browse-root=\"{Enc(BrowseRoot)}\"");
                        }

                        html.Append($">{Ui.IconHtml("folder-open")}<span>{Enc(string.IsNullOrEmpty(BrowseLabel) ? "…" : BrowseLabel)}</span></button>");
                    }

                    html.Append(addon).Append("</div>");
                }

                break;
        }

        if (!isCheck && !string.IsNullOrEmpty(Help))
        {
            html.Append($"<span class=\"mat-hint\" id=\"{Enc(id)}-hint\">{Enc(Help)}</span>");
        }

        html.Append($"<span class=\"mat-error\" id=\"{Enc(id)}-error\" aria-live=\"polite\">{Enc(error)}</span>");
        output.Content.SetHtmlContent(html.ToString());
    }

    private string? ErrorOf(string fullName)
    {
        if (!ViewContext.ViewData.ModelState.TryGetValue(fullName, out var entry) || entry.Errors.Count == 0)
        {
            return null;
        }

        string text = entry.Errors.Select(e => e.ErrorMessage).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? string.Empty;
        return text.Length == 0 ? "?" : text;
    }

    private void AppendCheck(StringBuilder html, string kind, string id, string labelText)
    {
        TagBuilder checkbox = _generator.GenerateCheckBox(ViewContext, For.ModelExplorer, For.Name, isChecked: null, htmlAttributes: null);
        checkbox.Attributes.Remove("class");
        checkbox.Attributes["id"] = id;
        StripValidation(checkbox);
        if (Disabled) { checkbox.Attributes["disabled"] = "disabled"; }
        string text = string.IsNullOrEmpty(Help)
            ? $"<span class=\"mat-check__text\">{Enc(labelText)}</span>"
            : $"<span class=\"mat-check__text\"><strong>{Enc(labelText)}</strong><small class=\"mat-muted\">{Enc(Help)}</small></span>";
        if (kind == "switch")
        {
            html.Append("<label class=\"mat-switch\">").Append(Render(checkbox)).Append("<span class=\"mat-switch__track\"></span>")
                .Append(string.IsNullOrEmpty(Help) ? $"<span>{Enc(labelText)}</span>" : $"<span class=\"mat-check__text\"><strong>{Enc(labelText)}</strong><small class=\"mat-muted\">{Enc(Help)}</small></span>")
                .Append("</label>");
        }
        else
        {
            html.Append("<label class=\"mat-check\">").Append(Render(checkbox)).Append(text).Append("</label>");
        }

        html.Append(Render(_generator.GenerateHiddenForCheckbox(ViewContext, For.ModelExplorer, For.Name)));
    }

    private async Task<string> SelectAsync(TagHelperOutput output, string id, bool required, string? error)
    {
        if (Items is not null)
        {
            TagBuilder select = _generator.GenerateSelect(ViewContext, For.ModelExplorer, optionLabel: null, For.Name, Items, allowMultiple: false, htmlAttributes: null);
            return Render(select, "mat-select", required, id, error);
        }

        // Nested <option> children: render the select manually and mark the option whose value matches the current model value.
        string fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        string optionsHtml = HtmlOptionHelper.MarkSelected((await output.GetChildContentAsync()).GetContent(), For.Model?.ToString());
        var sb = new StringBuilder($"<select class=\"{Classes.Merge("mat-select", Mono ? "mat-mono" : null)}\" id=\"{Enc(id)}\" name=\"{Enc(fullName)}\"");
        if (required) { sb.Append(" required"); }
        if (Disabled) { sb.Append(" disabled"); }
        if (error is not null) { sb.Append($" aria-invalid=\"true\" aria-describedby=\"{Enc(id)}-error\""); }
        sb.Append('>').Append(optionsHtml).Append("</select>");
        return sb.ToString();
    }

    /// <summary>A picker of the toolkit around the real input that holds the value in a fixed format (date 2026-10-12, time 09:30, colour #b4235f).</summary>
    private string PickerHtml(string picker, string id, string fullName, string? error, bool required)
    {
        string value = For.Model switch
        {
            null => string.Empty,
            DateTime dt when picker == "date" => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("HH:mm", CultureInfo.InvariantCulture),
            DateTimeOffset dto when picker == "date" => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeSpan ts => $"{ts.Hours:00}:{ts.Minutes:00}",
            var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty,
        };

        var sb = new StringBuilder($"<div class=\"mat-picker\" data-picker=\"{picker}\"");
        if (Clearable || !required) { sb.Append(" data-clearable"); }
        if (!string.IsNullOrEmpty(Placeholder)) { sb.Append($" data-placeholder=\"{Enc(Placeholder)}\""); }
        if (!string.IsNullOrEmpty(Min)) { sb.Append($" data-min=\"{Enc(Min)}\""); }
        if (!string.IsNullOrEmpty(Max)) { sb.Append($" data-max=\"{Enc(Max)}\""); }
        if (!string.IsNullOrEmpty(Step)) { sb.Append($" data-step=\"{Enc(Step)}\""); }
        sb.Append($"><input class=\"mat-picker__input\" id=\"{Enc(id)}\" name=\"{Enc(fullName)}\" value=\"{Enc(value)}\"");
        if (required) { sb.Append(" required"); }
        if (Disabled) { sb.Append(" disabled"); }
        if (error is not null) { sb.Append($" aria-invalid=\"true\" aria-describedby=\"{Enc(id)}-error\""); }
        sb.Append(" /></div>");
        return sb.ToString();
    }

    /// <summary>Renders a control the generator made: the toolkit's class, the validation of the model as attributes of the control, and the state of the form.</summary>
    private string Render(TagBuilder control, string cssClass, bool required, string id, string? error)
    {
        control.AddCssClass(cssClass);
        control.Attributes["id"] = id;
        if (Mono) { control.AddCssClass("mat-mono"); }
        MapValidation(control, required);
        Set(control, "placeholder", Placeholder);
        Set(control, "autocomplete", Autocomplete);
        Set(control, "inputmode", InputMode);
        Set(control, "min", Min);
        Set(control, "max", Max);
        Set(control, "step", Step);
        Set(control, "pattern", Pattern);
        if (MaxLength is int max) { control.Attributes["maxlength"] = max.ToString(CultureInfo.InvariantCulture); }
        if (ReadOnly) { control.Attributes["readonly"] = "readonly"; }
        if (Disabled) { control.Attributes["disabled"] = "disabled"; }
        if (AutoFocus) { control.Attributes["autofocus"] = "autofocus"; }
        if (required) { control.Attributes["required"] = "required"; }
        if (error is not null)
        {
            control.Attributes["aria-invalid"] = "true";
            control.Attributes["aria-describedby"] = $"{id}-error";
        }

        return Render(control);
    }

    private static string Render(IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    private static void Set(TagBuilder control, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            control.Attributes[name] = value;
        }
    }

    /// <summary>
    /// The unobtrusive validation attributes of the model (<c>data-val-*</c>, written for a script that is not here) become what the toolkit reads:
    /// <c>required</c>, <c>minlength</c>, <c>maxlength</c>, <c>min</c>, <c>max</c>, <c>pattern</c> and the messages <c>data-msg-required</c>,
    /// <c>data-msg-range</c>, <c>data-msg-pattern</c>, <c>data-msg-type</c>.
    /// </summary>
    private static void MapValidation(TagBuilder control, bool required)
    {
        var a = control.Attributes;
        string? Get(string key) => a.TryGetValue(key, out var v) ? v : null;

        if (Get("data-val-required") is { } requiredMessage && required)
        {
            a["data-msg-required"] = requiredMessage;
        }

        if (Get("data-val-length") is { } lengthMessage)
        {
            if (Get("data-val-length-min") is { } min) { a["minlength"] = min; }
            if (Get("data-val-length-max") is { } max) { a["maxlength"] = max; }
            a["data-msg-range"] = lengthMessage;
        }

        if (Get("data-val-range") is { } rangeMessage)
        {
            if (Get("data-val-range-min") is { } min) { a["min"] = min; }
            if (Get("data-val-range-max") is { } max) { a["max"] = max; }
            a["data-msg-range"] = rangeMessage;
        }

        if (Get("data-val-regex") is { } regexMessage)
        {
            if (Get("data-val-regex-pattern") is { } pattern) { a["pattern"] = pattern; }
            a["data-msg-pattern"] = regexMessage;
        }

        if (Get("data-val-email") is { } emailMessage) { a["data-msg-type"] = emailMessage; }
        else if (Get("data-val-url") is { } urlMessage) { a["data-msg-type"] = urlMessage; }
        else if (Get("data-val-number") is { } numberMessage) { a["data-msg-type"] = numberMessage; }

        StripValidation(control);
    }

    private static void StripValidation(TagBuilder control)
    {
        foreach (string key in control.Attributes.Keys.Where(k => k.StartsWith("data-val", StringComparison.Ordinal)).ToList())
        {
            control.Attributes.Remove(key);
        }
    }

    private string PasswordLabel() => _l["Show or hide the password"].Value;

    private static string InputType(string kind) => kind switch
    {
        "email" => "email",
        "number" => "number",
        "tel" => "tel",
        "url" => "url",
        "search" => "search",
        _ => "text",
    };

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
