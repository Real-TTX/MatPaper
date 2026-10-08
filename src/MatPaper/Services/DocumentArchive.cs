using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace MatPaper.Services;

public enum ArchiveItemKind
{
    /// <summary>A document read from the archive, ready to be ingested.</summary>
    Document,

    /// <summary>An entry that is no document (another type of file) and was left out.</summary>
    Skipped,

    /// <summary>An entry (or the whole archive) that could not be read.</summary>
    Problem
}

/// <param name="Label">Where it sits: <c>mails.zip/2025/invoice.pdf</c> (for logs and messages).</param>
/// <param name="Name">The file name alone, which becomes the title and the extension of the document.</param>
/// <param name="Reason">What is wrong, for <see cref="ArchiveItemKind.Skipped"/> and <see cref="ArchiveItemKind.Problem"/>.</param>
public sealed record ArchiveItem(ArchiveItemKind Kind, string Label, string Name, byte[]? Content, DocumentSidecar? Sidecar, string? Reason);

/// <summary>
/// What is inside an archive (.zip) that was uploaded or imported: the documents in it, one after the other, ready to
/// be ingested like any other file. Nothing is written to disk - an entry is read into memory and handed on - so a path
/// inside the archive can do no harm; only its file name is used. Limits keep an archive that unpacks to far more than
/// it looks like (or holds archives within archives) from taking the server down.
/// </summary>
public static class DocumentArchive
{
    public const int MaxEntries = 2000;

    /// <summary>How many entries the directory of an archive may list at all (what is no document is only skipped, but it still has to be listed).</summary>
    private const int MaxDirectoryEntries = 20000;
    public const long MaxEntryBytes = 200L * 1024 * 1024;
    public const long MaxTotalBytes = 1024L * 1024 * 1024;

    /// <summary>An archive inside an archive is opened; one inside that is not.</summary>
    public const int MaxDepth = 2;

    // Reasons are shown to people (and translated by the page that shows them), so they are fixed texts.
    public const string NotAnArchive = "This is not a readable ZIP archive.";
    public const string TooManyFiles = "The archive holds too many files (limit 2000).";
    public const string TooLarge = "The archive is too large when unpacked (limit 1 GB).";
    public const string EntryTooLarge = "A file in the archive is larger than 200 MB.";
    public const string Unreadable = "The file could not be read (damaged or password-protected).";
    public const string NotADocument = "Not a document.";
    public const string NestedTooDeep = "An archive inside an archive inside an archive is not opened.";

