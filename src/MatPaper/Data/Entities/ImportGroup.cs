namespace MatPaper.Data;

/// <summary>
/// A group of import rules that share a target and a schedule. The group runs its rules one after the
/// other, highest priority first (<see cref="ImportTask.Priority"/>), so a specific rule ("invoices from
/// this sender") gets its mail before a catch-all rule ("everything that says Rechnung") sees it.
/// What a rule leaves empty (storage location, owner, project) it takes from the group; switches and
/// tags of the group apply on top of the rule's own.
/// </summary>
public class ImportGroup : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    /// <summary>When the group runs; empty = only on demand.</summary>
    public string? CronExpression { get; set; }

    // ---- target defaults for every rule of the group

    public long? StorageLocationId { get; set; }

    /// <summary>Whose review inbox the documents land in. Null = whoever created the rule.</summary>
    public long? OwnerUserId { get; set; }

    /// <summary>Put imported documents into the common area.</summary>
    public bool IsCommon { get; set; }

    /// <summary>File straight into the archive instead of the review inbox.</summary>
    public bool SkipInbox { get; set; }

    public long? ProjectId { get; set; }

    /// <summary>Comma-separated tag ids added to every document of the group.</summary>
    public string? TagIds { get; set; }

    public UpdateState UpdateState { get; set; }
}
