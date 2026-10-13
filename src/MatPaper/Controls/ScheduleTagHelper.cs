using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// A friendly schedule builder that edits a 5-field cron string, made of the toolkit's controls. It renders a <c>mat-field</c> with a mode selector
/// (Manual / Hourly / Daily / Weekly / Monthly / Custom) plus the matching inputs and posts the computed cron through a hidden input whose name matches
/// the bound property - so PageModels keep binding a plain <c>string? CronExpression</c> with no change. Behaviour lives in <c>schedule.js</c>; times are
/// wall-clock times in the configured app time zone.
/// Usage: &lt;mp-schedule asp-for="Input.CronExpression" label="Schedule" /&gt;
/// </summary>
[HtmlTargetElement("mp-schedule", Attributes = "asp-for")]
public sealed class ScheduleTagHelper : TagHelper
{
    private readonly Services.Fmt _fmt;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> _l;

    public ScheduleTagHelper(
        Services.Fmt fmt,
        Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> l)
    {
        _fmt = fmt;
        _l = l;
    }

    [HtmlAttributeName("asp-for")]
    public ModelExpression For { get; set; } = default!;

    [HtmlAttributeName("label")]
    public string Label { get; set; } = "Schedule";

    [HtmlAttributeName("help")]
    public string? Help { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;
        var fullName = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        var id = TagBuilder.CreateSanitizedId(fullName, "_");
        var current = For.Model?.ToString() ?? string.Empty;
        var error = ViewContext.ViewData.ModelState.TryGetValue(fullName, out var entry) && entry.Errors.Count > 0
            ? entry.Errors.Select(e => e.ErrorMessage).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "?"
            : null;

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", Classes.Merge("mat-field mp-schedule", error is null ? null : "is-invalid", output.Attributes["class"]?.Value?.ToString()));
        output.Attributes.SetAttribute("data-name", fullName);
        output.Attributes.SetAttribute("data-cron", current);
        output.Attributes.SetAttribute("data-zone", _fmt.ZoneName);
        // Sentence templates for the live summary rendered by schedule.js.
        output.Attributes.SetAttribute("data-t-manual", _l["Runs only when started manually."].Value);
        output.Attributes.SetAttribute("data-t-hourly", _l["Every hour at minute {0}."].Value);
        output.Attributes.SetAttribute("data-t-daily", _l["Every day at {0}"].Value);
        output.Attributes.SetAttribute("data-t-weekly", _l["Every {0} at {1}"].Value);
        output.Attributes.SetAttribute("data-t-monthly", _l["On day {0} of each month at {1}"].Value);

        var w = new StringBuilder();
        w.Append($"<label class=\"mat-label\" for=\"{id}__mode\">{enc.Encode(Label)}</label>");
        w.Append($"<input type=\"hidden\" class=\"mp-schedule__value\" name=\"{enc.Encode(fullName)}\" value=\"{enc.Encode(current)}\">");

        w.Append("<div class=\"mp-schedule__controls\">");
        w.Append($"<select class=\"mat-select mp-schedule__mode\" id=\"{id}__mode\">");
        w.Append($"<option value=\"manual\">{enc.Encode(_l["Manual only"].Value)}</option>");
        w.Append($"<option value=\"hourly\">{enc.Encode(_l["Hourly"].Value)}</option>");
        w.Append($"<option value=\"daily\">{enc.Encode(_l["Daily"].Value)}</option>");
        w.Append($"<option value=\"weekly\">{enc.Encode(_l["Weekly"].Value)}</option>");
        w.Append($"<option value=\"monthly\">{enc.Encode(_l["Monthly"].Value)}</option>");
        w.Append($"<option value=\"custom\">{enc.Encode(_l["Custom (cron)"].Value)}</option>");
        w.Append("</select>");

        w.Append($"<label class=\"mp-schedule__part\" data-modes=\"hourly\" hidden><span>{enc.Encode(_l["at minute"].Value)}</span> <input type=\"number\" class=\"mat-input mp-schedule__minute\" min=\"0\" max=\"59\" value=\"0\" inputmode=\"numeric\"></label>");

        w.Append("<span class=\"mp-schedule__part\" data-modes=\"weekly\" hidden><select class=\"mat-select mp-schedule__weekday\" aria-label=\"")
            .Append(enc.Encode(_l["Weekday"].Value)).Append("\">");
        string[] days =
        {
            _l["Monday"].Value, _l["Tuesday"].Value, _l["Wednesday"].Value, _l["Thursday"].Value,
            _l["Friday"].Value, _l["Saturday"].Value, _l["Sunday"].Value
        };
        int[] vals = { 1, 2, 3, 4, 5, 6, 0 };
        for (var i = 0; i < days.Length; i++)
        {
            w.Append($"<option value=\"{vals[i]}\">{enc.Encode(days[i])}</option>");
        }
        w.Append("</select></span>");

        w.Append($"<label class=\"mp-schedule__part\" data-modes=\"monthly\" hidden><span>{enc.Encode(_l["day"].Value)}</span> <input type=\"number\" class=\"mat-input mp-schedule__dom\" min=\"1\" max=\"31\" value=\"1\" inputmode=\"numeric\"></label>");
        w.Append($"<div class=\"mp-schedule__part\" data-modes=\"daily weekly monthly\" hidden><span>{enc.Encode(_l["at"].Value)}</span> <div class=\"mat-picker\" data-picker=\"time\" data-step=\"5\"><input class=\"mat-picker__input mp-schedule__time\" id=\"{id}__time\" value=\"03:00\" aria-label=\"{enc.Encode(_l["Time"].Value)}\"></div></div>");
        w.Append($"<span class=\"mp-schedule__part mp-schedule__part--wide\" data-modes=\"custom\" hidden><input type=\"text\" class=\"mat-input mat-mono mp-schedule__cron\" placeholder=\"*/15 * * * *\" aria-label=\"{enc.Encode(_l["Custom (cron)"].Value)}\"></span>");
        w.Append("</div>");

        w.Append("<span class=\"mat-hint mp-schedule__summary\" aria-live=\"polite\"></span>");
        if (!string.IsNullOrEmpty(Help))
        {
            w.Append($"<span class=\"mat-hint\">{enc.Encode(Help)}</span>");
        }

        // Without this an invalid cron is refused silently.
        w.Append($"<span class=\"mat-error\" id=\"{id}-error\" aria-live=\"polite\">{enc.Encode(error ?? string.Empty)}</span>");

        output.Content.SetHtmlContent(w.ToString());
    }
}
