using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Pages.System;

public class IndexModel : PageModel
{
    public void OnGet()
    {
        ViewData["Breadcrumb"] = "System";
    }
}
