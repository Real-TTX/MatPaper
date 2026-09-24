using System.Security.Cryptography;
using System.Text;
using MatPaper.Configuration;
using MatPaper.Data;
using Microsoft.EntityFrameworkCore;

namespace MatPaper.Services;

/// <summary>A file discovered in a storage location (forward-slash relative path).</summary>
public sealed record StorageEntry(string RelativePath, long Size, DateTime ModifiedUtc);

/// <summary>
/// A document file materialised on local disk for processing (OCR, thumbnails,
/// e-invoice parsing). Temporary copies (pulled from an SMB share) are deleted on dispose.
/// </summary>
public sealed class LocalCopy : IDisposable
{
    internal LocalCopy(string filePath, bool isTemporary)
    {
        FilePath = filePath;
        IsTemporary = isTemporary;
    }

    public string FilePath { get; }
    public bool IsTemporary { get; }

    public void Dispose()
    {
        if (!IsTemporary)
        {
            return;
        }

        try { File.Delete(FilePath); } catch { /* best effort */ }
    }
}

/// <summary>
/// Owns every physical document file operation. Two areas exist:
/// <list type="bullet">
/// <item><b>Staging</b> (<c>{data}/inbox</c>): where new documents wait while they are in
/// the review inbox. Files are named by token, no template applied.</item>
/// <item><b>Storage locations</b>: local folders or SMB shares. Files are placed using the
/// location's human-readable path template when a document is confirmed/filed.</item>
/// </list>
/// All relative paths use forward slashes and are guaranteed not to escape their root.
/// </summary>
public sealed class DocumentStorageService
{
    // Characters that are unsafe in a path segment on any of our target
    // platforms (build/runtime is Linux, development is Windows). Path
    // separators are handled separately so template separators survive.
    private static readonly char[] InvalidSegmentChars =
    {
        '<', '>', ':', '"', '|', '?', '*'
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly SecretProtector _secrets;

    public DocumentStorageService(IServiceScopeFactory scopes, SecretProtector secrets, AppConfig config)
    {
        _scopes = scopes;
        _secrets = secrets;

        var dataDir = Environment.GetEnvironmentVariable("MATPAPER_DATA") ?? "/data";
        StagingRoot = config.ResolveInboxPath(dataDir);
        TempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dataDir, "tmp")));
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(TempRoot);
        SweepTemp();
    }

    /// <summary>Drops temp copies left behind by a crashed process (older than an hour).</summary>
    private void SweepTemp()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var file in Directory.EnumerateFiles(TempRoot))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException) { /* in use: leave it */ }
                catch (UnauthorizedAccessException) { /* leave it */ }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // nothing to sweep
        }
    }

    /// <summary>
    /// Local folder holding files that are in the review inbox and not filed yet
    /// (configurable: env MATPAPER_INBOX or Storage.InboxPath in config/app.json).
    /// </summary>
    public string StagingRoot { get; }

    /// <summary>Scratch folder for temporary local copies of remote (SMB) files.</summary>
    public string TempRoot { get; }

    // ----- Path template ----------------------------------------------------

    /// <summary>
    /// Resolves the template tokens for the given metadata into a sanitized,
    /// forward-slash relative path (no leading slash, no traversal).
    /// </summary>
    public string BuildRelativePath(
        StorageLocation loc,
        string title,
        DateTime date,
        string? correspondentName,
        string? documentTypeName,
        string originalFileName)
    {
        ArgumentNullException.ThrowIfNull(loc);

        var extension = Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant();

        var titleValue = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(originalFileName ?? string.Empty)
            : title;

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{Correspondent}"] = SanitizeTokenValue(string.IsNullOrWhiteSpace(correspondentName) ? "Unsorted" : correspondentName),
            ["{Year}"] = date.ToString("yyyy"),
            ["{Month}"] = date.ToString("MM"),
            ["{DocumentType}"] = SanitizeTokenValue(string.IsNullOrWhiteSpace(documentTypeName) ? "Unfiled" : documentTypeName),
            ["{Date}"] = date.ToString("yyyy-MM-dd"),
            ["{Title}"] = SanitizeTokenValue(string.IsNullOrWhiteSpace(titleValue) ? "Untitled" : titleValue),
            // Keep the leading dot; only strip separators/invalid chars.
            ["{Ext}"] = SanitizeTokenValue(extension)
        };

        var template = string.IsNullOrWhiteSpace(loc.PathTemplate)
            ? "{Title}{Ext}"
            : loc.PathTemplate;

        foreach (var token in tokens)
        {
            template = template.Replace(token.Key, token.Value, StringComparison.OrdinalIgnoreCase);
        }

        var rawSegments = template.Split('/', '\\');
        var segments = new List<string>(rawSegments.Length);
        foreach (var raw in rawSegments)
        {
            var segment = SanitizeSegment(raw);
            if (segment.Length > 0)
            {
                segments.Add(segment);
            }
        }

        if (segments.Count == 0)
        {
            segments.Add(SanitizeSegment($"Untitled{extension}"));
        }

        return string.Join('/', segments);
    }

    // ----- Staging (review inbox) -------------------------------------------

    /// <summary>Writes a new inbox file as <c>{token}{ext}</c> into the staging root. Returns the relative name.</summary>
    public async Task<string> StageNewAsync(Guid token, string originalFileName, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var extension = SanitizeTokenValue(Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant());
        var relative = token.ToString("N") + extension;
        var absolute = Path.Combine(StagingRoot, relative);

        await using (var file = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        return relative;
    }

    public string GetStagedAbsolutePath(string relativePath)
        => LocalBackend.ResolveWithinRoot(StagingRoot, relativePath);

    public bool StagedExists(string relativePath)
        => File.Exists(GetStagedAbsolutePath(relativePath));

    /// <summary>Removes a staged file (inbox documents that get deleted never reach a storage location). Best effort.</summary>
    public bool TryDeleteStaged(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        try
        {
            var absolute = GetStagedAbsolutePath(relativePath);
            if (File.Exists(absolute))
            {
                File.Delete(absolute);
                return true;
            }
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
        catch (InvalidOperationException)
        {
            // path escaped the root: never touch anything outside the staging area
        }

        return false;
    }

    /// <summary>
    /// Moves a staged file into <paramref name="target"/> at the desired template path
    /// (unique-suffixed on collision) and returns the actual relative path in the location.
    /// </summary>
    public async Task<string> FileFromStagingAsync(
        string stagedRelativePath, StorageLocation target, string desiredRelativePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        var source = GetStagedAbsolutePath(stagedRelativePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The staged file is missing.", source);
        }

        var backend = await GetBackendAsync(target, ct).ConfigureAwait(false);
        if (backend is LocalBackend local)
        {
            return local.MoveIn(source, desiredRelativePath);
        }

        string actual;
        await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            actual = await backend.SaveNewAsync(desiredRelativePath, stream, ct).ConfigureAwait(false);
        }

        File.Delete(source);
        return actual;
    }

    // ----- Storage location operations --------------------------------------

    /// <summary>Writes new content at the desired path (unique-suffixing on collision). Returns the actual relative path.</summary>
    public async Task<string> SaveNewAsync(StorageLocation loc, string desiredRelativePath, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(content);
        var backend = await GetBackendAsync(loc, ct).ConfigureAwait(false);
        return await backend.SaveNewAsync(desiredRelativePath, content, ct).ConfigureAwait(false);
    }

    /// <summary>Moves a file within one location, unique-suffixing on collision and cleaning emptied folders.</summary>
    public async Task<string> MoveAsync(StorageLocation loc, string currentRelativePath, string desiredRelativePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (string.Equals(currentRelativePath, desiredRelativePath, StringComparison.Ordinal))
        {
            return currentRelativePath;
        }

        var backend = await GetBackendAsync(loc, ct).ConfigureAwait(false);
        return await backend.MoveAsync(currentRelativePath, desiredRelativePath, ct).ConfigureAwait(false);
    }

    /// <summary>Moves a file between two locations (any kinds): copy, then delete the source.</summary>
    public async Task<string> RelocateAsync(
        StorageLocation fromLoc, string fromRelativePath,
        StorageLocation toLoc, string desiredRelativePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fromLoc);
        ArgumentNullException.ThrowIfNull(toLoc);

        if (fromLoc.Id == toLoc.Id)
        {
            return await MoveAsync(fromLoc, fromRelativePath, desiredRelativePath, ct).ConfigureAwait(false);
        }

        var from = await GetBackendAsync(fromLoc, ct).ConfigureAwait(false);
        var to = await GetBackendAsync(toLoc, ct).ConfigureAwait(false);

        string actual;
        await using (var source = await from.OpenReadAsync(fromRelativePath, ct).ConfigureAwait(false))
        {
            actual = await to.SaveNewAsync(desiredRelativePath, source, ct).ConfigureAwait(false);
        }

        await from.DeleteAsync(fromRelativePath, ct).ConfigureAwait(false);
        return actual;
    }

    public async Task<bool> ExistsAsync(StorageLocation loc, string relativePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var backend = await GetBackendAsync(loc, ct).ConfigureAwait(false);
        return await backend.ExistsAsync(relativePath, ct).ConfigureAwait(false);
    }

    /// <summary>Opens a seekable read stream. Throws <see cref="FileNotFoundException"/> when missing.</summary>
    public async Task<Stream> OpenReadAsync(StorageLocation loc, string relativePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var backend = await GetBackendAsync(loc, ct).ConfigureAwait(false);
        return await backend.OpenReadAsync(relativePath, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(StorageLocation loc, string relativePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var backend = await GetBackendAsync(loc, ct).ConfigureAwait(false);
        await backend.DeleteAsync(relativePath, ct).ConfigureAwait(false);
    }

    /// <summary>Lists every file under the location root. Throws <see cref="DirectoryNotFoundException"/> when the root is missing.</summary>
    public async Task<IReadOnlyList<StorageEntry>> ListFilesAsync(StorageLocation loc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var backend = await GetBackendAsync(loc, ct).ConfigureAwait(false);
        return await backend.ListFilesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// SHA-256 for many files of one location in a single batch (remote backends reuse one
    /// connection for the whole run). Unreadable files are omitted from the result.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ComputeHashesAsync(
        StorageLocation loc, IReadOnlyCollection<string> relativePaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loc);

        if (relativePaths is null || relativePaths.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var backend = await GetBackendAsync(loc, ct).ConfigureAwait(false);
        return await backend.ComputeHashesAsync(relativePaths, ct).ConfigureAwait(false);
    }

    internal static string HashOf(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Verifies the location is reachable/writable (creates the SMB base folder if missing). Throws with a readable message.</summary>
    public async Task TestAsync(StorageLocation loc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var backend = await GetBackendAsync(loc, ct).ConfigureAwait(false);
        await backend.TestAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Absolute path for local locations; null for remote ones.</summary>
    public string? TryGetLocalPath(StorageLocation loc, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Kind == StorageKind.Local && !string.IsNullOrWhiteSpace(loc.RootPath)
            ? LocalBackend.ResolveWithinRoot(LocalBackend.RootFullPath(loc), relativePath)
            : null;
    }

    // ----- Document-level helpers -------------------------------------------

    /// <summary>Absolute path when the file is on local disk (staged or local location); null for SMB.</summary>
    public string? TryGetDocumentLocalPath(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrEmpty(document.RelativePath))
        {
            return null;
        }

        if (document.IsStaged)
        {
            return GetStagedAbsolutePath(document.RelativePath);
        }

        return document.StorageLocation is { } loc ? TryGetLocalPath(loc, document.RelativePath) : null;
    }

    public async Task<bool> DocumentExistsAsync(Document document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrEmpty(document.RelativePath))
        {
            return false;
        }

        if (document.IsStaged)
        {
            return StagedExists(document.RelativePath);
        }

        return document.StorageLocation is { } loc
            && await ExistsAsync(loc, document.RelativePath, ct).ConfigureAwait(false);
    }

    /// <summary>Opens the document's file for reading wherever it lives (requires <c>StorageLocation</c> loaded for filed documents).</summary>
    public async Task<Stream> OpenDocumentReadAsync(Document document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var local = TryGetDocumentLocalPath(document);
        if (local is not null)
        {
            if (!File.Exists(local))
            {
                throw new FileNotFoundException("The document file is missing.", local);
            }

            return new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        }

        if (document.StorageLocation is null)
        {
            throw new FileNotFoundException("The document has no storage location.");
        }

        return await OpenReadAsync(document.StorageLocation, document.RelativePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a local file path for processing: the real path for staged/local files, a
    /// temporary download for SMB files (deleted when the returned handle is disposed).
    /// </summary>
    public async Task<LocalCopy> GetLocalCopyAsync(Document document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var local = TryGetDocumentLocalPath(document);
        if (local is not null)
        {
            if (!File.Exists(local))
            {
                throw new FileNotFoundException("The document file is missing.", local);
            }

            return new LocalCopy(local, isTemporary: false);
        }

        if (document.StorageLocation is null)
        {
            throw new FileNotFoundException("The document has no storage location.");
        }

        var extension = SanitizeTokenValue(Path.GetExtension(document.OriginalFileName ?? string.Empty).ToLowerInvariant());
        var temp = Path.Combine(TempRoot, $"{document.Token:N}-{Guid.NewGuid():N}{extension}");

        try
        {
            await using var source = await OpenReadAsync(document.StorageLocation, document.RelativePath, ct).ConfigureAwait(false);
            await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }
        catch
        {
            // A half-written copy must not survive: nothing else would ever delete it.
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        return new LocalCopy(temp, isTemporary: true);
    }

    // ----- Backends ---------------------------------------------------------

    private async Task<IStorageBackend> GetBackendAsync(StorageLocation loc, CancellationToken ct)
    {
        if (loc.Kind == StorageKind.Smb)
        {
            var connection = await ResolveSmbConnectionAsync(loc, ct).ConfigureAwait(false);
            return new SmbBackend(connection, loc.SmbPath);
        }

        return new LocalBackend(LocalBackend.RootFullPath(loc));
    }

    /// <summary>Builds the SMB connection from the location; loads the credential when it is not attached.</summary>
    private async Task<SmbConnection> ResolveSmbConnectionAsync(StorageLocation loc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loc.SmbHost) || string.IsNullOrWhiteSpace(loc.SmbShare))
        {
            throw new InvalidOperationException($"Storage location '{loc.Name}' has no SMB host or share.");
        }

        var credential = loc.Credential;
        if (credential is null && loc.CredentialId is long credentialId)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            credential = await db.Credentials
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == credentialId && c.UpdateState != UpdateState.Deleted, ct)
                .ConfigureAwait(false);
        }

        if (credential is null)
        {
            throw new InvalidOperationException($"Storage location '{loc.Name}' has no saved credential.");
        }

        return new SmbConnection(
            loc.SmbHost.Trim(),
            loc.SmbShare.Trim(),
            credential.Domain,
            credential.Username,
            _secrets.Unprotect(credential.ProtectedPassword));
    }

    // ----- Sanitizing -------------------------------------------------------

    internal static string SanitizeTokenValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '/' || ch == '\\')
            {
                builder.Append(' ');
                continue;
            }

            if (char.IsControl(ch) || Array.IndexOf(InvalidSegmentChars, ch) >= 0)
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    internal static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(segment.Length);
        var previousWasWhitespace = false;

        foreach (var ch in segment)
        {
            if (ch == '/' || ch == '\\' || char.IsControl(ch) || Array.IndexOf(InvalidSegmentChars, ch) >= 0)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(ch);
            previousWasWhitespace = false;
        }

        // Trim leading/trailing dots and spaces; guard against reserved names.
        var result = builder.ToString().Trim().Trim('.').Trim();
        return result;
    }
}

