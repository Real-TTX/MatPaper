namespace MatPaper.Data;

/// <summary>
/// A document the user deleted for good and does not want back: an import or storage search that finds the same
/// content again skips it. The name is kept so the list can be searched and an entry released by mistake.
/// </summary>
public class BlockedDocument : BaseEntity
{
    /// <summary>Hash of the file content (same hash the duplicate check uses).</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>What the document was called (its title), for the list.</summary>
    public string Name { get; set; } = string.Empty;

    public string? OriginalFileName { get; set; }

    /// <summary>Whose documents it applies to: the user who blocked it.</summary>
    public long? OwnerId { get; set; }
}
