using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatPaper.Controls;

/// <summary>
/// A friendly schedule builder that edits a 5-field cron string. It renders a
/// mode selector (Manual / Hourly / Daily / Weekly / Monthly / Custom) plus the
/// matching inputs and posts the computed cron through a hidden input whose name
/// matches the bound property — so PageModels keep binding a plain
/// <c>string? CronExpression</c> with no change. Behaviour lives in
/// <c>schedule.js</c>; times are wall-clock times in the configured app time zone.
/// Usage: &lt;mp-schedule asp-for="Input.CronExpression" label="Schedule" /&gt;
/// </summary>
[HtmlTargetElement("mp-schedule", Attributes = "asp-for")]
public sealed class ScheduleTagHelper : TagHelper
{
    private readonly Services.Fmt _fmt;
    private readonly Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> _l;

    public ScheduleTagHelper(Services.Fmt fmt, Microsoft.Extensions.Localization.IStringLocalizer<SharedResource> l)
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
        var current = For.Model?.ToString() ?? string.Empty;

        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "form-row mp-schedule");
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
        w.Append($"<label>{enc.Encode(Label)}</label>");
        w.Append($"<input type=\"hidden\" class=\"mp-schedule__value\" name=\"{enc.Encode(fullName)}\" value=\"{enc.Encode(current)}\">");

        w.Append("<div class=\"mp-schedule__controls\">");
        w.Append("<select class=\"mp-schedule__mode form-control\">");
        w.Append($"<option value=\"manual\">{enc.Encode(_l["Manual only"].Value)}</option>");
        w.Append($"<option value=\"hourly\">{enc.Encode(_l["Hourly"].Value)}</option>");
        w.Append($"<option value=\"daily\">{enc.Encode(_l["Daily"].Value)}</option>");
        w.Append($"<option value=\"weekly\">{enc.Encode(_l["Weekly"].Value)}</option>");
        w.Append($"<option value=\"monthly\">{enc.Encode(_l["Monthly"].Value)}</option>");
        w.Append($"<option value=\"custom\">{enc.Encode(_l["Custom (cron)"].Value)}</option>");
        w.Append("</select>");

        w.Append($"<span class=\"mp-schedule__part\" data-modes=\"hourly\">{enc.Encode(_l["at minute"].Value)} <input type=\"number\" class=\"mp-schedule__minute form-control\" min=\"0\" max=\"59\" value=\"0\"></span>");
        w.Append($"<span class=\"mp-schedule__part\" data-modes=\"daily weekly monthly\">{enc.Encode(_l["at"].Value)} <input type=\"time\" class=\"mp-schedule__time form-control\" value=\"03:00\"></span>");

        w.Append("<span class=\"mp-schedule__part\" data-modes=\"weekly\"><select class=\"mp-schedule__weekday form-control\">");
        string[] days =
        {
            _l["Monday"].Value, _l["Tuesday"].Value, _l["Wednesday"].Value, _l["Thursday"].Value,
            _l["Friday"].Value, _l["Saturday"].Value, _l["Sunday"].Value
        };
        int[] vals = { 1, 2, 3, 4, 5, 6, 0 };
        for (var i = 0; i < days.Length; i++)
        {
            w.Append($"<option value=\"{vals[i]}\">{days[i]}</option>");
        }
        w.Append("</select></span>");

        w.Append($"<span class=\"mp-schedule__part\" data-modes=\"monthly\">{enc.Encode(_l["day"].Value)} <input type=\"number\" class=\"mp-schedule__dom form-control\" min=\"1\" max=\"31\" value=\"1\"></span>");
        w.Append("<span class=\"mp-schedule__part\" data-modes=\"custom\"><input type=\"text\" class=\"mp-schedule__cron form-control\" placeholder=\"*/15 * * * *\"></span>");
        w.Append("</div>");

        w.Append("<p class=\"mp-schedule__summary form-help\"></p>");

        if (!string.IsNullOrEmpty(Help))
        {
            w.Append($"<p class=\"form-help\">{enc.Encode(Help)}</p>");
        }

        output.Content.SetHtmlContent(w.ToString());
    }
}
