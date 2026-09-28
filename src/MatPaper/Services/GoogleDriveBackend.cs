using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatPaper.Services;

/// <summary>
/// A storage backend over the Google Drive REST API (v3). Files are addressed by a
/// forward-slash path relative to a configured root folder, so the rest of MatPaper keeps
/// treating a Drive location like any other: the storage search lists it, filing writes into
/// it, and View/Download stream from it. The container stays lean — nothing is bulk-copied;
/// reads are served with HTTP range requests and OCR uses the existing temp-copy path.
/// <para>
/// Drive allows several files with the same name in one folder; MatPaper's own filing produces
/// unique names, and path lookups take the first match, mirroring the SMB backend's assumption.
/// </para>
/// </summary>
internal sealed class GoogleDriveBackend : IStorageBackend
{
    private const string ApiBase = "https://www.googleapis.com/drive/v3";
    private const string UploadBase = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FolderMime = "application/vnd.google-apps.folder";

    private readonly HttpClient _http;
    private readonly string _accessToken;
    private readonly string _rootFolderId;

    /// <summary>Sub-folder path (by name, forward slashes) beneath the connection root that acts
    /// as this location's root; empty means the connection root itself. Mirrors SMB's base path.</summary>
    private readonly string _base;

    // path (forward slashes, from the connection root) -> folder id, filled as we walk.
    private readonly Dictionary<string, string> _folderCache = new(StringComparer.Ordinal);

    public GoogleDriveBackend(HttpClient http, string accessToken, string? rootFolderId, string? basePath)
    {
        _http = http;
        _accessToken = accessToken;
        _rootFolderId = string.IsNullOrWhiteSpace(rootFolderId) ? "root" : rootFolderId.Trim();
        _base = (basePath ?? string.Empty).Replace('\\', '/').Trim('/');
        _folderCache[string.Empty] = _rootFolderId;
    }

    /// <summary>Prefixes a location-relative directory with the base path so all lookups happen
    /// beneath this location's root.</summary>
    private string Qualify(string dir)
        => _base.Length == 0 ? dir : (dir.Length == 0 ? _base : _base + "/" + dir);

    // ----- Reads / listing --------------------------------------------------

    public async Task<IReadOnlyList<StorageEntry>> ListFilesAsync(CancellationToken ct)
    {
        var results = new List<StorageEntry>();
        var baseId = await ResolveFolderIdAsync(_base, create: false, ct).ConfigureAwait(false);
        if (baseId is not null)
        {
            await WalkAsync(baseId, string.Empty, results, ct).ConfigureAwait(false);
        }
        return results;
    }

    private async Task WalkAsync(string folderId, string prefix, List<StorageEntry> results, CancellationToken ct)
    {
        string? pageToken = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{ApiBase}/files?q={Uri.EscapeDataString($"'{folderId}' in parents and trashed=false")}"
                + "&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime)"
                + "&pageSize=1000"
                + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");

            var list = await GetJsonAsync<FileList>(url, ct).ConfigureAwait(false);
            foreach (var f in list.Files ?? new List<DriveFile>())
            {
                var relPath = prefix.Length == 0 ? f.Name : prefix + "/" + f.Name;
                if (f.MimeType == FolderMime)
                {
                    await WalkAsync(f.Id!, relPath, results, ct).ConfigureAwait(false);
                }
                else
                {
                    long.TryParse(f.Size, out var size);
                    results.Add(new StorageEntry(relPath, size, f.ModifiedTime ?? DateTime.UtcNow));
                }
            }

            pageToken = list.NextPageToken;
        }
        while (pageToken is not null);
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
        => (await FindFileAsync(relativePath, ct).ConfigureAwait(false)) is not null;

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        var file = await FindFileAsync(relativePath, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The document file is missing on Drive.", relativePath);
        long.TryParse(file.Size, out var size);
        return new DriveReadStream(_http, _accessToken, file.Id!, size);
    }