    /// <summary>The files MatPaper reads: PDF, images, XML (e-invoices).</summary>
    public static readonly IReadOnlyCollection<string> DocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".xml", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp"
    };

    public static bool IsArchive(string? fileName)
        => string.Equals(Path.GetExtension(fileName ?? string.Empty), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <param name="archive">A seekable stream with the archive; it stays open.</param>
    /// <param name="archiveName">The file name of the archive (the start of every label).</param>
    /// <param name="allowedExtensions">Which entries count as documents (".pdf" ...); null = <see cref="DocumentExtensions"/>.
    /// An archive inside the archive is opened only when ".zip" is listed (or the list is null).</param>
    public static async IAsyncEnumerable<ArchiveItem> ReadAsync(
        Stream archive,
        string archiveName,
        IReadOnlyCollection<string>? allowedExtensions,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var budget = new Budget();
        await foreach (var item in ReadLevelAsync(archive, archiveName, allowedExtensions, 1, budget, ct).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private sealed class Budget
    {
        public int Entries = MaxEntries;
        public long Bytes = MaxTotalBytes;
    }

    private static async IAsyncEnumerable<ArchiveItem> ReadLevelAsync(
        Stream stream,
        string label,
        IReadOnlyCollection<string>? allowed,
        int depth,
        Budget budget,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (zip, openProblem) = TryOpen(stream);
        if (zip is null)
        {
            yield return new ArchiveItem(ArchiveItemKind.Problem, label, label, null, null, openProblem);
            yield break;
        }

        using (zip)
        {
            var entries = zip.Entries.Where(e => IsFile(e.FullName) && !IsJunk(e.FullName)).ToList();
            var names = new HashSet<string>(entries.Select(e => Normalize(e.FullName)), StringComparer.OrdinalIgnoreCase);
            var sidecars = ReadSidecars(entries);
            var nestedAllowed = allowed is null || allowed.Contains(".zip", StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                var path = Normalize(entry.FullName);
                var name = path[(path.LastIndexOf('/') + 1)..];
                var extension = Path.GetExtension(name).ToLowerInvariant();
                var where = label + "/" + path;

                // The metadata file and the XRechnung XML of a file belong to the file they are named after
                // (the same rule as in a watched folder).
                if (path.EndsWith(DocumentStorageService.MetadataCompanion, StringComparison.OrdinalIgnoreCase)
                    || (extension == DocumentStorageService.XmlCompanion && names.Contains(path[..^DocumentStorageService.XmlCompanion.Length])))
                {
                    continue;
                }

                var isNested = extension == ".zip";
                if (isNested ? !nestedAllowed : !IsDocument(extension, allowed))
                {
                    yield return new ArchiveItem(ArchiveItemKind.Skipped, where, name, null, null, NotADocument);
                    continue;
                }

                if (isNested && depth >= MaxDepth)
                {
                    yield return new ArchiveItem(ArchiveItemKind.Skipped, where, name, null, null, NestedTooDeep);
                    continue;
                }

                if (budget.Entries <= 0)
                {
                    yield return new ArchiveItem(ArchiveItemKind.Problem, label, label, null, null, TooManyFiles);
                    yield break;
                }

                budget.Entries--;

                var (bytes, problem) = ReadEntry(entry, budget);
                if (bytes is null)
                {
                    yield return new ArchiveItem(ArchiveItemKind.Problem, where, name, null, null, problem);
                    if (budget.Bytes <= 0)
                    {
                        yield break;
                    }

                    continue;
                }

                if (isNested)
                {
                    using var inner = new MemoryStream(bytes, writable: false);
                    await foreach (var item in ReadLevelAsync(inner, where, allowed, depth + 1, budget, ct).ConfigureAwait(false))
                    {
                        yield return item;
                    }

                    continue;
                }

                sidecars.TryGetValue(path, out var sidecar);
                yield return new ArchiveItem(ArchiveItemKind.Document, where, name, bytes, sidecar, null);
            }
        }
    }

    private static (ZipArchive? Zip, string? Problem) TryOpen(Stream stream)
    {
        // The directory of the archive says how many entries it lists. Opening it loads them all, so an archive with
        // millions of (tiny) entries is turned away first, before that costs the memory.
        var listed = ReadEntryCount(stream);
        if (listed is null)
        {
            return (null, NotAnArchive);
        }

        if (listed >= ushort.MaxValue || listed > MaxDirectoryEntries)
        {
            return (null, TooManyFiles);
        }

        try
        {
            var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            _ = zip.Entries.Count; // reads the directory of the archive: a damaged one fails here
            return (zip, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            return (null, NotAnArchive);
        }
    }

    /// <summary>The number of entries named in the end-of-directory record at the end of an archive, or null if there is none.</summary>
    private static int? ReadEntryCount(Stream stream)
    {
        try
        {
            if (!stream.CanSeek || stream.Length < 22)
            {
                return null;
            }

            var length = stream.Length;
            var window = (int)Math.Min(length, 22 + ushort.MaxValue); // the record, behind a comment of up to 64 KB
            var buffer = new byte[window];
            stream.Seek(length - window, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            stream.Seek(0, SeekOrigin.Begin);

            for (var i = window - 22; i >= 0; i--)
            {
                if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
                {
                    return BitConverter.ToUInt16(buffer, i + 10);
                }
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The metadata files of the archive (<c>file.pdf.matpaper.json</c>), by the path of the file they belong to.</summary>
    private static Dictionary<string, DocumentSidecar> ReadSidecars(IEnumerable<ZipArchiveEntry> entries)
    {
        var found = new Dictionary<string, DocumentSidecar>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var path = Normalize(entry.FullName);
            if (!path.EndsWith(DocumentStorageService.MetadataCompanion, StringComparison.OrdinalIgnoreCase) || entry.Length > 1024 * 1024)
            {
                continue;
            }

            try
            {
                using var source = entry.Open();
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                var sidecar = DocumentSidecar.TryParse(buffer.ToArray());
                if (sidecar is not null)
                {
                    found[path[..^DocumentStorageService.MetadataCompanion.Length]] = sidecar;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                // a metadata file that cannot be read is simply not used
            }
        }

        return found;
    }

    /// <summary>Reads one entry into memory, never more than the limits allow (the size an entry declares can be a lie).</summary>
    private static (byte[]? Bytes, string? Problem) ReadEntry(ZipArchiveEntry entry, Budget budget)
    {
        try
        {
            var limit = Math.Min(MaxEntryBytes, budget.Bytes);
            if (entry.Length > limit)
            {
                return (null, entry.Length > MaxEntryBytes ? EntryTooLarge : TooLarge);
            }

            using var source = entry.Open();
            using var buffer = new MemoryStream((int)Math.Min(entry.Length, limit));
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    budget.Bytes = 0;
                    return (null, total > MaxEntryBytes ? EntryTooLarge : TooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            budget.Bytes -= total;
            return (buffer.ToArray(), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return (null, Unreadable);
        }
    }

    private static bool IsDocument(string extension, IReadOnlyCollection<string>? allowed)
        => extension.Length > 0 && (allowed ?? DocumentExtensions).Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders of a Mac (<c>__MACOSX</c>), hidden files, Office lock files and the thumbnail caches of Windows.</summary>
    private static bool IsJunk(string fullName)
    {
        var path = Normalize(fullName);
        var name = path[(path.LastIndexOf('/') + 1)..];
        return path.Split('/').Any(segment => segment.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
            || name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A folder entry ends with a slash.</summary>
    private static bool IsFile(string fullName)
    {
        var path = Normalize(fullName);
        return path.Length > 0 && !path.EndsWith('/');
    }

    /// <summary>Archives made on Windows sometimes use backslashes in the paths.</summary>
    private static string Normalize(string fullName) => fullName.Replace('\\', '/').TrimStart('/');
}
