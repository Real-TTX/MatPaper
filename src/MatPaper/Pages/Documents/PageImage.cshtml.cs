using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PDFtoImage;
using SkiaSharp;
using MatPaper.Data;
using MatPaper.Services;

namespace MatPaper.Pages.Documents;

/// <summary>
/// One page of a PDF as an image, for the phone's page viewer (a browser's own PDF viewer is
/// unusable on a phone: one page, no scrolling, no zoom). Rendered once per width and kept in
/// <c>{data}/pagecache</c>.
/// </summary>
public class PageImageModel : PageModel
{
    private const int MinWidth = 400;
    private const int MaxWidth = 2000;
    private const int DefaultWidth = 1100;

    private readonly AppDbContext _db;
    private readonly DocumentStorageService _storage;
    private readonly CurrentUser _currentUser;
    private readonly ILogger<PageImageModel> _logger;

    public PageImageModel(AppDbContext db, DocumentStorageService storage, CurrentUser currentUser, ILogger<PageImageModel> logger)
    {
        _db = db;
        _storage = storage;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync(Guid token, int number, int? w, CancellationToken ct)
    {
        var document = await _db.Documents
            .AsNoTracking()
            .Include(d => d.StorageLocation).ThenInclude(s => s!.Connection)
            .AccessibleTo(_currentUser)
            .FirstOrDefaultAsync(d => d.Token == token && d.UpdateState != UpdateState.Deleted, ct);
        if (document is null || number < 1)
        {
            return NotFound();
        }

        var width = Math.Clamp(w ?? DefaultWidth, MinWidth, MaxWidth);
        var dataDir = Environment.GetEnvironmentVariable("MATPAPER_DATA") ?? "/data";
        var cacheDir = Path.Combine(dataDir, "pagecache");
        var cached = Path.Combine(cacheDir, $"{document.Token:N}-{document.FileSize}-{number}-{width}.webp");
        if (global::System.IO.File.Exists(cached))
        {
            return PhysicalFile(cached, "image/webp");
        }

        try
        {
            using var local = await _storage.GetLocalCopyAsync(document, ct);
            var bytes = await global::System.IO.File.ReadAllBytesAsync(local.FilePath, ct);
            if (number > Conversion.GetPageCount(bytes))
            {
                return NotFound();
            }

            using var bitmap = Conversion.ToImage(bytes, page: number - 1, password: null,
                options: new RenderOptions(Width: width, WithAspectRatio: true));
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 85);

            Directory.CreateDirectory(cacheDir);
            var temp = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var fs = global::System.IO.File.Create(temp))
            {
                encoded.SaveTo(fs);
            }

            global::System.IO.File.Move(temp, cached, overwrite: true);
            return PhysicalFile(cached, "image/webp");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not render page {Page} of document {DocumentId}.", number, document.Id);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }
}