/// <summary>Physical file operations for one storage location root.</summary>
internal interface IStorageBackend
{
    Task<string> SaveNewAsync(string desiredRelativePath, Stream content, CancellationToken ct);
    Task<string> MoveAsync(string currentRelativePath, string desiredRelativePath, CancellationToken ct);
    Task<bool> ExistsAsync(string relativePath, CancellationToken ct);
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct);
    Task DeleteAsync(string relativePath, CancellationToken ct);
    Task<IReadOnlyList<StorageEntry>> ListFilesAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> ComputeHashesAsync(IReadOnlyCollection<string> relativePaths, CancellationToken ct);
    Task TestAsync(CancellationToken ct);
}

/// <summary>Local folder backend.</summary>
internal sealed class LocalBackend : IStorageBackend
{
    private readonly string _root;

    public LocalBackend(string rootFullPath)
    {
        _root = rootFullPath;
    }

    public static string RootFullPath(StorageLocation loc)
    {
        if (string.IsNullOrWhiteSpace(loc.RootPath))
        {
            throw new InvalidOperationException($"Storage location '{loc.Name}' has no root path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(loc.RootPath));
    }

    /// <summary>
    /// Safely combines a root with a relative path, rejecting any path that would escape it.
    /// </summary>
    public static string ResolveWithinRoot(string rootFullPath, string relativePath)
    {
        var normalized = (relativePath ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/');

        var combined = Path.GetFullPath(Path.Combine(rootFullPath, normalized));

        var rootWithSeparator = rootFullPath + Path.DirectorySeparatorChar;
        if (!combined.Equals(rootFullPath, StringComparison.Ordinal) &&
            !combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved path escapes the storage root.");
        }

        return combined;
    }

    public async Task<string> SaveNewAsync(string desiredRelativePath, Stream content, CancellationToken ct)
    {
        var targetAbsolute = ResolveWithinRoot(_root, desiredRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute)!);

        var uniqueAbsolute = MakeUnique(targetAbsolute);
        await using (var file = new FileStream(uniqueAbsolute, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        return ToRelative(_root, uniqueAbsolute);
    }

    /// <summary>Moves a file from anywhere on local disk (e.g. the staging area) into this root.</summary>
    public string MoveIn(string sourceAbsolute, string desiredRelativePath)
    {
        var targetAbsolute = ResolveWithinRoot(_root, desiredRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute)!);

        var uniqueAbsolute = MakeUnique(targetAbsolute);
        File.Move(sourceAbsolute, uniqueAbsolute);
        return ToRelative(_root, uniqueAbsolute);
    }

    public Task<string> MoveAsync(string currentRelativePath, string desiredRelativePath, CancellationToken ct)
    {
        var sourceAbsolute = ResolveWithinRoot(_root, currentRelativePath);
        var targetAbsolute = ResolveWithinRoot(_root, desiredRelativePath);

        if (string.Equals(sourceAbsolute, targetAbsolute, StringComparison.Ordinal))
        {
            return Task.FromResult(ToRelative(_root, sourceAbsolute));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetAbsolute)!);

        var uniqueAbsolute = MakeUnique(targetAbsolute);
        File.Move(sourceAbsolute, uniqueAbsolute);

        CleanupEmptyDirectories(_root, Path.GetDirectoryName(sourceAbsolute));

        return Task.FromResult(ToRelative(_root, uniqueAbsolute));
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
        => Task.FromResult(File.Exists(ResolveWithinRoot(_root, relativePath)));

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        var absolute = ResolveWithinRoot(_root, relativePath);
        if (!File.Exists(absolute))
        {
            throw new FileNotFoundException("The document file is missing.", absolute);
        }

        return Task.FromResult<Stream>(new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct)
    {
        var absolute = ResolveWithinRoot(_root, relativePath);
        if (File.Exists(absolute))
        {
            File.Delete(absolute);
            CleanupEmptyDirectories(_root, Path.GetDirectoryName(absolute));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StorageEntry>> ListFilesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Folder '{_root}' does not exist.");
        }

        var entries = new List<StorageEntry>();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                entries.Add(new StorageEntry(ToRelative(_root, file), info.Length, info.LastWriteTimeUtc));
            }
            catch (IOException)
            {
                // unreadable entry: skip
            }
            catch (UnauthorizedAccessException)
            {
                // unreadable entry: skip
            }
        }

        return Task.FromResult<IReadOnlyList<StorageEntry>>(entries);
    }

    public Task<IReadOnlyDictionary<string, string>> ComputeHashesAsync(IReadOnlyCollection<string> relativePaths, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(relativePaths.Count, StringComparer.Ordinal);

        foreach (var relativePath in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = new FileStream(ResolveWithinRoot(_root, relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
                result[relativePath] = DocumentStorageService.HashOf(stream);
            }
            catch (IOException) { /* unreadable: skip */ }
            catch (UnauthorizedAccessException) { /* skip */ }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }

    public Task TestAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Folder '{_root}' does not exist inside the container.");
        }

        return Task.CompletedTask;
    }

    private static string ToRelative(string rootFullPath, string absolutePath)
    {
        var relative = Path.GetRelativePath(rootFullPath, absolutePath);
        return relative.Replace('\\', '/');
    }

    private static string MakeUnique(string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            return absolutePath;
        }

        var directory = Path.GetDirectoryName(absolutePath)!;
        var name = Path.GetFileNameWithoutExtension(absolutePath);
        var extension = Path.GetExtension(absolutePath);

        for (var counter = 2; ; counter++)
        {
            var candidate = Path.Combine(directory, $"{name} ({counter}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CleanupEmptyDirectories(string rootFullPath, string? startDirectory)
    {
        var current = startDirectory;

        while (!string.IsNullOrEmpty(current) &&
               !current.Equals(rootFullPath, StringComparison.Ordinal) &&
               current.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            try
            {
                if (Directory.Exists(current) &&
                    !Directory.EnumerateFileSystemEntries(current).Any())
                {
                    Directory.Delete(current);
                }
                else
                {
                    break;
                }
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }
}

/// <summary>
/// SMB/CIFS share backend. Each operation opens its own short-lived session (streams
/// returned by <see cref="OpenReadAsync"/> keep theirs open until disposed). The blocking
/// SMBLibrary calls run on the thread pool.
/// </summary>
internal sealed class SmbBackend : IStorageBackend
{
    private readonly SmbConnection _connection;
    private readonly string _base;

    public SmbBackend(SmbConnection connection, string? basePath)
    {
        _connection = connection;
        _base = SmbSession.Norm(basePath);
    }

    public Task<string> SaveNewAsync(string desiredRelativePath, Stream content, CancellationToken ct) => Task.Run(() =>
    {
        using var session = SmbSession.Connect(_connection);
        var target = Full(desiredRelativePath);
        session.EnsureDirectory(DirOf(target));
        target = MakeUnique(session, target);
        session.WriteFile(target, content);
        return ToRelative(target);
    }, ct);

    public Task<string> MoveAsync(string currentRelativePath, string desiredRelativePath, CancellationToken ct) => Task.Run(() =>
    {
        var source = Full(currentRelativePath);
        var target = Full(desiredRelativePath);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return ToRelative(source);
        }

        using var session = SmbSession.Connect(_connection);
        if (!session.FileExists(source))
        {
            throw new FileNotFoundException("The document file is missing on the share.", currentRelativePath);
        }

        session.EnsureDirectory(DirOf(target));
        target = MakeUnique(session, target);
        session.Rename(source, target);
        CleanupEmptyDirectories(session, DirOf(source));
        return ToRelative(target);
    }, ct);

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct) => Task.Run(() =>
    {
        using var session = SmbSession.Connect(_connection);
        return session.FileExists(Full(relativePath));
    }, ct);

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct) => Task.Run<Stream>(() =>
    {
        var session = SmbSession.Connect(_connection);
        try
        {
            return session.OpenRead(Full(relativePath), ownsSession: true);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }, ct);

    public Task DeleteAsync(string relativePath, CancellationToken ct) => Task.Run(() =>
    {
        using var session = SmbSession.Connect(_connection);
        var full = Full(relativePath);
        session.TryDelete(full);
        CleanupEmptyDirectories(session, DirOf(full));
    }, ct);

    public Task<IReadOnlyList<StorageEntry>> ListFilesAsync(CancellationToken ct) => Task.Run<IReadOnlyList<StorageEntry>>(() =>
    {
        using var session = SmbSession.Connect(_connection);
        return session.ListEntries(_base, null, recursive: true)
            .Select(e => new StorageEntry(ToRelative(e.Path), e.Size, e.ModifiedUtc))
            .ToList();
    }, ct);

    /// <summary>One session for the whole batch — a scan must not re-handshake per file.</summary>
    public Task<IReadOnlyDictionary<string, string>> ComputeHashesAsync(IReadOnlyCollection<string> relativePaths, CancellationToken ct)
        => Task.Run<IReadOnlyDictionary<string, string>>(() =>
    {
        var result = new Dictionary<string, string>(relativePaths.Count, StringComparer.Ordinal);

        using var session = SmbSession.Connect(_connection);
        foreach (var relativePath in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var stream = session.OpenRead(Full(relativePath), ownsSession: false);
                result[relativePath] = DocumentStorageService.HashOf(stream);
            }
            catch (IOException) { /* unreadable: skip */ }
        }

        return result;
    }, ct);

    public Task TestAsync(CancellationToken ct) => Task.Run(() =>
    {
        using var session = SmbSession.Connect(_connection);
        if (!string.IsNullOrEmpty(_base) && !session.DirectoryExists(_base))
        {
            session.EnsureDirectory(_base);
            if (!session.DirectoryExists(_base))
            {
                throw new IOException($"Folder '{_base}' does not exist on the share and could not be created.");
            }
        }
    }, ct);

    /// <summary>Share-relative backslash path under the base folder; rejects traversal.</summary>
    private string Full(string relativePath)
    {
        var normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "." || segment == "..")
            {
                throw new InvalidOperationException("Resolved path escapes the storage root.");
            }
        }

        normalized = normalized.Replace('/', '\\');
        if (string.IsNullOrEmpty(_base))
        {
            return normalized;
        }

        return normalized.Length == 0 ? _base : _base + "\\" + normalized;
    }

