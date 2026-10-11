using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static MatPaper.Services.Barcodes.BarcodeWriter;

namespace MatPaper.Services.Barcodes;

/// <summary>A sheet of sticky labels: how many, how big, how far apart. Lengths in millimetres.</summary>
/// <param name="Id">Short name (also used in the address of the page).</param>
/// <param name="Name">What people call the sheet.</param>
/// <param name="GapXMm">Space between two labels in a row.</param>
/// <param name="GapYMm">Space between two rows.</param>
/// <param name="PageWidthMm">Null = A4. A label printer feeds a label per page: the page is as big as the label then.</param>
public sealed record LabelFormat(
    string Id, string Name, int Columns, int Rows, double LabelWidthMm, double LabelHeightMm, double GapXMm, double GapYMm,
    double? PageWidthMm = null, double? PageHeightMm = null)
{
    public int PerPage => Columns * Rows;
    public double PageWidth => PageWidthMm ?? 210;
    public double PageHeight => PageHeightMm ?? 297;
}

/// <param name="Start">The first number.</param>
/// <param name="Count">How many labels.</param>
/// <param name="SkipLabels">Labels left blank at the start of the first sheet (one that is partly used).</param>
/// <param name="Border">Draws a frame around every label: a test print on plain paper shows if it fits.</param>
public sealed record LabelRequest(
    LabelFormat Format, long Start, int Count, BarcodeKind Kind = BarcodeKind.Qr, bool ShowText = true, bool Border = false,
    int SkipLabels = 0, double OffsetXMm = 0, double OffsetYMm = 0);

/// <summary>Texts of a separator sheet; written without umlauts (the standard fonts of a PDF cannot do them).</summary>
public sealed record SeparatorTexts(string Title, string Line1, string Line2);

/// <summary>
/// Sheets of labels with the archive numbers as codes, and separator sheets - as PDFs ready to print. The labels
/// say the number as a QR code (or bar code) and as text; the codes are the text <c>ASN00123</c> that the reader
/// (<see cref="BarcodeReader"/>) and Paperless both know.
/// </summary>
public static class LabelSheets
{
    public const int MaxLabels = 5000;

    /// <summary>The sheets people buy for this: the grid is centred on an A4 page with the gaps below.</summary>
    public static readonly IReadOnlyList<LabelFormat> Formats = new[]
    {
        new LabelFormat("avery-l4731", "Avery Zweckform L4731REV (25,4 x 10 mm, 189)", 7, 27, 25.4, 10, 2.54, 0),
        new LabelFormat("avery-l7651", "Avery L7651 / Zweckform 3657 (38,1 x 21,2 mm, 65)", 5, 13, 38.1, 21.2, 2.54, 0),
        new LabelFormat("avery-l7160", "Avery L7160 (63,5 x 38,1 mm, 21)", 3, 7, 63.5, 38.1, 2.54, 0),
        new LabelFormat("avery-l7163", "Avery L7163 (99,1 x 38,1 mm, 14)", 2, 7, 99.1, 38.1, 2.54, 0)
    };

    public static LabelFormat? Find(string? id) => Formats.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The sheet(s) with the labels as a PDF.</summary>
    public static byte[] Render(LabelRequest request, BarcodeOptions options)
    {
        var format = request.Format;
        var count = Math.Clamp(request.Count, 1, MaxLabels);
        var skip = Math.Clamp(request.SkipLabels, 0, Math.Max(0, format.PerPage - 1));

        var builder = new PdfDocumentBuilder();
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        // the grid sits in the middle of the page, as on the sheets
        var gridWidth = format.Columns * format.LabelWidthMm + (format.Columns - 1) * format.GapXMm;
        var gridHeight = format.Rows * format.LabelHeightMm + (format.Rows - 1) * format.GapYMm;
        var left = (format.PageWidth - gridWidth) / 2 + request.OffsetXMm;
        var top = (format.PageHeight - gridHeight) / 2 + request.OffsetYMm;

        PdfPageBuilder? page = null;
        for (var i = 0; i < count; i++)
        {
            var slot = (i + skip) % format.PerPage;
            if (page is null || slot == 0)
            {
                page = builder.AddPage(format.PageWidth * Mm, format.PageHeight * Mm);
            }

            var column = slot % format.Columns;
            var row = slot / format.Columns;
            var x = (left + column * (format.LabelWidthMm + format.GapXMm)) * Mm;
            var y = (format.PageHeight - top - row * (format.LabelHeightMm + format.GapYMm) - format.LabelHeightMm) * Mm;
            DrawLabel(page, bold, x, y, format.LabelWidthMm * Mm, format.LabelHeightMm * Mm, request, options, request.Start + i);
        }

        return builder.Build();
    }

