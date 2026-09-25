namespace MatPaper;

/// <summary>
/// Marker type for the app-wide translation table (<c>Resources/SharedResource.*.resx</c>).
/// <para>
/// The resource KEY is the English text, so a string without a translation still renders
/// its English original — pages can therefore be localised one at a time. In a view use
/// the injected localizer: <c>@L["Documents"]</c>; in a PageModel inject
/// <c>IStringLocalizer&lt;SharedResource&gt;</c>.
/// </para>
/// </summary>
public sealed class SharedResource
{
}
