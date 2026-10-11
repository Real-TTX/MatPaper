using System.Text;
using UglyToad.PdfPig.Graphics.Operations.PathConstruction;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;
using UglyToad.PdfPig.Writer;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace MatPaper.Services.Barcodes;

public enum BarcodeKind
{
    /// <summary>A QR code: small and robust, the choice for stickers.</summary>
    Qr,

    /// <summary>A Code 128 bar code: long and thin, readable by every scanner.</summary>
    Code128
}

/// <summary>
/// Makes barcodes and draws them into a PDF page as plain rectangles (vector, so they stay sharp at any printer
/// resolution). Lengths here are points (1/72 inch) unless a name says millimetres; the origin of a PDF page is its
/// lower left corner.
/// </summary>
public static class BarcodeWriter
{
    public const double Mm = 72.0 / 25.4;

    public static BitMatrix Qr(string text, int quietModules = 1)
    {
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
            [EncodeHintType.MARGIN] = quietModules,
            [EncodeHintType.CHARACTER_SET] = "ISO-8859-1"
        };
        return new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, hints);
    }

    /// <summary>The bars of a Code 128 (one row; the quiet zones at both ends are part of it).</summary>
    public static BitMatrix Bars(string text) => new MultiFormatWriter().encode(text, BarcodeFormat.CODE_128, 0, 1);

    /// <summary>
    /// Draws a QR code into the square with the lower left corner (x, y) and the side <paramref name="size"/>. All dark
    /// modules are ONE path that is filled once: a renderer adds up the coverage of the whole shape, so no light
    /// hairline shows where two modules touch (separate rectangles do show them at some resolutions - a code that is
    /// read at 250 dpi and not at 300).
    /// </summary>
    public static void DrawQr(PdfPageBuilder page, BitMatrix matrix, double x, double y, double size)
    {
        var step = size / Math.Max(matrix.Width, matrix.Height);
        page.SetTextAndFillColor(0, 0, 0);
        var operations = page.CurrentStream.Operations;
        for (var row = 0; row < matrix.Height; row++)
        {
            // a run of dark modules in a row is one rectangle of the path
            var col = 0;
            while (col < matrix.Width)
            {
                if (!matrix[col, row])
                {
                    col++;
                    continue;
                }

                var start = col;
                while (col < matrix.Width && matrix[col, row])
                {
                    col++;
                }

                operations.Add(new AppendRectangle(x + start * step, y + size - (row + 1) * step, (col - start) * step, step));
            }
        }

        operations.Add(FillPathNonZeroWinding.Value);
    }

    /// <summary>Draws the bars of a Code 128 into the box with the lower left corner (x, y) (one path, see <see cref="DrawQr"/>).</summary>
    public static void DrawBars(PdfPageBuilder page, BitMatrix bars, double x, double y, double width, double height)
    {
        var step = width / bars.Width;
        page.SetTextAndFillColor(0, 0, 0);
        var operations = page.CurrentStream.Operations;
        var col = 0;
        while (col < bars.Width)
        {
            if (!bars[col, 0])
            {
                col++;
                continue;
            }

            var start = col;
            while (col < bars.Width && bars[col, 0])
            {
                col++;
            }

            operations.Add(new AppendRectangle(x + start * step, y, (col - start) * step, height));
        }

        operations.Add(FillPathNonZeroWinding.Value);
    }

    /// <summary>
    /// A text the standard fonts can write: the umlauts become ae, oe, ue, ss, typographic dashes and quotes
    /// become plain ones, anything else outside ASCII a question mark.
    /// </summary>
    public static string Ascii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case 'ä': sb.Append("ae"); break;
                case 'ö': sb.Append("oe"); break;
                case 'ü': sb.Append("ue"); break;
                case 'Ä': sb.Append("Ae"); break;
                case 'Ö': sb.Append("Oe"); break;
                case 'Ü': sb.Append("Ue"); break;
                case 'ß': sb.Append("ss"); break;
                case '–': case '—': sb.Append('-'); break;
                case '“': case '”': case '„': sb.Append('"'); break;
                case '‘': case '’': sb.Append('\''); break;
                case '…': sb.Append("..."); break;
                default: sb.Append(c is >= ' ' and <= '~' ? c : '?'); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// How wide a text is in Helvetica (bold or not) at the given size: close enough to fit it into a box.
    /// </summary>
    public static double TextWidth(string text, double fontSize, bool bold)
    {
        double width = 0;
        foreach (var c in text)
        {
            width += c switch
            {
                ' ' => 0.278,
                >= '0' and <= '9' => 0.556,
                'I' or 'i' or 'l' or 'j' or '.' or ',' or ':' or ';' or '\'' or '!' => bold ? 0.3 : 0.25,
                'M' or 'W' or 'm' or 'w' => bold ? 0.9 : 0.85,
                >= 'A' and <= 'Z' => bold ? 0.74 : 0.69,
                _ => bold ? 0.6 : 0.55
            };
        }

        return width * fontSize;
    }
}
