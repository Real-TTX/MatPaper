using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Pages.System.AppConnections;

/// <summary>
/// The connections of MatPaper to other apps (the Mat app model, MatDesign docs/APP-MODEL.md): the Connections area of every Mat app (<c>&lt;mat-connections /&gt;</c>
/// of MatPlatform: what MatPaper uses, how each was set, what it adds to a home screen). MatPaper uses only the sign-in through MatIdentity, set by its variables
/// (Mattiverse passes them), so there is nothing to enter by hand here. Administrators only (the folder <c>/System</c>, and
/// <c>MatPlatformOptions.ConnectionsAccess</c> for the forms of the area). Not to be mixed up with <c>/System/Connections</c>: MatPaper's own connections to
/// shares, mailboxes and cloud drives.
/// </summary>
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
