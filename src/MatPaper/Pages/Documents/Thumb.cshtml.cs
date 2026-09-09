using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MatPaper.Data;

namespace MatPaper.Pages.Documents;

public class ThumbModel : PageModel
{
    private readonly AppDbContext _db;

    public ThumbModel(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> OnGetAsync(Guid token)
    {
        var document = await _db.Documents
            .AsNoTracking()
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
