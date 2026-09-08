using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatPaper.Pages;

public class IndexModel : PageModel
{
    public int DocumentCount { get; private set; }
    public int Last7DaysCount { get; private set; }
    public int OcrQueueCount { get; private set; }
    public int FailedTaskCount { get; private set; }

    public void OnGet()
    {
        ViewData["Breadcrumb"] = "Dashboard";

        // Phase 1: all KPIs are placeholders and show 0.
        DocumentCount = 0;
        Last7DaysCount = 0;
        OcrQueueCount = 0;
        FailedTaskCount = 0;
    }
}
