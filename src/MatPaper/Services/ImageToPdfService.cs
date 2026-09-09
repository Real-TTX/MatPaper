using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MatPaper.Services;

/// <summary>
/// Builds a single PDF from a set of images (e.g. camera scans), placing one
/// image per page, fit to the page with a small margin. The QuestPDF community
/// license is assumed to be configured by the application startup.
/// </summary>
public sealed class ImageToPdfService
{
    /// <summary>
    /// Renders the supplied images into a PDF, one image per page.
    /// </summary>
    /// <param name="images">The raw image bytes, in the desired page order.</param>
    /// <returns>The generated PDF as a byte array.</returns>
    public byte[] Build(IReadOnlyList<byte[]> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        return Document.Create(container =>
        {
            foreach (var image in images)
            {
                if (image is null || image.Length == 0)
                {
                    continue;
                }

                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.Content()
                        .AlignCenter()
                        .AlignMiddle()
                        .Image(image)
                        .FitArea();
                });
            }
        }).GeneratePdf();
    }
}
