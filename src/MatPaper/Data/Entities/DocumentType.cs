namespace MatPaper.Data;

public class DocumentType : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional text/regex matched against a document's title + OCR text to auto-assign this type.</summary>
    public string? MatchPattern { get; set; }

    public UpdateState UpdateState { get; set; }
}
