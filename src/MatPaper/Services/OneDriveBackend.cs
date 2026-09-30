using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MatPaper.Services;

/// <summary>
/// A storage backend over Microsoft Graph v1.0 (OneDrive / OneDrive for Business / SharePoint
/// document libraries). Files are addressed by a forward-slash path relative to a drive anchor,
/// so the rest of MatPaper treats a OneDrive location like any other: the storage search lists it,
/// filing writes into it, and View/Download stream from it. Nothing is bulk-copied — reads use
/// HTTP range requests and OCR uses the existing temp-copy path.
/// <para>
/// Graph exposes path-based addressing directly (<c>…/root:/{path}:/…</c>), so we avoid the
/// per-segment folder-id walk the Drive backend needs for reads. Server-side conflict handling
/// (<c>@microsoft.graph.conflictBehavior=rename</c>) does the unique-naming, so there is no
/// client-side probe loop; the actual stored name is read back from the returned driveItem.
/// </para>
/// </summary>
internal sealed class OneDriveBackend : IStorageBackend
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    /// <summary>Files at or below this size use a single PUT; larger ones use an upload session.</summary>
    private const long SimpleUploadMax = 4L * 1024 * 1024;

    /// <summary>Upload-session chunk size: 10 MiB = 32 × 320 KiB (Graph requires a 320 KiB multiple).</summary>
    private const int ChunkSize = 10 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _accessToken;

    /// <summary><c>/me/drive</c> or <c>/drives/{id}</c>.</summary>
    private readonly string _driveSeg;

    /// <summary><c>/root</c> or <c>/items/{id}</c> — the connection's anchor folder.</summary>
    private readonly string _anchorSeg;

    /// <summary>Sub-folder path (by name, forward slashes) beneath the anchor that acts as this
    /// location's root; empty means the anchor itself. Mirrors SMB's base path.</summary>
    private readonly string _base;

    // Folder paths (qualified, from the anchor) we have already created/verified this instance.
    private readonly HashSet<string> _ensured = new(StringComparer.Ordinal);

    public OneDriveBackend(HttpClient http, string accessToken, string? driveId, string? rootItemId, string? basePath)
    {
        _http = http;
        _accessToken = accessToken;
        _driveSeg = string.IsNullOrWhiteSpace(driveId) ? "/me/drive" : $"/drives/{driveId.Trim()}";
        _anchorSeg = string.IsNullOrWhiteSpace(rootItemId) ? "/root" : $"/items/{rootItemId.Trim()}";
        _base = (basePath ?? string.Empty).Replace('\\', '/').Trim('/');
    }

    // ----- URL building -----------------------------------------------------

    /// <summary>Prefixes a location-relative path with the base folder.</summary>
    private string Qualify(string path)
    {
        var clean = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        return _base.Length == 0 ? clean : (clean.Length == 0 ? _base : _base + "/" + clean);
    }

    private static string Encode(string qualified)
        => string.Join("/", qualified.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    /// <summary>Base address of the anchor (no path), e.g. <c>…/me/drive/root</c>.</summary>
    private string AnchorUrl() => $"{GraphBase}{_driveSeg}{_anchorSeg}";

    /// <summary>Address of a non-empty qualified path, e.g. <c>…/root:/Docs/2026</c>.</summary>
    private string ItemUrl(string qualified) => $"{AnchorUrl()}:/{Encode(qualified)}";

    private string MetaUrl(string qualified, string? select)
    {
        if (qualified.Length == 0)
        {
            return select is null ? AnchorUrl() : $"{AnchorUrl()}?$select={select}";
        }
        return select is null ? ItemUrl(qualified) : $"{ItemUrl(qualified)}:?$select={select}";
    }

    private string ChildrenUrl(string qualified, string? query)
    {
        var url = qualified.Length == 0 ? $"{AnchorUrl()}/children" : $"{ItemUrl(qualified)}:/children";
        return query is null ? url : $"{url}?{query}";
    }

    private string ContentUrl(string qualified, string? query)
    {
        var url = $"{ItemUrl(qualified)}:/content";
        return query is null ? url : $"{url}?{query}";
    }

    private string IdUrl(string id) => $"{GraphBase}{_driveSeg}/items/{id}";

    // ----- Reads / listing --------------------------------------------------

    public async Task<IReadOnlyList<StorageEntry>> ListFilesAsync(CancellationToken ct)
    {
        var results = new List<StorageEntry>();
        await WalkAsync(_base, string.Empty, results, ct).ConfigureAwait(false);
        return results;
    }

    private async Task WalkAsync(string qualifiedFolder, string prefix, List<StorageEntry> results, CancellationToken ct)
    {
        var url = ChildrenUrl(qualifiedFolder, "$select=id,name,size,file,folder,lastModifiedDateTime&$top=200");
        while (url is not null)
        {
            ct.ThrowIfCancellationRequested();
            using var response = await GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return; // folder does not exist yet: nothing to list
            }
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

            var page = await response.Content.ReadFromJsonSafeAsync<ChildrenResponse>(ct).ConfigureAwait(false) ?? new ChildrenResponse();
            foreach (var item in page.Value ?? new List<DriveItem>())
            {
                var childPrefix = prefix.Length == 0 ? item.Name : prefix + "/" + item.Name;
                if (item.IsFolder)
                {
                    var childFolder = qualifiedFolder.Length == 0 ? item.Name : qualifiedFolder + "/" + item.Name;
                    await WalkAsync(childFolder, childPrefix, results, ct).ConfigureAwait(false);
                }
                else
                {
                    results.Add(new StorageEntry(childPrefix, item.Size ?? 0, item.LastModifiedDateTime ?? DateTime.UtcNow));
                }
            }

            url = page.NextLink;
        }
    }

    public async Task<IReadOnlyList<string>> ListFoldersAsync(string relativeDir, CancellationToken ct)
    {
        var names = new List<string>();
        var url = ChildrenUrl(Qualify(relativeDir), "$select=name,folder&$top=200");
        while (url is not null)
        {
            ct.ThrowIfCancellationRequested();
            using var response = await GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Array.Empty<string>(); // folder does not exist yet
            }
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

            var page = await response.Content.ReadFromJsonSafeAsync<ChildrenResponse>(ct).ConfigureAwait(false) ?? new ChildrenResponse();
            names.AddRange((page.Value ?? new List<DriveItem>()).Where(i => i.IsFolder && i.Name.Length > 0).Select(i => i.Name));
            url = page.NextLink;
        }

        return names;
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
    {
        var qualified = Qualify(relativePath);
        using var response = await GetAsync(MetaUrl(qualified, "id"), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        var qualified = Qualify(relativePath);
        var item = await GetItemAsync(qualified, "id,size,@microsoft.graph.downloadUrl", ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The document file is missing on OneDrive.", relativePath);
        if (string.IsNullOrEmpty(item.DownloadUrl))
        {
            throw new IOException("OneDrive did not return a download URL for the file.");
        }

        var itemId = item.Id!;
        // The download URL expires within minutes; give the stream a way to mint a fresh one by id.
        async Task<string> Refresh(CancellationToken token)
        {
            var fresh = await GetByIdAsync(itemId, "@microsoft.graph.downloadUrl", token).ConfigureAwait(false);
            return fresh?.DownloadUrl ?? throw new IOException("OneDrive did not return a fresh download URL.");
        }

        return new GraphReadStream(_http, item.Size ?? 0, item.DownloadUrl!, Refresh);
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
        var qualifiedDir = Qualify(dir);
        var qualifiedFile = qualifiedDir.Length == 0 ? name : qualifiedDir + "/" + name;

        await EnsureFolderPathAsync(qualifiedDir, ct).ConfigureAwait(false);

        // Determine length; upload sessions need the full size up front for Content-Range.
        Stream src = content;
        long total;
        if (src.CanSeek)
        {
            total = src.Length - src.Position;
        }
        else
        {
            var ms = new MemoryStream();
            await src.CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;
            src = ms;
            total = ms.Length;
        }

        var item = total <= SimpleUploadMax
            ? await SimpleUploadAsync(qualifiedFile, src, ct).ConfigureAwait(false)
            : await SessionUploadAsync(qualifiedFile, name, src, total, ct).ConfigureAwait(false);

        var finalName = string.IsNullOrEmpty(item.Name) ? name : item.Name;
        return dir.Length == 0 ? finalName : dir + "/" + finalName;
    }

    private async Task<DriveItem> SimpleUploadAsync(string qualifiedFile, Stream src, CancellationToken ct)
    {
        // Buffer so the request can be retried safely.
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await src.CopyToAsync(buffer, ct).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        var url = ContentUrl(qualifiedFile, "@microsoft.graph.conflictBehavior=rename");
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Authorize(request);
            return request;
        }, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonSafeAsync<DriveItem>(ct).ConfigureAwait(false)
            ?? throw new IOException("OneDrive did not return the uploaded file.");
    }

    private async Task<DriveItem> SessionUploadAsync(string qualifiedFile, string name, Stream src, long total, CancellationToken ct)
    {
        var sessionBody = JsonSerializer.Serialize(new
        {
            item = new Dictionary<string, object?>
            {
                ["@microsoft.graph.conflictBehavior"] = "rename",
                ["name"] = name,
            },
        });

        string uploadUrl;
        using (var sessionResponse = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ItemUrl(qualifiedFile)}:/createUploadSession")
            {
                Content = new StringContent(sessionBody, Encoding.UTF8, "application/json"),
            };
            Authorize(request);
            return request;
        }, ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(sessionResponse, ct).ConfigureAwait(false);
            var session = await sessionResponse.Content.ReadFromJsonSafeAsync<UploadSession>(ct).ConfigureAwait(false);
            uploadUrl = session?.UploadUrl ?? throw new IOException("OneDrive did not return an upload URL.");
        }

        var chunk = new byte[ChunkSize];
        long sent = 0;
        DriveItem? completed = null;
        while (sent < total)
        {
            var toRead = (int)Math.Min(ChunkSize, total - sent);
            await ReadExactAsync(src, chunk, toRead, ct).ConfigureAwait(false);
            var start = sent;
            var end = sent + toRead - 1;

            using var response = await PutChunkAsync(uploadUrl, chunk, toRead, start, end, total, ct).ConfigureAwait(false);
            if (end == total - 1)
            {
                await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
                completed = await response.Content.ReadFromJsonSafeAsync<DriveItem>(ct).ConfigureAwait(false);
            }
            else if (response.StatusCode != HttpStatusCode.Accepted && !response.IsSuccessStatusCode)
            {
                await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            }

            sent += toRead;
        }

        return completed ?? throw new IOException("OneDrive upload session did not complete.");
    }

    private async Task<HttpResponseMessage> PutChunkAsync(string uploadUrl, byte[] chunk, int length, long start, long end, long total, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // The upload URL is pre-authenticated: no Authorization header.
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(chunk, 0, length) };
            request.Content.Headers.ContentLength = length;
            request.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {start}-{end}/{total}");

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode < 500 || attempt >= 3)
            {
                return response;
            }

            var delay = RetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> MoveAsync(string currentRelativePath, string desiredRelativePath, bool cleanupEmptyDirectories, CancellationToken ct)
    {
        var qualifiedSource = Qualify(currentRelativePath);
        var source = await GetItemAsync(qualifiedSource, "id", ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The document file is missing on OneDrive.", currentRelativePath);

        var (targetDir, targetName) = SplitPath(desiredRelativePath);
        var qualifiedTargetDir = Qualify(targetDir);
        await EnsureFolderPathAsync(qualifiedTargetDir, ct).ConfigureAwait(false);

        // The move target parent must be referenced by id — never the literal "root".
        var parent = await GetItemAsync(qualifiedTargetDir, "id", ct).ConfigureAwait(false)
            ?? throw new IOException($"Target folder '{targetDir}' could not be resolved on OneDrive.");

        var body = JsonSerializer.Serialize(new
        {
            name = targetName,
            parentReference = new { id = parent.Id },
        });

        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"{IdUrl(source.Id!)}?@microsoft.graph.conflictBehavior=rename")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            Authorize(request);
            return request;
        }, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var moved = await response.Content.ReadFromJsonSafeAsync<DriveItem>(ct).ConfigureAwait(false);

        var finalName = string.IsNullOrEmpty(moved?.Name) ? targetName : moved!.Name;
        return targetDir.Length == 0 ? finalName : targetDir + "/" + finalName;
    }

    public async Task DeleteAsync(string relativePath, bool cleanupEmptyDirectories, CancellationToken ct)
    {
        var qualified = Qualify(relativePath);
        var item = await GetItemAsync(qualified, "id", ct).ConfigureAwait(false);
        if (item is null)
        {
            return; // already gone
        }

        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, IdUrl(item.Id!));
            Authorize(request);
            return request;
        }, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }
    }

    public async Task TestAsync(CancellationToken ct)
    {
        // Confirms the token is accepted and the anchor is reachable, then makes sure this
        // location's base folder exists (creating it if missing, like the SMB/Drive backends).
        using (var response = await GetAsync(MetaUrl(string.Empty, "id"), ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }

        if (_base.Length != 0)
        {
            await EnsureFolderPathAsync(_base, ct).ConfigureAwait(false);
        }
    }

    // ----- Folder creation --------------------------------------------------

    /// <summary>Creates every folder along a qualified path (parents are not auto-created by uploads).</summary>
    private async Task EnsureFolderPathAsync(string qualifiedDir, CancellationToken ct)
    {
        // Read-only check — do NOT pre-insert qualifiedDir, or the loop below would treat the
        // deepest segment as already created and skip it (Graph never auto-creates parents).
        if (qualifiedDir.Length == 0 || _ensured.Contains(qualifiedDir))
        {
            return;
        }

        var accumulated = string.Empty;
        foreach (var segment in qualifiedDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var childPath = accumulated.Length == 0 ? segment : accumulated + "/" + segment;
            if (!_ensured.Contains(childPath))
            {
                await CreateFolderAsync(accumulated, segment, ct).ConfigureAwait(false);
                // Record only after a successful create, so a failure doesn't mark it ensured.
                _ensured.Add(childPath);
            }
            accumulated = childPath;
        }
    }

    private async Task CreateFolderAsync(string qualifiedParent, string name, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["folder"] = new { },
            ["@microsoft.graph.conflictBehavior"] = "fail",
        });

        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ChildrenUrl(qualifiedParent, null))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            Authorize(request);
            return request;
        }, ct).ConfigureAwait(false);

        // Conflict means the folder already exists — that's the goal.
        if (response.StatusCode == HttpStatusCode.Conflict || response.IsSuccessStatusCode)
        {
            return;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    // ----- HTTP helpers -----------------------------------------------------

    private void Authorize(HttpRequestMessage request)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

    private Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
        => SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            Authorize(request);
            return request;
        }, ct);

    /// <summary>GETs an item's metadata by qualified path; null on 404.</summary>
    private async Task<DriveItem?> GetItemAsync(string qualified, string select, CancellationToken ct)
    {
        using var response = await GetAsync(MetaUrl(qualified, select), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonSafeAsync<DriveItem>(ct).ConfigureAwait(false);
    }

    private async Task<DriveItem?> GetByIdAsync(string id, string select, CancellationToken ct)
    {
        using var response = await GetAsync($"{IdUrl(id)}?$select={select}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonSafeAsync<DriveItem>(ct).ConfigureAwait(false);
    }

    /// <summary>Sends a freshly-built request, retrying throttling (429) and 5xx while honoring Retry-After.</summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = factory();
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if ((status != 429 && status < 500) || attempt >= 3)
            {
                return response;
            }

            var delay = RetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is { } delta && delta > TimeSpan.Zero)
        {
            return delta < TimeSpan.FromSeconds(30) ? delta : TimeSpan.FromSeconds(30);
        }
        var seconds = Math.Min(16, 1 << attempt); // 1, 2, 4, 8, capped
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new IOException($"Microsoft Graph request failed ({(int)response.StatusCode}): {body}");
    }

    private static (string Dir, string Name) SplitPath(string relativePath)
    {
        var clean = relativePath.Replace('\\', '/').Trim('/');
        var slash = clean.LastIndexOf('/');
        return slash < 0 ? (string.Empty, clean) : (clean[..slash], clean[(slash + 1)..]);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Unexpected end of stream while uploading to OneDrive.");
            }
            total += read;
        }
    }

    // ----- DTOs -------------------------------------------------------------

    private sealed class ChildrenResponse
    {
        [JsonPropertyName("value")] public List<DriveItem>? Value { get; set; }
        [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
    }

    private sealed class DriveItem
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("lastModifiedDateTime")] public DateTime? LastModifiedDateTime { get; set; }
        [JsonPropertyName("folder")] public JsonElement? Folder { get; set; }
        [JsonPropertyName("file")] public JsonElement? File { get; set; }
        [JsonPropertyName("@microsoft.graph.downloadUrl")] public string? DownloadUrl { get; set; }

        public bool IsFolder => Folder.HasValue;
    }

    private sealed class UploadSession
    {
        [JsonPropertyName("uploadUrl")] public string? UploadUrl { get; set; }
    }
}

