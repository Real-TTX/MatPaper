using PDFtoImage;
using SkiaSharp;

namespace MatPaper.Services;

/// <summary>
/// Generates WebP thumbnails (max 400px wide, aspect preserved) for PDFs and images.
/// Thumbnails are written to <c>{dataDir}/thumbnails/{token}.webp</c>. The value stored
/// on the Document is just the file name ("{token}.webp"). Never throws.
/// </summary>
public sealed class ThumbnailService
{
    private const int MaxWidth = 400;
    private const int WebpQuality = 80;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp"
    };

    private readonly ILogger<ThumbnailService> _logger;

    public ThumbnailService(ILogger<ThumbnailService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> GenerateAsync(Guid token, string absolutePath, string extension, CancellationToken ct)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (ext.Length > 0 && !ext.StartsWith('.'))
        {
            ext = "." + ext;
        }

        try
        {
            using var source = LoadSource(absolutePath, ext);
            if (source is null)
            {
                return null;
            }

            using var resized = Resize(source);
            if (resized is null)
            {
                return null;
            }

            var dataDir = Environment.GetEnvironmentVariable("MATPAPER_DATA") ?? "/data";
            var thumbDir = Path.Combine(dataDir, "thumbnails");
            Directory.CreateDirectory(thumbDir);

            var fileName = $"{token}.webp";
            var targetPath = Path.Combine(thumbDir, fileName);

            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
            if (encoded is null)
            {
                return null;
            }

            await using var fs = File.Create(targetPath);
            encoded.SaveTo(fs);

            return fileName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail generation failed for {Path}", absolutePath);
            return null;
        }
    }

    private SKBitmap? LoadSource(string absolutePath, string ext)
    {
        if (ext == ".pdf")
        {
            // Render the first page. Request a target width so the raster is not huge.
            var pdfBytes = File.ReadAllBytes(absolutePath);
            return Conversion.ToImage(
                pdfBytes,
                page: 0,
                password: null,
                options: new RenderOptions(Width: MaxWidth, WithAspectRatio: true));
        }

        if (ImageExtensions.Contains(ext))
        {
            return SKBitmap.Decode(absolutePath);
        }

        return null;
    }

    private static SKBitmap? Resize(SKBitmap source)
    {
        if (source.Width <= 0 || source.Height <= 0)
        {
            return null;
        }

        if (source.Width <= MaxWidth)
        {
            // Already small enough; return a copy so the caller can dispose independently.
            return source.Copy();
        }

        var ratio = (double)MaxWidth / source.Width;
        var targetHeight = Math.Max(1, (int)Math.Round(source.Height * ratio));

        var info = new SKImageInfo(MaxWidth, targetHeight);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        return source.Resize(info, sampling);
    }
}
