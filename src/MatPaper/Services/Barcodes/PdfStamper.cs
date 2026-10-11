using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static MatPaper.Services.Barcodes.BarcodeWriter;

namespace MatPaper.Services.Barcodes;

public enum StampCorner
{
    TopRight,
    TopLeft,
    BottomRight,
    BottomLeft
}

/// <summary>
/// Works on the pages of an existing PDF: puts the archive number on a copy as a code and as text, and cuts pages
/// out of a PDF (the parts of a scanned stack). The original is never changed; what comes back is a new file.
/// A copy keeps the text of the pages, a page that is rotated stays rotated; it loses what a page cannot carry along
/// (outlines, attachments, form fields).
/// </summary>
public static class PdfStamper
{
    /// <summary>A copy of the PDF with the number in a corner of the first page.</summary>
    public static byte[] Stamp(byte[] pdf, BarcodeOptions options, long number, StampCorner corner, BarcodeKind kind = BarcodeKind.Qr)
    {
        using var document = PdfDocument.Open(pdf);
        var builder = new PdfDocumentBuilder();
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

        for (var number1 = 1; number1 <= document.NumberOfPages; number1++)
        {
            var page = builder.AddPage(document, number1);
            if (number1 == 1)
            {
                var source = document.GetPage(1);
                DrawStamp(page, bold, source, options, number, corner, kind);
            }
        }

        return builder.Build();
    }

    /// <summary>The given pages (1-based, in this order) as a PDF of their own.</summary>
    public static byte[] Extract(byte[] pdf, IReadOnlyList<int> pages)
        => PdfMerger.Merge(new[] { pdf }, new IReadOnlyList<int>[] { pages });

    private static void DrawStamp(
        PdfPageBuilder page, PdfDocumentBuilder.AddedFont font, UglyToad.PdfPig.Content.Page source,
        BarcodeOptions options, long number, StampCorner corner, BarcodeKind kind)
    {
        // the page is drawn on in its own, unrotated coordinates; a rotated page shows another corner at the top
        var bounds = source.MediaBox.Bounds;
        var width = (double)bounds.Width;
        var height = (double)bounds.Height;
        corner = Unrotate(corner, source.Rotation.Value);

        var boxWidth = (kind == BarcodeKind.Qr ? 36 : 52) * Mm;
        var boxHeight = (kind == BarcodeKind.Qr ? 19 : 17) * Mm;
        var margin = 8 * Mm;
        var x = corner is StampCorner.TopRight or StampCorner.BottomRight ? (double)bounds.Left + width - margin - boxWidth : (double)bounds.Left + margin;
        var y = corner is StampCorner.TopRight or StampCorner.TopLeft ? (double)bounds.Bottom + height - margin - boxHeight : (double)bounds.Bottom + margin;

        // a white box with a thin frame: the code must be readable whatever is on the page behind it
        page.SetTextAndFillColor(255, 255, 255);
        page.DrawRectangle(new PdfPoint(x, y), boxWidth, boxHeight, 0, true);
        page.SetStrokeColor(120, 120, 120);
        page.DrawRectangle(new PdfPoint(x, y), boxWidth, boxHeight, 0.4, false);

        var code = options.AsnText(number);
        var text = options.AsnReadable(number);
        page.SetTextAndFillColor(0, 0, 0);
        if (kind == BarcodeKind.Qr)
        {
            var size = 15 * Mm;
            DrawQr(page, Qr(code, 1), x + 2 * Mm, y + (boxHeight - size) / 2, size);
            var fontSize = 7.5;
            page.AddText(text, fontSize, new PdfPoint(x + 2 * Mm + size + 1.5 * Mm, y + boxHeight / 2 - fontSize * 0.35), font);
        }
        else
        {
            DrawBars(page, Bars(code), x + 3 * Mm, y + 6 * Mm, boxWidth - 6 * Mm, boxHeight - 8.5 * Mm);
            var fontSize = 7.5;
            page.AddText(text, fontSize, new PdfPoint(x + (boxWidth - TextWidth(text, fontSize, true)) / 2, y + 2 * Mm), font);
        }
    }

    /// <summary>The corner of the unrotated page that is shown at the given corner of the rotated one.</summary>
    private static StampCorner Unrotate(StampCorner shown, int rotation)
    {
        var turns = ((rotation % 360) + 360) % 360 / 90;
        var order = new[] { StampCorner.TopLeft, StampCorner.TopRight, StampCorner.BottomRight, StampCorner.BottomLeft };
        var index = Array.IndexOf(order, shown);
        // turning the page clockwise by 90 degrees moves the corner that was at the top left to the top right ...
        return order[((index - turns) % 4 + 4) % 4];
    }
}
