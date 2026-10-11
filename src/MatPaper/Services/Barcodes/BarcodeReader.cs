using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using ZXing;
using ZXing.Common;

namespace MatPaper.Services.Barcodes;

/// <param name="Page">1-based number of the page in the file.</param>
/// <param name="Codes">What the codes on the page say.</param>
public sealed record PageCodes(int Page, IReadOnlyList<string> Codes);

/// <summary>One document in a scanned file: its pages (1-based, in order) and the archive number on its first sheet.</summary>
public sealed record ScanPart(IReadOnlyList<int> Pages, long? ArchiveNumber);

/// <summary>The codes found in a file, page by page.</summary>
public sealed class BarcodeScan
{
    private readonly BarcodeOptions _options;

    public BarcodeScan(int pageCount, IReadOnlyList<PageCodes> pages, BarcodeOptions options)
    {
        PageCount = pageCount;
        Pages = pages;
        _options = options;
    }

    /// <summary>How many pages the file has (not only the ones that were searched).</summary>
    public int PageCount { get; }

    public IReadOnlyList<PageCodes> Pages { get; }

    /// <summary>True when the file has a separator sheet and separators are to be cut at.</summary>
    public bool HasSeparators => _options.SplitAtSeparators && Pages.Any(p => p.Codes.Any(_options.IsSeparator));

    public bool IsSeparator(int page)
        => Pages.Any(p => p.Page == page && p.Codes.Any(_options.IsSeparator));

