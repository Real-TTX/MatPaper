using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MatPaper.Services;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace MatPaper.Controls;

/// <summary>What the children of a toolbar tell it: the search, the sort options, the view switch.</summary>
internal sealed class ToolbarState
{
    public string? SearchName { get; set; }

    public string? SearchValue { get; set; }

    public string? SearchPlaceholder { get; set; }

    public List<(string Value, string Text)> SortOptions { get; } = [];

    public string? SortName { get; set; }

    public string? SortValue { get; set; }

    public string? ViewParam { get; set; }

    public string? ViewValue { get; set; }
}

/// <summary>
/// The toolbar of a list or a gallery as the toolkit has it (rules LIST-02 to LIST-04, DIALOG-02): the search is a field at the left of the bar (or a button
/// that opens a dialog), Filter and Sort are buttons that open dialogs, the count and the switch between gallery and list sit at the right. A tool that
/// is in effect shows on its button. All the fields of all the dialogs belong to ONE hidden GET form (the <c>form</c> attribute of every field), so every
/// dialog sends the complete state and a result can be shared as a link.
/// <para>
/// The children say what the tools are: &lt;mp-toolbar-search&gt; (name, value, placeholder), &lt;mp-toolbar-sort&gt; with &lt;option&gt;s,
/// &lt;mp-view-switch&gt;; everything else - <c>mp-picker</c>, <c>mp-toolbar-select</c>, a <c>mat-field</c> with an input - is a filter and lands in the
/// filter dialog. The toolbar is the first child of <c>&lt;div class="mat-list"&gt;</c>; the table or the gallery, the pager and the actions
/// follow in any order (the toolkit orders them).
/// </para>
/// Usage: &lt;mp-toolbar id="docs" label="Documents" count="Model.TotalCount" singular="{0} document" plural="{0} documents"&gt; … &lt;/mp-toolbar&gt;
/// </summary>
[HtmlTargetElement("mp-toolbar")]
public sealed partial class ToolbarTagHelper : TagHelper
{
    private readonly IStringLocalizer<SharedResource> _l;

    public ToolbarTagHelper(IStringLocalizer<SharedResource> l) => _l = l;

    /// <summary>Unique on the page: the ids of the form and of the dialogs derive from it.</summary>
    [HtmlAttributeName("id")]
    public string Id { get; set; } = "list";

    /// <summary>Names the list for screen readers ("Documents tools") and the search field.</summary>
    [HtmlAttributeName("label")]
    public string? Label { get; set; }

    /// <summary>The address the form goes to; default the current page.</summary>
    [HtmlAttributeName("action")]
    public string? Action { get; set; }

    /// <summary>Current search text (or let a &lt;mp-toolbar-search&gt; child say it).</summary>
    [HtmlAttributeName("search")]
    public string? Search { get; set; }

    [HtmlAttributeName("search-name")]
    public string? SearchName { get; set; }

    [HtmlAttributeName("search-placeholder")]
    public string? SearchPlaceholder { get; set; }

    /// <summary>The search is a field in the toolbar (default) or a button that opens a dialog.</summary>
    [HtmlAttributeName("search-field")]
    public bool SearchField { get; set; } = true;

    /// <summary>How many entries match, over all pages; the toolbar says it ("12 documents").</summary>
    [HtmlAttributeName("count")]
    public int? Count { get; set; }

    [HtmlAttributeName("singular")]
    public string Singular { get; set; } = "{0} result";

    [HtmlAttributeName("plural")]
    public string Plural { get; set; } = "{0} results";

    /// <summary>Current sort value (or let the sort child say it) and the one that is the default order.</summary>
    [HtmlAttributeName("sort")]
    public string? Sort { get; set; }

    [HtmlAttributeName("sort-default")]
    public string? SortDefault { get; set; }

    /// <summary>Query values that are neither search, filter nor sort and must survive an "Apply" (names, comma-separated).</summary>
    [HtmlAttributeName("keep")]
    public string? Keep { get; set; }

    /// <summary>"sm" (default) or "md": the size of the filter dialog; the fields stand in two columns in "md".</summary>
    [HtmlAttributeName("filter-size")]
    public string FilterSize { get; set; } = "sm";

