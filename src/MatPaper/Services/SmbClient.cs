using System.Text.RegularExpressions;
using SMBLibrary;
using SMBLibrary.Client;

namespace MatPaper.Services;

/// <summary>Connection details for an SMB/CIFS share.</summary>
public sealed record SmbConnection(string Host, string Share, string? Domain, string Username, string Password);

/// <summary>A file on a share: share-relative path (backslashes), size and last write time (UTC).</summary>
public sealed record SmbEntry(string Path, long Size, DateTime ModifiedUtc);

/// <summary>
/// Thin wrapper over SMBLibrary's SMB2 client used for network-share imports AND for
/// SMB storage locations without a host mount: connect once, enumerate, read (as bytes
/// or as a seekable stream), write, rename, delete. Paths are share-relative and use
/// backslashes internally; forward slashes are accepted on input.
/// </summary>
public sealed class SmbSession : IDisposable
{
    private readonly SMB2Client _client;
    private readonly ISMBFileStore _store;
    private bool _disposed;

    private SmbSession(SMB2Client client, ISMBFileStore store)
    {
        _client = client;
        _store = store;
    }

    public int MaxReadSize => (int)Math.Min(_client.MaxReadSize, 1024 * 1024);
    public int MaxWriteSize => (int)Math.Min(_client.MaxWriteSize, 1024 * 1024);

    public static SmbSession Connect(SmbConnection c)
    {
        var client = new SMB2Client();

        // SMBLibrary resolves the host itself and throws SocketException (or a bare
        // Exception when the name has no address). Normalise everything to IOException
        // so callers only ever have to handle one failure type.
        bool connected;
        try
        {
            connected = client.Connect(c.Host, SMBTransportType.DirectTCPTransport);
        }
        catch (Exception ex)
        {
            throw new IOException($"Cannot reach SMB host '{c.Host}': {ex.Message}", ex);
        }

        if (!connected)
        {
            throw new IOException($"Cannot reach SMB host '{c.Host}'.");
        }

        var login = client.Login(c.Domain ?? string.Empty, c.Username, c.Password);
        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new IOException($"SMB login failed ({login}).");
        }

        var store = client.TreeConnect(c.Share, out var treeStatus);
        if (treeStatus != NTStatus.STATUS_SUCCESS || store is null)
        {
            client.Logoff();
            client.Disconnect();
            throw new IOException($"Cannot connect to share '{c.Share}' ({treeStatus}).");
        }