    private string ToRelative(string fullPath)
    {
        var relative = fullPath;
        if (!string.IsNullOrEmpty(_base) && relative.StartsWith(_base + "\\", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[(_base.Length + 1)..];
        }

        return relative.Replace('\\', '/');
    }

    private static string DirOf(string fullPath)
        => fullPath.Contains('\\') ? fullPath[..fullPath.LastIndexOf('\\')] : string.Empty;

    private static string MakeUnique(SmbSession session, string fullPath)
    {
        if (!session.FileExists(fullPath))
        {
            return fullPath;
        }

        var directory = DirOf(fullPath);
        var fileName = fullPath.Contains('\\') ? fullPath[(fullPath.LastIndexOf('\\') + 1)..] : fullPath;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var counter = 2; ; counter++)
        {
            var candidateName = $"{name} ({counter}){extension}";
            var candidate = string.IsNullOrEmpty(directory) ? candidateName : directory + "\\" + candidateName;
            if (!session.FileExists(candidate))
            {
                return candidate;
            }
        }
    }

    private void CleanupEmptyDirectories(SmbSession session, string directory)
    {
        var current = directory;
        while (!string.IsNullOrEmpty(current)
               && current.Length > _base.Length
               && current.StartsWith(_base, StringComparison.OrdinalIgnoreCase))
        {
            if (!session.TryRemoveEmptyDirectory(current))
            {
                break;
            }

            current = DirOf(current);
        }
    }
}