    /// <summary>How many filters are in effect, when the page knows better than the query string does.</summary>
    [HtmlAttributeName("active-filters")]
    public int? ActiveFilters { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var state = new ToolbarState();
        context.Items[typeof(ToolbarState)] = state;
        string filters = (await output.GetChildContentAsync()).GetContent();

        var request = ViewContext.HttpContext.Request;
        string form = Id + "-form";
        string action = Action ?? (request.PathBase + request.Path);
        string label = Label ?? _l["List"].Value;

        string searchName = SearchName ?? state.SearchName ?? "Search";
        string search = Search ?? state.SearchValue ?? string.Empty;
        string placeholder = SearchPlaceholder ?? state.SearchPlaceholder ?? _l["Search"].Value;
        bool searchActive = !string.IsNullOrWhiteSpace(search);

        string sortName = state.SortName ?? "Sort";
        string sort = Sort ?? state.SortValue ?? SortDefault ?? state.SortOptions.FirstOrDefault().Value ?? string.Empty;
        string sortDefault = SortDefault ?? state.SortOptions.FirstOrDefault().Value ?? string.Empty;
        bool sortActive = state.SortOptions.Count > 0 && sort != sortDefault;
        string sortLabel = state.SortOptions.FirstOrDefault(o => o.Value == sort).Text ?? string.Empty;

        // The fields of the filter dialog stand outside the form: each one points to it.
        filters = ControlTag().Replace(filters, m => m.Groups[2].Value.Contains(" form=", StringComparison.OrdinalIgnoreCase)
            ? m.Value
            : $"<{m.Groups[1].Value} form=\"{form}\"{m.Groups[2].Value}>");
        bool hasFilters = !string.IsNullOrWhiteSpace(filters);
        int active = ActiveFilters ?? CountActive(filters, [searchName, sortName, state.ViewParam ?? "View", "PageNumber", "__RequestVerificationToken"]);

        string info = Count is int n
            ? _l[n == 1 ? Singular : Plural, n.ToString("N0", CultureInfo.CurrentCulture)].Value
            : string.Empty;

        var sb = new StringBuilder();
        sb.Append($"<div class=\"mat-toolbar\" role=\"toolbar\" aria-label=\"{Enc(_l["{0} tools", label].Value)}\">");
        if (SearchField)
        {
            sb.Append("<div class=\"mat-input-icon mat-toolbar__search\">").Append(Ui.IconHtml("search"));
            sb.Append($"<input class=\"mat-input\" type=\"search\" id=\"{Id}-q\" name=\"{Enc(searchName)}\" form=\"{form}\" value=\"{Enc(search)}\" placeholder=\"{Enc(placeholder)}\" aria-label=\"{Enc(placeholder)}\" autocomplete=\"off\" enterkeyhint=\"search\" data-shortcut=\"/\" /></div>");
            sb.Append("<span class=\"mat-toolbar__spacer\"></span>");
            Info(sb, info);
            sb.Append("<div class=\"mat-toolbar__end\">");
            FilterButton(sb, hasFilters, active);
            SortButton(sb, state, sortActive, sortLabel);
            ViewSwitch(sb, state, request);
            sb.Append("</div>");
        }
        else
        {
            sb.Append($"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-toolbar__btn{(searchActive ? " is-active" : "")}\" data-dialog-open=\"{Id}-search\" data-shortcut=\"/\" aria-haspopup=\"dialog\">{Ui.IconHtml("search")}<span>{Enc(_l["Search"].Value)}</span>");
            if (searchActive)
            {
                sb.Append($"<small>“{Enc(search)}”</small>");
            }

            sb.Append("</button>");
            FilterButton(sb, hasFilters, active);
            SortButton(sb, state, sortActive, sortLabel);
            sb.Append("<span class=\"mat-toolbar__spacer\"></span>");
            Info(sb, info);
            ViewSwitch(sb, state, request, wrap: true);
        }

        sb.Append("</div>");

        // The form that carries every tool: nothing but what the dialogs hold, and what the page keeps (the query values that are no tool).
        sb.Append($"<form id=\"{form}\" method=\"get\" action=\"{Enc(action)}\" hidden>");
        foreach (string name in (Keep ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string? value in request.Query[name])
            {
                if (!string.IsNullOrEmpty(value))
                {
                    sb.Append($"<input type=\"hidden\" name=\"{Enc(name)}\" value=\"{Enc(value)}\" />");
                }
            }
        }

        // The default button of the form: Enter in a field of a dialog (or in the search field) sends it.
        sb.Append("<button type=\"submit\" tabindex=\"-1\" aria-hidden=\"true\"></button></form>");

        if (!SearchField)
        {
            SearchDialog(sb, searchName, search, placeholder, action, request);
        }

        if (hasFilters)
        {
            FilterDialog(sb, filters, action);
        }

        if (state.SortOptions.Count > 0)
        {
            SortDialog(sb, state, sortName, sort, request);
        }

        output.TagName = null;
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Content.SetHtmlContent(sb.ToString());
    }