        return new SmbSession(client, store);
    }

    /// <summary>Normalises a path to share-relative backslash form without leading/trailing separators.</summary>
    public static string Norm(string? path)
        => (path ?? string.Empty).Trim().Replace('/', '\\').Trim('\\');

    // ----- Listing ----------------------------------------------------------

    public IReadOnlyList<string> ListFiles(string startPath, string? pattern, bool recursive)
        => ListEntries(startPath, pattern, recursive).Select(e => e.Path).ToList();

    /// <summary>Lists files under <paramref name="startPath"/>. Throws if the start folder is missing.</summary>
    public IReadOnlyList<SmbEntry> ListEntries(string startPath, string? pattern, bool recursive)
    {
        var start = Norm(startPath);
        if (!DirectoryExists(start))
        {
            throw new DirectoryNotFoundException($"SMB folder '{start}' does not exist on the share.");
        }

        var results = new List<SmbEntry>();
        Walk(start, pattern, recursive, results);
        return results;
    }

    private void Walk(string dir, string? pattern, bool recursive, List<SmbEntry> results)
    {
        var status = _store.CreateFile(out var handle, out _, dir,
            AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return;
        }

        try
        {
            _store.QueryDirectory(out var list, handle, "*", FileInformationClass.FileDirectoryInformation);
            foreach (var info in list)
            {
                var entry = (FileDirectoryInformation)info;
                var name = entry.FileName;
                if (name is "." or "..")
                {
                    continue;
                }

                var full = string.IsNullOrEmpty(dir) ? name : dir + "\\" + name;
                var isDir = (entry.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0;
                if (isDir)
                {
                    if (recursive)
                    {
                        Walk(full, pattern, recursive, results);
                    }
                }
                else if (MatchPattern(name, pattern))
                {
                    results.Add(new SmbEntry(full, entry.EndOfFile, entry.LastWriteTime.ToUniversalTime()));
                }
            }
        }
        finally
        {
            _store.CloseFile(handle);
        }
    }

    private static bool MatchPattern(string name, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            return true;
        }

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    // ----- Existence --------------------------------------------------------

    public bool DirectoryExists(string path)
    {
        var status = _store.CreateFile(out var handle, out _, Norm(path),
            AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return false;
        }

        _store.CloseFile(handle);
        return true;
    }

    public bool FileExists(string path)
    {
        var status = _store.CreateFile(out var handle, out _, Norm(path),
            AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return false;
        }

        _store.CloseFile(handle);
        return true;
    }

    // ----- Reading ----------------------------------------------------------

    public byte[] ReadAllBytes(string path)
    {
        using var stream = OpenRead(path, ownsSession: false);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Opens a seekable read-only stream over a file on the share. When
    /// <paramref name="ownsSession"/> is true, disposing the stream also disposes this session.
    /// </summary>
    public SmbReadStream OpenRead(string path, bool ownsSession)
    {
        var status = _store.CreateFile(out var handle, out _, Norm(path),
            AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        if (status == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND || status == NTStatus.STATUS_OBJECT_PATH_NOT_FOUND)
        {
            throw new FileNotFoundException($"'{path}' was not found on the share.", path);
        }
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"Cannot open '{path}' ({status}).");
        }

        long length;
        try
        {
            status = _store.GetFileInformation(out var info, handle, FileInformationClass.FileStandardInformation);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"Cannot read size of '{path}' ({status}).");
            }

            length = ((FileStandardInformation)info).EndOfFile;
        }
        catch
        {
            _store.CloseFile(handle);
            throw;
        }

        return new SmbReadStream(this, handle, length, ownsSession);
    }

    internal int ReadAt(object handle, long offset, byte[] buffer, int bufferOffset, int count)
    {
        var status = _store.ReadFile(out var data, handle, offset, Math.Min(count, MaxReadSize));
        if (status == NTStatus.STATUS_END_OF_FILE)
        {
            return 0;
        }
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"SMB read error ({status}).");
        }
        if (data is null || data.Length == 0)
        {
            return 0;
        }

        var n = Math.Min(data.Length, count);
        Buffer.BlockCopy(data, 0, buffer, bufferOffset, n);
        return n;
    }

    internal void CloseHandle(object handle)
    {
        try { _store.CloseFile(handle); } catch { /* best effort */ }
    }

    // ----- Writing ----------------------------------------------------------

    /// <summary>Creates a NEW file (fails if it exists) and writes the stream into it.</summary>
    public void WriteFile(string path, Stream content)
    {
        var status = _store.CreateFile(out var handle, out _, Norm(path),
            AccessMask.GENERIC_WRITE, SMBLibrary.FileAttributes.Normal, ShareAccess.None,
            CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"Cannot create '{path}' ({status}).");
        }

        try
        {
            var chunk = new byte[MaxWriteSize];
            long offset = 0;
            while (true)
            {
                var read = content.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    break;
                }

                var data = read == chunk.Length ? chunk : chunk[..read];
                status = _store.WriteFile(out var written, handle, offset, data);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw new IOException($"Write error on '{path}' ({status}).");
                }

                offset += written;
            }
        }
        finally
        {
            _store.CloseFile(handle);
        }
    }

    /// <summary>Renames/moves a file to a new share-relative path (target folder must exist).</summary>
    public void Rename(string path, string newPath)
    {
        var src = Norm(path);
        var dst = Norm(newPath);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Renaming only needs DELETE on the file (write permission on the folders);
        // asking for GENERIC_WRITE as well would fail on read-only files.
        var status = _store.CreateFile(out var handle, out _, src,
            AccessMask.DELETE, SMBLibrary.FileAttributes.Normal, ShareAccess.None,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"Cannot open '{path}' for rename ({status}).");
        }

        try
        {
            var rename = new FileRenameInformationType2 { FileName = dst, ReplaceIfExists = false };
            status = _store.SetFileInformation(handle, rename);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"Cannot rename '{path}' to '{newPath}' ({status}).");
            }
        }
        finally
        {
            _store.CloseFile(handle);
        }
    }

    public bool TryDelete(string path)
    {
        var status = _store.CreateFile(out var handle, out _, Norm(path),
            AccessMask.DELETE, SMBLibrary.FileAttributes.Normal, ShareAccess.None,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return false;
        }

        _store.CloseFile(handle);
        return true;
    }

    /// <summary>
    /// Moves a file into <paramref name="destFolder"/> (created if missing) on the share,
    /// unique-suffixing the name when the target already exists. Returns false (without
    /// throwing) when the move failed, so an import can report it and keep going.
    /// </summary>
    public bool TryMove(string path, string destFolder, out string? error)
    {
        var src = Norm(path);
        var name = src.Contains('\\') ? src[(src.LastIndexOf('\\') + 1)..] : src;
        var dest = Norm(destFolder);

        try
        {
            EnsureDirectory(dest);
            var target = string.IsNullOrEmpty(dest) ? name : dest + "\\" + name;
            target = MakeUniqueName(target);
            Rename(src, target);
            error = null;
            return true;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Appends " (n)" until the share-relative path is free.</summary>
    public string MakeUniqueName(string fullPath)
    {
        if (!FileExists(fullPath))
        {
            return fullPath;
        }

        var directory = fullPath.Contains('\\') ? fullPath[..fullPath.LastIndexOf('\\')] : string.Empty;
        var fileName = fullPath.Contains('\\') ? fullPath[(fullPath.LastIndexOf('\\') + 1)..] : fullPath;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var counter = 2; ; counter++)
        {
            var candidateName = $"{stem} ({counter}){extension}";
            var candidate = string.IsNullOrEmpty(directory) ? candidateName : directory + "\\" + candidateName;
            if (!FileExists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Creates the folder (and parents) if missing.</summary>
    public void EnsureDirectory(string dir)
    {
        var target = Norm(dir);
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        var current = string.Empty;
        foreach (var part in target.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = string.IsNullOrEmpty(current) ? part : current + "\\" + part;
            var status = _store.CreateFile(out var handle, out _, current,
                AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN_IF,
                CreateOptions.FILE_DIRECTORY_FILE, null);
            if (status == NTStatus.STATUS_SUCCESS)
            {
                _store.CloseFile(handle);
            }
        }
    }

    /// <summary>Removes the folder when it is empty. Returns false when it is missing or not empty.</summary>
    public bool TryRemoveEmptyDirectory(string dir)
    {
        var target = Norm(dir);
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        var status = _store.CreateFile(out var handle, out _, target,
            AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return false;
        }

        bool empty;
        try
        {
            _store.QueryDirectory(out var list, handle, "*", FileInformationClass.FileDirectoryInformation);
            empty = list.All(i => ((FileDirectoryInformation)i).FileName is "." or "..");
        }
        finally
        {
            _store.CloseFile(handle);
        }

        if (!empty)
        {
            return false;
        }

        status = _store.CreateFile(out handle, out _, target,
            AccessMask.DELETE, SMBLibrary.FileAttributes.Directory, ShareAccess.None,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return false;
        }

        _store.CloseFile(handle);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _store.Disconnect(); } catch { /* best effort */ }
        try { _client.Logoff(); } catch { /* best effort */ }
        try { _client.Disconnect(); } catch { /* best effort */ }
    }
}

/// <summary>
/// Seekable, read-only stream over a file on an SMB share (reads on demand in
/// server-sized chunks, so large PDFs are not buffered in memory and HTTP range
/// requests work). Optionally owns the underlying session.
/// </summary>
public sealed class SmbReadStream : Stream
{
    private readonly SmbSession _session;
    private readonly object _handle;
    private readonly long _length;
    private readonly bool _ownsSession;
    private long _position;
    private bool _disposed;

    internal SmbReadStream(SmbSession session, object handle, long length, bool ownsSession)
    {
        _session = session;
        _handle = handle;
        _length = length;
        _ownsSession = ownsSession;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (count == 0 || _position >= _length)
        {
            return 0;
        }

        var want = (int)Math.Min(count, _length - _position);
        var read = _session.ReadAt(_handle, _position, buffer, offset, want);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0)
        {
            throw new IOException("Cannot seek before the beginning of the stream.");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _session.CloseHandle(_handle);
            if (_ownsSession)
            {
                _session.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
