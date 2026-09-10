using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// A searchable select control that replaces long native &lt;select&gt; dropdowns and
/// long[] checkbox groups with a dialog-based picker. Renders a <c>.form-row</c>
/// containing a &lt;label&gt; and a <c>.mp-picker</c> widget.
/// <para>
/// Server-side binding is intentionally identical to a plain <c>&lt;select asp-for&gt;</c>
/// (single mode) or a checkbox group bound to a collection (multiple mode): the value(s)
/// post through hidden inputs whose name is the full field name derived from the model
/// expression. Existing PageModels therefore need no change.
/// </para>
/// <para>
/// Single mode expects a scalar property (e.g. <c>long?</c>); multiple mode expects a
/// collection (e.g. <c>long[]</c> / <c>IEnumerable&lt;long&gt;</c>). Options come from
/// <c>asp-items</c>. The client behaviour lives in <c>picker.js</c> / <c>picker.css</c>;
/// when &lt;dialog&gt; is unsupported the hidden inputs still post their current values.
/// </para>
/// </summary>
[HtmlTargetElement("mp-picker", Attributes = ForAttributeName + "," + ItemsAttributeName)]
public sealed class PickerTagHelper : TagHelper
{
    private const string ForAttributeName = "asp-for";
    private const string ItemsAttributeName = "asp-items";

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

    /// <summary>Placeholder shown when nothing is selected. Defaults per mode.</summary>
    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    /// <summary>Optional help text rendered as <c>.form-help</c> under the widget.</summary>
    [HtmlAttributeName("help")]
    public string? Help { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        HtmlEncoder enc = HtmlEncoder.Default;

        string fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        string fieldId = TagBuilder.CreateSanitizedId(fullName, "_");
        string widgetId = $"{fieldId}__picker";
        string placeholder = Placeholder ?? (Multiple ? "Add…" : "— None —");

        IReadOnlyList<SelectListItem> items = Items as IReadOnlyList<SelectListItem> ?? Items.ToList();
        HashSet<string> selected = ResolveSelectedValues();

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "form-row");

        // Label
        string labelText = Label ?? For.Metadata.DisplayName ?? For.Name;
        output.Content.AppendHtml(
            $"<label for=\"{enc.Encode(widgetId)}\">{enc.Encode(labelText)}</label>");

        // Widget root
        string optionsJson = BuildOptionsJson(items);
        string selectedAttr = string.Join(",", selected.Select(enc.Encode));

        var widget = new StringBuilder();
        widget.Append("<div class=\"mp-picker\"");
        widget.Append($" id=\"{enc.Encode(widgetId)}\"");
        widget.Append($" data-multiple=\"{(Multiple ? "true" : "false")}\"");
        widget.Append($" data-name=\"{enc.Encode(fullName)}\"");
        widget.Append($" data-selected=\"{selectedAttr}\"");
        widget.Append($" data-placeholder=\"{enc.Encode(placeholder)}\">");

        widget.Append($"<script type=\"application/json\" class=\"mp-picker__data\">{optionsJson}</script>");

        if (Multiple)
        {
            AppendMultiple(widget, items, selected, fullName, placeholder, enc);
        }
        else
        {
            AppendSingle(widget, items, selected, fullName, placeholder, enc);
        }

        widget.Append("</div>");
        output.Content.AppendHtml(widget.ToString());

        // Validation + help (match other fields)
        output.Content.AppendHtml(
            $"<span class=\"field-error\" data-valmsg-for=\"{enc.Encode(fullName)}\" data-valmsg-replace=\"true\"></span>");

        if (!string.IsNullOrEmpty(Help))
        {
            output.Content.AppendHtml($"<p class=\"form-help\">{enc.Encode(Help)}</p>");
        }
    }

    private static void AppendSingle(
        StringBuilder w, IReadOnlyList<SelectListItem> items, HashSet<string> selected,
        string fullName, string placeholder, HtmlEncoder enc)
    {
        string? current = selected.Count > 0 ? selected.First() : null;
        SelectListItem? match = current is null
            ? null
            : items.FirstOrDefault(i => string.Equals(i.Value, current, StringComparison.Ordinal));

        string display = match?.Text ?? placeholder;
        bool isPlaceholder = match is null;

        w.Append($"<input type=\"hidden\" class=\"mp-picker__value\" name=\"{enc.Encode(fullName)}\" value=\"{enc.Encode(current ?? string.Empty)}\">");

        w.Append("<button type=\"button\" class=\"mp-picker__trigger form-control\"");
        w.Append(" aria-haspopup=\"dialog\"");
        if (isPlaceholder)
        {
            w.Append(" data-placeholder-shown=\"true\"");
        }

        w.Append(">");
        w.Append($"<span class=\"mp-picker__label\">{enc.Encode(display)}</span>");
        w.Append("<span class=\"mp-picker__caret\" aria-hidden=\"true\">▾</span>");
        w.Append("</button>");
    }

    private static void AppendMultiple(
        StringBuilder w, IReadOnlyList<SelectListItem> items, HashSet<string> selected,
        string fullName, string placeholder, HtmlEncoder enc)
    {
        w.Append("<div class=\"mp-picker__chips\">");
        foreach (SelectListItem item in items)
        {
            if (!selected.Contains(item.Value))
            {
                continue;
            }

            w.Append($"<span class=\"mp-picker__chip\" data-value=\"{enc.Encode(item.Value)}\">");
            w.Append($"<input type=\"hidden\" name=\"{enc.Encode(fullName)}\" value=\"{enc.Encode(item.Value)}\">");
            w.Append($"<span class=\"mp-picker__chip-text\">{enc.Encode(item.Text)}</span>");
            w.Append("<button type=\"button\" class=\"mp-picker__chip-remove\" aria-label=\"Remove\" tabindex=\"-1\">✕</button>");
            w.Append("</span>");
        }

        w.Append("</div>");

        w.Append("<button type=\"button\" class=\"mp-picker__trigger mp-picker__trigger--add form-control\"");
        w.Append(" aria-haspopup=\"dialog\">");
        w.Append($"<span class=\"mp-picker__label\">{enc.Encode(placeholder)}</span>");
        w.Append("<span class=\"mp-picker__caret\" aria-hidden=\"true\">＋</span>");
        w.Append("</button>");
    }

    private HashSet<string> ResolveSelectedValues()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        object? model = For.Model;
        if (model is null)
        {
            return result;
        }

        if (Multiple && model is IEnumerable enumerable && model is not string)
        {
            foreach (object? entry in enumerable)
            {
                if (entry is not null)
                {
                    result.Add(Convert.ToString(entry, CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }

            return result;
        }

        string? scalar = Convert.ToString(model, CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(scalar))
        {
            result.Add(scalar);
        }

        return result;
    }

    private static string BuildOptionsJson(IReadOnlyList<SelectListItem> items)
    {
        var options = new List<Dictionary<string, string>>(items.Count);
        foreach (SelectListItem item in items)
        {
            options.Add(new Dictionary<string, string>
            {
                ["v"] = item.Value ?? string.Empty,
                ["t"] = item.Text ?? string.Empty,
            });
        }

        // The default serializer escapes '<' to "<", so the payload is safe to embed
        // inside a <script type="application/json"> block (it cannot break out via "</script>").
        return JsonSerializer.Serialize(options);
    }
}
