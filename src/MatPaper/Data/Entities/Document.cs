using NpgsqlTypes;

namespace MatPaper.Data;

public class Document : BaseEntity
{
    public Guid Token { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime? DocumentDate { get; set; }
    public long? DocumentTypeId { get; set; }
    public long? CorrespondentId { get; set; }
    public long? ProjectId { get; set; }
    public long? StorageLocationId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? ContentHash { get; set; }

    /// <summary>The user who owns this document. Null = ownerless (visible to admins only).</summary>
    public long? OwnerId { get; set; }

    /// <summary>When true, the document lives in the shared/common area visible to every user.</summary>
    public bool IsCommon { get; set; }

    /// <summary>Whether the auto-suggested metadata still needs confirmation in the inbox.</summary>
    public ReviewState ReviewState { get; set; } = ReviewState.Pending;

    /// <summary>Invoice number extracted from a structured e-invoice (XRechnung/ZUGFeRD), if any.</summary>
    public string? InvoiceNumber { get; set; }

    public string? OcrText { get; set; }
    public string? ThumbnailPath { get; set; }
    public int PageCount { get; set; }
    public OcrState OcrState { get; set; }
    public UpdateState UpdateState { get; set; }
    public NpgsqlTsVector? SearchVector { get; set; }
    public DocumentType? DocumentType { get; set; }
    public Correspondent? Correspondent { get; set; }
    public Project? Project { get; set; }
    public StorageLocation? StorageLocation { get; set; }
    public User? Owner { get; set; }
    public ICollection<DocumentTag> DocumentTags { get; set; } = new List<DocumentTag>();
    public ICollection<DocumentShare> Shares { get; set; } = new List<DocumentShare>();
}
