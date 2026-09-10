using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public IndexModel(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public int DocumentCount { get; private set; }
    public int Last7DaysCount { get; private set; }
    public int OcrQueueCount { get; private set; }
    public int FailedTaskCount { get; private set; }

    public async Task OnGetAsync()
    {
        ViewData["Breadcrumb"] = "Dashboard";

        var documents = _db.Documents
            .Where(d => d.UpdateState != UpdateState.Deleted)
            .AccessibleTo(_currentUser);
        var since = DateTime.UtcNow.AddDays(-7);

        DocumentCount = await documents.CountAsync();
        Last7DaysCount = await documents.CountAsync(d => d.CreateDate >= since);
        OcrQueueCount = await documents.CountAsync(d => d.OcrState == OcrState.Pending);
        FailedTaskCount = await documents.CountAsync(d => d.OcrState == OcrState.Failed);
    }
}