    private void Info(StringBuilder sb, string info)
    {
        if (info.Length > 0)
        {
            sb.Append($"<span class=\"mat-toolbar__info\" aria-live=\"polite\">{Enc(info)}</span>");
        }
    }

    private void FilterButton(StringBuilder sb, bool hasFilters, int active)
    {
        if (!hasFilters)
        {
            return;
        }

        sb.Append($"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-toolbar__btn{(active > 0 ? " is-active" : "")}\" data-dialog-open=\"{Id}-filter\" aria-haspopup=\"dialog\">{Ui.IconHtml("sliders")}<span>{Enc(_l["Filter"].Value)}</span>");
        if (active > 0)
        {
            sb.Append($"<span class=\"mat-badge\">{active}</span>");
        }

        sb.Append("</button>");
    }

    private void SortButton(StringBuilder sb, ToolbarState state, bool sortActive, string sortLabel)
    {
        if (state.SortOptions.Count == 0)
        {
            return;
        }

        sb.Append($"<button type=\"button\" class=\"mat-btn mat-btn--ghost mat-toolbar__btn{(sortActive ? " is-active" : "")}\" data-dialog-open=\"{Id}-sort\" aria-haspopup=\"dialog\">{Ui.IconHtml("sort")}<span>{Enc(_l["Sort"].Value)}</span>");
        if (sortActive)
        {
            sb.Append($"<small>{Enc(sortLabel)}</small>");
        }

        sb.Append("</button>");
    }

    /// <summary>The switch between gallery and list: links, because the page reads the choice from the query and remembers it.</summary>
    private void ViewSwitch(StringBuilder sb, ToolbarState state, Microsoft.AspNetCore.Http.HttpRequest request, bool wrap = false)
    {
        if (state.ViewParam is null)
        {
            return;
        }

        string title = Enc(_l["Display as"].Value);
        if (wrap)
        {
            sb.Append("<div class=\"mat-toolbar__end\">");
        }

        sb.Append($"<div class=\"mat-seg\" role=\"group\" aria-label=\"{title}\">");
        foreach (var (value, icon, text) in new[] { ("gallery", "grid", _l["Gallery"].Value), ("list", "list", _l["List"].Value) })
        {
            bool on = string.Equals(state.ViewValue, value, StringComparison.OrdinalIgnoreCase);
            string href = Enc(PageLinks.With(request.Query, state.ViewParam, value, "PageNumber"));
            sb.Append($"<a class=\"mat-seg__btn\" href=\"{href}\" aria-pressed=\"{(on ? "true" : "false")}\" aria-label=\"{Enc(text)}\" data-tip=\"{Enc(text)}\">{Ui.IconHtml(icon)}</a>");
        }

        sb.Append("</div>");
        if (wrap)
        {
            sb.Append("</div>");
        }
    }

    private void Head(StringBuilder sb, string dialogId, string icon, string title)
        => sb.Append($"<div class=\"mat-dialog__head\">{Ui.IconHtml(icon)}<h2 class=\"mat-dialog__title\" id=\"{dialogId}-title\">{Enc(title)}</h2><button type=\"button\" class=\"mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close\" data-dialog-close aria-label=\"{Enc(_l["Close"].Value)}\">{Ui.IconHtml("x")}</button></div>");

    private void Foot(StringBuilder sb, string icon, string resetText, string resetHref)
        => sb.Append("<div class=\"mat-dialog__foot\"><div class=\"mat-btn-row mat-btn-row--plain\">")
            .Append($"<button type=\"submit\" form=\"{Id}-form\" class=\"mat-btn mat-btn--primary\">{Ui.IconHtml(icon)}<span>{Enc(_l["Apply"].Value)}</span></button>")
            .Append($"<button type=\"button\" class=\"mat-btn mat-btn--secondary\" data-dialog-close>{Enc(_l["Cancel"].Value)}</button>")
            .Append("<span class=\"mat-btn-row__spacer\"></span>")
            .Append($"<a class=\"mat-btn mat-btn--ghost mat-btn--end\" href=\"{Enc(resetHref)}\">{Enc(resetText)}</a>")
            .Append("</div></div>");

