using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Services;

/// <summary>
/// Which addresses are a page of the app. The breadcrumb mirrors the address, and each crumb but the last leads back - but only to an address that is a page:
/// <c>/System/ImportGroups</c> has none (the groups are listed with the import tasks), so its crumb is plain text and not a link to an error.
/// </summary>
public sealed class PageRoutes(IActionDescriptorCollectionProvider actions)
{
    private HashSet<string>? _known;

    public bool Exists(string path) => (_known ??= Build()).Contains(Normalise(path));

    private HashSet<string> Build()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in actions.ActionDescriptors.Items.OfType<PageActionDescriptor>())
        {
            var template = page.AttributeRouteInfo?.Template;
            if (!string.IsNullOrEmpty(template) && !template.Contains('{'))
            {
                known.Add(Normalise(template));
            }

            // a page without a route of its own: its folder and its name ("/System/Users/Index" is "/System/Users")
            var view = page.ViewEnginePath ?? string.Empty;
            known.Add(Normalise(view.EndsWith("/Index", StringComparison.OrdinalIgnoreCase) ? view[..^"/Index".Length] : view));
        }

        return known;
    }

    private static string Normalise(string path) => "/" + path.Trim('/');
}
