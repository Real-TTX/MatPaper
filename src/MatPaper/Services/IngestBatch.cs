namespace MatPaper.Services;

/// <summary>What became of a file once it has been read: see <see cref="IngestBatch.Outcome"/>.</summary>
public enum IngestOutcome
{
    /// <summary>Everything in it was dealt with (imported, or known already): a source that is done with can go.</summary>
    Done,

    /// <summary>An archive without a single document in it: nothing to import, but nothing went wrong either.</summary>
    NothingToImport,

    /// <summary>Something could not be stored or read: the source stays, so the next run tries again.</summary>
    Incomplete
}

/// <param name="Label">The file name, or where an archive held it (<c>mails.zip/2025/invoice.pdf</c>).</param>
public sealed record IngestItem(string Label, IngestResult Result);

/// <summary>
/// What one file (or one archive full of them) became. A plain file is a batch of one; an archive (.zip) is opened and
/// every document in it is ingested on its own - and found to be new, a duplicate, blocked - like a file of its own.
/// </summary>
public sealed class IngestBatch
{
    private readonly List<IngestItem> _items = new();
    private readonly List<(string Label, string Reason)> _failed = new();
    private readonly List<(string Label, string Reason)> _skipped = new();

    public IngestBatch(bool fromArchive) => FromArchive = fromArchive;

    public bool FromArchive { get; }

    /// <summary>The documents that were offered to the ingest, whatever came of them.</summary>
    public IReadOnlyList<IngestItem> Items => _items;

    /// <summary>Entries that could not be read or stored (an error is never thrown for a single entry of an archive).</summary>
    public IReadOnlyList<(string Label, string Reason)> Failed => _failed;

    /// <summary>Entries of an archive that are no documents.</summary>
    public IReadOnlyList<(string Label, string Reason)> Skipped => _skipped;

    public int Created => Count(IngestStatus.Created);
    public int Duplicates => Count(IngestStatus.Duplicate);
    public int Blocked => Count(IngestStatus.Blocked);
    public int NoStorage => Count(IngestStatus.NoStorage);

    /// <summary>The id of the first document that was created (or, if none was, the first known one).</summary>
    public long? FirstDocumentId
        => _items.FirstOrDefault(i => i.Result.Status == IngestStatus.Created)?.Result.DocumentId
           ?? _items.FirstOrDefault(i => i.Result.Status == IngestStatus.Duplicate)?.Result.DocumentId;

    public IngestOutcome Outcome
        => NoStorage > 0 || _failed.Count > 0
            ? IngestOutcome.Incomplete
            : _items.Count == 0 ? IngestOutcome.NothingToImport : IngestOutcome.Done;

    internal void Add(string label, IngestResult result) => _items.Add(new IngestItem(label, result));

    internal void Fail(string label, string reason) => _failed.Add((label, reason));

    internal void Skip(string label, string reason) => _skipped.Add((label, reason));

    private int Count(IngestStatus status) => _items.Count(i => i.Result.Status == status);
}