    private void SearchDialog(StringBuilder sb, string name, string value, string placeholder, string action, Microsoft.AspNetCore.Http.HttpRequest request)
    {
        string id = Id + "-search";
        sb.Append($"<dialog class=\"mat-dialog mat-dialog--sm\" id=\"{id}\" aria-labelledby=\"{id}-title\"><div class=\"mat-dialog__form\">");
        Head(sb, id, "search", _l["Search"].Value);
        sb.Append($"<div class=\"mat-dialog__body\"><div class=\"mat-field\"><label class=\"mat-label\" for=\"{Id}-q\">{Enc(_l["Search for"].Value)}</label>")
            .Append($"<input class=\"mat-input\" type=\"search\" id=\"{Id}-q\" name=\"{Enc(name)}\" form=\"{Id}-form\" value=\"{Enc(value)}\" placeholder=\"{Enc(placeholder)}\" autocomplete=\"off\" />")
            .Append($"<span class=\"mat-hint\">{Enc(_l["Press / on the page to open this dialog."].Value)}</span></div></div>");
        Foot(sb, "search", _l["Clear search"].Value, FilterUrl.Without(request, name));
        sb.Append("</div></dialog>");
    }

    private void FilterDialog(StringBuilder sb, string filters, string action)
    {
        string id = Id + "-filter";
        bool wide = FilterSize == "md";
        sb.Append($"<dialog class=\"mat-dialog{(wide ? "" : " mat-dialog--sm")}\" id=\"{id}\" aria-labelledby=\"{id}-title\"><div class=\"mat-dialog__form\">");
        Head(sb, id, "sliders", _l["Filter"].Value);
        sb.Append("<div class=\"mat-dialog__body mat-form\">");
        sb.Append(wide ? "<div class=\"mat-form__grid\">" : "<div class=\"mat-stack\">").Append(filters).Append("</div>");
        sb.Append("</div>");
        Foot(sb, "sliders", _l["Reset everything"].Value, action);
        sb.Append("</div></dialog>");
    }

    private void SortDialog(StringBuilder sb, ToolbarState state, string name, string current, Microsoft.AspNetCore.Http.HttpRequest request)
    {
        string id = Id + "-sort";
        sb.Append($"<dialog class=\"mat-dialog mat-dialog--sm\" id=\"{id}\" aria-labelledby=\"{id}-title\"><div class=\"mat-dialog__form\">");
        Head(sb, id, "sort", _l["Sort"].Value);
        sb.Append("<div class=\"mat-dialog__body mat-form\"><fieldset class=\"mat-field mat-fieldset\">");
        sb.Append($"<legend class=\"mat-label\">{Enc(_l["Sort by"].Value)}</legend><div class=\"mat-options\">");
        int i = 0;
        foreach (var (value, text) in state.SortOptions)
        {
            sb.Append($"<label class=\"mat-option\"><input type=\"radio\" id=\"{id}-{i++}\" name=\"{Enc(name)}\" value=\"{Enc(value)}\" form=\"{Id}-form\"{(value == current ? " checked" : "")} /><span>{Enc(text)}</span></label>");
        }

        sb.Append("</div></fieldset></div>");
        Foot(sb, "sort", _l["Default order"].Value, FilterUrl.Without(request, name));
        sb.Append("</div></dialog>");
    }