    private static void DrawLabel(
        PdfPageBuilder page, PdfDocumentBuilder.AddedFont font, double x, double y, double w, double h,
        LabelRequest request, BarcodeOptions options, long number)
    {
        if (request.Border)
        {
            page.SetStrokeColor(160, 160, 160);
            page.DrawRectangle(new PdfPoint(x, y), w, h, 0.25, false);
        }

        var pad = Math.Min(0.9 * Mm, Math.Min(w, h) * 0.08);
        var code = options.AsnText(number);
        var text = options.AsnReadable(number);
        page.SetTextAndFillColor(0, 0, 0);

        var wide = w >= h * 1.5;
        if (request.Kind == BarcodeKind.Qr)
        {
            var matrix = Qr(code, 1);
            if (wide)
            {
                // the code on the left, the number beside it
                var size = Math.Min(h - 2 * pad, w * 0.5);
                DrawQr(page, matrix, x + pad, y + (h - size) / 2, size);
                if (request.ShowText)
                {
                    var room = w - size - 3 * pad;
                    var fontSize = Fit(text, room, h * 0.42, bold: true);
                    page.AddText(text, fontSize, new PdfPoint(x + 2 * pad + size, y + h / 2 - fontSize * 0.35), font);
                }
            }
            else
            {
                // the code on top, the number below it
                var room = h - 2 * pad;
                var size = Math.Min(w - 2 * pad, request.ShowText ? room * 0.72 : room);
                DrawQr(page, matrix, x + (w - size) / 2, y + h - pad - size, size);
                if (request.ShowText)
                {
                    var fontSize = Fit(text, w - 2 * pad, room * 0.2, bold: true);
                    page.AddText(text, fontSize, new PdfPoint(x + (w - TextWidth(text, fontSize, true)) / 2, y + pad + (room - size) / 2 - fontSize * 0.2), font);
                }
            }

            return;
        }

        // a bar code: wide and low, the number under it
        var bars = Bars(code);
        var barsHeight = (h - 2 * pad) * (request.ShowText ? 0.62 : 1);
        DrawBars(page, bars, x + pad, y + h - pad - barsHeight, w - 2 * pad, barsHeight);
        if (request.ShowText)
        {
            var fontSize = Fit(text, w - 2 * pad, (h - 2 * pad) * 0.3, bold: true);
            page.AddText(text, fontSize, new PdfPoint(x + (w - TextWidth(text, fontSize, true)) / 2, y + pad), font);
        }
    }

    /// <summary>The largest font size (up to <paramref name="max"/>) at which the text fits the width.</summary>
    private static double Fit(string text, double width, double max, bool bold)
    {
        var size = max;
        while (size > 3 && TextWidth(text, size, bold) > width)
        {
            size -= 0.25;
        }

        return Math.Max(size, 3);
    }

    /// <summary>Separator sheets (A4): a big code and what it is for. Put one between two documents of a stack that is scanned in one go.</summary>
    public static byte[] RenderSeparators(int count, BarcodeKind kind, BarcodeOptions options, SeparatorTexts texts)
    {
        var builder = new PdfDocumentBuilder();
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var pageWidth = 210 * Mm;
        var pageHeight = 297 * Mm;

        var title = Ascii(texts.Title);
        var line1 = Ascii(texts.Line1);
        var line2 = Ascii(texts.Line2);
        var code = options.SeparatorText.Trim();

        for (var i = 0; i < Math.Clamp(count, 1, 500); i++)
        {
            var page = builder.AddPage(pageWidth, pageHeight);
            page.SetTextAndFillColor(0, 0, 0);

            var titleSize = Fit(title, pageWidth - 40 * Mm, 34, bold: true);
            page.AddText(title, titleSize, new PdfPoint((pageWidth - TextWidth(title, titleSize, true)) / 2, pageHeight - 45 * Mm), bold);

            if (kind == BarcodeKind.Qr)
            {
                var size = 90 * Mm;
                DrawQr(page, Qr(code, 2), (pageWidth - size) / 2, (pageHeight - size) / 2 - 5 * Mm, size);
            }
            else
            {
                var width = 150 * Mm;
                DrawBars(page, Bars(code), (pageWidth - width) / 2, pageHeight / 2 - 15 * Mm, width, 40 * Mm);
            }

            var codeSize = Fit(code, pageWidth - 40 * Mm, 20, bold: true);
            page.AddText(code, codeSize, new PdfPoint((pageWidth - TextWidth(code, codeSize, true)) / 2, pageHeight / 2 - (kind == BarcodeKind.Qr ? 62 : 32) * Mm), bold);

            var lineSize = Fit(line1, pageWidth - 40 * Mm, 12, bold: false);
            page.AddText(line1, lineSize, new PdfPoint((pageWidth - TextWidth(line1, lineSize, false)) / 2, 70 * Mm), regular);
            var lineSize2 = Fit(line2, pageWidth - 40 * Mm, 10, bold: false);
            page.AddText(line2, lineSize2, new PdfPoint((pageWidth - TextWidth(line2, lineSize2, false)) / 2, 62 * Mm), regular);
        }

        return builder.Build();
    }
}
