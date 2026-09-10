using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

public class ThumbModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CurrentUser _currentUser;

    public ThumbModel(AppDbContext db, CurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IActionResult> OnGetAsync(Guid token)
    {
        var document = await _db.Documents
            .AsNoTracking()
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Token == token && d.UpdateState != UpdateState.Deleted);

        if (document?.ThumbnailPath == null)
        {
            return NotFound();
        }

        var dataDir = Environment.GetEnvironmentVariable("MATPAPER_DATA") ?? "/data";
        var path = Path.Combine(dataDir, "thumbnails", document.ThumbnailPath);

        if (!global::System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, "image/webp");
    }
}
