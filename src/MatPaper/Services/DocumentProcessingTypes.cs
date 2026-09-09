namespace MatPaper.Services;

/// <summary>
/// Result of extracting text from a document file. Shared between the text
/// extractor and the background processing service.
/// </summary>
public sealed class TextExtractionResult
{
    public string Text { get; set; } = string.Empty;

    public int PageCount { get; set; }

    public bool UsedOcr { get; set; }
}