    /// <summary>The filters in effect: the names of the fields in the dialog that the query string gives a value other than "all".</summary>
    private int CountActive(string filters, string[] excluded)
    {
        var query = ViewContext.HttpContext.Request.Query;
        var names = FieldName().Matches(filters).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).Where(n => !excluded.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
        return names.Count(n => query[n].Any(v => !string.IsNullOrWhiteSpace(v) && !string.Equals(v, "all", StringComparison.OrdinalIgnoreCase)));
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    [GeneratedRegex(@"<(input|select|textarea)(?=[\s/>])([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ControlTag();

    [GeneratedRegex(@"<(?:input|select|textarea)\b[^>]*?\sname=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FieldName();
}

/// <summary>
/// The search of a list toolbar: name of the query value, its current value and the placeholder. It draws nothing itself; the toolbar draws the field.
/// Usage: &lt;mp-toolbar-search name="Search" value="@Model.Search" placeholder="Full-text search" /&gt;
/// </summary>
[HtmlTargetElement("mp-toolbar-search", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ToolbarSearchTagHelper : TagHelper
{
    /// <summary>Query-string parameter name.</summary>
    [HtmlAttributeName("name")]
    public string Name { get; set; } = "Search";

    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    [HtmlAttributeName("placeholder")]
    public string? Placeholder { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(ToolbarState), out var found) && found is ToolbarState state)
        {
            state.SearchName = Name;
            state.SearchValue = Value;
            state.SearchPlaceholder = Placeholder;
        }

        output.SuppressOutput();
    }
}

/// <summary>
/// A labelled select for the filter dialog of a toolbar: a <c>mat-field</c> with a label and a native <c>mat-select</c>; give &lt;option&gt; children, the
/// one whose value equals <c>value</c> is marked selected. A select named like the sort (<c>Sort</c>) is the sort of the list instead: it becomes the
/// options of the Sort dialog (the same as &lt;mp-toolbar-sort&gt;).
/// Usage: &lt;mp-toolbar-select name="Scope" label="Scope" value="Model.Scope"&gt;&lt;option …&gt;…&lt;/mp-toolbar-select&gt;
/// </summary>
[HtmlTargetElement("mp-toolbar-select")]
public sealed partial class ToolbarSelectTagHelper : TagHelper
{
    /// <summary>Query-string parameter name. Also used as the element id.</summary>
    [HtmlAttributeName("name")]
    public string Name { get; set; } = default!;

    [HtmlAttributeName("label")]
    public string Label { get; set; } = default!;

    /// <summary>Currently selected value; the matching &lt;option value="…"&gt; is marked selected.</summary>
    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        string options = (await output.GetChildContentAsync()).GetContent();

        if (string.Equals(Name, "Sort", StringComparison.OrdinalIgnoreCase)
            && context.Items.TryGetValue(typeof(ToolbarState), out var found) && found is ToolbarState state)
        {
            ToolbarSortTagHelper.Register(state, Name, Value, options);
            output.SuppressOutput();
            return;
        }

        string id = WebUtility.HtmlEncode(Name);
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "mat-field");
        output.Content.SetHtmlContent(
            $"<label class=\"mat-label\" for=\"{id}\">{WebUtility.HtmlEncode(Label ?? string.Empty)}</label>" +
            $"<select class=\"mat-select\" id=\"{id}\" name=\"{id}\">{HtmlOptionHelper.MarkSelected(options, Value)}</select>");
    }
}

/// <summary>
/// The sort options of a list: &lt;option value="newest"&gt;Newest first&lt;/option&gt; … The toolbar shows them in its Sort dialog; the first one is the default
/// order unless the toolbar says otherwise.
/// Usage: &lt;mp-toolbar-sort value="@Model.Sort"&gt; &lt;option …&gt; &lt;/mp-toolbar-sort&gt;
/// </summary>
[HtmlTargetElement("mp-toolbar-sort")]
public sealed partial class ToolbarSortTagHelper : TagHelper
{
    [HtmlAttributeName("name")]
    public string Name { get; set; } = "Sort";

    [HtmlAttributeName("value")]
    public string? Value { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(ToolbarState), out var found) && found is ToolbarState state)
        {
            Register(state, Name, Value, (await output.GetChildContentAsync()).GetContent());
        }

        output.SuppressOutput();
    }

    internal static void Register(ToolbarState state, string name, string? value, string optionsHtml)
    {
        state.SortName = name;
        state.SortValue = value;
        state.SortOptions.Clear();
        foreach (Match option in OptionTag().Matches(optionsHtml))
        {
            var v = ValueAttribute().Match(option.Groups[1].Value);
            state.SortOptions.Add((v.Success ? WebUtility.HtmlDecode(v.Groups[1].Value) : string.Empty, WebUtility.HtmlDecode(option.Groups[2].Value).Trim()));
        }
    }

    [GeneratedRegex(@"<option\b([^>]*)>(.*?)</option>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex OptionTag();

    [GeneratedRegex(@"value\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttribute();
}

/// <summary>
/// The choice between gallery and list of a toolbar (rule LIST-10): two segments at the right of the bar. They are links: the page reads the choice from
/// the query string (and remembers it), so the switch works without a script; the page number is dropped, because the two views do not hold the same
/// number of entries per page. The toolbar draws it.
/// Usage: &lt;mp-view-switch current="@Model.View" /&gt;
/// </summary>
[HtmlTargetElement("mp-view-switch", TagStructure = TagStructure.WithoutEndTag)]
public sealed class ViewSwitchTagHelper : TagHelper
{
    /// <summary>"gallery" or "list".</summary>
    [HtmlAttributeName("current")]
    public string Current { get; set; } = "gallery";

    [HtmlAttributeName("param")]
    public string Param { get; set; } = "View";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (context.Items.TryGetValue(typeof(ToolbarState), out var found) && found is ToolbarState state)
        {
            state.ViewParam = Param;
            state.ViewValue = Current;
        }

        output.SuppressOutput();
    }
}
