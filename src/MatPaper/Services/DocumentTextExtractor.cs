using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MatPaper.Services;

/// <summary>
/// Extracts text (and page count) from PDFs and images. PDFs use PdfPig for the text
/// layer and fall back to rasterise + Tesseract OCR when the PDF is scanned (no text).
/// Images always go through OCR. Never throws: returns best-effort partial results.
/// </summary>
public sealed class DocumentTextExtractor
{
    // Below this many characters of embedded text a PDF is treated as scanned.
    private const int ScannedTextThreshold = 16;

    // DPI used when rasterising a PDF page for OCR.
    private const int OcrRenderDpi = 200;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp"
    };

    private readonly TesseractOcrRunner _ocr;
    private readonly ILogger<DocumentTextExtractor> _logger;

    public DocumentTextExtractor(TesseractOcrRunner ocr, ILogger<DocumentTextExtractor> logger)
    {
        _ocr = ocr;
        _logger = logger;
    }

    public async Task<TextExtractionResult> ExtractAsync(string absolutePath, string extension, CancellationToken ct)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (ext.Length > 0 && !ext.StartsWith('.'))
        {
            ext = "." + ext;
        }

        try
        {
            if (ext == ".pdf")
            {
                return await ExtractPdfAsync(absolutePath, ct);
            }

            if (ImageExtensions.Contains(ext))
            {
                var text = await _ocr.RunAsync(absolutePath, ct);
                return new TextExtractionResult
                {
                    Text = text ?? string.Empty,
                    PageCount = 1,
                    UsedOcr = true
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Text extraction failed for {Path}", absolutePath);
        }

        return new TextExtractionResult { Text = string.Empty, PageCount = 0, UsedOcr = false };
    }

    private async Task<TextExtractionResult> ExtractPdfAsync(string absolutePath, CancellationToken ct)
    {
        int pageCount = 0;
        var builder = new System.Text.StringBuilder();

        try
        {
            using var pdf = PdfDocument.Open(absolutePath);
            pageCount = pdf.NumberOfPages;

            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var pageText = ContentOrderTextExtractor.GetText(page);
                    if (string.IsNullOrWhiteSpace(pageText))
                    {
                        pageText = page.Text;
                    }

                    if (!string.IsNullOrEmpty(pageText))
                    {
                        builder.AppendLine(pageText);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read text from page in {Path}", absolutePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open PDF {Path}", absolutePath);
        }

        var embedded = builder.ToString();
        if (embedded.Trim().Length >= ScannedTextThreshold)
        {
            return new TextExtractionResult
            {
                Text = embedded,
                PageCount = Math.Max(pageCount, 1),
                UsedOcr = false
            };
        }

        // Scanned PDF (little or no embedded text) -> OCR fallback per page.
        var ocrText = await OcrPdfAsync(absolutePath, pageCount, ct);
        return new TextExtractionResult
        {
            Text = ocrText,
            PageCount = Math.Max(pageCount, ocrText.Length > 0 ? 1 : 0),
            UsedOcr = true
        };
    }

    private async Task<string> OcrPdfAsync(string absolutePath, int pageCount, CancellationToken ct)
    {
        var builder = new System.Text.StringBuilder();
        var pages = pageCount > 0 ? pageCount : 1;

        byte[] pdfBytes;
        try
        {
            pdfBytes = await File.ReadAllBytesAsync(absolutePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read PDF bytes for OCR: {Path}", absolutePath);
            return string.Empty;
        }

        for (int i = 0; i < pages; i++)
        {
            ct.ThrowIfCancellationRequested();

            var tempPng = Path.Combine(Path.GetTempPath(), $"matpaper-page-{Guid.NewGuid():N}.png");
            try
            {
                using (var bitmap = Conversion.ToImage(
                    pdfBytes,
                    page: i,
                    password: null,
                    options: new RenderOptions(Dpi: OcrRenderDpi)))
                {
                    if (bitmap is null)
                    {
                        continue;
                    }

                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    await using var fs = File.Create(tempPng);
                    data.SaveTo(fs);
                }

                var pageText = await _ocr.RunAsync(tempPng, ct);
                if (!string.IsNullOrEmpty(pageText))
                {
                    builder.AppendLine(pageText);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OCR fallback failed for page {Page} of {Path}", i, absolutePath);
            }
            finally
            {
                TryDelete(tempPng);
            }
        }

        return builder.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
