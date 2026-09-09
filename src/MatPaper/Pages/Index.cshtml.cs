using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db) => _db = db;

    public int DocumentCount { get; private set; }
    public int Last7DaysCount { get; private set; }
    public int OcrQueueCount { get; private set; }
    public int FailedTaskCount { get; private set; }

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Dashboard";

        var documents = _db.Documents.Where(d => d.UpdateState != UpdateState.Deleted);
        var since = DateTime.UtcNow.AddDays(-7);

        DocumentCount = await documents.CountAsync();
        Last7DaysCount = await documents.CountAsync(d => d.CreateDate >= since);
        OcrQueueCount = await documents.CountAsync(d => d.OcrState == OcrState.Pending);
        FailedTaskCount = await documents.CountAsync(d => d.OcrState == OcrState.Failed);
    }
}