    /// <summary>The first number found on the given pages (1-based), in the order of the pages.</summary>
    public long? AsnOn(IEnumerable<int> pages)
    {
        foreach (var page in pages)
        {
            var found = Pages.FirstOrDefault(p => p.Page == page);
            if (found is null)
            {
                continue;
            }

            foreach (var code in found.Codes)
            {
                if (_options.ParseAsn(code) is long number)
                {
                    return number;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The documents in the file: the stretches between two separator sheets (the sheets themselves belong to no
    /// document) or - without separators - the whole file as one. Each has the number found on its first sheet(s).
    /// An empty list means the file has nothing but separator sheets.
    /// </summary>
    public IReadOnlyList<ScanPart> Parts()
    {
        var parts = new List<ScanPart>();
        var run = new List<int>();

        void Flush()
        {
            if (run.Count > 0)
            {
                parts.Add(new ScanPart(run.ToArray(), AsnOn(run.Take(Math.Max(1, _options.AsnPages)))));
                run.Clear();
            }
        }

        for (var page = 1; page <= PageCount; page++)
        {
            if (_options.SplitAtSeparators && IsSeparator(page))
            {
                Flush();
            }
            else
            {
                run.Add(page);
            }
        }

        Flush();
        return parts;
    }
}

/// <summary>
/// Reads the barcodes and QR codes on the pages of a PDF or an image (ZXing): a separator sheet, an archive number
/// on a sticker. A page is drawn to a picture first; a big code (a separator sheet) is found at a low resolution, a
/// small sticker needs more - and a tiny one (9 mm) needs 300 dpi, below that it is found by luck.
/// Nothing here throws except the cancellation: a file that cannot be read has no codes.
/// </summary>
public static class BarcodeReader
{
    private const int SeparatorDpi = 120;
    private const int AsnDpi = 300;
    private const int AsnHighDpi = 450;

    /// <summary>A page larger than this (in pixels) is drawn at a lower resolution: a poster must not take the memory of the server.</summary>
    private const double MaxPixels = 36_000_000;

    private static readonly BarcodeFormat[] Formats =
    {
        BarcodeFormat.QR_CODE, BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.DATA_MATRIX
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp"
    };

    /// <summary>True for the file types that have pages or pixels to look at.</summary>
    public static bool Supports(string? extension)
    {
        var ext = (extension ?? string.Empty).Trim();
        return string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase) || ImageExtensions.Contains(ext);
    }

    /// <summary>The codes in the file, or null when nothing was searched (switched off, a type without pages, unreadable).</summary>
    public static BarcodeScan? Scan(byte[] content, string extension, BarcodeOptions options, CancellationToken ct = default)
    {
        if (!options.Enabled || content.Length == 0)
        {
            return null;
        }

        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        try
        {
            if (ext == ".pdf")
            {
                return ScanPdf(content, options, ct);
            }

            if (ImageExtensions.Contains(ext))
            {
                return ScanImage(content, options);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // a damaged or protected file: no codes, and the file is taken in as it is
        }

        return null;
    }

    /// <summary>
    /// The codes in one picture - a frame of a camera, a photo of a label: every format that is read here, any
    /// rotation. Nothing found (or a picture that cannot be read) is an empty list; this never throws.
    /// </summary>
    public static IReadOnlyList<string> DecodeImage(byte[] image)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(image);
            return bitmap is null ? Array.Empty<string>() : DecodeBitmap(bitmap, tryHarder: true).Distinct().ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static BarcodeScan ScanPdf(byte[] pdf, BarcodeOptions options, CancellationToken ct)
    {
        int pageCount;
        using (var document = PdfDocument.Open(pdf))
        {
            pageCount = document.NumberOfPages;
        }

        var searched = Math.Min(pageCount, Math.Max(1, options.MaxPages));
        var found = new List<string>[searched];
        for (var i = 0; i < searched; i++)
        {
            found[i] = new List<string>();
        }

        bool IsSeparatorPage(int i) => options.SplitAtSeparators && found[i].Any(options.IsSeparator);

        // 1. every page at a low resolution: separator sheets (and big stickers) are found at once
        if (options.SplitAtSeparators)
        {
            for (var i = 0; i < searched; i++)
            {
                ct.ThrowIfCancellationRequested();
                found[i].AddRange(Decode(pdf, i, SeparatorDpi, tryHarder: false));
            }
        }

        // 2. the first sheet(s) of every document - the first of the file, the ones after a separator - at a
        //    resolution for small stickers, unless the number has been read already
        var starts = new List<int> { 0 };
        for (var i = 0; i < searched - 1; i++)
        {
            if (IsSeparatorPage(i))
            {
                starts.Add(i + 1);
            }
        }

        var numberPages = Math.Max(1, options.AsnPages);
        foreach (var start in starts.Distinct())
        {
            if (IsSeparatorPage(start))
            {
                continue;
            }

            var range = new List<int>();
            for (var i = start; i < Math.Min(searched, start + numberPages) && !IsSeparatorPage(i); i++)
            {
                range.Add(i);
            }

            if (range.Any(i => HasNumber(found[i], options)))
            {
                continue;
            }

            foreach (var i in range)
            {
                ct.ThrowIfCancellationRequested();
                found[i].AddRange(Decode(pdf, i, AsnDpi, tryHarder: true));
                if (HasNumber(found[i], options))
                {
                    break;
                }

                if (options.HighResolution)
                {
                    ct.ThrowIfCancellationRequested();
                    found[i].AddRange(Decode(pdf, i, AsnHighDpi, tryHarder: true));
                    if (HasNumber(found[i], options))
                    {
                        break;
                    }
                }
            }
        }

        var pages = found.Select((codes, i) => new PageCodes(i + 1, codes.Distinct().ToList())).Where(p => p.Codes.Count > 0).ToList();
        return new BarcodeScan(pageCount, pages, options);
    }

    private static BarcodeScan ScanImage(byte[] image, BarcodeOptions options)
    {
        using var bitmap = SKBitmap.Decode(image);
        var codes = bitmap is null ? new List<string>() : DecodeBitmap(bitmap, tryHarder: true);
        var pages = codes.Count == 0 ? new List<PageCodes>() : new List<PageCodes> { new(1, codes.Distinct().ToList()) };
        return new BarcodeScan(1, pages, options);
    }

    private static bool HasNumber(IEnumerable<string> codes, BarcodeOptions options) => codes.Any(c => options.ParseAsn(c) is not null);

    private static List<string> Decode(byte[] pdf, int pageIndex, int dpi, bool tryHarder)
    {
        try
        {
            var size = Conversion.GetPageSize(pdf, pageIndex, null);
            var scale = dpi / 72.0;
            var pixels = size.Width * scale * size.Height * scale;
            if (pixels > MaxPixels)
            {
                dpi = (int)(dpi * Math.Sqrt(MaxPixels / pixels));
            }

            using var bitmap = Conversion.ToImage(pdf, page: pageIndex, password: null, options: new RenderOptions(Dpi: Math.Max(dpi, 50)));
            return DecodeBitmap(bitmap, tryHarder);
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    private static List<string> DecodeBitmap(SKBitmap bitmap, bool tryHarder)
    {
        var converted = bitmap.ColorType is SKColorType.Bgra8888 or SKColorType.Rgba8888 ? null : bitmap.Copy(SKColorType.Rgba8888);
        try
        {
            var source = converted ?? bitmap;
            var format = source.ColorType == SKColorType.Bgra8888 ? RGBLuminanceSource.BitmapFormat.BGRA32 : RGBLuminanceSource.BitmapFormat.RGBA32;
            var luminance = new RGBLuminanceSource(source.Bytes, source.Width, source.Height, format);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = tryHarder,
                Options = new DecodingOptions { TryHarder = tryHarder, PossibleFormats = Formats }
            };
            var results = reader.DecodeMultiple(luminance);
            return results is null
                ? new List<string>()
                : results.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