/// <summary>
/// Seekable read over a OneDrive file. Microsoft Graph ignores Range on <c>/content</c>, so bytes
/// are fetched a block at a time from the pre-authenticated download URL (no Authorization header).
/// The download URL expires within minutes, so a refresh delegate re-mints it on 401/403/410. A
/// ranged GET may legally return 200 with the whole file instead of 206; that fallback is handled.
/// </summary>
internal sealed class GraphReadStream : Stream
{
    private const int BlockSize = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _refresh;
    private readonly long _length;
    private string _downloadUrl;
    private long _position;

    private byte[]? _block;
    private long _blockStart;
    private int _blockLen;

    public GraphReadStream(HttpClient http, long length, string downloadUrl, Func<CancellationToken, Task<string>> refresh)
    {
        _http = http;
        _length = length;
        _downloadUrl = downloadUrl;
        _refresh = refresh;
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

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _downloadUrl);
            request.Headers.Range = new RangeHeaderValue(start, last); // no Authorization on the pre-auth URL

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if ((status == 401 || status == 403 || status == 410) && attempt == 0)
            {
                _downloadUrl = await _refresh(ct).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var size = (int)(last - start + 1);
                var block = new byte[size];
                _blockLen = await FillAsync(stream, block, size, ct).ConfigureAwait(false);
                _block = block;
                _blockStart = start;
            }
            else
            {
                // 200 OK: the body is the whole file. Skip to our block start, then fill.
                await SkipAsync(stream, start, ct).ConfigureAwait(false);
                var size = (int)Math.Min(BlockSize, _length - start);
                var block = new byte[size];
                _blockLen = await FillAsync(stream, block, size, ct).ConfigureAwait(false);
                _block = block;
                _blockStart = start;
            }
            return;
        }
    }

    private static async Task<int> FillAsync(Stream stream, byte[] block, int size, CancellationToken ct)
    {
        var total = 0;
        int read;
        while (total < size && (read = await stream.ReadAsync(block.AsMemory(total, size - total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }
        return total;
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return;
        }
        var scratch = new byte[Math.Min(count, 81920)];
        long skipped = 0;
        while (skipped < count)
        {
            var want = (int)Math.Min(scratch.Length, count - skipped);
            var read = await stream.ReadAsync(scratch.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            skipped += read;
        }
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