    public Task<IReadOnlyDictionary<string, string>> ComputeHashesAsync(IReadOnlyCollection<string> relativePaths, CancellationToken ct)
        => Task.Run<IReadOnlyDictionary<string, string>>(async () =>
        {
            var result = new Dictionary<string, string>(relativePaths.Count, StringComparer.Ordinal);
            foreach (var relativePath in relativePaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var stream = await OpenReadAsync(relativePath, ct).ConfigureAwait(false);
                    result[relativePath] = DocumentStorageService.HashOf(stream);
                }
                catch (IOException) { /* unreadable: skip */ }
                catch (HttpRequestException) { /* unreadable: skip */ }
            }
            return result;
        }, ct);

    // ----- Writes -----------------------------------------------------------

    public async Task<string> SaveNewAsync(string desiredRelativePath, Stream content, CancellationToken ct)
    {
        var (dir, name) = SplitPath(desiredRelativePath);
        var folderId = await EnsureFolderPathAsync(dir, ct).ConfigureAwait(false);
        var uniqueName = await MakeUniqueAsync(folderId, name, ct).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        var metadata = JsonSerializer.Serialize(new { name = uniqueName, parents = new[] { folderId } });
        using var multipart = new MultipartContent("related")
        {
            new StringContent(metadata, Encoding.UTF8, "application/json"),
            new StreamContent(buffer) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadBase}?uploadType=multipart&fields=id,name") { Content = multipart };
        Authorize(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        return dir.Length == 0 ? uniqueName : dir + "/" + uniqueName;
    }

    public async Task<string> MoveAsync(string currentRelativePath, string desiredRelativePath, bool cleanupEmptyDirectories, CancellationToken ct)
    {
        var file = await FindFileAsync(currentRelativePath, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The document file is missing on Drive.", currentRelativePath);

        var (currentDir, _) = SplitPath(currentRelativePath);
        var (targetDir, targetName) = SplitPath(desiredRelativePath);
        var currentParentId = await EnsureFolderPathAsync(currentDir, ct).ConfigureAwait(false);
        var targetParentId = await EnsureFolderPathAsync(targetDir, ct).ConfigureAwait(false);
        var uniqueName = await MakeUniqueAsync(targetParentId, targetName, ct).ConfigureAwait(false);

        var url = $"{ApiBase}/files/{file.Id}?fields=id,name"
            + (targetParentId == currentParentId ? "" : $"&addParents={targetParentId}&removeParents={currentParentId}");
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = uniqueName }), Encoding.UTF8, "application/json"),
        };
        Authorize(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        return targetDir.Length == 0 ? uniqueName : targetDir + "/" + uniqueName;
    }

    public async Task DeleteAsync(string relativePath, bool cleanupEmptyDirectories, CancellationToken ct)
    {
        var file = await FindFileAsync(relativePath, ct).ConfigureAwait(false);
        if (file is null)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBase}/files/{file.Id}");
        Authorize(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }
    }

    public async Task TestAsync(CancellationToken ct)
    {
        // Confirms the token is accepted and the connection root is reachable, then makes sure
        // this location's base folder exists (creating it if missing, like the SMB backend).
        var probe = $"{ApiBase}/files?q={Uri.EscapeDataString($"'{_rootFolderId}' in parents and trashed=false")}&pageSize=1&fields=files(id)";
        await GetJsonAsync<FileList>(probe, ct).ConfigureAwait(false);

        if (_base.Length != 0)
        {
            await EnsureFolderPathAsync(string.Empty, ct).ConfigureAwait(false);
        }
    }

    // ----- Path resolution --------------------------------------------------

    private static (string Dir, string Name) SplitPath(string relativePath)
    {
        var clean = relativePath.Replace('\\', '/').Trim('/');
        var slash = clean.LastIndexOf('/');
        return slash < 0 ? (string.Empty, clean) : (clean[..slash], clean[(slash + 1)..]);
    }

    private async Task<string?> ResolveFolderIdAsync(string dirPath, bool create, CancellationToken ct)
    {
        var clean = dirPath.Replace('\\', '/').Trim('/');
        if (_folderCache.TryGetValue(clean, out var cached))
        {
            return cached;
        }

        var parentId = _rootFolderId;
        var accumulated = string.Empty;
        foreach (var segment in clean.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = accumulated.Length == 0 ? segment : accumulated + "/" + segment;
            if (_folderCache.TryGetValue(accumulated, out var known))
            {
                parentId = known;
                continue;
            }

            var childId = await FindChildAsync(parentId, segment, folder: true, ct).ConfigureAwait(false);
            if (childId is null)
            {
                if (!create)
                {
                    return null;
                }
                childId = await CreateFolderAsync(parentId, segment, ct).ConfigureAwait(false);
            }

            _folderCache[accumulated] = childId;
            parentId = childId;
        }

        return parentId;
    }

    private async Task<string> EnsureFolderPathAsync(string dirPath, CancellationToken ct)
        => (await ResolveFolderIdAsync(Qualify(dirPath), create: true, ct).ConfigureAwait(false))!;

    private async Task<DriveFile?> FindFileAsync(string relativePath, CancellationToken ct)
    {
        var (dir, name) = SplitPath(relativePath);
        var folderId = await ResolveFolderIdAsync(Qualify(dir), create: false, ct).ConfigureAwait(false);
        if (folderId is null)
        {
            return null;
        }

        var q = $"name = {Quote(name)} and '{folderId}' in parents and trashed = false";
        var url = $"{ApiBase}/files?q={Uri.EscapeDataString(q)}&fields=files(id,name,size,mimeType,modifiedTime)&pageSize=1";
        var list = await GetJsonAsync<FileList>(url, ct).ConfigureAwait(false);
        return list.Files?.FirstOrDefault(f => f.MimeType != FolderMime);
    }

    private async Task<string?> FindChildAsync(string parentId, string name, bool folder, CancellationToken ct)
    {
        var q = $"name = {Quote(name)} and '{parentId}' in parents and trashed = false"
            + (folder ? $" and mimeType = '{FolderMime}'" : "");
        var url = $"{ApiBase}/files?q={Uri.EscapeDataString(q)}&fields=files(id)&pageSize=1";
        var list = await GetJsonAsync<FileList>(url, ct).ConfigureAwait(false);
        return list.Files?.FirstOrDefault()?.Id;
    }

    private async Task<string> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { name, mimeType = FolderMime, parents = new[] { parentId } });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/files?fields=id")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        Authorize(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonSafeAsync<DriveFile>(ct).ConfigureAwait(false);
        return created?.Id ?? throw new IOException("Drive did not return the new folder id.");
    }

    private async Task<string> MakeUniqueAsync(string folderId, string name, CancellationToken ct)
    {
        if (await FindChildAsync(folderId, name, folder: false, ct).ConfigureAwait(false) is null)
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (await FindChildAsync(folderId, candidate, folder: false, ct).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }

        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    // ----- HTTP helpers -----------------------------------------------------

    private void Authorize(HttpRequestMessage request)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct) where T : new()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonSafeAsync<T>(ct).ConfigureAwait(false) ?? new T();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new IOException($"Google Drive request failed ({(int)response.StatusCode}): {body}");
    }

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // ----- DTOs -------------------------------------------------------------

    private sealed class FileList
    {
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
        [JsonPropertyName("files")] public List<DriveFile>? Files { get; set; }
    }

    private sealed class DriveFile
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("mimeType")] public string? MimeType { get; set; }
        [JsonPropertyName("size")] public string? Size { get; set; }
        [JsonPropertyName("modifiedTime")] public DateTime? ModifiedTime { get; set; }
    }
}

/// <summary>
/// Seekable read over a Drive file using HTTP range requests, for View/Download. Bytes are
/// fetched a block at a time and served from an in-memory buffer, so a sequential download (many
/// small <see cref="Read"/> calls from <c>FileStreamResult</c>) costs a handful of requests, not
/// one per copy buffer. A seek outside the current block simply refetches on the next read.
/// </summary>
internal sealed class DriveReadStream : Stream
{
    private const string Media = "https://www.googleapis.com/drive/v3/files";
    private const int BlockSize = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _accessToken;
    private readonly string _fileId;
    private readonly long _length;
    private long _position;

    // The most recently fetched block: bytes [_blockStart, _blockStart + _blockLen).
    private byte[]? _block;
    private long _blockStart;
    private int _blockLen;

    public DriveReadStream(HttpClient http, string accessToken, string fileId, long length)
    {
        _http = http;
        _accessToken = accessToken;
        _fileId = fileId;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_position >= _length || count == 0)
        {
            return 0;
        }

        if (_block is null || _position < _blockStart || _position >= _blockStart + _blockLen)
        {
            await FetchBlockAsync(_position, ct).ConfigureAwait(false);
        }

        var available = (int)(_blockStart + _blockLen - _position);
        var n = Math.Min(count, available);
        Array.Copy(_block!, (int)(_position - _blockStart), buffer, offset, n);
        _position += n;
        return n;
    }

    private async Task FetchBlockAsync(long start, CancellationToken ct)
    {
        var last = Math.Min(start + BlockSize, _length) - 1;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Media}/{_fileId}?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Range = new RangeHeaderValue(start, last);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var size = (int)(last - start + 1);
        var block = new byte[size];
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var total = 0;
        int read;
        while (total < size && (read = await stream.ReadAsync(block.AsMemory(total, size - total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        _block = block;
        _blockStart = start;
        _blockLen = total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class HttpContentJsonExtensions
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<T?> ReadFromJsonSafeAsync<T>(this HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false);
    }
}
